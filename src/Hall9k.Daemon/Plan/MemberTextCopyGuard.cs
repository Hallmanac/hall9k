using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Daemon.Plan;

/// <summary>
/// Refuses a refinement revision that copies another member project's task objective into the draft. A refinement session is
/// told what the solution epic's other member tasks are so it can set blocked-by edges across projects, and that material is
/// fenced for study, not for quotation: a plan stays project-level, so what another member wrote must not travel into this
/// project's task by way of a session's revision. The check reads the revision's objective, criteria and context (a
/// blocked-by reason may name a project or a task, which is the point of the material) and compares after whitespace and
/// case normalisation. It names a member task only by its short id, so the refusal itself never repeats the copied text.
/// A member project's name is deliberately not checked: it is often an ordinary word ("api", "web") that a revision, or the
/// draft it started from, uses for its own reasons, so refusing on it would park a plan that a rerun cannot fix. The prompt
/// still asks the session to leave project names out.
/// </summary>
public static class MemberTextCopyGuard
{
    /// <summary>
    /// The shortest normalised objective that counts as copied text. A shorter one ("Add logging") is a phrase an unrelated
    /// revision can contain without having read the member's task, so it is not enough to refuse on.
    /// </summary>
    internal const int MinimumCopiedLength = 24;

    /// <summary>
    /// The refusal reason for <paramref name="revisionText"/>, or null when it copies nothing from
    /// <paramref name="memberTasks"/>. A member task's objective counts when it appears whole in a field, bounded by
    /// non-letters or non-digits on both sides (so it does not match inside a longer word), and is at least
    /// <see cref="MinimumCopiedLength"/> characters long once normalised.
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
            bool copied = objective.Length >= MinimumCopiedLength
                && fields.Any(field => ContainsPhrase(field, objective));
            if (copied)
            {
                return $"The revision copies text of another member project's task {DomainId.Short(task.Id)} into the draft, "
                    + "so it was not applied. Name that task in blocked-by instead of repeating what it says.";
            }
        }

        return null;
    }

    private static bool ContainsPhrase(string field, string phrase)
    {
        int index = field.IndexOf(phrase, StringComparison.Ordinal);
        while (index >= 0)
        {
            int end = index + phrase.Length;
            bool startsPhrase = index == 0 || !char.IsLetterOrDigit(field[index - 1]);
            bool endsPhrase = end == field.Length || !char.IsLetterOrDigit(field[end]);
            if (startsPhrase && endsPhrase)
            {
                return true;
            }

            index = field.IndexOf(phrase, index + 1, StringComparison.Ordinal);
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
