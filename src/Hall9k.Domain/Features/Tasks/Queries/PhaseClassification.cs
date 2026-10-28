namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// Who ran one phase of a task (idea 6e6d0200, Card 6): a person by hand, a contractor agent the
/// person delegated the phase to, the platform on its own, or nobody because a manual override
/// skipped it. Unpersisted and computed fresh on every read, so a plain enum is the right
/// vocabulary (AGENTS.md, "Value objects over primitives and enums").
/// </summary>
public enum PhaseActor
{
    Human,
    Delegated,
    Automatic,
    Skipped,
}

/// <summary>The phases <see cref="PhaseClassification"/> classifies; every one of them is a phase the passage section already times.</summary>
public enum PhaseKind
{
    Build,
    Gates,
    ReviewCycle,
    PullRequestOpen,
    /// <summary>One lap on an open pull request, in place of the build: a follow-up run's own work before its session completed.</summary>
    CloseoutLap,
}

/// <summary>A point where a person handed a task back to the platform, which the row names so the laps after it read as the daemon's own.</summary>
public enum PhaseHandOff
{
    /// <summary><c>h9k pr resolve</c>: the owner's go on a held closeout, or a hand-asked lap.</summary>
    PullRequestResolve,
    /// <summary><c>h9k task handback</c>.</summary>
    Handback,
}

/// <summary>
/// The owner a claim recorded, as <see cref="Events.TaskClaimed"/> carries it: the local Guid always,
/// and the cross-node root fingerprint when the event predates that field's absence (a null here is
/// resolved from <see cref="OwnerId"/> by the reader that needs one, never guessed).
/// </summary>
public readonly record struct ClaimOwner(Guid OwnerId, string? RootFingerprint);

/// <summary>
/// One classified phase. <see cref="Number"/> is the review cycle's or closeout lap's own 1-based
/// position across the whole task, and 0 for a phase with no number (a skipped review track).
/// <see cref="Owner"/> is the claim the phase ran under, which is what a per-owner split keys on.
/// </summary>
public sealed record ClassifiedPhase(
    PhaseKind Kind,
    PhaseActor Actor,
    Guid RunId,
    ClaimOwner? Owner,
    int Number = 0,
    bool FixedByHand = false,
    PhaseHandOff? HandOff = null,
    bool UnderSkipOverride = false);

/// <summary>
/// Which phases of a task a human ran, which a delegated agent ran, which ran automatically, and
/// which a manual override skipped, in the order they happened. Computed on read by
/// <see cref="TaskPassageQuery"/>, so it covers every stream with no backfill.
/// </summary>
public sealed record PhaseClassification(IReadOnlyList<ClassifiedPhase> Phases)
{
    /// <summary>
    /// The classification as one sentence-like row in plain words: consecutive phases that share a
    /// class read as one clause, a hand-off or an override starts a clause of its own, and a hand fix
    /// inside an automatic review cycle is named. Null when there is nothing to say.
    /// </summary>
    public string? Describe()
    {
        if (Phases.Count == 0)
        {
            return null;
        }

        int totalLaps = Phases.Count(phase => phase.Kind == PhaseKind.CloseoutLap);
        List<List<ClassifiedPhase>> groups = [];
        foreach (ClassifiedPhase phase in Phases)
        {
            if (groups.Count > 0 && !StartsNewGroup(groups[^1][^1], phase))
            {
                groups[^1].Add(phase);
                continue;
            }

            groups.Add([phase]);
        }

        string text = string.Join("; ", groups.Select(group => DescribeGroup(group, totalLaps)));
        return string.Concat(char.ToUpperInvariant(text[0]).ToString(), text.AsSpan(1));
    }

    private static bool StartsNewGroup(ClassifiedPhase previous, ClassifiedPhase next) =>
        previous.Actor != next.Actor
        || next.HandOff is not null
        || previous.UnderSkipOverride != next.UnderSkipOverride;

    private static string DescribeGroup(List<ClassifiedPhase> group, int totalLaps)
    {
        List<string> names = [];
        foreach (PhaseKind kind in group.Select(phase => phase.Kind).Distinct())
        {
            List<int> numbers = [.. group.Where(phase => phase.Kind == kind && phase.Number > 0).Select(phase => phase.Number)];
            switch (kind)
            {
                case PhaseKind.Build:
                    names.Add("build");
                    break;
                case PhaseKind.Gates:
                    names.Add("gates");
                    break;
                case PhaseKind.ReviewCycle:
                    if (numbers.Count > 0)
                    {
                        names.Add($"review cycle{(numbers.Count == 1 ? string.Empty : "s")} {JoinWords([.. numbers.Select(number => number.ToString())])}");
                    }

                    if (group.Any(phase => phase.Kind == kind && phase.Number == 0))
                    {
                        names.Add("review");
                    }

                    break;
                case PhaseKind.PullRequestOpen:
                    names.Add("PR opened");
                    break;
                case PhaseKind.CloseoutLap:
                    names.Add(totalLaps <= 1
                        ? "closeout"
                        : $"closeout lap{(numbers.Count == 1 ? string.Empty : "s")} {JoinWords([.. numbers.Select(number => number.ToString())])}");
                    break;
            }
        }

        ClassifiedPhase first = group[0];
        string actor = first.Actor switch
        {
            PhaseActor.Human => "by hand",
            PhaseActor.Delegated => "delegated",
            PhaseActor.Skipped => "skipped",
            _ => "automatic",
        };
        string overrideNote = group.Any(phase => phase.UnderSkipOverride) ? " (skip to PR)" : string.Empty;
        string handOff = first.HandOff switch
        {
            PhaseHandOff.PullRequestResolve => " after pr resolve",
            PhaseHandOff.Handback => " after handback",
            _ => string.Empty,
        };

        List<int> fixedByHand = [.. group.Where(phase => phase.FixedByHand && phase.Number > 0).Select(phase => phase.Number)];
        int reviewCycles = group.Count(phase => phase.Kind == PhaseKind.ReviewCycle && phase.Number > 0);
        string handFix = fixedByHand.Count switch
        {
            0 => string.Empty,
            _ when reviewCycles == 1 => ", fixed by hand",
            1 => $", cycle {fixedByHand[0]} fixed by hand",
            _ => $", cycles {JoinWords([.. fixedByHand.Select(number => number.ToString())])} fixed by hand",
        };

        return $"{JoinWords(names)} {actor}{overrideNote}{handOff}{handFix}";
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string JoinWords(IReadOnlyList<string> words) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        _ => $"{string.Join(", ", words.Take(words.Count - 1))} and {words[^1]}",
    };
}

/// <summary>
/// How a per-owner split keys an owner: by the cross-node root fingerprint, so one person's several
/// nodes count as one owner, and by the local owner Guid only when no fingerprint is known anywhere.
/// </summary>
public static class PhaseOwnerKey
{
    public static string Of(string? rootFingerprint, Guid ownerId) =>
        string.IsNullOrWhiteSpace(rootFingerprint) ? $"owner {ownerId}" : rootFingerprint;
}
