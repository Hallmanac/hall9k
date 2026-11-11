using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Hall9k.Daemon;

/// <summary>
/// The daemon's wiring of git remote authentication, out of Program.cs so a test can run it. A git
/// call that reaches github.com gets its project's token through git's own per-call environment
/// configuration, so git-remote-https never starts the owner's credential helper (sh and gh on
/// Windows, a kernel token kept for each until reboot). Per call, on the child's start info alone:
/// nothing here, or in <see cref="GitRemoteAuthentication"/>, writes the token or any of its keys
/// into this process's environment, which every agent session would inherit.
/// </summary>
internal static class DaemonGitRemoteAuthentication
{
    /// <summary>
    /// Hands <paramref name="configure"/> the daemon's token source, built from the host's store and
    /// its one shared <see cref="ProjectGitHubClient"/>. Program.cs passes
    /// <see cref="GitRemoteAuthentication.Configure"/>; a test passes a capturing setter, because
    /// the real one is process-wide and every test's remote git call would read it.
    /// </summary>
    public static void Configure(IServiceProvider services, Action<IGitRemoteTokenSource> configure) =>
        configure(new ProjectGitRemoteTokenSource(
            services.GetRequiredService<IDocumentStore>(), services.GetRequiredService<ProjectGitHubClient>()));
}
