using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The run directory's <c>pr-summary.md</c>: written by whichever session last composed one, read
/// by <see cref="PullRequestOpener"/> when it opens the pull request.
/// <para>
/// One type rather than a copy in each caller because two sessions can write it — the build
/// session at <c>RunSupervisor.CaptureHandoffAsync</c>, and a review-fix session at
/// <c>ReviewEngine.RecordFixResultAsync</c> whose fixes changed what a reviewer of the whole pull
/// request needs to know — and a second copy of "write it only when a block exists" is a second
/// copy that can disagree about what "exists" means. The one rule both share: a result carrying
/// no block leaves whatever is already on disk alone, so a fix session that had nothing new to say
/// never erases the build session's own summary.
/// </para>
/// </summary>
internal static class PrSummaryArtifact
{
    /// <summary>
    /// Persists the block this session's result carried, or leaves the file exactly as it is when
    /// it carried none. Best-effort, like every other run artifact write: the run itself
    /// succeeded, and losing this file costs the pull request its authored prose, never the work.
    /// </summary>
    public static async Task CaptureAsync(
        ILogger logger, Guid runId, string runDirectory, string? summary, CancellationToken cancellationToken)
    {
        if (PrSummaryParser.Parse(summary) is not { } prSummary)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(
                RunPaths.PrSummaryFile(runDirectory),
                PrSummaryParser.Render(OverExisting(logger, runId, runDirectory, prSummary)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the pr-summary artifact for run {RunId}", runId);
        }
    }

    /// <summary>
    /// The block as it should land on disk: this session's own halves, with whatever it left out
    /// taken from the block already there.
    /// <para>
    /// The same rule <see cref="CaptureAsync"/> applies to a whole missing block, applied to half
    /// of one. A review-fix session whose fixes changed the story writes a fresh body and, reading
    /// the build session's title as still standing, routinely omits the <c>Title:</c> line; a
    /// whole-file replace would drop that title and open the pull request under the
    /// truncated-objective fallback instead — a milder rerun of the very shape this artifact
    /// exists to fix (independent pre-PR review, cycle 1, adversarial lens). The body half is the
    /// same defect mirrored, so it carries forward on the same terms.
    /// </para>
    /// <para>
    /// It is a carry-forward, never a merge of prose: a half this session actually wrote always
    /// wins whole, so a fix session that rewrote the body replaces it outright rather than
    /// appending to what was there. Only a half it said nothing about is inherited, which is what
    /// keeps this from guessing at what a session meant (AGENTS.md: never guess at unobserved
    /// facts) — a missing <c>Title:</c> line and an empty body are the two observations that mean
    /// "this session said nothing about that half", and <see cref="PrSummaryParser.ParseBlock"/>
    /// already records each as exactly that rather than as an authored blank.
    /// </para>
    /// </summary>
    private static PrSummaryParser.PrSummary OverExisting(
        ILogger logger, Guid runId, string runDirectory, PrSummaryParser.PrSummary fresh)
    {
        if ((fresh.Title is not null && fresh.Body.IsNotBlank())
            || TryRead(logger, runId, runDirectory) is not { } existing)
        {
            return fresh;
        }

        return new PrSummaryParser.PrSummary(
            fresh.Title ?? existing.Title, fresh.Body.IsNotBlank() ? fresh.Body : existing.Body);
    }

    /// <summary>
    /// What the last session to compose one wrote, or null when no session did (an interactive
    /// claim delivered by hand, a session killed before its final message, a run whose stream
    /// predates this artifact). Null is what puts <see cref="PullRequestBody"/> back on the
    /// skeleton it has always composed.
    /// </summary>
    public static PrSummaryParser.PrSummary? TryRead(ILogger logger, Guid runId, string runDirectory)
    {
        try
        {
            string file = RunPaths.PrSummaryFile(runDirectory);
            return File.Exists(file) ? PrSummaryParser.ParseBlock(File.ReadAllText(file)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the pr-summary artifact for run {RunId}", runId);
            return null;
        }
    }
}
