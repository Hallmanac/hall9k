using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// The whole of capture: a thought, whose it is, and when (Decisions Log #35). ProjectId is
/// nullable because an idea may precede its project — or become one — and demanding a project
/// at capture would defeat the one thing capture is for.
/// <para>
/// WorkspaceHome is the one fact about the discovery workspace this event does record — not
/// where the workspace is, but whether it started life under a project's home
/// (<see cref="Hall9k.Domain.Infrastructure.Storage.IdeaPaths"/>): an idea captured before its
/// project had a home materialised here is permanently on the platform-global location, because
/// a home gained later must never redirect the read path away from a workspace a human may
/// already have dropped files into. <see cref="ProjectHome.None"/> for every idea captured with
/// no project, or with one whose home was not yet materialised on this machine — including every
/// idea captured before this field existed, which is the honest reading for a stream that never
/// observed a home in the first place.
/// </para>
/// </summary>
public sealed record IdeaCaptured(
    Guid Id,
    Guid OwnerId,
    string Text,
    Guid? ProjectId,
    DateTimeOffset CapturedAt,
    string WorkspaceHomeDirectory = "");
