namespace MemoryService.Api.Contracts;

public sealed record IngestMessage(string Role, string Content, string? Name);

public sealed record IngestTurnRequest(
    string SessionId,
    string? UserId,
    List<IngestMessage> Messages,
    DateTime Timestamp,
    Dictionary<string, object>? Metadata);

public sealed record IngestTurnResponse(string Id);
