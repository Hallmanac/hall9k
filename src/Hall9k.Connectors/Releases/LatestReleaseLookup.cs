using System.ComponentModel;
using System.Text.Json;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Releases;

/// <summary>What a <see cref="LatestReleaseLookup"/> found, or why it found nothing.</summary>
public enum LatestReleaseOutcome
{
    /// <summary>A release was chosen and its assets carry this platform's archive and <c>checksums.txt</c>.</summary>
    Found,

    /// <summary>A release was chosen but one of those two assets is not on it yet (release.yml uploads them last).</summary>
    NotYetComplete,

    /// <summary>The all channel found no published release with a version it can order.</summary>
    NoRelease,

    /// <summary><c>gh</c> is not installed or not on the PATH.</summary>
    GhMissing,

    /// <summary><c>gh</c> ran and exited non-zero: an unreachable repository, a signed-out account, an unknown release.</summary>
    GhFailed,

    /// <summary><c>gh</c> never answered within its deadline.</summary>
    TimedOut,

    /// <summary><c>gh</c> answered with JSON the lookup could not read.</summary>
    Unparseable,
}

/// <summary>
/// The lookup's answer, one shape for every outcome so a caller switches on
/// <see cref="Outcome"/> rather than catching anything. <see cref="Command"/> and
/// <see cref="Detail"/> carry what a failure message needs: the <c>gh</c> subcommand that failed
/// and what it (or the exception) said.
/// </summary>
public sealed record LatestReleaseResult
{
    private LatestReleaseResult(
        LatestReleaseOutcome outcome, ReleaseChannelResolution channel, string? candidateTag, string command, string detail,
        IReadOnlyList<string> missingAssets)
    {
        Outcome = outcome;
        Channel = channel;
        CandidateTag = candidateTag;
        Command = command;
        Detail = detail;
        MissingAssets = missingAssets;
    }

    public LatestReleaseOutcome Outcome { get; }

    /// <summary>The channel this lookup used, with the warning to surface when it fell back to cleared.</summary>
    public ReleaseChannelResolution Channel { get; }

    /// <summary>The release the lookup chose, whether or not its assets are complete.</summary>
    public string? CandidateTag { get; }

    /// <summary>The tag to install: set only when <see cref="Outcome"/> is <see cref="LatestReleaseOutcome.Found"/>.</summary>
    public string? Tag => Outcome == LatestReleaseOutcome.Found ? CandidateTag : null;

    public string Command { get; }

    public string Detail { get; }

    /// <summary>The asset names a <see cref="LatestReleaseOutcome.NotYetComplete"/> release still lacks.</summary>
    public IReadOnlyList<string> MissingAssets { get; }

    internal static LatestReleaseResult Found(ReleaseChannelResolution channel, string tag) =>
        new(LatestReleaseOutcome.Found, channel, tag, string.Empty, string.Empty, []);

    internal static LatestReleaseResult NotYetComplete(
        ReleaseChannelResolution channel, string tag, IReadOnlyList<string> missingAssets) =>
        new(LatestReleaseOutcome.NotYetComplete, channel, tag, string.Empty, string.Empty, missingAssets);

    internal static LatestReleaseResult Failed(
        LatestReleaseOutcome outcome, ReleaseChannelResolution channel, string command, string detail) =>
        new(outcome, channel, null, command, detail, []);
}

/// <summary>
/// The one release lookup in the platform (idea 93c1d24d): <c>h9k update</c> and the daemon's
/// release notice both ask it, so a node never disagrees with itself about what is current. The
/// <see cref="ReleaseChannel.Cleared"/> channel is GitHub's own latest release, which excludes
/// pre-releases and drafts. The <see cref="ReleaseChannel.All"/> channel lists the repository's
/// releases and takes the published one with the highest version by
/// <see cref="BuildVersionOrdering"/>, never by creation date, since a patch cut from an older
/// tag is created later than a newer minor. <c>gh release list --json</c> carries no assets
/// field, so the all channel views its chosen tag afterwards; both channels end on that view,
/// because a release only counts once this platform's archive and <c>checksums.txt</c> are on it.
/// </summary>
public sealed class LatestReleaseLookup(ProcessRunner gh, ReleaseChannelReader? readChannel = null)
{
    private const string ChecksumsAssetName = "checksums.txt";

    /// <summary>Only this many of the newest releases are listed; the all channel picks the highest version among them.</summary>
    private const int ListLimit = 100;

    private readonly ReleaseChannelReader readChannel = readChannel ?? ReleaseChannelSetting.ReadAsync;

