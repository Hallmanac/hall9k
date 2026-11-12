using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The token header against real git and real sockets: a local server stands in for github.com
/// (the keys take whatever URL scope the authorization names, and production's is always
/// https://github.com/). What these pin is what only git can answer: that a redirect never carries
/// the header to the second server, that the retry after it succeeds, and that an authentication
/// failure re-reads the token once. Every test starts real git processes and listens on real local
/// sockets, so the class sits in the same fence as the other tests that spawn processes.
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
public sealed class GitRemoteAuthenticationLocalServerTests : IDisposable
{
    private const string Token = "ghp_REDIRECTPROBE0123456789";

    private readonly string workDirectory = Path.Combine(Path.GetTempPath(), $"h9k-remote-auth-{Guid.NewGuid():N}");

    public GitRemoteAuthenticationLocalServerTests() => Directory.CreateDirectory(workDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static string Basic(string token) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{token}"));

    [Fact]
    public async Task A_redirect_never_carries_the_token_to_the_second_server_and_the_retry_without_it_succeeds()
    {
        using RecordingServer second = RecordingServer.Advertising();
        using RecordingServer first = RecordingServer.RedirectingTo(second);
        string repository = await InitRepositoryAsync(first);
        TokenSequence source = new(Token);
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source with { ScopeUrl = first.BaseUrl });
        Dictionary<string, string> gitEnvironmentBefore = GitEnvironment();

        ProcessResult result = await ExternalProcess.Runner(
            "git", ["ls-remote", "origin"], repository, CancellationToken.None);

        result.ExitCode.Should().Be(0, result.StandardError);
        first.Requests.Where(r => r.Path.Contains("info/refs")).Select(r => r.Authorization).Should().Equal(
            [$"Basic {Basic(Token)}", null],
            "the call went out with the header once, then once more without it after failing on the redirect");
        second.Requests.Should().NotBeEmpty("the retry followed the redirect the way git did before the keys existed");
        second.Requests.Should().OnlyContain(
            request => request.Authorization == null, "the second server never sees the token");
        source.Invalidations.Should().Be(0, "a redirect is not a rejected token");
        // This is the test host's environment around one call. Every variable the call sets or
        // removes on its child is named GIT_, so the call must leave the host's GIT_ variables as it
        // found them, and no entry may name extraHeader or carry the token. Other variables are left
        // out because tests in other collections change them in parallel. The daemon's own startup
        // is pinned by DaemonGitRemoteAuthenticationTests.
        GitEnvironment().Should().Equal(
            gitEnvironmentBefore, "the call sets its git keys on the child, never on this process");
        foreach (KeyValuePair<string, string> entry in ProcessEnvironment())
        {
            string text = $"{entry.Key}={entry.Value}";
            text.Should().NotContain("extraHeader", because: "no key naming it is ever set here");
            text.Should().NotContain(Token).And.NotContain(Basic(Token));
        }

        result.StandardOutput.Should().NotContain(Token);
        result.StandardError.Should().NotContain(Token).And.NotContain(Basic(Token));
    }

    [Fact]
    public async Task A_rejected_token_is_read_again_once_and_the_retry_with_the_new_one_succeeds()
    {
        using RecordingServer server = RecordingServer.Accepting($"Basic {Basic("fresh-token")}");
        string repository = await InitRepositoryAsync(server);
        TokenSequence source = new("stale-token", "fresh-token");
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source with { ScopeUrl = server.BaseUrl });

        ProcessResult result = await ExternalProcess.Runner(
            "git", ["ls-remote", "origin"], repository, CancellationToken.None);

