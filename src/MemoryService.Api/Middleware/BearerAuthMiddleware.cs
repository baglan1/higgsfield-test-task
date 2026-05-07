using MemoryService.Core.Configuration;
using Microsoft.Extensions.Options;

namespace MemoryService.Api.Middleware;

public sealed class BearerAuthMiddleware(RequestDelegate next, IOptions<MemoryServiceOptions> options)
{
    private readonly string? _token = options.Value.AuthToken;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(_token))
        {
            await next(ctx);
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            await next(ctx);
            return;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(header["Bearer ".Length..].Trim(), _token, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }

        await next(ctx);
    }
}
