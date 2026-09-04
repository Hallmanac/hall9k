using Hall9k.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// <para>
/// The whole <c>h9k</c> command tree, in one place and separate from the entry point, because the
/// <c>--help</c> tree is a first-class interface rather than a by-product of <c>Main</c> (AGENTS.md,
/// CLI command standards): it is how an agent discovers what the platform can do, so it is something
/// tests get to walk and something a failing command line gets to quote back.
/// </para>
/// <para>
/// Two callers build the same tree from here. <c>Program</c> builds the app that actually runs, and
/// <see cref="UsageError"/> builds a second, console-redirected one purely to render the help for a
/// command line that never reached a command — which is why every example below has to stay a real
/// invocation: it is the correction an agent reads after it gets the call wrong.
/// </para>
/// </summary>
public static class CliCommandTree
{
    /// <summary>The application name every usage line and example is written against.</summary>
    public const string ApplicationName = "h9k";

    /// <summary>
    /// The narrowest the help is ever rendered at, whatever the terminal says.
    /// </summary>
    /// <remarks>
    /// Spectre wraps to the console width, and a wrapped example is not a command: at the default
    /// 80 columns a redirected <c>h9k task resolve --help</c> prints its example broken after
    /// <c>--reason "Work merged as PR #7; only the daemon's</c>, with the rest on an unindented
    /// line of its own, so an agent pasting what it was just handed gets an unterminated quote.
    /// Eleven of the tree's examples are longer than 80 columns and nine of those are quoted, and
    /// a redirected stdout is exactly how a dispatched agent reads help. The longest example
    /// renders at 141 columns including its indent; this leaves room for a longer one, and
    /// <c>CommandTreeHelpTests</c> walks the tree at exactly this width so an example that outgrows
    /// it fails there rather than shipping wrapped.
    /// </remarks>
    internal const int MinimumHelpWidth = 160;

    /// <summary>
    /// Register the application's identity, its exception posture, and every command.
    /// </summary>
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName(ApplicationName);
        config.SetApplicationVersion(CliVersion.Current);
        config.PropagateExceptions();

        // The help is rendered at least MinimumHelpWidth wide, here rather than at either call site,
        // because the width belongs to the examples registered below rather than to whoever printed
        // them: an example the renderer hard-breaks is unpasteable on the ordinary --help path as
        // surely as on the usage-error one, and that path is the one a caller reaches first.
        // UsageError configures its own console after this and so replaces it, since a refusal is
        // written to stderr as plain text; both floor the width the same way.
        config.ConfigureConsole(HelpConsole());

        // Root examples, stated rather than inherited. Left alone, Spectre fills this block with
        // the first handful of examples it finds walking the tree, which is registration order and
        // not a story. These five are the orchestrator's loop (AGENTS.md, the orchestrator window):
        // see what needs you, draft the work, gate it, dispatch it, read one task.
        config.AddExample("status");
        config.AddExample("task", "add", "--project", "hall9k", "--objective", "\"Add the project browse surface\"",
            "--criteria", "\"h9k project list shows one row per project\"");
        config.AddExample("task", "publish", "28b19893", "--assign");
        config.AddExample("task", "list", "--state", "needs-you");
        config.AddExample("task", "show", "28b19893");

        config.AddBranch("project", project =>
        {
            project.SetDescription("Manage projects: register them, give them a home on disk, browse them, inspect one");
            project.AddCommand<ProjectAddCommand>("add")
                .WithDescription(
                    "Register a project and create its home directory: the generated AGENTS.md, repo/ "
                    + "bare-cloned from the remote with a dev/ worktree on the primary branch, ideas/, "
                    + "tasks/, skills/ seeded from the install's canonical set, and the .claude/ adapter. "
                    + "Platform code end to end — no agent, nothing to review, the same shape on every "
                    + "machine. The location is yours (--home); the shape is the platform's.")
                .WithExample("project", "add", "--name", "hall9k", "--repo-url", "https://github.com/Hallmanac/hall9k")
                .WithExample("project", "add", "--name", "hall9k", "--repo-url", "https://github.com/Hallmanac/hall9k",
                    "--home", "~/work/hall9k", "--base-branch", "main");
            project.AddCommand<ProjectInitCommand>("init")
                .WithDescription(
                    "Create (or repair) a registered project's home directory. The adopt path for a "
                    + "project that has none, and the repair path for one that is incomplete: every step "
                    + "is idempotent, so an existing bare clone is reported and left alone rather than "
                    + "re-cloned. repo/ always materialises fresh from the recorded remote — git is "
                    + "distributed, so a clone elsewhere on this machine is inconsequential, and moving "
                    + "work out of an old location is a separate, deliberate act.")
                .WithExample("project", "init", "hall9k")
                .WithExample("project", "init", "hall9k", "--home", "~/work/hall9k");
            project.AddCommand<ProjectListCommand>("list")
                .WithDescription(
                    "Every registered project, one row each, with its tasks counted by attention bucket "
                    + "(needs you, stalled, active, in review, queued, done, closed). The counts are "
                    + "single-assignment, so a row sums to the project's task count — this is where you look "
                    + "to see which project is asking for something.")
                .WithExample("project", "list");
            project.AddCommand<ProjectShowCommand>("show")
                .WithDescription(
                    "One project in one pane: how it is registered (repository, base branch, connection binding, "
                    + "owner) and every setting the daemon runs it by (skip-permissions, verify gates, parallelism, "
                    + "commit style, context links), plus its task rollup and newest tasks. Takes the project name, "
                    + "an unambiguous fragment of it, or its id.")
                .WithExample("project", "show", "hall9k")
                .WithExample("project", "show", "hall");
            project.AddCommand<ProjectSetCommand>("set")
                .WithDescription(
                    "Change project settings: verify gates, skip-permissions, links, parallelism, "
                    + "commit style, agent model, review re-requests, the Jira board, the backlog "
                    + "policy that tracks every published task (none, github-issues, jira) and its "
                    + "routing guidance, the branch-name template task branches are cut under, and "
                    + "where the project lives on disk. Any change that the home's generated "
                    + "AGENTS.md renders rewrites that file.")
                .WithExample("project", "set", "hall9k", "--commit-style", "narrative")
                .WithExample("project", "set", "hall9k", "--home", "~/.hall9k/projects/hall9k")
                .WithExample("project", "set", "hall9k", "--model", "claude-opus-5")
                .WithExample("project", "set", "hall9k", "--rerequest-review", "on")
                .WithExample("project", "set", "hall9k", "--jira", "PROJ")
                .WithExample("project", "set", "hall9k", "--backlog", "github-issues")
                .WithExample("project", "set", "hall9k", "--branch-template", "{key}-{slug}")
                .WithExample(
                    "project", "set", "hall9k", "--review-stage-composition", "none", "--accept-reduced-review");
        });

