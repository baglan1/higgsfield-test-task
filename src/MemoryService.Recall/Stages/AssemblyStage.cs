namespace MemoryService.Recall.Stages;

/// <summary>Final stage — packs Tier A/B/C into the markdown context under <see cref="RecallContext.MaxTokens"/> and emits the result.</summary>
public sealed class AssemblyStage(ContextAssembler assembler) : IRecallStage
{
    public string Name => "Assembly";

    public Task RunAsync(RecallContext ctx, CancellationToken ct)
    {
        // If LowScoreFilter was disabled, fall back to the unfiltered fused scores.
        var ranked = ctx.Ranked.Count > 0 ? ctx.Ranked : ctx.Combined;
        var assembled = assembler.Assemble(
            new TierInputs(ctx.StableFacts, ranked, ctx.Relevant, ctx.RecentTurns),
            ctx.MaxTokens);
        ctx.Terminate(new RecallResult(assembled.Text, assembled.Citations));
        return Task.CompletedTask;
    }
}
