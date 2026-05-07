using OpenAI.Embeddings;

namespace MemoryService.Llm.Providers.OpenAi;

internal sealed class OpenAiEmbedder(EmbeddingClient client, int dimension) : IEmbedder
{
    public int Dimension => dimension;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await client.GenerateEmbeddingAsync(text, options: null, cancellationToken: ct);
        return result.Value.ToFloats().ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var result = await client.GenerateEmbeddingsAsync(texts, options: null, cancellationToken: ct);
        return result.Value.Select(e => e.ToFloats().ToArray()).ToList();
    }
}
