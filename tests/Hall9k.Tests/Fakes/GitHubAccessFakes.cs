using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// Preconfigured <see cref="ProjectGitHubClient"/>/<see cref="ProjectGitHubAccessMirror"/>
/// instances for a test that needs <c>h9k project join</c>'s push check and access mirror to
/// answer a scripted way rather than reaching a real <c>gh</c> or network — Brian's 2026-09-13
/// testing rule, applied through the transport seam like every other GitHub-backed test double in
/// this suite.
/// </summary>
internal static class GitHubAccessFakes
{
    public static ProjectGitHubClient Client(
        string repository = "acme/widgets", string role = "ADMIN", string collaboratorsJson = "[]") =>
        new(
            (_, arguments, _, _, _) => Task.FromResult(new ProcessResult(
                0,
                arguments.Any(argument => argument.Contains("collaborators", StringComparison.Ordinal))
                    ? collaboratorsJson
                    : $$"""{"viewerPermission":"{{role}}","nameWithOwner":"{{repository}}"}""",
                string.Empty)),
            (_, _, _, _) => Task.FromResult(new ProcessResult(0, "gh-token-for-test", string.Empty)));

    public static ProjectGitHubAccessMirror GrantingPush(
        string repository = "acme/widgets", string role = "ADMIN", string collaboratorsJson = "[]") =>
        new(Client(repository, role, collaboratorsJson));

    public static ProjectGitHubAccessMirror DenyingPush(string repository = "acme/widgets", string role = "READ") =>
        new(Client(repository, role));
}
