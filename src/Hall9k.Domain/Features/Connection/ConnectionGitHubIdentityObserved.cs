namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// GitHub's own numeric id and login for this connection's account, as GitHub itself reported
/// them — the identity counterpart to <see cref="ConnectionTrackerIdentityObserved"/>'s Jira
/// <c>accountId</c>, appended beside it rather than folded into
/// <see cref="ConnectionRegistered"/>/<see cref="ConnectionReregistered"/> for the identical
/// reason: registering a connection and observing what the tracker or, here, GitHub itself says
/// about it are different acts that happen at different moments (idea 202383dc, A2b).
/// <para>
/// The numeric id is what makes this an identity rather than a label: a GitHub login can be
/// renamed by its own holder, but the id underneath it never changes, so two installs
/// authenticated as the same account always observe the same id even if one of them last observed
/// an old login.
/// </para>
/// <para>
/// Read at bootstrap (<see cref="Hall9k.Domain.Infrastructure.Bootstrap.NodeBootstrap"/>, the
/// first time this install's GitHub connection is created), again at every daemon start
/// (<c>DispatchLoop.ExecuteAsync</c>, through <c>NodeContext.InitializeAsync</c>'s
/// <c>NodeBootstrap.GhIdentityReader</c> seam), and again at <c>h9k project add</c> and
/// <c>h9k project join</c> (both through <c>NodeBootstrap.RefreshGitHubIdentityAsync</c>) —
/// nothing else already triggers a GitHub read the way Jira's own <c>TrackerClaimGate</c> does
/// lazily on first claim-gate check, so each of those moments asks explicitly. A daemon-start
/// refresh with no seam was tried first and reverted, then restored once
/// <c>NodeBootstrap.GhIdentityReader</c> gave every integration test a fake to pin instead of the
/// real <c>gh</c> (<c>NodeContext.InitializeAsync</c>'s own doc comment has the detail).
/// </para>
/// </summary>
public sealed record ConnectionGitHubIdentityObserved(
    Guid Id,
    long GitHubAccountId,
    string GitHubLogin,
    DateTimeOffset ObservedAt);
