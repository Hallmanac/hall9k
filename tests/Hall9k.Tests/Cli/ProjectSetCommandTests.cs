using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Verification;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>--link</c> accepts. Origin: the pre-PR review of the project-home branch found
/// <c>ParseLink</c> calling <c>new Uri(...)</c> directly on a malformed value (a link with no
/// scheme, e.g. <c>design=github.com/you/proj/wiki</c>), so a caller got an unhandled
/// <c>UriFormatException</c> — a type the CLI's exception mapping does not know — instead of a
/// rule to self-correct from. This is the same defect class <see cref="GitRemoteUrl"/> was
/// introduced to fix for <c>--repo-url</c>.
/// </summary>
public sealed class ProjectSetCommandTests
{
    [Fact]
    public void A_well_formed_link_is_recorded_as_its_named_url()
    {
        ContextLink link = ProjectSetCommand.ParseLink("wiki=https://github.com/you/proj/wiki");

        link.Name.Should().Be("wiki");
        link.Url.Should().Be(new Uri("https://github.com/you/proj/wiki"));
    }

    [Fact]
    public void A_link_with_no_scheme_is_a_refusal_rather_than_an_unhandled_exception()
    {
        Action act = () => ProjectSetCommand.ParseLink("design=github.com/you/proj/wiki");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--link*", "the message has to teach its own correction");
    }

    [Fact]
    public void A_link_with_no_separator_is_a_refusal_naming_the_expected_shape()
    {
        Action act = () => ProjectSetCommand.ParseLink("not-a-name-value-pair");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*name=url*");
    }

    /// <summary>
    /// Origin: the pre-PR review of this branch found that prefixing the rejected value with
    /// <c>https://</c> degenerates to <c>https://</c> when the url half is blank, which
    /// <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> also rejects, so the worked
    /// example told the caller to retry with a value this same function refuses.
    /// </summary>
    [Fact]
    public void A_link_with_a_blank_url_suggests_a_correction_that_is_itself_valid()
    {
        Action act = () => ProjectSetCommand.ParseLink("wiki=");

        string message = act.Should().Throw<DomainValidationException>().Which.Message;

        message.Should().NotContain(
            "https://\".", "the suggested correction must not be the same value this call just rejected");
        message.Should().MatchRegex(@"--link ""wiki=https://\S+""");
    }

    /// <summary>
    /// Origin: the pre-PR review of this branch found that the blank-url guard above only
    /// covered that one degeneration; a rejected value whose url half is non-blank but still
    /// unparseable once prefixed with <c>https://</c> (an embedded space is the common case)
    /// produced a suggestion this same function would reject on the very next call.
    /// </summary>
    [Fact]
    public void A_link_whose_prefixed_suggestion_would_itself_fail_falls_back_to_the_worked_example()
    {
        Action act = () => ProjectSetCommand.ParseLink("design=Design Doc in Notion");

        string message = act.Should().Throw<DomainValidationException>().Which.Message;

        message.Should().Contain("--link \"design=https://example.com/wiki\"");
    }

    [Fact]
    public void A_link_with_a_working_prefix_correction_suggests_the_prefixed_value()
    {
        Action act = () => ProjectSetCommand.ParseLink("wiki=github.com/you/proj/wiki");

        string message = act.Should().Throw<DomainValidationException>().Which.Message;

        message.Should().Contain("--link \"wiki=https://github.com/you/proj/wiki\"");
    }

    /// <summary>
    /// Origin: the pre-PR review of this branch found that a blank name half (e.g. a leading
    /// space before the separator) was never validated, so the refusal for the unparseable url
    /// built a suggestion of the form <c>--link "=https://..."</c> that this same function
    /// rejects on the very next call. If the url half happened to parse, the blank name was
    /// accepted and recorded as a nameless <see cref="ContextLink"/>.
    /// </summary>
    [Fact]
    public void A_link_with_a_blank_name_is_a_refusal_naming_the_expected_shape()
    {
        Action act = () => ProjectSetCommand.ParseLink(" =github.com/you/proj/wiki");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*name=url*");
    }

    /// <summary>
    /// Origin: five duplicate routed findings (drafts 985ee66c, 0f9d780c, ee33068a, 2baaae12,
    /// 213a47d8), consolidated into the cleanup sweep this test file's own change closes.
    /// <c>ParseVerify</c> only ever checked <c>separator &lt;= 0</c>, the same hole
    /// <c>ParseLink</c> was hardened against above: a whitespace-only name before the `=`
    /// trims to empty without ever being checked, so <c>h9k project set p --verify " =dotnet
    /// test"</c> recorded a nameless <see cref="VerifyCommand"/> that
    /// <c>VerificationRunner.RunGateAsync</c> then wrote to a collision-prone <c>verify-.log</c>.
    /// </summary>
    [Fact]
    public void A_verify_gate_with_a_blank_name_is_a_refusal_naming_the_expected_shape()
    {
        Action act = () => ProjectSetCommand.ParseVerify(" =dotnet test");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*name=command*");
    }

    /// <summary>
    /// The sibling hole the same five drafts describe: a blank command half (`"build="`) was
    /// recorded as an empty <see cref="VerifyCommand.Command"/>, which
    /// <c>VerificationRunner.RunGateAsync</c> then ran as <c>sh -c "() > ... 2>&amp;1"</c> — a
    /// shell syntax error on every dispatched run — instead of being refused at the point it
    /// was typed.
    /// </summary>
    [Fact]
    public void A_verify_gate_with_a_blank_command_is_a_refusal_naming_the_expected_shape()
    {
        Action act = () => ProjectSetCommand.ParseVerify("build=");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*name=command*");
    }

    /// <summary>
    /// The Windows field report's own origin incident (item 11b): a verify gate that cannot pass
    /// on clean base is caught before it costs a run. This is the pure half of that check —
    /// composing the refusal's own words from a precomputed failure list, without spawning the
    /// process <c>ValidateGatesAgainstCleanBaseAsync</c> itself needs to produce one.
    /// </summary>
    [Fact]
    public void The_clean_base_refusal_names_every_failing_gate_its_command_and_its_output()
    {
        (VerifyCommand, GateCheckResult)[] failures =
        [
            (new VerifyCommand("test", "dotnet test"), new GateCheckResult(GateCheckOutcome.Failed, "2 Failed, 0 Passed")),
        ];

        string message = ProjectSetCommand.BuildCleanBaseRefusal("main", failures);

        message.Should().Contain("'test'")
            .And.Contain("dotnet test")
            .And.Contain("clean main")
            .And.Contain("2 Failed, 0 Passed")
            .And.Contain("--accept-broken-gate", "the refusal must teach its own way past itself");
    }

    [Fact]
    public void The_clean_base_refusal_names_every_gate_when_more_than_one_fails()
    {
        (VerifyCommand, GateCheckResult)[] failures =
        [
            (new VerifyCommand("build", "dotnet build"), new GateCheckResult(GateCheckOutcome.Failed, "error CS0000")),
            (new VerifyCommand("test", "dotnet test"), new GateCheckResult(GateCheckOutcome.Failed, "MSB4019")),
        ];

        string message = ProjectSetCommand.DescribeCleanBaseFailures("main", failures);

        message.Should().Contain("'build'").And.Contain("error CS0000");
        message.Should().Contain("'test'").And.Contain("MSB4019");
    }
}
