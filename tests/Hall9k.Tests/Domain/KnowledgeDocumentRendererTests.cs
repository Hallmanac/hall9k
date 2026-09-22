using FluentAssertions;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <c>decisions.md</c> and <c>lessons.md</c> as rendered from a fixture set of projection rows
/// (idea d805fd8b, piece 2): what the documents carry, what they leave out, and the determinism
/// the whole feature rests on — two nodes holding the same history have to render the same bytes,
/// or the file is a source of diffs rather than a shared record.
/// </summary>
public sealed class KnowledgeDocumentRendererTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 16, 15, 42, 7, TimeSpan.Zero);

    [Fact]
    public void A_binding_decision_renders_with_its_id_as_the_heading_and_its_provenance_beneath()
    {
        DecisionDetails decision = SomeDecision("Agents never push; the daemon pushes every branch.");
        decision.OriginIncident = "a plain push rejected two rebased follow-up branches, 2026-08-17";

        string rendered = DecisionsDocumentRenderer.Render([decision]);

        rendered.Should().Contain($"## {DomainId.Short(decision.Id)}");
        rendered.Should().Contain("Agents never push; the daemon pushes every branch.");
        rendered.Should().Contain("- Recorded 2026-09-16 15:42:07Z, at a shell, outside any run.");
        rendered.Should().Contain("- Origin incident: a plain push rejected two rebased follow-up branches, 2026-08-17");
    }

    [Fact]
    public void The_decisions_header_says_it_is_generated_and_names_the_command_that_records_one()
    {
        string rendered = DecisionsDocumentRenderer.Render([SomeDecision("Something already decided.")]);

        rendered.Should().StartWith("# Decisions\n");
        rendered.Should().Contain(DecisionsDocumentRenderer.GeneratedMarker);
        rendered.Should().Contain("h9k decide \"<one claim>\"");
    }

    [Fact]
    public void The_lessons_header_says_it_is_generated_and_names_the_command_that_records_one()
    {
        string rendered = LessonsDocumentRenderer.Render([SomeLesson("Something already learned.")]);

        rendered.Should().StartWith("# Lessons\n");
        rendered.Should().Contain(LessonsDocumentRenderer.GeneratedMarker);
        rendered.Should().Contain("h9k learn \"<what you learned>\"");
    }

    /// <summary>
    /// The empty document is the one every project has on day one, so it has to teach rather than
    /// sit there blank: nothing is recorded, and here is the command that records the first one.
    /// </summary>
    [Fact]
    public void An_empty_store_still_renders_both_files_naming_the_command_that_records_the_first_entry()
    {
        DecisionsDocumentRenderer.Render([]).Should()
            .Contain("Nothing is recorded here yet.").And.Contain("h9k decide ");
        LessonsDocumentRenderer.Render([]).Should()
            .Contain("Nothing is recorded here yet.").And.Contain("h9k learn ");
    }

    /// <summary>
    /// Nothing is deleted in this store, but a document agents read as the rulebook must not carry
    /// rules that stopped binding or lessons somebody retired for being wrong.
    /// </summary>
    [Fact]
    public void A_superseded_decision_and_a_retired_lesson_are_left_out()
    {
        DecisionDetails superseded = SomeDecision("What we used to do.");
        superseded.Status = DecisionStatus.Superseded;
        superseded.SupersededAt = Noon.AddDays(1);
        LearningDetails retired = SomeLesson("What turned out to be wrong.");
        retired.Status = LearningStatus.Retired;
        retired.RetiredAt = Noon.AddDays(1);

        DecisionsDocumentRenderer.Render([superseded, SomeDecision("What we do now.")])
            .Should().Contain("What we do now.").And.NotContain("What we used to do.");
        LessonsDocumentRenderer.Render([retired, SomeLesson("What still holds.")])
            .Should().Contain("What still holds.").And.NotContain("What turned out to be wrong.");
    }

    /// <summary>
    /// The origin incident sits inside a markdown list item, and nothing stops a recorded one
    /// from spanning lines: the deciders trim a statement, they do not reflow it. A newline left
    /// in would end the bullet and leave the rest of the sentence rendering as a paragraph of its
    /// own, so the document reduces it rather than trusting the recording to be one line.
    /// </summary>
    [Fact]
    public void A_multi_line_origin_incident_still_renders_as_one_bullet()
    {
        DecisionDetails decision = SomeDecision("The rule that came out of it.");
        decision.OriginIncident = "2026-08-17, PR #6:\na plain push stranded two rebased branches.";

        string rendered = DecisionsDocumentRenderer.Render([decision]);

        rendered.Should().Contain(
            "- Origin incident: 2026-08-17, PR #6: a plain push stranded two rebased branches.");
    }

    [Fact]
    public void A_decision_names_what_it_supersedes_by_id()
    {
        Guid replaced = DomainId.New();
        DecisionDetails decision = SomeDecision("The ruling that holds now.");
        decision.Supersedes = [replaced];

        DecisionsDocumentRenderer.Render([decision]).Should().Contain($"- Supersedes: {DomainId.Short(replaced)}");
    }

    /// <summary>
    /// A lesson is live the moment it is recorded, with no review step, so the provenance mark is
    /// what makes it safe to read: an unattended run's lesson says so rather than arriving
    /// indistinguishable from one a human typed.
    /// </summary>
    [Fact]
    public void A_lesson_recorded_inside_an_unattended_run_says_so()
    {
        Guid runId = DomainId.New();
        LearningDetails lesson = SomeLesson("Match \\r?\\n; this worktree's files are CRLF.");
        lesson.Provenance = new RecordedProvenance(DomainId.New(), runId, DomainId.New(), HumanAttendance.Unattended);

        LessonsDocumentRenderer.Render([lesson]).Should()
            .Contain($"- Recorded 2026-09-16 15:42:07Z, from run {DomainId.Short(runId)} (unattended).");
    }

    /// <summary>
    /// Attendance that was never read is its own answer. Rendering it as silence would leave a
    /// reader to fill in "a human wrote this", which is the guess AGENTS.md's never-guess rule
    /// exists to stop.
    /// </summary>
    [Fact]
    public void A_run_whose_attendance_was_never_observed_is_rendered_as_exactly_that()
    {
        LearningDetails lesson = SomeLesson("Something learned somewhere.");
        lesson.Provenance = new RecordedProvenance(
            DomainId.New(), DomainId.New(), DomainId.New(), HumanAttendance.Unobserved);

        LessonsDocumentRenderer.Render([lesson]).Should().Contain("attendance not observed");
    }

    /// <summary>
    /// The determinism the feature rests on, exercised against everything that could break it at
    /// once: the order the rows arrive in, the host's culture, and the offset the same instant was
    /// recorded under.
    /// </summary>
    [Fact]
    public void The_same_records_render_the_same_bytes_whatever_their_order_offset_or_the_hosts_culture()
    {
        IReadOnlyList<DecisionDetails> fixture = Fixture();
        string reference = DecisionsDocumentRenderer.Render(fixture);

        DecisionsDocumentRenderer.Render([.. fixture.Reverse()]).Should().Be(reference,
            "the order is derived from the records, never taken from the order a query returned them in");

        string[] cultures = ["fi-FI", "da-DK", "en-US"];
        foreach (string culture in cultures)
        {
            CultureScope.Run(culture, () => DecisionsDocumentRenderer.Render(fixture).Should().Be(reference,
                $"a node running under {culture} holds the same history as one running under any other"));
        }

        IReadOnlyList<DecisionDetails> sameInstantsElsewhere = [.. fixture.Select(decision =>
        {
            DecisionDetails shifted = Clone(decision);
            shifted.RecordedAt = decision.RecordedAt.ToOffset(TimeSpan.FromHours(-4));
            return shifted;
        })];
        DecisionsDocumentRenderer.Render(sameInstantsElsewhere).Should().Be(reference,
            "the same instant recorded in a different time zone is the same instant");
    }

    /// <summary>
    /// The line ending is the determinism failure that would not show up in any assertion about
    /// content: <c>AppendLine</c> writes CRLF on Windows and LF everywhere else, so the two
    /// renderers write every newline themselves.
    /// </summary>
    [Fact]
    public void Nothing_rendered_carries_a_carriage_return_on_any_host()
    {
        DecisionDetails decision = SomeDecision("A statement\r\nrecorded over two lines on Windows.");
        LearningDetails lesson = SomeLesson("A lesson\r\nrecorded over two lines on Windows.");

        DecisionsDocumentRenderer.Render([decision]).Should().NotContain("\r");
        LessonsDocumentRenderer.Render([lesson]).Should().NotContain("\r");
    }

    private static IReadOnlyList<DecisionDetails> Fixture() =>
    [
        WithRecordedAt(SomeDecision("The first thing decided."), Noon),
        WithRecordedAt(SomeDecision("Decided at the same instant as the first."), Noon),
        WithRecordedAt(SomeDecision("Decided a week later."), Noon.AddDays(7)),
    ];

    private static DecisionDetails WithRecordedAt(DecisionDetails decision, DateTimeOffset recordedAt)
    {
        decision.RecordedAt = recordedAt;
        return decision;
    }

    private static DecisionDetails Clone(DecisionDetails decision) => new()
    {
        Id = decision.Id,
        Scope = decision.Scope,
        ScopeId = decision.ScopeId,
        Statement = decision.Statement,
        OriginIncident = decision.OriginIncident,
        Supersedes = [.. decision.Supersedes],
        Provenance = decision.Provenance,
        RecordedAt = decision.RecordedAt,
        Status = decision.Status,
    };

    private static DecisionDetails SomeDecision(string statement) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = DomainId.New(),
        Statement = statement,
        Provenance = RecordedProvenance.FromShell(DomainId.New()),
        RecordedAt = Noon,
        Status = DecisionStatus.Recorded,
    };

    private static LearningDetails SomeLesson(string statement) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = DomainId.New(),
        Statement = statement,
        Provenance = RecordedProvenance.FromShell(DomainId.New()),
        RecordedAt = Noon,
        Status = LearningStatus.Active,
    };
}
