using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Connectors.Identity;

/// <summary>
/// <see cref="NodeKeyStore"/> against a real ssh-keygen (the same dependency GitLedgerTests
/// already has) and a throwaway <c>HALL9K_HOME</c>, never this machine's real
/// <c>~/.hall9k/keys</c>.
/// </summary>
[Collection("Hall9kHome")]
public sealed class NodeKeyStoreTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-node-key-store-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public NodeKeyStoreTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void Fingerprint_is_the_lowercase_hex_sha256_of_the_decoded_key_blob()
    {
        // A real ed25519 public key line's second field is base64; the fingerprint is just
        // SHA-256 of the bytes that decode to, independent of any real key generation.
        byte[] blob = [1, 2, 3, 4];
        string publicKeyLine = $"ssh-ed25519 {Convert.ToBase64String(blob)} comment";

        string fingerprint = NodeKeyStore.Fingerprint(publicKeyLine);

        fingerprint.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blob)).ToLowerInvariant());
        fingerprint.Should().MatchRegex("^[0-9a-f]{64}$", "a git ref name and a filesystem path both accept this unconditionally");
    }

    [Fact]
    public void Fingerprint_refuses_a_line_that_is_not_a_public_key()
    {
        Action act = () => NodeKeyStore.Fingerprint("not-a-key-line");

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public async Task EnsureAsync_generates_a_keypair_with_the_private_half_readable_by_this_account_alone()
    {
        Guid nodeId = DomainId.New();
        NodeKeyStore store = new();

        NodeSigningKey key = await store.EnsureAsync(nodeId, CancellationToken.None);

        File.Exists(key.PrivateKeyPath).Should().BeTrue();
        key.PublicKeyLine.Should().StartWith("ssh-ed25519 ");
        key.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(key.PrivateKeyPath);
            mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "the private half is mode 0600");
        }
    }

    [Fact]
    public async Task EnsureAsync_called_twice_never_regenerates_the_key()
    {
        Guid nodeId = DomainId.New();
        NodeKeyStore store = new();

        NodeSigningKey first = await store.EnsureAsync(nodeId, CancellationToken.None);
        NodeSigningKey second = await store.EnsureAsync(nodeId, CancellationToken.None);

        second.Should().Be(first);
    }
}
