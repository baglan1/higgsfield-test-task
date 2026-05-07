using MemoryService.Core.Configuration;

namespace MemoryService.Api.Configuration;

public static class MemoryServiceOptionsBinder
{
    public static void Bind(MemoryServiceOptions opts, IConfiguration config)
    {
        opts.LlmProvider     = config["MEMORY_LLM_PROVIDER"]                  ?? opts.LlmProvider;
        opts.ChatModel       = config["MEMORY_CHAT_MODEL"]                    ?? opts.ChatModel;
        opts.EmbedModel      = config["MEMORY_EMBED_MODEL"]                   ?? opts.EmbedModel;
        if (int.TryParse(config["MEMORY_EMBED_DIM"], out var dim)) opts.EmbedDim = dim;
        opts.AuthToken       = NullIfEmpty(config["MEMORY_AUTH_TOKEN"]);
        opts.Rerank          = config["MEMORY_RERANK"] == "1" || string.Equals(config["MEMORY_RERANK"], "true", StringComparison.OrdinalIgnoreCase);
        opts.OpenAiApiKey    = NullIfEmpty(config["OPENAI_API_KEY"]);
        opts.AnthropicApiKey = NullIfEmpty(config["ANTHROPIC_API_KEY"]);
        opts.OllamaHost      = NullIfEmpty(config["OLLAMA_HOST"]);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
