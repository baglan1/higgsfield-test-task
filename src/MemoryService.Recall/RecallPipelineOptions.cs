namespace MemoryService.Recall;

/// <summary>
/// Configures the recall pipeline: which stages run, in what order, and their tunables.
/// Bind from configuration section "Recall" (see Program.cs).
/// </summary>
public sealed class RecallPipelineOptions
{
    /// <summary>
    /// Stage names in execution order. To disable a stage, remove it from the list.
    /// To reorder, change the position. Names match the <c>Name</c> property on each
    /// <see cref="IRecallStage"/> implementation (case-insensitive).
    /// </summary>
    // Default is empty: configuration binding for List<T> APPENDS to the existing list
    // rather than replacing it. Leaving an in-code default would mean every config in
    // appsettings.json gets the in-code defaults appended after it, breaking ablations.
    // The "real" default list lives in appsettings.json under Recall:Stages.
    public List<string> Stages { get; set; } = new();

    // ---- per-stage tunables ----

    /// <summary>Top-K per channel inside HybridRetrieval (vector + lexical).</summary>
    public int PerSubqueryK { get; set; } = 30;

    /// <summary>Number of edge hops in MultiHopExpansion.</summary>
    public int MultiHopMaxHops { get; set; } = 2;

    /// <summary>How many top fused memories seed the multi-hop traversal.</summary>
    public int MultiHopSeedTopK { get; set; } = 10;

    /// <summary>Recent-turn cap for Tier C.</summary>
    public int MaxRecentTurns { get; set; } = 3;

    /// <summary>Floor below which RRF-fused scores are discarded as noise.</summary>
    public double MinKeepScore { get; set; } = 0.005;

    /// <summary>If max raw cosine similarity across sub-queries falls below this, treat the query as off-topic and short-circuit.</summary>
    public double VectorSimilarityFloor { get; set; } = 0.45;

    /// <summary>Cap on the candidate set after LowScoreFilter (top-K passed to tier assembly).</summary>
    public int MaxRankedTake { get; set; } = 40;

    /// <summary>Confidence threshold for Tier-A "stable user facts".</summary>
    public float StableFactConfidence { get; set; } = 0.7f;

    /// <summary>How many stable facts are eligible to enter Tier A.</summary>
    public int StableFactsTopK { get; set; } = 20;
}
