using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.TestSupport;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k doctor</c> tells a human about the Linear connection of a project bound to a team:
/// no connection, a rejected key, and a probe that could not confirm either are three different
/// things to do, and none of them may throw out of the probe and stop the rest of the doctor run.
/// Responses are fixtures written from Linear's public schema; none reaches a live workspace.
/// </summary>
public sealed class LinearDoctorTests : IDisposable
{
    private const string KeyFileName = "linear-doctor-test-key";
    private static readonly Uri Workspace = new("https://linear.app/hall9k");

    private readonly ScopedTestHome _home = new();
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(1));

    private CancellationToken Token => _cancellation.Token;

    public LinearDoctorTests()
    {
        Directory.CreateDirectory(CredentialVault.Directory);
        File.WriteAllText(CredentialVault.FileFor(KeyFileName), "lin_api_doctor_test_key");
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _home.Dispose();
    }

    private static Func<Task<LinearWorkItemProvider?>> Open(LinearRequester requester, string keyFile = KeyFileName) =>
        () => Task.FromResult<LinearWorkItemProvider?>(
            new LinearWorkItemProvider(
                LinearAccount.Registered(Workspace, "Brian Hall", CredentialReference.File(keyFile)), requester));

    private static LinearRequester Answer(int status, string body, string? retryAfter = null) =>
        (_, _) => Task.FromResult(new LinearResponse(status, body, retryAfter));

    [Fact]
    public async Task An_accepted_key_is_reported_as_accepted()
    {
        LinearProbe probe = await LinearDoctor.ProbeAsync(
            Open(Answer(200, """{"data":{"viewer":{"id":"u1","name":"Brian"},"organization":{"urlKey":"hall9k"}}}""")), Token);

        probe.Outcome.Should().Be(LinearProbeOutcome.Accepted);
    }

    [Fact]
    public async Task No_connection_is_told_apart_from_a_rejected_key()
    {
        LinearProbe probe = await LinearDoctor.ProbeAsync(() => Task.FromResult<LinearWorkItemProvider?>(null), Token);

        probe.Outcome.Should().Be(LinearProbeOutcome.NoConnection);
    }

    [Fact]
    public async Task A_key_Linear_rejects_is_a_rejection_with_the_reason_quoted()
    {
        LinearProbe probe = await LinearDoctor.ProbeAsync(
            Open(Answer(401, """{"errors":[{"message":"Authentication required","extensions":{"code":"AUTHENTICATION_ERROR"}}]}""")), Token);

        probe.Outcome.Should().Be(LinearProbeOutcome.Rejected);
        probe.Detail.Should().Contain("rejected the API key");
    }

    [Fact]
    public async Task A_credential_that_cannot_be_read_is_unconfirmed_because_Linear_was_never_asked()
    {
        LinearProbe probe = await LinearDoctor.ProbeAsync(
            Open(Answer(200, "{}"), keyFile: "a-key-file-nobody-wrote"), Token);

        probe.Outcome.Should().Be(LinearProbeOutcome.Unconfirmed);
        probe.Detail.Should().Contain("h9k connection add linear");
    }

    [Theory]
    [InlineData(429, "{}")]
    [InlineData(503, "service unavailable")]
    [InlineData(200, "<html>sign in to the guest wifi</html>")]
    [InlineData(200, """{"data":{"viewer":null}}""")]
    public async Task A_probe_that_could_not_confirm_either_is_neither_accepted_nor_rejected(int status, string body)
    {
        LinearProbe probe = await LinearDoctor.ProbeAsync(Open(Answer(status, body, retryAfter: "10")), Token);

        probe.Outcome.Should().Be(LinearProbeOutcome.Unconfirmed);
    }

    [Fact]
    public async Task A_network_failure_is_unconfirmed()
    {
        LinearProbe probe = await LinearDoctor.ProbeAsync(
            Open((_, _) => throw new HttpRequestException("connection refused")), Token);

        probe.Outcome.Should().Be(LinearProbeOutcome.Unconfirmed);
    }

    [Fact]
    public async Task Two_connections_left_by_overlapping_runs_do_not_abort_the_doctor_run()
    {
        LinearProbe probe = await LinearDoctor.ProbeAsync(
            () => throw new DomainConflictException("2 Linear connections are registered and nothing says which one to use"), Token);

        probe.Outcome.Should().Be(LinearProbeOutcome.Unconfirmed);
        probe.Detail.Should().Contain("2 Linear connections");
    }

    private static ProjectDetails Project(string name, BranchNameTemplate template) => new()
    {
        Id = DomainId.New(),
        Name = name,
        BranchNameTemplate = template,
    };

    [Theory]
    [InlineData("task/{shortid}-{slug}")]
    [InlineData("feature/{slug}")]
    public void A_Linear_project_whose_branch_template_has_no_key_is_warned_with_the_command_that_fixes_it(string template)
    {
        IReadOnlyList<string> warnings = LinearDoctor.BranchTemplateWarnings(
            [Project("hall9k", BranchNameTemplate.Parse(template))]);

        string warning = warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("hall9k").And.Contain(template).And.Contain("no {key}")
            .And.Contain("h9k project set hall9k --branch-template \"{key}-{slug}\"");
        Action render = () => _ = new Markup(warning);
        render.Should().NotThrow("the warning is printed as console markup");
    }

    [Fact]
    public void The_default_template_is_warned_about_too_which_covers_a_template_cleared_after_the_bind()
    {
        // --branch-template none stores the default, and the warning given at bind time cannot see a
        // change made after it; the doctor reads the project as it stands.
        LinearDoctor.BranchTemplateWarnings([Project("hall9k", BranchNameTemplate.Default)]).Should().ContainSingle();
    }

    [Fact]
    public void A_template_that_carries_the_key_is_not_warned_about_and_each_project_is_judged_on_its_own()
    {
        IReadOnlyList<string> warnings = LinearDoctor.BranchTemplateWarnings(
        [
            Project("keyed", BranchNameTemplate.Parse("{key}-{slug}")),
            Project("unkeyed", BranchNameTemplate.Default),
            Project("prefixed", BranchNameTemplate.Parse("feature/{key}-{slug}")),
        ]);

        warnings.Should().ContainSingle().Which.Should().Contain("unkeyed");
    }
}
