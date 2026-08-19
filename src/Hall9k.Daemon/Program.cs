using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Review;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx;
using Wolverine;
using Wolverine.Marten;

// Single-instance guard before anything expensive. The refusal exits 0 on purpose: an
// autostart LaunchAgent with KeepAlive that loses this race must not be thrash-restarted
// by launchd every throttle interval (Decisions Log #30); the log carries the refusal.
using SingleInstanceGuard? instance = SingleInstanceGuard.TryAcquire(DaemonRuntime.LockFile, DaemonRuntime.PidFile);
if (instance is null)
{
    DaemonProcessDescriptor? running = DaemonPidFile.TryRead(DaemonRuntime.PidFile);
    Console.Error.WriteLine(running is null
        ? "h9kd is already running (another instance holds the lock). Refusing to start a second."
        : $"h9kd is already running (pid {running.ProcessId}, started {running.StartedAt:u}). Refusing to start a second.");
    return 0;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

// Stdout IS the log file when the CLI starts the daemon detached, so how this reads is
// part of the lifecycle contract, not a preference (Decisions Log #30).
DaemonLogging.Configure(builder.Logging);

string connectionString = Hall9kDatabase.ResolveConnectionString(
    builder.Configuration.GetConnectionString("hall9k"));

builder.Services.AddOptions<DaemonOptions>().Bind(builder.Configuration.GetSection(DaemonOptions.SectionName));
builder.Services.AddSingleton(new DaemonConnection(connectionString));
builder.Services.AddSingleton<NodeContext>();
builder.Services.AddSingleton<IProcessManager, UnixProcessManager>();
builder.Services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
builder.Services.AddSingleton<DispatchEngine>();
builder.Services.AddSingleton<IExecutor, ClaudeExecutor>();
builder.Services.AddSingleton<VerificationRunner>();
builder.Services.AddSingleton<ReviewEngine>();
builder.Services.AddSingleton<PullRequestOpener>();
builder.Services.AddSingleton<RunSupervisor>();
builder.Services.AddSingleton<RunLauncher>();
builder.Services.AddSingleton<IPullRequestInspector, GitHubPullRequestInspector>();
builder.Services.AddSingleton<CloseoutEngine>();

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

// h9k daemon stop sends SIGTERM; graceful shutdown means in-flight event appends get
// this long to finish. Agents are detached by design and keep running — adoption on
// the next start picks them back up (Decisions Log #2, #7).
builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(30));

IHost host = builder.Build();
host.Run();
return 0;