        config.AddBranch("owner", owner =>
        {
            owner.SetDescription("The human every node and project belongs to (PLAN.md §6.2), and their standing preferences");
            owner.AddCommand<OwnerShowCommand>("show")
                .WithDescription(
                    "One owner: identity, the projects registered to them, and every preference their work "
                    + "runs by. Takes the owner's name, an unambiguous fragment of it or their email, or their "
                    + "id — omit it entirely when this platform has one owner.")
                .WithExample("owner", "show")
                .WithExample("owner", "show", "brian");
            owner.AddCommand<OwnerSetCommand>("set")
                .WithDescription(
                    "Change an owner's standing preferences: whether closeout asks a pull request's reviewers "
                    + "to look again once a fix follow-up has pushed (Decisions Log #62). A project setting "
                    + "outranks this; the node default sits under both.")
                .WithExample("owner", "set", "--rerequest-review", "on")
                .WithExample("owner", "set", "brian", "--rerequest-review", "default");
        });

        config.AddBranch("connection", connection =>
        {
            connection.SetDescription(
                "External accounts this install can reach. Access is modelled as a list of connections — "
                + "provider, account, credential reference — and projects bind to one, never to \"the "
                + "machine's GitHub\" (PLAN.md §10). A connection records WHERE its credential lives; the "
                + "secret itself never reaches an event payload.");
            connection.AddBranch("add", add =>
            {
                add.SetDescription("Register an external account");
                add.AddCommand<ConnectionAddJiraCommand>("jira")
                    .WithDescription(
                        "Register the Jira Cloud account Hall9k reads AND writes cards through (site, email, "
                        + "API token). The credentials are verified against the site before anything is "
                        + "recorded, and the token is stored by reference — an environment variable, a macOS "
                        + "keychain item, or a file under ~/.hall9k/credentials readable by you alone. Running "
                        + "it again replaces the existing connection, which is how a rotated token is applied. "
                        + "This one credential covers both directions: reading a card for import or "
                        + "verification, and every Jira write (create, update, comment), which still goes "
                        + "through a separate path (Decisions Log #102, #114) — an agent run composes the "
                        + "payload, and `h9k task write-jira` is the sole executor, submitting it against the "
                        + "Jira Cloud REST API with this same credential. Run `h9k doctor` to check the "
                        + "connection is usable for writes, or watch for a needs-you row if Jira ever rejects "
                        + "it. Hall9k never transitions a card — that stays a team's own workflow in Jira.")
                    .WithExample("connection", "add", "jira", "--site", "https://your-org.atlassian.net",
                        "--email", "you@example.com")
                    .WithExample("connection", "add", "jira", "--site", "https://your-org.atlassian.net",
                        "--email", "you@example.com", "--token-env", "JIRA_API_TOKEN");
            });
            connection.AddCommand<ConnectionListCommand>("list")
                .WithDescription(
                    "Every registered connection with where its credential lives and how many projects bind "
                    + "to it. Reads what is recorded and calls nothing, so it cannot fail because a site is "
                    + "unreachable.")
                .WithExample("connection", "list");
        });

        config.AddBranch("pr", pullRequest =>
        {
            pullRequest.SetDescription("Work with a task's pull request");
            pullRequest.AddCommand<PullRequestResolveCommand>("resolve")
                .WithDescription(
                    "Dispatch a follow-up run onto a done task's existing PR branch to resolve review feedback "
                    + "(fix failing CI with --checks, or rebase onto its base branch with --rebase). Also resets "
                    + "the closeout monitor's automatic retry budget.")
                .WithExample("pr", "resolve", "28b19893")
                .WithExample("pr", "resolve", "28b19893", "--checks")
                .WithExample("pr", "resolve", "28b19893", "--rebase");
        });

        config.AddBranch("review", review =>
        {
            review.SetDescription("Work with the pre-PR review loop (PLAN.md log #24)");
            review.AddCommand<ReviewResolveCommand>("resolve")
                .WithDescription(
                    "Record your verdict on a review-parked run: --merge-ready runs one mandatory full-scope "
                    + "verification gate over the fix (unless this tip was already gated at full scope) and "
                    + "the pull request opens only if it passes (pair it with --reason to say why, e.g. the "
                    + "evidence that dismissed a finding), "
                    + "--needs-fixes <reason> dispatches a fix session (and, like pr resolve, restores the "
                    + "automatic fix budget). Both verdicts and their reasons are recorded on the task and "
                    + "carried into every later fresh-context review pass, but they are not read the same way: "
                    + "a --merge-ready reason is a dismissal, so a later pass treats the question as settled and "
                    + "does not re-raise it without new evidence; a --needs-fixes reason confirms the defect is "
                    + "real, so a later pass checks whether the fix actually landed and reports it again if it "
                    + "did not — except on a thread-dispute park, which settles a disputed thread rather than a "
                    + "review finding and is not carried forward this way. The park reason and findings files "
                    + "name what needs judging.")
                .WithExample("review", "resolve", "28b19893", "--merge-ready")
                .WithExample("review", "resolve", "28b19893", "--merge-ready", "--reason", "\"False positive - confirmed via git log\"")
                .WithExample("review", "resolve", "28b19893", "--needs-fixes", "\"The limiter reset finding is real; fix it as the reviewer described\"");
        });

        config.AddCommand<StatusCommand>("status")
            .WithDescription(
                "The attention pane: what needs you, what has gone quiet, what is running — bounded and "
                + "glanceable, with everything else counted in the header. Browsing lives under the nouns "
                + "(h9k task list, h9k project list); this answers \"what should I look at right now\".")
            .WithExample("status");
        config.AddCommand<LogsCommand>("logs")
            .WithDescription(
                "A run's transcript, rendered from the stream-json the agent wrote (or --raw for the "
                + "stream-json itself). Defaults to the task's latest run, which is the one you want "
                + "after a failure; --run reaches an earlier one, and h9k task show lists their ids. "
                + "This is the log dive h9k status is meant to save you, so reach for it when the pane "
                + "has already told you which task to look at.")
            .WithExample("logs", "28b19893")
            .WithExample("logs", "28b19893", "--raw")
            .WithExample("logs", "28b19893", "--run", "01a0248c-87e1-727f-a721-e1635e5ef65f");

