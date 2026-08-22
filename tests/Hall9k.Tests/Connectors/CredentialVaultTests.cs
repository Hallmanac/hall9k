using FluentAssertions;
using Hall9k.Connectors.Credentials;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The one place a secret is read back from where a <see cref="CredentialReference"/> says it
/// lives (PLAN.md §10). What is tested here is mostly the refusals, because the failure this
/// type exists to prevent is the quiet one: a reference that resolves to something other than
/// what it names.
/// </summary>
[Collection("Hall9kHome")]
public sealed class CredentialVaultTests : IDisposable
{
    private const string Variable = "HALL9K_TEST_VAULT_TOKEN";

    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");

    private CancellationToken Token => _cancellation.Token;

    public CredentialVaultTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        Environment.SetEnvironmentVariable(Variable, null);
        _cancellation.Dispose();
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task A_stored_secret_comes_back_through_the_reference_that_names_it()
    {
        CredentialReference reference = await CredentialVault.StoreAsync("jira-test", "the-token", Token);

        reference.ToString().Should().Be("file:jira-test", "the reference names the file, never the secret");
        (await CredentialVault.Default.ResolveAsync(reference, "read Jira", Token)).Should().Be("the-token");
    }

    [Fact]
    public async Task A_stored_secret_is_readable_by_its_owner_alone()
    {
        await CredentialVault.StoreAsync("jira-perms", "the-token", Token);

        // On Windows there is no mode to set: the file inherits the user profile's own access
        // control, which is what the registration command says out loud rather than implying. The
        // assertion is skipped there rather than the whole test, so the write itself is still
        // exercised on both legs of CI.
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(CredentialVault.FileFor("jira-perms"))
                .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Exists(CredentialVault.FileFor("jira-perms")).Should().BeTrue();
    }

    /// <summary>
    /// The counterpart to storing one. Rotating a stored token into an environment variable or a
    /// keychain item leaves the file behind holding a working secret, and the reference the
    /// connection now carries names somewhere else entirely — so there has to be a way to take it
    /// off disk, and it has to be narrow enough that it can only ever remove a file this vault
    /// wrote itself.
    /// </summary>
    [Fact]
    public async Task A_stored_secret_can_be_discarded_once_nothing_points_at_it()
    {
        CredentialReference reference = await CredentialVault.StoreAsync("jira-rotated", "the-token", Token);
        CredentialVault.Holds(reference).Should().BeTrue();

        CredentialVault.Discard(reference).Should().Be(CredentialVault.FileFor("jira-rotated"));

        CredentialVault.Holds(reference).Should().BeFalse();
        File.Exists(CredentialVault.FileFor("jira-rotated")).Should().BeFalse();
    }

    /// <summary>
    /// The names this platform derives are slugs and could not do this, so the guard is for the
    /// call site that has not been written yet and for the identifier that arrives off the event
    /// stream. A vault that composed a path out of whatever it was handed would write a token
    /// outside the owner's credentials directory on one side and resolve a reference to a file it
    /// does not name on the other. Origin: the pull-request review of the Jira branch (2026-08-22)
    /// read StoreAsync and asked what stopped a name from carrying a separator.
    /// </summary>
    [Fact]
    public async Task A_credential_name_that_would_leave_the_directory_is_refused_rather_than_composed()
    {
        foreach (string escape in new[] { "..", "../outside", "nested/jira", ".", string.Empty })
        {
            Func<Task> store = () => CredentialVault.StoreAsync(escape, "the-token", Token);
            (await store.Should().ThrowAsync<DomainValidationException>()).Which
                .Message.Should().NotContain("the-token", "a refusal never quotes the secret");

            Func<Task> resolve = () => CredentialVault.Default
                .ResolveAsync(CredentialReference.File(escape), "read Jira", Token).AsTask();
            await resolve.Should().ThrowAsync<DomainValidationException>();

            CredentialVault.Holds(CredentialReference.File(escape)).Should()
                .BeFalse("asking whether a name is stored is not a way around the rule");
        }

        Directory.Exists(CredentialVault.Directory).Should()
            .BeFalse("nothing was written, so nothing needed a directory");
    }

