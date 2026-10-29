namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Lets <c>--invite -</c> reach its command: Spectre reads a lone <c>-</c> as the start of an
/// option with no name and refuses the whole line ("Option does not have a name"), which left the
/// documented stdin form of <c>h9k project join --invite -</c> unusable even though
/// <see cref="InviteTokenInput"/> handles it. <c>--invite=-</c> parses, so this rewrites an option
/// followed by a lone <c>-</c> into that form before the command app sees the arguments.
/// <para>
/// A lone <c>-</c> never parsed as anything else, so no accepted line changes meaning. Whether a given
/// option takes a value does not matter here: one that does not would have refused the <c>-</c> too.
/// </para>
/// </summary>
internal static class StdinOptionValue
{
    public static string[] Normalise(string[] args)
    {
        List<string> normalised = [];
        for (int index = 0; index < args.Length; index++)
        {
            bool isOptionBeforeLoneDash = args[index].StartsWith("--", StringComparison.Ordinal)
                && args[index].Length > 2
                && !args[index].Contains('=')
                && index + 1 < args.Length
                && args[index + 1] == "-";
            if (isOptionBeforeLoneDash)
            {
                normalised.Add($"{args[index]}=-");
                index++;
            }
            else
            {
                normalised.Add(args[index]);
            }
        }

        return [.. normalised];
    }
}

/// <summary>Every rewrite <c>Program.cs</c> applies to the arguments before the command app runs, in one place so the tests that parse examples apply the same ones.</summary>
internal static class CommandLineArguments
{
    public static string[] Normalise(string[] args) => BarePositionalRecording.Normalise(StdinOptionValue.Normalise(args));
}
