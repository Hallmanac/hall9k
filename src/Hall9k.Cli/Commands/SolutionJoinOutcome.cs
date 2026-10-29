using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>The one state each member of a solution join ends in.</summary>
internal enum SolutionJoinState
{
    RegisteredAndJoined,
    Joined,
    AlreadyJoined,
    NotFinished,
}

/// <summary>
/// How one member's join ended. <see cref="HandCommand"/> is what finishes a member that did not
/// finish, and never carries the secret itself: it names the bundle's secret by a placeholder.
/// </summary>
internal sealed record SolutionJoinOutcome(
    SolutionMember Member, SolutionJoinState State, string? Reason = null, string? HandCommand = null)
{
    public static SolutionJoinOutcome RegisteredAndJoined(SolutionMember member) => new(member, SolutionJoinState.RegisteredAndJoined);

    public static SolutionJoinOutcome Joined(SolutionMember member) => new(member, SolutionJoinState.Joined);

    public static SolutionJoinOutcome AlreadyJoined(SolutionMember member) => new(member, SolutionJoinState.AlreadyJoined);

    public static SolutionJoinOutcome NotFinished(SolutionMember member, string reason, string handCommand) =>
        new(member, SolutionJoinState.NotFinished, reason, handCommand);
}

/// <summary>The hand commands that finish a member <c>h9k solution join</c> could not, each naming the bundle's secret as a placeholder.</summary>
internal static class SolutionJoinHandCommands
{
    public static string Add(SolutionMember member, string? name = null) =>
        $"h9k project add --name {Quote(name ?? member.Name)} --repo-url {Quote(member.RepositoryUrl.ToString())} --invite <secret>";

    public static string Join(string projectName) => $"h9k project join {Quote(projectName)} --invite <secret>";

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
}

/// <summary>Runs a plan one member at a time, so a member that does not finish never stops the ones after it.</summary>
internal static class SolutionJoinRunner
{
    /// <summary>
    /// <paramref name="execute"/> does the work of a <see cref="SolutionJoinAction.Register"/> or
    /// <see cref="SolutionJoinAction.Join"/> step and returns how it ended; a member that needs
    /// nothing done is settled here without calling it. Anything it throws is that member's own
    /// failure rather than the batch's.
    /// </summary>
    public static async Task<IReadOnlyList<SolutionJoinOutcome>> RunAsync(
        IReadOnlyList<SolutionJoinStep> plan,
        Func<SolutionJoinStep, CancellationToken, Task<SolutionJoinOutcome>> execute,
        CancellationToken cancellationToken)
    {
        List<SolutionJoinOutcome> outcomes = [];
        foreach (SolutionJoinStep step in plan)
        {
            outcomes.Add(await OutcomeAsync(step, execute, cancellationToken));
        }

        return outcomes;
    }

    private static async Task<SolutionJoinOutcome> OutcomeAsync(
        SolutionJoinStep step,
        Func<SolutionJoinStep, CancellationToken, Task<SolutionJoinOutcome>> execute,
        CancellationToken cancellationToken)
    {
        SolutionMember member = step.Entry.Member;
        switch (step.Action)
        {
            case SolutionJoinAction.AlreadyJoined:
                return SolutionJoinOutcome.AlreadyJoined(member);
            case SolutionJoinAction.NameCollision:
                return SolutionJoinOutcome.NotFinished(
                    member,
                    $"this node already has a project named '{step.Local?.Name}'"
                    + (step.Local is { IsArchived: true } ? " (archived)" : string.Empty)
                    + ", so registering this member under its name would collide with it",
                    SolutionJoinHandCommands.Add(member, name: "<another-name>"));
        }

        try
        {
            return await execute(step, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SolutionJoinOutcome.NotFinished(
                member,
                exception.Message,
                step.Action == SolutionJoinAction.Join && step.Local is { } local
                    ? SolutionJoinHandCommands.Join(local.Name)
                    : SolutionJoinHandCommands.Add(member));
        }
    }
}

/// <summary>What <c>h9k solution join</c> prints and returns once every member has been tried.</summary>
internal static class SolutionJoinReport
{
    public static int ExitCode(IReadOnlyList<SolutionJoinOutcome> outcomes) =>
        outcomes.Any(outcome => outcome.State == SolutionJoinState.NotFinished) ? ExitCodes.Error : ExitCodes.Ok;

    public static string Label(SolutionJoinState state) =>
        state switch
        {
            SolutionJoinState.RegisteredAndJoined => "registered and joined",
            SolutionJoinState.Joined => "joined",
            SolutionJoinState.AlreadyJoined => "already joined",
            _ => "not finished",
        };

    /// <summary>The report as console markup, one line each: a member per line (a member that did not finish adds its reason and its hand command), then what happens next.</summary>
    public static IReadOnlyList<string> Lines(string solutionName, IReadOnlyList<SolutionJoinOutcome> outcomes)
    {
        List<string> lines = [$"Solution '{solutionName.EscapeMarkup()}', {outcomes.Count} {(outcomes.Count == 1 ? "member" : "members")}:"];
        foreach (SolutionJoinOutcome outcome in outcomes)
        {
            string color = outcome.State switch
            {
                SolutionJoinState.NotFinished => "yellow",
                SolutionJoinState.AlreadyJoined => "dim",
                _ => "green",
            };
            lines.Add(
                $"  [{color}]{Label(outcome.State)}[/]: {outcome.Member.Name.EscapeMarkup()} "
                + $"[dim]({outcome.Member.RepositoryUrl.ToString().EscapeMarkup()})[/]");

            if (outcome.State == SolutionJoinState.NotFinished)
            {
                lines.Add($"    why: {outcome.Reason?.EscapeMarkup()}");
                lines.Add($"    finish it with: {outcome.HandCommand?.EscapeMarkup()}");
            }
        }

        int unfinished = outcomes.Count(outcome => outcome.State == SolutionJoinState.NotFinished);
        if (unfinished > 0)
        {
            lines.Add(
                $"[yellow]{unfinished} of {outcomes.Count} did not finish.[/] <secret> in a hand command is that member's secret from the bundle.");
        }

        lines.Add(
            "[dim]Each join is vouched in by the minting node's daemon on its next sweep; nothing here waits for it, "
            + "so a member shows as joined there once that daemon is running and has swept. Each member still needs "
            + "push access on its own repository.[/]");
        return lines;
    }

    public static void Write(string solutionName, IReadOnlyList<SolutionJoinOutcome> outcomes)
    {
        foreach (string line in Lines(solutionName, outcomes))
        {
            AnsiConsole.MarkupLine(line);
        }
    }
}
