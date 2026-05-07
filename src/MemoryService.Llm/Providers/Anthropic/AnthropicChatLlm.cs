using Anthropic.SDK;
using Anthropic.SDK.Messaging;

namespace MemoryService.Llm.Providers.Anthropic;

internal sealed class AnthropicChatLlm(AnthropicClient client, string model) : IChatLlm
{
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var resp = await client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model    = model,
            System   = new List<SystemMessage> { new(systemPrompt) },
            Messages = new List<Message>       { new(RoleType.User, userPrompt) },
            MaxTokens = 2048,
        }, ct);
        return resp?.Message?.ToString() ?? "";
    }

    public Task<string> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        string? jsonSchema = null,
        string? schemaName = null,
        CancellationToken ct = default)
    {
        // Anthropic's Messages API doesn't have native JSON-schema enforcement in this SDK,
        // so the schema parameter is informational. We still ask for valid JSON via the prompt.
        var hardened = systemPrompt + "\n\nReturn valid JSON only. No prose. No markdown fences.";
        return CompleteAsync(hardened, userPrompt, ct);
    }
}
