using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// How prose an agent writes for people has to read on this project: the house style carried
/// verbatim into every prompt that asks an agent to compose text posted to GitHub under the
/// owner's login, and the rule the platform's own mechanical check
/// (<see cref="WritingConventionsCheck"/>) reads before it posts any of it.
/// <para>
/// Origin incident (2026-09-09): follow-up run 01a085d9 of arx-platform task 01a083d8 answered a
/// Copilot review on AgelessRx/arx-platform#2042 with a top-level comment under Brian's login
/// carrying em dashes in most of its paragraphs. The rule against them existed in every
/// orchestrator recipe on both nodes and in the operator's own user-level CLAUDE.md, and reached
/// none of them: <c>--setting-sources project</c> drops CLAUDE.md from a dispatched session, and
/// no composition prompt the daemon writes carried the rule itself. A convention that lives only
/// where the composing session cannot read it is not a convention.
/// </para>
/// <para>
/// Free text rather than a closed vocabulary, unlike <see cref="CommitStyle"/> and
/// <see cref="BacklogPolicy"/>: a house style is a team's own sentence, not a value the platform
/// can enumerate. It follows those settings' idiom in every other respect —
/// <see cref="Default"/> is both the untouched default and what <c>'default'</c> restores, and
/// the value is recorded on <c>ProjectSettingsChanged</c> like any other. Only the two rules the
/// default text states are mechanically checkable; the rest is prose an agent reads and obeys,
/// which is exactly the split <see cref="WritingConventionsCheck"/> documents.
/// </para>
/// </summary>
[JsonConverter(typeof(WritingConventionsJsonConverter))]
public sealed record WritingConventions
{
    /// <summary>
    /// The platform default, and the text every project reads until it sets its own. Written as
    /// the sentences an agent is handed rather than as a bullet list, because it is rendered
    /// verbatim into prompts and a project that replaces it will write sentences too.
    /// </summary>
    public static readonly WritingConventions Default = new(
        "No em dashes (U+2014); use commas, semicolons, colons, periods, or parentheses instead. "
        + "Full sentences over telegraphic fragments. No AI attribution anywhere in it: no "
        + "\"Generated with Claude\", no Co-Authored-By trailer.");

    /// <summary>
    /// How long a project's own conventions text may be. Generous for any house style anyone has
    /// asked for, and bounded because this text is pasted verbatim into every composition prompt
    /// the platform writes: an unbounded value would spend a dispatched session's context on one
    /// setting.
    /// </summary>
    public const int MaximumLength = 4000;

    public string Value { get; }

    private WritingConventions(string value) => Value = value;

    public static implicit operator string(WritingConventions? conventions) => conventions?.Value ?? Default.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="BacklogPolicy"/> convention: a value built
    /// this way can carry anything, which is what lets <see cref="Handlers.ProjectDecider.ChangeSettings"/>
    /// be the one place that enforces the rules. Blank reads as <see cref="Default"/>, the same
    /// "absent means the platform's own" idiom <see cref="BranchNameTemplate"/> uses.
    /// </summary>
    public static implicit operator WritingConventions(string? value) =>
        value.IsBlank() ? Default : new WritingConventions(value);

