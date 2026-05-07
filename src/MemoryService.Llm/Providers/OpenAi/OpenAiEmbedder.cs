using OpenAI.Embeddings;

namespace MemoryService.Llm.Providers.OpenAi;

internal sealed class OpenAiEmbedder(EmbeddingClient client, int dimension) : IEmbedder
{
    public int Dimension => dimension;

    // Pass the configured dimension so OpenAI honors it (Matryoshka — supported by
    // text-embedding-3-small / -large; ignored for older models which only return their fixed dim).
    private EmbeddingGenerationOptions Options => new() { Dimensions = dimension };

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await client.GenerateEmbeddingAsync(text, Options, ct);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var result = await client.GenerateEmbeddingsAsync(texts, Options, ct);
        return result.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}
