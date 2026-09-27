using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The one reading of a <c>--invite</c> option's value, shared by <c>h9k project join</c> and
/// <c>h9k project add</c> (2026-09-26/27 security review, idea 6be68ee2): an invite secret passed
/// directly on argv lands in shell history and process listings on most platforms, so
/// <c>--invite -</c> reads it from stdin instead, and passing the secret itself still works but
/// prints a one-line warning rather than silently accepting it. The interactive on-the-spot prompt
/// (<c>ProjectJoinCommand.PromptForInviteTokenFromConsole</c>) has its own <c>.Secret()</c> masking
/// and never reaches this method at all.
/// </summary>
public static class InviteTokenInput
{
    public static async Task<string?> ResolveAsync(string? rawInvite, CancellationToken cancellationToken)
    {
        if (rawInvite == "-")
        {
            string? line = await Console.In.ReadLineAsync(cancellationToken);
            // Refused rather than silently treated as "no --invite at all": the caller explicitly
            // asked for the secret to come from stdin, so an empty pipe or an immediate EOF must
            // not fall through into the plain, --invite-less join path — on a project with no root
            // of its own yet, that path self-establishes a brand-new root instead of the join the
            // caller actually asked for (independent self-review, adversarial lens, high).
            return line?.Trim() is { Length: > 0 } trimmed
                ? trimmed
                : throw new DomainValidationException("--invite - was given but stdin carried no secret to read.");
        }

        if (rawInvite.IsNotBlank())
        {
            AnsiConsole.MarkupLine(
                "[yellow]--invite on the command line can leave the secret in shell history and process "
                + "listings — pass --invite - to read it from stdin instead.[/]");
        }

        return rawInvite;
    }
}
