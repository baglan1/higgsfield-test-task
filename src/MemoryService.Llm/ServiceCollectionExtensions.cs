using MemoryService.Core.Configuration;
using MemoryService.Llm.Extraction;
using MemoryService.Llm.Providers.Anthropic;
using MemoryService.Llm.Providers.Ollama;
using MemoryService.Llm.Providers.OpenAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using AnthropicClient = Anthropic.SDK.AnthropicClient;

namespace MemoryService.Llm;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProviders(this IServiceCollection services)
    {
        services.AddSingleton<IChatLlm>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MemoryServiceOptions>>();
            return new LazyChatLlm(() => BuildChatLlm(opts.Value));
        });
        services.AddSingleton<IEmbedder>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<MemoryServiceOptions>>();
            return new LazyEmbedder(() => BuildEmbedder(opts.Value), opts.Value.EmbedDim);
        });
        services.AddSingleton<MemoryExtractor>();
        services.AddSingleton<SupersessionJudge>();
        return services;
    }

    private static IChatLlm BuildChatLlm(MemoryServiceOptions o) => o.LlmProvider.ToLowerInvariant() switch
    {
        "openai" => BuildOpenAiChat(o),
        "anthropic" => BuildAnthropicChat(o),
        "ollama" => BuildOllamaChat(o),
        var p => throw new InvalidOperationException(
            $"Unknown MEMORY_LLM_PROVIDER '{p}'. Supported: openai | anthropic | ollama"),
    };

    private static IEmbedder BuildEmbedder(MemoryServiceOptions o)
    {
        // Embeddings: OpenAI is the default (Anthropic ships no embeddings model);
        // Ollama is used only when chat provider is ollama AND OLLAMA_HOST is set.
        return o.LlmProvider.ToLowerInvariant() switch
        {
            "ollama" when !string.IsNullOrEmpty(o.OllamaHost) => BuildOllamaEmbedder(o),
            _ when !string.IsNullOrEmpty(o.OpenAiApiKey)      => BuildOpenAiEmbedder(o),
            "ollama" when !string.IsNullOrEmpty(o.OpenAiApiKey) => BuildOpenAiEmbedder(o),
            _ => throw new InvalidOperationException(
                "No embedding backend configured. Set OPENAI_API_KEY (works with any chat provider), " +
                "or use MEMORY_LLM_PROVIDER=ollama with OLLAMA_HOST set."),
        };
    }

    private static IChatLlm BuildOpenAiChat(MemoryServiceOptions o)
    {
        Require(o.OpenAiApiKey, "OPENAI_API_KEY");
        var client = new OpenAIClient(o.OpenAiApiKey).GetChatClient(o.ChatModel);
        return new OpenAiChatLlm(client);
    }

    private static IEmbedder BuildOpenAiEmbedder(MemoryServiceOptions o)
    {
        Require(o.OpenAiApiKey, "OPENAI_API_KEY");
        var client = new OpenAIClient(o.OpenAiApiKey).GetEmbeddingClient(o.EmbedModel);
        return new OpenAiEmbedder(client, o.EmbedDim);
    }

    private static IChatLlm BuildAnthropicChat(MemoryServiceOptions o)
    {
        Require(o.AnthropicApiKey, "ANTHROPIC_API_KEY");
        var client = new AnthropicClient(o.AnthropicApiKey);
        return new AnthropicChatLlm(client, o.ChatModel);
    }

    private static IChatLlm BuildOllamaChat(MemoryServiceOptions o)
    {
        Require(o.OllamaHost, "OLLAMA_HOST");
        var client = new OllamaApiClient(new Uri(o.OllamaHost!)) { SelectedModel = o.ChatModel };
        return new OllamaChatLlm(client, o.ChatModel);
    }

    private static IEmbedder BuildOllamaEmbedder(MemoryServiceOptions o)
    {
        Require(o.OllamaHost, "OLLAMA_HOST");
        var client = new OllamaApiClient(new Uri(o.OllamaHost!)) { SelectedModel = o.EmbedModel };
        return new OllamaEmbedder(client, o.EmbedModel, o.EmbedDim);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required for the selected provider but was not set.");
    }
}
