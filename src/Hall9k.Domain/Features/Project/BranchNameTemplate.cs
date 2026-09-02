using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The name a task's branch is cut under, as a template a project owns rather than a constant the
/// platform owns. <see cref="Default"/> is exactly the name the platform cut before this setting
/// existed, so a project that sets nothing sees no change at all.
/// <para>
/// LOAD-BEARING: every token here must resolve to the same value at dispatch that it would resolve
/// to at push. <c>RunDispatched</c> records the rendered name and <c>PullRequestOpener</c> pushes
/// that recorded name verbatim, hours or days later, so a token derived from state a human can
/// still edit in between would recreate by supported feature the exact failure a hand-renamed
/// branch caused in the field on 2026-08-31: the push hits a refspec that no longer exists and the
/// task parks Failed. Each token is safe for its own reason, and each reason is a rule enforced
/// elsewhere in the domain rather than a habit:
/// </para>
/// <list type="bullet">
/// <item><c>{shortid}</c> — the task's id, immutable by construction.</item>
/// <item><c>{slug}</c> — the objective, frozen once the task leaves Draft:
/// <c>TaskDecider.Revise</c> is Draft-only and <c>TaskDecider.ReturnToDraft</c> reaches back only
/// from Published, so nothing a dispatched task can reach edits it again.</item>
/// <item><c>{key}</c> — the linked external item's key. <c>TaskDecider.LinkWorkItem</c> refuses a
/// second, different link and the platform has no unlink at all, so a key present at dispatch is
/// that task's key forever. A task carrying no reference at dispatch renders
/// <see cref="NoExternalKey"/> and keeps it: the recorded branch is never re-rendered, so a link
/// arriving mid-run changes nothing about the ref that gets pushed. This is not a rare edge —
/// under either tracking backlog policy (<c>BacklogPolicy.Jira</c> or <c>BacklogPolicy.GitHubIssues</c>)
/// it is the ordinary case for a platform-published task: a Jira card is minted minutes later by a
/// separately dispatched session, and even a GitHub issue created inline by <c>TaskPublishCommand</c>
/// routinely loses the race against <c>DispatchLoop</c>'s five-second poll. A project templating
/// <c>{key}</c> should expect <see cref="NoExternalKey"/> on most of its own published tasks, and
/// only a reliable <c>{key}</c> on tasks adopted with <c>--from-issue</c> or <c>--from-jira</c>,
/// which already carry their reference before dispatch.</item>
/// </list>
/// </summary>
[JsonConverter(typeof(BranchNameTemplateJsonConverter))]
public sealed record BranchNameTemplate
{
    /// <summary>The platform default, byte for byte the name <c>GitWorktreeManager</c> cut before templates existed.</summary>
    public static readonly BranchNameTemplate Default = new("task/{shortid}-{slug}");

    /// <summary>The task's short id: the last eight characters of its UUIDv7, per <see cref="DomainId.Short"/>.</summary>
    public const string ShortIdToken = "shortid";

    /// <summary>The objective, lowercased and hyphenated, capped at <see cref="MaximumSlugLength"/> characters.</summary>
    public const string SlugToken = "slug";

    /// <summary>The linked external item's key — a Jira key, a GitHub issue number — or <see cref="NoExternalKey"/>.</summary>
    public const string KeyToken = "key";

    /// <summary>
    /// What <c>{key}</c> renders as on a task carrying no external reference. Spelled out rather
    /// than elided, because an empty segment would silently collapse <c>{key}-{slug}</c> into a
    /// name that reads as though a key had been part of it, and guessing one would put a card
    /// number on a branch nobody filed a card for (AGENTS.md, "never guess at unobserved facts").
    /// </summary>
    public const string NoExternalKey = "no-key";

    private const int MaximumSlugLength = 30;
    private const int MaximumKeyLength = 40;

    /// <summary>The task <see cref="Parse"/> validates against; its short id is <c>0abcdef1</c>.</summary>
    private static readonly Guid SampleTaskId = Guid.Parse("00000000-0000-0000-0000-00000abcdef1");

