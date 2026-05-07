using MemoryService.Core.Domain;
using MemoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MemoryService.Recall;

public sealed class HybridRetriever(MemoryDbContext db, NpgsqlDataSource dataSource)
{
    public async Task<List<RankedMemory>> VectorSearchAsync(string userId, Vector queryVec, int k, CancellationToken ct)
    {
        // Cosine distance: lower is better. Convert to similarity via 1 - distance.
        var rows = await db.Memories
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.Active)
            .Select(m => new { m.Id, Distance = m.Embedding!.CosineDistance(queryVec) })
            .OrderBy(x => x.Distance)
            .Take(k)
            .ToListAsync(ct);
        return rows.Select(r => new RankedMemory(r.Id, 1.0 - r.Distance)).ToList();
    }

    public async Task<List<RankedMemory>> KeywordSearchAsync(string userId, string query, int k, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<RankedMemory>();

        const string sql = """
            SELECT id, ts_rank(ts, plainto_tsquery('english', $1)) AS score
            FROM memories
            WHERE user_id = $2 AND active
              AND ts @@ plainto_tsquery('english', $1)
            ORDER BY score DESC
            LIMIT $3
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter { Value = query });
        cmd.Parameters.Add(new NpgsqlParameter { Value = userId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = k });
        var results = new List<RankedMemory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RankedMemory(reader.GetGuid(0), reader.GetFieldValue<float>(1)));
        }
        return results;
    }

    public Task<List<Memory>> LoadByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? Task.FromResult(new List<Memory>())
            : db.Memories.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
}
