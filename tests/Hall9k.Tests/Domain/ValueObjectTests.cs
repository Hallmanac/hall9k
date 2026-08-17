using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class ValueObjectTests
{
    [Fact]
    public void WorkItemProvider_serializes_as_bare_string_and_round_trips()
    {
        string json = JsonSerializer.Serialize(WorkItemProvider.GitHub);
        json.Should().Be("\"github\"");

        WorkItemProvider back = JsonSerializer.Deserialize<WorkItemProvider>(json)!;
        (back == WorkItemProvider.GitHub).Should().BeTrue();
    }

    [Fact]
    public void WorkItemProvider_unrecognized_values_round_trip_as_themselves()
    {
        WorkItemProvider provider = JsonSerializer.Deserialize<WorkItemProvider>("\"azure-devops\"")!;
        provider.Value.Should().Be("azure-devops", "the set is defined once, not enforced");
    }

    [Fact]
    public void WorkItemProvider_blank_normalizes_to_unknown_sentinel()
    {
        WorkItemProvider provider = (string?)null;
        (provider == WorkItemProvider.Unknown).Should().BeTrue();
    }

    [Fact]
    public void CredentialReference_canonical_forms_round_trip()
    {
        CredentialReference.GhCli.ToString().Should().Be("gh-cli");
        CredentialReference.Keychain("hall9k-jira").ToString().Should().Be("keychain:hall9k-jira");
        CredentialReference.EnvironmentVariable("ANTHROPIC_API_KEY").ToString().Should().Be("env:ANTHROPIC_API_KEY");

        CredentialReference parsed = CredentialReference.Parse("keychain:hall9k-jira");
        parsed.Should().Be(CredentialReference.Keychain("hall9k-jira"));
    }

    [Fact]
    public void ReviewVerdict_serializes_as_bare_string_and_blank_normalizes_to_unknown()
    {
        JsonSerializer.Serialize(ReviewVerdict.MergeReady).Should().Be("\"MergeReady\"");
        JsonSerializer.Deserialize<ReviewVerdict>("\"NeedsFixes\"").Should().Be(ReviewVerdict.NeedsFixes);

        ReviewVerdict blank = (string?)null;
        (blank == ReviewVerdict.Unknown).Should().BeTrue();
    }

    [Fact]
    public void ReviewFixOutcome_round_trips_and_keeps_unrecognized_values()
    {
        JsonSerializer.Deserialize<ReviewFixOutcome>("\"Disputed\"").Should().Be(ReviewFixOutcome.Disputed);
        JsonSerializer.Deserialize<ReviewFixOutcome>("\"Deferred\"")!.Value
            .Should().Be("Deferred", "the set is defined once, not enforced");
    }

    [Fact]
    public void Optional_distinguishes_absent_from_explicitly_null()
    {
        Optional<string> absent = Optional<string>.None;
        Optional<string> explicitlyNull = Optional<string>.Of(null);

        absent.HasValue.Should().BeFalse();
        explicitlyNull.HasValue.Should().BeTrue();
        explicitlyNull.Value.Should().BeNull();

        string json = JsonSerializer.Serialize(explicitlyNull);
        JsonSerializer.Deserialize<Optional<string>>(json).HasValue.Should().BeTrue();
        JsonSerializer.Deserialize<Optional<string>>(JsonSerializer.Serialize(absent)).HasValue.Should().BeFalse();
    }
}
