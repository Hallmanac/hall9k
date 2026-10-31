using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The one reader of which team keys the install's Linear connection can see, for the two places that
/// have to tell a Linear identifier from a token that only looks like one (<c>UTF-8</c>,
/// <c>SHA-256</c>, <c>CVE-2024-1</c>): the guard on a pull request's text, and the scan of a pull
/// request Hall9k adopts. One helper, so there is one rule for paging, for the deadline, and for what
/// failing means.
/// <para>
/// The answer is a set of upper-case keys, or null, and null is the one signal for every way the read
/// can fail: this install has no Linear connection (or an unusable one), any page fails (a partial
/// list is never used), or the read runs past <see cref="Deadline"/>. Each caller treats null as a
/// loss of precision and never as a reason to fail what it was doing. Only cancellation of the
/// caller's own token propagates.
/// </para>
/// </summary>
public static class LinearTeamKeys
{
    /// <summary>
    /// How long the whole read gets, every page together. Each request has the provider's own thirty
    /// seconds, so without this a workspace with many teams and a slow connection could hold a pull
    /// request open for minutes over a check whose failure costs only precision.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Reads the keys through the connection this install registered. <paramref name="requester"/> is
    /// the seam a test drives with recorded responses; null is the real network.
    /// </summary>
    public static async Task<IReadOnlySet<string>?> TryReadAsync(
        IQuerySession session, CancellationToken cancellationToken, LinearRequester? requester = null,
        TimeSpan? deadline = null) =>
        await TryReadAsync(await TryProviderAsync(session, requester, cancellationToken), cancellationToken, deadline);

    /// <summary>
    /// The Linear provider this install registered, or null when there is none or the record cannot be
    /// resolved (two connections, one with no workspace). Never throws for a tracker reason.
    /// </summary>
    public static async Task<LinearWorkItemProvider?> TryProviderAsync(
        IQuerySession session, LinearRequester? requester, CancellationToken cancellationToken)
    {
        try
        {
            return await WorkItemConnections.TryLinearProviderAsync(session, cancellationToken, requester: requester);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The same read against a provider already in hand; a null provider is a failed read.</summary>
    public static async Task<IReadOnlySet<string>?> TryReadAsync(
        LinearWorkItemProvider? provider, CancellationToken cancellationToken, TimeSpan? deadline = null)
    {
        if (provider is null)
        {
            return null;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline ?? Deadline);
        try
        {
            return await provider.ListTeamKeysAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
