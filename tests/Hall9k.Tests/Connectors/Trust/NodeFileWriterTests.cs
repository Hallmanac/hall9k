using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors.Trust;

/// <summary>
/// <see cref="NodeFileWriter"/> against <see cref="FakeLedger"/>: the join write carries the
/// GitHub declaration and the display name, and the refresh changes only the named field(s) of an
/// existing file.
/// </summary>
public sealed class NodeFileWriterTests
{
    private const string Repository = "/fake/repo";
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string RefName = $"refs/hall9k/ledger/nodes/{NodeId}";
    private static readonly string NodePath = $"nodes/{NodeId}/node.yaml";
    private static readonly NodeSigningKey Key = new("/keys/id_ed25519", "ssh-ed25519 AAAAkey node", "key-fingerprint");
    private static readonly LedgerCommitter Committer = new("Test", "test@hall9k.local");
    private static readonly LedgerSigningKey SigningKey = new("/keys/id_ed25519");
    private static readonly DeclaredGitHubAccount Octocat = new(42, "octocat");

    private const string OldFile =
        "node_id: \"11111111-1111-1111-1111-111111111111\"\n"
        + "public_key: \"ssh-ed25519 AAAAkey node\"\n"
        + "owner_fingerprint: \"root-fingerprint\"\n"
        + "joined_at: \"2026-09-01T00:00:00.0000000+00:00\"\n"
        + "invite_proof: \"pending-proof\"\n";

    [Fact]
    public async Task Join_writes_the_declaration_with_the_rest_of_the_file()
    {
        FakeLedger ledger = new();

        bool wrote = await NodeFileWriter.WriteAsync(
            ledger, Repository, NodeId, Key, "root-fingerprint", "machine", "macOS", DateTimeOffset.UnixEpoch, inviteProof: null,
            Octocat, DisplayName.None, Committer, SigningKey, CancellationToken.None);

        wrote.Should().BeTrue();
        LedgerWriteRequest write = ledger.Writes.Should().ContainSingle().Subject;
        write.CommitMessage.Should().Be("Join node");
        write.Content.Should().Contain("github_login: \"octocat\"").And.Contain("github_account_id: \"42\"");
        NodeFileWriter.ReadDeclaration(write.Content).Should().Be(Octocat);
    }

    [Fact]
    public async Task Join_with_no_readable_account_omits_the_fields_rather_than_guessing()
    {
        FakeLedger ledger = new();

        await NodeFileWriter.WriteAsync(
            ledger, Repository, NodeId, Key, "root-fingerprint", "machine", "macOS", DateTimeOffset.UnixEpoch, null,
            github: null, DisplayName.None, Committer, SigningKey, CancellationToken.None);

        ledger.Writes.Single().Content.Should().NotContain("github_");
    }

    [Fact]
    public async Task Join_writes_the_effective_display_name_it_is_given()
    {
        FakeLedger ledger = new();

        await NodeFileWriter.WriteAsync(
            ledger, Repository, NodeId, Key, "root-fingerprint", "machine", "macOS", DateTimeOffset.UnixEpoch, null,
            github: null, DisplayName.Parse("Ada Lovelace"), Committer, SigningKey, CancellationToken.None);

        ledger.Writes.Single().Content.Should().Contain("display_name: \"Ada Lovelace\"");
    }

    [Fact]
    public async Task Join_with_no_display_name_omits_the_line_rather_than_writing_an_empty_one()
    {
        FakeLedger ledger = new();

        await NodeFileWriter.WriteAsync(
            ledger, Repository, NodeId, Key, "root-fingerprint", "machine", "macOS", DateTimeOffset.UnixEpoch, null,
            github: null, DisplayName.None, Committer, SigningKey, CancellationToken.None);

        ledger.Writes.Single().Content.Should().NotContain("display_name");
    }

