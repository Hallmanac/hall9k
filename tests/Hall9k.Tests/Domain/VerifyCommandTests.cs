using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Task: a verify gate can be scoped to a path set. The path set is a list on a positional record,
/// so equality and the fingerprint both have to read its contents rather than its identity.
/// </summary>
public sealed class VerifyCommandTests
{
    [Fact]
    public void Two_gates_with_the_same_path_set_contents_are_equal()
    {
        VerifyCommand first = new("test", "dotnet test", PathSet: ["src/", "!src/generated/"]);
        VerifyCommand second = new("test", "dotnet test", PathSet: ["src/", "!src/generated/"]);

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void Gates_differing_in_path_set_contents_are_not_equal()
    {
        VerifyCommand scoped = new("test", "dotnet test", PathSet: ["src/"]);

        scoped.Should().NotBe(new VerifyCommand("test", "dotnet test", PathSet: ["lib/"]));
        scoped.Should().NotBe(new VerifyCommand("test", "dotnet test", PathSet: ["src/", "lib/"]));
        scoped.Should().NotBe(new VerifyCommand("test", "dotnet test"));
    }

    [Fact]
    public void An_absent_and_an_empty_path_set_are_equal_and_fingerprint_alike()
    {
        VerifyCommand absent = new("test", "dotnet test");
        VerifyCommand empty = new("test", "dotnet test", PathSet: []);

        absent.Should().Be(empty);
        VerifyCommand.Fingerprint([absent]).Should().Be(VerifyCommand.Fingerprint([empty]));
    }

    [Fact]
    public void A_gate_without_a_path_set_keeps_its_existing_fingerprint_byte_for_byte()
    {
        VerifyCommand.Fingerprint([new VerifyCommand("build", "dotnet build"), new VerifyCommand("test", "dotnet test", "Category=Slow")])
            .Should().Be("5:build:12:dotnet build:-1:|4:test:11:dotnet test:13:Category=Slow");
    }

    [Fact]
    public void A_path_set_changes_the_fingerprint_and_each_glob_is_length_prefixed()
    {
        VerifyCommand.Fingerprint([new VerifyCommand("test", "dotnet test", PathSet: ["src/", "!src/gen/"])])
            .Should().Be("4:test:11:dotnet test:-1::2:4:src/:9:!src/gen/");
    }

    [Fact]
    public void A_comma_inside_one_glob_never_collides_with_two_globs()
    {
        string joined = VerifyCommand.Fingerprint([new VerifyCommand("test", "dotnet test", PathSet: ["a,b"])]);
        string split = VerifyCommand.Fingerprint([new VerifyCommand("test", "dotnet test", PathSet: ["a", "b"])]);

        joined.Should().NotBe(split);
    }

    [Fact]
    public void A_changed_path_set_holds_a_node_until_it_accepts_it_and_an_unchanged_one_does_not()
    {
        IReadOnlyList<VerifyCommand> accepted = [new VerifyCommand("test", "dotnet test", PathSet: ["src/"])];

        GateSetAcceptance.Decide(accepted, [new VerifyCommand("test", "dotnet test", PathSet: ["src/"])]).Proceed
            .Should().BeTrue("the deserialized copy of an unchanged set compares by contents");
        GateSetAcceptance.Decide(accepted, [new VerifyCommand("test", "dotnet test", PathSet: ["lib/"])]).Proceed
            .Should().BeFalse();
    }

    [Fact]
    public void The_acceptance_diff_shows_an_unchanged_scoped_gate_as_unchanged()
    {
        VerifyCommand scoped = new("vitest", "npm test", PathSet: ["web/"]);
        VerifyCommand changed = new("test", "dotnet test --no-build");

        GateSetAcceptance.Decision decision = GateSetAcceptance.Decide(
            [new VerifyCommand("vitest", "npm test", PathSet: ["web/"]), new VerifyCommand("test", "dotnet test")],
            [scoped, changed]);

        decision.Diff.Select(line => line.Kind).Should().Equal(
            GateSetAcceptance.GateDiffLineKind.Unchanged,
            GateSetAcceptance.GateDiffLineKind.Removed,
            GateSetAcceptance.GateDiffLineKind.Added);
    }

    [Fact]
    public void A_stream_recorded_before_the_field_existed_deserializes_with_no_path_set()
    {
        VerifyCommand gate = JsonSerializer.Deserialize<VerifyCommand>(
            """{"Name":"test","Command":"dotnet test","HostCoupledFilter":null}""")
            ?? throw new InvalidOperationException("deserialized to null");

        gate.PathSet.Should().BeNull();
        gate.IsPathScoped.Should().BeFalse();
    }

    [Fact]
    public void A_path_set_round_trips_through_json()
    {
        VerifyCommand original = new("test", "dotnet test", PathSet: ["src/", "!src/gen/"]);

        VerifyCommand? roundTripped = JsonSerializer.Deserialize<VerifyCommand>(JsonSerializer.Serialize(original));

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void A_gate_duration_recorded_before_the_flag_existed_reads_as_not_path_skipped()
    {
        GateDuration duration = JsonSerializer.Deserialize<GateDuration>(
            """{"Gate":"test","Duration":"00:00:05","Passed":true}""")
            ?? throw new InvalidOperationException("deserialized to null");

        duration.PathScopeSkipped.Should().BeFalse();
    }
}
