namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The daemon's own last observed <c>gh repo view --json isPrivate</c> read for a project, and
/// when it read it (security review idea 6be68ee2, finding 1) — mutable telemetry, not an event
/// (the <c>TaskLease</c>/<c>RunActivity</c>/<c>AutoPrReviewDefaultAdoption</c> convention): one
/// recorded observation of what GitHub last reported, overwritten on every successful read and
/// left standing on a failed one, since the most recently observed visibility is still the best
/// evidence there is. <c>h9k project show</c> reads this row rather than calling <c>gh</c> itself;
/// only the daemon's own auto-pr-review sweep — once per project per sweep, in
/// <c>AutoPrReviewEngine.SweepProjectAsync</c> — ever writes it.
/// <para>
/// <see cref="IsPrivate"/> is GitHub's own reading of the field: true for both PRIVATE and INTERNAL
/// visibility, false only for PUBLIC — the daemon's own membership gate treats INTERNAL as private
/// on exactly this basis, since nothing this row carries tells the two apart.
/// </para>
/// </summary>
public sealed class ProjectRepositoryVisibility
{
    /// <summary>The project this observation belongs to.</summary>
    public Guid Id { get; set; }

    /// <summary>GitHub's own <c>isPrivate</c> field as last successfully read.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>When this observation was made.</summary>
    public DateTimeOffset ObservedAt { get; set; }
}
