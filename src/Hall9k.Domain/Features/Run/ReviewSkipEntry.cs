namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Which of <c>h9k task skip-to-pr</c>'s two doors a run is at. In-process only and never
/// persisted, so an enum rather than a value object (the same call <see cref="ReviewPhase"/> makes).
/// </summary>
public enum ReviewSkipEntry
{
    /// <summary>The claim is still building: nothing has been delivered, so the skip delivers first.</summary>
    FromBuild,

    /// <summary>The run is parked in its review loop, at an interactive boundary or any other park.</summary>
    FromPark,
}
