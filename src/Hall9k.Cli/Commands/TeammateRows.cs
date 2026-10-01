namespace Hall9k.Cli.Commands;

/// <summary>
/// The one place a surface that shows a person their own work drops another owner's rows, and says
/// so. <see cref="TaskStatusComposer"/> composes a teammate's task into its own group
/// (<see cref="AttentionBucket.Teammates"/>) so no count of any other group includes it; what a
/// surface does with that group is this seam: hide it by default, and print once how many rows it is
/// holding back and the flag that shows them. A teammate's row is never worded as the viewer's, so
/// showing them is safe, but a pane that lists them beside the viewer's own would bury what is theirs.
/// </summary>
internal static class TeammateRows
{
    /// <summary>The flag every browse surface takes to bring teammates' rows back, for the --help text.</summary>
    public const string EveryoneDescription =
        "Also show other owners' tasks. By default a board shows only your own work: the tasks this "
        + "node's owner root may act on, or has asked to take. A teammate's task, and one whose owner "
        + "root this node cannot resolve, is held back, counted in no group, and summarised in one line "
        + "saying how many rows were hidden. With this flag they appear as a Teammates group of their "
        + "own, naming each owner, with their state and objective but never a cause, phase or next step "
        + "written for the owner.";

    /// <summary>
    /// <paramref name="rows"/> without the teammates' rows unless <paramref name="everyone"/>, and how
    /// many it held back (zero when they were asked for, since none were).
    /// </summary>
    public static (IReadOnlyList<TaskStatusRow> Visible, int Hidden) Apply(IReadOnlyList<TaskStatusRow> rows, bool everyone)
    {
        if (everyone)
        {
            return (rows, 0);
        }

        List<TaskStatusRow> visible = [.. rows.Where(row => !row.IsTeammates)];
        return (visible, rows.Count - visible.Count);
    }

    /// <summary>
    /// The sentence that says how many rows were held back and the exact flag that shows them,
    /// printed once per surface and never when nothing was hidden.
    /// </summary>
    public static string HiddenNote(int hidden, string command) =>
        hidden switch
        {
            0 => string.Empty,
            1 => $"1 task belonging to a teammate is not shown: {command} --everyone shows it",
            _ => $"{hidden} tasks belonging to teammates are not shown: {command} --everyone shows them",
        };
}
