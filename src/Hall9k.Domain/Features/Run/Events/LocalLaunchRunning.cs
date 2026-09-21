namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The launch's own launch steps ran and left the product standing (idea b9b09779, piece 5): the
/// processes it owns, the port it chose, and the address the reviewer was handed.
/// </summary>
/// <param name="Port">
/// The ephemeral port the launch picked, or null when the skill's launch command named no port to
/// substitute one into. Null is an observation — this launch did not choose the port — and never a
/// stand-in for one the platform failed to read.
/// </param>
/// <param name="Address">What the reviewer was actually told to open, which is the skill's own address line with the chosen port applied where it carried a loopback one.</param>
/// <param name="NextStepNumber">
/// Where the plan stands after this walk: one past the end when every step is done, or the human
/// step it is about to stop at when one sits after the launch steps. Carried here rather than
/// inferred, because a plan whose human steps all come first and one that has a login step after
/// the app is up both reach this event, and only the plan itself knows which.
/// </param>
public sealed record LocalLaunchRunning(
    Guid Id,
    Guid LaunchId,
    IReadOnlyList<LocalLaunchProcess> Processes,
    int? Port,
    string Address,
    int NextStepNumber,
    DateTimeOffset RunningAt);
