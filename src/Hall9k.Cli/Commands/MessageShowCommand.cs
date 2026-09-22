using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One received note in full: who sent it, which project's copy it is, and the whole body rather
/// than the sixty characters <c>h9k messages</c> has room for or the hundred and sixty the
/// orchestrator feed quotes. Until this existed a window that saw a note arrive had no read path
/// to the rest of it at all and went to Postgres by hand for the text (observed 2026-09-22 by the
/// Mac project orchestrator, on two notes from the Windows node, one of them marked for a human).
/// <para>
/// Read-only by construction: it takes an <see cref="IQuerySession"/>, so there is no session here
/// that could append, and reading a note is deliberately not handling it — <c>h9k message
/// handle</c> stays the one explicit act that marks one read (idea 202383dc, M1b).
/// </para>
/// </summary>
public sealed class MessageShowCommand : Hall9kAsyncCommand<MessageShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("The message's own id, or an unambiguous fragment of one, as h9k messages and the orchestrator feed both print it.")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Narrows the id fragment match to one project's own received messages: its name, an "
            + "unambiguous fragment of it, or its id (h9k project list shows them all). Only needed "
            + "when the identical fragment matches messages from more than one project.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();
        return await RunAsync(session, settings, cancellationToken);
    }

    /// <summary>
    /// The whole command body, with its session handed in rather than opened through
    /// <see cref="CliStore.Open()"/> — the same test seam
    /// <see cref="MessageHandleCommand.RunAsync"/> takes, and for the same reason.
    /// </summary>
    internal static async Task<int> RunAsync(IQuerySession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Id.IsBlank())
        {
            throw new DomainValidationException("An id is required — h9k messages prints one for every received message.");
        }

        Guid? projectId = null;
        if (settings.Project.IsNotBlank())
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
            projectId = project.Id;
        }

        MessageDetails details = await MessageIdResolver.ResolveReceivedAsync(session, settings.Id, projectId, cancellationToken);
        ProjectDetails? owningProject = details.ProjectId == Guid.Empty
            ? null
            : await session.LoadAsync<ProjectDetails>(details.ProjectId, cancellationToken);

        foreach (string line in Lines(details, owningProject?.Name, TimeZoneInfo.Local))
        {
            // Console.Out, never AnsiConsole: Spectre word-wraps at eighty columns whenever stdout
            // is not a TTY, which is every call a window or a script makes, and a body rewrapped
            // at eighty is a body whose own line structure the reader can no longer trust. Writing
            // raw also takes markup parsing out of the picture, so a note containing square
            // brackets reaches the terminal as itself rather than as a Spectre parse error.
            Console.Out.WriteLine(line);
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The printed note, line by line, with no database in it: this codebase's CLI commands are
    /// otherwise driven through <c>RunAsync</c> against a real Postgres, and the shape of this
    /// output is worth pinning as a golden on its own.
    /// <para>
    /// Every header field is folded to one line (<see cref="ExternalText.OneLine"/>) and the body
    /// keeps its own layout (<see cref="ExternalText.ForTerminal"/>). Both halves of that matter:
    /// a kind or an about-id is written by whoever sent the note, so one free to emit a newline
    /// could print a header row of its own choosing, while the body is the whole reason to run
    /// this command and its paragraphs have to survive.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Lines(MessageDetails message, string? projectName, TimeZoneInfo zone)
    {
        List<string> lines =
        [
            Field("From", Sender(message)),
            Field("Project", ProjectField(message, projectName)),
            Field("Kind", ExternalText.OneLine(message.Kind ?? "not recorded")),
        ];

        if (message.About is { Length: > 0 } about)
        {
            lines.Add(Field("About", ExternalText.OneLine(about)));
        }

        lines.Add(Field("Sent", When(message.SentAt, zone)));
        lines.Add(Field("Received", When(message.ReceivedAt, zone)));
        lines.Add(Field("Status", message.HandledAt is { } handledAt
            ? $"handled at {When(handledAt, zone)}"
            : "unread"));

        lines.Add(string.Empty);
        string body = ExternalText.ForTerminal(message.Body ?? string.Empty);
        lines.AddRange(body.IsBlank() ? ["(no body)"] : BodyLines(body));
        return lines;
    }

    private static string Field(string label, string value) => $"{label,-9}{value}";

    /// <summary>
    /// Who sent it: the owner's own cross-node root fingerprint cut to the same short prefix the
    /// feed and <c>h9k status</c>'s own foreign-holder line already print, alongside the sending
    /// node's short id. Both, rather than either — a foreign node's friendly name never
    /// replicates, so these two are the whole of what this node can honestly say about a sender.
    /// </summary>
    private static string Sender(MessageDetails message)
    {
        string fingerprint = ExternalText.OneLine(message.FromOwnerFingerprint ?? string.Empty);
        string owner = fingerprint.IsBlank()
            ? "owner not recorded"
            : fingerprint[..Math.Min(12, fingerprint.Length)];
        return $"{owner} (node {DomainId.Short(message.FromNodeId)})";
    }

    /// <summary>
    /// Which project's copy this is, named rather than left as a bare id, so a fragment that
    /// resolved against a different project's note than the caller meant is obvious on sight. The
    /// CLI has no notion of a calling window's own project, so naming the project is the whole of
    /// the guard available here; <c>--project</c> is the rest of it, exactly as on
    /// <c>h9k message handle</c>.
    /// </summary>
    private static string ProjectField(MessageDetails message, string? projectName) =>
        message.ProjectId == Guid.Empty
            ? "none recorded (queued before messages were project-scoped)"
            : projectName.IsNotBlank()
                ? $"{ExternalText.OneLine(projectName)} ({DomainId.Short(message.ProjectId)})"
                : $"{DomainId.Short(message.ProjectId)} (no project of that id on this node)";

    private static string When(DateTimeOffset? at, TimeZoneInfo zone) =>
        at is { } value
            ? TimeZoneInfo.ConvertTime(value, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "not recorded";

    /// <summary>
    /// The sanitised body split on the line breaks <see cref="ExternalText.ForTerminal"/> keeps —
    /// which is the line feed, and the carriage return in front of one, since a lone carriage
    /// return is dropped there as something a terminal obeys rather than shows.
    /// </summary>
    private static IEnumerable<string> BodyLines(string body) =>
        body.Split('\n').Select(line => line.TrimEnd('\r'));
}
