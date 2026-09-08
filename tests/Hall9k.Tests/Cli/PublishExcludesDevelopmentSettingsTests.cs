using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The actual publish exclusion lives in Directory.Build.targets, not in
/// <see cref="Hall9k.Cli.Commands.InstallCommand"/>'s own defense-in-depth check — that check
/// only ever sees a payload staged from a prior publish, so it cannot catch the glob itself
/// going inert. Only a real <c>dotnet publish</c> exercises the MSBuild item that matters, and
/// nothing in the rest of the suite runs one, so this is the only regression net before a tag
/// push finds out at release time instead.
/// <para>
/// <c>Category=PublishesBinary</c> marks this as one of the two tests that shell out to a real
/// build, so a cycle gate can drop the pair by filter while the mandatory final full pass still
/// runs it (documented beside <c>Category=RequiresDocker</c> in this project's README). The
/// collection of the same name is not throughput bookkeeping: both publish tests now build
/// against the repository's own <c>obj/Release</c> rather than a private intermediate tree, so
/// running them concurrently would put two MSBuild processes in the same project directories.
/// </para>
/// </summary>
[Trait("Category", "PublishesBinary")]
[Collection("PublishesBinary")]
public sealed class PublishExcludesDevelopmentSettingsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("h9k-publish-").FullName;

    public void Dispose()
    {
        // Best-effort, matching InstallCommand.TryDelete: a `dotnet publish` this test just
        // killed on a budget miss can leave a child process (or the OS) holding a file open for a
        // moment after Kill returns, and an unguarded delete here would throw and report that
        // instead of the timeout that actually failed the test.
        PublishTestSupport.TryDelete(directory);
    }

    [Fact]
    public async Task Publishing_the_daemon_excludes_its_development_settings_file()
    {
        string repoRoot = PublishTestSupport.FindRepositoryRoot();

        PublishTestSupport.ExecResult publish = await PublishTestSupport.RunPublishAsync(
            repoRoot, "Hall9k.Daemon", directory, []);

        publish.AssertSucceeded();
        File.Exists(Path.Combine(directory, "appsettings.json")).Should().BeTrue(
            "production settings still have to ship");
        File.Exists(Path.Combine(directory, "appsettings.Development.json")).Should().BeFalse(
            "Directory.Build.targets excludes a Development settings file from publish output — "
            + "if this regresses, every install ships one until the next tagged release catches it");
    }
}
