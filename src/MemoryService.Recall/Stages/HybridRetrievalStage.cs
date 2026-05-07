using MemoryService.Llm;
using Microsoft.Extensions.Options;
using Pgvector;

namespace MemoryService.Recall.Stages;

/// <summary>
/// Per sub-query: vector search + lexical search → RRF fuse → average across sub-queries.
/// Also tracks the maximum raw cosine similarity for the off-topic gate.
/// Falls back to <see cref="RecallQuery.Query"/> if QueryRewrite was disabled.
/// </summary>
public sealed class HybridRetrievalStage(
    HybridRetriever retriever,
    IEmbedder embedder,
    IOptions<RecallPipelineOptions> options) : IRecallStage
{
    public string Name => "HybridRetrieval";

    public async Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        var opts = options.Value;
        var subqueries = ctx.SubQueries.Count > 0 ? ctx.SubQueries : new List<string> { ctx.Query.Query };

        foreach (var sq in subqueries)
        {
            var qVecRaw = await embedder.EmbedAsync(sq, ct);
            if (qVecRaw.Length != embedder.Dimension) continue;
            var qVec = new Vector(qVecRaw);

            var vec = await retriever.VectorSearchAsync(ctx.UserId, qVec, opts.PerSubqueryK, ct);
            if (vec.Count > 0)
                ctx.MaxVectorSimilarity = Math.Max(ctx.MaxVectorSimilarity, vec[0].Score);

            var lex = await retriever.KeywordSearchAsync(ctx.UserId, sq, opts.PerSubqueryK, ct);
            var fused = Rrf.Fuse(vec, lex);
            foreach (var r in fused)
            {
                ctx.Combined.TryGetValue(r.Id, out var cur);
                ctx.Combined[r.Id] = cur + r.Score / subqueries.Count;
            }
        }

        if (ctx.Combined.Count == 0)
            ctx.Terminate(new RecallResult("", Array.Empty<RecallCitation>()));
    }
}