    /// <summary>
    /// A rendered branch name becomes a ref file path under the repository, beside a worktree path
    /// that is already long on a Windows node; 200 characters is generous for every convention
    /// anyone has asked for and still leaves room under the platform's shortest path ceiling.
    /// </summary>
    private const int MaximumRenderedLength = 200;

    /// <summary>
    /// What <c>GitWorktreeManager.ResolveBranchNameAsync</c>'s collision retry adds on top of a
    /// rendered name (<c>-r</c> plus four hex digits), reserved out of the ceiling above so a
    /// template accepted here cannot produce a retry name that overruns it. Without the reservation
    /// a 200-character name would be accepted and its retry would be 206 — refusing that at the
    /// retry is the worst possible moment, since the run has already done its work.
    /// </summary>
    private const int CollisionSuffixLength = 6;

    private const int MaximumRenderedBaseLength = MaximumRenderedLength - CollisionSuffixLength;

    /// <summary>How much of a refused template <see cref="Legible"/> is willing to echo back.</summary>
    private const int MaximumRelayedLength = 80;

    /// <summary>
    /// Every token <see cref="Render"/> recognizes, keyed to the renderer it dispatches to — the
    /// single source both <see cref="Render"/> and <c>BranchNameTokensAreFixedAtDispatchTests</c>
    /// read from, so a token added here without a freezing gate in that test file fails it rather
    /// than reaching a project's branch names unproven.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<Guid, string?, string?, string>> TokenRenderers =
        new Dictionary<string, Func<Guid, string?, string?, string>>
        {
            [ShortIdToken] = (taskId, _, _) => DomainId.Short(taskId),
            [SlugToken] = (_, objective, _) => Slug(objective),
            [KeyToken] = (_, _, externalKey) => Key(externalKey),
        };

    /// <summary>The token set this build ships, for the guard test above to check itself against.</summary>
    internal static IEnumerable<string> KnownTokens => TokenRenderers.Keys;

    public string Value { get; }

    private BranchNameTemplate(string value) => Value = value;

    public static implicit operator string(BranchNameTemplate? template) => template?.Value ?? Default.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="BacklogPolicy"/> convention: a value built
    /// this way can carry anything, which is what lets <see cref="Parse"/> be the one place a
    /// human's own input is actually vetted. <see cref="Render"/> vets it again at dispatch, so a
    /// template that reached the stream some other way still cannot cut an illegal ref.
    /// </summary>
    public static implicit operator BranchNameTemplate(string? value) =>
        value.IsBlank() ? Default : new BranchNameTemplate(value);

    /// <summary>Lenient mapping for a value already on the stream; blank reads as the platform default.</summary>
    public static BranchNameTemplate FromInput(string? value) =>
        value.IsBlank() ? Default : new BranchNameTemplate(value.Trim());

    /// <summary>
    /// The strict form a human's own input goes through. Validation is a handful of real renders
    /// of representative tasks — the worst case for length, a task carrying no key, and a set of
    /// adversarial short values that probe the one rule length alone cannot prove (see the
    /// comment inside) — rather than a second, parallel set of rules, so what is accepted here is
    /// exactly what will cut a branch at dispatch. A template that renders an illegal ref is
    /// refused at the command line, where the human can fix it, rather than at the dispatch that
    /// would otherwise fail the run.
    /// </summary>
    public static BranchNameTemplate Parse(string? value)
    {
        if (value.IsBlank())
        {
            return Default;
        }

        BranchNameTemplate candidate = new(value.Trim());

        string maximumSlug = new('a', MaximumSlugLength);
        string maximumKey = new('K', MaximumKeyLength);

        // The two worst-case-for-length renders (both tokens maxed out, then a key-less task)
        // prove every rule except one: '.lock'. That rule is a suffix rule, not a
        // barred-character rule, and a token can never contain '.', so the only way a rendered
        // component ends in '.lock' is some combination of literal template text and the two
        // adversarial tokens' values assembling into it — {shortid} always renders exactly eight
        // characters, too long to sit inside the four-character run '.lock' requires and unable
        // to supply the leading '.' itself, so it can never contribute a character toward
        // spelling "lock", leaving {slug} and {key} as the only tokens whose value an operator controls.
        // "lock" has exactly ten contiguous substrings ("l", "o", "c", "k", "lo", "oc", "ck",
        // "loc", "ock", "lock"), and trying each one as a token's rendered value — every token in
        // combination with every value the other one could take, since {slug} and {key} can sit
        // on either side of a literal '.' or directly beside each other — exhausts every way the
        // two of them, together with the template's own fixed literal text, could complete a
        // literal '.' into "lock": a single token supplying a prefix, a suffix or a middle run
        // with literal on both sides, or two adjacent tokens each supplying one half of a split
        // that a lone token could never spell on its own.
        candidate.Render(SampleTaskId, maximumSlug, maximumKey);
        candidate.Render(SampleTaskId, maximumSlug, null);

        string[] lockSubstrings = ["l", "o", "c", "k", "lo", "oc", "ck", "loc", "ock", "lock"];
        string[] slugCandidates = [.. lockSubstrings, maximumSlug];
        string?[] keyCandidates = [.. lockSubstrings, maximumKey, null];

        foreach (string slugCandidate in slugCandidates)
        {
            foreach (string? keyCandidate in keyCandidates)
            {
                candidate.Render(SampleTaskId, slugCandidate, keyCandidate);
            }
        }

        return candidate;
    }

