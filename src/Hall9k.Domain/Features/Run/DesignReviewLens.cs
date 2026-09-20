namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One of the seven things a design review reads a pull request for (idea b9b09779, piece 3), in
/// the fixed order its report prints them. A sealed record with static instances and an
/// <see cref="Unknown"/> sentinel per the house type discipline (TASK-MODEL.md §8), never an
/// enum, and closed rather than open: the report owes a section per lens whether or not the
/// change touched what that lens looks at, so a lens nobody could name would be a section the
/// report silently never has.
/// <para>
/// Distinct from <see cref="ReviewLens"/>, which is the engineer review's own adversarial /
/// conformance / verify split and names whole review PASSES. These name sections inside one
/// pass: the design review is a single session that answers all seven.
/// </para>
/// </summary>
public sealed record DesignReviewLens
{
    /// <summary>Whether the change is usable: the flow, the affordances, the states a person actually lands in.</summary>
    public static readonly DesignReviewLens UserExperience = new(
        "user-experience", "User experience",
        "the change touches nothing a person interacts with");

    /// <summary>Whether the change matches the proposed design it was drawn against — a Figma link, an image set, or a prototype.</summary>
    public static readonly DesignReviewLens ProposedDesign = new(
        "proposed-design", "Conformance to the proposed design",
        "the change alters nothing the proposed design covers");

    /// <summary>Transitions and animations: what moves, how long, and whether reduced-motion is honoured.</summary>
    public static readonly DesignReviewLens Motion = new(
        "motion", "Motion (transitions and animations)",
        "the change adds, removes and alters no transition or animation");

    /// <summary>How the styling is written: specificity, duplication, layout technique, dead rules.</summary>
    public static readonly DesignReviewLens CssPractice = new(
        "css-practice", "CSS practice",
        "the change touches no stylesheet or style rule");

    /// <summary>Whether a person using a keyboard, a screen reader, or a high-contrast display can do what the change added.</summary>
    public static readonly DesignReviewLens Accessibility = new(
        "accessibility", "Accessibility",
        "the change adds no markup, control or state anything assistive would meet");

    /// <summary>The visual judgment: type, spacing, colour, weight, and whether it looks like the rest of the product.</summary>
    public static readonly DesignReviewLens LookAndFeel = new(
        "look-and-feel", "Look and feel",
        "the change alters nothing anybody sees");

    /// <summary>Whether the change spends the project's own tokens and components rather than inventing beside them.</summary>
    public static readonly DesignReviewLens DesignSystem = new(
        "design-system", "Design system",
        "the change introduces no token, component or pattern the design system has an opinion about");

    /// <summary>A slug nobody could read — a lens a later build knew and this one does not. Never printed as a section.</summary>
    public static readonly DesignReviewLens Unknown = new("", "Unrecognized lens", "");

    /// <summary>The whole set, in the fixed order the report prints its sections.</summary>
    public static readonly IReadOnlyList<DesignReviewLens> All =
        [UserExperience, ProposedDesign, Motion, CssPractice, Accessibility, LookAndFeel, DesignSystem];

    /// <summary>The slug a session tags its own block with, and the one this vocabulary is matched on.</summary>
    public string Slug { get; }

    /// <summary>This lens's own heading inside the design review's section of the findings report.</summary>
    public string Heading { get; }

    /// <summary>
    /// The half-sentence the report completes when a session answered nothing under this lens:
    /// the usual reason a lens goes unanswered, named as the usual reason rather than asserted
    /// about this change, since a session that skipped the lens reported no such thing. Written
    /// here rather than in the composer so each lens names what specifically tends to be absent,
    /// instead of seven sections all reading the same way.
    /// </summary>
    public string NotApplicableBecause { get; }

    private DesignReviewLens(string slug, string heading, string notApplicableBecause)
    {
        Slug = slug;
        Heading = heading;
        NotApplicableBecause = notApplicableBecause;
    }

    /// <summary>True for one of the seven; false for <see cref="Unknown"/>.</summary>
    public bool HasValue => Slug.IsNotBlank();

    /// <summary>The tolerant read — anything unrecognized is <see cref="Unknown"/>, never a guess.</summary>
    public static DesignReviewLens Read(string? value)
    {
        string slug = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return All.FirstOrDefault(lens => lens.Slug == slug) ?? Unknown;
    }

    public bool Equals(DesignReviewLens? other) => other is not null && Slug == other.Slug;

    public override int GetHashCode() => Slug.GetHashCode();

    public override string ToString() => Slug;
}
