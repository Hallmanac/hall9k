using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Reading a run skill as an ordered plan (idea b9b09779, piece 5). DB-free: the document is the
/// whole input.
/// <para>
/// <see cref="ThreeStepsAndOneHumanStep"/> is the fake run skill the acceptance criteria name, and
/// it is shared with <c>LocalLaunchWalkerTests</c> so the parse and the walk are tested against the
/// same document rather than against two hand-built approximations of it.
/// </para>
/// </summary>
public sealed class RunSkillStepsTests
{
    /// <summary>
    /// Three command steps and one human step: a prerequisite check, a setup command, a launch
    /// command, and a credential nobody could determine. Deliberately written the way a discovery
    /// session actually writes one — fenced blocks, inline backticks, a citation in parentheses —
    /// rather than in a shape the parser would find convenient.
    /// </summary>
    public const string ThreeStepsAndOneHumanStep = """
        Run skill shape: full-text. This repository documents no launch procedure.

        ## Prerequisites

        - The .NET 10 SDK. Check it with `dotnet --version` (global.json).

        ## One-time setup

        1. Restore and build once:

           ```
           dotnet restore
           ```

        ## Launch

        - Start the API, which blocks (src/Api/Properties/launchSettings.json):

          ```
          dotnet run --project src/Api --port 5000
          ```

        ## How to know it is up

        The log prints `Now listening on:` and the health endpoint answers 200.

        ## Address or entry point

        http://localhost:5000/swagger

        ## Human steps

        - An API key for the payments sandbox, which nothing in this repository carries. Put it in
          `src/Api/appsettings.Development.json` under `Payments:ApiKey` before launching.
        """;

    [Fact]
    public void The_plan_is_four_steps_with_the_human_one_first()
    {
        RunSkillPlan plan = RunSkillSteps.Parse(ThreeStepsAndOneHumanStep);

        plan.Steps.Should().HaveCount(4);
        plan.Steps.Select(step => step.Number).Should().Equal(1, 2, 3, 4);
        plan.Steps[0].Kind.Should().Be(RunSkillStepKind.Human);
        plan.Steps[0].Section.Should().Be(RunSkillDocument.HumanStepsHeading);
        plan.Steps.Skip(1).Should().OnlyContain(step => step.Kind == RunSkillStepKind.Command);
        plan.Steps.Select(step => step.Section)
            .Should().Equal(
                RunSkillDocument.HumanStepsHeading,
                RunSkillDocument.PrerequisitesHeading,
                RunSkillDocument.OneTimeSetupHeading,
                RunSkillDocument.LaunchHeading);
    }

    [Fact]
    public void A_commands_own_text_is_lifted_out_of_its_prose()
    {
        RunSkillPlan plan = RunSkillSteps.Parse(ThreeStepsAndOneHumanStep);

        plan.Steps[1].Command.Should().Be("dotnet --version");
        plan.Steps[2].Command.Should().Be("dotnet restore");
        plan.Steps[3].Command.Should().Be("dotnet run --project src/Api --port 5000");

        // The fenced block is not repeated in the prose the reviewer is shown, and the prose the
        // step came from survives.
        plan.Steps[2].Text.Should().Contain("Restore and build once");
        plan.Steps[2].Text.Should().NotContain("dotnet restore");
    }

    [Fact]
    public void The_two_readout_sections_come_back_whole()
    {
        RunSkillPlan plan = RunSkillSteps.Parse(ThreeStepsAndOneHumanStep);

        plan.AddressOrEntryPoint.Should().Be("http://localhost:5000/swagger");
        plan.HowToKnowItIsUp.Should().Contain("Now listening on:");
    }

    [Fact]
    public void The_launch_steps_are_the_ones_under_the_launch_heading()
    {
        RunSkillPlan plan = RunSkillSteps.Parse(ThreeStepsAndOneHumanStep);

        plan.LaunchSteps.Should().ContainSingle()
            .Which.Command.Should().Be("dotnet run --project src/Api --port 5000");
        plan.HumanSteps.Should().ContainSingle()
            .Which.Text.Should().Contain("API key for the payments sandbox");
    }

