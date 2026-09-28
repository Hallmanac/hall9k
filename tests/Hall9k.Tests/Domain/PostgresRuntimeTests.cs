using FluentAssertions;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="PostgresRuntime.WriteComposeFileAsync(CancellationToken)"/>'s own load-bearing
/// behaviour: it keeps whatever password is already in effect rather than resetting it on every
/// rewrite, since <c>POSTGRES_PASSWORD</c> only applies at <c>initdb</c> and a freshly generated
/// one would silently orphan whatever the running container's data volume actually holds
/// (conformance and adversarial pre-PR review, cycle 1). Nothing exercised this before this class:
/// every other test touching this method wrote the compose file from a fresh, empty home.
/// </summary>
public sealed class PostgresRuntimeTests : IDisposable
{
    private readonly ScopedTestHome scopedHome = new();

    public void Dispose() => scopedHome.Dispose();

    [Fact]
    public async Task Rewriting_the_compose_file_keeps_its_own_already_quoted_password()
    {
        string first = await PostgresRuntime.WriteComposeFileAsync(CancellationToken.None);

        string second = await PostgresRuntime.WriteComposeFileAsync(CancellationToken.None);

        second.Should().Be(first, "a rewrite must never orphan the password the running container's initdb already baked in");
        PostgresRuntime.ReadPasswordFromComposeFile().Should().Be(first);
    }

    [Fact]
    public async Task Rewriting_the_compose_file_keeps_a_pre_migration_bare_unquoted_password()
    {
        Directory.CreateDirectory(PostgresRuntime.ComposeDirectory);
        await File.WriteAllTextAsync(PostgresRuntime.ComposeFile, """
            services:
              postgres:
                image: postgres:18
                environment:
                  POSTGRES_PASSWORD: hall9k
            """);

        string password = await PostgresRuntime.WriteComposeFileAsync(CancellationToken.None);

        password.Should().Be("hall9k", "the shipped constant on a compose file no run has rewritten yet is the real, already-in-effect password");
        PostgresRuntime.ReadPasswordFromComposeFile().Should().Be("hall9k");
    }

    [Fact]
    public async Task A_missing_compose_file_reuses_the_password_already_recorded_for_the_installed_container_in_config_json()
    {
        await Hall9kDatabase.WriteConfiguredConnectionStringAsync(
            Hall9kDatabase.ConnectionStringWithPassword("survivingpassword"), CancellationToken.None);

        string password = await PostgresRuntime.WriteComposeFileAsync(CancellationToken.None);

        password.Should().Be(
            "survivingpassword",
            "h9k uninstall without --purge-data deletes the compose file but keeps config.json and the "
            + "already-initialized data volume, so a reinstall must not generate a password the running "
            + "container never adopted");
        PostgresRuntime.ReadPasswordFromComposeFile().Should().Be("survivingpassword");
    }

    [Fact]
    public async Task A_missing_compose_file_with_no_usable_record_anywhere_generates_a_fresh_password()
    {
        string password = await PostgresRuntime.WriteComposeFileAsync(CancellationToken.None);

        password.Should().MatchRegex("^[0-9a-f]{64}$", "32 CSPRNG bytes rendered as lowercase hex, a genuinely fresh machine");
    }
}
