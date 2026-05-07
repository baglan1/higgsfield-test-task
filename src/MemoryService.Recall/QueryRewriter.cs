using System.Text.Json;
using MemoryService.Llm;
using Microsoft.Extensions.Logging;

namespace MemoryService.Recall;

public sealed class QueryRewriter(IChatLlm llm, ILogger<QueryRewriter> log)
{
    private const string System = """
        You expand a single user query into 1-3 alternative phrasings or sub-queries that, taken together, give better recall over a memory store.
        - Keep alternatives short (under 12 words each).
        - Resolve pronouns and anaphora when context permits.
        - For multi-hop queries, decompose into atomic sub-queries.
        - If the query is already concrete and clear, return only the original.
        Return JSON: { "queries": ["..."] }. The array MUST contain the original query as the first item.
        """;

    private const string SchemaName = "query_rewrite";

    private const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "queries": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["queries"]
        }
        """;

    private static readonly HashSet<string> PronounsAndAnaphora = new(StringComparer.OrdinalIgnoreCase)
    {
        "they", "them", "their", "theirs", "it", "its", "this", "that", "these", "those", "he", "she", "his", "her",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<List<string>> ExpandAsync(string query, CancellationToken ct)
    {
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0) return [];

        if (!NeedsExpansion(trimmed))
            return [trimmed];

        string raw;
        try
        {
            raw = await llm.CompleteJsonAsync(
                systemPrompt: System,
                userPrompt:   trimmed,
                jsonSchema:   Schema,
                schemaName:   SchemaName,
                ct:           ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Query rewrite LLM call failed; using raw query");
            return [trimmed];
        }

        RewriteEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RewriteEnvelope>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Query rewrite JSON parse failed; using raw query. Raw: {Raw}", Truncate(raw, 300));
            return [trimmed];
        }

        var list = (envelope?.Queries ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        if (list.Count == 0) return [trimmed];
        if (!list.Any(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, trimmed);
        return list;
    }

    private static bool NeedsExpansion(string q)
    {
        var words = q.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 3) return true;
        return words.Any(w => PronounsAndAnaphora.Contains(w.Trim('?', '.', ',', '!')));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class RewriteEnvelope
    {
        public List<string>? Queries { get; set; }
    }
}
