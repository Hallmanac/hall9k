using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Wolverine;
using Wolverine.Marten;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

string connectionString = Hall9kDatabase.ResolveConnectionString(
    builder.Configuration.GetConnectionString("hall9k"));

builder.Services.AddOptions<DaemonOptions>().Bind(builder.Configuration.GetSection(DaemonOptions.SectionName));
builder.Services.AddSingleton(new DaemonConnection(connectionString));
builder.Services.AddSingleton<NodeContext>();
builder.Services.AddSingleton<IProcessManager, UnixProcessManager>();
builder.Services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
builder.Services.AddSingleton<DispatchEngine>();

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

IHost host = builder.Build();
host.Run();
