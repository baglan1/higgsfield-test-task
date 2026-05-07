namespace MemoryService.Core.Domain;

public sealed class Turn
{
    public Guid Id { get; set; }
    public string SessionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string MessagesJson { get; set; } = "[]";
    public string RawText { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";
}
