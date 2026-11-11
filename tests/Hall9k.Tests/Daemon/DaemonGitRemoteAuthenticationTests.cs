using System.Collections;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// h9kd's startup wiring of git remote authentication writes no token and no header key into the
/// daemon's own environment, which every agent session it starts inherits. The real setter,
/// <see cref="GitRemoteAuthentication.Configure"/>, is process-wide and read by every remote git
/// call any test makes, so this test hands the wiring a capturing setter instead and never calls it.
/// </summary>
public sealed class DaemonGitRemoteAuthenticationTests
{
    private const string Token = "ghp_DAEMONSTARTUPTOKEN0123456789";

    [Fact]
    public async Task Startup_hands_the_setter_a_source_and_writes_no_token_or_header_key_into_this_process()
    {
        RecordingProcessRunner tokenRunner = RecordingProcessRunner.Succeeding(Token + "\n");
        ProjectGitHubClient client = new(
            RecordingEnvironmentProcessRunner.Succeeding("{}").Runner, tokenRunner.Runner, _ => null);
        using DocumentStore store = DocumentStore.For(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IDocumentStore>(store)
            .AddSingleton(client)
            .BuildServiceProvider();
        List<IGitRemoteTokenSource> handed = [];

        DaemonGitRemoteAuthentication.Configure(services, handed.Add);
        (await client.TokenAsync("hallmanac", "/repos/hall9k", CancellationToken.None)).Should().Be(
            Token, "the shared client the source reads through now holds the token in memory");

        handed.Should().ContainSingle().Which.Should().BeOfType<ProjectGitRemoteTokenSource>();
        string basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{Token}"));
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = (string)entry.Key;
            string value = entry.Value as string ?? string.Empty;
            name.Should().NotContainEquivalentOf("extraHeader");
            value.Should().NotContainEquivalentOf("extraHeader", because: $"{name} names no header key");
            value.Should().NotContain(Token).And.NotContain(basic);
        }
    }
}
