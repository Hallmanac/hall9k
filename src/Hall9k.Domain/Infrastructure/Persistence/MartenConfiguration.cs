using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx;
using JasperFx.Events.Projections;
using Marten;
using Weasel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Hall9k.Domain.Infrastructure.Persistence;

public static class MartenConfiguration
{
    /// <summary>
    /// The single home for Hall9k's Marten setup. The CLI consumes it through a lightweight
    /// service collection (no Wolverine host — Decisions Log #8); the daemon chains
    /// IntegrateWithWolverine() onto the returned expression (S1-05).
    /// </summary>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression AddMartenEventStore(
        this IServiceCollection services,
        string connectionString,
        AutoCreate autoCreate = AutoCreate.CreateOnly,
        Action<StoreOptions>? configure = null) =>
        services.AddMarten(opts =>
            {
                opts.Connection(connectionString);
                opts.ConfigureHall9k(autoCreate);
                configure?.Invoke(opts);
            })
            .UseLightweightSessions();

    /// <summary>Shared by AddMartenEventStore and DocumentStore.For-based tests.</summary>
    public static void ConfigureHall9k(this StoreOptions opts, AutoCreate autoCreate)
    {
        opts.UseSystemTextJsonForSerialization(enumStorage: EnumStorage.AsString, casing: Casing.CamelCase);
        opts.AutoCreateSchemaObjects = autoCreate;

        opts.Projections.Add<OwnerDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<NodeDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<ConnectionDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<ProjectDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<IdeaDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<TaskDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<TaskListItemProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<RunDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<RunListItemProjection>(ProjectionLifecycle.Inline);

        // ProjectHomeRenderEngine's archive sweep filters every project's runs by TaskId on
        // every pass, with a parameter array that only grows as the project accumulates Done
        // tasks (backlog 51) — without this, that query is a sequential scan of the whole runs
        // table rather than an index lookup.
        opts.Schema.For<RunListItem>().Duplicate(item => item.TaskId);
    }
}