    [Fact]
    public void Discarding_touches_nothing_it_did_not_write()
    {
        // The other kinds point at a secret somebody else put in a store of their own, and this
        // platform deleting one of those would be it reaching outside what it created. A file that
        // is already gone is not an error either: the caller asked for it to be absent.
        CredentialVault.Discard(CredentialReference.EnvironmentVariable(Variable)).Should().BeNull();
        CredentialVault.Discard(CredentialReference.Keychain("hall9k-jira")).Should().BeNull();
        CredentialVault.Discard(CredentialReference.GhCli).Should().BeNull();
        CredentialVault.Discard(CredentialReference.File("never-written")).Should().BeNull();
    }

    [Fact]
    public async Task An_environment_reference_reads_the_variable_and_never_copies_it()
    {
        Environment.SetEnvironmentVariable(Variable, "from-the-environment");

        string secret = await CredentialVault.Default.ResolveAsync(
            CredentialReference.EnvironmentVariable(Variable), "read Jira", Token);

        secret.Should().Be("from-the-environment");
        Directory.Exists(CredentialVault.Directory).Should().BeFalse("nothing was stored, only pointed at");
    }

    [Fact]
    public async Task An_unset_variable_names_the_variable_and_the_daemons_own_catch()
    {
        Func<Task> resolve = () => CredentialVault.Default
            .ResolveAsync(CredentialReference.EnvironmentVariable(Variable), "read Jira", Token).AsTask();

        (await resolve.Should().ThrowAsync<DomainValidationException>())
            .WithMessage($"*{Variable}*")
            .WithMessage("*h9kd inherits the environment it was started from*");
    }

    [Fact]
    public async Task A_missing_credential_file_says_where_it_looked_and_how_to_put_it_back()
    {
        Func<Task> resolve = () => CredentialVault.Default
            .ResolveAsync(CredentialReference.File("jira-gone"), "read Jira", Token).AsTask();

        (await resolve.Should().ThrowAsync<DomainNotFoundException>())
            .WithMessage("*jira-gone*")
            .WithMessage("*h9k connection add jira*");
    }

    [Fact]
    public async Task The_gh_reference_is_refused_rather_than_treated_as_a_token()
    {
        // gh-cli points at a login the gh tool holds, which is not a secret Hall9k can read. A
        // fallback here would resolve a reference to something other than what it says.
        Func<Task> resolve = () => CredentialVault.Default
            .ResolveAsync(CredentialReference.GhCli, "read Jira", Token).AsTask();

        (await resolve.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*gh CLI's own login*");
    }

    /// <summary>
    /// The keychain branch asserts the two answers a keychain reference can honestly have, and
    /// which one is right is decided by the platform rather than by the test: on macOS the
    /// 'security' tool is asked, and anywhere else the reference names a store this code cannot
    /// open and is refused rather than approximated by reading the file store instead.
    /// </summary>
    [Fact]
    public async Task A_keychain_reference_reads_the_keychain_on_macOS_and_is_refused_anywhere_else()
    {
        RecordingProcessRunner security = RecordingProcessRunner.Succeeding("from-the-keychain\n");
        CredentialVault vault = new(security.Runner);
        CredentialReference reference = CredentialReference.Keychain("hall9k-jira");

        if (OperatingSystem.IsMacOS())
        {
            (await vault.ResolveAsync(reference, "read Jira", Token)).Should().Be("from-the-keychain");
            security.Calls.Should().ContainSingle()
                .Which.Arguments.Should().ContainInOrder("find-generic-password", "-s", "hall9k-jira", "-w");
            return;
        }

        Func<Task> resolve = () => vault.ResolveAsync(reference, "read Jira", Token).AsTask();

        (await resolve.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*--token-env*");
        security.Calls.Should().BeEmpty("a store this platform does not have is never asked");
    }

    [Fact]
    public async Task A_keychain_item_that_is_not_there_names_the_command_that_adds_it()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        RecordingProcessRunner security = RecordingProcessRunner.Failing("The specified item could not be found");
        CredentialVault vault = new(security.Runner);

        Func<Task> resolve = () => vault
            .ResolveAsync(CredentialReference.Keychain("hall9k-jira"), "read Jira", Token).AsTask();

        (await resolve.Should().ThrowAsync<DomainNotFoundException>())
            .WithMessage("*security add-generic-password*");
    }
}
