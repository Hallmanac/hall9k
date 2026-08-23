using System.Text;
using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <para>
/// The CLI command standards in AGENTS.md say every command carries a description and at least one
/// worked example, because <c>--help</c> is how an agent discovers the platform and how it corrects
/// itself after a refusal. A standard nothing checks is a standard that decays one command at a
/// time, so these tests walk the real help tree — the same <see cref="CliCommandTree"/> the shipped
/// binary builds — rather than a list somebody has to remember to update.
/// </para>
/// <para>
/// Origin: the S1-13 audit (2026-08-22) walked the tree by hand and found <c>h9k logs</c> shipping
/// with no example at all. The gap is small in elapsed time and total in kind: the standard became
/// law on 2026-08-17 and <c>h9k logs</c> was written the day before it, so nothing ever re-read the
/// tree against the new rule, and five days of unrelated work went past the hole without touching
/// it. A rule nothing checks does not decay on a schedule; it is simply never applied to what
/// already shipped, which is what these tests exist to change.
/// </para>
/// </summary>
public sealed class CommandTreeHelpTests
{
    /// <summary>Every command path in the tree, branches included, root excluded.</summary>
    public static TheoryData<string> EveryCommand()
    {
        TheoryData<string> paths = [];
        foreach (string[] path in Walk([]))
        {
            paths.Add(string.Join(' ', path));
        }

        return paths;
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void Every_command_describes_itself(string path)
    {
        string help = Help(path.Split(' '));

        help.Should().Contain("DESCRIPTION:", $"h9k {path} has to say what it is for");
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void Every_command_shows_a_worked_example(string path)
    {
        // A branch inherits its children's examples, which is Spectre's doing and the right
        // answer: the examples a branch should show are the ways into it.
        string help = Help(path.Split(' '));

        help.Should().Contain("EXAMPLES:", $"h9k {path} has to show at least one real invocation");
    }

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void Every_example_invokes_the_command_it_documents(string path)
    {
        // An example that names a different command teaches the wrong call, and it is exactly the
        // mistake a copied-and-edited registration makes.
        IReadOnlyList<string> examples = Examples(Help(path.Split(' ')));

        examples.Should().NotBeEmpty();
        examples.Should().OnlyContain(
            example => example.StartsWith($"h9k {path} ") || example == $"h9k {path}",
            $"every example under h9k {path} has to be a call to it");
    }

    /// <summary>
    /// The root's examples are the first thing anyone reads, so they are stated rather than
    /// inherited: left to Spectre they are whatever registration order put first.
    /// </summary>
    [Fact]
    public void The_root_examples_walk_the_orchestrator_loop()
    {
        IReadOnlyList<string> examples = Examples(Help([]));

        examples.Should().StartWith("h9k status", "the pane comes first — what needs you");
        examples.Should().Contain(example => example.StartsWith("h9k task add "), "then the work is drafted");
        examples.Should().Contain(example => example.StartsWith("h9k task publish "), "then gated and dispatched");
    }

    /// <summary>
    /// <para>
    /// Spectre renders examples verbatim, so a multi-word value written without quotes renders as
    /// separate arguments and the printed line is not a command anyone can run. That matters more
    /// here than it would elsewhere: <see cref="UsageError"/> prints these back at an agent as the
    /// correction for a call it just got wrong.
    /// </para>
    /// <para>
    /// So the check is the claim itself rather than a proxy for it: each example is tokenised the
    /// way a shell would and fed back through the real command tree, which parses it, binds it and
    /// runs the settings validators. Counting quotes was the first cut and it could not see the
    /// defect it was written for, since an unquoted multi-word value has no quotes at all to
    /// unbalance — <c>h9k idea add The attention pane should teach the next command</c> passed a
    /// modulo check and would have shipped as six stray positional arguments.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void Every_example_is_runnable_as_printed(string path)
    {
        AreRunnableAsPrinted(Examples(Help(path.Split(' '))));
    }

    /// <summary>
    /// The same property over the root block, which <see cref="Walk"/> does not reach.
    /// </summary>
    /// <remarks>
    /// The walk starts below the root, so without this the one example block the tree hand-writes
    /// would be the only one nothing parses: a typo in it (<c>--asign</c>) or a multi-word value
    /// that lost its quotes would ship green, and it is the block a caller reads first. Today every
    /// root example happens to repeat one registered on a leaf command, which is a coincidence of
    /// the current list rather than a property of it.
    /// </remarks>
    [Fact]
    public void The_root_examples_are_runnable_as_printed()
    {
        AreRunnableAsPrinted(Examples(Help([])));
    }

    /// <summary>Every example in one block, tokenised as a shell would and fed back through the tree.</summary>
    private static void AreRunnableAsPrinted(IReadOnlyList<string> examples)
    {
        examples.Should().NotBeEmpty();
        foreach (string example in examples)
        {
            (example.Count(character => character == '"') % 2).Should().Be(
                0, $"'{example}' has to balance its quotes to be pasteable");

            Parse(example);
        }
    }

    /// <summary>
    /// Run one printed example through the shipped tree and stop it the instant it is understood.
    /// </summary>
    /// <remarks>
    /// <see cref="StopOnceBound"/> is reached only after Spectre has resolved the command, bound
    /// every argument and option onto its settings and run the settings validators, which is the
    /// whole of "runnable"; going one step further would need a database and would do the thing the
    /// example describes. Anything the parser rejects instead throws before the interceptor is ever
    /// called, so a failure to reach it is the example failing.
    /// </remarks>
    private static void Parse(string example)
    {
        IReadOnlyList<string> tokens = Tokenize(example);
        tokens[0].Should().Be(CliCommandTree.ApplicationName, "an example is a call to this binary");

        StopOnceBound interceptor = new();
        CommandApp app = new();
        app.Configure(config =>
        {
            CliCommandTree.Configure(config);
            config.SetInterceptor(interceptor);
            // Stricter than the shipped binary parses, on purpose. Spectre's default is to absorb an
            // option it does not recognise into the remaining arguments, so `--asign` reaches the
            // command as though nothing were wrong; that is a forgiving thing to do to a caller and
            // the wrong thing to do to an example, which is read as the definition of the call.
            config.UseStrictParsing();
        });

        Exception? refusal = null;
        try
        {
            app.Run([.. tokens.Skip(1)]);
        }
        catch (StopOnceBound.Bound)
        {
            // Parsed, bound and validated. That is the property; the command itself must not run.
        }
        catch (Exception failure)
        {
            refusal = failure;
        }

        refusal.Should().BeNull(
            $"'{example}' has to be a call h9k accepts exactly as printed, and it answered: {refusal?.Message}");
        interceptor.Reached.Should().BeTrue($"'{example}' has to reach a command with its settings bound");
    }

    /// <summary>The tokens a shell would hand the binary: whitespace splits, double quotes group.</summary>
    private static IReadOnlyList<string> Tokenize(string example)
    {
        List<string> tokens = [];
        StringBuilder token = new();
        bool quoted = false;
        bool started = false;

        foreach (char character in example)
        {
            switch (character)
            {
                case '"':
                    quoted = !quoted;
                    started = true;
                    break;

                case char whitespace when char.IsWhiteSpace(whitespace) && !quoted:
                    if (started)
                    {
                        tokens.Add(token.ToString());
                        token.Clear();
                        started = false;
                    }

                    break;

                default:
                    token.Append(character);
                    started = true;
                    break;
            }
        }

        if (started)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }

    /// <summary>Stops an example at the point it has been understood, before it can act.</summary>
    private sealed class StopOnceBound : ICommandInterceptor
    {
        public bool Reached { get; private set; }

        public void Intercept(CommandContext context, CommandSettings settings)
        {
            Reached = true;
            throw new Bound();
        }

        public sealed class Bound : Exception;
    }

    /// <summary>
    /// Depth-first over the tree, reading each level's children out of the rendered help rather
    /// than out of a second list of command names — the point is to check what ships.
    /// </summary>
    /// <remarks>
    /// The root is walked but not yielded: it has no description of its own and its help is a list
    /// of branches rather than a call. Its examples are stated rather than inherited, so they are
    /// checked by <see cref="The_root_examples_walk_the_orchestrator_loop"/> and
    /// <see cref="The_root_examples_are_runnable_as_printed"/> instead.
    /// </remarks>
    private static IEnumerable<string[]> Walk(string[] path)
    {
        string help = Help(path);
        if (path.Length > 0)
        {
            yield return path;
        }

        foreach (string child in Children(help))
        {
            foreach (string[] descendant in Walk([.. path, child]))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The command names listed under COMMANDS, which is how a branch is recognised.</summary>
    private static IReadOnlyList<string> Children(string help)
    {
        string[] lines = help.Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim() == "COMMANDS:");
        if (start < 0)
        {
            return [];
        }

        List<string> children = [];
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd();
            if (line.Length == 0)
            {
                break;
            }

            // Entries sit at four spaces; wrapped description text sits at the description column.
            if (line.StartsWith("     ") || !line.StartsWith("    "))
            {
                continue;
            }

            children.Add(line.Trim().Split(' ')[0]);
        }

        return children;
    }

    /// <summary>The lines of the EXAMPLES block, one invocation each.</summary>
    private static IReadOnlyList<string> Examples(string help)
    {
        string[] lines = help.Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim() == "EXAMPLES:");
        if (start < 0)
        {
            return [];
        }

        List<string> examples = [];
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd();
            if (line.Length == 0)
            {
                break;
            }

            examples.Add(line.Trim());
        }

        return examples;
    }

    private static string Help(string[] path)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            // Spectre's default enrichers turn ANSI back on whenever they recognise the host CI,
            // whatever AnsiSupport.No asked for, and escape sequences would break the parsing here.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        // The width the shipped help is rendered at (CliCommandTree.MinimumHelpWidth, floored on
        // both the ordinary --help path and the usage-error one), and deliberately not something
        // wider: the properties below are claims about what a caller actually reads, and certifying
        // them at a width the binary never uses is how nine wrapped examples shipped while this
        // file said they were runnable. A wrapped example lands here as a stray line that starts
        // with neither h9k nor the command it documents, so it fails loudly.
        console.Profile.Width = CliCommandTree.MinimumHelpWidth;

        CommandApp app = new();
        app.Configure(config =>
        {
            CliCommandTree.Configure(config);
            config.ConfigureConsole(console);
        });
        app.Run([.. path, "--help"]);

        return writer.ToString();
    }
}
