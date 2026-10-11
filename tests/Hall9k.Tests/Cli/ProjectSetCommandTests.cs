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

    /// <summary>
    /// The pure half of <c>--verify-gate-filter</c> (task: host-coupled tests run in their own
    /// gate once per task, never in parallel with another run's copy — #225):
    /// composing the updated gate list without touching the project stream or spawning anything.
    /// </summary>
    [Fact]
    public void A_verify_gate_filter_marks_the_named_gate_host_coupled_and_leaves_the_others_alone()
    {
        VerifyCommand[] gates =
        [
            new VerifyCommand("build", "dotnet build"),
            new VerifyCommand("test", "dotnet test"),
        ];

        IReadOnlyList<VerifyCommand> updated =
            ProjectSetCommand.ApplyHostCoupledGateFilter(gates, "test=Category=RequiresDocker");

        updated.Should().Equal(
            new VerifyCommand("build", "dotnet build"),
            new VerifyCommand("test", "dotnet test", HostCoupledFilter: "Category=RequiresDocker"));
    }

    [Fact]
    public void A_second_verify_gate_filter_call_moves_the_designation_rather_than_stacking_it()
    {
        VerifyCommand[] gates =
        [
            new VerifyCommand("build", "dotnet build", HostCoupledFilter: "Category=RequiresDocker"),
            new VerifyCommand("test", "dotnet test"),
        ];

        IReadOnlyList<VerifyCommand> updated =
            ProjectSetCommand.ApplyHostCoupledGateFilter(gates, "test=Category=RequiresDocker");

        updated.Should().Equal(
            new VerifyCommand("build", "dotnet build"),
            new VerifyCommand("test", "dotnet test", HostCoupledFilter: "Category=RequiresDocker"));
    }

    [Fact]
    public void None_clears_the_host_coupled_designation_from_every_gate()
    {
        VerifyCommand[] gates =
        [
            new VerifyCommand("build", "dotnet build"),
            new VerifyCommand("test", "dotnet test", HostCoupledFilter: "Category=RequiresDocker"),
        ];

        IReadOnlyList<VerifyCommand> updated = ProjectSetCommand.ApplyHostCoupledGateFilter(gates, "none");

        updated.Should().Equal(
            new VerifyCommand("build", "dotnet build"),
            new VerifyCommand("test", "dotnet test"));
    }

    [Fact]
    public void A_verify_gate_filter_naming_an_unconfigured_gate_is_a_refusal_naming_it()
    {
        VerifyCommand[] gates = [new VerifyCommand("build", "dotnet build")];

        Action act = () => ProjectSetCommand.ApplyHostCoupledGateFilter(gates, "test=Category=RequiresDocker");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*'test'*")
            .WithMessage("*--verify*");
    }

    [Fact]
    public void A_verify_gate_filter_with_no_separator_is_a_refusal_naming_the_expected_shape()
    {
        VerifyCommand[] gates = [new VerifyCommand("test", "dotnet test")];

        Action act = () => ProjectSetCommand.ApplyHostCoupledGateFilter(gates, "not-a-name-filter-pair");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*name=filter*");
    }

    /// <summary>
    /// VerificationRunner.ComposeGateCommand injects a host-coupled gate's own filter as a
    /// `dotnet test --filter`, unconditionally, for whichever gate this designation names
    /// (independent pre-PR review, cycle 1, both lenses, medium) — a build gate, a lint gate, or
    /// any other shell command would have that flag spliced into it and fail (or, for a wrapper
    /// that ignores unknown arguments, silently run unfiltered) the first time the daemon actually
    /// spawns it. Refused here, at set time, rather than only failing later against a live run.
    /// </summary>
    [Fact]
    public void A_verify_gate_filter_naming_a_non_dotnet_test_gate_is_a_refusal_naming_the_command()
    {
        VerifyCommand[] gates = [new VerifyCommand("build", "dotnet build")];

        Action act = () => ProjectSetCommand.ApplyHostCoupledGateFilter(gates, "build=Category=RequiresDocker");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*'build'*")
            .WithMessage("*dotnet test*");
    }

    /// <summary>
    /// The pure half of <c>--verify-gate-paths</c> (task: a verify gate can be scoped to a path
    /// set): composing the updated gate list without touching the project stream or spawning anything.
    /// </summary>
    [Fact]
    public void A_verify_gate_paths_sets_the_named_gates_path_set_and_leaves_the_others_alone()
    {
        VerifyCommand[] gates =
        [
            new VerifyCommand("build", "dotnet build"),
            new VerifyCommand("vitest", "npm test"),
        ];

        IReadOnlyList<VerifyCommand> updated = ProjectSetCommand.ApplyGatePaths(
            gates, "vitest=Web/react-apps/customer-portal/, !Web/react-apps/customer-portal/legacy/");

        updated.Should().Equal(
            new VerifyCommand("build", "dotnet build"),
            new VerifyCommand(
                "vitest", "npm test", PathSet: ["Web/react-apps/customer-portal/", "!Web/react-apps/customer-portal/legacy/"]));
    }

    [Fact]
    public void A_verify_gate_paths_keeps_the_gates_host_coupled_filter()
    {
        VerifyCommand[] gates = [new VerifyCommand("test", "dotnet test", HostCoupledFilter: "Category=RequiresDocker")];

        ProjectSetCommand.ApplyGatePaths(gates, "test=src/")[0].HostCoupledFilter.Should().Be("Category=RequiresDocker");
    }

    [Fact]
    public void A_verify_gate_paths_set_to_none_clears_only_that_gates_set()
    {
        VerifyCommand[] gates =
        [
            new VerifyCommand("build", "dotnet build", PathSet: ["src/"]),
            new VerifyCommand("test", "dotnet test", PathSet: ["src/"]),
        ];

        IReadOnlyList<VerifyCommand> updated = ProjectSetCommand.ApplyGatePaths(gates, "test=none");

        updated.Should().Equal(
            new VerifyCommand("build", "dotnet build", PathSet: ["src/"]),
            new VerifyCommand("test", "dotnet test"));
    }

    [Fact]
    public void A_verify_gate_paths_naming_an_unconfigured_gate_is_a_refusal_naming_it()
    {
        Action act = () => ProjectSetCommand.ApplyGatePaths([new VerifyCommand("build", "dotnet build")], "test=src/");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*'test'*")
            .WithMessage("*--verify*");
    }

    [Theory]
    [InlineData("not-a-name-paths-pair")]
    [InlineData("test=")]
    [InlineData("=src/")]
    [InlineData("test=src/,,lib/")]
    public void A_malformed_verify_gate_paths_is_a_refusal_naming_the_expected_shape(string value)
    {
        Action act = () => ProjectSetCommand.ApplyGatePaths([new VerifyCommand("test", "dotnet test")], value);

        act.Should().Throw<DomainValidationException>().WithMessage("*--verify-gate-paths*");
    }

    [Theory]
    [InlineData("test=!docs/")]
    [InlineData("test=!docs/,!Playwright/")]
    public void A_verify_gate_paths_with_only_exclusions_is_a_refusal_because_it_would_always_skip(string value)
    {
        Action act = () => ProjectSetCommand.ApplyGatePaths([new VerifyCommand("test", "dotnet test")], value);

        act.Should().Throw<DomainValidationException>().WithMessage("*no positive glob*");
    }

    [Theory]
    [InlineData("test=/src/")]
    [InlineData("test=./src/")]
    [InlineData("test=src\\Services\\")]
    [InlineData("test=**,!/src/legacy/")]
    public void A_verify_gate_paths_glob_no_git_path_can_match_is_a_refusal_because_it_would_always_skip(string value)
    {
        Action act = () => ProjectSetCommand.ApplyGatePaths([new VerifyCommand("test", "dotnet test")], value);

        act.Should().Throw<DomainValidationException>().WithMessage("*can never match*");
    }

    [Fact]
    public void Changing_only_a_gates_path_set_validates_no_gate_against_clean_base()
    {
        VerifyCommand[] recorded = [new VerifyCommand("build", "dotnet build"), new VerifyCommand("test", "dotnet test")];
        IReadOnlyList<VerifyCommand> resolved = ProjectSetCommand.ApplyGatePaths(recorded, "test=src/");

        ProjectSetCommand.GatesChangedForValidation(resolved, recorded).Should().BeEmpty();
    }

    [Fact]
    public void A_changed_command_or_filter_is_still_validated_against_clean_base_whatever_its_path_set()
    {
        VerifyCommand[] recorded = [new VerifyCommand("build", "dotnet build", PathSet: ["src/"]), new VerifyCommand("test", "dotnet test")];
        VerifyCommand[] resolved =
        [
            new VerifyCommand("build", "dotnet build -c Release", PathSet: ["src/"]),
            new VerifyCommand("test", "dotnet test", HostCoupledFilter: "Category=RequiresDocker"),
        ];

        ProjectSetCommand.GatesChangedForValidation(resolved, recorded).Should().Equal(resolved);
    }

    [Fact]
    public void A_verify_gate_paths_accepts_an_exclusion_beside_a_positive_glob()
    {
        IReadOnlyList<VerifyCommand> updated = ProjectSetCommand.ApplyGatePaths(
            [new VerifyCommand("test", "dotnet test")], "test=**,!Playwright/");

        updated[0].PathSet.Should().Equal("**", "!Playwright/");
    }

    [Fact]
    public void A_second_verify_gate_paths_for_the_same_gate_replaces_the_first()
    {
        IReadOnlyList<VerifyCommand> updated = ProjectSetCommand.FoldGateFlags(
            "hall9k",
            recorded: [new VerifyCommand("test", "dotnet test")],
            accepted: [new VerifyCommand("test", "dotnet test")],
            verifyList: null,
            hostCoupledGateFilter: null,
            gatePaths: ["test=src/", "test=lib/"]) ?? [];

        updated[0].PathSet.Should().Equal("lib/");
    }

    [Fact]
    public void A_filter_and_paths_together_without_verify_fold_both_onto_an_accepted_recorded_set()
    {
        VerifyCommand[] recorded = [new VerifyCommand("test", "dotnet test")];

        IReadOnlyList<VerifyCommand> folded = ProjectSetCommand.FoldGateFlags(
            "hall9k", recorded, accepted: recorded, verifyList: null,
            hostCoupledGateFilter: "test=Category=RequiresDocker", gatePaths: ["test=src/"]) ?? [];

        folded.Should().Equal(
            new VerifyCommand("test", "dotnet test", HostCoupledFilter: "Category=RequiresDocker", PathSet: ["src/"]));
    }

    [Fact]
    public void A_filter_and_paths_together_without_verify_on_an_unaccepted_set_is_one_refusal_naming_both_flags()
    {
        VerifyCommand[] recorded = [new VerifyCommand("test", "dotnet test")];

        Action act = () => ProjectSetCommand.FoldGateFlags(
            "hall9k", recorded, accepted: null, verifyList: null,
            hostCoupledGateFilter: "test=Category=RequiresDocker", gatePaths: ["test=src/"]);

        act.Should().Throw<DomainConflictException>()
            .WithMessage("*--verify-gate-filter and --verify-gate-paths*")
            .WithMessage("*accept-gates*");
    }

    [Fact]
    public void A_bare_verify_gate_paths_on_an_unaccepted_set_is_refused_like_a_bare_filter()
    {
        Action act = () => ProjectSetCommand.FoldGateFlags(
            "hall9k", [new VerifyCommand("test", "dotnet test")], accepted: null, verifyList: null,
            hostCoupledGateFilter: null, gatePaths: ["test=src/"]);

        act.Should().Throw<DomainConflictException>().WithMessage("*--verify-gate-paths alone*");
    }

    [Fact]
    public void Verify_alongside_verify_gate_paths_folds_onto_the_list_verify_just_set_even_when_nothing_is_accepted()
    {
        VerifyCommand[] verifyList = [new VerifyCommand("test", "dotnet test")];

        IReadOnlyList<VerifyCommand> folded = ProjectSetCommand.FoldGateFlags(
            "hall9k", recorded: [], accepted: null, verifyList, hostCoupledGateFilter: null, gatePaths: ["test=src/"]) ?? [];

        folded.Should().Equal(new VerifyCommand("test", "dotnet test", PathSet: ["src/"]));
    }

    [Fact]
    public void No_gate_flag_leaves_the_gate_list_untouched()
    {
        ProjectSetCommand.FoldGateFlags(
            "hall9k", [new VerifyCommand("test", "dotnet test")], accepted: null, verifyList: null,
            hostCoupledGateFilter: null, gatePaths: []).Should().BeNull();
    }
}
