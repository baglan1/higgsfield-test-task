namespace MemoryService.Recall.Stages;

/// <summary>
/// Tier B — query-relevant memories ordered by fused score, excluding anything Tier A already has.
/// Loads the full memory rows for the ids in <see cref="RecallContext.Ranked"/> as part of this stage
/// (no separate "LoadMemories" step — that data is only used here).
/// </summary>
public sealed class TierBRelevantMemoriesStage(HybridRetriever retriever) : IRecallStage
{
    public string Name => "TierBRelevantMemories";

    public async Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        var ids = ctx.Ranked.Keys.ToList();
        if (ids.Count == 0)
        {
            ctx.Relevant = new();
            return;
        }

        var loaded = await retriever.LoadByIdsAsync(ids, ct);
        var stableIds = ctx.StableFacts.Select(m => m.Id).ToHashSet();

        ctx.Relevant = loaded
            .Where(m => !stableIds.Contains(m.Id))
            .OrderByDescending(m => ctx.Ranked.TryGetValue(m.Id, out var s) ? s : 0)
            .ToList();
    }
}
