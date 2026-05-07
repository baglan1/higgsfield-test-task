using Pgvector;

namespace MemoryService.Core.Domain;

public sealed class Memory
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public MemoryType Type { get; set; }
    public string Subject { get; set; } = "";
    public string? Predicate { get; set; }
    public string? Object { get; set; }
    public string? Aspect { get; set; }
    public string? Stance { get; set; }
    public string Text { get; set; } = "";
    public Vector Embedding { get; set; } = null!;
    public float Confidence { get; set; } = 0.7f;
    public Guid SourceTurnId { get; set; }
    public bool Active { get; set; } = true;
    public Guid? Supersedes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
