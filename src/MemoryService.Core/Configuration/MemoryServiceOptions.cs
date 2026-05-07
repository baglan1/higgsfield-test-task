namespace MemoryService.Core.Configuration;

public sealed class MemoryServiceOptions
{
    public string LlmProvider { get; set; } = "openai";
    public string ChatModel { get; set; } = "gpt-4o-mini";
    public string EmbedModel { get; set; } = "text-embedding-3-small";
    public int EmbedDim { get; set; } = 1536;
    public string? AuthToken { get; set; }
    public bool Rerank { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }
    public string? OllamaHost { get; set; }
}
