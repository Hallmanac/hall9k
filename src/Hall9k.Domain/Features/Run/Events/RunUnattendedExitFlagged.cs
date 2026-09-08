namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A deliberate headless start (<c>h9k task start</c>) exited with nobody watching, and the
/// worktree the platform found was not one it could honestly deliver on the operator's behalf —
/// uncommitted files, no commits beyond the base branch, or git itself unobservable at exit
/// (origin incident 2026-09-05, task ef2fefe5). Recorded so the task reads needs-you within
/// seconds instead of "building" indefinitely, naming what was found and the lever that clears
/// it (<c>h9k task work</c>, <c>h9k task handback</c>, or <c>h9k task release</c> — never
/// <c>h9k task deliver</c>: it runs the identical clean-tree-and-commits check this reason
/// already failed and refuses on that same ground every time, and on the one variant of this
/// reason it would not refuse outright — git unobservable — it could not verify the tree either,
/// so it would push blind rather than refuse, independent pre-PR review, cycle 3, both lenses) —
/// <see cref="RunDetails.ExitedUnattendedReason"/> is what <c>TaskPhaseComposer</c>
/// and <c>AttentionComposer</c> read to say so. Deliberately does not move <see cref="RunState"/>:
/// the run stays exactly where a live <c>h9k task work</c>/<c>h9k task handback</c>/
/// <c>h9k task release</c> already expects it (Dispatched or Running), so none of those three
/// levers needs a widened guard to keep working once this is recorded.
/// </summary>
public sealed record RunUnattendedExitFlagged(Guid Id, string Reason, DateTimeOffset FlaggedAt);
