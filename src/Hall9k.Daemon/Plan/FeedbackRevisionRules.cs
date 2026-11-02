using Hall9k.Connectors.Text;

namespace Hall9k.Daemon.Plan;

/// <summary>
/// What a feedback step's refinement session may put in its revision: the objective, the criteria, and the context (the body),
/// and nothing else. A revision file that names any other key, whether a dependency, a stack link, a type, an epic, a model, or a
/// key this rule has never heard of, is refused whole, and the step parks for a person, because those change what the task is and
/// where it sits, which no session decides on its own. "Names" is meant literally: a key written with no value, or set to the
/// value the task already has, is still a key the session was told not to write.
/// </summary>
public static class FeedbackRevisionRules
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "objective", "criteria", "context" };

    /// <summary>
    /// The first key in <paramref name="revision"/> (a task file as <c>h9k task revise --file</c> reads it) that is not the
    /// objective, the criteria, or the context, or null when it names none. Throws what <see cref="FrontmatterYaml.Parse"/> throws
    /// for a file that is not a task file.
    /// </summary>
    public static string? FirstKeyBeyondText(string revision) =>
        FrontmatterYaml.Parse(revision).Keys.FirstOrDefault(key => !Allowed.Contains(key));
}
