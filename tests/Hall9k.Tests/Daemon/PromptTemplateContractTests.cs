using FluentAssertions;
using Hall9k.Daemon.Review;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The mechanical half of this task's own rule for telling a contract from guidance: every literal
/// <see cref="PromptContractTokens.All"/> names is code outside the prompt builders parses,
/// matches, or greps, so a template file — an operator's to freely edit — must never carry one of
/// them literally. A builder that needs one in the assembled prompt injects it as a substituted
/// parameter instead, computed in C# from the same source the parser itself reads.
/// </summary>
public sealed class PromptTemplateContractTests
{
    [Fact]
    public void No_checked_in_template_contains_a_contract_token_literally()
    {
        string templatesDirectory = Path.Combine(RepositoryRoot(), ".claude", "templates");
        Directory.Exists(templatesDirectory).Should().BeTrue(
            $"{templatesDirectory} is this checkout's own checked-in template source and must exist for "
            + "this guard to mean anything; a missing directory here is a broken checkout or a mispackaged "
            + "release, not a reason to skip the scan");

        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(templatesDirectory, "*.md", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (string token in PromptContractTokens.All)
            {
                if (ContainsToken(text, token))
                {
                    violations.Add($"{Path.GetRelativePath(templatesDirectory, file)} contains \"{token}\"");
                }
            }
        }

        violations.Should().BeEmpty(
            "a contract token belongs in the builder that injects it as a parameter, never typed into a "
            + "template an operator can freely edit");
    }

    /// <summary>
    /// An ordinary substring match for a token that already carries its own punctuation (a colon,
    /// a mid-word hyphen) — distinctive enough that no ordinary prose collides with it — and a
    /// boundary-checked match for a bare word token like <c>"deliver"</c> (the CLI's own <c>h9k
    /// task deliver</c> command name), so a template that merely says "delivered" or "deliverable"
    /// is not flagged for a command name it never typed (independent pre-PR review, cycle 1: the
    /// original plain <c>Contains</c> matched "deliver" inside both). Ordinal, not
    /// OrdinalIgnoreCase, even though <see cref="ReviewResultParser"/>'s own marker checks are
    /// case-insensitive: this scan is a substring match anywhere in the file, not the parser's own
    /// trimmed-start-of-line check, so case-folding it flags ordinary prose the parser itself would
    /// never treat as a marker — <c>findings-report.md</c>'s own "nothing in it is a verdict: it is
    /// what two machines found" reads as containing "VERDICT:" the moment the comparison stops
    /// caring about case, a false positive a Copilot review's suggestion to match the parser's own
    /// case-insensitivity here would have introduced.
    /// </summary>
    private static bool ContainsToken(string text, string token)
    {
        if (!token.All(char.IsLetter))
        {
            return text.Contains(token, StringComparison.Ordinal);
        }

        int start = 0;
        int index;
        while ((index = text.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            bool boundaryBefore = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            int after = index + token.Length;
            bool boundaryAfter = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            if (boundaryBefore && boundaryAfter)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find this checkout's own Hall9k.slnx above " + AppContext.BaseDirectory);
    }
}
