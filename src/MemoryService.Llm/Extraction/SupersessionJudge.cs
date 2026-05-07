using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MemoryService.Llm.Extraction;

public enum JudgeAction
{
    Add,
    Update,
    Dedup,
    Coexist,
}

public sealed record JudgeDecision(JudgeAction Action, Guid? SupersedesId, string Reasoning);

public sealed record SimilarMemoryRef(Guid Id, string Type, string Subject, string? Predicate, string? Aspect, string? Stance, string Text);

public sealed class SupersessionJudge(IChatLlm llm, ILogger<SupersessionJudge> log)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<JudgeDecision> JudgeAsync(
        MemoryCandidate candidate,
        IReadOnlyList<SimilarMemoryRef> similar,
        CancellationToken ct = default)
    {
        if (similar.Count == 0)
            return new JudgeDecision(JudgeAction.Add, null, "no_similar");

        var prompt = BuildPrompt(candidate, similar);

        string raw;
        try
        {
            raw = await llm.CompleteJsonAsync(
                systemPrompt: Prompts.JudgeSystem,
                userPrompt:   prompt,
                jsonSchema:   Prompts.JudgeSchema,
                schemaName:   Prompts.JudgeSchemaName,
                ct:           ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Judge LLM call failed; defaulting to ADD");
            return new JudgeDecision(JudgeAction.Add, null, "judge_failed_fallback_add");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var actionStr = root.GetProperty("action").GetString() ?? "ADD";
            var action = actionStr.Trim().ToUpperInvariant() switch
            {
                "ADD" => JudgeAction.Add,
                "UPDATE" => JudgeAction.Update,
                "DEDUP" => JudgeAction.Dedup,
                "COEXIST" => JudgeAction.Coexist,
                _ => JudgeAction.Add,
            };
            Guid? supersedesId = null;
            if (root.TryGetProperty("supersedes_id", out var sid) && sid.ValueKind == JsonValueKind.String &&
                Guid.TryParse(sid.GetString(), out var parsed))
            {
                supersedesId = parsed;
            }
            var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
            if (action == JudgeAction.Update && supersedesId is null)
            {
                log.LogWarning("Judge returned UPDATE without supersedes_id; coercing to COEXIST");
                action = JudgeAction.Coexist;
            }
            return new JudgeDecision(action, supersedesId, reasoning);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Judge JSON parse failed. Raw: {Raw}", Truncate(raw, 300));
            return new JudgeDecision(JudgeAction.Add, null, "judge_parse_failed_fallback_add");
        }
    }

    private static string BuildPrompt(MemoryCandidate candidate, IReadOnlyList<SimilarMemoryRef> similar)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CANDIDATE NEW MEMORY:");
        sb.AppendLine(JsonSerializer.Serialize(new
        {
            type = candidate.Type,
            subject = candidate.Subject,
            predicate = candidate.Predicate,
            @object = candidate.Object,
            aspect = candidate.Aspect,
            stance = candidate.Stance,
            text = candidate.Text,
        }, JsonOpts));
        sb.AppendLine();
        sb.AppendLine("EXISTING SIMILAR ACTIVE MEMORIES:");
        foreach (var s in similar)
        {
            sb.Append("- id=").Append(s.Id).Append(" ");
            sb.Append("type=").Append(s.Type).Append(" ");
            sb.Append("subject=").Append(s.Subject);
            if (s.Predicate is not null) sb.Append(" predicate=").Append(s.Predicate);
            if (s.Aspect is not null) sb.Append(" aspect=").Append(s.Aspect);
            if (s.Stance is not null) sb.Append(" stance=").Append(s.Stance);
            sb.Append(" text=").AppendLine(s.Text);
        }
        sb.AppendLine();
        sb.AppendLine("Decide ADD, UPDATE, DEDUP, or COEXIST. Return JSON.");
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
