using MemoryService.Core.Domain;
using MemoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MemoryService.Recall.Stages;

/// <summary>Tier A — stable user facts (active, type=fact, confidence ≥ threshold), regardless of query relevance.</summary>
public sealed class TierAStableFactsStage(
    MemoryDbContext db,
    IOptions<RecallPipelineOptions> options) : IRecallStage
{
    public string Name => "TierAStableFacts";

    public async Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        var opts = options.Value;
        ctx.StableFacts = await db.Memories.AsNoTracking()
            .Where(m => m.UserId == ctx.UserId && m.Active &&
                        m.Type == MemoryType.Fact && m.Confidence >= opts.StableFactConfidence)
            .OrderByDescending(m => m.Confidence)
            .ThenByDescending(m => m.UpdatedAt)
            .Take(opts.StableFactsTopK)
            .ToListAsync(ct);
    }
}
