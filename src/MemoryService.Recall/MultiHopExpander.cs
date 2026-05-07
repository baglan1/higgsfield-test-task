using MemoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MemoryService.Recall;

public sealed class MultiHopExpander(MemoryDbContext db)
{
    private const double DepthDecay = 0.6; // hop-1 = 0.6 × seed; hop-2 = 0.36 × seed

    public async Task<List<RankedMemory>> ExpandAsync(IReadOnlyList<RankedMemory> seeds, int maxHops, CancellationToken ct)
    {
        if (seeds.Count == 0 || maxHops <= 0) return [];

        var seedIds = seeds.Select(s => s.Id).ToHashSet();

        // discovered[id] = best hop-decayed score for non-seed memories reachable from any seed.
        var discovered = new Dictionary<Guid, double>();

        // frontier[id] = best ORIGINAL seed score reaching this node at the current BFS layer.
        // We propagate the seed score (not the decayed score) so we can apply the correct
        // depth-decay exactly once per hop.
        var frontier = seeds.ToDictionary(s => s.Id, s => s.Score);

        for (int hop = 1; hop <= maxHops; hop++)
        {
            if (frontier.Count == 0) break;

            var srcIds = frontier.Keys.ToList();
            var edges = await db.MemoryEdges.AsNoTracking()
                .Where(e => srcIds.Contains(e.SrcMemoryId) || srcIds.Contains(e.DstMemoryId))
                .ToListAsync(ct);

            // Walk edges in both directions; carry the originating seed score forward.
            var nextOrigin = new Dictionary<Guid, double>();
            foreach (var e in edges)
            {
                if (frontier.TryGetValue(e.SrcMemoryId, out var srcOrigin))
                    BumpMax(nextOrigin, e.DstMemoryId, srcOrigin);
                if (frontier.TryGetValue(e.DstMemoryId, out var dstOrigin))
                    BumpMax(nextOrigin, e.SrcMemoryId, dstOrigin);
            }

            // BFS: keep only nodes we haven't seen yet (not seeds, not already discovered).
            var hopMultiplier = Math.Pow(DepthDecay, hop);
            var newFrontier = new Dictionary<Guid, double>();
            foreach (var (id, origin) in nextOrigin)
            {
                if (seedIds.Contains(id)) continue;
                if (discovered.ContainsKey(id)) continue;
                discovered[id] = origin * hopMultiplier;
                newFrontier[id] = origin;
            }
            frontier = newFrontier;
        }

        return [.. discovered.Select(kv => new RankedMemory(kv.Key, kv.Value))];
    }

    private static void BumpMax(Dictionary<Guid, double> map, Guid id, double score)
    {
        map.TryGetValue(id, out var current);
        if (score > current) map[id] = score;
    }
}
