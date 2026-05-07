using System.Text;
using System.Text.Json;
using MemoryService.Api.Contracts;
using MemoryService.Core.Domain;
using MemoryService.Infrastructure;
using MemoryService.Llm;
using MemoryService.Llm.Extraction;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MemoryService.Api.Services;

public sealed class TurnService(
    MemoryDbContext db,
    MemoryExtractor extractor,
    SupersessionJudge judge,
    IEmbedder embedder,
    ILogger<TurnService> log)
{
    private const int RecentMemoriesForExtractor = 10;
    private const int SimilarMemoriesForJudge    = 5;
    private const double SimilarVectorThreshold  = 0.30; // cosine distance ≤ 0.30 (similarity ≥ 0.70)
    private static readonly TimeSpan SoftBudget  = TimeSpan.FromSeconds(55);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IngestTurnResponse> IngestAsync(IngestTurnRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SessionId))
            throw new ArgumentException("session_id is required");
        if (req.Messages is null || req.Messages.Count == 0)
            throw new ArgumentException("messages must contain at least one entry");

        var userId = req.UserId ?? "anonymous";
        using var soft = CancellationTokenSource.CreateLinkedTokenSource(ct);
        soft.CancelAfter(SoftBudget);

        var turnId = Guid.NewGuid();

        // 1. Persist session + turn synchronously
        await EnsureSessionAsync(req.SessionId, userId, req.Metadata, ct);
        var rawText = BuildRawText(req.Messages);
        var turn = new Turn
        {
            Id            = turnId,
            SessionId     = req.SessionId,
            UserId        = userId,
            Timestamp     = req.Timestamp.ToUniversalTime(),
            MessagesJson  = JsonSerializer.Serialize(req.Messages, JsonOpts),
            RawText       = rawText,
            MetadataJson  = req.Metadata is null ? "{}" : JsonSerializer.Serialize(req.Metadata, JsonOpts),
        };
        db.Turns.Add(turn);
        await db.SaveChangesAsync(ct);

        // 2. Run extraction (best-effort — soft budget)
        try
        {
            await RunExtractionAsync(turn, soft.Token);
        }
        catch (OperationCanceledException) when (soft.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.LogWarning("Extraction soft-budget exceeded for turn {TurnId}; returning turn-only", turn.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Extraction failed for turn {TurnId}; returning turn-only", turn.Id);
        }

        return new IngestTurnResponse(turn.Id.ToString());
    }

    private async Task EnsureSessionAsync(string sessionId, string userId, Dictionary<string, object>? metadata, CancellationToken ct)
    {
        var existing = await db.Sessions.FindAsync(new object[] { sessionId }, ct);
        if (existing is not null) return;
        db.Sessions.Add(new Session
        {
            Id           = sessionId,
            UserId       = userId,
            CreatedAt    = DateTime.UtcNow,
            MetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, JsonOpts),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* concurrent insert raced us — fine */ }
    }

    private async Task RunExtractionAsync(Turn turn, CancellationToken ct)
    {
        var recent = await db.Memories.AsNoTracking()
            .Where(m => m.UserId == turn.UserId && m.Active)
            .OrderByDescending(m => m.UpdatedAt)
            .Take(RecentMemoriesForExtractor)
            .Select(m => m.Text)
            .ToListAsync(ct);

        var messages = JsonSerializer.Deserialize<List<IngestMessage>>(turn.MessagesJson, JsonOpts) ?? new();
        var extractorInput = messages.Select(m => (m.Role, m.Content)).ToList();
        var candidates = await extractor.ExtractAsync(extractorInput, recent, ct);
        if (candidates.Count == 0) return;

        var texts = candidates.Select(c => c.Text).ToList();
        var embeddings = await embedder.EmbedBatchAsync(texts, ct);
        if (embeddings.Count != candidates.Count)
        {
            log.LogWarning("Embedder returned {Got} for {Want} candidates; aborting extraction for turn {TurnId}",
                embeddings.Count, candidates.Count, turn.Id);
            return;
        }

        var subjectKeys = candidates
            .Select(c => (c.Subject, c.Predicate, c.Aspect))
            .ToHashSet();

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var emb = embeddings[i];
            if (emb.Length != embedder.Dimension)
            {
                log.LogWarning("Embedding dim mismatch ({Got} vs {Want}); skipping candidate", emb.Length, embedder.Dimension);
                continue;
            }
            try
            {
                await ApplyCandidateAsync(turn, c, new Vector(emb), ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to apply candidate '{Text}' on turn {TurnId}", c.Text, turn.Id);
            }
        }
    }

    private async Task ApplyCandidateAsync(Turn turn, MemoryCandidate c, Vector embedding, CancellationToken ct)
    {
        // Find similar active memories: same (subject, predicate, aspect) OR vector neighbours.
        var similar = await db.Memories.AsNoTracking()
            .Where(m => m.UserId == turn.UserId && m.Active && m.Subject == c.Subject)
            .OrderBy(m => m.Embedding!.CosineDistance(embedding))
            .Take(SimilarMemoriesForJudge)
            .Select(m => new SimilarMemoryRef(m.Id, m.Type.ToString().ToLowerInvariant(), m.Subject, m.Predicate, m.Aspect, m.Stance, m.Text))
            .ToListAsync(ct);

        var decision = await judge.JudgeAsync(c, similar, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        switch (decision.Action)
        {
            case JudgeAction.Dedup:
                log.LogDebug("DEDUP candidate '{Text}'", c.Text);
                break;

            case JudgeAction.Update when decision.SupersedesId is { } oldId:
                {
                    var old = await db.Memories.FirstOrDefaultAsync(m => m.Id == oldId && m.Active, ct);
                    if (old is null)
                    {
                        // Old was already superseded by another concurrent write; just COEXIST.
                        await InsertMemoryAsync(turn, c, embedding, supersedes: null, ct);
                    }
                    else
                    {
                        old.Active = false;
                        old.UpdatedAt = DateTime.UtcNow;
                        await InsertMemoryAsync(turn, c, embedding, supersedes: old.Id, ct);
                    }
                    break;
                }

            case JudgeAction.Add:
            case JudgeAction.Coexist:
            default:
                {
                    // Safety net: if a row with same (subject, predicate, aspect) is active, treat as UPDATE.
                    var collision = await db.Memories.FirstOrDefaultAsync(m =>
                        m.UserId == turn.UserId && m.Active &&
                        m.Subject == c.Subject &&
                        m.Predicate == c.Predicate &&
                        m.Aspect == c.Aspect, ct);
                    if (collision is not null && !string.Equals(collision.Text, c.Text, StringComparison.OrdinalIgnoreCase))
                    {
                        collision.Active = false;
                        collision.UpdatedAt = DateTime.UtcNow;
                        await InsertMemoryAsync(turn, c, embedding, supersedes: collision.Id, ct);
                    }
                    else if (collision is null)
                    {
                        await InsertMemoryAsync(turn, c, embedding, supersedes: null, ct);
                    }
                    // else: same text — DEDUP implicitly
                    break;
                }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<Memory> InsertMemoryAsync(Turn turn, MemoryCandidate c, Vector embedding, Guid? supersedes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var mem = new Memory
        {
            Id            = Guid.NewGuid(),
            UserId        = turn.UserId,
            SessionId     = turn.SessionId,
            Type          = ParseType(c.Type),
            Subject       = c.Subject.ToLowerInvariant(),
            Predicate     = c.Predicate?.ToLowerInvariant(),
            Object        = c.Object?.ToLowerInvariant(),
            Aspect        = c.Aspect?.ToLowerInvariant(),
            Stance        = c.Stance?.ToLowerInvariant(),
            Text          = c.Text,
            Embedding     = embedding,
            Confidence    = (float)Math.Clamp(c.Confidence, 0.0, 1.0),
            SourceTurnId  = turn.Id,
            Active        = true,
            Supersedes    = supersedes,
            CreatedAt     = now,
            UpdatedAt     = now,
            MetadataJson  = "{}",
        };
        db.Memories.Add(mem);

        if (c.DerivedEdges is { Count: > 0 })
        {
            await db.SaveChangesAsync(ct); // ensure mem.Id exists in DB before edges reference it
            foreach (var edge in c.DerivedEdges)
            {
                var dst = await db.Memories.FirstOrDefaultAsync(m =>
                    m.UserId == turn.UserId && m.Active && m.Subject == edge.DstSubject.ToLowerInvariant(), ct);
                if (dst is null) continue;
                db.MemoryEdges.Add(new MemoryEdge
                {
                    SrcMemoryId = mem.Id,
                    Relation    = edge.Relation.ToLowerInvariant(),
                    DstMemoryId = dst.Id,
                    CreatedAt   = now,
                });
            }
        }

        return mem;
    }

    private static MemoryType ParseType(string s) => s.Trim().ToLowerInvariant() switch
    {
        "fact"       => MemoryType.Fact,
        "preference" => MemoryType.Preference,
        "opinion"    => MemoryType.Opinion,
        "event"      => MemoryType.Event,
        _            => MemoryType.Fact,
    };

    private static string BuildRawText(IReadOnlyList<IngestMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
            sb.Append('[').Append(m.Role).Append("] ").AppendLine(m.Content);
        return sb.ToString();
    }
}
