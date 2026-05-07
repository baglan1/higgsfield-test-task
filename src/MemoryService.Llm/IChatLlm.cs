namespace MemoryService.Llm;

public interface IChatLlm
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);

    /// <summary>
    /// Returns a JSON string. When <paramref name="jsonSchema"/> is provided AND the underlying
    /// provider supports it (currently OpenAI), the response conforms to the schema strictly.
    /// Other providers fall back to "JSON mode via prompt" — still JSON, no schema enforcement.
    /// </summary>
    Task<string> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        string? jsonSchema = null,
        string? schemaName = null,
        CancellationToken ct = default);
}
