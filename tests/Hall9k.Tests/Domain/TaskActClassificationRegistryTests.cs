using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="TaskActClassificationRegistry"/> is the one place every ProjectScoped Task, Run and Idea
/// event type is classified owner-only, member-safe, or conditional (idea 6be68ee2, trust-ledger
/// finding 5) — this is the completeness gate that fails the build the moment a new one ships
/// unclassified, the identical shape <see cref="EventScopeRegistryTests"/> already runs for scope
/// itself.
/// </summary>
public sealed class TaskActClassificationRegistryTests
{
    [Fact]
    public void Every_project_scoped_task_run_and_idea_event_type_has_a_task_act_classification()
    {
        Type[] candidates = DiscoverCandidateTypes();

        candidates.Should().NotBeEmpty("the discovery scan should find this platform's real Task/Run event types");

        Type[] unclassified = [.. candidates.Where(type => TaskActClassificationRegistry.TryClassificationOf(type) is null)];

        unclassified.Should().BeEmpty(
            "every ProjectScoped Task/Run event type must be classified in TaskActClassificationRegistry before it "
            + $"ships — found without an entry: {string.Join(", ", unclassified.Select(t => t.FullName))}");
    }

    [Fact]
    public void Every_registry_entry_is_still_a_real_discovered_candidate()
    {
        Type[] candidates = DiscoverCandidateTypes();
        TaskActClassificationRegistry.KnownEventTypes.Should().BeSubsetOf(candidates);
    }

    [Fact]
    public void An_unclassified_type_resolves_to_null_rather_than_throwing()
    {
        TaskActClassificationRegistry.TryClassificationOf(typeof(TaskActClassificationRegistryTests)).Should().BeNull();
    }

    /// <summary>Every real event type in the Tasks.Events, Run.Events or Idea namespace that is itself
    /// classified <see cref="EventScope.ProjectScoped"/> in <see cref="EventScopeRegistry"/> — the
    /// gate this registry exists for never runs against a NodeScoped Task/Run event (it never
    /// replicates at all), so those are out of scope here on purpose.</summary>
    private static Type[] DiscoverCandidateTypes() =>
        [.. typeof(EventScopeRegistry).Assembly.GetTypes()
            .Where(type => type.Namespace is "Hall9k.Domain.Features.Tasks.Events" or "Hall9k.Domain.Features.Run.Events"
                    or "Hall9k.Domain.Features.Idea"
                && !type.IsNested
                && IsRecord(type)
                && !KnownNonEventTypesInTheIdeaNamespace.Contains(type.FullName)
                && EventScopeRegistry.ClassificationOf(type) == EventScope.ProjectScoped)];

    /// <summary>
    /// The idea slice is flat: its events sit beside two value types that are never appended to a
    /// stream and so carry no scope, the same two <c>EventScopeRegistryTests</c> excludes.
    /// </summary>
    private static readonly string[] KnownNonEventTypesInTheIdeaNamespace =
    [
        "Hall9k.Domain.Features.Idea.IdeaSeed",
        "Hall9k.Domain.Features.Idea.IdeaState",
    ];

    private static bool IsRecord(Type type) =>
        type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Any(method => method.Name == "<Clone>$");
}
