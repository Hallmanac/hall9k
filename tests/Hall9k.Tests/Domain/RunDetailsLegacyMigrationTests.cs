using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <c>RunDetailsProjection</c> is registered <c>ProjectionLifecycle.Inline</c>
/// (<c>MartenConfiguration.cs</c>), so a stored document is never rebuilt from its full event
/// history — a run already mid-flight when <c>UncommittedWorkRecovery</c> (a nullable object) was
/// renamed to <c>UncommittedWorkRecoveries</c> (a list) would otherwise deserialize straight into
/// an empty list on the next daemon build, silently losing the one recovery attempt recorded
/// under the old shape from <c>h9k task show</c>'s own historical record (independent pre-PR
/// review, cycle 1, adversarial lens). The folded-in entry is tagged <see cref="RunSessionLeg.Unknown"/>,
/// since the old shape never recorded which leg it was for, so on its own it does NOT grant that
/// run's real leg a second automatic recovery — <c>HasUncommittedWorkRecoveryAttempt</c> checks by
/// leg, and <c>Unknown</c> never matches <c>Build</c>, <c>Fix</c>, or <c>RebaseRecovery</c>; only a
/// per-leg entry recorded from this point forward provides that eligibility protection
/// (<c>RunDetails.cs</c>'s own doc on <c>LegacyUncommittedWorkRecovery</c>). These pin the shim
/// that folds the old shape in once, on first read, using the same camelCase policy Marten itself
/// is configured with (<c>UseSystemTextJsonForSerialization</c>).
/// </summary>
public sealed class RunDetailsLegacyMigrationTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void A_document_written_under_the_old_singular_field_folds_into_the_list_on_read()
    {
        string legacyJson = """
            {
                "uncommittedWorkRecovery": {
                    "strandedFiles": ["src/Feature.cs"],
                    "reason": "modified-but-uncommitted tracked file(s)",
                    "attemptedAt": "2026-09-01T12:00:00+00:00",
                    "recoveredCleanly": null,
                    "discardedFiles": []
                }
            }
            """;

        RunDetails view = JsonSerializer.Deserialize<RunDetails>(legacyJson, Options)!;

        view.UncommittedWorkRecoveries.Should().ContainSingle();
        UncommittedWorkRecoveryRecord recovery = view.UncommittedWorkRecoveries[0];
        recovery.StrandedFiles.Should().ContainSingle().Which.Should().Be("src/Feature.cs");
        recovery.Leg.Should().Be(RunSessionLeg.Unknown,
            "the old shape never recorded a leg, and Unknown is the sentinel for exactly that gap");
        view.HasUncommittedWorkRecoveryAttempt(RunSessionLeg.Unknown).Should().BeTrue(
            "the folded-in attempt must actually be visible to the same eligibility check a live run reads");
    }

    [Fact]
    public void A_document_written_under_the_old_singular_field_clears_it_after_folding_so_the_next_write_self_heals()
    {
        string legacyJson = """
            {
                "uncommittedWorkRecovery": {
                    "strandedFiles": ["src/Feature.cs"],
                    "reason": "modified-but-uncommitted tracked file(s)",
                    "attemptedAt": "2026-09-01T12:00:00+00:00",
                    "recoveredCleanly": null,
                    "discardedFiles": []
                }
            }
            """;

        RunDetails view = JsonSerializer.Deserialize<RunDetails>(legacyJson, Options)!;

        view.LegacyUncommittedWorkRecovery.Should().BeNull(
            "the fold already preserved the historical record in UncommittedWorkRecoveries, so the next " +
            "save of this document should stop writing the now-redundant legacy field");
    }

    [Fact]
    public void A_document_already_written_under_the_new_list_shape_ignores_the_legacy_field()
    {
        string newJson = """
            {
                "uncommittedWorkRecoveries": [
                    {
                        "strandedFiles": ["src/Feature.cs"],
                        "reason": "modified-but-uncommitted tracked file(s)",
                        "attemptedAt": "2026-09-01T12:00:00+00:00",
                        "recoveredCleanly": null,
                        "discardedFiles": [],
                        "leg": "Build"
                    }
                ]
            }
            """;

        RunDetails view = JsonSerializer.Deserialize<RunDetails>(newJson, Options)!;

        view.UncommittedWorkRecoveries.Should().ContainSingle();
        view.UncommittedWorkRecoveries[0].Leg.Should().Be(RunSessionLeg.Build);
    }

    [Fact]
    public void A_document_with_neither_field_stays_an_empty_list() =>
        JsonSerializer.Deserialize<RunDetails>("{}", Options)!.UncommittedWorkRecoveries.Should().BeEmpty();
}
