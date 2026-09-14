using System.Reflection;
using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="EventScopeRegistry"/> is the one place every event type is classified
/// project-scoped, node-scoped, or owner-scoped (idea 202383dc). This is the completeness gate
/// acceptance criterion 2 asks for: a reflection scan discovers every real event type this
/// platform ships — independently of the registry itself — and fails the build the moment one of
/// them has no entry there, so a new event type can never ship unclassified.
/// <para>
/// An event record lives either under a feature's own <c>.Events</c> sub-namespace (Tasks, Run,
/// Project — unambiguous, since nothing but events lives there) or, for the five features with no
/// separate <c>Events</c> folder (Owner, Node, Connection, Epic, Idea), directly in that feature's
/// flat namespace alongside value objects. A record there is only an event when it is not one of
/// <see cref="KnownNonEventValueTypesInFlatNamespaces"/> — a short, hand-verified exclusion list
/// (<c>CredentialKind</c>/<c>CredentialReference</c> in Connection, <c>EpicState</c> in Epic,
/// <c>IdeaSeed</c>/<c>IdeaState</c> in Idea, <c>NodeLaunchHoldEpisode</c>/<c>ProjectRunLoad</c> in
/// Node), rather than a type this scan could tell apart from a real event on its own shape: both
/// are <c>sealed record</c> types in the same namespace. Getting one of those five wrong fails
/// safe either way — a real event missing from the list would surface here as "found but not
/// classified", the same failure a genuinely new, unclassified event type produces; a value type
/// wrongly left off the list would surface as "classify this", not as a silent gap.
/// </para>
/// </summary>
public sealed class EventScopeRegistryTests
{
    private static readonly string[] EventsSubNamespaceSuffix = [".Events"];

    private static readonly string[] FlatFeatureNamespacesWithEvents =
    [
        "Hall9k.Domain.Features.Owner",
        "Hall9k.Domain.Features.Node",
        "Hall9k.Domain.Features.Connection",
        "Hall9k.Domain.Features.Epic",
        "Hall9k.Domain.Features.Idea",
    ];

    /// <summary>
    /// Every non-event <c>sealed record</c> this scan would otherwise mistake for one, verified
    /// by hand against the actual contents of the five flat namespaces above (event stamping,
    /// idea 202383dc) — see this class's own doc comment for why the list is safe to get wrong.
    /// </summary>
    private static readonly string[] KnownNonEventValueTypesInFlatNamespaces =
    [
        "CredentialKind",
        "CredentialReference",
        "EpicState",
        "IdeaSeed",
        "IdeaState",
        "NodeLaunchHoldEpisode",
        "ProjectRunLoad",
    ];

    [Fact]
    public void Every_event_type_this_platform_ships_has_a_scope_classification()
    {
        Type[] candidateEventTypes = DiscoverCandidateEventTypes();

        // A positive control, the same shape ContainerRoutingGuardTests uses for its own source
        // scan: if this ever comes back empty, the discovery predicate itself has gone blind
        // rather than the registry having genuinely caught up to every event, and the assertion
        // below would pass for the wrong reason.
        candidateEventTypes.Should().NotBeEmpty("the discovery scan should find this platform's real event types");

        Type[] unclassified = [.. candidateEventTypes.Where(type => !TryClassify(type))];

        unclassified.Should().BeEmpty(
            "every event type must be classified in EventScopeRegistry before it ships — " +
            $"found without an entry: {string.Join(", ", unclassified.Select(t => t.FullName))}");
    }

    [Fact]
    public void Every_registry_entry_is_still_a_real_discovered_event_type()
    {
        // The other direction: a registry entry for a type this scan no longer discovers (renamed,
        // moved out of an Events namespace, deleted) is a stale entry, not a defect in production
        // code — but it means the registry has drifted from what it claims to classify.
        Type[] candidateEventTypes = DiscoverCandidateEventTypes();
        EventScopeRegistry.KnownEventTypes.Should().BeSubsetOf(candidateEventTypes);
    }

    [Fact]
    public void An_unclassified_type_throws_with_the_type_name_named()
    {
        Action act = () => EventScopeRegistry.ClassificationOf(typeof(EventScopeRegistryTests));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{nameof(EventScopeRegistryTests)}*");
    }

    private static bool TryClassify(Type type)
    {
        try
        {
            EventScopeRegistry.ClassificationOf(type);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Type[] DiscoverCandidateEventTypes() =>
        [.. typeof(EventScopeRegistry).Assembly.GetTypes()
            .Where(type => type.Namespace is not null
                && type.Namespace.StartsWith("Hall9k.Domain.Features", StringComparison.Ordinal)
                && !type.IsNested
                && IsRecord(type)
                && (EventsSubNamespaceSuffix.Any(suffix => type.Namespace.EndsWith(suffix, StringComparison.Ordinal))
                    || (FlatFeatureNamespacesWithEvents.Contains(type.Namespace)
                        && !KnownNonEventValueTypesInFlatNamespaces.Contains(type.Name))))];

    /// <summary>
    /// A record type declares the compiler-synthesized <c>&lt;Clone&gt;$</c> method; a plain class
    /// or a static class does not. The same detection <c>FakeEvent</c>'s own header dictionary
    /// implies is needed nowhere else in this suite, since this is the only place code asks
    /// "is this type shaped like a record" rather than "is it shaped like a specific one".
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(method => method.Name == "<Clone>$");
}
