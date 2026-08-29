using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Ids;
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
}
