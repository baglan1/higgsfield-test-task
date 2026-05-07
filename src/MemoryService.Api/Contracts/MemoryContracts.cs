namespace MemoryService.Api.Contracts;

public sealed record MemoryDto(
    string Id,
    string Type,
    string Key,
    string Value,
    double Confidence,
    string SourceSession,
    string SourceTurn,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Supersedes,
    bool Active);

public sealed record MemoriesResponse(IReadOnlyList<MemoryDto> Memories);
