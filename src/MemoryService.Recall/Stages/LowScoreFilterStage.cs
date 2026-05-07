using Microsoft.Extensions.Options;

namespace MemoryService.Recall.Stages;

/// <summary>Drops candidates below <see cref="RecallPipelineOptions.MinKeepScore"/> and caps to <see cref="RecallPipelineOptions.MaxRankedTake"/>.</summary>
public sealed class LowScoreFilterStage(IOptions<RecallPipelineOptions> options) : IRecallStage
{
    public string Name => "LowScoreFilter";

    public Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        var opts = options.Value;
        ctx.Ranked = ctx.Combined
            .Where(kv => kv.Value >= opts.MinKeepScore)
            .OrderByDescending(kv => kv.Value)
            .Take(opts.MaxRankedTake)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (ctx.Ranked.Count == 0)
            ctx.Terminate(new RecallResult("", Array.Empty<RecallCitation>()));
        return Task.CompletedTask;
    }
}