    /// <summary>
    /// A human-steps entry that happens to quote a command is still a human step. The section
    /// exists because the composing session could not determine something about it, so running the
    /// quoted half would be acting on the half the document admits it does not have.
    /// </summary>
    [Fact]
    public void A_human_step_quoting_a_command_is_still_a_human_step()
    {
        RunSkillPlan plan = RunSkillSteps.Parse("""
            ## Launch

            - `make dev`

            ## Human steps

            - Log in first with `gh auth login`; the account has to be on the org.
            """);

        plan.Steps[0].Kind.Should().Be(RunSkillStepKind.Human);
        plan.Steps[0].Command.Should().BeEmpty();
    }

    /// <summary>
    /// A backticked token with no whitespace is a name the prose is pointing at, never a command.
    /// Getting this wrong runs a filename on somebody's machine.
    /// </summary>
    [Fact]
    public void A_backticked_name_is_not_a_command()
    {
        RunSkillPlan plan = RunSkillSteps.Parse("""
            ## Prerequisites

            - Docker, for the containers `docker-compose.yml` declares.
            """);

        plan.Steps.Should().ContainSingle().Which.Kind.Should().Be(RunSkillStepKind.Human);
    }

    [Theory]
    [InlineData("None.")]
    [InlineData("Nothing in this repository states any.")]
    [InlineData("Unknown. Nothing in this repository states any.")]
    [InlineData("")]
    public void A_section_that_says_it_has_nothing_yields_no_step(string body)
    {
        RunSkillSteps.Parse($"## Prerequisites\n\n{body}\n").Steps.Should().BeEmpty();
    }

    /// <summary>
    /// The platform's own none-discoverable document. Its first five sections all say there is
    /// nothing, so none of them produces a step, and what is left is entirely human steps: the
    /// "somebody who knows how this runs has to say so" instruction and the survey's own list of
    /// where it looked, which the composer writes under that same heading.
    /// <para>
    /// No launch ever reaches this document — <see cref="ProjectRunSkillReader"/> reads that shape
    /// as no run skill at all, which is the refusal <c>h9k task run-local</c> gives — and this pins
    /// that even so the parse invents no launch procedure out of it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_none_discoverable_document_holds_nothing_to_run()
    {
        RunSkillPlan plan = RunSkillSteps.Parse(
            RunSkillDocument.ComposeNoneDiscoverable(["README.md", "docs/"]));

        plan.Steps.Should().OnlyContain(step => step.Kind == RunSkillStepKind.Human);
        plan.LaunchSteps.Should().BeEmpty();
        plan.AddressOrEntryPoint.Should().BeEmpty();
        plan.HowToKnowItIsUp.Should().BeEmpty();
    }

    /// <summary>
    /// Several commands in one list item become several steps, so nothing is dropped and a pause
    /// between them has a step number to resume from.
    /// </summary>
    [Fact]
    public void A_fenced_block_of_several_commands_becomes_several_steps()
    {
        RunSkillPlan plan = RunSkillSteps.Parse("""
            ## One-time setup

            1. Bring the database up and seed it:

               ```
               # the compose stack first
               docker compose up -d
               dotnet run --project tools/Seed
               ```
            """);

        plan.Steps.Select(step => step.Command)
            .Should().Equal("docker compose up -d", "dotnet run --project tools/Seed");
    }

    /// <summary>
    /// The one shape in which a human step follows a launch step: a prose-only item inside the
    /// Launch section, between two commands. Every other human step is hoisted to the front, so
    /// without this one nothing could ever pause with the product already up.
    /// </summary>
    [Fact]
    public void A_prose_only_item_inside_launch_is_a_human_step_between_two_launch_commands()
    {
        RunSkillPlan plan = RunSkillSteps.Parse("""
            ## Launch

            - Start the API: `dotnet run --project src/Api`
            - Accept the development certificate prompt when the browser raises it.
            - Start the worker: `dotnet run --project src/Worker`
            """);

        plan.Steps.Select(step => step.Kind)
            .Should().Equal(RunSkillStepKind.Command, RunSkillStepKind.Human, RunSkillStepKind.Command);
        plan.Steps.Should().OnlyContain(step => step.Section == RunSkillDocument.LaunchHeading);
        plan.LaunchSteps.Select(step => step.Number).Should().Equal(1, 3);
    }

    [Fact]
    public void A_document_with_no_recognised_section_parses_as_an_empty_plan()
    {
        RunSkillPlan plan = RunSkillSteps.Parse("Some prose nobody wrote headings for.");

        plan.Steps.Should().BeEmpty();
        plan.AddressOrEntryPoint.Should().BeEmpty();
        plan.HowToKnowItIsUp.Should().BeEmpty();
    }
}
