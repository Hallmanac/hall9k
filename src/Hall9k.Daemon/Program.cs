using Hall9k.Daemon;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.JiraWrites;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Daemon.Publication;
using Hall9k.Daemon.Review;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx;
using Microsoft.Extensions.Configuration;
using Wolverine;
using Wolverine.Marten;

// Single-instance guard before anything expensive. The refusal exits 0 on purpose: an
// autostart LaunchAgent with KeepAlive that loses this race must not be thrash-restarted
// by launchd every throttle interval (Decisions Log #31); the log carries the refusal.
using SingleInstanceGuard? instance = SingleInstanceGuard.TryAcquire(DaemonRuntime.LockFile, DaemonRuntime.PidFile);
if (instance is null)
{
    DaemonProcessDescriptor? running = DaemonPidFile.TryRead(DaemonRuntime.PidFile);
    Console.Error.WriteLine(running is null
        ? "h9kd is already running (another instance holds the lock). Refusing to start a second."
        : $"h9kd is already running (pid {running.ProcessId}, started {running.StartedAt:u}). Refusing to start a second.");
    return 0;
}

// Before anything logs a single line: the inherited stdout/stderr h9kd gets from
// cmd.exe's own `>>` redirect does not survive a live rotation (WindowsAppendOnlyLog),
// so every line — not just the ones after the first rotation — needs to go through the
// replacement handle from the start. Gated on the marker the two launch paths that
// actually set up that cmd.exe redirect set (DaemonRuntime.AppendOnlyLogEnvironmentVariable),
// not on OperatingSystem.IsWindows() alone: h9kd started any other way on Windows — a bare
// terminal invocation, or the AppHost dev loop — has its own real console/pipe, and taking
// it over here would silently vanish every line (including this process's own
// unconfigured-connection-string refusal below) into the installed daemon's log instead.
if (OperatingSystem.IsWindows()
    && Environment.GetEnvironmentVariable(DaemonRuntime.AppendOnlyLogEnvironmentVariable) == "1")
{
    try
    {
        WindowsAppendOnlyLog.TakeOverConsoleOutput(DaemonRuntime.LogFile);
    }
    catch (IOException exception)
    {
        // Losing the replacement handle only costs rotation fidelity — the log may read
        // back padded with NULs if DaemonLogRotation truncates it out from under the
        // inherited cmd.exe handle mid-run (see WindowsAppendOnlyLog's own doc comment).
        // That is strictly better than the alternative of letting this throw unhandled
        // above the host builder and above DaemonLogging.Configure: nothing would catch
        // it, the process would exit before logging its own diagnosis, and an autostarted
        // daemon would burn its whole RestartOnFailure budget leaving the machine with no
        // daemon at all. The inherited handles still work for this fallback line itself.
        Console.Error.WriteLine(
            $"Could not open {DaemonRuntime.LogFile} for append-only logging ({exception.Message}); "
            + "continuing on the inherited console handles. The log may be padded with NULs after the next rotation.");
    }

    // Cleared from this process's own environment the moment it has been acted on: a
    // child process spawned later (an agent session, a verify-gate command) inherits
    // this process's environment by default, and without clearing it here that child's
    // own h9kd — the dev loop, a bare terminal invocation, run to check on this very
    // change — would see the marker and wrongly take over its console output too.
    Environment.SetEnvironmentVariable(DaemonRuntime.AppendOnlyLogEnvironmentVariable, null);
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

// Ahead of the environment variables source Host.CreateApplicationBuilder already added, so an
// env var still outranks a config-file setting (backlog 59): env, then config file, then default.
PlatformConfigFileSource.Insert(builder.Configuration);

// Stdout IS the log file when the CLI starts the daemon detached, so how this reads is
// part of the lifecycle contract, not a preference (Decisions Log #31).
DaemonLogging.Configure(builder.Logging);

ConnectionStringResolution resolution = Hall9kDatabase.Resolve(builder.Configuration.GetConnectionString("hall9k"));
if (!resolution.IsConfigured)
{
    // h9k daemon start probes reachability before it ever spawns this process (Decisions
    // Log #73), so reaching here unconfigured means h9kd was started some other way —
    // launchd autostart, or the binary run by hand. Either way this is the daemon's own
    // log, not a terminal a human is watching, so the teaching is brief and points at the
    // command that gives the full diagnosis.
    Console.Error.WriteLine(
        "No Hall9k connection string is configured (checked HALL9K_CONNECTION_STRING, then "
        + $"{Hall9kDatabase.ConfigFile}, then a {Hall9kDatabase.ProjectOverrideFileName} file walking up "
        + $"from {Directory.GetCurrentDirectory()}). Run h9k doctor for the full diagnosis and the fix.");
    return 1;
}

string connectionString = resolution.Value;

// MaxConcurrentTaskRuns, SessionCapPerRun and MaxConcurrentAgentSessions are excluded from this
// generic Bind() and resolved separately (DaemonOptionsBinding's own doc explains why an internal
// setter alone does not keep ConfigurationBinder away from a key). The retired-key conversion
// (Decisions Log #111) needs the same per-precedence-level walk h9k config show and h9k daemon
// status already perform, which IConfiguration's own merged view of env, the config file, and
// appsettings.json cannot express: whether a level's own answer came from max-concurrent-task-runs
// directly or from converting max-concurrent-agent-sessions has to be decided per level, not from
// one flattened key.
IConfiguration bindableDaemonSection = DaemonOptionsBinding.ExcludingKeys(
    builder.Configuration.GetSection(DaemonOptions.SectionName), DaemonOptionsBinding.ResolverOwnedKeys);
builder.Services.AddOptions<DaemonOptions>().Bind(bindableDaemonSection);

// Resolved before the host is even built, so this is a one-time read no different in cost from
// the connection-string resolution just above it.
OperatingSettingsReport concurrencyReport = await OperatingSettingsResolver.ResolveAsync(CancellationToken.None);
builder.Services.AddSingleton(concurrencyReport);
builder.Services.PostConfigure<DaemonOptions>(options =>
{
    options.MaxConcurrentTaskRuns = concurrencyReport.MaxConcurrentTaskRuns.Value;
    options.SessionCapPerRun = concurrencyReport.SessionCapPerRun.Value;
    options.SpendBudgetTokens = concurrencyReport.SpendBudgetTokens.Value;
    options.SpendPeriod = concurrencyReport.SpendPeriod.Value;
});

// bindableDaemonSection above already has these keys stripped out, so this walks the
// un-excluded section instead — appsettings.json, a command-line argument, or any other source
// PostConfigure just overrode without saying so (independent pre-PR review, cycle 1, adversarial
// lens).
foreach (string message in DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(
    builder.Configuration.GetSection(DaemonOptions.SectionName), concurrencyReport))
{
    Console.Error.WriteLine(message);
}
builder.Services.AddSingleton(new DaemonConnection(connectionString));
builder.Services.AddSingleton<NodeContext>();
builder.Services.AddSingleton(ProcessManagers.ForCurrentPlatform());
builder.Services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
builder.Services.AddSingleton<DispatchEngine>();
builder.Services.AddSingleton<IExecutor, ClaudeExecutor>();
builder.Services.AddSingleton<VerificationRunner>();
builder.Services.AddSingleton<ReviewEngine>();
builder.Services.AddSingleton<PrReviewEngine>();
builder.Services.AddSingleton<PullRequestOpener>();
builder.Services.AddSingleton<BlockerContextAssembler>();
builder.Services.AddSingleton<RunSupervisor>();
builder.Services.AddSingleton<RunLauncher>();
builder.Services.AddSingleton<TokenBudgetRetryEngine>();
builder.Services.AddSingleton<IPullRequestInspector, GitHubPullRequestInspector>();
// The one process-spawning seam every connector's write goes through, registered rather than let
// GitHubWorkItemProvider or TwgJiraExecutor default to ExternalProcess.Runner, so CloseoutEngine's
// GitHub and Jira writes are both testable against a recorded process instead of a real,
// machine-authenticated gh or twg (the delegate is process-agnostic — it takes the tool's file
// name as an argument — so one registration serves both).
builder.Services.AddSingleton<ProcessRunner>(_ => ExternalProcess.Runner);
builder.Services.AddSingleton<CloseoutEngine>();
builder.Services.AddSingleton<CardPublicationEngine>();
builder.Services.AddSingleton<JiraWriteRetryEngine>();
builder.Services.AddSingleton<ProjectHomeRenderEngine>();

builder.Services.AddMartenEventStore(connectionString, AutoCreate.CreateOnly)
    .IntegrateWithWolverine();

builder.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(IDomainAssemblyMarker).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.Durability.Mode = DurabilityMode.Solo;
});

builder.Services.AddHostedService<DispatchLoop>();
builder.Services.AddHostedService<LeaseHeartbeatService>();
builder.Services.AddHostedService<PullRequestMonitor>();
builder.Services.AddHostedService<TokenBudgetRetryMonitor>();
builder.Services.AddHostedService<CardPublicationLoop>();
builder.Services.AddHostedService<JiraWriteRetryLoop>();
builder.Services.AddHostedService<ProjectHomeRenderLoop>();
builder.Services.AddHostedService<LogRotationService>();

// Windows has no SIGTERM h9k daemon stop can send to an arbitrary process (Decisions Log
// #3, S1-14); this watcher is the graceful-stop request in its place. Never registered on
// Unix, which keeps using a real signal, and the request file this watches for is never
// written there either.
if (OperatingSystem.IsWindows())
{
    builder.Services.AddHostedService<WindowsStopRequestWatcher>();
}

// h9k daemon stop sends SIGTERM; graceful shutdown means in-flight event appends get
// this long to finish. Agents are detached by design and keep running — adoption on
// the next start picks them back up (Decisions Log #2, #7).
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));

IHost host = builder.Build();
host.Run();
return 0;
