using Microsoft.Extensions.Options;

namespace MemoryService.Recall.Stages;

/// <summary>
/// Off-topic gate: if no memory shares meaningful semantic overlap with the query
/// (top raw cosine similarity below the floor), suppress the entire response.
/// This is the only stage that gives noise resistance against §9-style adversarial probes.
/// </summary>
public sealed class VectorSimilarityFloorStage(IOptions<RecallPipelineOptions> options) : IRecallStage
{
    public string Name => "VectorSimilarityFloor";

    public Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        if (ctx.MaxVectorSimilarity < options.Value.VectorSimilarityFloor)
            ctx.Terminate(new RecallResult("", Array.Empty<RecallCitation>()));
        return Task.CompletedTask;
    }
}
