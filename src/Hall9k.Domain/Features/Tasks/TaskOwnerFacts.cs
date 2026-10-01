namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// The facts <see cref="TaskOwnerRule"/> judges, already resolved to root fingerprints by
/// whoever read the task (<c>TaskOwnerFactsReader</c> for the CLI), so the rule itself stays a
/// pure function over data. <see cref="Creator"/> matters only when both <see cref="Holder"/> and
/// <see cref="Assigned"/> are absent, so a reader may leave it <see cref="OwnerRootFact.Absent"/>
/// otherwise.
/// </summary>
public sealed record TaskOwnerFacts(OwnerRootFact Holder, OwnerRootFact Assigned, OwnerRootFact Creator);
