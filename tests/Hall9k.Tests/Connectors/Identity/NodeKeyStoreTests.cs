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

    [Fact]
    public async Task EnsureAsync_with_the_public_half_missing_derives_it_rather_than_regenerating_the_private_key()
    {
        Guid nodeId = DomainId.New();
        NodeKeyStore store = new();
        NodeSigningKey original = await store.EnsureAsync(nodeId, CancellationToken.None);
        string privateKeyText = await File.ReadAllTextAsync(original.PrivateKeyPath);

        File.Delete($"{original.PrivateKeyPath}.pub");

        NodeSigningKey recovered = await store.EnsureAsync(nodeId, CancellationToken.None);

        // The private key on disk is byte-for-byte unchanged — a regenerated key would have a
        // different private half, and the recovered public key would then no longer match what
        // ProjectJoinCommand already recorded on the Node stream (independent pre-PR review,
        // cycle 1, conformance and adversarial lenses, medium).
        (await File.ReadAllTextAsync(original.PrivateKeyPath)).Should().Be(privateKeyText);
        recovered.PublicKeyLine.Should().Be(original.PublicKeyLine);
        recovered.Fingerprint.Should().Be(original.Fingerprint);
    }

    [Fact]
    public async Task EnsureAsync_with_a_stale_public_key_file_corrects_it_from_the_private_key()
    {
        Guid nodeId = DomainId.New();
        NodeKeyStore store = new();
        NodeSigningKey original = await store.EnsureAsync(nodeId, CancellationToken.None);

        // A stale or tampered .pub — never what the private half actually derives to — must
        // never be trusted and registered as-is: the ledger would then advertise a public key
        // that git's own signature (made with the real private key) can never verify against.
        await File.WriteAllTextAsync($"{original.PrivateKeyPath}.pub", "ssh-ed25519 AAAAstaleAAAA== stale-comment\n");

        NodeSigningKey corrected = await store.EnsureAsync(nodeId, CancellationToken.None);

        corrected.PublicKeyLine.Should().Be(original.PublicKeyLine);
        corrected.Fingerprint.Should().Be(original.Fingerprint);
        (await File.ReadAllTextAsync($"{original.PrivateKeyPath}.pub")).Should().Be($"{original.PublicKeyLine}\n");
    }

    [Fact]
    public async Task EnsureAsync_called_concurrently_for_the_same_node_never_races_ssh_keygen()
    {
        Guid nodeId = DomainId.New();
        NodeKeyStore store = new();

        // Two h9k processes reaching the same node id at once (h9k project add's own join
        // alongside a second, manually invoked h9k project join) must serialize rather than both
        // finding no private key and racing ssh-keygen against the same output file.
        NodeSigningKey[] results = await Task.WhenAll(
            store.EnsureAsync(nodeId, CancellationToken.None),
            store.EnsureAsync(nodeId, CancellationToken.None));

        results[0].Should().Be(results[1]);
    }
}
