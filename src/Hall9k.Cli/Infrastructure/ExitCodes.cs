namespace Hall9k.Cli.Infrastructure;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Validation = 64;
    public const int NotFound = 66;
    public const int Conflict = 69;
    public const int BusinessRule = 70;
}
