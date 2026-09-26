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

    /// <summary>A gh that cannot answer at all: every call exits non-zero, the shape an unauthenticated or offline gh has.</summary>
    public static ProjectGitHubAccessMirror Unreachable() =>
        new(new ProjectGitHubClient(
            (_, _, _, _, _) => Task.FromResult(new ProcessResult(1, string.Empty, "gh: not logged in")),
            (_, _, _, _) => Task.FromResult(new ProcessResult(0, "gh-token-for-test", string.Empty))));

    /// <summary>A gh that answers <c>repo view</c> with push but fails the collaborator list, so the roster cannot be read fresh.</summary>
    public static ProjectGitHubAccessMirror GrantingPushWithFailingCollaboratorList(string repository = "acme/widgets") =>
        new(new ProjectGitHubClient(
            (_, arguments, _, _, _) => Task.FromResult(
                arguments.Any(argument => argument.Contains("collaborators", StringComparison.Ordinal))
                    ? new ProcessResult(1, string.Empty, "gh: HTTP 502")
                    : new ProcessResult(0, $$"""{"viewerPermission":"ADMIN","nameWithOwner":"{{repository}}"}""", string.Empty)),
            (_, _, _, _) => Task.FromResult(new ProcessResult(0, "gh-token-for-test", string.Empty))));

    public static ProjectGitHubAccessMirror DenyingPush(string repository = "acme/widgets", string role = "READ") =>
        new(Client(repository, role));
}
