using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class ProjectSetCommand : Hall9kAsyncCommand<ProjectSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description(
            "Project to change: its name, an unambiguous fragment of it, or its full id "
            + "(h9k project list shows them all). A fragment matching more than one project is "
            + "rejected as ambiguous rather than guessed at.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--skip-permissions <BOOL>")]
        [Description("Agents run with --dangerously-skip-permissions (log #9)")]
        public bool? SkipPermissions { get; init; }

        [CommandOption("--max-parallel <N>")]
        [Description(
            "How many of this project's agents may run at once (at least 1, default 3). The daemon "
            + "currently enforces its node-wide cap (DaemonOptions.MaxConcurrentAgentSessions); this per-project "
            + "ceiling is recorded and shown by h9k project show.")]
        public int? MaxParallelAgents { get; init; }

        [CommandOption("--verify <NAME=COMMAND>")]
        [Description("Verification gate, e.g. --verify \"test=dotnet test\"; repeat for more. Replaces the whole list.")]
        public string[] Verify { get; init; } = [];

        [CommandOption("--link <NAME=URL>")]
        [Description("Context link injected into agent prompts; repeat for more. Replaces the whole list.")]
        public string[] Links { get; init; } = [];

        [CommandOption("--commit-style <STYLE>")]
        [Description(
            "How follow-up runs land review fixes on the PR branch (Decisions Log #26): "
            + "'narrative' folds each fix into its owning commit (fixup + autosquash + force-with-lease "
            + "push, the AGENTS.md authored-history rule), 'append' stacks fix commits on top, "
            + "'default' clears the project override so the platform default applies "
            + "(DaemonOptions.DefaultCommitStyle; narrative unless configured otherwise)")]
        public string? CommitStyle { get; init; }

        [CommandOption("--model <MODEL>")]
        [Description(
            "Model every agent session on this project runs on unless a more specific level says "
            + "otherwise (Decisions Log #33): a tier alias (fable, opus, sonnet, haiku) or an exact "
            + "model id (claude-opus-5, claude-sonnet-5, or a context variant like claude-opus-5[[1m]]); "
            + "anything 'claude -p --model' accepts, except the word 'default'. "
            + "The chain is task override > the node's per-role default (DaemonOptions.ModelByRole) > "
            + "this project value > the platform default (DaemonOptions.DefaultModel), so a node that "
            + "sets a default for a role outranks this for that role's sessions. "
            + "'default' is not a model name: it clears the project override so the levels above and "
            + "below decide. An exact id is the stabler choice: an alias is re-pointed as new models ship")]
        public string? Model { get; init; }

        [CommandOption("--rerequest-review <ON|OFF|DEFAULT>")]
        [Description(
            "Whether closeout asks this project's reviewers for another pass once a fix follow-up has "
            + "pushed, so whoever raised the findings countersigns that they were addressed (Decisions "
            + "Log #62). 'on' buys that countersignature and spends review quota for it; 'off' lets a "
            + "pull request settle on the internal review, the in-thread replies, and CI — the guards "
            + "that already ran before the fixes were pushed. 'default' clears the project override so "
            + "the owner preference (h9k owner set --rerequest-review), else the node default "
            + "(DaemonOptions.DefaultReviewRerequest, off), decides. This project value outranks the "
            + "owner's: a repository is where the review culture lives. Bounded either way — "
            + "DaemonOptions.MaxReviewRerequestsAfterFixes caps the passes per task, after which the "
            + "pull request settles rather than looping on its own refinements.")]
        public string? RerequestReview { get; init; }

        [CommandOption("--jira <KEY>")]
        [Description(
            "Bind this project to a Jira board by its project key — the PROJ in PROJ-123. It is what "
            + "h9k task push-to-jira tells the agent to file new cards under. It is a default rather "
            + "than a law: h9k task link-jira records a card that landed on another board and says so "
            + "rather than refusing it, because the project's own routing rules are allowed to know "
            + "better than this binding does. 'none' clears the binding; a project with none still "
            + "publishes, and the agent is left to work out from the project's own skills where the "
            + "card belongs")]
        public string? JiraProjectKey { get; init; }

        [CommandOption("--home <PATH>")]
        [Description(
            "Where this project lives on disk — the directory holding the generated AGENTS.md, "
            + "repo/, ideas/, tasks/ and skills/ (default: ~/.hall9k/projects/<name>). This records "
            + "the location and re-renders the AGENTS.md there; it never moves anything and never "
            + "creates the shape. h9k project init is what makes a home real. 'none' (or an empty "
            + "value) clears the recorded home, which is how you say this project has no directory "
            + "on this machine.")]
        public string? Home { get; init; }

        [CommandOption("--repo <PATH>")]
        [Description(
            "The local repository the daemon cuts worktrees from. Ordinarily this follows the home "
            + "(h9k project init points it at <home>/repo/<name>.git); set it by hand when a "
            + "relocation moved the clone and the recorded path has to catch up.")]
        public string? RepositoryPath { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails details = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        ProjectAggregate project = (await session.Events.AggregateStreamAsync<ProjectAggregate>(details.Id, token: cancellationToken))!;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // 'none' clears the home the same way it clears the Jira binding, and for the same
        // reason it is mapped here: ProjectHome.Parse would take it for a relative path. An
        // empty value clears it too, which is what ProjectHome already means by blank — and
        // resolving one against the current directory would instead record wherever the shell
        // happened to be standing. Path.GetFullPath throws on it outright.
        Optional<ProjectHome> homeDirectory = settings.Home is { } homePath
            ? Optional<ProjectHome>.Of(homePath.IsBlank() || ClearingWord(homePath)
                ? ProjectHome.None
                : ProjectHome.Parse(Path.GetFullPath(homePath)))
            : Optional<ProjectHome>.None;

        // Blank goes through untouched so the decider gives its own refusal — there is no
        // clearing a repository path — rather than Path.GetFullPath throwing ArgumentException
        // on the way, which is an unhandled stack trace instead of a rule the caller can read.
        Optional<string> repositoryPath = settings.RepositoryPath is { } repoPath
            ? Optional<string>.Of(repoPath.IsBlank() ? repoPath : Path.GetFullPath(repoPath))
            : Optional<string>.None;

        // --home and --repo are the second documented way a home and a repository path get
        // recorded, so they run the same one-home-one-project check h9k project add and h9k
        // project init run: without it this command records the collision and then the render
        // below immediately overwrites the other project's AGENTS.md, which is the exact damage
        // the check exists to prevent. Only what this invocation is actually changing is checked
        // — a blank never matches anything (ProjectHomePaths.SameDirectory) — so an option nobody
        // passed cannot refuse a change to the other one. Origin incident (2026-08-23): the
        // second cycle of this branch's pre-PR review walked h9k project set beta --home
        // <alpha's home> straight through, with every line reporting success.
        await ProjectHomeClaims.EnsureUnclaimedAsync(
            session,
            details.Id,
            homeDirectory is { HasValue: true, Value: { } claimedHome } ? claimedHome.Value : string.Empty,
            repositoryPath is { HasValue: true, Value: { } claimedRepository } ? claimedRepository : string.Empty,
            cancellationToken);

        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            project,
            verifyCommands: settings.Verify.Length > 0
                ? Optional<IReadOnlyList<VerifyCommand>>.Of([.. settings.Verify.Select(ParseVerify)])
                : Optional<IReadOnlyList<VerifyCommand>>.None,
            skipPermissions: settings.SkipPermissions is { } skip ? skip : Optional<bool>.None,
            maxParallelAgents: settings.MaxParallelAgents is { } max ? max : Optional<int>.None,
            contextLinks: settings.Links.Length > 0
                ? Optional<IReadOnlyList<ContextLink>>.Of([.. settings.Links.Select(ParseLink)])
                : Optional<IReadOnlyList<ContextLink>>.None,
            DateTimeOffset.UtcNow,
            context.OwnerId,
            commitStyle: settings.CommitStyle is { } commitStyle
                ? Optional<CommitStyle>.Of(ParseCommitStyle(commitStyle))
                : Optional<CommitStyle>.None,
            // 'default' is Unknown to AgentModel.FromInput at every level, so the clearing
            // idiom this option documents needs no special case here (Decisions Log #33).
            model: settings.Model is { } model
                ? Optional<AgentModel>.Of(AgentModel.FromInput(model))
                : Optional<AgentModel>.None,
            reviewRerequest: settings.RerequestReview is { } rerequestReview
                ? Optional<ReviewRerequestPolicy>.Of(ReviewRerequestOption.Parse(rerequestReview))
                : Optional<ReviewRerequestPolicy>.None,
            // 'none' is how a binding is cleared, and it reaches JiraProjectKey.Parse as the word
            // rather than as a key — which would be a perfectly legal one — so it is mapped here,
            // beside the option that documents it, exactly as --commit-style maps 'default'.
            jiraProjectKey: settings.JiraProjectKey is { } jiraKey
                ? Optional<JiraProjectKey>.Of(ClearingWord(jiraKey)
                    ? JiraProjectKey.None
                    : JiraProjectKey.Parse(jiraKey))
                : Optional<JiraProjectKey>.None,
            homeDirectory: homeDirectory,
            repositoryPath: repositoryPath);

        session.Events.Append(details.Id, changed);
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"project-changed:{details.Id}", cancellationToken);

        AnsiConsole.MarkupLine($"[green]Project '{details.Name.EscapeMarkup()}' settings updated.[/]");

        // The home's AGENTS.md is a render of exactly the facts this command changes (the Jira
        // binding and the remote drive its tool list), so it is rewritten here rather than left
        // to drift until somebody re-runs project init. Best effort by design: a home that is
        // recorded but not yet materialised has nothing to write into, and that is not a reason
        // to fail a settings change that already landed.
        ProjectDetails updated = (await session.LoadAsync<ProjectDetails>(details.Id, cancellationToken))!;
        if (updated.HomeDirectory.HasValue && Directory.Exists(updated.HomeDirectory.Value))
        {
            ProjectHomeRecipe.Report([ProjectAgentsDocument.Write(updated.HomeDirectory.Value, updated)]);
        }
        else if (updated.HomeDirectory.HasValue)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{updated.HomeDirectory.Value.EscapeMarkup()} does not exist yet[/] — create the shape "
                + $"there with: h9k project init {details.Name.EscapeMarkup()}");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The word that clears a binding rather than setting one. Spelled out here because "none" is
    /// a well-formed Jira project key and somebody's board could genuinely be called NONE — in
    /// which case this command cannot bind it, which is a real limitation and a cheap one, and far
    /// better than an option with no way to undo it.
    /// </summary>
    private static bool ClearingWord(string value) =>
        value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

    private static VerifyCommand ParseVerify(string value)
    {
        int separator = value.IndexOf('=');
        return separator <= 0
            ? throw new DomainValidationException($"--verify expects name=command, got '{value}'.")
            : new VerifyCommand(value[..separator].Trim(), value[(separator + 1)..].Trim());
    }

    private static CommitStyle ParseCommitStyle(string value) => value.Trim().ToLowerInvariant() switch
    {
        "narrative" => CommitStyle.Narrative,
        "append" => CommitStyle.Append,
        "default" => CommitStyle.Unknown,
        _ => throw new DomainValidationException(
            $"--commit-style expects narrative, append, or default; got '{value}'. Narrative folds "
            + "follow-up fixes into their owning commits (fixup + autosquash, the AGENTS.md "
            + "authored-history rule); append stacks fix commits on top of the existing history; "
            + "default clears the project override so the platform default applies."),
    };

    internal static ContextLink ParseLink(string value)
    {
        int separator = value.IndexOf('=');
        if (separator <= 0)
        {
            throw new DomainValidationException($"--link expects name=url, got '{value}'.");
        }

        string name = value[..separator].Trim();
        string url = value[(separator + 1)..].Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? link))
        {
            return new ContextLink(name, link);
        }

        string suggestion = string.IsNullOrEmpty(url) || url.Contains("://", StringComparison.Ordinal)
            ? $"--link \"{name}=https://example.com/wiki\""
            : $"--link \"{name}=https://{url}\"";
        throw new DomainValidationException(
            $"--link '{name}={url}' is not a URL this can record. Pass an absolute URL with a "
            + $"scheme, e.g. {suggestion}.");
    }
}
