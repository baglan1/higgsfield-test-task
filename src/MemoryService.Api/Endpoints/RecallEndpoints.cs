using MemoryService.Api.Contracts;
using MemoryService.Recall;

namespace MemoryService.Api.Endpoints;

public static class RecallEndpoints
{
    public static IEndpointRouteBuilder MapRecallEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/recall", async (RecallRequest req, RecallPipeline pipeline, CancellationToken ct) =>
        {
            var result = await pipeline.RecallAsync(
                new RecallQuery(req.Query, req.SessionId, req.UserId, req.MaxTokens), ct);
            var citations = result.Citations.Select(c => new Citation(c.TurnId, c.Score, c.Snippet)).ToList();
            return Results.Ok(new RecallResponse(result.Context, citations));
        });
        return app;
    }
}
