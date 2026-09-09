using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The last gate a piece of composed prose passes before this CLI posts it to GitHub under the
/// operator's own login: the review note <c>h9k pr approve</c> and <c>h9k pr request-changes</c>
/// submit, and the top-level comment <c>h9k review resolve</c> posts to answer a review's body
/// (task 412afe6c). The daemon's own copy of this gate is
/// <c>Hall9k.Daemon.Execution.PullRequestOpener.Vetted</c>, on the one piece of prose it posts.
/// <para>
/// The two sides differ in exactly one way, and deliberately. The daemon is unattended: a refused
/// piece of prose is dropped, the fallback underneath it is posted, and the run carries on,
/// because stranding finished work over a style miss costs more than the miss. Here there is a
/// person at the terminal who can fix the sentence in ten seconds, so a refusal stops the command
/// before anything is posted and tells them what to change. A rewrite is reported either way
/// rather than done silently: prose posted under someone's login should never differ from what
/// they last read without their being told.
/// </para>
/// </summary>
internal static class PostedProse
{
    /// <summary>
    /// The text to post, rewritten where a convention had a mechanical fix. Throws
    /// <see cref="DomainValidationException"/> where it did not, before the caller posts anything.
    /// </summary>
    /// <param name="text">The prose about to be posted.</param>
    /// <param name="conventions">The project's own house style.</param>
    /// <param name="what">What this prose is, named the way the operator would name it.</param>
    internal static string Vet(string text, WritingConventions conventions, string what)
    {
        WritingConventionsVerdict verdict = WritingConventionsCheck.Vet(text, conventions);
        if (verdict.Refused)
        {
            throw new DomainValidationException(
                $"NOTHING was posted: {what} breaks {verdict.Refusal}. That is this project's own writing "
                + "conventions (h9k project show names them, h9k project set --writing-conventions changes "
                + "them), and this one has no mechanical fix, so it is yours to reword. Re-run with the text "
                + "corrected.");
        }

        if (verdict.Rewrites.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Rewritten to this project's writing conventions before posting:[/] [dim]{what}, {string.Join("; ", verdict.Rewrites)}.[/]");
        }

        return verdict.Text;
    }
}