    public async Task<LatestReleaseResult> ResolveAsync(
        string repository, string rid, string workingDirectory, CancellationToken cancellationToken)
    {
        ReleaseChannelResolution channel = await readChannel(cancellationToken);
        try
        {
            string? tag = channel.Channel == ReleaseChannel.All
                ? await HighestPublishedTagAsync(repository, workingDirectory, channel, cancellationToken)
                : null;

            return await ViewAsync(repository, rid, tag, workingDirectory, channel, cancellationToken);
        }
        catch (LookupFailedException failure)
        {
            return failure.Result;
        }
    }

    /// <summary>
    /// <c>gh release view</c> of <paramref name="tag"/>, or of GitHub's latest release when it is
    /// null, checked for this platform's assets.
    /// </summary>
    private async Task<LatestReleaseResult> ViewAsync(
        string repository, string rid, string? tag, string workingDirectory, ReleaseChannelResolution channel,
        CancellationToken cancellationToken)
    {
        const string command = "gh release view";
        List<string> arguments = tag is null ? ["release", "view"] : ["release", "view", tag];
        arguments.AddRange(["--repo", repository, "--json", "tagName,assets"]);

        ProcessResult result = await RunAsync(command, arguments, workingDirectory, channel, cancellationToken);

        string? tagName;
        List<string> assetNames = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            tagName = document.RootElement.GetProperty("tagName").GetString();
            foreach (JsonElement asset in document.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() is { } name)
                {
                    assetNames.Add(name);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Failure(LatestReleaseOutcome.Unparseable, channel, command, exception.Message);
        }

        if (string.IsNullOrEmpty(tagName))
        {
            throw Failure(
                LatestReleaseOutcome.Unparseable, channel, command,
                "no tagName in the output, so there is no release to resolve");
        }

        string archiveName = ReleasePlatform.ArchiveFileName(rid);
        List<string> missing = [.. new[] { archiveName, ChecksumsAssetName }.Where(required => !assetNames.Contains(required))];
        return missing.Count == 0
            ? LatestReleaseResult.Found(channel, tagName)
            : LatestReleaseResult.NotYetComplete(channel, tagName, missing);
    }

    /// <summary>The published release tag with the highest version, or a <see cref="LookupFailedException"/> when there is none.</summary>
    private async Task<string> HighestPublishedTagAsync(
        string repository, string workingDirectory, ReleaseChannelResolution channel, CancellationToken cancellationToken)
    {
        const string command = "gh release list";
        ProcessResult result = await RunAsync(
            command,
            ["release", "list", "--repo", repository, "--json", "tagName,isDraft", "--limit", $"{ListLimit}"],
            workingDirectory, channel, cancellationToken);

        string? highest = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            foreach (JsonElement release in document.RootElement.EnumerateArray())
            {
                bool isDraft = release.GetProperty("isDraft").GetBoolean();
                string? tag = release.GetProperty("tagName").GetString();

                // A tag BuildVersionOrdering cannot read never claims an order, so it can neither win nor beat a readable one.
                if (!isDraft && tag is not null && IsVersion(tag)
                    && (highest is null || BuildVersionOrdering.IsOlderThan(Bare(highest), Bare(tag))))
                {
                    highest = tag;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Failure(LatestReleaseOutcome.Unparseable, channel, command, exception.Message);
        }

        return highest
            ?? throw Failure(
                LatestReleaseOutcome.NoRelease, channel, command,
                $"{repository} has no published release whose tag is a version");
    }

    /// <summary>Runs <c>gh</c>, mapping every way it can fail onto a <see cref="LookupFailedException"/> so a caller never sees the raw exception.</summary>
    private async Task<ProcessResult> RunAsync(
        string command, IReadOnlyList<string> arguments, string workingDirectory, ReleaseChannelResolution channel,
        CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await gh("gh", arguments, workingDirectory, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw Failure(LatestReleaseOutcome.GhMissing, channel, command, exception.Message);
        }
        catch (TimeoutException exception)
        {
            throw Failure(LatestReleaseOutcome.TimedOut, channel, command, exception.Message);
        }

        return result.ExitCode == 0
            ? result
            : throw Failure(LatestReleaseOutcome.GhFailed, channel, command, result.StandardError);
    }

    private static LookupFailedException Failure(
        LatestReleaseOutcome outcome, ReleaseChannelResolution channel, string command, string detail) =>
        new(LatestReleaseResult.Failed(outcome, channel, command, detail));

    // Release tags carry a leading v (v0.10.46); BuildVersionOrdering's own shape does not.
    private static string Bare(string tag) => tag.TrimStart('v');

    private static bool IsVersion(string tag) => Version.TryParse(Bare(tag).Split('-', 2)[0], out _);

    private sealed class LookupFailedException(LatestReleaseResult result) : Exception
    {
        public LatestReleaseResult Result { get; } = result;
    }
}
