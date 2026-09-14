using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The pure collaborator-list mapper behind the GitHub access mirror (idea 202383dc, A2b, item 3):
/// every field is checked for its expected JSON kind before being read, so gh answering with a
/// field of the wrong shape (a custom collaborator role naming an object instead of a string, a
/// numeric id gh's own API changes into a string) skips that one entry rather than throwing
/// <c>InvalidOperationException</c> out of what the caller (<c>ProjectGitHubAccessMirror.ObserveAsync</c>)
/// treats as a best-effort read — a defect this mapper shipped with and fixed in the same task's
/// own self-review, alongside <c>NodeBootstrap.ParseGhIdentity</c>'s identical class of bug.
/// </summary>
public sealed class ProjectGitHubAccessMirrorTests
{
    [Fact]
    public void ParseCollaborators_reads_every_well_shaped_entry()
    {
        const string json = """
            [
              {"id": 1, "login": "brian", "role_name": "admin"},
              {"id": 2, "login": "alex", "role_name": "write"}
            ]
            """;

        IReadOnlyList<GitHubCollaboratorRole> collaborators = ProjectGitHubAccessMirror.ParseCollaborators(json);

        collaborators.Should().BeEquivalentTo(
        [
            new GitHubCollaboratorRole(1, "brian", GitHubRepositoryRole.Admin),
            new GitHubCollaboratorRole(2, "alex", GitHubRepositoryRole.Write),
        ]);
    }

    [Fact]
    public void ParseCollaborators_skips_an_entry_whose_id_is_not_a_number_rather_than_throwing()
    {
        const string json = """
            [
              {"id": "not-a-number", "login": "brian", "role_name": "admin"},
              {"id": 2, "login": "alex", "role_name": "write"}
            ]
            """;

        IReadOnlyList<GitHubCollaboratorRole> collaborators = ProjectGitHubAccessMirror.ParseCollaborators(json);

        collaborators.Should().ContainSingle().Which.Should().Be(new GitHubCollaboratorRole(2, "alex", GitHubRepositoryRole.Write));
    }

    [Fact]
    public void ParseCollaborators_skips_an_entry_whose_role_is_not_a_string_rather_than_throwing()
    {
        const string json = """[{"id": 1, "login": "brian", "role_name": 42}]""";

        ProjectGitHubAccessMirror.ParseCollaborators(json).Should().BeEmpty();
    }

    [Fact]
    public void ParseCollaborators_reads_an_empty_array_as_no_collaborators()
    {
        ProjectGitHubAccessMirror.ParseCollaborators("[]").Should().BeEmpty();
    }

    /// <summary>
    /// The REST collaborator endpoint names write-level access "push" rather than "write" — the
    /// same level <c>viewerPermission</c> (the endpoint <see cref="GitHubRepositoryRole"/>'s other
    /// caller reads) calls "write" — so a collaborator observed through this parser must still
    /// carry push, not just one spelled the way the other endpoint spells it.
    /// </summary>
    [Fact]
    public void ParseCollaborators_recognizes_the_REST_endpoints_own_push_spelling_of_write_access()
    {
        const string json = """[{"id": 1, "login": "brian", "role_name": "push"}]""";

        IReadOnlyList<GitHubCollaboratorRole> collaborators = ProjectGitHubAccessMirror.ParseCollaborators(json);

        collaborators.Should().ContainSingle().Which.Role.HasPush.Should().BeTrue("REST's \"push\" is the same access level as \"write\"");
    }

    [Fact]
    public void ParseCollaborators_skips_an_entry_that_is_not_an_object_rather_than_throwing()
    {
        const string json = """
            [
              null,
              {"id": 2, "login": "alex", "role_name": "write"}
            ]
            """;

        IReadOnlyList<GitHubCollaboratorRole> collaborators = ProjectGitHubAccessMirror.ParseCollaborators(json);

        collaborators.Should().ContainSingle().Which.Should().Be(new GitHubCollaboratorRole(2, "alex", GitHubRepositoryRole.Write));
    }
}
