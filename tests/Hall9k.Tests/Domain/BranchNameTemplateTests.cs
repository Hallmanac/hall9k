using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class BranchNameTemplateTests
{
    private static readonly Guid TaskId = Guid.Parse("019890ab-cdef-7000-8000-1234deadbeef");

    /// <summary>
    /// The whole no-change-for-anybody promise: a project that never sets a template gets the
    /// name <c>GitWorktreeManager</c> hard-coded before this setting existed, character for
    /// character, slug cap included.
    /// </summary>
    [Fact]
    public void Default_renders_exactly_the_name_the_platform_cut_before_templates_existed()
    {
        string branch = BranchNameTemplate.Default.Render(
            TaskId, "Add rate limiting to the auth endpoints", externalKey: null);

        branch.Should().Be($"task/{DomainId.Short(TaskId)}-add-rate-limiting-to-the-auth");
        BranchNameTemplate.Default.Value.Should().Be("task/{shortid}-{slug}");
    }

    [Fact]
    public void Blank_and_absent_templates_read_as_the_platform_default()
    {
        BranchNameTemplate.Parse(null).Should().Be(BranchNameTemplate.Default);
        BranchNameTemplate.Parse("   ").Should().Be(BranchNameTemplate.Default);
        BranchNameTemplate.FromInput(null).Should().Be(BranchNameTemplate.Default);
    }

    /// <summary>The arx-platform convention the field report asked for (Windows field report item 9).</summary>
    [Fact]
    public void A_tracker_keyed_template_renders_the_key_then_the_slug()
    {
        BranchNameTemplate template = BranchNameTemplate.Parse("{key}-{slug}");

        template.Render(TaskId, "Add rate limiting to the auth endpoints", "ARX-14")
            .Should().Be("ARX-14-add-rate-limiting-to-the-auth");
    }

    [Fact]
    public void Every_token_is_available_and_case_is_ignored()
    {
        BranchNameTemplate template = BranchNameTemplate.Parse("feature/{KEY}/{ShortId}-{SLUG}");

        template.Render(TaskId, "Ship it", "PROJ-7")
            .Should().Be($"feature/PROJ-7/{DomainId.Short(TaskId)}-ship-it");
    }

    /// <summary>
    /// A GitHub reference is canonically <c>owner/repo#42</c>, which is true but is neither what
    /// anybody calls the issue nor something a branch name can carry without inventing directories.
    /// </summary>
    [Theory]
    [InlineData("jira:ARX-14", "ARX-14")]
    [InlineData("github:Hallmanac/hall9k#42", "42")]
    [InlineData("github-pr:Hallmanac/hall9k#7", "7")]
    public void The_key_token_renders_the_item_as_people_say_it(string reference, string expected)
    {
        ExternalReference parsed = ExternalReference.Parse(reference);

        BranchNameTemplate.Parse("{key}-{slug}").Render(TaskId, "Ship it", parsed.Key)
            .Should().Be($"{expected}-ship-it");
    }

    /// <summary>
    /// An empty segment would read as though a key had been part of the name, and a guessed one
    /// would put a card number on a branch nobody filed a card for (AGENTS.md, "never guess at
    /// unobserved facts"). The rendered name is still a ref git will take.
    /// </summary>
    [Fact]
    public void A_task_with_no_external_reference_says_so_rather_than_emitting_an_empty_segment()
    {
        string branch = BranchNameTemplate.Parse("{key}-{slug}").Render(TaskId, "Ship it", externalKey: null);

        branch.Should().Be("no-key-ship-it");
        branch.Should().NotStartWith("-", "an elided token would leave a leading separator");
        LegalGitRef(branch).Should().BeTrue();
    }

    [Fact]
    public void An_objective_with_nothing_sluggable_still_renders_a_legal_ref()
    {
        string branch = BranchNameTemplate.Parse("{key}/{slug}").Render(TaskId, "??? !!!", "ARX-14");

        branch.Should().Be("ARX-14/task");
        LegalGitRef(branch).Should().BeTrue();
    }

    /// <summary>
    /// Token values are what a template author cannot see coming, so they are sanitized to
    /// characters git allows anywhere, starting and ending with an alphanumeric. That property is
    /// what lets <see cref="BranchNameTemplate.Parse"/> prove a template legal from one sample
    /// render rather than from every task that will ever use it.
    /// </summary>
    [Theory]
    [InlineData("PROJ 123")]
    [InlineData("~PROJ:123^")]
    [InlineData("feature/PROJ-123")]
    [InlineData("PROJ..123")]
    [InlineData("PROJ-123.lock")]
    [InlineData("-PROJ-123-")]
    public void A_hostile_external_key_cannot_render_an_illegal_ref(string key)
    {
        string branch = BranchNameTemplate.Parse("{key}-{slug}").Render(TaskId, "Ship it", key);

        LegalGitRef(branch).Should().BeTrue($"'{key}' must not be able to cut an illegal branch");
        branch.Should().EndWith("-ship-it");
    }

    /// <summary>
    /// The refusal happens at h9k project set, where a human can fix it, rather than at the
    /// dispatch it would otherwise fail — and the message quotes the rule and the token list, per
    /// the CLI command standards.
    /// </summary>
    [Theory]
    [InlineData("task/{branch}-{slug}", "is not a token")]
    [InlineData("task/{slug", "never closed")]
    [InlineData("task/slug}", "never opened")]
    [InlineData("{slug}/", "empty path components")]
    [InlineData("/{slug}", "empty path components")]
    [InlineData("task//{slug}", "empty path components")]
    [InlineData("task/.{slug}", "begins with '.'")]
    [InlineData("task/{slug}.lock", "ends with '.lock'")]
    [InlineData("task/prefix.{slug}", "ends with '.lock'")]
    [InlineData("task/prefix.l{slug}", "ends with '.lock'")]
    [InlineData("task/prefix.lo{slug}", "ends with '.lock'")]
    [InlineData("task/prefix.loc{slug}", "ends with '.lock'")]
    [InlineData("task/prefix.{key}", "ends with '.lock'")]
    [InlineData("task/x.{slug}ck", "ends with '.lock'")]
    [InlineData("task/x.{slug}{key}", "ends with '.lock'")]
    [InlineData("task/{slug}..{shortid}", "'..'")]
    [InlineData("task/{slug}@{x", "never closed")]
    [InlineData("task/{slug} {shortid}", "git does not allow")]
    [InlineData("task/{slug}?", "git does not allow")]
    [InlineData("task/{slug}\"", "passes the rendered name through")]
    [InlineData("-{slug}", "cannot begin with '-'")]
    [InlineData("{slug}.", "cannot end with '.'")]
    public void An_illegal_template_is_refused_at_set_time_with_the_rule_quoted(string template, string rule)
    {
        Action act = () => BranchNameTemplate.Parse(template);

        act.Should().Throw<DomainValidationException>()
            .WithMessage($"*{rule}*")
            .And.Message.Should().Contain("{shortid}").And.Contain("{slug}").And.Contain("{key}");
    }

    /// <summary>
    /// Origin incident (2026-09-01, this branch's own self-review, running the procedure the docs
    /// describe): the refusal for <c>feature/{slug}:{shortid}</c> quoted the template back with the
    /// <c>:</c> rewritten to <c>?</c> by the terminal-safety relay, then named <c>'?'</c> as the
    /// illegal character — telling the operator their template contained a character it did not.
    /// </summary>
    [Fact]
    public void A_refusal_names_the_character_the_operator_actually_typed()
    {
        Action act = () => BranchNameTemplate.Parse("feature/{slug}:{shortid}");

        act.Should().Throw<DomainValidationException>()
            .And.Message.Should()
                .Contain("':' is a character git does not allow")
                .And.Contain("feature/{slug}:{shortid}", "the template is quoted back as it was typed");
    }

    /// <summary>
    /// The relay still does its job: a character a terminal can be attacked with never reaches the
    /// message, and an unprintable one is named by code point rather than as a look-alike '?'. The
    /// bidirectional override is the one rule stricter than git's own — git would take it, but a
    /// branch name is printed wherever this platform reports work.
    /// </summary>
    [Theory]
    [InlineData('', "U+0007")]
    [InlineData('‮', "U+202E")]
    public void A_refusal_names_an_unprintable_character_by_code_point(char character, string expected)
    {
        Action act = () => BranchNameTemplate.Parse($"task/{{slug}}{character}x");

        act.Should().Throw<DomainValidationException>()
            .And.Message.Should().Contain(expected).And.NotContain(character.ToString());
    }

    /// <summary>
    /// A TAG character such as U+E0041 (category Cf, same as the bidirectional overrides above) is
    /// outside the BMP, so it is a UTF-16 surrogate pair rather than one char — a per-char
    /// <c>char.GetUnicodeCategory</c> scan reads each half as Surrogate, not Format, and would miss
    /// it entirely (independent pre-PR review, cycle 3, adversarial).
    /// </summary>
    [Fact]
    public void A_refusal_names_a_non_bmp_formatting_character_by_code_point()
    {
        Action act = () => BranchNameTemplate.Parse("task/{slug}\U000E0041x");

        act.Should().Throw<DomainValidationException>()
            .And.Message.Should().Contain("U+E0041").And.NotContain("\U000E0041");
    }

    [Fact]
    public void A_template_that_renders_past_the_length_ceiling_is_refused()
    {
        Action act = () => BranchNameTemplate.Parse(new string('x', 250));

        act.Should().Throw<DomainValidationException>().WithMessage("*ceiling*");
    }

    /// <summary>
    /// A value that reached the stream some other way (an older build, a hand-edited document)
    /// still cannot cut an illegal ref: the render is the second gate, and it fails the run
    /// honestly rather than handing git a name it will reject.
    /// </summary>
    [Fact]
    public void A_template_off_the_stream_is_vetted_again_when_it_renders()
    {
        BranchNameTemplate lenient = BranchNameTemplate.FromInput("task/{slug}:{shortid}");

        Action act = () => lenient.Render(TaskId, "Ship it", externalKey: null);

        act.Should().Throw<DomainValidationException>().WithMessage("*git does not allow*");
    }

    /// <summary>
    /// The run-suffixed collision name <c>ResolveBranchNameAsync</c> appends is legal against any
    /// template Parse accepted, because a rendered name always ends in a character git allows and
    /// the suffix adds only <c>-r</c> and four hex digits.
    /// </summary>
    [Theory]
    [InlineData("task/{shortid}-{slug}")]
    [InlineData("{key}-{slug}")]
    [InlineData("feature/{key}/{shortid}")]
    public void The_collision_retry_suffix_stays_legal_against_any_accepted_template(string template)
    {
        string branch = BranchNameTemplate.Parse(template).Render(TaskId, "Ship it", "ARX-14");

        LegalGitRef($"{branch}-r{DomainId.Short(DomainId.New())[..4]}").Should().BeTrue();
    }

    /// <summary>
    /// The length ceiling reserves the collision suffix, so the longest template Parse accepts
    /// still has room for the retry name — refusing at the retry would be the worst possible
    /// moment, since the run has already done its work by then.
    /// </summary>
    [Fact]
    public void The_longest_accepted_template_still_has_room_for_its_retry_name()
    {
        string longest = new('x', 194);

        string branch = BranchNameTemplate.Parse(longest).Render(TaskId, "Ship it", "ARX-14");

        branch.Should().HaveLength(194);
        $"{branch}-rbeef".Should().HaveLength(200);
        new Action(() => BranchNameTemplate.Parse(new string('x', 195)))
            .Should().Throw<DomainValidationException>().WithMessage("*194-character ceiling*");
    }

    /// <summary>
    /// A whitespace-padded token still renders and resolves exactly like the unpadded form
    /// (<see cref="Render"/> trims inside the braces), so a caller checking whether a template
    /// leans on a given token — the {key}-versus-backlog-policy advisory in
    /// <c>ProjectSetCommand</c> — has to tokenize the same way rather than substring-match the
    /// raw text, or a padded token slips past undetected.
    /// </summary>
    [Theory]
    [InlineData("{key}-{slug}", true)]
    [InlineData("{ key }-{slug}", true)]
    [InlineData("{KEY}-{slug}", true)]
    [InlineData("{shortid}-{slug}", false)]
    public void UsesToken_finds_a_token_regardless_of_case_or_internal_whitespace(string template, bool expected)
    {
        BranchNameTemplate.Parse(template).UsesToken(BranchNameTemplate.KeyToken).Should().Be(expected);
    }

    /// <summary>
    /// The oracle for every legality assertion above: the rules <c>git check-ref-format --branch</c>
    /// enforces, restated independently of the implementation under test so a bug in one is not
    /// silently agreed with by the other.
    /// </summary>
    private static bool LegalGitRef(string branch) =>
        branch.Length > 0
        && branch.All(character =>
            !char.IsControl(character) && character is not (' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        && !branch.Contains("..", StringComparison.Ordinal)
        && !branch.Contains("@{", StringComparison.Ordinal)
        && branch is not "@"
        && branch[0] is not '-'
        && branch[^1] is not '.'
        && branch.Split('/').All(component =>
            component.Length > 0
            && component[0] is not '.'
            && !component.EndsWith(".lock", StringComparison.Ordinal));
}
