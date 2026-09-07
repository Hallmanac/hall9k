using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The reviewer's changes-requested verdict, and the end of their review lap (Decisions Log
/// #149): submits a REQUEST_CHANGES review on the pull request's current head with
/// <c>--note</c> as its body and each <c>--finding</c> as a line comment, records the verdict on
/// the pr-review task, and hands the task to the daemon to finalize exactly as
/// <c>h9k review resolve --merge-ready</c> already does. See
/// <see cref="PullRequestReviewVerdict"/> for the delivery order and why the post comes first.
/// <para>
/// <b>This is the one place in this codebase where an agent-adjacent command starts review
/// threads on a pull request, and it is legal only because a human ran it.</b> AGENTS.md's rule
/// is that agents never start a review thread — a thread's first comment is always a reviewer's,
/// because that is the only way the platform can tell a reviewer's comment from an agent's under
/// one shared login. A batched review submitted by <c>h9k pr request-changes</c> IS the
/// reviewer's, typed by them and run by them, so it is exactly the comment the rule reserves the
/// first position for. Nothing here lets a session post on its own the ordinary way: the lap's
/// own push guard denies <c>gh pr review</c>, <c>gh pr comment</c> and <c>gh api</c> — including
/// the very endpoint this command posts through — see
/// <c>ClaudeSettingsFile.ReviewLapDeniedTools</c>, whose own doc is honest about what a
/// permission deny is and is not.
/// </para>
/// </summary>
public sealed class PullRequestRequestChangesCommand : Hall9kAsyncCommand<PullRequestRequestChangesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("The pr-review task your lap is attached to (full id, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--note <TEXT>")]
        [Description(
            "The review's body — the summary of what needs to change, in your own words, posted under your "
            + "own login. Required: the platform never writes the words of a review a human is signing")]
        public string? Note { get; init; }

        [CommandOption("--finding <FILE:LINE: TEXT>")]
        [Description(
            "One line comment on the diff, as path:line: text — e.g. "
            + "\"src/Hall9k.Cli/Program.cs:42: this swallows the cancellation\". Repeat the option per "
            + "comment. The line has to be one the pull request's own diff contains; GitHub rejects the "
            + "whole review — body and every other comment with it — when one points anywhere else, so "
            + "nothing gets posted and you re-run with it corrected")]
        public string[] Findings { get; init; } = [];

        public override ValidationResult Validate() =>
            Note.IsBlank()
                ? ValidationResult.Error(
                    "Pass --note \"<the summary of what needs to change>\". A changes-requested review posted "
                    + "with a body this platform wrote would be Hall9k speaking under your login on somebody "
                    + "else's pull request.")
                : ValidationResult.Success();
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        // Parsed before the store is opened and before anything is read: a malformed --finding is
        // a bad command line, and refusing it here costs nothing and leaves nothing behind.
        PullRequestReviewLineComment[] findings =
            [.. settings.Findings.Select(PullRequestReviewLineComment.Parse)];

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        return await PullRequestReviewVerdict.DeliverAsync(
            session, taskId, ReviewerVerdict.ChangesRequested, settings.Note!.Trim(), findings,
            new GitHubPullRequestSurface(), cancellationToken);
    }
}
