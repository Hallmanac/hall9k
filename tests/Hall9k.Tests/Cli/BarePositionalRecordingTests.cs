using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The bare positional form always writes and never reads (idea d805fd8b, piece 1). Spectre
/// cannot bind a positional argument to a branch's default command, so the argument list is
/// normalised before the command app sees it — and the failure this guards against is the
/// rewrite firing on something it should have left alone, which would turn a read into a write.
/// </summary>
public sealed class BarePositionalRecordingTests
{
    [Theory]
    [InlineData("decide")]
    [InlineData("learn")]
    public void A_bare_statement_reaches_the_record_subcommand(string branch)
    {
        string[] normalised = BarePositionalRecording.Normalise([branch, "One claim, stated as a rule"]);

        normalised.Should().Equal(branch, "record", "One claim, stated as a rule");
    }

    [Theory]
    [InlineData("decide", "list")]
    [InlineData("decide", "show")]
    [InlineData("decide", "supersede")]
    [InlineData("decide", "record")]
    [InlineData("learn", "list")]
    [InlineData("learn", "show")]
    [InlineData("learn", "retire")]
    [InlineData("learn", "record")]
    public void A_named_subcommand_is_left_exactly_as_it_was(string branch, string subcommand)
    {
        string[] normalised = BarePositionalRecording.Normalise([branch, subcommand, "--all"]);

        normalised.Should().Equal(branch, subcommand, "--all");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void An_option_is_left_exactly_as_it_was(string option)
    {
        BarePositionalRecording.Normalise(["decide", option]).Should().Equal("decide", option);
    }

    [Fact]
    public void The_branch_name_alone_is_left_alone_so_the_branch_help_still_answers()
    {
        BarePositionalRecording.Normalise(["decide"]).Should().Equal("decide");
        BarePositionalRecording.Normalise([]).Should().BeEmpty();
    }

    [Fact]
    public void Every_other_command_passes_through_untouched()
    {
        string[] original = ["task", "list", "--project", "hall9k"];

        BarePositionalRecording.Normalise(original).Should().BeSameAs(original);
    }

    [Fact]
    public void The_statements_own_options_ride_along_with_it()
    {
        string[] normalised = BarePositionalRecording.Normalise(
            ["decide", "One claim", "--origin", "An incident", "--owner"]);

        normalised.Should().Equal("decide", "record", "One claim", "--origin", "An incident", "--owner");
    }

    /// <summary>
    /// The statement does not have to be the token right after the branch (independent pre-PR
    /// review, cycle 1, both lenses): an option typed first is the shape Spectre accepts
    /// everywhere else, and it used to fail with an unknown-command usage error. Whether an
    /// option takes a value never has to be known here, because <c>record</c> goes in directly
    /// after the branch name and Spectre binds the rest from there.
    /// </summary>
    [Fact]
    public void A_flag_typed_before_the_statement_still_reaches_the_record_subcommand()
    {
        BarePositionalRecording.Normalise(["decide", "--owner", "Split ternaries across lines"])
            .Should().Equal("decide", "record", "--owner", "Split ternaries across lines");
    }

    [Fact]
    public void An_option_with_a_value_typed_before_the_statement_still_reaches_the_record_subcommand()
    {
        BarePositionalRecording.Normalise(["learn", "--project", "hall9k", "Docker first, then dotnet test"])
            .Should().Equal("learn", "record", "--project", "hall9k", "Docker first, then dotnet test");
    }

    /// <summary>
    /// The read this must never turn into a write: a subcommand keeps its place however many
    /// options follow it.
    /// </summary>
    [Fact]
    public void A_subcommand_with_its_own_option_values_is_still_left_alone()
    {
        string[] original = ["decide", "list", "--project", "hall9k", "--all"];

        BarePositionalRecording.Normalise(original).Should().BeSameAs(original);
    }

    /// <summary>
    /// The list of names this rewrite must not swallow lives beside the branch registration, and
    /// the two drifting apart is the failure mode: a subcommand added to the tree and forgotten
    /// here would start being recorded as a statement instead of being run. Read off the shipped
    /// tree's own walk (<see cref="CommandTreeHelpTests.EveryCommand"/>) rather than a second
    /// hand-kept list, which would only move the drift somewhere else.
    /// </summary>
    [Theory]
    [InlineData("decide")]
    [InlineData("learn")]
    public void The_reserved_names_are_exactly_the_branchs_own_subcommands(string branch)
    {
        string[] inTheTree = [.. ((IEnumerable<object[]>)CommandTreeHelpTests.EveryCommand())
            .Select(row => (string)row[0])
            .Where(path => path.StartsWith(branch + " ", StringComparison.Ordinal))
            .Select(path => path[(branch.Length + 1)..])];

        inTheTree.Should().NotBeEmpty($"h9k {branch} has to have subcommands for this to mean anything");
        BarePositionalRecording.Branches[branch].Should().BeEquivalentTo(inTheTree);
    }

    /// <summary>
    /// The record subcommand's own description is where a human learns which statements the bare
    /// positional form cannot carry, so it has to name every reserved word rather than whichever
    /// ones existed when it was written. Origin: adding <c>h9k learn distill</c> put a fifth
    /// reserved word in the branch and left that sentence listing four (self-review, this branch),
    /// which is the same registration-and-prose drift the test above guards for the reserved list
    /// itself.
    /// </summary>
    [Theory]
    [InlineData("decide")]
    [InlineData("learn")]
    public void The_record_subcommands_description_names_every_word_it_cannot_record(string branch)
    {
        // Read off the rendered help a caller actually sees, with its word wrapping flattened
        // first: the shipped width breaks that sentence across lines, and a raw Contains against
        // the wrapped text would fail on where the break happens rather than on what it says.
        string help = string.Join(' ', CommandTreeHelpTests
            .Help([branch, "record"])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        foreach (string reserved in BarePositionalRecording.Branches[branch])
        {
            help.Should().Contain(reserved, $"h9k {branch} \"{reserved}\" cannot be recorded as a statement");
        }
    }
}
