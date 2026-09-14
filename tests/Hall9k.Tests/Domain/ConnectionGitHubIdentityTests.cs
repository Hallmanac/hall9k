using FluentAssertions;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// GitHub's own numeric id and login, observed onto a GitHub connection's stream (idea 202383dc,
/// A2b, item 1) — the identity counterpart to <see cref="ConnectionTrackerIdentityObserved"/>'s
/// Jira read, pure domain logic given an already-read (id, login) pair rather than a live gh call.
/// </summary>
public sealed class ConnectionGitHubIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    private static ConnectionAggregate RegisterGitHub(string login = "hallmanac")
    {
        ConnectionAggregate connection = new();
        connection.Apply(ConnectionDecider.Register(
            Guid.NewGuid(), Owner, WorkItemProvider.GitHub, login, CredentialReference.GhCli, Now));
        return connection;
    }

    [Fact]
    public void Two_installs_authenticated_as_the_same_account_observe_the_identical_numeric_id()
    {
        // Two different connections (two different installs, or two different genesis bootstraps)
        // both authenticated as the same GitHub account must both record the same numeric id —
        // the fact that makes it an identity rather than a label (idea 202383dc, A2b, item 1).
        ConnectionAggregate first = RegisterGitHub();
        ConnectionAggregate second = RegisterGitHub();

        ConnectionGitHubIdentityObserved? firstObserved = ConnectionDecider.ObserveGitHubIdentity(first, 4181388, "hallmanac", Now);
        ConnectionGitHubIdentityObserved? secondObserved = ConnectionDecider.ObserveGitHubIdentity(second, 4181388, "hallmanac", Now);

        firstObserved.Should().NotBeNull();
        secondObserved.Should().NotBeNull();
        firstObserved!.GitHubAccountId.Should().Be(secondObserved!.GitHubAccountId);
        firstObserved.GitHubAccountId.Should().Be(4181388);
    }

    [Fact]
    public void An_unchanged_identity_is_not_re_appended()
    {
        ConnectionAggregate connection = RegisterGitHub();
        connection.Apply(ConnectionDecider.ObserveGitHubIdentity(connection, 4181388, "hallmanac", Now)!);

        ConnectionDecider.ObserveGitHubIdentity(connection, 4181388, "hallmanac", Now.AddDays(1))
            .Should().BeNull("racing another door that observed the identical identity is harmless, not a change");
    }

    [Fact]
    public void A_renamed_login_under_the_same_numeric_id_is_still_a_change_worth_recording()
    {
        ConnectionAggregate connection = RegisterGitHub();
        connection.Apply(ConnectionDecider.ObserveGitHubIdentity(connection, 4181388, "hallmanac", Now)!);

        ConnectionGitHubIdentityObserved? renamed = ConnectionDecider.ObserveGitHubIdentity(connection, 4181388, "brianhall", Now.AddDays(1));

        renamed.Should().NotBeNull();
        renamed!.GitHubAccountId.Should().Be(4181388, "the id underneath a rename never changes");
        renamed.GitHubLogin.Should().Be("brianhall");
    }

    /// <summary>
    /// A different numeric id under an already-confirmed connection is a genuinely different
    /// GitHub account, not a rename — treating it the same as a rename would let an owner's
    /// unrelated `gh auth switch` silently rebind this install's one GitHub connection, and every
    /// project routed through it, to whichever account gh's active session happens to be the next
    /// time anything refreshes it. Account switching as a first-class feature is parked, so a
    /// genuine account change is refused rather than silently accepted (independent pre-PR review,
    /// cycle 3, adversarial lens, medium).
    /// </summary>
    [Fact]
    public void A_different_numeric_id_under_an_already_confirmed_connection_is_refused_rather_than_silently_rebound()
    {
        ConnectionAggregate connection = RegisterGitHub();
        connection.Apply(ConnectionDecider.ObserveGitHubIdentity(connection, 4181388, "hallmanac", Now)!);

        Action observe = () => ConnectionDecider.ObserveGitHubIdentity(connection, 999999, "someoneelse", Now.AddDays(1));

        observe.Should().Throw<DomainValidationException>().WithMessage("*4181388*999999*");
    }

    [Fact]
    public void A_jira_connection_is_refused_because_nothing_ever_asks_GitHub_who_a_Jira_account_is()
    {
        ConnectionAggregate jira = new();
        jira.Apply(ConnectionDecider.Register(
            Guid.NewGuid(), Owner, WorkItemProvider.Jira, "brian@example.com",
            CredentialReference.File("jira-hall9k"), Now, new Uri("https://hall9k.atlassian.net")));

        Action observe = () => ConnectionDecider.ObserveGitHubIdentity(jira, 1, "hallmanac", Now);

        observe.Should().Throw<DomainValidationException>().WithMessage("*GitHub*identity*");
    }
}
