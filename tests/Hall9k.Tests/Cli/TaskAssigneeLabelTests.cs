using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Task 21c8f2f3: the assignee a board row shows for an owner this install has no local record
/// of resolves to the project's own member label (<see cref="TaskStatusComposer"/>) rather than
/// the fingerprint's own bare short prefix — the same resolver <see cref="MemberLabelResolverTests"/>
/// (b7d8222e) already proves standalone; this suite proves the board actually threads it through.
/// </summary>
public sealed class TaskAssigneeLabelTests
{
    private const string Fingerprint = "c8f5c85900da1234567890abcdef1234567890abcdef1234567890abcdef12";

    [Fact]
    public void A_foreign_assignee_with_a_display_name_shows_that_name()
    {
        Guid projectId = DomainId.New();
        Guid foreignNodeId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Published, projectId: projectId);
        task.AssignedOwnerId = DomainId.New();
        task.AssignedOwnerFingerprint = Fingerprint;
        ProjectMemberLabels labels = new()
        {
            Id = projectId,
            Labels = [new ProjectMemberLabel(Fingerprint, [foreignNodeId], DisplayName.Parse("Windows"), "windows-login")],
        };

        TaskStatusContext context = StatusFixtures.Context() with
        {
            ProjectMemberLabelsById = new Dictionary<Guid, ProjectMemberLabels> { [projectId] = labels },
        };
        TaskStatusRow row = TaskStatusComposer.Compose(task, context, StatusFixtures.Now);

        row.Assignee.Should().Be("Windows");
    }

    [Fact]
    public void A_foreign_assignee_with_neither_a_name_nor_a_login_shows_the_short_fingerprint()
    {
        Guid projectId = DomainId.New();
        Guid foreignNodeId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Published, projectId: projectId);
        task.AssignedOwnerId = DomainId.New();
        task.AssignedOwnerFingerprint = Fingerprint;
        ProjectMemberLabels labels = new()
        {
            Id = projectId,
            Labels = [new ProjectMemberLabel(Fingerprint, [foreignNodeId], DisplayName.None, null)],
        };

        TaskStatusContext context = StatusFixtures.Context() with
        {
            ProjectMemberLabelsById = new Dictionary<Guid, ProjectMemberLabels> { [projectId] = labels },
        };
        TaskStatusRow row = TaskStatusComposer.Compose(task, context, StatusFixtures.Now);

        row.Assignee.Should().Be(Fingerprint[..12]);
    }

    /// <summary>This machine's own owner keeps the local name it resolves to today — the
    /// resolver-fallback change never touches the branch that already matches locally.</summary>
    [Fact]
    public void This_machines_own_owner_keeps_the_local_name_rather_than_a_project_label()
    {
        Guid projectId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Published, projectId: projectId);
        task.AssignedOwnerId = DomainId.New();
        task.AssignedOwnerFingerprint = Fingerprint;
        ProjectMemberLabels labels = new()
        {
            Id = projectId,
            Labels = [new ProjectMemberLabel(Fingerprint, [DomainId.New()], DisplayName.Parse("Not Brian"), null)],
        };

        TaskStatusContext context = StatusFixtures.Context() with
        {
            OwnersByFingerprint = new Dictionary<string, string> { [Fingerprint] = "Brian" },
            ProjectMemberLabelsById = new Dictionary<Guid, ProjectMemberLabels> { [projectId] = labels },
        };
        TaskStatusRow row = TaskStatusComposer.Compose(task, context, StatusFixtures.Now);

        row.Assignee.Should().Be("Brian");
    }
}
