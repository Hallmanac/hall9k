using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What <c>h9k epic add</c> does in a project that belongs to a solution: one copy per member project
/// this node holds, all sharing one solution epic key and the title, and the members that get none
/// from this node. A project in a solution has no project-only epic, so this is the only epic it can add.
/// </summary>
internal sealed record SolutionEpicAddition(
    SolutionView View,
    IReadOnlyList<EpicAdded> Copies,
    IReadOnlyList<ProjectDetails> Targets,
    IReadOnlyList<ProjectDetails> Purging,
    IReadOnlyList<SolutionMember> NotHeld)
{
    /// <summary>
    /// The copies to write for an epic added in <paramref name="project"/>, or null when the project is
    /// in no open solution and the epic is an ordinary project epic. A member this node does not hold
    /// (not registered and joined here) is named in <see cref="NotHeld"/> and receives no copy; one
    /// scheduled for permanent deletion is named in <see cref="Purging"/> for the same reason.
    /// </summary>
    public static async Task<SolutionEpicAddition?> PlanAsync(
        IQuerySession session, ProjectDetails project, string title, Guid ownerId, CancellationToken cancellationToken)
    {
        if (project.Solution is not { Holds: true } own)
        {
            return null;
        }

        SolutionContext solutions = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionView view = solutions.ViewOf(own.Key) ?? new SolutionView(own, [project], []);
        if (!view.Membership.Holds)
        {
            throw new DomainConflictException(
                $"The solution '{view.Membership.Name}' is closed ({view.Membership.CloseReason}) on a project that has not "
                + "caught up with it yet, and a closed solution takes no new epic. Let replication settle, "
                + $"or take the project out of it: h9k solution unlink \"{view.Membership.Name}\" --project {project.Name}");
        }

        List<ProjectDetails> held = [.. solutions.HeldMembers(view)];
        if (held.All(member => member.Id != project.Id))
        {
            held.Add(project);
        }

        ProjectDetails[] purging = [.. held.Where(member => member.PurgeAt is not null)];
        ProjectDetails[] targets = [.. held.Except(purging)];
        SolutionMember[] notHeld =
        [
            .. view.Membership.Members.Where(member => !held.Any(
                holder => holder.ProjectKey is { } key && key.Equals(member.ProjectKey, StringComparison.OrdinalIgnoreCase))),
        ];

        IReadOnlyList<EpicAdded> copies = SolutionEpicDecider.Add(
            view.Membership.Key, title, [.. targets.Select(member => member.Id)], DomainId.New, DateTimeOffset.UtcNow, ownerId);
        return new SolutionEpicAddition(view, copies, targets, purging, notHeld);
    }

    /// <summary>
    /// Prints which members got a copy, the key the copies share, and any member left without one and why. Shared by
    /// <c>h9k epic add</c> and <c>h9k plan pursue --epic-title</c> so the two never describe the same epic differently.
    /// </summary>
    public void PrintMemberNotes(string indent)
    {
        string key = Copies[0].SolutionEpicKey ?? string.Empty;
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]{indent}A copy in each of {string.Join(", ", Targets.Select(member => member.Name))}; the copies share the key {key}.[/]");
        if (Purging.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]{indent}No copy in {string.Join(", ", Purging.Select(member => member.Name))}[/] [dim](scheduled for permanent deletion).[/]");
        }

        if (NotHeld.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]{indent}No copy in {string.Join(", ", NotHeld.Select(member => member.Name))}[/] [dim](not registered and joined on this node; this node makes copies only in the projects it holds).[/]");
        }
    }
}
