using FluentAssertions;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="CleanBaseGateVerdict.ComputeId"/> — the cache key a clean-base comparison's verdict
/// is remembered and looked up under (task: the clean-base comparison can actually finish).
/// </summary>
public sealed class CleanBaseGateVerdictTests
{
    [Fact]
    public void A_changed_gate_command_at_the_same_commit_computes_a_different_id()
    {
        // independent pre-PR review, cycle 1, both lenses, medium: a project's base commit does
        // not move when only `h9k project set --verify` changes a gate's own command, so a key
        // that ignored the command would replay a verdict recorded for the PREVIOUS command as an
        // observation of whatever the gate was just changed to.
        Guid nodeId = DomainId.New();
        Guid projectId = DomainId.New();
        const string baseCommitSha = "abc123";

        string before = CleanBaseGateVerdict.ComputeId(nodeId, projectId, "test", "dotnet test", baseCommitSha);
        string after = CleanBaseGateVerdict.ComputeId(
            nodeId, projectId, "test", "dotnet test --filter Category!=Flaky", baseCommitSha);

        before.Should().NotBe(after);
    }

    [Fact]
    public void The_same_gate_name_and_command_at_the_same_commit_computes_the_same_id()
    {
        Guid nodeId = DomainId.New();
        Guid projectId = DomainId.New();
        const string baseCommitSha = "abc123";

        string first = CleanBaseGateVerdict.ComputeId(nodeId, projectId, "test", "dotnet test", baseCommitSha);
        string second = CleanBaseGateVerdict.ComputeId(nodeId, projectId, "test", "dotnet test", baseCommitSha);

        first.Should().Be(second, "two runs against the identical gate must share one cached verdict");
    }
}
