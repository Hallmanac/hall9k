using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Extensions;
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
            "Who this note is addressed to: node:<node-id> (one specific node, the full id — h9k "
            + "status only prints its short form; h9k project join prints the full id), "
            + "owner:<fingerprint> (every node that owner reads from), or the literal word project "
            + "(every node reading this project's messages). h9k owner show prints a root "
            + "fingerprint.")]
        public string To { get; init; } = string.Empty;

        [CommandOption("--about <ID>")]
        [Description(
            "A task or idea id this note is about, carried through as-is for the reader to act on "
            + "(h9k messages prints it back). Optional.")]
        public string? About { get; init; }

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Project this note is queued for: its name, an unambiguous fragment of it, or its full "
            + "id (h9k project list shows them all). Defaults to this node's only eligible project "
            + "(not archived, with a repository) when there is exactly one; with more than one "
            + "registered, this is required.")]
        public string? Project { get; init; }
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
        ProjectDetails project = await ResolveProjectAsync(session, settings.Project, cancellationToken);

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
            session, context.NodeId, project.Id, ownerRootFingerprint, audience, settings.About, MessageKind.Note,
            settings.Text, DateTimeOffset.UtcNow, cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[blue]Queued[/] to {audience.Value} for {project.Name} [dim](seq {envelope.Seq})[/] — the daemon's next message sweep sends it.");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Explicit <c>--project</c> resolves the same way every other command's own resolves one
    /// (name, fragment, or id — <see cref="ProjectResolver.ResolveAsync"/>), with no eligibility
    /// check of its own: a project not yet eligible for messaging still queues fine, since queueing
    /// never touches git (idea 202383dc, M1b) — only the daemon's own flush actually needs a
    /// repository, and by then the project may well have one. The default-selection path is
    /// narrower on purpose: defaulting to, or silently offering, a project the sweep can never
    /// actually flush would only ever strand a message.
    /// </summary>
    private static async Task<ProjectDetails> ResolveProjectAsync(
        IQuerySession session, string? projectOption, CancellationToken cancellationToken)
    {
        if (projectOption.IsNotBlank())
        {
            return await ProjectResolver.ResolveAsync(session, projectOption, cancellationToken);
        }

        IReadOnlyList<ProjectDetails> allProjects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        List<ProjectDetails> eligible = [.. allProjects
            .Where(candidate => candidate.IsEligibleForMessaging())
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)];

        return eligible switch
        {
            [ProjectDetails single] => single,
            [] => throw new DomainValidationException(
                "No eligible projects (not archived, with a repository) are registered yet. Register "
                + "one: h9k project add --name <name> --repo <path>."),
            _ => throw new DomainConflictException(
                $"This node has {eligible.Count} eligible projects — pass --project to say which one "
                + $"this message is for: {string.Join(", ", eligible.Select(candidate => candidate.Name))}."),
        };
    }
}
