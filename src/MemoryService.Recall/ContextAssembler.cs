using System.Text;
using MemoryService.Core.Domain;

namespace MemoryService.Recall;

public sealed record RecallCitation(string TurnId, double Score, string Snippet);

public sealed class AssembledContext
{
    public string Text { get; init; } = "";
    public List<RecallCitation> Citations { get; init; } = new();
}

public sealed record TierInputs(
    IReadOnlyList<Memory> StableFacts,
    IReadOnlyDictionary<Guid, double> RankedScores,
    IReadOnlyList<Memory> Relevant,
    IReadOnlyList<Turn> RecentTurns);

/// <summary>
/// Tier-budget context assembler. Tier weights: A 30% / B 50% / C 20% of max_tokens.
/// Spillover redistributes to A → B → C.
/// </summary>
public sealed class ContextAssembler(TokenCounter tokens)
{
    private const double TierA = 0.30;
    private const double TierB = 0.50;
    private const double TierC = 0.20;
    private const int    MinHeaderBudget = 8; // tokens reserved for section header

    public AssembledContext Assemble(TierInputs input, int maxTokens)
    {
        if (maxTokens <= 0) return new AssembledContext();

        var sb = new StringBuilder();
        var citations = new List<RecallCitation>();

        var aBudget = (int)Math.Floor(maxTokens * TierA);
        var bBudget = (int)Math.Floor(maxTokens * TierB);
        var cBudget = (int)Math.Floor(maxTokens * TierC);

        // Tier A — stable user facts
        var aOutput = BuildBullets(input.StableFacts, input.RankedScores, aBudget, "## Known facts about this user", citations);
        sb.Append(aOutput.Text);
        var aSpill = aBudget - aOutput.UsedTokens;

        // Spill A → B
        var bAdjusted = bBudget + Math.Max(0, aSpill);
        var bOutput = BuildBullets(input.Relevant, input.RankedScores, bAdjusted, "## Relevant from recent conversations", citations);
        sb.Append(bOutput.Text);
        var bSpill = bAdjusted - bOutput.UsedTokens;

        // Spill A+B → C
        var cAdjusted = cBudget + Math.Max(0, bSpill);
        var cOutput = BuildRecent(input.RecentTurns, cAdjusted);
        sb.Append(cOutput.Text);

        return new AssembledContext { Text = sb.ToString().TrimEnd() + (sb.Length > 0 ? "\n" : ""), Citations = citations };
    }

    private (string Text, int UsedTokens) BuildBullets(
        IReadOnlyList<Memory> memories,
        IReadOnlyDictionary<Guid, double> ranked,
        int budget,
        string header,
        List<RecallCitation> citations)
    {
        if (budget <= 0 || memories.Count == 0) return ("", 0);

        var sb = new StringBuilder();
        sb.AppendLine(header);
        var used = tokens.Count(header) + MinHeaderBudget;

        foreach (var m in memories)
        {
            var line = "- " + m.Text;
            var lineTokens = tokens.Count(line);
            if (used + lineTokens > budget) continue;
            sb.AppendLine(line);
            used += lineTokens;
            ranked.TryGetValue(m.Id, out var score);
            citations.Add(new RecallCitation(
                TurnId: m.SourceTurnId.ToString(),
                Score: score,
                Snippet: m.Text.Length <= 160 ? m.Text : m.Text[..160]));
        }
        sb.AppendLine();
        return (sb.ToString(), used);
    }

    private (string Text, int UsedTokens) BuildRecent(IReadOnlyList<Turn> turns, int budget)
    {
        if (budget <= 0 || turns.Count == 0) return ("", 0);

        var sb = new StringBuilder();
        sb.AppendLine("## Recent in this session");
        var header = "## Recent in this session";
        var used = tokens.Count(header) + MinHeaderBudget;

        foreach (var t in turns.OrderByDescending(t => t.Timestamp))
        {
            var prefix = $"- [{t.Timestamp:yyyy-MM-dd}] ";
            var snippet = Truncate(t.RawText.Replace('\n', ' ').Replace('\r', ' '), 200);
            var line = prefix + snippet;
            var lineTokens = tokens.Count(line);
            if (used + lineTokens > budget) break;
            sb.AppendLine(line);
            used += lineTokens;
        }
        sb.AppendLine();
        return (sb.ToString(), used);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
