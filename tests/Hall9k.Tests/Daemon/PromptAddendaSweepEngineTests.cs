using FluentAssertions;
using Hall9k.Daemon.PromptAddenda;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="PromptAddendaSweepEngine.SelectMaterializedContent"/> — the pure reducer behind the
/// owner test (idea 6be68ee2, trust-ledger finding 6): from one builder path's own commit history,
/// newest first, plus a verdict per commit, the state to materialize is whatever the NEWEST
/// authorized commit produced. No document store, no ledger, no chain read — just the reduction
/// itself.
/// </summary>
public sealed class PromptAddendaSweepEngineTests
{
    [Fact]
    public void An_owner_authorized_tip_materializes_its_own_content()
    {
        PromptAddendumCommitVerdict[] commits = [new("sha-owner", "Owner's own guidance.", AuthorizedByOwner: true)];

        string? materialized = PromptAddendaSweepEngine.SelectMaterializedContent(commits);

        materialized.Should().Be("Owner's own guidance.");
    }

    [Fact]
    public void A_members_overwrite_over_an_owner_commit_gives_the_owners_content()
    {
        PromptAddendumCommitVerdict[] commits =
        [
            new("sha-member", "A member's own overwrite.", AuthorizedByOwner: false),
            new("sha-owner", "Owner's own guidance.", AuthorizedByOwner: true),
        ];

        string? materialized = PromptAddendaSweepEngine.SelectMaterializedContent(commits);

        materialized.Should().Be("Owner's own guidance.", "the member's own overwrite is skipped over");
    }

    [Fact]
    public void A_members_delete_over_an_owner_commit_restores_the_owners_content()
    {
        PromptAddendumCommitVerdict[] commits =
        [
            new("sha-member-delete", null, AuthorizedByOwner: false),
            new("sha-owner", "Owner's own guidance.", AuthorizedByOwner: true),
        ];

        string? materialized = PromptAddendaSweepEngine.SelectMaterializedContent(commits);

        materialized.Should().Be(
            "Owner's own guidance.", "the member's own delete is skipped, restoring the owner's content rather than removing it");
    }

    /// <summary>Covers a single unsigned commit as well as multiple: the reducer sees only the
    /// verdict, never why it was refused (an unsigned commit vs. a revoked node's own commit), so
    /// one <see cref="AuthorizedByOwner"/>-false case proves the whole family — that distinction
    /// itself lives in <c>OwnerChainAuthorization</c> and is covered there and in the integration
    /// tests. A dedicated single-commit case used to sit beside this one proving the identical
    /// thing through the identical seam (independent pre-PR review, cycle 3, both lenses, low) —
    /// folded in here as the single-entry input rather than kept as its own fact.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Nothing_authorized_across_the_whole_history_gives_absent(int commitCount)
    {
        PromptAddendumCommitVerdict[] allCommits =
        [
            new("sha-member-2", "Second bad edit.", AuthorizedByOwner: false),
            new("sha-member-1", "First bad edit.", AuthorizedByOwner: false),
        ];
        PromptAddendumCommitVerdict[] commits = allCommits[..commitCount];

        string? materialized = PromptAddendaSweepEngine.SelectMaterializedContent(commits);

        materialized.Should().BeNull();
    }

    [Fact]
    public void An_empty_history_gives_absent()
    {
        string? materialized = PromptAddendaSweepEngine.SelectMaterializedContent([]);

        materialized.Should().BeNull();
    }

    [Fact]
    public void The_owners_own_deletion_commit_materializes_as_absent()
    {
        PromptAddendumCommitVerdict[] commits = [new("sha-owner-delete", null, AuthorizedByOwner: true)];

        string? materialized = PromptAddendaSweepEngine.SelectMaterializedContent(commits);

        materialized.Should().BeNull("the owner's own commit deleted the addendum — its absence is the owner's own state, not a refusal");
    }
}
