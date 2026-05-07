using MemoryService.Api.Contracts;
using MemoryService.Core.Domain;
using MemoryService.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MemoryService.Api.Endpoints;

public static class MemoriesEndpoints
{
    public static IEndpointRouteBuilder MapMemoriesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/users/{userId}/memories", async (string userId, MemoryDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Memories.AsNoTracking()
                .Where(m => m.UserId == userId)
                .OrderByDescending(m => m.UpdatedAt)
                .ToListAsync(ct);

            var dtos = rows.Select(ToDto).ToList();
            return Results.Ok(new MemoriesResponse(dtos));
        });

        app.MapDelete("/sessions/{sessionId}", async (string sessionId, MemoryDbContext db, CancellationToken ct) =>
        {
            // Cascade via FK: deleting session removes turns and memories tied to it.
            var session = await db.Sessions.FindAsync(new object[] { sessionId }, ct);
            if (session is not null)
            {
                db.Sessions.Remove(session);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        app.MapDelete("/users/{userId}", async (string userId, MemoryDbContext db, CancellationToken ct) =>
        {
            var sessions = await db.Sessions.Where(s => s.UserId == userId).ToListAsync(ct);
            db.Sessions.RemoveRange(sessions);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private static MemoryDto ToDto(Memory m) => new(
        Id:            m.Id.ToString(),
        Type:          m.Type.ToString().ToLowerInvariant(),
        Key:           string.IsNullOrEmpty(m.Predicate) ? m.Subject : $"{m.Subject}.{m.Predicate}{(m.Aspect is null ? "" : $".{m.Aspect}")}",
        Value:         m.Object ?? m.Text,
        Confidence:    m.Confidence,
        SourceSession: m.SessionId,
        SourceTurn:    m.SourceTurnId.ToString(),
        CreatedAt:     m.CreatedAt,
        UpdatedAt:     m.UpdatedAt,
        Supersedes:    m.Supersedes?.ToString(),
        Active:        m.Active);
}