        config.AddCommand<DoctorCommand>("doctor")
            .WithDescription(
                "Diagnose the database situation: is a connection string configured, is it reachable "
                + "(nothing listening vs. credentials rejected are named separately), and is the schema "
                + "there — offering to fix what it can along the way (starting Hall9k's own Postgres, "
                + "creating the schema). The same check any other command runs automatically the moment "
                + "it cannot reach a database, available here on demand (Decisions Log #58, #73). "
                + "--yes remediates non-interactively, for a script or a dispatched agent.")
            .WithExample("doctor")
            .WithExample("doctor", "--yes");

        config.AddCommand<InstallCommand>("install")
            .WithDescription(
                "Publish h9k + h9kd binaries to ~/.hall9k/bin, publish the canonical skill set to "
                + "~/.hall9k/skills, and put h9k on the PATH. Registers no background service and no login "
                + "item — the daemon is started on demand (h9k daemon start). --repo builds locally with the "
                + ".NET SDK (the default); --from-release stages an already-downloaded, checksum-verified "
                + "release payload instead, which is what the bootstrap scripts and h9k update use on a bare "
                + "machine. Re-run after a merge or a release to refresh; a running daemon is offered a "
                + "restart (Decisions Log #31, backlog 42).")
            .WithExample("install")
            .WithExample("install", "--restart")
            .WithExample("install", "--from-release", "/tmp/hall9k-osx-arm64");

        config.AddCommand<UninstallCommand>("uninstall")
            .WithDescription(
                "Take the platform off this machine without taking the work with it (Decisions Log #83): stop a "
                + "running daemon, unregister autostart, drop the PATH link, and remove everything h9k install "
                + "itself wrote under ~/.hall9k — bin/, the skill set, logs — while leaving a registered "
                + "project's home, config.json, your credentials, and anything else you put there untouched. The "
                + "hall9k-postgres container is stopped, never removed, and its data volume is never touched, so "
                + "a later h9k install reconnects to every task, run, and idea exactly as it was. --purge-data is "
                + "the only path that destroys the container and its volume too, and it asks for confirmation "
                + "first (--yes skips the prompt for a non-interactive run).")
            .WithExample("uninstall")
            .WithExample("uninstall", "--purge-data")
            .WithExample("uninstall", "--purge-data", "--yes");

        config.AddCommand<UpdateCommand>("update")
            .WithDescription(
                "The one-command path for a machine already installed: fetch the latest GitHub release for "
                + "this platform via gh, verify its checksum, republish binaries and the canonical skill set "
                + "through the same idempotent path as h9k install --from-release, and offer the daemon "
                + "restart — no repo checkout, no .NET SDK. gh must be authenticated against the release's "
                + "repository (backlog 42).")
            .WithExample("update")
            .WithExample("update", "--restart");

        config.AddBranch("daemon", daemon =>
        {
            daemon.SetDescription(
                "The daemon's CLI-owned lifecycle (Decisions Log #31): start and stop on demand; a stopped daemon "
                + "costs latency, never correctness — startup adopts, sweeps, and closes out whatever happened while down");
            daemon.AddCommand<DaemonStartCommand>("start")
                .WithDescription(
                    "Launch h9kd detached from this terminal (it survives shell exit), logging to ~/.hall9k/h9kd.log. "
                    + "Refuses politely if one is already running, then reports what startup caught up on "
                    + "(runs adopted, leases swept, merges observed).")
                .WithExample("daemon", "start");
            daemon.AddCommand<DaemonStopCommand>("stop")
                .WithDescription(
                    "Stop h9kd gracefully: in-flight event appends finish, detached agents keep running and are "
                    + "adopted on the next start. Goes through the service manager (launchctl, Task Scheduler) "
                    + "when autostart owns the job, so stopped means stopped.")
                .WithExample("daemon", "stop");
            daemon.AddCommand<DaemonStatusCommand>("status")
                .WithDescription(
                    "Running or not, pid, uptime, autostart posture, the last few log lines, and the effective "
                    + "operating settings (concurrency, model roles) with where each one came from")
                .WithExample("daemon", "status");
            daemon.AddBranch("autostart", autostart =>
            {
                autostart.SetDescription(
                    "Start-at-login, strictly opt-in — never implied by install or start (macOS launchd "
                    + "LaunchAgent; Windows Task Scheduler logon task, never a service)");
                autostart.AddCommand<DaemonAutostartEnableCommand>("enable")
                    .WithDescription(
                        "Register the platform's start-at-login mechanism: h9kd starts at login and restarts "
                        + "after a crash — never after a clean stop, and h9k daemon stop always still means stopped")
                    .WithExample("daemon", "autostart", "enable");
                autostart.AddCommand<DaemonAutostartDisableCommand>("disable")
                    .WithDescription("Fully unregister start-at-login (stops an autostart-owned daemon and says so)")
                    .WithExample("daemon", "autostart", "disable");
            });
        });

        config.AddBranch("config", operatingSettings =>
        {
            operatingSettings.SetDescription(
                "The daemon's durable operating settings — concurrency and the model-by-role policy — read "
                + "from the platform config file (~/.hall9k/config.json) so an autostart-launched daemon runs "
                + "with the operator's settings and not just built-in defaults (backlog 59). Precedence: an "
                + "environment variable outranks this file, which outranks the built-in default. Hand-editing "
                + "the file works just as well as these commands. --interactive-claim-stale-after-days is the "
                + "one exception to all of this: it has no environment-variable tier and no daemon-startup "
                + "binding, since there is no daemon-side reclaim to configure — h9k status reads it straight "
                + "from the file on every render.");
            operatingSettings.AddCommand<ConfigShowCommand>("show")
                .WithDescription(
                    "The effective operating settings right now, and where each one came from: an environment "
                    + "variable, the platform config file, or the built-in default — the same precedence "
                    + "DaemonOptions binds by at daemon startup. --interactive-claim-stale-after-days is read "
                    + "straight from the file instead; it binds nothing at daemon startup.")
                .WithExample("config", "show");
            operatingSettings.AddCommand<ConfigSetCommand>("set")
                .WithDescription(
                    "Write one or more operating settings to the platform config file. A running daemon picks "
                    + "up most changes on its next start (h9k daemon stop, then h9k daemon start) — it binds "
                    + "configuration once, at startup, the same as every environment variable it reads. "
                    + "--interactive-claim-stale-after-days is the exception: h9k status reads it fresh from "
                    + "the file on every render, so a new value is in force immediately, with no daemon restart "
                    + "and no environment variable to outrank it.")
                .WithExample("config", "set", "--max-concurrent-task-runs", "2")
                .WithExample("config", "set", "--session-cap-per-run", "1")
                .WithExample("config", "set", "--model-review", "sonnet", "--model-fix", "haiku")
                .WithExample("config", "set", "--model-review-verify", "sonnet")
                .WithExample("config", "set", "--model-review-finalpass", "sonnet")
                .WithExample("config", "set", "--interactive-claim-stale-after-days", "5")
                .WithExample("config", "set", "--spend-budget", "5000000", "--spend-period", "week")
                .WithExample("config", "set", "--review-stage-composition", "skip-final-pass", "--accept-reduced-review");
        });

