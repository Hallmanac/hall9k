using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The "In prompts" line <c>h9k learn show</c> prints (idea d805fd8b, piece 5). Driven through
/// the pure composer rather than the command, because the answer is decided by two facts on the
/// row and nothing about a store or a console changes it.
/// </summary>
public sealed class LearningShowInjectionLineTests
{
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    [Fact]
    public void An_active_lesson_the_provenance_rule_lets_through_is_reported_as_carried()
    {
        LearningShowCommand.InjectionLine(Lesson(LearningStatus.Active), LessonProvenanceMark.AgentOnThisNode)
            .Should().Contain("yes, inside this node's lesson caps");
    }

    [Fact]
    public void An_active_lesson_the_provenance_rule_holds_back_names_the_review_that_will_settle_it()
    {
        LearningShowCommand.InjectionLine(Lesson(LearningStatus.Active), LessonProvenanceMark.AgentOnAnotherNode)
            .Should().Contain("no").And.Contain("7e403b80");
    }

    /// <summary>
    /// The status gate runs first, because <see cref="LessonInjection.Compose"/> filters on active
    /// before any mark is computed. Reading the mark alone reported a retired lesson as carried,
    /// telling an operator checking that a retirement had taken effect the opposite of the truth
    /// (independent pre-PR review, cycle 3, adversarial lens).
    /// </summary>
    [Fact]
    public void A_lesson_that_is_not_active_reaches_no_prompt_whatever_its_mark_says()
    {
        foreach (LearningStatus status in new[] { LearningStatus.Retired, LearningStatus.Unknown })
        {
            LearningDetails learning = Lesson(status);

            LearningShowCommand.InjectionLine(learning, LessonProvenanceMark.NoRunNamed).Should()
                .Contain("only active lessons are composed into a section");
            LearningShowCommand.InjectionLine(learning, LessonProvenanceMark.AgentOnThisNode).Should()
                .NotContain("yes");
        }
    }

    private static LearningDetails Lesson(LearningStatus status) => new()
    {
        Id = DomainId.New(),
        Scope = KnowledgeScope.Project,
        ScopeId = Project,
        Statement = "Integration tests need Docker running before dotnet test",
        Provenance = RecordedProvenance.FromShell(Owner),
        RecordedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        Status = status,
    };
}
