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

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Body_is_optional()
    {
        TaskFileContent content = TaskFileParser.Parse("---\nobjective: x\ncriteria:\n- y\n---");

        content.AgentContext.Should().BeNull();
    }
}
