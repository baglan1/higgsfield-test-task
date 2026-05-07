namespace MemoryService.Core.Domain;

public sealed record ChatMessage(string Role, string Content, string? Name = null);
