using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
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

        // Task 29b0ca1a (Marten 9 security upgrade): Marten 9 flips five defaults — append mode to
        // QuickWithServerTimestamps (was Rich), bigint event columns on, identity map for aggregates
        // on, advanced async tracking on, and Npgsql's internal logger silenced. RestoreV8Defaults()
        // holds this store to the V8-era value of all five, unchanged from what the fleet runs today:
        // Rich append mode (every fence site here follows FetchStreamStateAsync, which the Marten docs
        // call out as needing Rich), int event columns, no identity map, no async tracking (this store
        // runs only inline projections), and Npgsql logging left alone. Opting into Quick append is a
        // separate later task, not this security upgrade.
        opts.RestoreV8Defaults();

        // Idea 202383dc, ruled 2026-09-12: every event carries its origin (owner root
        // fingerprint, node id) as event metadata, stamped by this one listener — see
        // EventOriginStampingListener for how it learns its own node's identity.
        opts.Events.MetadataConfig.HeadersEnabled = true;
        opts.Listeners.Add(new EventOriginStampingListener());

        opts.Projections.Add<OwnerDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<NodeDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<ConnectionDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<ProjectDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<ProjectGitHubMembersProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<IdeaDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<EpicDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<TaskDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<TaskListItemProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<RunDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<RunListItemProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<MessageDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<MessageInboxDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<LegacyMessageAdoptionDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<UnverifiedLedgerWriteDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<InviteDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<OrchestratorPresenceDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<CourierRunDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<DecisionDetailsProjection>(ProjectionLifecycle.Inline);
        opts.Projections.Add<LearningDetailsProjection>(ProjectionLifecycle.Inline);

        // Idea d805fd8b, piece 1: h9k decide list and h9k learn list both filter on the scope
        // coordinate and nothing else, so it is the one field on either row worth an index.
        // Named here rather than left to a jsonb scan because these two tables are the only ones
        // in this store designed to grow without bound — every ruling and every run-earned lesson
        // this platform records lands in one of them, and nothing ever deletes a row.
        opts.Schema.For<DecisionDetails>().Index(decision => decision.ScopeId);
        opts.Schema.For<LearningDetails>().Index(learning => learning.ScopeId);
    }
}
