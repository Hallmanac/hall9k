namespace Hall9k.Cli.DaemonControl;

/// <summary>What <see cref="WindowsDaemonAutostart"/> registered: the variable names it recorded and the run level the task was made at.</summary>
public sealed record WindowsAutostartRegistration(IReadOnlyList<string> RecordedVariables, AutostartRunLevel RunLevel);
