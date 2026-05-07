using MemoryService.Core.Domain;

namespace MemoryService.Recall;

/// <summary>
/// Mutable state passed through the recall pipeline. Stages read what previous stages
/// produced and write their own output. Calling <see cref="Terminate"/> signals the
/// pipeline runner to stop and return the supplied result.
/// </summary>
public sealed class RecallContext
{
    public required RecallQuery Query { get; init; }
    public required string UserId { get; init; }
    public required int MaxTokens { get; init; }

    // ---- mutable state filled by stages ----

    /// <summary>Set by QueryRewrite. If empty, downstream stages fall back to <see cref="RecallQuery.Query"/>.</summary>
    public List<string> SubQueries { get; set; } = new();

    /// <summary>RRF-fused (and possibly multi-hop-expanded) scores keyed by memory id.</summary>
    public Dictionary<Guid, double> Combined { get; set; } = new();

    /// <summary>Top raw cosine similarity across sub-queries — used by VectorSimilarityFloor.</summary>
    public double MaxVectorSimilarity { get; set; }

    /// <summary>Filtered + capped candidate set produced by LowScoreFilter.</summary>
    public Dictionary<Guid, double> Ranked { get; set; } = new();

    /// <summary>Tier A — stable user facts (filled by TierAStableFacts).</summary>
    public List<Memory> StableFacts { get; set; } = new();

    /// <summary>Tier B — query-relevant memories (filled by TierBRelevantMemories).</summary>
    public List<Memory> Relevant { get; set; } = new();

    /// <summary>Tier C — recent turns from the same session (filled by TierCRecentSession).</summary>
    public List<Turn> RecentTurns { get; set; } = new();

    // ---- termination ----

    public bool ShortCircuited { get; private set; }
    public RecallResult? Result { get; private set; }

    /// <summary>Terminate the pipeline and return the given result.</summary>
    public void Terminate(RecallResult result)
    {
        Result = result;
        ShortCircuited = true;
    }
}
