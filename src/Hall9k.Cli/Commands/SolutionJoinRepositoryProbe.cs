using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Reads the origin of a project registered with <c>--repo</c> alone, which records no repository URL,
/// so <c>h9k solution join</c> can tell that such a project is a bundle member instead of registering a
/// second copy of its repository. Best-effort: any trouble reading it answers null, which leaves the
/// project matchable by its project key only.
/// </summary>
internal static class SolutionJoinRepositoryProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static Uri? OriginUrlOf(ProjectDetails project)
    {
        if (!Directory.Exists(project.RepositoryPath))
        {
            return null;
        }

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    ArgumentList = { "-C", project.RepositoryPath, "remote", "get-url", "origin" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 && output.GetAwaiter().GetResult().Trim() is { Length: > 0 } url
                ? GitRemoteUrl.Parse(url)
                : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or DomainValidationException)
        {
            return null;
        }
    }
}
