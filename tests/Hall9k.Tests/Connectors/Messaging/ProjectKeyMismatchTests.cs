using FluentAssertions;
using Hall9k.Connectors.Messaging;
using Xunit;

namespace Hall9k.Tests.Connectors.Messaging;

/// <summary>
/// <see cref="ProjectKeyMismatch.IsMismatch"/> alone — the pure comparison idea 6be68ee2's
/// trust-ledger review (finding 13) extracted from the three near-identical private copies
/// <c>MessageInbox</c>, <c>EventCatchUpInbox</c>, and <c>EventReplicationInbox</c> each carried, and
/// which every one of them now applies unconditionally rather than only once an envelope's own key
/// happens to already be 26 characters. The async cross-project lookup
/// <see cref="ProjectKeyMismatch.IsMismatchAsync"/> falls back to is exercised at the reader level
/// (the existing Docker-tagged project-scoping tests), never here — it needs a document store this
/// pure predicate deliberately does not.
/// </summary>
public sealed class ProjectKeyMismatchTests
{
    private const string LocalKey = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public void A_null_candidate_key_mismatches_once_the_local_project_has_a_key()
    {
        ProjectKeyMismatch.IsMismatch(candidateKey: null, LocalKey).Should().BeTrue(
            "once this project knows its own key, an envelope carrying no key at all is refused rather than waved through");
    }

    [Fact]
    public void A_non_26_character_candidate_key_mismatches_once_the_local_project_has_a_key()
    {
        ProjectKeyMismatch.IsMismatch(candidateKey: "too-short", LocalKey).Should().BeTrue(
            "a malformed key is refused the identical way a genuine mismatch is once this project has a key");
    }

    [Fact]
    public void A_genuinely_different_26_character_candidate_key_mismatches()
    {
        ProjectKeyMismatch.IsMismatch(candidateKey: "01ARZ3NDEKTSV4RRFFQ69G5FZZ", LocalKey).Should().BeTrue();
    }

    [Fact]
    public void A_candidate_key_matching_the_local_key_exactly_never_mismatches()
    {
        ProjectKeyMismatch.IsMismatch(candidateKey: LocalKey, LocalKey).Should().BeFalse();
    }

    [Fact]
    public void A_null_candidate_key_never_mismatches_when_the_local_project_has_no_key_yet()
    {
        ProjectKeyMismatch.IsMismatch(candidateKey: null, localProjectKey: null).Should().BeFalse(
            "a project with no key of its own yet has no opinion — the local fact that keeps an existing "
            + "fleet with an unkeyed project from ever stalling");
    }

    [Fact]
    public void A_malformed_candidate_key_never_mismatches_when_the_local_project_has_no_key_yet()
    {
        ProjectKeyMismatch.IsMismatch(candidateKey: "not-a-real-key", localProjectKey: null).Should().BeFalse(
            "no local key means nothing here is judged as a mismatch, whatever the candidate looks like");
    }

    [Fact]
    public void A_genuinely_26_character_candidate_key_never_mismatches_on_its_own_when_the_local_project_has_no_key_yet()
    {
        // The direct-comparison branch alone has no opinion here — ProjectKeyMismatch.IsMismatchAsync's
        // own cross-project lookup is the only thing that can still refuse this shape, and that lookup
        // is deliberately outside this pure predicate's own reach.
        ProjectKeyMismatch.IsMismatch(candidateKey: "01ARZ3NDEKTSV4RRFFQ69G5FZZ", localProjectKey: null).Should().BeFalse();
    }
}
