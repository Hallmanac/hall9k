using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Trust;

public static class UnverifiedLedgerWriteDecider
{
    public static UnverifiedLedgerWriteObserved Observe(
        Guid projectId, string kind, string identifier, string rootFingerprint, string reason, DateTimeOffset at)
    {
        if (kind.IsBlank())
        {
            throw new DomainValidationException("Observing an unverifiable ledger write needs the kind of write it was.");
        }

        if (identifier.IsBlank())
        {
            throw new DomainValidationException("Observing an unverifiable ledger write needs the identifier it named.");
        }

        // rootFingerprint is deliberately allowed blank: a "ref" kind refusal (an append-only ref
        // integrity check catching a rewind on a ref with no single owning root — records, members,
        // prompt-addenda, run-skill) genuinely has none to name (independent pre-PR review self-check,
        // discovered writing this branch's own missing sweep test: every such refusal previously threw
        // this validation error and was silently swallowed by the sweep's own catch, so it never once
        // reached h9k status).
        if (reason.IsBlank())
        {
            throw new DomainValidationException("Observing an unverifiable ledger write needs the reason it could not be verified.");
        }

        return new UnverifiedLedgerWriteObserved(projectId, kind, identifier, rootFingerprint, reason, at);
    }

    /// <summary>A sweep whose trust chain read no longer names this stream's own writer among the
    /// unverifiable ones — the identical writer this stream was raised for became verifiable again.
    /// </summary>
    public static UnverifiedLedgerWriteResolved Resolve(DateTimeOffset at) => new(at);
}
