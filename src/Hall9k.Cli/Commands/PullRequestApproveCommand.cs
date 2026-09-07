using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The reviewer's approving verdict, and the end of their review lap (Decisions Log #149):
/// submits an APPROVE review on the pull request's current head with <c>--note</c> as its body,
/// records the verdict on the pr-review task, and hands the task to the daemon to finalize
/// exactly as <c>h9k review resolve --merge-ready</c> already does. See
/// <see cref="PullRequestReviewVerdict"/> for the delivery order and why the post comes first.
/// <para>
/// <c>--note</c> is required rather than defaulted. An approval with a platform-written body
/// would be this platform speaking under a human's login on somebody else's pull request, which
/// is the one thing the whole verdict path exists to keep from happening.
/// </para>
/// </summary>
public sealed class PullRequestApproveCommand : Hall9kAsyncCommand<PullRequestApproveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("The pr-review task your lap is attached to (full id, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--note <TEXT>")]
        [Description(
            "The approving review's body, in your own words — posted to GitHub under your own login. "
            + "Required: the platform never writes the words of a review a human is signing")]
        public string? Note { get; init; }

        public override ValidationResult Validate() =>
            Note.IsBlank()
                ? ValidationResult.Error(
                    "Pass --note \"<what the approval says>\". An approval posted with a body this platform "
                    + "wrote would be Hall9k speaking under your login on somebody else's pull request.")
                : ValidationResult.Success();
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        return await PullRequestReviewVerdict.DeliverAsync(
            session, taskId, ReviewerVerdict.Approved, settings.Note!.Trim(), findings: [],
            new GitHubPullRequestSurface(), cancellationToken);
    }
}
