namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The two privilege levels a Windows logon task's principal can carry, under the three names
/// each one answers to: the Task Scheduler XML value the registration writes, the PowerShell
/// <c>RunLevel</c> enum member <c>Get-ScheduledTask</c> reads back (unlocalized, unlike any
/// schtasks display text), and the word an operator types after <c>--run-level</c>.
/// </summary>
public sealed record AutostartRunLevel
{
    /// <summary>Registered as <c>HighestAvailable</c>: the daemon, and every agent it dispatches, runs as an administrator.</summary>
    public static readonly AutostartRunLevel Highest = new("HighestAvailable", "Highest", "highest", "elevated");

    /// <summary>Registered as <c>LeastPrivilege</c>: the daemon runs with the signed-in user's ordinary token.</summary>
    public static readonly AutostartRunLevel Limited = new("LeastPrivilege", "Limited", "limited", "not elevated");

    private AutostartRunLevel(string taskXmlValue, string powerShellName, string optionValue, string description)
    {
        TaskXmlValue = taskXmlValue;
        PowerShellName = powerShellName;
        OptionValue = optionValue;
        Description = description;
    }

    /// <summary>The <c>&lt;RunLevel&gt;</c> value in the task's XML.</summary>
    public string TaskXmlValue { get; }

    /// <summary>The <c>RunLevel</c> member name <c>Get-ScheduledTask</c> prints for a task at this level.</summary>
    public string PowerShellName { get; }

    /// <summary>What <c>--run-level</c> takes for this level.</summary>
    public string OptionValue { get; }

    /// <summary>How the success line and the refusals describe a daemon at this level.</summary>
    public string Description { get; }

    /// <summary>The level a <c>--run-level</c> value names, or <c>null</c> for anything else.</summary>
    public static AutostartRunLevel? FromOptionValue(string value) =>
        value.Trim() switch
        {
            var text when text.Equals(Highest.OptionValue, StringComparison.OrdinalIgnoreCase) => Highest,
            var text when text.Equals(Limited.OptionValue, StringComparison.OrdinalIgnoreCase) => Limited,
            _ => null,
        };

    /// <summary>The level <c>Get-ScheduledTask</c>'s <c>Principal.RunLevel.ToString()</c> output names, or <c>null</c> for anything else.</summary>
    public static AutostartRunLevel? FromPowerShellOutput(string output) =>
        output.Trim() switch
        {
            var text when text.Equals(Highest.PowerShellName, StringComparison.Ordinal) => Highest,
            var text when text.Equals(Limited.PowerShellName, StringComparison.Ordinal) => Limited,
            _ => null,
        };
}
