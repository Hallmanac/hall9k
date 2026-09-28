using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Extensions;
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

        [CommandOption("--display-name <NAME>")]
        [Description(
            "The name teammates see for this member: a label only, never part of any trust or "
            + "cross-check decision (task e6744304). With --project, this is that project's own "
            + "entry; without it, this is this machine's own default, which applies to every "
            + "project that has no entry of its own. An empty value ('') clears whichever one this "
            + "call targets: the project's entry, or the default. The value is trimmed; a blank "
            + "result clears, and anything else must be 1 to 64 characters with no control "
            + "characters (a newline, a carriage return, and a tab included). It settles into the "
            + "effective name and is then written into the affected project's own node file, which "
            + "h9k project members reads back.")]
        public string? DisplayName { get; init; }

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Scopes --display-name to one project this node has already joined, by name, an "
            + "unambiguous fragment of it, or its id, instead of changing this machine's own "
            + "default. Naming a project this node has not joined is refused. Has no effect on any "
            + "other option.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            session, settings, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new NodeKeyStore(), cancellationToken);
    }

    /// <summary>The whole flow, seamed on <see cref="ILedger"/> and <see cref="NodeKeyStore"/> so a
    /// test drives the display-name node-file writes against <c>FakeLedger</c> rather than a real
    /// repository (Brian's 2026-09-13 testing rule), the same shape <c>h9k node vouch</c> already
    /// uses.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedger ledger, NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        if (settings.RerequestReview is null
            && settings.VoiceSkill is null
            && !settings.ClearVoiceSkill
            && settings.Persona is not { Length: > 0 }
            && !settings.ClearPersonas
            && settings.DisplayName is null)
        {
            throw new DomainValidationException(
                "Nothing to change — pass --rerequest-review on|off|default, --voice-skill <NAME>, "
                + "--clear-voice-skill, --persona engineer|qa|designer, --clear-personas, or "
                + "--display-name <NAME> (optionally with --project <PROJECT>). "
                + "h9k owner show prints the current preferences.");
        }

        // Refused before any owner is resolved or anything is recorded: an option pair that
        // contradicts itself is not a fact about any owner. DisplayName.Parse's own rule (trimmed,
        // blank clears, else 1 to 64 characters with no control characters) is enforced here too,
        // before anything else in this command runs.
        Optional<VoiceSkillName> voiceSkill =
            VoiceSkillOption.Resolve(settings.VoiceSkill, settings.ClearVoiceSkill);
        Optional<IReadOnlyList<ReviewPersona>> reviewPersonas =
            ReviewPersonaOption.Resolve(settings.Persona, settings.ClearPersonas);
        DisplayName? displayName = settings.DisplayName is null ? null : DisplayName.Parse(settings.DisplayName);

        // Registers this machine's owner if the database has never seen one, so the first
        // command a fresh install runs can be this one (every other writing command does the
        // same). Idempotent: an existing owner is found, not replaced.
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
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

        // A --project naming a project this node has not joined is refused before anything is
        // recorded: checked purely against this node's own local registrations, the same
        // ownership tier VoiceSkillLocation.Resolve above already reads from, never against the
        // ledger, so this refusal is meant to be instant, not a network round trip.
        ProjectDetails? targetProject = null;
        if (displayName is not null && settings.Project is not null)
        {
            ProjectDetails candidate;
            try
            {
                candidate = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
            }
            catch (DomainNotFoundException)
            {
                throw new DomainValidationException(
                    $"'{settings.Project}' is not a project this node has joined: h9k project list shows what "
                    + "this node already has, and h9k project join <project> joins a new one.");
            }

            if (candidate.OwnerId != details.Id)
            {
                throw new DomainValidationException(
                    $"'{candidate.Name}' is not a project this node has joined for owner '{details.Name}': "
                    + "h9k project list shows what this node already has.");
            }

            targetProject = candidate;
        }

        Optional<DisplayName> defaultDisplayName = Optional<DisplayName>.None;
        Optional<OwnerProjectDisplayName> projectDisplayName = Optional<OwnerProjectDisplayName>.None;
        if (displayName is not null)
        {
            if (targetProject is not null)
            {
                projectDisplayName = Optional<OwnerProjectDisplayName>.Of(
                    new OwnerProjectDisplayName(targetProject.Id, displayName));
            }
            else
            {
                defaultDisplayName = Optional<DisplayName>.Of(displayName);
            }
        }

        OwnerSettingsChanged changed = OwnerDecider.ChangeSettings(
            owner,
            settings.RerequestReview is null
                ? Optional<ReviewRerequestPolicy>.None
                : Optional<ReviewRerequestPolicy>.Of(ReviewRerequestOption.Parse(settings.RerequestReview)),
            DateTimeOffset.UtcNow,
            voiceSkill,
            reviewPersonas,
            defaultDisplayName,
            projectDisplayName);

        session.Events.Append(details.Id, changed);
        await session.SaveChangesAsync(cancellationToken);

        // Applied to the in-memory aggregate right away so the node-file writes below see the
        // effective name this same call just recorded, without a second round trip to re-aggregate
        // from what was just saved.
        owner.Apply(changed);

        if (displayName is not null)
        {
            IReadOnlyList<ProjectDetails> affected = targetProject is not null
                ? [targetProject]
                : await session.Query<ProjectDetails>()
                    .Where(project => project.OwnerId == details.Id && !project.IsArchived)
                    .ToListAsync(cancellationToken);
            await WriteDisplayNamesAsync(ledger, keyStore, context.NodeId, details, owner, affected, cancellationToken);
        }

        AnsiConsole.MarkupLine($"[green]Owner '{details.Name.EscapeMarkup()}' settings updated.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Brings each affected project's own node file up to its effective display name: the named
    /// project alone, or every joined, non-archived project when the machine default changed. The
    /// local setting is already saved by the time this runs, so a failure here never loses it: this
    /// only ever throws at the very end, naming every project that failed, after every other
    /// project has already been attempted (the same per-project isolation <c>h9k node vouch</c>'s
    /// own loop uses).
    /// </summary>
    private static async Task WriteDisplayNamesAsync(
        ILedger ledger, NodeKeyStore keyStore, Guid nodeId, OwnerDetails details, OwnerAggregate owner,
        IReadOnlyList<ProjectDetails> affected, CancellationToken cancellationToken)
    {
        if (affected.Count == 0)
        {
            return;
        }

        NodeSigningKey key = await keyStore.EnsureAsync(nodeId, cancellationToken);
        LedgerCommitter committer = new(
            details.Name.IsNotBlank() ? details.Name : Environment.UserName,
            details.Email.IsNotBlank() ? details.Email : $"{nodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        List<string> failedProjects = [];
        foreach (ProjectDetails project in affected)
        {
            DisplayName effective = owner.EffectiveDisplayName(project.Id);
            try
            {
                NodeFileRefreshOutcome outcome = await NodeFileWriter.RefreshDisplayNameAsync(
                    ledger, project.RepositoryPath, nodeId, effective, key.PublicKeyLine, committer, signingKey,
                    cancellationToken);
                if (outcome == NodeFileRefreshOutcome.SigningKeyDiffers)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]'{project.Name.EscapeMarkup()}'s own node file names a different public key "
                        + "than this node signs with now, left untouched; re-run h9k project join there to "
                        + "fix it.[/]");
                }
            }
            catch (Exception exception)
                when (exception is LedgerPushRejectedException or InvalidOperationException or DomainConflictException)
            {
                failedProjects.Add(project.Name);
                AnsiConsole.MarkupLine(
                    $"[red]Failed to update the display name in '{project.Name.EscapeMarkup()}' "
                    + $"({exception.Message.EscapeMarkup()}).[/]");
            }
        }

        if (failedProjects.Count > 0)
        {
            throw new DomainValidationException(
                $"The display name was saved for this machine, but its node file could not be updated in "
                + $"{failedProjects.Count} project(s): {string.Join(", ", failedProjects)}. The next daemon "
                + "start there brings it up to date, or re-run h9k owner set once the failure is fixed.");
        }
    }
}
