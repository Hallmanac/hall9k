using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// An external system this install can reach, as one account with one credential pointer
/// (PLAN.md §10). The credential is a <see cref="CredentialReference"/> and never a secret:
/// this record is a JSON payload in Postgres that anyone with the database can read.
/// <para>
/// SiteUrl is where the account lives, for the providers that have more than one home. GitHub
/// has exactly one (github.com), so its connections carry null and that is a fact rather than a
/// gap; a Jira account lives at a tenant of its own (https://your-org.atlassian.net) and there
/// is no reading a card without it. Appended with a default so connections registered before
/// Jira existed replay as the null they were written with.
/// </para>
/// </summary>
public sealed record ConnectionRegistered(
    Guid Id,
    Guid OwnerId,
    WorkItemProvider Provider,
    string ExternalAccountId,
    CredentialReference CredentialReference,
    DateTimeOffset RegisteredAt,
    Uri? SiteUrl = null);
