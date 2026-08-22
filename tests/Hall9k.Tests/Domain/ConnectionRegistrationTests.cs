using FluentAssertions;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// What a connection is allowed to record (PLAN.md §10). The whole discipline is that the event
/// carries a pointer to a credential and never the credential, and the rules here are the two
/// that make a pointer usable: it has to name something, and a provider with more than one home
/// has to say which one.
/// </summary>
public sealed class ConnectionRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Uri Site = new("https://hall9k.atlassian.net");

    private static ConnectionRegistered RegisterJira(
        CredentialReference? credential = null, Uri? site = null) =>
        ConnectionDecider.Register(
            Guid.NewGuid(), Owner, WorkItemProvider.Jira, "brian@example.com",
            credential ?? CredentialReference.File("jira-hall9k"), Now, site ?? Site);

    [Fact]
    public void A_jira_connection_records_its_site_its_account_and_where_the_token_lives()
    {
        ConnectionRegistered registered = RegisterJira();

        registered.SiteUrl.Should().Be(Site);
        registered.ExternalAccountId.Should().Be("brian@example.com");
        registered.CredentialReference.ToString().Should().Be("file:jira-hall9k");
    }

    [Fact]
    public void A_jira_connection_without_a_site_is_refused_because_nothing_can_be_read_without_one()
    {
        Action register = () => ConnectionDecider.Register(
            Guid.NewGuid(), Owner, WorkItemProvider.Jira, "brian@example.com",
            CredentialReference.File("jira-hall9k"), Now, siteUrl: null);

        register.Should().Throw<DomainValidationException>().WithMessage("*requires the site*");
    }

    [Fact]
    public void A_github_connection_carries_no_site_and_that_is_a_fact_rather_than_a_gap()
    {
        // GitHub has exactly one home, so the field stays null there rather than being filled in
        // with the obvious answer — an observation nobody made is not one to record.
        ConnectionRegistered registered = ConnectionDecider.Register(
            Guid.NewGuid(), Owner, WorkItemProvider.GitHub, "hallmanac", CredentialReference.GhCli, Now);

        registered.SiteUrl.Should().BeNull();
    }

    [Fact]
    public void A_github_connection_handed_a_site_is_refused_rather_than_recording_one()
    {
        // The rule has to run both ways or the stream can say what the other half forbids: with
        // only "Jira needs a site" enforced, a GitHub connection could carry https://github.com
        // and a null SiteUrl would stop meaning "there is one home". Origin: the pull-request
        // review of the Jira branch (2026-08-22).
        Action register = () => ConnectionDecider.Register(
            Guid.NewGuid(), Owner, WorkItemProvider.GitHub, "hallmanac",
            CredentialReference.GhCli, Now, new Uri("https://github.com"));

        register.Should().Throw<DomainValidationException>().WithMessage("*records no site*");
    }

    [Fact]
    public void A_credential_reference_that_names_nothing_is_refused_where_it_is_written()
    {
        // Every kind but gh-cli names a variable, a keychain entry, or a file. One that names
        // none of them is a pointer that cannot be resolved, and finding that out at the first
        // import is finding it out in the wrong place.
        Action register = () => RegisterJira(new CredentialReference(CredentialKind.EnvironmentVariable, null));

        register.Should().Throw<DomainValidationException>().WithMessage("*names nothing*");
    }

    [Fact]
    public void Re_registering_keeps_the_id_projects_bind_to_and_replaces_the_rest()
    {
        // Rotating a token has to be possible without a remove command, and a second connection
        // would leave two Jira accounts with nothing saying which one a project uses.
        ConnectionRegistered first = RegisterJira();
        ConnectionAggregate connection = new();
        connection.Apply(first);

        ConnectionReregistered again = ConnectionDecider.Reregister(
            connection, "someone-else@example.com", CredentialReference.EnvironmentVariable("JIRA_TOKEN"),
            Now.AddDays(1), new Uri("https://moved.atlassian.net"));
        connection.Apply(again);

        connection.Id.Should().Be(first.Id, "projects bind to this id");
        connection.OwnerId.Should().Be(Owner, "whose connection it is was settled when it was created");
        connection.ExternalAccountId.Should().Be("someone-else@example.com");
        connection.CredentialReference.ToString().Should().Be("env:JIRA_TOKEN");
        connection.SiteUrl.Should().Be(new Uri("https://moved.atlassian.net"));
    }

    [Fact]
    public void Re_registering_runs_the_same_rules_rather_than_a_relaxed_set()
    {
        // Otherwise registering twice would be a way around the check registering once enforces.
        ConnectionAggregate connection = new();
        connection.Apply(RegisterJira());

        Action reregister = () => ConnectionDecider.Reregister(
            connection, "brian@example.com", CredentialReference.File("jira-hall9k"), Now, siteUrl: null);

        reregister.Should().Throw<DomainValidationException>().WithMessage("*requires the site*");
    }

    [Fact]
    public void The_pane_reads_the_replacement_rather_than_the_original()
    {
        ConnectionRegistered first = RegisterJira();
        ConnectionDetailsProjection projection = new();
        ConnectionDetails view = projection.Create(new FakeEvent<ConnectionRegistered>(first));

        projection.Apply(
            new FakeEvent<ConnectionReregistered>(new ConnectionReregistered(
                first.Id, WorkItemProvider.Jira, "brian@example.com",
                CredentialReference.EnvironmentVariable("JIRA_TOKEN"), Now.AddDays(1), Site)),
            view);

        view.CredentialReference.Should().Be("env:JIRA_TOKEN");
        view.ReregisteredAt.Should().Be(Now.AddDays(1));
    }
}
