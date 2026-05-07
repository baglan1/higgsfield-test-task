using MemoryService.Infrastructure;
using MemoryService.Llm;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace MemoryService.Recall;

public sealed record SearchQuery(string Query, string? SessionId, string? UserId, int Limit);

public sealed record SearchHit(
    string Content,
    double Score,
    string SessionId,
    DateTime Timestamp,
    Dictionary<string, object>? Metadata);

public sealed record SearchResult(IReadOnlyList<SearchHit> Results);

public sealed class SearchService(HybridRetriever retriever, IEmbedder embedder, MemoryDbContext db)
{
    public async Task<SearchResult> SearchAsync(SearchQuery req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query) || string.IsNullOrWhiteSpace(req.UserId))
            return new SearchResult(Array.Empty<SearchHit>());

        var hasAny = await db.Memories.AsNoTracking().AnyAsync(m => m.UserId == req.UserId && m.Active, ct);
        if (!hasAny) return new SearchResult(Array.Empty<SearchHit>());

        var limit = req.Limit > 0 ? Math.Min(req.Limit, 50) : 10;
        var qVecRaw = await embedder.EmbedAsync(req.Query, ct);
        if (qVecRaw.Length != embedder.Dimension)
            return new SearchResult(Array.Empty<SearchHit>());
        var qVec = new Vector(qVecRaw);

        var vec = await retriever.VectorSearchAsync(req.UserId!, qVec, limit * 2, ct);
        var lex = await retriever.KeywordSearchAsync(req.UserId!, req.Query, limit * 2, ct);
        var fused = Rrf.Fuse(vec, lex);
        var top = fused.Take(limit).ToList();
        if (top.Count == 0) return new SearchResult(Array.Empty<SearchHit>());

        var ids = top.Select(t => t.Id).ToList();
        var memories = await retriever.LoadByIdsAsync(ids, ct);
        var byId = memories.ToDictionary(m => m.Id);

        var hits = top
            .Where(t => byId.ContainsKey(t.Id))
            .Select(t =>
            {
                var m = byId[t.Id];
                if (!string.IsNullOrEmpty(req.SessionId) && m.SessionId != req.SessionId) return null;
                return new SearchHit(
                    Content:   m.Text,
                    Score:     t.Score,
                    SessionId: m.SessionId,
                    Timestamp: m.UpdatedAt,
                    Metadata:  null);
            })
            .Where(h => h is not null)
            .Select(h => h!)
            .ToList();

        return new SearchResult(hits);
    }
}
