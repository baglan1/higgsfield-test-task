using System.Text;
using OllamaSharp;
using OllamaSharp.Models.Chat;

namespace MemoryService.Llm.Providers.Ollama;

internal sealed class OllamaChatLlm(IOllamaApiClient client, string model) : IChatLlm
{
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var request = new ChatRequest
        {
            Model = model,
            Messages = new List<Message>
            {
                new() { Role = ChatRole.System, Content = systemPrompt },
                new() { Role = ChatRole.User,   Content = userPrompt },
            },
            Stream = false,
        };
        await foreach (var chunk in client.ChatAsync(request, ct))
        {
            if (chunk?.Message?.Content is { Length: > 0 } c) sb.Append(c);
        }
        return sb.ToString();
    }

    public Task<string> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        string? jsonSchema = null,
        string? schemaName = null,
        CancellationToken ct = default)
    {
        // Ollama supports a "format" parameter (e.g. "json") but not arbitrary JSON-schema enforcement
        // through this SDK in a portable way. We rely on prompt-level instruction.
        var hardened = systemPrompt + "\n\nReturn valid JSON only. No prose. No markdown fences.";
        return CompleteAsync(hardened, userPrompt, ct);
    }
}
