using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The one reading of an effort option's value, shared by every command that takes one
/// (<c>h9k config set --effort</c> and its per-role siblings, <c>h9k project set --effort</c>,
/// <c>h9k task revise --effort</c>), so all of them accept the same four names and the same clearing
/// word and refuse anything else with the same message.
/// </summary>
public static class EffortInput
{
    /// <summary>
    /// The level <paramref name="input"/> names, or <see cref="AgentEffort.Unknown"/> for the clearing word
    /// 'default', which means "no opinion at this level, ask the next one down". Anything else is refused
    /// with the four accepted names quoted, so an unrecognized word never reaches a stored setting.
    /// </summary>
    public static AgentEffort Parse(string flag, string input)
    {
        AgentEffort effort = AgentEffort.FromInput(input);
        if (effort.IsWellFormed || string.Equals(input.Trim(), AgentEffort.ClearingWord, StringComparison.OrdinalIgnoreCase))
        {
            return effort;
        }

        throw new DomainValidationException(
            $"{flag} must be one of {AgentEffort.DescribeAccepted()}, or '{AgentEffort.ClearingWord}' "
            + "to clear it so the next level of the effort chain decides again (task, then project, then the "
            + "node's value for the session's role, then the node-wide --effort, then each model's own default). "
            + "It is the reasoning effort level a dispatched session runs at.");
    }
}
