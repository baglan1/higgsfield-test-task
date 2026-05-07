using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryService.Recall;

public sealed record RecallQuery(string Query, string? SessionId, string? UserId, int MaxTokens);
public sealed record RecallResult(string Context, IReadOnlyList<RecallCitation> Citations);

/// <summary>
/// Stage-driven recall pipeline. Stages are registered in DI (one class per <see cref="IRecallStage"/>)
/// and executed in the order specified by <see cref="RecallPipelineOptions.Stages"/>.
///
/// To turn off a stage: remove its name from the list.
/// To reorder: change the position.
/// To tune: edit the per-stage values in <see cref="RecallPipelineOptions"/>.
///
/// All three are exposed via the "Recall" config section (see Program.cs).
/// </summary>
public sealed class RecallPipeline
{
    private readonly Dictionary<string, IRecallStage> _stages;
    private readonly RecallPipelineOptions _options;
    private readonly ILogger<RecallPipeline>? _logger;

    public RecallPipeline(
        IEnumerable<IRecallStage> stages,
        IOptions<RecallPipelineOptions> options,
        ILogger<RecallPipeline>? logger = null)
    {
        _stages = stages.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RecallResult> RecallAsync(RecallQuery req, CancellationToken ct)
    {
        // Input validation — guard the pipeline against missing query/user. (HybridRetrieval
        // already terminates when the user has no memories, so we don't re-check that here.)
        if (string.IsNullOrWhiteSpace(req.Query) || string.IsNullOrWhiteSpace(req.UserId))
            return new RecallResult("", Array.Empty<RecallCitation>());

        var ctx = new RecallContext
        {
            Query    = req,
            UserId   = req.UserId,
            MaxTokens = req.MaxTokens > 0 ? req.MaxTokens : 1024,
        };

        foreach (var name in _options.Stages)
        {
            if (!_stages.TryGetValue(name, out var stage))
            {
                _logger?.LogWarning("Recall pipeline references unknown stage '{Name}'; skipping. Registered: {Registered}",
                    name, string.Join(", ", _stages.Keys));
                continue;
            }

            try
            {
                await stage.RunAsync(ctx, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Recall stage '{Name}' threw; aborting pipeline", name);
                return ctx.Result ?? new RecallResult("", Array.Empty<RecallCitation>());
            }

            if (ctx.ShortCircuited) break;
        }

        return ctx.Result ?? new RecallResult("", Array.Empty<RecallCitation>());
    }
}