    [Fact]
    public async Task A_rejoin_that_cannot_read_gh_keeps_the_declaration_the_file_already_carries()
    {
        FakeLedger ledger = new();
        await NodeFileWriter.WriteAsync(
            ledger, Repository, NodeId, Key, "root-fingerprint", "machine", "macOS", DateTimeOffset.UnixEpoch, null,
            Octocat, DisplayName.None, Committer, SigningKey, CancellationToken.None);

        bool wrote = await NodeFileWriter.WriteAsync(
            ledger, Repository, NodeId, Key, "root-fingerprint", "machine", "macOS", DateTimeOffset.UnixEpoch, null,
            github: null, DisplayName.None, Committer, SigningKey, CancellationToken.None);

        wrote.Should().BeFalse("the carried declaration makes the regenerated content identical");
        ledger.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_adds_only_the_two_fields_and_keeps_the_invite_proof_as_an_update_signed_by_the_nodes_key()
    {
        FakeLedger ledger = await SeedAsync(OldFile);

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshGitHubDeclarationAsync(
            ledger, Repository, NodeId, Octocat, Key.PublicKeyLine, Committer, SigningKey, CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.Written);
        LedgerWriteRequest write = ledger.Writes[^1];
        write.CommitMessage.Should().Be("Update node facts");
        write.SigningKey.Should().Be(SigningKey);
        write.Content.Should().Be(OldFile + "github_login: \"octocat\"\ngithub_account_id: \"42\"\n");
    }

    [Fact]
    public async Task Refresh_replaces_a_changed_declaration_in_place_and_touches_nothing_else()
    {
        string declared = OldFile.Replace("invite_proof", "github_login: \"old-name\"\ngithub_account_id: \"42\"\ninvite_proof");
        FakeLedger ledger = await SeedAsync(declared);

        await NodeFileWriter.RefreshGitHubDeclarationAsync(
            ledger, Repository, NodeId, Octocat, Key.PublicKeyLine, Committer, SigningKey, CancellationToken.None);

        ledger.Writes[^1].Content.Should().Be(declared.Replace("old-name", "octocat"));
    }

    [Fact]
    public async Task Refresh_keeps_a_files_own_windows_line_endings()
    {
        string crlf = OldFile.Replace("\n", "\r\n");
        FakeLedger ledger = await SeedAsync(crlf);

        await NodeFileWriter.RefreshGitHubDeclarationAsync(
            ledger, Repository, NodeId, Octocat, Key.PublicKeyLine, Committer, SigningKey, CancellationToken.None);

        ledger.Writes[^1].Content.Should().Be(crlf + "github_login: \"octocat\"\r\ngithub_account_id: \"42\"\r\n");
    }

    [Fact]
    public async Task Refresh_writes_nothing_when_the_file_already_declares_the_observed_account()
    {
        FakeLedger ledger = await SeedAsync(OldFile + "github_login: \"octocat\"\ngithub_account_id: \"42\"\n");
        int writesBefore = ledger.Writes.Count;

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshGitHubDeclarationAsync(
            ledger, Repository, NodeId, Octocat, Key.PublicKeyLine, Committer, SigningKey, CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.Unchanged);
        ledger.Writes.Should().HaveCount(writesBefore);
    }

    [Fact]
    public async Task Refresh_never_creates_a_node_file_where_none_exists()
    {
        FakeLedger ledger = new();

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshGitHubDeclarationAsync(
            ledger, Repository, NodeId, Octocat, Key.PublicKeyLine, Committer, SigningKey, CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.NoNodeFile);
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_writes_nothing_when_the_file_names_a_different_key_than_the_one_that_would_sign()
    {
        FakeLedger ledger = await SeedAsync(OldFile);
        int writesBefore = ledger.Writes.Count;

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshGitHubDeclarationAsync(
            ledger, Repository, NodeId, Octocat, "ssh-ed25519 AAAAregenerated node", Committer, SigningKey, CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.SigningKeyDiffers);
        ledger.Writes.Should().HaveCount(writesBefore);
    }

    [Fact]
    public async Task Display_name_refresh_adds_the_field_and_touches_nothing_else()
    {
        FakeLedger ledger = await SeedAsync(OldFile);

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshDisplayNameAsync(
            ledger, Repository, NodeId, DisplayName.Parse("Ada Lovelace"), Key.PublicKeyLine, Committer, SigningKey,
            CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.Written);
        LedgerWriteRequest write = ledger.Writes[^1];
        write.CommitMessage.Should().Be("Update node facts");
        write.SigningKey.Should().Be(SigningKey);
        write.Content.Should().Be(OldFile + "display_name: \"Ada Lovelace\"\n");
    }

    [Fact]
    public async Task Display_name_refresh_replaces_a_changed_name_in_place_and_touches_nothing_else()
    {
        string declared = OldFile.Replace("invite_proof", "display_name: \"Old Name\"\ninvite_proof");
        FakeLedger ledger = await SeedAsync(declared);

        await NodeFileWriter.RefreshDisplayNameAsync(
            ledger, Repository, NodeId, DisplayName.Parse("New Name"), Key.PublicKeyLine, Committer, SigningKey,
            CancellationToken.None);

        ledger.Writes[^1].Content.Should().Be(declared.Replace("Old Name", "New Name"));
    }

    [Fact]
    public async Task Display_name_refresh_removes_the_line_entirely_rather_than_writing_an_empty_one()
    {
        string declared = OldFile.Replace("invite_proof", "display_name: \"Old Name\"\ninvite_proof");
        FakeLedger ledger = await SeedAsync(declared);

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshDisplayNameAsync(
            ledger, Repository, NodeId, DisplayName.None, Key.PublicKeyLine, Committer, SigningKey, CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.Written);
        ledger.Writes[^1].Content.Should().Be(OldFile).And.NotContain("display_name");
    }

    [Fact]
    public async Task Display_name_refresh_writes_nothing_when_the_file_already_carries_no_name_and_none_is_asked_for()
    {
        FakeLedger ledger = await SeedAsync(OldFile);
        int writesBefore = ledger.Writes.Count;

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshDisplayNameAsync(
            ledger, Repository, NodeId, DisplayName.None, Key.PublicKeyLine, Committer, SigningKey, CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.Unchanged);
        ledger.Writes.Should().HaveCount(writesBefore);
    }

    [Fact]
    public async Task Display_name_refresh_never_creates_a_node_file_where_none_exists()
    {
        FakeLedger ledger = new();

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshDisplayNameAsync(
            ledger, Repository, NodeId, DisplayName.Parse("Ada Lovelace"), Key.PublicKeyLine, Committer, SigningKey,
            CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.NoNodeFile);
        ledger.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Display_name_refresh_writes_nothing_when_the_file_names_a_different_key_than_the_one_that_would_sign()
    {
        FakeLedger ledger = await SeedAsync(OldFile);
        int writesBefore = ledger.Writes.Count;

        NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshDisplayNameAsync(
            ledger, Repository, NodeId, DisplayName.Parse("Ada Lovelace"), "ssh-ed25519 AAAAregenerated node", Committer,
            SigningKey, CancellationToken.None);

        outcome.Should().Be(NodeFileRefreshOutcome.SigningKeyDiffers);
        ledger.Writes.Should().HaveCount(writesBefore);
    }

    private static async Task<FakeLedger> SeedAsync(string content)
    {
        FakeLedger ledger = new();
        await ledger.WriteAsync(
            new LedgerWriteRequest(Repository, RefName, NodePath, content, null, "seed", Committer, SigningKey),
            CancellationToken.None);
        return ledger;
    }
}
