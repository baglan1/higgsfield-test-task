namespace MemoryService.Core.Domain;

public sealed class Session
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
