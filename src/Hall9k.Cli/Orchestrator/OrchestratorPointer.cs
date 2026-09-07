using Spectre.Console;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The closing line <c>h9k install</c> and <c>h9k project add</c> both print (task: an operator
/// starts a lean node or project orchestrator window), so a user reaching for a plain interactive
/// session is redirected to the lean one instead. It also tells an agent installing or
/// registering on a human's behalf to hand the human a fresh terminal rather than treat this
/// session as the orchestrator itself. Worded for both readers: a human reading a terminal, and
/// an agent that just ran the install or registration for them.
/// </summary>
public static class OrchestratorPointer
{
    public static string ForNode() =>
        "[dim]An orchestrator exists for this node: run the orchestrator-recipe-generator skill "
        + "once from ~/.hall9k, then start a fresh session with the launch line h9k orchestrator "
        + "node prints. If an agent ran this install on your behalf, that agent should hand you "
        + "this line rather than continue as the orchestrator itself. This session is not the "
        + "lean window.[/]";

    public static string ForProject(string projectName)
    {
        string escaped = projectName.EscapeMarkup();
        return $"[dim]An orchestrator exists for '{escaped}': run the orchestrator-recipe-generator "
            + $"skill once from its home, then start a fresh session with the launch line "
            + $"h9k orchestrator project {escaped} prints. If an agent registered this project on "
            + "your behalf, that agent should hand you this line rather than continue as the "
            + "orchestrator itself. This session is not the lean window.[/]";
    }
}