    /// <summary>
    /// Whether this template references the named token — <see cref="KeyToken"/>,
    /// <see cref="SlugToken"/> or <see cref="ShortIdToken"/> — tokenized the same way
    /// <see cref="Render"/> parses a <c>{...}</c> block: trimmed and case-insensitive, so
    /// <c>{ key }</c> and <c>{KEY}</c> are found exactly as reliably as <c>{key}</c>, unlike a
    /// plain substring search over <see cref="Value"/>.
    /// </summary>
    public bool UsesToken(string token)
    {
        for (int index = 0; index < Value.Length; index++)
        {
            if (Value[index] != '{')
            {
                continue;
            }

            int close = Value.IndexOf('}', index);
            if (close < 0)
            {
                break;
            }

            if (string.Equals(Value[(index + 1)..close].Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            index = close;
        }

        return false;
    }

    /// <summary>
    /// The branch name for one task. Called once, when the branch is cut, and the result is
    /// recorded on <c>RunDispatched</c> — nothing downstream re-renders it (see the type remarks).
    /// </summary>
    /// <param name="taskId">The task the branch belongs to.</param>
    /// <param name="objective">The task's objective, the source of <c>{slug}</c>.</param>
    /// <param name="externalKey">
    /// The linked external item's key, or null when the task carries no reference — rendered as
    /// <see cref="NoExternalKey"/> rather than as an empty segment.
    /// </param>
    public string Render(Guid taskId, string? objective, string? externalKey)
    {
        StringBuilder rendered = new();
        for (int index = 0; index < Value.Length; index++)
        {
            char character = Value[index];
            if (character == '}')
            {
                throw Refuse($"'}}' at position {index} closes a token that was never opened.");
            }

            if (character != '{')
            {
                rendered.Append(character);
                continue;
            }

            int close = Value.IndexOf('}', index);
            if (close < 0)
            {
                throw Refuse($"'{{' at position {index} is never closed.");
            }

            string token = Value[(index + 1)..close].Trim().ToLowerInvariant();
            rendered.Append(TokenRenderers.TryGetValue(token, out Func<Guid, string?, string?, string>? renderer)
                ? renderer(taskId, objective, externalKey)
                : throw Refuse($"'{{{Legible(token)}}}' is not a token."));
            index = close;
        }

        string branch = rendered.ToString();
        EnsureLegalBranchName(branch);
        return branch;
    }

    /// <summary>
    /// The subset of <c>git check-ref-format --branch</c> that a rendered name can actually
    /// violate. Implemented here rather than shelled out to git because the domain runs nowhere
    /// near a repository and this has to answer at <c>h9k project set</c> time, on a machine whose
    /// project may not even be materialised yet.
    /// </summary>
    private void EnsureLegalBranchName(string branch)
    {
        if (branch.Length == 0)
        {
            throw Refuse("it renders an empty name.");
        }

        if (branch.Length > MaximumRenderedBaseLength)
        {
            throw Refuse(
                $"it renders {branch.Length} characters, past the {MaximumRenderedBaseLength}-character "
                + "ceiling a branch name has room for beside a worktree path, once the run suffix a "
                + "retried task's branch gets is reserved.");
        }

        // Enumerated as Rune, not char: a foreach over a string yields UTF-16 code units, and a
        // non-BMP Unicode formatting character (a TAG character such as U+E0041, category Cf) is
        // a surrogate pair whose two halves each categorise as Surrogate rather than Format —
        // invisible to a per-char check of char.GetUnicodeCategory even though the whole
        // character is exactly what that check exists to catch (independent pre-PR review,
        // cycle 3, adversarial).
        foreach (Rune rune in branch.EnumerateRunes())
        {
            // Every character these two rules bar is ASCII, so a rune outside the BMP can never
            // match either — the cast is exact whenever it can possibly matter.
            if (rune.Value <= char.MaxValue)
            {
                char character = (char)rune.Value;

                // char.IsControl covers DEL as well as the C0 range, so both ends of git's own
                // "no control characters" rule are here.
                if (char.IsControl(character) || character is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                {
                    throw Refuse($"{Describe(character)} is a character git does not allow in a ref name.");
                }

                // git allows '"' in a ref name, but GitWorktreeManager interpolates the rendered
                // branch straight into a single ProcessStartInfo.Arguments string, where '"' is
                // the quoting character itself — a name carrying one parses into the wrong argv,
                // breaking every git invocation this branch is used in, exactly the failure this
                // validation exists to catch at project-set time instead of at dispatch.
                if (character == '"')
                {
                    throw Refuse(
                        $"{Describe(character)} is a character git allows in a ref name, but this platform "
                        + "passes the rendered name through a single quoted command-line argument, which a "
                        + "'\"' would break out of.");
                }
            }

            // The one rule here that is stricter than git's own, and deliberately: git takes a
            // Unicode formatting character in a ref name quite happily, but a branch name is
            // printed into terminals, logs, pull-request bodies and this platform's own board, and
            // a bidirectional override makes one name read on screen as another. It is the same
            // concern the Legible relay below exists for, applied to the value rather than to the
            // refusal explaining it. No branch convention anybody has asked for needs one.
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                throw Refuse(
                    $"{Describe(rune)} is a Unicode formatting character. git would take it, but "
                    + "a branch name is printed wherever this platform reports work, and a "
                    + "bidirectional override there makes one name read as another.");
            }
        }

        if (branch.Contains("..", StringComparison.Ordinal))
        {
            throw Refuse("git does not allow '..' in a ref name.");
        }

        if (branch.Contains("@{", StringComparison.Ordinal))
        {
            throw Refuse("git does not allow '@{' in a ref name.");
        }

        if (branch is "@")
        {
            throw Refuse("'@' on its own is not a ref name git accepts.");
        }

        if (branch[0] == '-')
        {
            throw Refuse("a branch name cannot begin with '-' — git reads it as an option.");
        }

        if (branch[^1] == '.')
        {
            throw Refuse("a ref name cannot end with '.'.");
        }

        foreach (string component in branch.Split('/'))
        {
            if (component.Length == 0)
            {
                throw Refuse("a ref name has no empty path components — it cannot start or end with '/', or contain '//'.");
            }

            if (component[0] == '.')
            {
                throw Refuse($"'{Legible(component)}' begins with '.', which git does not allow in a ref path component.");
            }

            if (component.EndsWith(".lock", StringComparison.Ordinal))
            {
                throw Refuse($"'{Legible(component)}' ends with '.lock', which git reserves for its own lock files.");
            }
        }
    }

    private DomainValidationException Refuse(string rule) =>
        new($"'{Legible(Value)}' is not a usable branch-name template: {rule} "
            + $"Tokens are {{{ShortIdToken}}} (the task's short id), {{{SlugToken}}} (its objective, "
            + $"hyphenated) and {{{KeyToken}}} (the linked Jira key or GitHub issue number, or "
            + $"'{NoExternalKey}' when the task carries no reference); everything else is literal. "
            + $"The platform default is \"{Default.Value}\", and 'none' restores it.");

    /// <summary>
    /// The objective as branch-name material, unchanged from the shape <c>GitWorktreeManager</c>
    /// produced before templates existed: lowercase alphanumerics, every run of anything else
    /// collapsed to a single hyphen, capped and trimmed. Starts and ends with an alphanumeric,
    /// which is what lets <see cref="Parse"/> prove legality from a fixed-length sample.
    /// </summary>
    private static string Slug(string? objective)
    {
        StringBuilder slug = new();
        foreach (char character in (objective ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }

            if (slug.Length >= MaximumSlugLength)
            {
                break;
            }
        }

        string result = slug.ToString().Trim('-');
        return result.IsBlank() ? "task" : result;
    }

    /// <summary>
    /// The external key as branch-name material. Case is kept, because ARX-14 is how the team
    /// writes it and a branch named arx-14 is a different thing to look at in a branch list.
    /// Dots are deliberately not carried through even though git allows them mid-name: a key
    /// ending in <c>.lock</c> or containing <c>..</c> would render an illegal ref out of a
    /// template <see cref="Parse"/> had already accepted, and no real key needs one.
    /// </summary>
    private static string Key(string? externalKey)
    {
        if (externalKey.IsBlank())
        {
            return NoExternalKey;
        }

        StringBuilder key = new();
        foreach (char character in externalKey)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            {
                key.Append(character);
            }
            else if (key.Length > 0 && key[^1] != '-')
            {
                key.Append('-');
            }

            if (key.Length >= MaximumKeyLength)
            {
                break;
            }
        }

        string result = key.ToString().Trim('-', '_');
        return result.IsBlank() ? NoExternalKey : result;
    }

    /// <summary>
    /// What a refused template is safe to be quoted as, the <see cref="BacklogPolicy"/> convention
    /// (<c>RelayedPolicy</c>): the value came off a command line and the refusal is printed to a
    /// terminal, so a control character or a bidirectional override cannot reach the refusal it is
    /// explaining, and an unbounded argument cannot be echoed whole.
    /// <para>
    /// The line it draws is printable ASCII rather than a hand-picked allowlist, which is wider
    /// than the sibling relays deliberately. Origin incident (2026-09-01, this branch's own
    /// self-review, running the procedure the docs describe): a narrow allowlist rewrote the
    /// <c>:</c> in <c>feature/{slug}:{shortid}</c> to <c>?</c>, and the very next clause named
    /// <c>'?'</c> as the illegal character — a refusal that told the operator their template
    /// contained a character it did not. Every character git objects to is printable ASCII, so
    /// showing that range is what makes the message true; the sanitizing still catches everything
    /// a terminal can actually be attacked with, all of which is outside it.
    /// </para>
    /// </summary>
    private static string Legible(string value)
    {
        // Rune, not char, for the same reason EnsureLegalBranchName enumerates runes: a non-BMP
        // character is a surrogate pair, and reading it as two chars would render it as two '?'s
        // rather than by its own code point.
        StringBuilder visible = new();
        int runeCount = 0;
        bool truncated = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (runeCount >= MaximumRelayedLength)
            {
                truncated = true;
                break;
            }

            visible.Append(Readable(rune));
            runeCount++;
        }

        return truncated ? visible.Append('…').ToString() : visible.ToString();
    }

    private static string Readable(Rune rune) => Printable(rune) ? rune.ToString() : "?";

    /// <summary>
    /// One offending character, named so the operator can find it: itself when it is printable,
    /// and its code point when it is not, since <c>'?'</c> for an unprintable character reads as a
    /// claim about a character the template does not contain.
    /// </summary>
    private static string Describe(char character) => Describe(new Rune(character));

    private static string Describe(Rune rune) =>
        Printable(rune) ? $"'{rune}'" : $"U+{rune.Value:X4}";

    private static bool Printable(Rune rune) => rune.IsAscii && rune.Value is >= ' ' and <= '~';

    public bool Equals(BranchNameTemplate? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class BranchNameTemplateJsonConverter : JsonConverter<BranchNameTemplate>
    {
        // Reading is deliberately not Parse, the BacklogPolicy convention: a value already on an
        // event stream is a record of what was set, and a rule tightened later must not make an
        // old document unreadable. Render vets it again before it can cut anything.
        public override BranchNameTemplate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, BranchNameTemplate value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
