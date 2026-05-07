namespace MemoryService.Llm.Extraction;

public sealed record DerivedEdge(string Relation, string DstSubject);

public sealed record MemoryCandidate(
    string Type,
    string Subject,
    string? Predicate,
    string? Object,
    string? Aspect,
    string? Stance,
    string Text,
    double Confidence,
    IReadOnlyList<DerivedEdge>? DerivedEdges);