        result.ExitCode.Should().Be(0, result.StandardError);
        server.Requests.Where(r => r.Path.Contains("info/refs")).Select(r => r.Authorization).Should().Equal(
            $"Basic {Basic("stale-token")}", $"Basic {Basic("fresh-token")}");
        source.Invalidations.Should().Be(1);
    }

    [Fact]
    public async Task A_token_that_is_rejected_twice_ends_in_one_run_without_the_keys_and_that_failure_is_reported()
    {
        using RecordingServer server = RecordingServer.Accepting("Basic never");
        string repository = await InitRepositoryAsync(server);
        TokenSequence source = new("stale-token", "also-bad");
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(source with { ScopeUrl = server.BaseUrl });

        ProcessResult result = await ExternalProcess.Runner(
            "git", ["ls-remote", "origin"], repository, CancellationToken.None);

        result.ExitCode.Should().NotBe(0);
        server.Requests.Where(r => r.Path.Contains("info/refs")).Select(r => r.Authorization).Should().Equal(
            [$"Basic {Basic("stale-token")}", $"Basic {Basic("also-bad")}", null]);
        source.Invalidations.Should().Be(1);
        result.StandardError.Should().NotContain("stale-token").And.NotContain("also-bad");
    }

    [Fact]
    public async Task The_ledgers_own_ls_remote_carries_the_header_too()
    {
        using RecordingServer server = RecordingServer.Accepting($"Basic {Basic(Token)}");
        string repository = await InitRepositoryAsync(server);
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(
            new TokenSequence(Token) with { ScopeUrl = server.BaseUrl });

        IReadOnlyList<LedgerRef> refs = await new GitLedger(NullLogger<GitLedger>.Instance).ListRefsAsync(
            repository, LedgerRefRegistry.MessagesPrefix.RefspecSource, CancellationToken.None);

        refs.Should().BeEmpty();
        server.Requests.Where(r => r.Path.Contains("info/refs")).Select(r => r.Authorization)
            .Should().Equal($"Basic {Basic(Token)}");
    }

    [Fact]
    public async Task A_remote_the_source_has_no_token_for_runs_untouched()
    {
        using RecordingServer second = RecordingServer.Advertising();
        using RecordingServer first = RecordingServer.RedirectingTo(second);
        string repository = await InitRepositoryAsync(first);
        using IDisposable scope = GitRemoteAuthentication.UseForCurrentFlow(new TokenSequence { ScopeUrl = first.BaseUrl });

        ProcessResult result = await ExternalProcess.Runner(
            "git", ["ls-remote", "origin"], repository, CancellationToken.None);

        result.ExitCode.Should().Be(0, result.StandardError);
        first.Requests.Should().OnlyContain(request => request.Authorization == null);
    }

    private async Task<string> InitRepositoryAsync(RecordingServer origin)
    {
        string repository = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        foreach (string[] arguments in new[]
        {
            new[] { "init", "-q" },
            ["remote", "add", "origin", $"{origin.BaseUrl}r.git"],
        })
        {
            ProcessResult step = await ExternalProcess.Runner("git", arguments, repository, CancellationToken.None);
            step.ExitCode.Should().Be(0, step.StandardError);
        }

        return repository;
    }

    private static Dictionary<string, string> ProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty);

    private static Dictionary<string, string> GitEnvironment() =>
        ProcessEnvironment()
            .Where(entry => entry.Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary();

    /// <summary>
    /// Hands out the given tokens in order, then none. Carries the URL scope as a property so a
    /// test can point the keys at its own server; <c>with</c> copies the queue state by reference,
    /// so the original's counters stay the ones the test reads.
    /// </summary>
    private sealed record TokenSequence : IGitRemoteTokenSource
    {
        private readonly Queue<string> tokens;
        private readonly int[] invalidations = [0];

        public TokenSequence(params string[] tokens) => this.tokens = new Queue<string>(tokens);

        public string? ScopeUrl { get; init; }

        public int Invalidations => invalidations[0];

        public Task<GitRemoteAuthorization?> TryGetAsync(string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(tokens.TryDequeue(out string? token)
                ? new GitRemoteAuthorization(token, ScopeUrl)
                : null);

        public void Invalidate(string workingDirectory) => invalidations[0]++;

        public void RecordRedirect(string workingDirectory)
        {
        }
    }

    private sealed record ObservedRequest(string Method, string Path, string? Authorization);

    /// <summary>
    /// A just-enough HTTP server for <c>git ls-remote</c> over protocol v2: it either answers with
    /// the capability advertisement and an empty ref list, redirects every request, or demands one
    /// exact Authorization value. Every request's Authorization header is recorded.
    /// </summary>
    private sealed class RecordingServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Func<ObservedRequest, (int Status, string? Location)> decide;
        private readonly Task accepting;
        private readonly object gate = new();
        private readonly List<ObservedRequest> requests = [];

        private RecordingServer(Func<ObservedRequest, (int Status, string? Location)> decide)
        {
            this.decide = decide;
            listener.Start();
            accepting = Task.Run(AcceptAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

        public IReadOnlyList<ObservedRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return [.. requests];
                }
            }
        }

        public static RecordingServer Advertising() => new(_ => (200, null));

        public static RecordingServer RedirectingTo(RecordingServer target) =>
            new(request => (301, $"{target.BaseUrl.TrimEnd('/')}{request.Path}"));

        public static RecordingServer Accepting(string authorization) =>
            new(request => request.Authorization == authorization ? (200, null) : (403, null));

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            try
            {
                accepting.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The accept loop ends by the listener closing under it.
            }

            stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    (string Method, string Path, string? Authorization, int ContentLength) head = await ReadHeadAsync(stream);
                    byte[] body = new byte[head.ContentLength];
                    for (int read = 0; read < body.Length;)
                    {
                        int count = await stream.ReadAsync(body.AsMemory(read), stop.Token);
                        if (count == 0)
                        {
                            break;
                        }

                        read += count;
                    }

                    ObservedRequest request = new(head.Method, head.Path, head.Authorization);
                    lock (gate)
                    {
                        requests.Add(request);
                    }

                    (int status, string? location) = decide(request);
                    await WriteResponseAsync(stream, request, status, location);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // A client that hung up mid-request; nothing to answer.
                }
            }
        }

        private async Task<(string Method, string Path, string? Authorization, int ContentLength)> ReadHeadAsync(NetworkStream stream)
        {
            StringBuilder text = new();
            byte[] one = new byte[1];
            while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, stop.Token) == 0)
                {
                    break;
                }

                text.Append((char)one[0]);
            }

            string[] lines = text.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string[] start = lines[0].Split(' ');
            string? authorization = null;
            int contentLength = 0;
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                string name = line[..colon];
                string value = line[(colon + 1)..].Trim();
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = value;
                }
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(value);
                }
            }

            return (start[0], start[1], authorization, contentLength);
        }

        private static string Pkt(string text) => $"{(text.Length + 4):x4}{text}";

        private async Task WriteResponseAsync(NetworkStream stream, ObservedRequest request, int status, string? location)
        {
            string contentType = "text/plain";
            string body = string.Empty;
            if (status == 200)
            {
                if (request.Method == "GET")
                {
                    contentType = "application/x-git-upload-pack-advertisement";
                    body = Pkt("version 2\n") + Pkt("agent=git/probe\n") + Pkt("ls-refs\n") + Pkt("fetch=shallow\n") + "0000";
                }
                else
                {
                    contentType = "application/x-git-upload-pack-result";
                    body = "0000";
                }
            }

            string reason = status switch { 200 => "OK", 301 => "Moved Permanently", _ => "Forbidden" };
            StringBuilder head = new();
            head.Append($"HTTP/1.1 {status} {reason}\r\n");
            if (location is not null)
            {
                head.Append($"Location: {location}\r\n");
            }

            // A refusal is a 403 with no WWW-Authenticate challenge, never a 401: a 401 makes git ask
            // the machine's own credential helper for a login, which on an owner's machine can open
            // a window and wait for a person.
            head.Append($"Content-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
            byte[] bytes = Encoding.ASCII.GetBytes(head + body);
            await stream.WriteAsync(bytes, stop.Token);
            await stream.FlushAsync(stop.Token);
        }
    }
}
