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
    string? Epic);

/// <summary>
/// Parses the h9k task file format: a minimal frontmatter block (project, type, objective,
/// criteria as "- " items, optional model, optional blocked-by as "- " items, optional epic)
/// followed by a markdown body that becomes the agent context. Deliberately not YAML, since a
/// handful of known keys don't warrant a dependency.
/// </summary>
public static class TaskFileParser
{
    public static TaskFileContent Parse(string content)
    {
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw new DomainValidationException(
                "Task files start with a '---' frontmatter block (project, type, objective, criteria, "
                + "an optional model, and optional blocked-by dependencies).");
        }

        string? project = null;
        string? type = null;
        string? objective = null;
        string? model = null;
        string? epic = null;
        List<string> criteria = [];
        List<string> blockedBy = [];
        List<string>? list = null;
        int bodyStart = lines.Length;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim() == "---")
            {
                bodyStart = i + 1;
                break;
            }

            if (list is not null && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                list.Add(line.TrimStart()[2..].Trim());
                continue;
            }

            list = null;
            int separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string key = line[..separator].Trim().ToLowerInvariant();
            string value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "project":
                    project = value;
                    break;
                case "type":
                    type = value;
                    break;
                case "objective":
                    objective = value;
                    break;
                case "model":
                    model = value;
                    break;
                case "epic":
                    epic = value;
                    break;
                case "criteria":
                    list = criteria;
                    break;
                case "blocked-by":
                case "blockedby":
                    list = blockedBy;
                    // An inline "blocked-by: a, b" is as natural as the list form; take both.
                    blockedBy.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
            }
        }

        string body = string.Join('\n', lines.Skip(bodyStart)).Trim();
        return new TaskFileContent(
            project, type, objective, criteria, body.IsBlank() ? null : body, model, blockedBy, epic);
    }
}