        config.AddBranch("idea", idea =>
        {
            idea.SetDescription(
                "Capture and develop ideas. An idea undergoes DISCOVERY — what is this? — and becomes a "
                + "draft task the moment discovery gives it intent; the draft then undergoes REFINEMENT — "
                + "how does this become executable? (Decisions Log #35). A task is an idea with intent.");
            idea.AddCommand<IdeaAddCommand>("add")
                .WithDescription(
                    "Capture an idea: one command, one argument, no ceremony. --project is the only "
                    + "option and it is optional — an idea may precede its project, or become one. Each "
                    + "idea gets a discovery workspace directory for the research, files, and prototypes "
                    + "that accumulate while you figure out what it is.")
                .WithExample("idea", "add", "\"The attention pane should teach the next command\"")
                .WithExample("idea", "add", "\"Stacked PRs for dependency chains\"", "--project", "hall9k");
            idea.AddCommand<IdeaListCommand>("list")
                .WithDescription(
                    "Browse ideas newest-first, with their age and their project (or the honest absence "
                    + "of one). Shows what is still in discovery by default; --state all adds what was "
                    + "promoted or discarded. The footer teaches promotion, which is what the list is for.")
                .WithExample("idea", "list")
                .WithExample("idea", "list", "--project", "hall9k")
                .WithExample("idea", "list", "--unassigned")
                .WithExample("idea", "list", "--state", "all", "--all");
            idea.AddCommand<IdeaShowCommand>("show")
                .WithDescription(
                    "One idea: its note, its project, its discovery workspace path and what has piled up "
                    + "in it, every version the note has had, and what the idea became if it was promoted "
                    + "or why it was discarded.")
                .WithExample("idea", "show", "28b19893");
            idea.AddCommand<IdeaReviseCommand>("revise")
                .WithDescription(
                    "Rewrite the note as discovery sharpens it. No ceremony, unlike a task revision: "
                    + "nothing dispatches from an idea, so there is no promise an edit could break. Every "
                    + "earlier version stays on the stream and in h9k idea show.")
                .WithExample("idea", "revise", "28b19893", "\"Ideas need their own discovery workspace, not just a note\"");
            idea.AddCommand<IdeaAssignCommand>("assign")
                .WithDescription(
                    "Set or change the project an idea belongs to — for when capture did not know yet, "
                    + "which is most of the time. An unassigned idea is honest, not incomplete; a project "
                    + "only becomes required at promotion.")
                .WithExample("idea", "assign", "28b19893", "--project", "hall9k");
            idea.AddCommand<IdeaPromoteCommand>("promote")
                .WithDescription(
                    "Promote an idea into a draft task: discovery ends, refinement begins. The note seeds "
                    + "the draft (its first sentence becomes the objective — taken mechanically, never "
                    + "interpreted, and overridable with --objective; the remainder becomes agent context), "
                    + "the discovery workspace pointer rides along, and provenance is recorded both ways. "
                    + "Needs a project, supplied here or already assigned.")
                .WithExample("idea", "promote", "28b19893")
                .WithExample("idea", "promote", "28b19893", "--project", "hall9k")
                .WithExample("idea", "promote", "28b19893", "--objective", "\"Give every idea a discovery workspace\"");
            idea.AddCommand<IdeaDiscardCommand>("discard")
                .WithDescription(
                    "Close an idea with the reason recorded. Nothing is deleted and the workspace stays "
                    + "put: an idea that keeps coming back is a signal, and only a kept record can show it.")
                .WithExample("idea", "discard", "28b19893", "--reason", "\"Superseded by the attachments design\"");
        });

        config.AddBranch("epic", epic =>
        {
            epic.SetDescription(
                "Name a cohesive family of tasks (Decisions Log #100): an epic is a first-class "
                + "entity with its own id, title, and open state, event-sourced like everything else. "
                + "Membership is optional and no ceremony — a task joins at add or revise and can leave "
                + "the same way, and the flat task model is undisturbed for everything ungrouped. An epic "
                + "closes only by explicit human act, never automatically — not even when its last task "
                + "closes out.");
            epic.AddCommand<EpicAddCommand>("add")
                .WithDescription("Name a new epic: a project and a title, nothing else.")
                .WithExample("epic", "add", "--project", "hall9k", "--title", "\"Interactive mode\"");
            epic.AddCommand<EpicListCommand>("list")
                .WithDescription(
                    "Every epic with a member-task rollup by attention bucket, same columns h9k project "
                    + "list shows. Defaults to open epics; --state all adds closed ones.")
                .WithExample("epic", "list")
                .WithExample("epic", "list", "--project", "hall9k")
                .WithExample("epic", "list", "--state", "all");
            epic.AddCommand<EpicShowCommand>("show")
                .WithDescription(
                    "One epic: its title, state, project, Jira link if it has one, and every member "
                    + "task with its current state, composed the same way h9k project show composes a "
                    + "project's tasks.")
                .WithExample("epic", "show", "28b19893");
            epic.AddCommand<EpicLinkJiraCommand>("link-jira")
                .WithDescription(
                    "Record the Jira epic this one corresponds to — a key or a URL, stored exactly as "
                    + "typed. Identity only: no data is read from or written to Jira through this "
                    + "command (Decisions Log #100 — no mirroring, ever). h9k epic show "
                    + "renders it as a link-out when it is a URL.")
                .WithExample("epic", "link-jira", "28b19893", "PROJ-45")
                .WithExample("epic", "link-jira", "28b19893", "https://your-org.atlassian.net/browse/PROJ-45");
            epic.AddCommand<EpicCloseCommand>("close")
                .WithDescription(
                    "Close an epic: the only way one ever closes, always an explicit human act with a "
                    + "reason. Nothing closes an epic automatically, including its last member task "
                    + "closing out.")
                .WithExample("epic", "close", "28b19893", "--reason", "\"Interactive mode shipped\"");
        });

