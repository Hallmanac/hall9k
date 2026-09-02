using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
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
    public void CommitStyle_serializes_as_bare_string_and_maps_input_case_insensitively()
    {
        JsonSerializer.Serialize(CommitStyle.Narrative).Should().Be("\"Narrative\"");
        JsonSerializer.Deserialize<CommitStyle>("\"Append\"").Should().Be(CommitStyle.Append);

        CommitStyle.FromInput("NARRATIVE").Should().Be(CommitStyle.Narrative);
        CommitStyle.FromInput(" append ").Should().Be(CommitStyle.Append);
        CommitStyle.FromInput("squash").Should().Be(CommitStyle.Unknown, "unrecognized input is never guessed at");
        CommitStyle.FromInput(null).Should().Be(CommitStyle.Unknown);
    }

    [Fact]
    public void CommitStyle_resolves_project_over_platform_default_and_lands_on_narrative()
    {
        CommitStyle.Resolve(CommitStyle.Append, "Narrative").Should().Be(
            CommitStyle.Append, "the project override wins over the platform default");
        CommitStyle.Resolve(CommitStyle.Unknown, "Append").Should().Be(
            CommitStyle.Append, "an unset project falls through to the platform default");
        CommitStyle.Resolve(CommitStyle.Unknown, "garbage").Should().Be(
            CommitStyle.Narrative, "an unrecognized platform default lands on the documented default");
        CommitStyle.Resolve(null, null).Should().Be(CommitStyle.Narrative);
    }

    [Fact]
    public void AgentModel_serializes_as_bare_string_and_keeps_exact_model_ids()
    {
        JsonSerializer.Serialize(AgentModel.Opus).Should().Be("\"opus\"");
        JsonSerializer.Deserialize<AgentModel>("\"sonnet\"").Should().Be(AgentModel.Sonnet);
        JsonSerializer.Deserialize<AgentModel>("\"claude-opus-5[1m]\"")!.Value
            .Should().Be("claude-opus-5[1m]", "an exact model id rides through as itself");
        JsonSerializer.Deserialize<AgentModel>("\"default\"").Should().Be(
            AgentModel.Unknown, "a stored payload is an input too: the word never rehydrates as a model name");
        JsonSerializer.Deserialize<AgentModel>("\" Opus \"").Should().Be(
            AgentModel.Opus, "an alias arrives canonicalized however it was written to storage");

        AgentModel blank = (string?)null;
        (blank == AgentModel.Unknown).Should().BeTrue();
    }

    [Fact]
    public void AgentModel_canonicalizes_tier_aliases_and_leaves_exact_ids_alone()
    {
        AgentModel.FromInput(" OPUS ").Should().Be(AgentModel.Opus);
        AgentModel.FromInput("Fable").Should().Be(AgentModel.Fable);
        AgentModel.FromInput("Haiku").Should().Be(AgentModel.Haiku);
        AgentModel.FromInput(" claude-Opus-5 ").Value.Should().Be(
            "claude-Opus-5", "an exact id's casing is the provider's business, not ours");
        AgentModel.FromInput("   ").Should().Be(AgentModel.Unknown, "blank states no preference, never a guessed model");
    }

    /// <summary>
    /// 'default' is the one word that must not ride through as an exact id: Claude Code reads
    /// '--model default' as "whatever this machine is configured for", so passing it on would
    /// spawn the session on the human's personal setting and then record a model no session
    /// ran on (Decisions Log #33).
    /// </summary>
    [Fact]
    public void AgentModel_reads_default_as_no_opinion_rather_than_as_a_model_name()
    {
        AgentModel.FromInput("default").Should().Be(AgentModel.Unknown);
        AgentModel.FromInput(" DEFAULT ").Should().Be(AgentModel.Unknown);
        AgentModel.Resolve(AgentModel.FromInput("default"), AgentModel.Unknown, AgentModel.Fable, "claude-opus-5")
            .Should().Be(AgentModel.Fable, "a cleared task override defers instead of spawning on the human's setting");
        AgentModel.Resolve(null, null, null, "default")
            .Value.Should().Be(AgentModel.PlatformFallback, "even a node configured with the word lands somewhere explicit");
    }

    [Fact]
    public void AgentModel_rejects_values_that_could_not_be_handed_to_a_shell()
    {
        AgentModel.FromInput("claude-opus-5").IsWellFormed.Should().BeTrue();
        AgentModel.FromInput("claude-opus-5[1m]").IsWellFormed.Should().BeTrue();
        AgentModel.FromInput("anthropic.claude-opus-5").IsWellFormed.Should().BeTrue();
        AgentModel.Unknown.IsWellFormed.Should().BeFalse("an absent model is never spawnable");

        AgentModel.FromInput("opus; rm -rf /").IsWellFormed.Should().BeFalse();
        AgentModel.FromInput("$(whoami)").IsWellFormed.Should().BeFalse();
        AgentModel.FromInput("opus\"").IsWellFormed.Should().BeFalse();
    }

    [Fact]
    public void AgentModel_resolution_prefers_the_most_specific_level_and_always_ends_explicit()
    {
        AgentModel.Resolve(AgentModel.Haiku, AgentModel.Sonnet, AgentModel.Fable, "claude-opus-5")
            .Should().Be(AgentModel.Haiku, "a task override beats every other level");
        AgentModel.Resolve(AgentModel.Unknown, AgentModel.Sonnet, AgentModel.Fable, "claude-opus-5")
            .Should().Be(AgentModel.Sonnet, "the role default beats the project default");
        AgentModel.Resolve(AgentModel.Unknown, AgentModel.Unknown, AgentModel.Fable, "claude-opus-5")
            .Should().Be(AgentModel.Fable, "the project default beats the platform default");
        AgentModel.Resolve(AgentModel.Unknown, AgentModel.Unknown, AgentModel.Unknown, "claude-opus-5")
            .Value.Should().Be("claude-opus-5", "the chain bottoms out at the configured platform default");
        AgentModel.Resolve(null, null, null, null)
            .Value.Should().Be(AgentModel.PlatformFallback,
                "even a blanked-out platform default lands somewhere explicit, never on inheritance");
    }

    [Fact]
    public void SpendPeriod_recognizes_only_day_and_week()
    {
        SpendPeriod.FromInput("day").Should().Be(SpendPeriod.Day);
        SpendPeriod.FromInput(" WEEK ").Should().Be(SpendPeriod.Week);
        SpendPeriod.FromInput("month").Should().Be(SpendPeriod.Unknown);
        SpendPeriod.FromInput(null).Should().Be(SpendPeriod.Unknown);

        SpendPeriod.Day.IsWellFormed.Should().BeTrue();
        SpendPeriod.Week.IsWellFormed.Should().BeTrue();
        SpendPeriod.Unknown.IsWellFormed.Should().BeFalse();
    }

    [Fact]
    public void SpendPeriod_day_starts_at_utc_midnight_and_rolls_the_next_day()
    {
        DateTimeOffset midAfternoon = new(2026, 8, 19, 15, 30, 0, TimeSpan.Zero);

        SpendPeriod.Day.StartOf(midAfternoon).Should().Be(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        SpendPeriod.Day.NextRolloverAfter(midAfternoon).Should().Be(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SpendPeriod_week_starts_monday_utc_regardless_of_which_day_of_the_week_it_is_asked_from()
    {
        // 2026-08-19 is a Wednesday; the week it belongs to starts Monday 2026-08-17.
        DateTimeOffset wednesday = new(2026, 8, 19, 15, 30, 0, TimeSpan.Zero);
        DateTimeOffset monday = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        SpendPeriod.Week.StartOf(wednesday).Should().Be(monday);
        SpendPeriod.Week.StartOf(monday).Should().Be(monday, "Monday's own midnight is already the week's start");
        SpendPeriod.Week.NextRolloverAfter(wednesday).Should().Be(monday.AddDays(7));
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
