namespace MemoryService.Llm;

internal sealed class LazyChatLlm(Func<IChatLlm> factory) : IChatLlm
{
    private IChatLlm? _inner;
    private readonly object _lock = new();

    private IChatLlm Get()
    {
        if (_inner is not null) return _inner;
        lock (_lock) { _inner ??= factory(); }
        return _inner;
    }

    public Task<string> CompleteAsync(string s, string u, CancellationToken ct = default)
        => Get().CompleteAsync(s, u, ct);

    public Task<string> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        string? jsonSchema = null,
        string? schemaName = null,
        CancellationToken ct = default)
        => Get().CompleteJsonAsync(systemPrompt, userPrompt, jsonSchema, schemaName, ct);
}

internal sealed class LazyEmbedder : IEmbedder
{
    private readonly Func<IEmbedder> _factory;
    private readonly int _declaredDim;
    private IEmbedder? _inner;
    private readonly object _lock = new();

    public LazyEmbedder(Func<IEmbedder> factory, int declaredDim)
    {
        _factory = factory;
        _declaredDim = declaredDim;
    }

    public int Dimension => _inner?.Dimension ?? _declaredDim;

    private IEmbedder Get()
    {
        if (_inner is not null) return _inner;
        lock (_lock) { _inner ??= _factory(); }
        return _inner;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Get().EmbedAsync(text, ct);

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => Get().EmbedBatchAsync(texts, ct);
}
