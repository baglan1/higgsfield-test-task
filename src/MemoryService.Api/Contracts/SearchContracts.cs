namespace MemoryService.Api.Contracts;

public sealed record SearchRequest(string Query, string? SessionId, string? UserId, int Limit);

public sealed record SearchResultItem(
    string Content,
    double Score,
    string SessionId,
    DateTime Timestamp,
    Dictionary<string, object>? Metadata);

public sealed record SearchResponse(IReadOnlyList<SearchResultItem> Results);
