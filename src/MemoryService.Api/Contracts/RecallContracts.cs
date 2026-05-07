namespace MemoryService.Api.Contracts;

public sealed record RecallRequest(string Query, string SessionId, string? UserId, int MaxTokens);

public sealed record Citation(string TurnId, double Score, string Snippet);

public sealed record RecallResponse(string Context, IReadOnlyList<Citation> Citations);
