using MemoryService.Core.Configuration;
using MemoryService.Recall.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MemoryService.Recall;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRecallPipeline(this IServiceCollection services)
    {
        services.AddScoped<HybridRetriever>();
        services.AddScoped<MultiHopExpander>();
        services.AddSingleton<QueryRewriter>();
        services.AddSingleton<TokenCounter>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MemoryServiceOptions>>().Value;
            return new TokenCounter(opts.ChatModel, opts.LlmProvider);
        });
        services.AddSingleton<ContextAssembler>();

        // Register every stage as IRecallStage. The pipeline resolves them by name
        // from RecallPipelineOptions.Stages.
        services.AddScoped<IRecallStage, QueryRewriteStage>();
        services.AddScoped<IRecallStage, HybridRetrievalStage>();
        services.AddScoped<IRecallStage, VectorSimilarityFloorStage>();
        services.AddScoped<IRecallStage, MultiHopExpansionStage>();
        services.AddScoped<IRecallStage, LowScoreFilterStage>();
        services.AddScoped<IRecallStage, TierAStableFactsStage>();
        services.AddScoped<IRecallStage, TierBRelevantMemoriesStage>();
        services.AddScoped<IRecallStage, TierCRecentSessionStage>();
        services.AddScoped<IRecallStage, AssemblyStage>();

        services.AddScoped<RecallPipeline>();
        services.AddScoped<SearchService>();
        return services;
    }
}
