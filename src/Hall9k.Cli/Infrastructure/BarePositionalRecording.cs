namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Makes <c>h9k decide "…"</c> and <c>h9k learn "…"</c> reach their record command (idea
/// d805fd8b, piece 1: the bare positional form always writes and never reads).
/// <para>
/// This exists because Spectre's own <c>SetDefaultCommand</c> on a branch cannot take a
/// positional argument: verified against Spectre.Console.Cli 0.55.0 by running the built binary,
/// which answered <c>Unknown command 'one claim here'</c> and printed the branch help rather than
/// binding the statement. Its own documentation says as much ("It must be able to execute
/// successfully by itself i.e. without requiring any command line arguments"). So the branch
/// keeps a plain, fully documented <c>record</c> subcommand and this inserts it, once, before the
/// command app ever sees the arguments.
/// </para>
/// <para>
/// Deliberately the narrowest rewrite that does the job. It fires only for these two branch
/// names, only when the token after the branch is not one of that branch's own subcommands, and
/// only when something after the branch could be a statement at all — so <c>h9k decide --help</c>,
/// which carries nothing but options, still reaches Spectre untouched.
/// </para>
/// <para>
/// The statement does not have to come first. <c>h9k decide --owner "…"</c> and <c>h9k learn
/// --project hall9k "…"</c> are the shape Spectre accepts everywhere else, and an earlier version
/// of this that only looked at the token immediately after the branch failed both with an
/// unknown-command usage error (independent pre-PR review, cycle 1, both lenses). Whether a given
/// option takes a value is not knowable here and does not need to be: <c>record</c> is inserted
/// directly after the branch name either way, and Spectre binds the options and the positional
/// from there exactly as it would have if the subcommand had been typed.
/// </para>
/// <para>
/// Two statements it still cannot record, both with a door of their own. One whose entire text is
/// the name of a subcommand, since that token has to stay a subcommand: <c>h9k decide record
/// "list"</c>. And one that begins with a dash, which reads as an option to this and to Spectre
/// alike: <c>h9k decide record -- "-fN is never inlined"</c>. The branch help names the record
/// subcommand for exactly these two reasons.
/// </para>
/// </summary>
internal static class BarePositionalRecording
{
    private const string RecordSubcommand = "record";

    /// <summary>
    /// Each recording branch and the subcommand names that must never be read as a statement.
    /// Kept beside <see cref="CliCommandTree"/>'s own registration of the same two branches: a
    /// subcommand added there and forgotten here would start swallowing statements, which is why
    /// <c>BarePositionalRecordingTests</c> asserts the two lists agree.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Branches =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["decide"] = [RecordSubcommand, "list", "show", "supersede", "import"],
            ["learn"] = [RecordSubcommand, "list", "show", "retire", "distill"],
        };

    public static string[] Normalise(string[] args)
    {
        if (args.Length < 2 || !Branches.TryGetValue(args[0], out string[]? subcommands))
        {
            return args;
        }

        if (subcommands.Contains(args[1], StringComparer.Ordinal))
        {
            return args;
        }

        // Anything that is not an option is either the statement itself or some option's value,
        // and both mean this call is a recording — `h9k decide --help` is the shape with nothing
        // but options, and it is the one that must pass through. An option's value standing alone
        // (`h9k decide --project hall9k`, with no statement typed) reaches the record command and
        // is refused there for the missing statement, which is the honest error.
        bool carriesSomethingToRecord = args[1..].Any(argument => !argument.StartsWith('-'));

        return carriesSomethingToRecord
            ? [args[0], RecordSubcommand, .. args[1..]]
            : args;
    }
}
