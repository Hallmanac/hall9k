namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// What one <see cref="IProcessElevationReader.Read"/> saw of a process's token: elevated, not
/// elevated, or <see cref="Unknown"/> when the token could not be read (another account's process,
/// a pid already gone, or any platform other than Windows). Never persisted: it is read live by
/// whichever surface asks, so this is the in-process outcome an enum is for.
/// </summary>
public enum ProcessElevation
{
    /// <summary>The token could not be read; nothing is concluded from it.</summary>
    Unknown,

    /// <summary>The token's <c>TokenElevation</c> is zero: a filtered or standard token.</summary>
    NotElevated,

    /// <summary>The token's <c>TokenElevation</c> is nonzero: the process runs as an administrator.</summary>
    Elevated,
}
