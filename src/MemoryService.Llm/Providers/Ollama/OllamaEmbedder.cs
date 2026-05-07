using OllamaSharp;
using OllamaSharp.Models;

namespace MemoryService.Llm.Providers.Ollama;

internal sealed class OllamaEmbedder(IOllamaApiClient client, string model, int dimension) : IEmbedder
{
    public int Dimension => dimension;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var resp = await client.EmbedAsync(new EmbedRequest { Model = model, Input = new List<string> { text } }, ct);
        var emb = resp?.Embeddings?.FirstOrDefault();
        return emb is null ? Array.Empty<float>() : emb;
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        var resp = await client.EmbedAsync(new EmbedRequest { Model = model, Input = texts.ToList() }, ct);
        return resp?.Embeddings?.ToList() ?? new List<float[]>();
    }
}
