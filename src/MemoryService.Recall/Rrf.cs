namespace MemoryService.Recall;

public static class Rrf
{
    public static List<RankedMemory> Fuse(
        IReadOnlyList<RankedMemory> vector,
        IReadOnlyList<RankedMemory> lexical,
        double vectorWeight = 0.6,
        double lexicalWeight = 0.4,
        int k = 60)
    {
        var scores = new Dictionary<Guid, double>();
        Add(scores, vector, vectorWeight, k);
        Add(scores, lexical, lexicalWeight, k);
        return scores
            .Select(kv => new RankedMemory(kv.Key, kv.Value))
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    private static void Add(Dictionary<Guid, double> acc, IReadOnlyList<RankedMemory> list, double weight, int k)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var rank = i + 1;
            var contribution = weight / (k + rank);
            acc.TryGetValue(list[i].Id, out var current);
            acc[list[i].Id] = current + contribution;
        }
    }
}
