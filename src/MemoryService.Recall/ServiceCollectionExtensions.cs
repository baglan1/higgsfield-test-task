using MemoryService.Core.Configuration;
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
        services.AddScoped<RecallPipeline>();
        services.AddScoped<SearchService>();
        return services;
    }
}
