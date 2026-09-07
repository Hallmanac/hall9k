using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class TaskFileParserTests
{
    [Fact]
    public void Parses_frontmatter_and_body_into_content()
    {
        const string file = """
            ---
            project: hall9k
            type: feature
            objective: Add rate limiting to auth endpoints
            criteria:
            - 429 returned past the limit
            - tests cover the limiter
            ---

            Read the auth middleware first. Do not touch the session store.
            """;

        TaskFileContent content = TaskFileParser.Parse(file);

        content.Project.Should().Be("hall9k");
        content.Type.Should().Be("feature");
        content.Objective.Should().Be("Add rate limiting to auth endpoints");
        content.Criteria.Should().HaveCount(2);
        content.AgentContext.Should().Contain("auth middleware");
    }

    [Fact]
    public void File_without_frontmatter_fails_validation()
    {
        Action act = () => TaskFileParser.Parse("just some text");

        act.Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("model", "the rejection names every key the parser reads, so an author can self-correct");
    }

    [Fact]
    public void Body_is_optional()
    {
        TaskFileContent content = TaskFileParser.Parse("---\nobjective: x\ncriteria:\n- y\n---");

        content.AgentContext.Should().BeNull();
    }

    /// <summary>
    /// The task file is how the platform queues its own work, so the model override has to
    /// be statable there too (Decisions Log #33); absent means the chain decides.
    /// </summary>
    [Fact]
    public void Reads_an_optional_model_from_the_frontmatter()
    {
        const string withModel = """
            ---
            project: hall9k
            type: feature
            model: claude-opus-5
            objective: Pin the model
            criteria:
            - the run records what it ran on
            ---

            Body.
            """;

        TaskFileParser.Parse(withModel).Model.Should().Be("claude-opus-5");

        const string withoutModel = """
            ---
            project: hall9k
            objective: Let the chain decide
            criteria:
            - the run records what it ran on
            ---
            """;

        TaskFileParser.Parse(withoutModel).Model.Should().BeNull("an unstated model is not a guessed one");
    }

    /// <summary>
    /// Origin incident (2026-09-06): the Windows node adopted issues #81 and #82 from record blocks
    /// hand-written with double-quoted YAML scalars, and this parser — line-oriented, taking
    /// everything after the first colon verbatim — stored the quote characters inside the objective
    /// and inside every criterion. It reads real YAML scalars now, so this is also the repair path:
    /// one <c>h9k task revise &lt;id&gt; --file &lt;task.md&gt;</c> over a task whose values carry
    /// stray quotes re-parses them and stores them clean.
    /// </summary>
    [Fact]
    public void A_double_quoted_objective_or_criterion_is_stored_without_the_quote_characters()
    {
        const string file = """
            ---
            project: hall9k
            type: feature
            objective: "Free run slots are allocated across projects by round-robin"
            criteria:
            - "Under contention, free slots rotate: longest-unserved wins"
            - Eligibility means ready work under every applicable limit
            ---

            Body.
            """;

        TaskFileContent parsed = TaskFileParser.Parse(file);

        parsed.Objective.Should().Be("Free run slots are allocated across projects by round-robin");
        parsed.Criteria.Should().Equal(
            "Under contention, free slots rotate: longest-unserved wins",
            "Eligibility means ready work under every applicable limit");
    }

    /// <summary>
    /// The record block a published issue carries is the same shape this parser reads, agent context
    /// included — which it holds as a <c>context</c> block scalar, since a fenced YAML block has
    /// nowhere to put a markdown body. Saved to a file and fed to <c>--file</c>, it has to produce
    /// the same draft.
    /// </summary>
    [Fact]
    public void The_context_key_is_read_as_agent_context_when_there_is_no_markdown_body()
    {
        const string file = """
            ---
            project: hall9k
            objective: Carry the whole task record on the issue
            criteria:
            - Adoption reads it once
            context: |-
              Origin: Brian.

              Second paragraph.
            ---
            """;

        TaskFileParser.Parse(file).AgentContext.Should().Be("Origin: Brian.\n\nSecond paragraph.");
    }
}
