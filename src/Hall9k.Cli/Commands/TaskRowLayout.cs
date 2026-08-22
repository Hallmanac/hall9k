using Spectre.Console;
using Spectre.Console.Rendering;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One fixed-width column of a task surface: the header it is shown under, and one cell per row.
/// </summary>
/// <param name="Header">The word above the column, shown only where the surface asks for headers.</param>
/// <param name="Cells">One cell of markup per row, in the rows' own order.</param>
internal sealed record TaskColumn(string Header, IReadOnlyList<string> Cells);

/// <summary>
/// How every task surface is laid out: the fixed columns, one flexible objective column, and each
/// row's detail lines running underneath at full console width.
/// <para>
/// Laid out by hand rather than as a Spectre table because of those detail lines. A table has no
/// column span, so a phase or an attention cause would have to live inside the objective column's
/// share of the width and wrap over three lines — and the three surfaces (Decisions Log #66) are
/// only worth anything if the lifecycle word, the phase and the ask are all readable. That is why
/// the browse surfaces are borderless too: the border bought a frame and cost the second line.
/// </para>
/// <para>
/// Shared so <c>h9k status</c>, <c>h9k task list</c> and <c>h9k project show</c> cannot drift into
/// laying the same row out differently. Built apart from the query so every surface can be
/// rendered and measured without a database (TaskTableLayoutTests).
/// </para>
/// </summary>
internal static class TaskRowLayout
{
    /// <summary>What the detail lines are indented by, so they read as belonging to the row above.</summary>
    private const string DetailIndent = "    ";

    /// <summary>
    /// The rows, laid out. Columns are as wide as their widest cell (and their header, where one
    /// is shown), the objective takes whatever width is left, and each row's detail lines follow
    /// it indented.
    /// </summary>
    /// <param name="rows">The rows to render; the objective column is composed from them.</param>
    /// <param name="before">The fixed columns left of the objective.</param>
    /// <param name="after">The fixed columns right of the objective.</param>
    /// <param name="details">
    /// Each row's detail lines, in the rows' order. The attention pane passes every line a row
    /// has; the browse surfaces pass the first one only, which keeps a long list scannable.
    /// </param>
    /// <param name="consoleWidth">The width the surface will be rendered at.</param>
    /// <param name="headers">Whether to print (and pay width for) the column headers.</param>
    public static IRenderable Render(
        IReadOnlyList<TaskStatusRow> rows,
        IReadOnlyList<TaskColumn> before,
        IReadOnlyList<TaskColumn> after,
        IReadOnlyList<IReadOnlyList<string>> details,
        int consoleWidth,
        bool headers)
    {
        if (rows.Count == 0)
        {
            return new Rows([]);
        }

        TaskColumn[] columns = [.. before, .. after];
        // A hidden header costs nothing: unlike a table, nothing here sizes a column to a word it
        // is not going to print.
        IReadOnlyList<string>[] measured =
            [.. columns.Select(column => headers ? (string[])[column.Header, .. column.Cells] : [.. column.Cells])];
        int[] widths = [.. measured.Select(column => column.Max(TaskStatusRow.CellWidth))];
        int objective = TaskStatusRow.ObjectiveWidth(consoleWidth, bordered: false, measured);

        List<IRenderable> lines = [];
        if (headers)
        {
            lines.Add(new Markup(Line(
                [.. columns.Select(column => $"[dim]{column.Header}[/]")],
                "[dim]Objective[/]", widths, before.Count, objective)));
        }

        for (int index = 0; index < rows.Count; index++)
        {
            lines.Add(new Markup(Line(
                [.. columns.Select(column => column.Cells[index])],
                rows[index].ObjectiveMarkup(objective), widths, before.Count, objective)));

            foreach (string detail in details[index])
            {
                lines.Add(new Markup($"{DetailIndent}[dim]↳[/] {detail}").Overflow(Overflow.Ellipsis));
            }
        }

        return new Rows(lines);
    }

    /// <summary>
    /// One line's cells, padded to their columns with the objective in its place among them —
    /// exactly as a borderless table would, and with the same two-space gap between columns.
    /// </summary>
    private static string Line(
        IReadOnlyList<string> cells, string objective, int[] widths, int split, int objectiveWidth) =>
        string.Join("  ",
        [
            .. cells.Take(split).Select((cell, column) => Pad(cell, widths[column])),
            Pad(objective, objectiveWidth),
            .. cells.Skip(split).Select((cell, column) => Pad(cell, widths[split + column])),
        ]).TrimEnd();

    /// <summary>
    /// A markup cell padded to its column, measured in the terminal cells it will actually
    /// render to rather than in the characters its markup happens to contain.
    /// </summary>
    private static string Pad(string markup, int width) =>
        markup + new string(' ', Math.Max(0, width - TaskStatusRow.CellWidth(markup)));
}
