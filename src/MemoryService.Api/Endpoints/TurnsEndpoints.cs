using MemoryService.Api.Contracts;
using MemoryService.Api.Services;

namespace MemoryService.Api.Endpoints;

public static class TurnsEndpoints
{
    public static IEndpointRouteBuilder MapTurnsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/turns", async (IngestTurnRequest req, TurnService svc, CancellationToken ct) =>
        {
            var resp = await svc.IngestAsync(req, ct);
            return Results.Created($"/turns/{resp.Id}", resp);
        });
        return app;
    }
}
