using MemoryService.Core.Domain;
using MemoryService.Infrastructure;
using MemoryService.Llm;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace MemoryService.Recall;

public sealed record RecallQuery(string Query, string? SessionId, string? UserId, int MaxTokens);
public sealed record RecallResult(string Context, IReadOnlyList<RecallCitation> Citations);

public sealed class RecallPipeline(
    HybridRetriever retriever,
    QueryRewriter rewriter,
    MultiHopExpander expander,
    ContextAssembler assembler,
    IEmbedder embedder,
    MemoryDbContext db)
{
    private const int PerSubqueryK     = 30;
    private const int MultiHopMaxHops  = 2;
    private const int MaxRecentTurns   = 3;
    private const double MinKeepScore  = 0.005; // RRF + multi-hop combined floor
    private const float StableFactConfidence = 0.7f;

    public async Task<RecallResult> RecallAsync(RecallQuery req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query) || string.IsNullOrWhiteSpace(req.UserId))
            return new RecallResult("", Array.Empty<RecallCitation>());

        var maxTokens = req.MaxTokens > 0 ? req.MaxTokens : 1024;
        var userId = req.UserId!;

        // 0. Cold-session shortcut: if the user has no memories, return empty without invoking any LLM.
        var hasAny = await db.Memories.AsNoTracking().AnyAsync(m => m.UserId == userId && m.Active, ct);
        if (!hasAny) return new RecallResult("", Array.Empty<RecallCitation>());

        // 1. Query rewrite
        var subqueries = await rewriter.ExpandAsync(req.Query, ct);

        // 2. Hybrid retrieve per sub-query, fuse with RRF, then average across sub-queries.
        var combined = new Dictionary<Guid, double>();
        foreach (var sq in subqueries)
        {
            var qVecRaw = await embedder.EmbedAsync(sq, ct);
            if (qVecRaw.Length != embedder.Dimension) continue;
            var qVec = new Vector(qVecRaw);

            var vec = await retriever.VectorSearchAsync(userId, qVec, PerSubqueryK, ct);
            var lex = await retriever.KeywordSearchAsync(userId, sq, PerSubqueryK, ct);
            var fused = Rrf.Fuse(vec, lex);
            foreach (var r in fused)
            {
                combined.TryGetValue(r.Id, out var cur);
                combined[r.Id] = cur + r.Score / subqueries.Count;
            }
        }

        if (combined.Count == 0)
            return new RecallResult("", Array.Empty<RecallCitation>());

        // 3. Multi-hop expansion seeded by top-K from fused results.
        var seeds = combined
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => new RankedMemory(kv.Key, kv.Value))
            .ToList();
        var expanded = await expander.ExpandAsync(seeds, MultiHopMaxHops, ct);
        foreach (var e in expanded)
        {
            combined.TryGetValue(e.Id, out var cur);
            combined[e.Id] = Math.Max(cur, e.Score);
        }

        // 4. Filter very low-score hits to satisfy noise resistance.
        var ranked = combined
            .Where(kv => kv.Value >= MinKeepScore)
            .OrderByDescending(kv => kv.Value)
            .Take(40)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (ranked.Count == 0)
            return new RecallResult("", Array.Empty<RecallCitation>());

        // 5. Load full memory rows for the candidates.
        var ids = ranked.Keys.ToList();
        var memories = await retriever.LoadByIdsAsync(ids, ct);

        // 6. Tier A — stable user facts (active facts, high confidence) regardless of query.
        var stableFacts = await db.Memories.AsNoTracking()
            .Where(m => m.UserId == userId && m.Active &&
                        m.Type == MemoryType.Fact && m.Confidence >= StableFactConfidence)
            .OrderByDescending(m => m.Confidence)
            .ThenByDescending(m => m.UpdatedAt)
            .Take(20)
            .ToListAsync(ct);

        // 7. Tier B — query-relevant memories (top fused). Exclude any already in Tier A.
        var stableIds = stableFacts.Select(m => m.Id).ToHashSet();
        var relevant = memories
            .Where(m => !stableIds.Contains(m.Id))
            .OrderByDescending(m => ranked[m.Id])
            .ToList();

        // 8. Tier C — recent turns from this session.
        var recent = string.IsNullOrEmpty(req.SessionId)
            ? new List<Turn>()
            : await db.Turns.AsNoTracking()
                .Where(t => t.SessionId == req.SessionId)
                .OrderByDescending(t => t.Timestamp)
                .Take(MaxRecentTurns)
                .ToListAsync(ct);

        var assembled = assembler.Assemble(new TierInputs(stableFacts, ranked, relevant, recent), maxTokens);

        return new RecallResult(assembled.Text, assembled.Citations);
    }
}
