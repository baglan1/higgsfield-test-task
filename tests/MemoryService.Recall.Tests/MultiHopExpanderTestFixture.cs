using MemoryService.Core.Domain;
using MemoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace MemoryService.Recall.Tests;

/// <summary>
/// Spins up a Postgres+pgvector container, applies EF migrations, and exposes helpers
/// to seed memories and edges by raw SQL. Shared across all tests in the class.
/// </summary>
public sealed class MultiHopExpanderTestFixture : IAsyncLifetime
{
    /// <summary>Matches <see cref="MemoryDbContext.EmbeddingDim"/> baked into the migration.</summary>
    public static readonly int EmbedDim = MemoryDbContext.EmbeddingDim;

    public const string TestUserId    = "test-user";
    public const string TestSessionId = "test-session";
    public static readonly Guid TestTurnId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .WithDatabase("memory")
        .WithUsername("memory")
        .WithPassword("memory")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        var connStr = _pg.GetConnectionString() + ";Include Error Detail=true";

        var dsBuilder = new NpgsqlDataSourceBuilder(connStr);
        dsBuilder.UseVector();
        dsBuilder.MapEnum<MemoryType>("memory_type");
        DataSource = dsBuilder.Build();

        // Apply EF migrations to bring the schema up.
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
        }

        // Shared session + turn so memory inserts can satisfy FK constraints cheaply.
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO sessions (id, user_id) VALUES ('{TestSessionId}', '{TestUserId}');
            INSERT INTO turns (id, session_id, user_id, ts, messages, raw_text)
              VALUES ('{TestTurnId}', '{TestSessionId}', '{TestUserId}', now(), '[]'::jsonb, '');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        await _pg.DisposeAsync().AsTask();
    }

    public MemoryDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql(DataSource, npg => npg.UseVector())
            .Options;
        return new MemoryDbContext(options);
    }

    /// <summary>Wipes edges and memories between tests; keeps the shared session + turn.</summary>
    public async Task ResetGraphAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_edges; DELETE FROM memories;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SeedMemoriesAsync(params Guid[] ids)
    {
        if (ids.Length == 0) return;
        var zero = "[" + string.Join(",", Enumerable.Repeat("0", EmbedDim)) + "]";
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        var sb = new System.Text.StringBuilder();
        foreach (var id in ids)
        {
            sb.AppendLine($"""
                INSERT INTO memories (id, user_id, session_id, type, subject, text, embedding, source_turn_id, active)
                  VALUES ('{id}', '{TestUserId}', '{TestSessionId}', 'fact', 'subj-{id}', 'text', '{zero}'::vector, '{TestTurnId}', true);
                """);
        }
        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SeedEdgeAsync(Guid src, Guid dst, string relation = "rel")
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO memory_edges (src_memory_id, relation, dst_memory_id) VALUES ('{src}', '{relation}', '{dst}');";
        await cmd.ExecuteNonQueryAsync();
    }
}
