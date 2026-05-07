using OpenAI.Chat;

namespace MemoryService.Llm.Providers.OpenAi;

internal sealed class OpenAiChatLlm(ChatClient client) : IChatLlm
{
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt),
        };
        var result = await client.CompleteChatAsync(messages, options: null, cancellationToken: ct);
        return ExtractText(result.Value);
    }

    public async Task<string> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        string? jsonSchema = null,
        string? schemaName = null,
        CancellationToken ct = default)
    {
        // With a schema → strict structured outputs (server-side guarantee).
        // Without → fall back to JSON object mode + prompt-level instruction.
        var system = jsonSchema is null
            ? systemPrompt + "\n\nReturn valid JSON only. No prose. No markdown fences."
            : systemPrompt;

        var messages = new ChatMessage[]
        {
            new SystemChatMessage(system),
            new UserChatMessage(userPrompt),
        };

        var responseFormat = jsonSchema is null
            ? ChatResponseFormat.CreateJsonObjectFormat()
            : ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: schemaName ?? "response",
                jsonSchema: BinaryData.FromString(jsonSchema),
                jsonSchemaIsStrict: true);

        var options = new ChatCompletionOptions { ResponseFormat = responseFormat };
        var result = await client.CompleteChatAsync(messages, options, ct);
        return ExtractText(result.Value);
    }

    private static string ExtractText(ChatCompletion completion)
    {
        if (completion.Content is { Count: > 0 })
            return completion.Content[0].Text ?? "";
        return "";
    }
}
