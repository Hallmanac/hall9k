using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

public sealed record TaskFileContent(
    string? Project,
    string? Type,
    string? Objective,
    IReadOnlyList<string> Criteria,
    string? AgentContext,
    string? Model);

/// <summary>
/// Parses the h9k task file format: a minimal frontmatter block (project, type, objective,
/// criteria as "- " items, optional model) followed by a markdown body that becomes the
/// agent context. Deliberately not YAML, since five known keys don't warrant a dependency.
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
                + "and an optional model).");
        }

        string? project = null;
        string? type = null;
        string? objective = null;
        string? model = null;
        List<string> criteria = [];
        bool inCriteria = false;
        int bodyStart = lines.Length;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim() == "---")
            {
                bodyStart = i + 1;
                break;
            }

            if (inCriteria && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                criteria.Add(line.TrimStart()[2..].Trim());
                continue;
            }

            inCriteria = false;
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
                case "criteria":
                    inCriteria = true;
                    break;
            }
        }

        string body = string.Join('\n', lines.Skip(bodyStart)).Trim();
        return new TaskFileContent(project, type, objective, criteria, body.IsBlank() ? null : body, model);
    }
}
