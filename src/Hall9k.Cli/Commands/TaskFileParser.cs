using Hall9k.Connectors.Text;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

public sealed record TaskFileContent(
    string? Project,
    string? Type,
    string? Objective,
    IReadOnlyList<string> Criteria,
    string? AgentContext,
    string? Model,
    IReadOnlyList<string> BlockedBy,
    string? Epic,
    /// <summary>
    /// The blocker this task declares itself stacked on, or null when it declares none (task: a
    /// stacked pull-request edge exists as an explicit opt-in dependency). Single-valued: a branch
    /// sits on top of exactly one other branch.
    /// </summary>
    string? StackedOn = null);

/// <summary>
/// Parses the h9k task file format: a <c>---</c> frontmatter block (project, type, objective,
/// criteria as "- " items, optional model, optional blocked-by as "- " items, optional stacked-on,
/// optional epic) followed by a markdown body that becomes the agent context.
/// <para>
/// The reading is <see cref="FrontmatterYaml"/>'s: the forgiving line-oriented document grammar the
/// format has always had, with every value read as a real YAML scalar — so a double-quoted
/// objective or criterion is stored without its quote characters, and a <c>|</c> block scalar
/// arrives as the multi-line text it denotes. The task record on a published issue reads through
/// exactly the same parser, which is what makes it true that the record is "the same shape --file
/// accepts" rather than a second shape that happens to look similar.
/// </para>
/// <para>
/// <c>context</c> is accepted as a frontmatter key as well as a markdown body, because the record
/// carries the agent context as a block scalar (a fenced YAML block has nowhere to put a markdown
/// body). The body wins when a file somehow carries both: it is the form a human types.
/// </para>
/// </summary>
public static class TaskFileParser
{
    public static TaskFileContent Parse(string content)
    {
        string[] lines = (content ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw new DomainValidationException(
                "Task files start with a '---' frontmatter block (project, type, objective, criteria, "
                + "an optional model, and optional blocked-by dependencies).");
        }

        Frontmatter parsed = FrontmatterYaml.Parse(content);
        return new TaskFileContent(
            parsed.Scalar("project"),
            parsed.Scalar("type"),
            parsed.Scalar("objective"),
            [.. parsed.List("criteria").Where(criterion => criterion.IsNotBlank())],
            parsed.Body ?? parsed.Scalar("context"),
            parsed.Scalar("model"),
            [.. parsed.ListOrInline("blocked-by").Concat(parsed.ListOrInline("blockedby"))],
            parsed.Scalar("epic"),
            parsed.Scalar("stacked-on") ?? parsed.Scalar("stackedon"));
    }
}
