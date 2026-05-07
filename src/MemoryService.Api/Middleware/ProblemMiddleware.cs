using Microsoft.Extensions.Logging;

namespace MemoryService.Api.Middleware;

public sealed class ProblemMiddleware(RequestDelegate next, ILogger<ProblemMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (BadHttpRequestException ex)
        {
            log.LogWarning(ex, "Bad request: {Message}", ex.Message);
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "bad_request", message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            log.LogWarning(ex, "Validation error: {Message}", ex.Message);
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "bad_request", message = ex.Message });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "internal", message = "an unexpected error occurred" });
        }
    }
}
