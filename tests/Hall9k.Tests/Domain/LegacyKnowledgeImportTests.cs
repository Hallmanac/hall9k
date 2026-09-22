using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The one-time import of PLAN.md §16 and AGENTS.md's standing rules (idea d805fd8b, piece 3),
/// proved against the real sections rather than against a miniature of them: the fixture is the
/// frozen snapshot the import itself reads, embedded in Hall9k.Domain, so these tests fail the day
/// the parser stops handling the document it exists for. Nothing here touches a database, a
/// repository, or a remote. One test is the exception and says so in its own summary: the
/// placeholder-token handling has no instance left in the snapshot to drive it, so it writes
/// its own two-entry section.
/// </summary>
public sealed class LegacyKnowledgeImportTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 21, 15, 42, 7, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    private static IReadOnlyList<LegacyDecisionEntry> DecisionsLog() =>
        LegacyDecisionsLogParser.Parse(LegacyKnowledgeSource.DecisionsLog());

    private static IReadOnlyList<LegacyDecisionEntry> StandingRules() =>
        LegacyStandingRulesParser.Parse(LegacyKnowledgeSource.StandingRules());

    /// <summary>
    /// The count is asserted exactly because both failure directions are silent otherwise: an
    /// entry the parser misses is a decision that never reaches the store, and a continuation
    /// paragraph it mistakes for an entry head is a decision torn in half.
    /// </summary>
    [Fact]
    public void Every_numbered_entry_in_the_real_decisions_log_is_read_exactly_once_from_one_to_two_hundred_and_sixty_seven()
    {
        IReadOnlyList<LegacyDecisionEntry> entries = DecisionsLog();

        IEnumerable<string> expected = Enumerable.Range(1, 267).Select(number => $"Decisions Log #{number}");
        entries.Should().HaveCount(267, "the log carries 267 numbered entries and nothing else");
        entries.Select(entry => entry.LegacyId).Should().Contain(expected);
        entries.Select(entry => entry.LegacyId).Should().OnlyHaveUniqueItems();
        entries.Should().OnlyContain(entry => entry.Statement.Length > 0);
    }

    /// <summary>
    /// The log's entries are not in numeric order in the file (#36 sits after #58, and a dozen
    /// later entries were filed beside the ones they were written next to rather than in
    /// sequence). The parser reads file order and does not sort, which is what keeps an entry's
    /// own trailing notes and sub-paragraphs with the entry they belong to.
    /// </summary>
    [Fact]
    public void The_entries_come_back_in_the_order_the_file_carries_them_rather_than_in_numeric_order()
    {
        List<string> citations = [.. DecisionsLog().Select(entry => entry.LegacyId)];

        citations[0].Should().Be("Decisions Log #1");
        citations.IndexOf("Decisions Log #36").Should().BeGreaterThan(citations.IndexOf("Decisions Log #58"));
    }

    [Fact]
    public void An_entry_keeps_its_whole_text_and_loses_only_its_numbering_token()
    {
        LegacyDecisionEntry first = DecisionsLog().Single(entry => entry.LegacyId == "Decisions Log #1");

        first.Statement.Should().StartWith("**Billing & spawn flags.** Subscription is the default billing mode");
        first.Statement.Should().Contain("`--settings '{\"includeCoAuthoredBy\": false}'`");
        first.Statement.Should().NotStartWith("1. ");
    }

    /// <summary>
    /// The column-zero rule doing its job: entry #58 carries a nested ordered list of four
    /// questions, each written as "1. **…**" with three spaces in front of it. Read as entry
    /// heads, they would tear one decision into five and mint four decisions numbered 1 to 4 that
    /// nobody ever made.
    /// </summary>
    [Fact]
    public void A_nested_ordered_list_inside_an_entry_stays_inside_that_entry()
    {
        LegacyDecisionEntry entry = DecisionsLog().Single(item => item.LegacyId == "Decisions Log #58");

        entry.Statement.Should().Contain("1. **Is a connection string configured at all?**");
        entry.Statement.Should().Contain("4. **Only if (1) found nothing");
    }

    /// <summary>
    /// The renumbering notes are the machinery describing itself, and a rulebook agents read
    /// should not be a third of the way made of them.
    /// </summary>
    [Fact]
    public void Placement_notes_about_numbering_are_left_behind()
    {
        IReadOnlyList<LegacyDecisionEntry> entries = DecisionsLog();

        entries.Should().OnlyContain(entry => !entry.Statement.Contains("Renumbering placement note:"));
        entries.Should().OnlyContain(entry => !entry.Statement.Contains("P2P placement note:"));
        LegacyKnowledgeSource.DecisionsLog().Should().Contain("> Renumbering placement note:",
            "the snapshot itself still carries them; it is the parse that leaves them behind");
    }

    /// <summary>
    /// The preamble is not a decision: it is the section's own title paragraph and the placeholder
    /// convention this change retires. Both sit above the first entry and neither is imported.
    /// </summary>
    [Fact]
    public void The_sections_own_preamble_is_not_read_as_a_decision()
    {
        IReadOnlyList<LegacyDecisionEntry> entries = DecisionsLog();

        entries.Should().OnlyContain(entry => !entry.Statement.StartsWith("**Placeholder numbering.**"));
        entries.Should().OnlyContain(entry => !entry.Statement.Contains("Decisions made during the v0 kickoff session"));
    }

    /// <summary>
    /// An entry whose branch had not reached its mechanical renumbering step when a snapshot is
    /// taken is a real decision under a token instead of a number. Dropping it for being unnumbered
    /// would lose it outright, so it comes across under the token the rest of the repository cites
    /// it by at that moment. The frozen snapshot has no such entry to prove this against: the one
    /// that stood under `PLACEHOLDER-adbc4a1e` was assigned #265 on main before this branch merged.
    /// So this is the one test here that writes its own section, because the shape it guards is a
    /// snapshot taken mid-flight rather than this particular snapshot.
    /// </summary>
    [Fact]
    public void An_entry_still_under_a_placeholder_token_is_imported_under_that_token()
    {
        IReadOnlyList<LegacyDecisionEntry> entries = LegacyDecisionsLogParser.Parse(string.Join(
            '\n',
            "1. **A numbered entry.**",
            "",
            "PLACEHOLDER-6df5f975. **An entry whose branch is still in flight.**"));

        entries.Select(entry => entry.LegacyId).Should()
            .Equal("Decisions Log #1", "Decisions Log PLACEHOLDER-6df5f975");
        entries[1].Statement.Should().Be("**An entry whose branch is still in flight.**");
    }

    /// <summary>
    /// The completeness property behind every assertion above: apart from each entry's own
    /// numbering token and the placement-note blockquotes, every line the section carries from
    /// its first entry onward survives into some statement. A parser that silently swallowed a
    /// sub-paragraph, a table row or a fenced block would pass a count check and fail this one.
    /// Every blockquote line in the snapshot belongs to a placement note (169 of them, all
    /// opening with that label), which is why they are the one exclusion here.
    /// </summary>
    [Fact]
    public void Nothing_but_the_numbering_and_the_placement_notes_is_lost_in_the_parse()
    {
        string[] lines = LegacyKnowledgeSource.DecisionsLog().Split('\n');
        int firstEntry = Array.FindIndex(lines, line => EntryHead.IsMatch(line));
        firstEntry.Should().BeGreaterThan(0, "the snapshot should carry entries to parse");

        string statements = string.Join('\n', DecisionsLog().Select(entry => entry.Statement));

        List<string> missing = [];
        foreach (string line in lines[firstEntry..])
        {
            string text = EntryHead.Replace(line, "**");
            if (text.Length == 0 || text.StartsWith('>') || statements.Contains(text, StringComparison.Ordinal))
            {
                continue;
            }

            missing.Add(text);
        }

        missing.Should().BeEmpty("every line of the section should reach a statement intact");
    }

    [Fact]
    public void Every_standing_rule_in_both_AGENTS_sections_is_read_with_its_section_in_its_citation()
    {
        IReadOnlyList<LegacyDecisionEntry> rules = StandingRules();

        rules.Should().HaveCount(16);
        rules.Select(rule => rule.LegacyId).Should().OnlyHaveUniqueItems();
        rules.Take(10).Should().OnlyContain(rule => rule.LegacyId.StartsWith("AGENTS.md Git rules #"));
        rules.Skip(10).Should().OnlyContain(rule => rule.LegacyId.StartsWith("AGENTS.md Working agreements #"));
        rules[0].Statement.Should().StartWith("**Commits are authored as the repo owner.");
    }

    /// <summary>
    /// AGENTS.md hard-wraps a rule across several lines and indents the wrapped ones. The words
    /// have to survive the unwrapping, and the leading indent must not, or every rule renders as a
    /// code block in the document that replaces the file.
    /// </summary>
    [Fact]
    public void A_hard_wrapped_rule_comes_back_as_its_own_whole_paragraph_with_the_indent_removed()
    {
        LegacyDecisionEntry rule = StandingRules()
            .Single(item => item.Statement.StartsWith("**An agent never pushes;"));

        rule.Statement.Should().Contain("git push --force-with-lease");
        rule.Statement.Should().Contain("Decisions Log #26, #103, #104");
        rule.Statement.Split('\n').Should().OnlyContain(line => !line.StartsWith(' '));
    }

    /// <summary>
    /// The one rule deliberately left out of the snapshot: this change is what retires the
    /// placeholder convention, so importing it would record a rule that stopped holding the moment
    /// it landed. Dropping this one outright costs nothing, because nothing in this repository
    /// cites an AGENTS.md rule by position. The §16 decision it restated, #162, is cited thirteen
    /// times and gets the other treatment instead — see
    /// <see cref="The_placeholder_numbering_decision_is_imported_and_retired_in_the_same_act"/>.
    /// </summary>
    [Fact]
    public void The_placeholder_convention_rule_is_not_carried_into_the_store()
    {
        StandingRules().Should().OnlyContain(rule => !rule.Statement.Contains("PLACEHOLDER-<shortid>"));
    }

    [Fact]
    public void An_import_on_a_node_that_has_not_switched_replication_on_is_refused_and_says_which_milestone()
    {
        Action importing = () => LegacyKnowledgeImportDecider.RequireReplicationSwitchedOn(null);

        importing.Should().Throw<DomainValidationException>()
            .WithMessage("*M2a*")
            .WithMessage("*never rides its outbox*");
    }

    [Fact]
    public void An_import_on_a_node_that_has_switched_replication_on_is_allowed()
    {
        Action importing = () => LegacyKnowledgeImportDecider.RequireReplicationSwitchedOn(0);

        importing.Should().NotThrow("a switch-on point of zero is a real switch-on point on a store with no events yet");
    }

    [Fact]
    public void Planning_records_one_decision_per_entry_scoped_to_the_project_with_no_invented_origin()
    {
        LegacyImportPlan plan = Plan([Entry("Decisions Log #1"), Entry("Decisions Log #2")], []);

        plan.ToRecord.Should().HaveCount(2);
        plan.AlreadyImported.Should().Be(0);
        plan.ToRecord.Should().OnlyContain(recorded => recorded.Scope == KnowledgeScope.Project);
        plan.ToRecord.Should().OnlyContain(recorded => recorded.ScopeId == Project);
        plan.ToRecord.Should().OnlyContain(recorded => recorded.OriginIncident == null);
        plan.ToRecord.Should().OnlyContain(recorded => recorded.Supersedes.Count == 0);
        plan.ToRecord.Should().OnlyContain(recorded => recorded.RecordedAt == Noon);
        plan.ToRecord.Select(recorded => recorded.LegacyId).Should()
            .Equal("Decisions Log #1", "Decisions Log #2");
        plan.Retirements.Should().BeEmpty("an imported rule binds unless this change is what retired it");
    }

    /// <summary>
    /// The one entry recorded and ended in a single act (independent pre-PR review, cycle 1,
    /// adversarial lens). #162 states the convention this change retires — a branch appending its
    /// own entry to PLAN.md §16 under a placeholder — and a session following it out of
    /// <c>decisions.md</c> would fail the mandatory gate's own numbering guard doing so. It cannot
    /// simply be left out of the import the way AGENTS.md's restatement of it was, because thirteen
    /// places in this repository cite <c>Decisions Log #162</c> and a citation whose entry was never
    /// imported resolves to nothing. So it is imported keeping that citation and superseded behind
    /// it on the same stream.
    /// <para>
    /// The single retirement is asserted against the real sources, which is also what proves
    /// nothing else is retired on the way in: a rule that merely reads as dated, or one whose
    /// machinery this change happens to leave standing, is imported binding and left for a human
    /// to supersede deliberately. The import is a migration, not a place to re-decide 280 rules.
    /// </para>
    /// </summary>
    [Fact]
    public void The_placeholder_numbering_decision_is_imported_and_retired_in_the_same_act()
    {
        LegacyImportPlan plan = Plan([.. DecisionsLog(), .. StandingRules()], []);

        DecisionRecorded imported = plan.ToRecord.Single(recorded => recorded.LegacyId == "Decisions Log #162");
        imported.Statement.Should().StartWith("**A Decisions Log entry gets its number at merge time");

        DecisionSuperseded retirement = plan.Retirements.Should().ContainSingle().Subject;
        retirement.Id.Should().Be(imported.Id, "the retirement belongs on the stream of the decision it ends");
        retirement.SupersededByDecisionId.Should().BeNull(
            "this rule was overruled by a change rather than replaced by another ruling, and naming the "
            + "nearest plausible successor would be a guess");
        retirement.SupersededByOwnerId.Should().Be(Owner);
        retirement.SupersededAt.Should().Be(Noon, "one import is one act, retirements included");
        retirement.Reason.Should().Contain("no tail for a branch to append its own entry to");
        retirement.Reason.Should().Contain("h9k decide");
    }

    /// <summary>
    /// The retirement is part of the entry's own import rather than a sweep over the store, so a
    /// second run that records nothing retires nothing either. Re-appending it would be a second
    /// supersession on a stream already ended, which <c>DecisionDecider.Supersede</c> refuses.
    /// </summary>
    [Fact]
    public void A_second_import_does_not_retire_an_entry_the_first_one_already_recorded()
    {
        LegacyImportPlan plan = Plan(
            [Entry("Decisions Log #162"), Entry("Decisions Log #163")], ["Decisions Log #162"]);

        plan.ToRecord.Should().ContainSingle().Which.LegacyId.Should().Be("Decisions Log #163");
        plan.Retirements.Should().BeEmpty();
    }

    /// <summary>
    /// Idempotency, which is what makes a half-finished import safe to finish and a finished one
    /// safe to repeat. The key is the legacy id rather than the statement, because a statement is
    /// long enough that a trailing space would silently mint a second copy of the same rule.
    /// </summary>
    [Fact]
    public void A_second_import_records_only_what_the_first_one_missed()
    {
        LegacyImportPlan plan = Plan(
            [Entry("Decisions Log #1"), Entry("Decisions Log #2"), Entry("Decisions Log #3")],
            ["Decisions Log #1", "Decisions Log #3"]);

        plan.ToRecord.Should().ContainSingle().Which.LegacyId.Should().Be("Decisions Log #2");
        plan.AlreadyImported.Should().Be(2);
    }

    [Fact]
    public void A_completed_import_run_a_second_time_records_nothing()
    {
        LegacyImportPlan plan = Plan(
            [Entry("Decisions Log #1")], ["Decisions Log #1"]);

        plan.ToRecord.Should().BeEmpty();
        plan.AlreadyImported.Should().Be(1);
    }

    /// <summary>
    /// A source that names one citation twice would import one rule under two ids on the first run
    /// and then re-import neither reliably on the second, so it is refused before anything is
    /// appended rather than half-recorded.
    /// </summary>
    [Fact]
    public void A_source_naming_the_same_citation_twice_is_refused_before_anything_is_recorded()
    {
        Action planning = () => Plan([Entry("Decisions Log #7"), Entry("Decisions Log #7")], []);

        planning.Should().Throw<DomainValidationException>().WithMessage("*Decisions Log #7*");
    }

    /// <summary>
    /// The real sources planned together, which is the shape the command actually runs: nothing
    /// collides between the two files, and every entry in both becomes exactly one decision.
    /// </summary>
    [Fact]
    public void Both_real_sources_import_together_without_a_citation_collision()
    {
        List<LegacyDecisionEntry> everything = [.. DecisionsLog(), .. StandingRules()];

        LegacyImportPlan plan = Plan(everything, []);

        plan.ToRecord.Should().HaveCount(everything.Count);
        plan.ToRecord.Select(recorded => recorded.Id).Should().OnlyHaveUniqueItems();
    }

    private static LegacyImportPlan Plan(
        IReadOnlyList<LegacyDecisionEntry> entries, IReadOnlyCollection<string> alreadyImported) =>
        LegacyKnowledgeImportDecider.Plan(
            entries, alreadyImported, KnowledgeScope.Project, Project,
            RecordedProvenance.FromShell(Owner), Noon);

    private static LegacyDecisionEntry Entry(string legacyId) => new(legacyId, $"Whatever {legacyId} said.");

    /// <summary>The parser's own entry-head shape, restated here so the completeness check above tests the parse rather than sharing its implementation.</summary>
    private static readonly Regex EntryHead = new(@"^(\d+|PLACEHOLDER-[0-9a-f]{8})\. \*\*", RegexOptions.Compiled);
}
