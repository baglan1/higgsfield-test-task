using MemoryService.Api.Contracts;
using MemoryService.Recall;

namespace MemoryService.Api.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/search", async (SearchRequest req, SearchService svc, CancellationToken ct) =>
        {
            var result = await svc.SearchAsync(
                new SearchQuery(req.Query, req.SessionId, req.UserId, req.Limit), ct);
            var items = result.Results
                .Select(h => new SearchResultItem(h.Content, h.Score, h.SessionId, h.Timestamp, h.Metadata))
                .ToList();
            return Results.Ok(new SearchResponse(items));
        });
        return app;
    }
}
