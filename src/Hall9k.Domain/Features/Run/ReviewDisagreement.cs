namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One finding from a human's changes-requested review that the fix session did not agree with,
/// parked for the implementer to settle (task: a changes-requested pull-request review from a
/// human becomes a fix lap). Nothing about it has been said on the pull request: the session
/// neither replied nor resolved the thread, because a disagreement with a person is the
/// implementer's to send, not an agent's (Brian's ruling, 2026-09-06 12:15).
/// <para>
/// The three things a human needs to decide are what this carries: what the reviewer asked for
/// (<see cref="Finding"/>), why the session thinks otherwise (<see cref="Reasoning"/>), and the
/// words it would send if the human agrees (<see cref="ProposedReply"/>). The last of those is a
/// draft and only ever a draft — <c>h9k review resolve</c> posts it as written, posts an edited
/// version, or posts nothing.
/// </para>
/// </summary>
/// <param name="Finding">
/// The reviewer's own point, as the session restated it. Blank when the session named none, which
/// reads as "the session disagreed without saying with what" rather than being filled in.
/// </param>
/// <param name="Reasoning">The session's own position: why it thinks the reviewer is wrong here.</param>
/// <param name="ProposedReply">
/// The reply the session drafted for the human to send, edit, or drop. Blank when it drafted none
/// — which is itself worth showing, because the human then has nothing to post as-written.
/// </param>
/// <param name="Location">
/// Where the disputed finding points, in the `path/to/file.cs:123` form
/// <see cref="ChangesRequestedFinding.Location"/> uses. Null when the session named none.
/// </param>
/// <param name="ThreadId">
/// The review thread the proposed reply would land inside, when the disputed finding was an
/// inline comment. Null when the disputed point was the review's own body, which has no thread:
/// a reply to that can only be a top-level pull-request comment, and this platform never starts a
/// review thread (AGENTS.md).
/// </param>
/// <param name="ReviewUrl">
/// Which changes-requested review this answers, when the session said so. Null when it did not —
/// a lap can carry more than one reviewer's review, and attributing a disagreement to whichever
/// one happened to be first would be a guess written into an audit field (AGENTS.md).
/// </param>
public sealed record ReviewDisagreement(
    string Finding,
    string Reasoning,
    string ProposedReply,
    string? Location = null,
    string? ThreadId = null,
    string? ReviewUrl = null);
