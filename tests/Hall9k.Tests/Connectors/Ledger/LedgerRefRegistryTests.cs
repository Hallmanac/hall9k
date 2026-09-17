using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Xunit;

namespace Hall9k.Tests.Connectors.Ledger;

/// <summary>
/// Pure unit tests over <see cref="LedgerRefRegistry"/>'s own matching and idempotency rules — no
/// git involved, so these do not need a repository. <see cref="GitLedgerTests"/> covers the
/// registry's actual effect (an unregistered ref refused, two registered refs both usable) against
/// real git.
/// </summary>
public sealed class LedgerRefRegistryTests
{
    [Fact]
    public void RecordsRef_IsRegisteredAsAnExactEntry_FromTheComponentsFirstCommit()
    {
        LedgerRefRegistry.IsRegistered("refs/hall9k/ledger/records").Should().BeTrue();
        LedgerRefRegistry.Records.Kind.Should().Be(LedgerRefKind.Exact);
        LedgerRefRegistry.FetchRefspecs.Should().Contain("+refs/hall9k/ledger/records:refs/hall9k/ledger/records");
    }

    /// <summary>
    /// A task's record path is keyed by task id under the same <see cref="LedgerRefRegistry.Records"/>
    /// ref every other record test writes to — idea 202383dc, A3a registered no new ref for this
    /// feature, only the path convention beside the ref that already existed.
    /// </summary>
    [Fact]
    public void RecordPath_IsKeyedByTaskIdUnderTheRecordsPathPrefix()
    {
        Guid taskId = Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5");

        LedgerRefRegistry.RecordPath(taskId).Should().Be($"records/{taskId}.yaml");
        LedgerRefRegistry.RecordPath(taskId).Should().StartWith(LedgerRefRegistry.RecordsPathPrefix);
        LedgerRefRegistry.RecordsPathPrefix.Should().Be("records/");
        LedgerRefRegistry.Records.RefspecSource.Should().Be("refs/hall9k/ledger/records");
    }

    [Fact]
    public void MessagesPrefix_IsRegisteredAsAPrefix_CoveringEveryNodesOwnOutboxRef()
    {
        LedgerRefRegistry.IsRegistered("refs/hall9k/messages/some-node-id").Should().BeTrue();
        LedgerRefRegistry.IsRegistered("refs/hall9k/messages/another-node").Should().BeTrue();
        LedgerRefRegistry.MessagesPrefix.Kind.Should().Be(LedgerRefKind.Prefix);
        LedgerRefRegistry.FetchRefspecs.Should().Contain("+refs/hall9k/messages/*:refs/hall9k/messages/*");
    }

    [Fact]
    public void MembersRef_IsRegisteredAsAnExactEntry_FromTheChainReadersFirstCommit()
    {
        LedgerRefRegistry.IsRegistered("refs/hall9k/ledger/members").Should().BeTrue();
        LedgerRefRegistry.MembersRef.Kind.Should().Be(LedgerRefKind.Exact);
        LedgerRefRegistry.FetchRefspecs.Should().Contain("+refs/hall9k/ledger/members:refs/hall9k/ledger/members");
    }

    [Fact]
    public void IsRegistered_OnAnUnrelatedRef_IsFalse()
    {
        LedgerRefRegistry.IsRegistered("refs/heads/main").Should().BeFalse();
        LedgerRefRegistry.IsRegistered("refs/hall9k/somethingnobodyregistered").Should().BeFalse();
    }

    [Fact]
    public void RegisterExact_CalledTwiceWithTheIdenticalName_ProducesOneEntry()
    {
        string refName = $"refs/hall9k/ledger/registry-test-{Guid.NewGuid():N}";

        LedgerRefEntry first = LedgerRefRegistry.RegisterExact(refName);
        LedgerRefEntry second = LedgerRefRegistry.RegisterExact(refName);

        first.Should().Be(second);
        LedgerRefRegistry.FetchRefspecs.Count(refspec => refspec == first.Refspec).Should().Be(1);
    }

    [Fact]
    public void RegisterPrefix_RequiresATrailingSlash()
    {
        Action act = () => LedgerRefRegistry.RegisterPrefix("refs/hall9k/no-trailing-slash");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RegisterExact_OutsideTheHall9kNamespace_Refuses()
    {
        Action act = () => LedgerRefRegistry.RegisterExact("refs/heads/main");

        act.Should().Throw<ArgumentException>();
        LedgerRefRegistry.IsRegistered("refs/heads/main").Should().BeFalse();
    }

    [Fact]
    public void RegisterPrefix_OutsideTheHall9kNamespace_Refuses()
    {
        Action act = () => LedgerRefRegistry.RegisterPrefix("refs/heads/");

        act.Should().Throw<ArgumentException>();
        LedgerRefRegistry.IsRegistered("refs/heads/main").Should().BeFalse();
    }
}
