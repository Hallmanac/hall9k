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
}
