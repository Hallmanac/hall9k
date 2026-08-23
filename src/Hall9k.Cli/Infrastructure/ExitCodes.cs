namespace Hall9k.Cli.Infrastructure;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Validation = 64;

    /// <summary>
    /// The command line itself was wrong — a missing argument, an unknown command, a settings rule
    /// broken before any command ran. The same number as <see cref="Validation"/> on purpose: both
    /// are sysexits' EX_USAGE, and the caller's fix is the same shape either way. Named separately
    /// because the two failures come from opposite sides of the boundary, and a reader tracing one
    /// should not have to decide whether the other was meant.
    /// </summary>
    public const int Usage = 64;
    public const int NotFound = 66;
    public const int Conflict = 69;
    public const int BusinessRule = 70;
}
