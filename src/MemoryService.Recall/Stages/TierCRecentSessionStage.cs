using MemoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MemoryService.Recall.Stages;

/// <summary>Tier C — last <c>MaxRecentTurns</c> turns from the same session for short-term context.</summary>
public sealed class TierCRecentSessionStage(
    MemoryDbContext db,
    IOptions<RecallPipelineOptions> options) : IRecallStage
{
    public string Name => "TierCRecentSession";

    public async Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.Query.SessionId)) return;

        ctx.RecentTurns = await db.Turns.AsNoTracking()
            .Where(t => t.SessionId == ctx.Query.SessionId)
            .OrderByDescending(t => t.Timestamp)
            .Take(options.Value.MaxRecentTurns)
            .ToListAsync(ct);
    }
}
