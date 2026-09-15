using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Queues an envelope in this node's own store (idea 202383dc, M1b) — never touches git, never
/// waits on a network: the daemon's own message sweep is what actually lands it in the outbox on
/// its own cadence (15 to 25 seconds while there is something to send, sooner still on the tick
/// right after a push). Retiring <c>notes/node-mailbox.md</c>'s GitHub-issue workaround for
/// node-to-node traffic is exactly what this command and its daemon-side flush replace.
/// </summary>
public sealed class MessageSendCommand : Hall9kAsyncCommand<MessageSendCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TEXT>")]
        [Description("The note's own body, quoted — the whole message; there is no subject line.")]
        public string Text { get; init; } = string.Empty;

        [CommandOption("--to <AUDIENCE>")]
        [Description(
            "Who this note is addressed to: node:<node-id> (one specific node), owner:<fingerprint> "
            + "(every node that owner reads from), or the literal word project (every node reading "
            + "this project's messages). h9k status prints this node's own id; h9k owner show prints "
            + "a root fingerprint.")]
        public string To { get; init; } = string.Empty;

        [CommandOption("--about <ID>")]
        [Description(
            "A task or idea id this note is about, carried through as-is for the reader to act on "
            + "(h9k messages prints it back). Optional.")]
        public string? About { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    /// <summary>
    /// The whole command body, with its session handed in rather than opened through
    /// <see cref="CliStore.Open()"/> — this codebase's CLI commands have no other test seam (the
    /// same shape <c>PullRequestReviewCommand.RunAsync</c> and
    /// <c>ReviewResolveCommand.ResolvePrReviewAsync</c> already take), and a round trip test that
    /// only re-implements this body inline never actually exercises it.
    /// </summary>
    internal static async Task<int> RunAsync(IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Text.IsBlank())
        {
            throw new DomainValidationException("A message needs a body — pass the text as the command's own argument.");
        }

        if (settings.To.IsBlank())
        {
            throw new DomainValidationException("--to is required: node:<node-id>, owner:<fingerprint>, or project.");
        }

        MessageAudience audience = MessageAudience.Parse(settings.To);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);
        if (ownerRootFingerprint is null)
        {
            throw new DomainValidationException(
                "This node's owner has not claimed a root fingerprint yet, so an envelope's own "
                + "from-owner field has nothing to carry. Run h9k project join first.");
        }

        MessageEnvelopeV1 envelope = await MessageOutbox.QueueAsync(
            session, context.NodeId, ownerRootFingerprint, audience, settings.About, MessageKind.Note,
            settings.Text, DateTimeOffset.UtcNow, cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[blue]Queued[/] to {audience.Value} [dim](seq {envelope.Seq})[/] — the daemon's next message sweep sends it.");
        return ExitCodes.Ok;
    }
}
