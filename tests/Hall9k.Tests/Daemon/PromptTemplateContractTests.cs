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
        if (!Directory.Exists(templatesDirectory))
        {
            return;
        }

        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(templatesDirectory, "*.md", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (string token in PromptContractTokens.All)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetRelativePath(templatesDirectory, file)} contains \"{token}\"");
                }
            }
        }

        violations.Should().BeEmpty(
            "a contract token belongs in the builder that injects it as a parameter, never typed into a "
            + "template an operator can freely edit");
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