        config.AddBranch("task", task =>
        {
            task.SetDescription(
                "Manage tasks. Development and dispatch are separate lifecycles (Decisions Log #34): "
                + "add drafts, revise develops, publish is the readiness gate, and assign is the go signal. "
                + "A task runs only once a human assigns it and every dependency has closed out.");
            task.AddCommand<TaskAddCommand>("add")
                .WithDescription(
                    "Create a draft (flags, --file task.md, --from-issue to adopt a GitHub issue, or "
                    + "--from-pr to adopt a pull request to review — a pr-review task, read-only until "
                    + "you direct otherwise). Creation is identity, not readiness: a project and an "
                    + "objective are all it takes, and the draft is invisible to the dispatcher until you "
                    + "publish and assign it. Acceptance criteria are what h9k task publish demands, and "
                    + "an adopted issue or pull request never supplies them.")
                .WithExample("task", "add", "--project", "hall9k", "--objective", "\"Add the project browse surface\"",
                    "--criteria", "\"h9k project list shows one row per project\"")
                .WithExample("task", "add", "--file", "backlog/19-model-policy.md", "--model", "claude-opus-5")
                .WithExample("task", "add", "--project", "hall9k", "--from-issue", "42")
                .WithExample("task", "add", "--project", "hall9k", "--from-jira", "PROJ-123")
                .WithExample("task", "add", "--project", "hall9k",
                    "--from-pr", "https://github.com/Hallmanac/hall9k/pull/42")
                .WithExample("task", "add", "--project", "hall9k",
                    "--from-issue", "https://github.com/Hallmanac/hall9k/issues/42",
                    "--criteria", "\"The importer refuses a closed issue\"")
                .WithExample("task", "add", "--project", "hall9k", "--objective", "\"Wire the new pane in\"",
                    "--blocked-by", "28b19893")
                .WithExample("task", "add", "--project", "hall9k", "--objective", "\"Wire the new pane in\"",
                    "--epic", "28b19893")
                .WithExample("task", "add", "--project", "hall9k", "--objective", "\"Prototype the new endpoint\"",
                    "--review-stage-composition", "none", "--accept-reduced-review");
            task.AddCommand<TaskReviseCommand>("revise")
                .WithDescription(
                    "Revise a draft: objective, acceptance criteria, agent context, type, model, dependencies. "
                    + "Draft-only for all of those — a published task promises it may be assigned at any moment "
                    + "and an assigned one promises a node may read it at any moment, and editing them would break "
                    + "both. --queue-first/--clear-queue-first is the one exception (Decisions Log #127): a "
                    + "scheduling fact, not part of the readiness contract, settable on a call that names nothing "
                    + "else in any live state — Queued, Blocked, a currently Claimed task (for its next turn in "
                    + "the queue), even a Done one (for the follow-up run a later reopen might dispatch); refused "
                    + "only on Abandoned, which nothing ever requeues from. Each option passed replaces that part; "
                    + "each one left off is left alone.")
                .WithExample("task", "revise", "28b19893", "--criteria", "\"h9k status shows the blocked reason\"")
                .WithExample("task", "revise", "28b19893", "--blocked-by", "3f2a91b2", "--blocked-by", "91bd44c0")
                .WithExample("task", "revise", "28b19893", "--clear-dependencies")
                .WithExample("task", "revise", "28b19893", "--epic", "3f2a91b2")
                .WithExample("task", "revise", "28b19893", "--clear-epic")
                .WithExample("task", "revise", "28b19893", "--queue-first")
                .WithExample("task", "revise", "28b19893", "--clear-queue-first")
                .WithExample("task", "revise", "28b19893", "--review-stage-composition", "default");
            task.AddCommand<TaskSetReviewCapsCommand>("set-review-caps")
                .WithDescription(
                    "Override one or more of this task's four review-cycle caps — the conformance and "
                    + "adversarial track cycle caps, the mandatory final-full-pass round cap, and the "
                    + "task-lifetime review-cycle budget (task: the review cycle caps become settable at "
                    + "three levels). Each resolves task > project > node > compiled default, "
                    + "independently of the other three. Deliberately state-agnostic, unlike h9k task "
                    + "revise: settable at any time, including while the task's run is live — the daemon "
                    + "picks it up at the next cap check. Setting a cap at or below the cycles that track "
                    + "has run since its last human takeover grant (0, if it has never had one, which is "
                    + "also when this count matches the absolute review cycle h9k status/h9k task show "
                    + "print — a grant or a track reactivation moves this count's own base forward, and "
                    + "only from there do the two numbers diverge) parks the run the next time that cap "
                    + "is actually checked — a per-track cap at its next fix-session dispatch, the "
                    + "final-full-pass cap at its next mandatory round — the documented takeover lever "
                    + "for a task observed grinding; it does not stop a run that converges clean before "
                    + "then. The lifetime budget is the one exception, checked at every settle point. "
                    + "'default' clears an override back to the level above.")
                .WithExample("task", "set-review-caps", "28b19893", "--max-compliance-review-cycles", "1")
                .WithExample("task", "set-review-caps", "28b19893", "--lifetime-review-cycle-budget", "40")
                .WithExample("task", "set-review-caps", "28b19893", "--max-adversarial-review-cycles", "default");
            task.AddCommand<TaskPublishCommand>("publish")
                .WithDescription(
                    "Publish a draft: the readiness gate. Enforces the full contract (an outcome-phrased "
                    + "objective and at least one checkable acceptance criterion, PLAN.md §4) and refuses a "
                    + "dependency cycle, naming it. A published task is immutable and assignable but still "
                    + "will not run — assigning it is a separate, explicit act (--assign does both at once). "
                    + "Under a tracking backlog policy (h9k project set --backlog), publishing has an "
                    + "external side effect: github-issues files a GitHub issue itself, through the "
                    + "operator's own gh credentials, and jira dispatches an agent run to author the card. "
                    + "That policy is also a dedup gate: a draft with no linked item and no publication "
                    + "already pending is refused until you search the tracker yourself and either link "
                    + "what you find (h9k task link-jira / h9k task link-issue), attest none exists with "
                    + "--no-existing-item, or attest that this task should skip tracking altogether with "
                    + "--untracked (for internal chores that should not pollute a team's tracker).")
                .WithExample("task", "publish", "28b19893")
                .WithExample("task", "publish", "28b19893", "--assign")
                .WithExample("task", "publish", "28b19893", "--no-assign")
                .WithExample("task", "publish", "28b19893", "--no-existing-item")
                .WithExample("task", "publish", "28b19893", "--untracked");
            task.AddCommand<TaskAssignCommand>("assign")
                .WithDescription(
                    "Assign a published task to an owner: the dispatch trigger, and the only way a task "
                    + "becomes claimable. It queues when every dependency has reached true closeout (the "
                    + "pull request merged), and blocks otherwise — unblocking itself when the last one lands. "
                    + "Only that owner's nodes may claim it.")
                .WithExample("task", "assign", "28b19893")
                .WithExample("task", "assign", "28b19893", "brian");
            task.AddCommand<TaskSetSessionCapCommand>("set-session-cap")
                .WithDescription(
                    "Override how many agent sessions this task's own run may hold simultaneously (Decisions Log "
                    + "#111) — the global default is 3, and this overrides it for this task alone, at any time, "
                    + "including while the task's run is live. A cap of 1 serializes the run's two review lenses "
                    + "instead of dispatching them together, for maximum throttle. Takes effect at the run's next "
                    + "session dispatch: raising it lets the next phase fan out wider, lowering it never "
                    + "terminates a session already running. An interactive claim (h9k task work) occupies zero "
                    + "runs and ignores this cap entirely. Pass 'default' to clear this task's own override and "
                    + "let the node's global session-cap-per-run decide again.")
                .WithExample("task", "set-session-cap", "28b19893", "1")
                .WithExample("task", "set-session-cap", "28b19893", "3")
                .WithExample("task", "set-session-cap", "28b19893", "default");
            task.AddCommand<TaskUnassignCommand>("unassign")
                .WithDescription(
                    "Take a queued or blocked task back to Published, so no node claims it. Refused while a "
                    + "node holds the lease — that is a running agent. This is the first step of the "
                    + "edit-after-the-fact path: unassign → draft → revise → publish → assign.")
                .WithExample("task", "unassign", "28b19893", "--reason", "\"The criteria missed the migration case\"");
            task.AddCommand<TaskDraftCommand>("draft")
                .WithDescription(
                    "Return a published task to Draft so it can be revised. Refused from Queued and Blocked "
                    + "onward: unassign it first, so a task the dispatcher can see never becomes editable by "
                    + "one keystroke.")
                .WithExample("task", "draft", "28b19893");
            task.AddCommand<TaskListCommand>("list")
                .WithDescription(
                    "Browse tasks newest-first, across projects or filtered to one (--project) and to one or "
                    + "more states (--state: a lifecycle word, which selects exactly what the Status column "
                    + "shows, such as Delivered; an attention group like needs-you or attention-delivered; or a "
                    + "run state like Running from the phase line — comma-separated, repeatable, or both, "
                    + "unioned together, and the three vocabularies may mix in one filter). Archived rows — a "
                    + "human walked away, not a merged task, which stays Done and is never hidden — are hidden "
                    + "from an otherwise-unfiltered view by default so abandoned work doesn't accumulate "
                    + "alongside live and done tasks; ask for them with --state archived, --state closed, or "
                    + "--include-archived. Bounded to the newest 20 by default — the footer says how many were "
                    + "held back and how to see them (--all, --limit <n>), and how many Archived rows the "
                    + "default hid.")
                .WithExample("task", "list")
                .WithExample("task", "list", "--project", "hall9k", "--state", "needs-you")
                .WithExample("task", "list", "--state", "draft")
                .WithExample("task", "list", "--state", "attention-delivered", "--all")
                .WithExample("task", "list", "--state", "AwaitingReview")
                .WithExample("task", "list", "--state", "published,working", "--state", "delivered")
                .WithExample("task", "list", "--state", "archived")
                .WithExample("task", "list", "--include-archived")
                .WithExample("task", "list", "--epic", "28b19893");
            task.AddCommand<TaskShowCommand>("show")
                .WithDescription(
                    "One task in full: the readiness contract it was published against, its dependencies "
                    + "and what they are waiting on, its external reference, the conversation, and every "
                    + "run with its outcome and pull request. This is the second command of any "
                    + "investigation — h9k status names the task, this says what happened to it. Takes "
                    + "the full id or an unambiguous fragment.")
                .WithExample("task", "show", "28b19893");
            task.AddCommand<TaskPushToJiraCommand>("push-to-jira")
                .WithDescription(
                    "Publish this task as a Jira card, by dispatching an agent run that composes it. The "
                    + "platform never authors the card itself: issue types, required fields, and routing "
                    + "rules are the organisation's configuration, so the session runs in the project's "
                    + "repository with its own Claude skills and works out the fields. It performs no "
                    + "direct Jira access: it finishes by submitting the composed payload through "
                    + "h9k task write-jira, which is the sole executor — hall9k validates it, executes it "
                    + "against the Jira Cloud REST API, and reads the card back before recording anything. "
                    + "Needs a registered Jira connection; the project's bound board "
                    + "(h9k project set --jira) tells the agent where to file it.")
                .WithExample("task", "push-to-jira", "28b19893");
            task.AddCommand<TaskLinkJiraCommand>("link-jira")
                .WithDescription(
                    "Record the Jira card this task belongs to, verified first. The key you pass is read "
                    + "through the registered connection and what gets recorded is the response, never the "
                    + "claim — so a key that does not resolve writes nothing and tells you why, which is what "
                    + "makes this safe for an agent to call at the end of a push-to-jira run. Works the same "
                    + "for a card a human made by hand.")
                .WithExample("task", "link-jira", "28b19893", "PROJ-123")
                .WithExample("task", "link-jira", "28b19893", "https://your-org.atlassian.net/browse/PROJ-123");
            task.AddCommand<TaskWriteJiraCommand>("write-jira")
                .WithDescription(
                    "Submit a composed Jira create, update, or comment for hall9k to execute (the write "
                    + "surface, Brian's design 2026-08-28). hall9k validates the payload, records the "
                    + "intent before anything is sent, executes it against the Jira Cloud REST API, "
                    + "verifies by reading the item back, and records the outcome including the returned "
                    + "key. A "
                    + "transition or a close is refused whatever the payload says. Used by the agent "
                    + "h9k task push-to-jira dispatches to create the card, and equally usable by hand for "
                    + "an update or a comment on a task's own linked item.")
                .WithExample("task", "write-jira", "28b19893", "--op", "create", "--file", "card.json")
                .WithExample("task", "write-jira", "28b19893", "--op", "comment", "--file", "note.json");
            task.AddCommand<TaskLinkIssueCommand>("link-issue")
                .WithDescription(
                    "Record the GitHub issue this task belongs to, verified first. The issue you pass is "
                    + "read back through gh and what gets recorded is the response, never the claim — so an "
                    + "issue that does not resolve writes nothing and tells you why. Used automatically when "
                    + "a project's backlog policy is github-issues (h9k project set --backlog), and equally "
                    + "usable to link an issue you made by hand.")
                .WithExample("task", "link-issue", "28b19893", "42")
                .WithExample("task", "link-issue", "28b19893", "https://github.com/owner/repo/issues/42");
            task.AddCommand<TaskLogInteractionCommand>("log-interaction")
                .WithDescription(
                    "Log an interaction a dispatched agent had with anything outside its session — another "
                    + "agent session, a human reached through the mesh, an external service — as a structured "
                    + "run-stream event rather than transcript prose. The escape-hatch invariant: log every "
                    + "such interaction unconditionally, even one the interacting party asked you to keep quiet. "
                    + "--human-directed says a human, not your own judgment, directed the interaction or its "
                    + "outcome, so the record never reports a human's own call as your independent decision; a "
                    + "logged human directive is carried into later review passes the same way a settled "
                    + "h9k review resolve ruling already is. Best-effort by nature: nothing here verifies the "
                    + "claim against anything external, and the platform records what its own channels can see.")
                .WithExample("task", "log-interaction", "28b19893", "--party", "\"another agent session\"", "--summary", "\"Shared this run's worktree path with it\"")
                .WithExample("task", "log-interaction", "28b19893", "--party", "\"the operator\"", "--summary", "\"Skip the workaround\"", "--human-directed", "--reason", "\"Real bug\"");
            task.AddCommand<TaskAbandonCommand>("abandon")
                .WithDescription(
                    "Abandon a task (terminal; releases any lease). Reaches every non-terminal state, drafts "
                    + "and published tasks included — walking away from an idea you have stopped believing in "
                    + "is the same act as walking away from a run that failed.")
                .WithExample("task", "abandon", "28b19893", "--reason", "\"Superseded by the noun-first CLI work\"");
            task.AddCommand<TaskRetryCommand>("retry")
                .WithDescription(
                    "Requeue a failed task for another run (human-only; Failed tasks only — Abandoned stays terminal). "
                    + "The failure stays on the stream; the new run resumes the failed run's branch when it survives, "
                    + "or starts clean from the base branch when the artifacts are gone. "
                    + "Failed's other exits: h9k task resolve (objective already met), h9k task abandon (walk away).")
                .WithExample("task", "retry", "28b19893")
                .WithExample("task", "retry", "28b19893", "--reason", "\"Daemon push bug fixed; the completed work is intact in the worktree\"");
            task.AddCommand<TaskResolveCommand>("resolve")
                .WithDescription(
                    "Resolve a failed task to Done: your attestation that the objective was met even though the run "
                    + "failed (human-only; Failed tasks only). --reason is required — an attestation without a why is "
                    + "a guess. The failure stays on the stream; --pr records where the work landed. "
                    + "Failed's other exits: h9k task retry (run again), h9k task abandon (walk away).")
                .WithExample("task", "resolve", "28b19893", "--reason", "\"Work merged as PR #7; only the daemon's push step failed\"")
                .WithExample("task", "resolve", "28b19893", "--reason", "\"Objective met by hand in the worktree\"", "--pr", "https://github.com/x/y/pull/7");
            task.AddCommand<TaskWorkCommand>("work")
                .WithDescription(
                    "Work a Published, Queued, or already-Blocked task interactively. On a Published task assigned to nobody, "
                    + "this assigns it to your own owner and claims it interactively in one atomic event append, "
                    + "the same collapsing h9k task publish --assign already does for publish and assign: the "
                    + "task is never observably Queued in between, so the dispatcher (woken within moments by "
                    + "the doorbell a plain h9k task assign would send) can never win the race to it. An unmet "
                    + "dependency — whether just discovered here or already sitting Blocked from an earlier "
                    + "h9k task assign or a handed-back/retried claim — warns rather than refuses: the platform "
                    + "names every open blocker, and --acknowledge-unmet-dependencies is your recorded override "
                    + "to claim it anyway. Not needed twice: an acknowledgment this task already carries from an "
                    + "earlier claim on the same still-open blockers is honored without asking again. On an "
                    + "already-Queued task assigned to "
                    + "you, this claims it exactly as before: cuts the same branch and worktree headless "
                    + "dispatch would, assembles the prompt through the identical code path (its working rules "
                    + "swapped for an attached operator). By default it then prints the worktree path, the "
                    + "branch, and that prompt for you to paste into a Claude Code session you start yourself, "
                    + "anywhere — the pasted session self-registers (h9k task register-session), which is what "
                    + "lets the double-booking and liveness guards below recognise it; --direct-launch instead "
                    + "launches a plain interactive Claude Code process attached to this terminal the way this "
                    + "command always did (kept for one release; refused on a machine where Claude Code resolves "
                    + "to a Windows script shim). The claim is held by you, not a process — no liveness "
                    + "lease, no heartbeat reclaim, and the dispatcher never claims a task you hold this way. "
                    + "Occupies zero concurrency slots: it starts even when the daemon's session ceiling is fully "
                    + "consumed. Closing the terminal is a normal way to leave — the task stays claimed, and "
                    + "running this again re-enters the same worktree and branch with a fresh prompt "
                    + "(--direct-launch instead resumes the most recently recorded session's own conversation, "
                    + "falling back to a fresh one — announced, never silent — only when the recorded one cannot "
                    + "be resumed). Exits are h9k task deliver (push and hand into the standard pipeline), "
                    + "h9k task release (give it "
                    + "back to the queue), or h9k task handback (let a headless agent finish from here). "
                    + "Re-entry is refused when the claim's session was recorded on another machine this one "
                    + "cannot check — --force attests you confirmed by hand that it has exited.")
                .WithExample("task", "work", "28b19893")
                .WithExample("task", "work", "28b19893", "--direct-launch")
                .WithExample("task", "work", "28b19893", "--acknowledge-unmet-dependencies");
            task.AddCommand<TaskRegisterSessionCommand>("register-session")
                .WithDescription(
                    "The self-registration observation gate a starting prompt (h9k task work's default, "
                    + "prompt-handoff output) tells the pasted-in Claude Code session to call as its first act: "
                    + "records this session's own process identity (read from CLAUDE_PID, Claude Code's own "
                    + "environment variable) against the run the same way a direct launch's own onStarted "
                    + "callback used to. The double-booking and liveness guards (re-entry, verify, deliver, "
                    + "handback, release) key off this record from here on. Refuses rather than guessing when "
                    + "CLAUDE_PID is absent — not a session this platform can ever check, so nothing is recorded, "
                    + "the same honest degradation a session that never calls this at all already gets. Only for "
                    + "a task you hold interactively (h9k task work). Also refused when another session is "
                    + "already registered and still attached — --force attests you confirmed by hand that a "
                    + "session recorded on another machine has actually exited.")
                .WithExample("task", "register-session", "28b19893");
            task.AddCommand<TaskStartCommand>("start")
                .WithDescription(
                    "Dispatch a Published, Queued, or already-Blocked task on the spot, headless, instead of waiting for the "
                    + "dispatcher's own ceiling and ordering to reach it (a deliberate human kick-off). On a "
                    + "Published task assigned to nobody, this assigns it to your own owner and claims it in "
                    + "one atomic event append, the same collapsing h9k task work's own Published entry already "
                    + "uses, including h9k task work's own warn-then-acknowledge shape for an unmet dependency, on "
                    + "a Published task and on an already-Blocked one alike: the platform names every open "
                    + "blocker and advises, and --acknowledge-unmet-dependencies is your recorded override to "
                    + "start it anyway. Not needed twice: an acknowledgment this task already carries from an "
                    + "earlier claim on the same still-open blockers is honored without asking again, whichever "
                    + "of h9k task start or h9k task work gave it. Refused on Draft (publish it first), a task "
                    + "that already has a live claim, and every terminal state — there is nothing there to "
                    + "start; there is no re-entry branch the way h9k task work has one, but a fresh claim on an "
                    + "already-Blocked task is exactly what this command's own Blocked entry is, not a re-entry. "
                    + "Ceiling-exempt on the same reasoning h9k task work's own "
                    + "claim already is (Decisions Log #103): a deliberate human act is outside the automation's "
                    + "budget. The session launches headless and detached under the <task-shortid>-build name — "
                    + "reachable on the session mesh (claude agents --json, ListAgents/SendMessage) — and this "
                    + "command returns as soon as the process is confirmed alive, without waiting for it to "
                    + "finish. Once it is done: h9k task deliver, h9k task verify, h9k task work (to attach), "
                    + "h9k task handback, or h9k task release.")
                .WithExample("task", "start", "28b19893")
                .WithExample("task", "start", "28b19893", "--acknowledge-unmet-dependencies");
            task.AddCommand<TaskVerifyCommand>("verify")
                .WithDescription(
                    "Run the project's build and test gates on demand against an interactive claim's worktree, "
                    + "and record the outcome as the same gate events a headless run's own verification records. "
                    + "Reports modified-but-uncommitted files rather than refusing on them — the gates run "
                    + "against the worktree as it stands; h9k task deliver is the one that refuses on them, "
                    + "before it pushes. Only for a task you hold interactively (h9k task work). Refused when the "
                    + "claim's session was recorded on another machine this one cannot check — --force attests "
                    + "you confirmed by hand that it has exited.")
                .WithExample("task", "verify", "28b19893");
            task.AddCommand<TaskDeliverCommand>("deliver")
                .WithDescription(
                    "Deliver an interactive claim: refuses on uncommitted files, naming them, then pushes the "
                    + "branch and hands the run into the standard delivery pipeline — gates, the independent "
                    + "review loop, the pull request, the closeout watcher — indistinguishable downstream from a "
                    + "headless run from this point on. Only for a task you hold interactively (h9k task work). "
                    + "Refused when the claim's session was recorded on another machine this one cannot check — "
                    + "--force attests you confirmed by hand that it has exited.")
                .WithExample("task", "deliver", "28b19893");
            task.AddCommand<TaskReleaseCommand>("release")
                .WithDescription(
                    "Give an interactive claim back to the dispatch queue, exactly as any other queued task — "
                    + "the daemon claims it as capacity allows. Refused on a task a node holds (that is running "
                    + "headless work; let it finish, or h9k task abandon it) and on a claim that is not untouched: "
                    + "modified-but-uncommitted files, naming them, or commits beyond the base branch (h9k task "
                    + "handback or h9k task deliver instead). The worktree and branch are left on disk untouched; "
                    + "nothing resumes them automatically (h9k task handback is the lever for that). Refused when "
                    + "the claim's session was recorded on another machine this one cannot check — --force "
                    + "attests you confirmed by hand that it has exited.")
                .WithExample("task", "release", "28b19893");
            task.AddCommand<TaskHandbackCommand>("handback")
                .WithDescription(
                    "Hand an interactive claim to a headless agent partway through: refuses on uncommitted "
                    + "files (you are present to commit), releases your claim, and queues the task through "
                    + "normal dispatch. Mechanically the existing follow-up resume-existing-branch flow — the "
                    + "next headless run resumes your branch instead of starting clean. Pickup speed is a "
                    + "three-way choice (Decisions Log #127): no flag is the normal rotation, byte-for-byte "
                    + "today's behavior; --first records the queue-first marker so the next free dispatch slot "
                    + "takes this task regardless of assignment age; --now dispatches it immediately instead, "
                    + "ceiling-exempt, through the same mechanism h9k task start uses. --first and --now are "
                    + "refused together — pass one. Refused when the claim's "
                    + "session was recorded on another machine this one cannot check — --force attests you "
                    + "confirmed by hand that it has exited.")
                .WithExample("task", "handback", "28b19893")
                .WithExample("task", "handback", "28b19893", "--reason", "\"Need to step away; the migration script is drafted but untested\"")
                .WithExample("task", "handback", "28b19893", "--first")
                .WithExample("task", "handback", "28b19893", "--now");
        });
    }

    /// <summary>
    /// The console the shipped binary renders <c>--help</c> through: stdout, as the terminal
    /// would have it, but never narrower than <see cref="MinimumHelpWidth"/>.
    /// </summary>
    /// <remarks>
    /// Colour and ANSI detection are left alone, so help in a terminal reads like the rest of the
    /// CLI's output. Only the width is floored, and it is widened rather than pinned: a wide
    /// terminal keeps its own width, and a narrow one gets prose the terminal soft-wraps instead of
    /// examples the renderer hard-breaks. Soft-wrapped prose is a cosmetic cost; a hard-broken
    /// example is a command nobody can paste.
    /// </remarks>
    private static IAnsiConsole HelpConsole()
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Out),
        });
        console.Profile.Width = Math.Max(console.Profile.Width, MinimumHelpWidth);
        return console;
    }
}
