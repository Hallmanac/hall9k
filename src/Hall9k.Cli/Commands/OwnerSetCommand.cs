using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class OwnerSetCommand : Hall9kAsyncCommand<OwnerSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[OWNER]")]
        [Description(
            "Whose preferences to change: their name, an unambiguous fragment of it or their email, "
            + "or their full id. Omit it when this platform has exactly one owner — a convenience "
            + "offered only where it cannot be wrong (Decisions Log #34).")]
        public string? Owner { get; init; }

        [CommandOption("--rerequest-review <ON|OFF|DEFAULT>")]
        [Description(
            "Whether closeout asks a pull request's reviewers for another pass once a fix follow-up "
            + "has pushed, so whoever raised the findings countersigns that they were addressed "
            + "(Decisions Log #62). 'on' buys that countersignature and spends review quota for it; "
            + "'off' lets the pull request settle on the internal review, the in-thread replies, and "
            + "CI — the guards that already ran before the fixes were pushed. 'default' clears this "
            + "preference. A project setting outranks this one, and the node default "
            + "(DaemonOptions.DefaultReviewRerequest, off) sits under both. Bounded either way: "
            + "DaemonOptions.MaxReviewRerequestsAfterFixes caps the passes per task so review cannot "
            + "loop on its own refinements.")]
        public string? RerequestReview { get; init; }

        [CommandOption("--voice-skill <NAME>")]
        [Description(
            "The skill this owner writes in, by name. Every prompt seam where a session composes "
            + "text a human reads as the owner's — a pull request description, a review-thread "
            + "reply, a commit message, a posted review finding, a drafted reply to a mention — "
            + "then tells that session to load this skill and its matching context "
            + "(contexts/code-review.md for prose that posts to GitHub, contexts/explainer.md for "
            + "a draft the owner reads and decides on) before writing a word. The skill itself "
            + "stays the owner's: it is referenced by name and never copied into a project, a "
            + "prompt template, or the platform. The name must already be a skill directory in the "
            + "owner's own user skills (~/.claude/skills/<NAME>) or in a project home's skills/, "
            + "and the repository's own PR-description rule still decides the structure — the "
            + "voice skill decides only the prose.")]
        public string? VoiceSkill { get; init; }

        [CommandOption("--clear-voice-skill")]
        [Description(
            "Forget the voice skill, so every prompt seam renders exactly as it does for an owner "
            + "who never named one. Its own switch rather than a 'default' word passed to "
            + "--voice-skill, because every other value that option takes is a real skill name and "
            + "one of them could legitimately be spelled 'default'.")]
        public bool ClearVoiceSkill { get; init; }

        [CommandOption("--persona <engineer|qa|designer>")]
        [Description(
            "A review persona this member holds, repeatable — the lens they review somebody else's "
            + "pull request through (idea b9b09779). A pull request assigned to them mints the same "
            + "pr-review task it always has, and that task runs one review session per declared "
            + "persona on its single worktree and branch: 'engineer' is today's pull-request review "
            + "of code, logic and functionality, unchanged; 'qa' is compliance and functionality "
            + "through the lens of blast radius; 'designer' is user experience, the proposed design, "
            + "accessibility and the project's design system. The set is fixed, because each persona "
            + "maps to its own prompt and criteria in the platform's persona registry — a persona "
            + "with no prompt registered yet is named in the findings report as skipped rather than "
            + "silently ignored. Declaring none is the ordinary case and reads as the engineer's "
            + "review, so nothing changes for anyone who never passes this. Repeating the option "
            + "replaces the whole declaration rather than adding to it: pass every persona the "
            + "member holds in one command.")]
        public string[]? Persona { get; init; }

        [CommandOption("--clear-personas")]
        [Description(
            "Declare no review personas, so a pull request assigned to this member gets the "
            + "engineer's review again — exactly what it gets for a member who never declared one. "
            + "Its own switch rather than a word passed to --persona, because every value that "
            + "option takes is a persona the registry can resolve.")]
        public bool ClearPersonas { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.RerequestReview is null
            && settings.VoiceSkill is null
            && !settings.ClearVoiceSkill
            && settings.Persona is not { Length: > 0 }
            && !settings.ClearPersonas)
        {
            throw new DomainValidationException(
                "Nothing to change — pass --rerequest-review on|off|default, --voice-skill <NAME>, "
                + "--clear-voice-skill, --persona engineer|qa|designer, or --clear-personas. "
                + "h9k owner show prints the current preferences.");
        }

        // Refused before the database is opened at all: an option pair that contradicts itself is
        // not a fact about any owner.
        Optional<VoiceSkillName> voiceSkill =
            VoiceSkillOption.Resolve(settings.VoiceSkill, settings.ClearVoiceSkill);
        Optional<IReadOnlyList<ReviewPersona>> reviewPersonas =
            ReviewPersonaOption.Resolve(settings.Persona, settings.ClearPersonas);

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        // Registers this machine's owner if the database has never seen one, so the first
        // command a fresh install runs can be this one (every other writing command does the
        // same). Idempotent: an existing owner is found, not replaced.
        await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        OwnerDetails details = await OwnerResolver.ResolveOrSoleAsync(session, settings.Owner, cancellationToken);
        OwnerAggregate owner =
            (await session.Events.AggregateStreamAsync<OwnerAggregate>(details.Id, token: cancellationToken))!;

        // Refused where the human typed it, against this machine's own two tiers, rather than at
        // the prompt seam that names it hours later in a session's own prompt nobody reads. The
        // owner's project homes are the second tier, so they are read here from the same owner the
        // resolver just settled on.
        if (voiceSkill.Value is { HasValue: true } named)
        {
            IReadOnlyList<ProjectDetails> owned = await session.Query<ProjectDetails>()
                .Where(project => project.OwnerId == details.Id)
                .ToListAsync(cancellationToken);
            VoiceSkillLocation.Resolve(
                named,
                owned.Where(project => project.HomeDirectory.HasValue)
                    .Select(project => project.HomeDirectory.Value)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal));
        }

        OwnerSettingsChanged changed = OwnerDecider.ChangeSettings(
            owner,
            settings.RerequestReview is null
                ? Optional<ReviewRerequestPolicy>.None
                : Optional<ReviewRerequestPolicy>.Of(ReviewRerequestOption.Parse(settings.RerequestReview)),
            DateTimeOffset.UtcNow,
            voiceSkill,
            reviewPersonas);

        session.Events.Append(details.Id, changed);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine($"[green]Owner '{details.Name.EscapeMarkup()}' settings updated.[/]");
        return ExitCodes.Ok;
    }
}
