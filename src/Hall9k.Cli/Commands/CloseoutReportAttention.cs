using Hall9k.Domain.Features.Run;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The needs-you row for what closeout reported on a pull request it left alone because the task's
/// interactive flag is on (task: analysis-only closeout). Read off <see cref="CloseoutReport"/>, which
/// carries check names, review urls, and counts, never a reviewer's words, so <c>h9k status</c> and
/// <c>h9k task show</c> can render it with permissions bypassed.
/// <para>
/// The lever is the go that matches what the report holds, and every one of them hands the task back to
/// automation, which is what the cause says. <c>h9k pr resolve</c> in its three forms dispatches a lap for
/// threads, failing checks, and a conflict; no <c>pr resolve</c> form dispatches the changes-requested or
/// stacked-replay laps, so those rows (and the merge hold) name
/// <c>h9k task revise &lt;id&gt; --clear-interactive-mode</c>, after which the next poll dispatches the lap
/// as it always has.
/// </para>
/// </summary>
internal static class CloseoutReportAttention
{
    /// <summary>The row for a non-empty report on the task <paramref name="id"/> (its short id).</summary>
    public static TaskAttention Compose(CloseoutReport report, string id)
    {
        List<string> found = [];
        if (report.StackedParent is { } stacked)
        {
            found.Add($"its stacked parent moved or merged, so closeout left the retarget and replay alone: {stacked}");
        }

        if (report.Conflicting)
        {
            found.Add("the branch conflicts with its base, and closeout did not rebase it");
        }

        if (report.ChangesRequestedReviews.Count > 0)
        {
            found.Add(report.ChangesRequestedReviews.Count == 1
                ? $"a reviewer requested changes ({report.ChangesRequestedReviews[0]})"
                : $"{report.ChangesRequestedReviews.Count} reviews request changes "
                    + $"({string.Join(", ", report.ChangesRequestedReviews)})");
        }

        if (report.Threads is { } threads)
        {
            found.Add(DescribeThreads(threads));
        }

        if (report.FailingChecks.Count > 0)
        {
            found.Add($"CI checks are failing: {string.Join(", ", report.FailingChecks)}");
        }

        if (report.MergeHeld)
        {
            found.Add(
                "pre-approved; end iteration with `h9k task revise " + id + " --clear-interactive-mode`, "
                + "or merge it yourself on GitHub");
        }

        string lever = Lever(report, id);
        string cause = report.MergeHeld && found.Count == 1
            ? found[0]
            : $"closeout found, and left alone while you drive this task: {string.Join("; ", found)}. "
                + "The lever below hands the task back to automation";
        return new TaskAttention(AttentionLevel.NeedsYou, cause, lever);
    }

    /// <summary>The go matching what the report holds, in the order closeout would have acted on it.</summary>
    internal static string Lever(CloseoutReport report, string id) =>
        report switch
        {
            { ChangesRequestedReviews.Count: > 0 } => $"h9k task revise {id} --clear-interactive-mode",
            { Threads: not null } => $"h9k pr resolve {id}",
            { FailingChecks.Count: > 0 } => $"h9k pr resolve {id} --checks",
            { Conflicting: true } => $"h9k pr resolve {id} --rebase",
            _ => $"h9k task revise {id} --clear-interactive-mode",
        };

    private static string DescribeThreads(CloseoutThreadReport threads)
    {
        string noun = threads.ThreadCount == 1 ? "unresolved review thread" : "unresolved review threads";
        string triage = threads.Status.Value switch
        {
            "Running" => "a read-only session is triaging them",
            "Assessed" =>
                $"triaged: {threads.Fix} fix, {threads.Decline} decline, {threads.Route} route"
                + (threads.ReportPath is { Length: > 0 } path ? $"; read the report at {path}" : string.Empty),
            "PastCap" => $"no triage ran: {threads.Reason ?? "this task is past its triage allowance"}",
            "Failed" => $"no triage: {threads.Reason ?? "the triage session did not produce a report"}",
            _ => $"no triage ran: {threads.Reason ?? "no triage session is available"}",
        };
        return $"{threads.ThreadCount} {noun} ({triage})";
    }
}
