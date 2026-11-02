using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Daemon.Plan;

/// <summary>
/// Refuses a refinement revision that copies another member project's text into the draft. A refinement session is told what
/// the solution epic's other member tasks are so it can set blocked-by edges across projects, and that material is fenced
/// for study, not for quotation: a plan stays project-level, so what another member wrote must not travel into this
/// project's task by way of a session's revision. The check reads the revision's objective, criteria and context (a
/// blocked-by reason may name a project or a task, which is the point of the material) and compares after whitespace and
/// case normalisation. It names a member task only by its short id, so the refusal itself never repeats the copied text.
/// </summary>
public static class MemberTextCopyGuard
{
    /// <summary>
    /// The refusal reason for <paramref name="revisionText"/>, or null when it copies nothing from
    /// <paramref name="memberTasks"/>. A member task's objective counts when it appears whole in a field; a member project's
    /// name counts when it appears as a whole word (so a short name does not match inside an unrelated word).
    /// </summary>
    public static string? Refusal(string revisionText, IReadOnlyList<SolutionMemberTask>? memberTasks)
    {
        if (memberTasks is not { Count: > 0 })
        {
            return null;
        }

        Frontmatter revision = FrontmatterYaml.Parse(revisionText);
        string[] fields =
        [
            .. new[] { revision.Scalar("objective"), revision.Body, revision.Scalar("context") }
                .Concat(revision.List("criteria"))
                .Select(Normalise)
                .Where(field => field.Length > 0),
        ];

        foreach (SolutionMemberTask task in memberTasks)
        {
            string objective = Normalise(task.Objective);
            string project = Normalise(task.ProjectName);
            bool copied = fields.Any(field =>
                (objective.Length > 0 && field.Contains(objective, StringComparison.Ordinal))
                || (project.Length > 0 && ContainsWord(field, project)));
            if (copied)
            {
                return $"The revision copies text of another member project's task {DomainId.Short(task.Id)} into the draft, "
                    + "so it was not applied. Name that task in blocked-by instead of repeating what it says.";
            }
        }

        return null;
    }

    private static bool ContainsWord(string field, string word)
    {
        int index = field.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            int end = index + word.Length;
            bool startsWord = index == 0 || !char.IsLetterOrDigit(field[index - 1]);
            bool endsWord = end == field.Length || !char.IsLetterOrDigit(field[end]);
            if (startsWord && endsWord)
            {
                return true;
            }

            index = field.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        bool pendingSpace = false;
        foreach (char character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
