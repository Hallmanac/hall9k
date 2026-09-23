using Hall9k.Domain;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Task 29b0ca1a (Marten 9 security upgrade): boots <c>Hall9k.Daemon</c>'s exact Marten and
/// Wolverine wiring from <c>Program.cs</c> — <c>TypeLoadMode.Static</c> (Wolverine 6 moved runtime
/// codegen to the opt-in <c>WolverineFx.RuntimeCompilation</c> package, not referenced here, and
/// refuses to start in the default Dynamic mode without it; Static boots cleanly because the
/// daemon has no handlers) and <c>IntegrateWithWolverine(x =&gt; x.AutoCreate =
/// AutoCreate.CreateOrUpdate)</c> (the Wolverine message tables inherit <c>CreateOnly</c> from the
/// Marten store otherwise and throw <c>SchemaMigrationException</c> at daemon start) — against a
/// database that started as the committed Marten 8.17 dump, the same shape an installed daemon's
/// own restart migrates.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class DaemonMartenWolverineBootTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_daemon_s_host_boots_on_a_migrated_8_17_database_and_writes_through_the_DI_session()
    {
        string connectionString = await Marten8SchemaFixtureLoader.LoadIntoFreshDatabaseAsync(postgres, CancellationToken.None);
        await EventStoreSchemaGuard.EnsureCurrentAsync(connectionString, CancellationToken.None);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddMartenEventStore(connectionString, AutoCreate.CreateOnly)
            .IntegrateWithWolverine(x => x.AutoCreate = AutoCreate.CreateOrUpdate);
        builder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(IDomainAssemblyMarker).Assembly);
            opts.Policies.AutoApplyTransactions();
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        });

        using IHost host = builder.Build();
        await host.StartAsync(CancellationToken.None);
        try
        {
            Guid ownerId = DomainId.New();
            await using IDocumentSession session = host.Services.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<OwnerAggregate>(ownerId, new OwnerRegistered(ownerId, "daemon-boot", null, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
