namespace MemoryService.Recall.Stages;

/// <summary>Expands the query into 1–3 sub-queries (gated on length / pronoun heuristics).</summary>
public sealed class QueryRewriteStage(QueryRewriter rewriter) : IRecallStage
{
    public string Name => "QueryRewrite";

    public async Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        ctx.SubQueries = await rewriter.ExpandAsync(ctx.Query.Query, ct);
    }
}
