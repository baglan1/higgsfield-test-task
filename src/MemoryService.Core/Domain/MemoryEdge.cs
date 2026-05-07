namespace MemoryService.Core.Domain;

public sealed class MemoryEdge
{
    public Guid SrcMemoryId { get; set; }
    public string Relation { get; set; } = "";
    public Guid DstMemoryId { get; set; }
    public DateTime CreatedAt { get; set; }
}
