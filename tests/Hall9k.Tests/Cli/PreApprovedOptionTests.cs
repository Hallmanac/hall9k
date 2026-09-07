using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>--pre-approved</c> became an optional-value option (task: the people a pull request is
/// waiting on are named, and pre-approval gains a mode that waits for human review), and what
/// Spectre actually binds for the three shapes on a command line is the one thing about that change
/// no amount of reading the code settles. So the real parser runs here, against
/// <see cref="TaskPublishCommand.Settings"/> itself rather than a copy of it, with the command body
/// replaced by a capture — the whole point being to exercise the binding without a database.
/// <para>
/// The bare flag is the shape that matters most: it is what every call written before the mode
/// existed uses, and it has to keep meaning <see cref="PreApprovalMode.On"/>. The
/// bare-flag-followed-by-another-option shape is the one that could plausibly have gone wrong,
/// since an optional-value option sitting next to another option is exactly where a parser can
/// swallow what follows it.
/// </para>
/// </summary>
public sealed class PreApprovedOptionTests
{
    [Fact]
    public void The_option_is_absent_when_it_is_not_passed()
    {
        TaskPublishCommand.Settings settings = Parse("28b19893");

        settings.PreApproved.IsSet.Should().BeFalse();
        PreApprovalInput.FromFlag(settings.PreApproved).Should().BeNull(
            "nothing passed is the ordinary publish, which the decider reads as off");
    }

    [Fact]
    public void The_bare_flag_still_means_on()
    {
        TaskPublishCommand.Settings settings = Parse("28b19893", "--pre-approved");

        settings.PreApproved.IsSet.Should().BeTrue();
        PreApprovalInput.FromFlag(settings.PreApproved).Should().Be(PreApprovalMode.On);
    }

    /// <summary>
    /// Asserted through the decider's own vetting rather than on <see cref="PreApprovalInput.FromFlag"/>'s
    /// raw result, because that result is the word as typed and deliberately NOT a member of the
    /// closed vocabulary — <c>after-human-review</c> is not
    /// <see cref="PreApprovalMode.AfterHumanReview"/> until <see cref="PreApprovalMode.FromInput"/>
    /// normalizes it, which is what keeps the refusal able to quote what somebody actually typed.
    /// This test found that out the hard way, which is the argument for running a procedure rather
    /// than reading it.
    /// </summary>
    [Fact]
    public void A_named_mode_is_bound_and_normalizes_to_the_vocabulary_at_the_decider()
    {
        TaskPublishCommand.Settings settings = Parse("28b19893", "--pre-approved", "after-human-review");

        PreApprovalInput.FromFlag(settings.PreApproved)!.Value.Should().Be("after-human-review");
        TaskDecider.VetPreApprovalMode(PreApprovalInput.FromFlag(settings.PreApproved), DomainId.New())
            .Should().Be(PreApprovalMode.AfterHumanReview);
    }

    [Fact]
    public void An_unrecognized_mode_reaches_the_decider_verbatim_rather_than_being_read_as_off()
    {
        TaskPublishCommand.Settings settings = Parse("28b19893", "--pre-approved", "sometimes");

        PreApprovalInput.FromFlag(settings.PreApproved)!.Value.Should().Be("sometimes",
            "the decider is what refuses it, with the whole vocabulary quoted");
    }

    /// <summary>
    /// The bare flag beside another option: the value slot stays empty and the neighbouring option
    /// is still bound, rather than being eaten as this one's value.
    /// </summary>
    [Fact]
    public void The_bare_flag_does_not_swallow_the_option_after_it()
    {
        TaskPublishCommand.Settings settings = Parse("28b19893", "--pre-approved", "--no-assign");

        PreApprovalInput.FromFlag(settings.PreApproved).Should().Be(PreApprovalMode.On);
        settings.NoAssign.Should().BeTrue();
    }

    [Fact]
    public void A_named_mode_beside_another_option_binds_both()
    {
        TaskPublishCommand.Settings settings = Parse(
            "28b19893", "--pre-approved", "after-human-review", "--no-assign");

        TaskDecider.VetPreApprovalMode(PreApprovalInput.FromFlag(settings.PreApproved), DomainId.New())
            .Should().Be(PreApprovalMode.AfterHumanReview);
        settings.NoAssign.Should().BeTrue();
    }

    /// <summary>
    /// Spectre's own parser and binder, driven through a command whose body only records what it
    /// was handed — so a binding failure shows up as a failing assertion here rather than as a
    /// surprise on somebody's terminal.
    /// </summary>
    private static TaskPublishCommand.Settings Parse(params string[] arguments)
    {
        CaptureCommand.Captured = null;
        CommandApp app = new();
        app.Configure(configurator => configurator
            .AddBranch("task", task => task.AddCommand<CaptureCommand>("publish")
                .WithDescription("capture")
                .WithExample("task", "publish", "28b19893")));

        int code = app.Run(["task", "publish", .. arguments]);

        code.Should().Be(0);
        return CaptureCommand.Captured
            ?? throw new InvalidOperationException("the command never ran, so nothing was bound");
    }

    private sealed class CaptureCommand : Command<TaskPublishCommand.Settings>
    {
        public static TaskPublishCommand.Settings? Captured { get; set; }

        protected override int Execute(
            CommandContext context, TaskPublishCommand.Settings settings, CancellationToken cancellationToken)
        {
            Captured = settings;
            return 0;
        }
    }
}