    /// <summary>
    /// The strict form a human's own input goes through. Blank restores the default, for the same
    /// reason <c>--backlog-routing ""</c> clears its guidance: absent and cleared are one state
    /// here, because a project with no conventions of its own still gets the platform's.
    /// <para>
    /// Only two things are refused, and both because this text is rendered somewhere a reader
    /// cannot defend themselves against it: a value past <see cref="MaximumLength"/>, and a
    /// control or layout-override character, which reaches an operator's terminal through
    /// <c>h9k project show</c> and a dispatched session's prompt file alike. Everything else is
    /// accepted as written, including every non-ASCII character a real house style needs; the
    /// default text itself names U+2014 out loud.
    /// </para>
    /// </summary>
    public static WritingConventions Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            return Default;
        }

        if (trimmed.Length > MaximumLength)
        {
            throw new DomainValidationException(
                $"The writing conventions are {trimmed.Length} characters, past the {MaximumLength}-character "
                + "limit. This text is pasted verbatim into every prompt that asks an agent to compose prose "
                + "for people, so a long one spends the session's context rather than steering it. State the "
                + "house style and point at a skill for the rest.");
        }

        foreach (char character in trimmed)
        {
            if (Illegible(character))
            {
                throw new DomainValidationException(
                    $"The writing conventions contain U+{(int)character:X4}, which is a control or "
                    + "layout-override character. This text is printed to a terminal by h9k project show and "
                    + "written into a prompt file for every composing session, and neither can show what that "
                    + "character does. Newlines and tabs are fine; nothing else in that range is.");
            }
        }

        return new WritingConventions(trimmed);
    }

    /// <summary>
    /// A character no reader can see and no terminal can be trusted to render harmlessly: a
    /// control character other than the ones that mean "new line" and "indent", or one of the
    /// bidirectional and zero-width overrides that make displayed text disagree with stored text.
    /// The same class <c>RelayedText.Printable</c> drops at the other end of this pipeline, stated
    /// here because the domain cannot reference Hall9k.Connectors (AGENTS.md, reference graph).
    /// </summary>
    internal static bool Illegible(char character)
    {
        if (char.IsControl(character) && character is not '\n' and not '\r' and not '\t')
        {
            return true;
        }

        // Written as code points rather than as character literals for the reason the whole type
        // exists: every one of these is invisible in a source file, so a literal here would be a
        // line no reviewer could read and no later editor could retype correctly.
        int codePoint = character;
        return codePoint is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF
            || codePoint is >= 0x202A and <= 0x202E
            || codePoint is >= 0x2066 and <= 0x2069;
    }

    /// <summary>
    /// Whether this project's conventions actually <em>ban</em> the thing a mechanical check would
    /// enforce. A keyword probe over the operator's own sentence, which is an approximation and
    /// named as one: a project that rewrites its conventions and drops the em-dash sentence turns
    /// that check off, and one that words the rule some way this probe does not recognize turns it
    /// off too. The alternative, enforcing the platform's default rules over a text that never
    /// claimed them, would rewrite prose an operator deliberately allowed, which is worse than
    /// missing a rule they can restate in words the probe reads.
    /// <para>
    /// Polarity is read, not assumed, which is why a cue alone is not enough: "Em dashes are fine
    /// here; use them freely" mentions em dashes and permits them, and a probe that only looked for
    /// the words would turn the rewrite on over prose the operator deliberately allowed — the exact
    /// outcome the paragraph above calls worse than missing a rule. So a rule counts as stated only
    /// where a cue and a word of prohibition sit in the same sentence
    /// (<see cref="Prohibitions"/>), and a sentence is as far as the probe reads: a prohibition two
    /// sentences upstream of its cue leaves the check off, which is the same safe direction every
    /// other miss here errs in.
    /// </para>
    /// </summary>
    internal bool States(IReadOnlyList<string> cues) =>
        Value.Split(SentenceEnds, StringSplitOptions.RemoveEmptyEntries)
            .Any(sentence =>
                cues.Any(cue => sentence.Contains(cue, StringComparison.OrdinalIgnoreCase))
                && Prohibits(sentence));

    /// <summary>
    /// Where one rule of a house style stops and the next begins, for the probe's purposes: a
    /// sentence terminator or a line break. Not the semicolon or the colon, because the default
    /// text's own em-dash rule ("No em dashes (U+2014); use commas ... instead") states its
    /// prohibition on one side of a semicolon and would fail its own probe if this list carried one.
    /// </summary>
    private static readonly char[] SentenceEnds = ['.', '!', '?', '\n', '\r'];

    /// <summary>
    /// The words a person uses when they are banning something rather than mentioning it. Judgment
    /// list rather than an observed one, and the same kind of approximation as
    /// <c>WritingConventionsCheck.ClauseOpeners</c>: a house style worded past this list reads as
    /// permissive and leaves the check off, which is the direction <see cref="States"/> errs in
    /// deliberately.
    /// </summary>
    private static readonly HashSet<string> Prohibitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "not", "never", "none", "nothing", "avoid", "avoids", "ban", "bans", "banned",
        "forbid", "forbids", "forbidden", "prohibit", "prohibits", "prohibited", "without",
        "omit", "omits", "exclude", "excludes", "refrain", "drop", "strip",
        "don't", "dont", "doesn't", "won't", "can't", "cannot", "shouldn't", "isn't", "aren't",
    };

    /// <summary>The curly apostrophe, by code point: a house style pasted out of a document carries it.</summary>
    private const char CurlyApostrophe = (char)0x2019;

    /// <summary>
    /// Whether one sentence of the conventions is banning rather than describing. Word by word, so
    /// that "notation" is not read as the "not" inside it and "don't" survives its own apostrophe,
    /// whichever of the two apostrophes it was typed with.
    /// </summary>
    private static bool Prohibits(string sentence)
    {
        StringBuilder word = new();
        foreach (char character in sentence)
        {
            if (char.IsLetter(character) || character is '\'' or CurlyApostrophe)
            {
                word.Append(character == CurlyApostrophe ? '\'' : character);
                continue;
            }

            if (word.Length > 0 && Prohibitions.Contains(word.ToString()))
            {
                return true;
            }

            word.Clear();
        }

        return word.Length > 0 && Prohibitions.Contains(word.ToString());
    }

    public bool Equals(WritingConventions? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    /// <summary>
    /// The conventions as prompt lines, indented under whatever bullet the caller is writing.
    /// Verbatim: the operator's own sentences, broken only where they already broke lines
    /// themselves, because a prompt that paraphrases the house style is a prompt that states a
    /// different one.
    /// </summary>
    public string ToPromptLines(string indent)
    {
        StringBuilder lines = new();
        foreach (string line in Value.Replace("\r\n", "\n").Split('\n'))
        {
            lines.AppendLine($"{indent}{line}".TrimEnd());
        }

        return lines.ToString();
    }

    private sealed class WritingConventionsJsonConverter : JsonConverter<WritingConventions>
    {
        // Reading is deliberately not Parse, the BacklogPolicy convention: a value already on an
        // event stream is a record of what was set, and a rule tightened later must not make an
        // old document unreadable.
        public override WritingConventions Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, WritingConventions value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
