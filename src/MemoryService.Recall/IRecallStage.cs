namespace MemoryService.Recall;

/// <summary>
/// One step of the recall pipeline. Stages are registered in DI and looked up by name
/// from <see cref="RecallPipelineOptions.Stages"/>.
/// </summary>
public interface IRecallStage
{
    /// <summary>Identifier matched against <see cref="RecallPipelineOptions.Stages"/>. Case-insensitive.</summary>
    string Name { get; }

    /// <summary>Mutate the context. Call <see cref="RecallContext.Terminate"/> to stop the pipeline.</summary>
    Task RunAsync(RecallContext ctx, CancellationToken ct);
}
