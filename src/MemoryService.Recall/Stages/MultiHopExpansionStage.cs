using Microsoft.Extensions.Options;

namespace MemoryService.Recall.Stages;

/// <summary>BFS over <c>memory_edges</c> from the top-K fused seeds; merges decayed scores back into <see cref="RecallContext.Combined"/>.</summary>
public sealed class MultiHopExpansionStage(
    MultiHopExpander expander,
    IOptions<RecallPipelineOptions> options) : IRecallStage
{
    public string Name => "MultiHopExpansion";

    public async Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        var opts = options.Value;
        var seeds = ctx.Combined
            .OrderByDescending(kv => kv.Value)
            .Take(opts.MultiHopSeedTopK)
            .Select(kv => new RankedMemory(kv.Key, kv.Value))
            .ToList();

        var expanded = await expander.ExpandAsync(seeds, opts.MultiHopMaxHops, ct);
        foreach (var e in expanded)
        {
            ctx.Combined.TryGetValue(e.Id, out var cur);
            ctx.Combined[e.Id] = Math.Max(cur, e.Score);
        }
    }
}
