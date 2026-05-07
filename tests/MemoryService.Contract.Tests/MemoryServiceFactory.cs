using MemoryService.Llm;
using MemoryService.Llm.Extraction;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace MemoryService.Contract.Tests;

public sealed class MemoryServiceFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .WithDatabase("memory")
        .WithUsername("memory")
        .WithPassword("memory")
        .Build();

    public async Task InitializeAsync() => await _pg.StartAsync();
    public new async Task DisposeAsync() => await _pg.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _pg.GetConnectionString() + ";Include Error Detail=true");
        builder.UseSetting("MEMORY_LLM_PROVIDER", "openai");
        builder.UseSetting("MEMORY_CHAT_MODEL", "gpt-4o-mini");
        builder.UseSetting("MEMORY_EMBED_MODEL", "fake");
        builder.UseSetting("MEMORY_EMBED_DIM", "64");
        builder.UseSetting("OPENAI_API_KEY", "test-key-not-used");

        builder.ConfigureServices(svc =>
        {
            // Replace LLM + embedder with deterministic fakes so contract tests don't hit the network.
            ReplaceSingleton<IChatLlm>(svc, new FakeChatLlm());
            ReplaceSingleton<IEmbedder>(svc, new FakeEmbedder(64));
        });
    }

    private static void ReplaceSingleton<T>(IServiceCollection svc, T impl) where T : class
    {
        var existing = svc.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in existing) svc.Remove(d);
        svc.AddSingleton<T>(impl);
    }
}

internal sealed class FakeChatLlm : IChatLlm
{
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        => Task.FromResult("");

    public Task<string> CompleteJsonAsync(
        string systemPrompt,
        string userPrompt,
        string? jsonSchema = null,
        string? schemaName = null,
        CancellationToken ct = default)
        => Task.FromResult("{\"candidates\": []}");
}

internal sealed class FakeEmbedder(int dim) : IEmbedder
{
    public int Dimension => dim;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Hash(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Hash).ToList());

    private float[] Hash(string text)
    {
        // Deterministic, normalized pseudo-embedding so similar text produces similar vectors.
        var arr = new float[dim];
        var h = 17u;
        foreach (var ch in text) { h = h * 31u + ch; arr[(int)(h % (uint)dim)] += 1f; }
        var norm = (float)Math.Sqrt(arr.Sum(x => x * x));
        if (norm > 0) for (int i = 0; i < dim; i++) arr[i] /= norm;
        return arr;
    }
}
