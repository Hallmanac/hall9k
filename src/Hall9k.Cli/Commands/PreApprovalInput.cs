using Hall9k.Domain.Features.Tasks;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one place the CLI turns a human's pre-approval input into a <see cref="PreApprovalMode"/>
/// and back into a sentence (task: the people a pull request is waiting on are named, and
/// pre-approval gains a mode that waits for human review). Two commands set the fact —
/// <c>h9k task publish --pre-approved</c> and <c>h9k task set-pre-approved</c> — and three
/// surfaces read it back, so the vocabulary and the wording live here rather than in each of them.
/// <para>
/// Validation deliberately does NOT live here: <see cref="Handlers.TaskDecider.VetPreApprovalMode"/>
/// refuses an unrecognized mode, so a value that reaches these methods unrecognized is reported by
/// the decider with the vocabulary quoted, exactly as an agent needs in order to self-correct.
/// </para>
/// </summary>
internal static class PreApprovalInput
{
    /// <summary>
    /// What an optional-value <c>--pre-approved</c> flag means: null when it was not passed at all
    /// (which the decider reads as off), <see cref="PreApprovalMode.On"/> for the bare flag — the
    /// spelling that worked before the mode existed, and the one that must keep working — and
    /// otherwise whatever was typed, passed through unvetted, the same convention
    /// <see cref="Handlers.TaskDecider.VetModel"/> already uses for a model name.
    /// <para>
    /// So a returned mode built from a typed word is NOT a member of the closed vocabulary and must
    /// not be compared against one: <c>after-human-review</c> comes back with that exact
    /// <see cref="PreApprovalMode.Value"/>, which does not equal
    /// <see cref="PreApprovalMode.AfterHumanReview"/>'s. <see cref="PreApprovalMode.FromInput"/> is
    /// what normalizes it, and <see cref="Handlers.TaskDecider.VetPreApprovalMode"/> — the single
    /// gate every caller here goes through — is where that happens. The raw word survives that far
    /// on purpose: it is what the refusal quotes back, which is the whole of what makes the message
    /// self-correctable.
    /// </para>
    /// </summary>
    public static PreApprovalMode? FromFlag(FlagValue<string> flag) =>
        !flag.IsSet ? null
            : flag.Value.IsBlank() ? PreApprovalMode.On
            : flag.Value;

    /// <summary>
    /// The sentence a surface prints for a mode — what the daemon will actually do, in the words a
    /// human or an agent reading the terminal can act on. <see cref="PreApprovalMode.Unknown"/>
    /// never reaches a surface (the decider refuses it) and is named as unrecognized rather than
    /// described as one of the modes it might have been.
    /// </summary>
    public static string Describe(PreApprovalMode mode) => mode.Value switch
    {
        "On" => "the daemon merges this task's pull request on its own once GitHub's own gates are satisfied",
        "AfterHumanReview" => "the daemon merges this task's pull request on its own, but only once a human "
            + "reviewer has been requested on it and every requested reviewer has approved the current head "
            + "— add the reviewers you want in GitHub",
        "Off" => "the owner is a synchronous gate at the pull request; the daemon merges nothing",
        _ => $"the recorded pre-approval ({mode.Value}) is not one this build knows",
    };

    /// <summary>How a mode reads as a word on a status or detail surface — the CLI's own spelling, the one a human types back.</summary>
    public static string Word(PreApprovalMode mode) => mode.Value switch
    {
        "On" => "on",
        "AfterHumanReview" => "after-human-review",
        "Off" => "off",
        _ => mode.Value,
    };
}
