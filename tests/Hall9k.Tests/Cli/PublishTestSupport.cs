using System.Diagnostics;
using FluentAssertions;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The `dotnet publish`-running machinery shared by <see cref="PublishExcludesDevelopmentSettingsTests"/>
/// and <see cref="PublishStampsInformationalVersionTests"/> — the only two tests in the suite that
/// exercise a real publish rather than mocking around it, and for the identical reason: each is a
/// regression net for an assumption only a real `dotnet publish` can actually prove (cycle 1
/// conformance review finding: the two classes had carried nearly the same ~85 lines independently).
/// </summary>
internal static class PublishTestSupport
{
    internal sealed record ExecResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Succeeded => ExitCode == 0;
    }

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "Hall9k.slnx")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException($"No Hall9k.slnx found above {AppContext.BaseDirectory}.");
    }

    internal static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs `dotnet publish` directly rather than through the shared Exec.RunAsync,
    /// because on timeout the caller must kill the whole process tree, not just the `dotnet`
    /// parent: `dotnet publish` hands off to MSBuild worker nodes that outlive a bare
    /// WaitForExitAsync cancellation and keep the NuGet global-packages lock held, which would
    /// otherwise wedge the NEXT run of this test (or a concurrent `dotnet publish` anywhere else
    /// on the machine) behind the same lock this one was trying to escape.</summary>
    internal static async Task<ExecResult> RunPublishAsync(
        string repoRoot,
        string project,
        string outputDirectory,
        string artifactsPath,
        IReadOnlyList<string> extraProperties,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        List<string> arguments =
        [
            "publish", Path.Combine(repoRoot, "src", project), "-c", "Release", "-o", outputDirectory, "--nologo",
            "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={artifactsPath}",
            .. extraProperties,
        ];
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ExecResult(process.ExitCode, await standardOutput, await standardError);
    }

    internal static void AssertSucceeded(this ExecResult result)
    {
        result.Succeeded.Should().BeTrue(
            $"dotnet publish should succeed:\n{result.StandardOutput}\n{result.StandardError}");
    }
}
