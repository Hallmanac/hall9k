using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The input side of a run's usage (Decisions Log #30): fresh prompt input, cache reads,
/// and cache writes accumulate as three separate counts because they price differently,
/// and streams written before the cache counts existed replay as zero rather than a guess.
/// </summary>
public sealed class RunTokenAccountingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Matches the daemon's Marten setup, so the payloads below are the stored shape.</summary>
    private static readonly JsonSerializerOptions StoredJson =
        new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void The_aggregate_accumulates_each_input_kind_on_its_own_line()
    {
        Guid id = DomainId.New();
        RunAggregate run = new();

        run.Apply(new TokensRecorded(id, 118, 80_515, 16.19052m, Now, CacheReadInputTokens: 8_239_942, CacheCreationInputTokens: 196_080));
        run.Apply(new TokensRecorded(id, 14, 7_439, 1.728278m, Now, CacheReadInputTokens: 257_857, CacheCreationInputTokens: 37_635));

        run.InputTokens.Should().Be(132, "fresh prompt input is a tiny fraction of a cached session");
        run.CacheReadInputTokens.Should().Be(8_497_799);
        run.CacheCreationInputTokens.Should().Be(233_715);
        run.OutputTokens.Should().Be(87_954);
        run.TotalInputTokens.Should().Be(8_731_646, "the whole input side is what a cost report needs");
        run.CostUsd.Should().Be(17.918798m, "cost is the sum of what the sessions reported, never priced from tokens");
    }

    [Fact]
    public void Run_details_accumulates_the_same_counts_the_aggregate_does()
    {
        RunDetailsProjection projection = new();
        Guid id = DomainId.New();
        RunDetails view = new() { Id = id };

        projection.Apply(
            new FakeEvent<TokensRecorded>(new TokensRecorded(id, 118, 80_515, 16.19052m, Now, 8_239_942, 196_080)), view);
        projection.Apply(
            new FakeEvent<TokensRecorded>(new TokensRecorded(id, 14, 7_439, 1.728278m, Now, 257_857, 37_635)), view);

        view.InputTokens.Should().Be(132);
        view.CacheReadInputTokens.Should().Be(8_497_799);
        view.CacheCreationInputTokens.Should().Be(233_715);
        view.OutputTokens.Should().Be(87_954);
        view.CostUsd.Should().Be(17.918798m);
    }

    [Fact]
    public void An_event_written_before_the_cache_counts_existed_replays_as_zero()
    {
        // The stored shape of the 14 runs behind the incident: an input side with no cache fields.
        const string storedBeforeTheCacheFields =
            """{"id":"01a01754-4f0e-7775-af1e-3aca2e67be8b","inputTokens":822,"outputTokens":444443,"costUsd":null,"recordedAt":"2026-08-18T12:00:00+00:00"}""";

        TokensRecorded? replayed = JsonSerializer.Deserialize<TokensRecorded>(storedBeforeTheCacheFields, StoredJson);

        replayed.Should().NotBeNull();
        replayed.InputTokens.Should().Be(822);
        replayed.OutputTokens.Should().Be(444_443);
        replayed.CacheReadInputTokens.Should().Be(0, "what was never observed is zero, not reconstructed");
        replayed.CacheCreationInputTokens.Should().Be(0);

        RunAggregate run = new();
        run.Apply(replayed);
        run.TotalInputTokens.Should().Be(822, "an old stream still replays, it just under-reports honestly");
    }

    [Fact]
    public void A_recorded_cost_is_carried_as_observed_and_an_unreported_one_stays_unknown()
    {
        Guid id = DomainId.New();
        RunAggregate run = new();

        run.Apply(new TokensRecorded(id, 100, 200, CostUsd: null, Now, 5_000, 1_000));
        run.CostUsd.Should().BeNull("a session that reported no cost leaves the run's cost unknown");

        run.Apply(new TokensRecorded(id, 100, 200, CostUsd: 0.0123m, Now, 5_000, 1_000));
        run.CostUsd.Should().Be(0.0123m, "only the reported costs add up; the unreported one is not imputed");
    }
}
