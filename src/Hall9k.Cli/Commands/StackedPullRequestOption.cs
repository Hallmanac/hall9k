using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Reads <c>--stacked-on-pull-request</c>'s value into the number the domain takes (task: a stacked
/// child can stand on a pull request another install owns). Written once and shared by
/// <see cref="TaskAddCommand"/> and <see cref="TaskReviseCommand"/> so the two doors accept exactly
/// the same forms — a bare <c>264</c> and the <c>#264</c> a human copies out of GitHub — and refuse
/// anything else with the same sentence.
/// <para>
/// Deliberately narrower than <c>--from-pr</c>, which also takes <c>owner/repo#42</c> and a full
/// URL: the parent must be a pull request on THIS project's own repository, since that is the only
/// repository a child's branch can be cut from, so a form that names another one would only be
/// accepted in order to fail later.
/// </para>
/// </summary>
public static class StackedPullRequestOption
{
    /// <summary>
    /// The number, or null when nothing was passed. Refuses rather than guessing: a value that is
    /// not a positive number names no pull request, and silently reading it as "unstacked" would
    /// dispatch a child straight onto the project's base without the human ever being told.
    /// </summary>
    public static int? Parse(string? value)
    {
        if (value.IsBlank())
        {
            return null;
        }

        string bare = value!.Trim();
        bare = bare.StartsWith('#') ? bare[1..] : bare;
        return int.TryParse(bare, out int number) && number > 0
            ? number
            : throw new DomainValidationException(
                $"--stacked-on-pull-request takes the parent's pull-request number on this project's "
                + $"repository, as GitHub shows it (264 or #264); '{value}' is not one. To stack on a task "
                + "in this install's own records instead, use --stacked-on with its id.");
    }
}
