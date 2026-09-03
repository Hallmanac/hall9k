using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskReviseCommand.NamesCurrentEpic"/> is what keeps a --file revision of a task
/// idempotent for its own rendered "epic:" key: the renderer always writes the task's current
/// membership back into task.md, so applying that same file back must not re-run the join gate
/// (adversarial review, cycle 4) — only an actual change in membership should.
/// </summary>
public sealed class TaskReviseCommandTests
{
    [Fact]
    public void Names_current_epic_by_full_id()
    {
        Guid epicId = DomainId.New();

        TaskReviseCommand.NamesCurrentEpic(epicId.ToString(), epicId).Should().BeTrue();
    }

    [Fact]
    public void Names_current_epic_by_the_rendered_short_fragment()
    {
        Guid epicId = DomainId.New();
        string shortId = DomainId.Short(epicId);

        TaskReviseCommand.NamesCurrentEpic(shortId, epicId).Should().BeTrue();
    }

    [Fact]
    public void Does_not_name_a_different_epic()
    {
        Guid currentEpicId = DomainId.New();
        Guid otherEpicId = DomainId.New();

        TaskReviseCommand.NamesCurrentEpic(otherEpicId.ToString(), currentEpicId).Should().BeFalse();
    }

    [Fact]
    public void Never_names_a_current_epic_when_the_task_has_none()
    {
        TaskReviseCommand.NamesCurrentEpic(DomainId.New().ToString(), currentEpicId: null).Should().BeFalse();
    }

    [Fact]
    public void A_dashes_only_fragment_never_matches_any_epic()
    {
        Guid currentEpicId = DomainId.New();

        TaskReviseCommand.NamesCurrentEpic("-", currentEpicId).Should().BeFalse();
    }

    [Fact]
    public void A_short_fragment_that_only_partially_overlaps_the_current_epic_is_not_a_match()
    {
        // adversarial review, cycle 1: a prefix/suffix substring match was too permissive — a
        // fragment aimed at a *different* epic could be swallowed as a no-op whenever it happened
        // to also overlap the current epic's id. Only the exact rendered short form (or the full
        // id) may short-circuit; anything shorter must fall through to the resolver instead.
        Guid currentEpicId = DomainId.New();
        string shortId = DomainId.Short(currentEpicId);
        string partialFragment = shortId[..3];

        TaskReviseCommand.NamesCurrentEpic(partialFragment, currentEpicId).Should().BeFalse();
    }

    /// <summary>
    /// Adversarial review (originating task 01a05cde-c9b3-7198-84ff-ab25cd6de898, routed here as a
    /// pre-existing out-of-scope defect): <c>Changed()</c> used to hand the raw model value
    /// straight to <c>AnsiConsole.MarkupLine</c>, so revising a task onto
    /// <see cref="AgentModel.PlatformFallback"/> — the platform's own worked example, brackets and
    /// all — crashed <c>h9k task revise</c> with an unhandled Spectre exception on a revision that
    /// had already committed successfully.
    /// </summary>
    [Fact]
    public void Changed_escapes_a_model_override_carrying_spectre_markup_syntax()
    {
        TaskRevised revised = new(
            DomainId.New(),
            Optional<string>.None,
            Optional<IReadOnlyList<string>>.None,
            Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None,
            Optional<TaskType>.None,
            Optional<AgentModel>.Of(AgentModel.FromInput(AgentModel.PlatformFallback)),
            DateTimeOffset.UtcNow,
            DomainId.New());

        string[] changed = [.. TaskReviseCommand.Changed(revised)];

        changed.Should().ContainSingle().Which.Should().Contain("[[1m]]",
            "the model id's own brackets are escaped rather than parsed as a Spectre style tag");

        Action act = () => RenderPlain($"[blue]Draft revised[/]: {string.Join(", ", changed)}.");
        act.Should().NotThrow("a raw model id used to crash this exact confirmation line");
    }

    /// <summary>
    /// A console that adds no escape sequences of its own, the same technique
    /// <c>StreamRendererTests</c> uses to check composed markup without spawning a real terminal.
    /// </summary>
    private static string RenderPlain(string markup)
    {
        StringWriter writer = new();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;

        console.MarkupLine(markup);

        return writer.ToString();
    }
}
