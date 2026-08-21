using System.Globalization;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The seam itself: routing by source, and the adoption policy that has to hold for every source
/// rather than for whichever one remembered it. Backlog 18 adds a Jira provider to this list and
/// inherits the open-work gate for free — which is the only reason the policy lives in the
/// importer instead of in <see cref="GitHubWorkItemProvider"/>.
/// </summary>
public sealed class WorkItemImporterTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_open_item_comes_back_exactly_as_the_source_reported_it()
    {
        WorkItemImporter importer = new(new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Open));

        ImportedWorkItem imported = await Import(importer, WorkItemProvider.GitHub);

        imported.Status.Should().Be(WorkItemStatus.Open);
        imported.Title.Should().Be("Stub item");
    }

    [Fact]
    public async Task A_closed_item_is_refused_and_the_refusal_dates_its_own_observation()
    {
        WorkItemImporter importer = new(new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Closed));

        Func<Task> import = () => Import(importer, WorkItemProvider.GitHub);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("was not open when Hall9k read it")
            .And.Contain("'closed'", "the refusal quotes the state the source actually reported")
            .And.Contain("2026-08-21 09:30:00Z", "the platform reports what it saw and when, not what is true now")
            .And.Contain("h9k task add --project", "the refusal names the way forward");
    }

    /// <summary>
    /// The stamp is not a display detail. It is written into the refusal a human reads, into the
    /// agent context a task stores, and into the event stream, where it outlives the machine that
    /// formatted it. Formatted with the ambient culture it would carry that machine's locale
    /// permanently: fi-FI and da-DK separate a time with a full stop, so the same import run in
    /// Helsinki would record '09.30.00Z' and read as a different observation everywhere else.
    /// </summary>
    [Theory]
    [InlineData("fi-FI")]
    [InlineData("da-DK")]
    [InlineData("en-US")]
    public async Task The_observation_is_stamped_the_same_way_whatever_locale_the_machine_runs_in(
        string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            WorkItemImporter importer = new(new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Closed));

            Func<Task> import = () => Import(importer, WorkItemProvider.GitHub);

            (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
                .Should().Contain("2026-08-21 09:30:00Z");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The gate is positively open, not merely not-closed. A source that said nothing, and a
    /// source whose own vocabulary the adapter left untranslated, are both states Hall9k never
    /// observed as open — and adopting either would be the guess the never-guess rule forbids
    /// (AGENTS.md), at the one door where a fabricated "it's open" turns into dispatched work.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("merged")]
    [InlineData("in progress")]
    public async Task An_item_never_observed_open_is_refused_with_the_state_that_was_observed(string state)
    {
        WorkItemImporter importer = new(
            new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Parse(state)));

        Func<Task> import = () => Import(importer, WorkItemProvider.GitHub);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("was not open when Hall9k read it")
            .And.Contain($"'{WorkItemStatus.Parse(state)}'", "an unadoptable state is reported, never relabelled");
    }

    [Fact]
    public async Task An_unrecognised_state_is_quoted_in_the_words_the_source_used()
    {
        // The refusal is an audit line about a moment: it says what was read. Case-folding
        // "In Review" to "in review" is a small edit to that record, and a record nobody may
        // edit is the whole reason the observed value rides through unrecognised.
        WorkItemImporter importer = new(
            new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Parse("  In Review  ")));

        Func<Task> import = () => Import(importer, WorkItemProvider.GitHub);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("'In Review'");
    }

    [Fact]
    public async Task The_refusal_quotes_the_source_without_letting_it_act_on_the_terminal()
    {
        // WorkItemStatus keeps a state it had no rule for exactly as it was reported, and this
        // refusal is printed straight to stderr with no sanitiser between. So a source that can
        // put an escape sequence in a status can repaint the very refusal that is quoting it.
        WorkItemImporter importer = new(
            new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Parse("in \u001b[2Jreview\r approved")));

        Func<Task> import = () => Import(importer, WorkItemProvider.GitHub);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().NotContain("\u001b").And.NotContain("\r")
            .And.Contain("'in [2Jreview approved'", "the words are still the source's own");
    }

    [Fact]
    public async Task An_unknown_source_names_the_sources_that_do_exist()
    {
        WorkItemImporter importer = new(new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Open));

        Func<Task> import = () => Import(importer, WorkItemProvider.Jira);

        (await import.Should().ThrowAsync<DomainValidationException>()).Which.Message
            .Should().Contain("no importer for 'jira'")
            .And.Contain("github");
    }

    /// <summary>
    /// A source this install could speak but has not connected is a different sentence from one
    /// Hall9k has never heard of, and only one of them has a remedy. h9k task add --from-jira on a
    /// machine that has not run h9k connection add jira is the likeliest first-run ordering there
    /// is, and the CLI standard is that a refusal lets an agent self-correct from it (AGENTS.md),
    /// which a list of the sources that happen to be configured does not.
    /// </summary>
    [Fact]
    public async Task A_source_this_install_has_not_connected_is_refused_with_the_command_that_connects_it()
    {
        WorkItemImporter importer = new(new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Open))
        {
            Unregistered = new Dictionary<WorkItemProvider, string>
            {
                [WorkItemProvider.Jira] = WorkItemConnections.NoJiraConnection,
            },
        };

        Func<Task> import = () => Import(importer, WorkItemProvider.Jira);

        (await import.Should().ThrowAsync<DomainNotFoundException>()).Which.Message
            .Should().Contain("No Jira connection is registered")
            .And.Contain("h9k connection add jira --site")
            .And.NotContain("Known sources", "the sources that are configured are not the answer here");
    }

    [Fact]
    public void A_reference_from_an_unregistered_source_has_no_url_rather_than_a_guessed_one()
    {
        WorkItemImporter importer = new(new StubProvider(WorkItemProvider.GitHub, WorkItemStatus.Open));

        importer.WebUrl("jira:PROJ-123").Should().BeNull();
        importer.WebUrl((string?)null).Should().BeNull();
    }

    [Fact]
    public void The_default_importer_places_a_github_reference()
    {
        WorkItemImporter.Default.WebUrl("github:Hallmanac/hall9k#42")
            .Should().Be(new Uri("https://github.com/Hallmanac/hall9k/issues/42"));
    }

    private static async Task<ImportedWorkItem> Import(WorkItemImporter importer, WorkItemProvider provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        return await importer.ImportAsync(
            new WorkItemImportRequest(provider, "42", "/repos/hall9k"), cts.Token);
    }

    private sealed class StubProvider(WorkItemProvider provider, WorkItemStatus status) : IWorkItemProvider
    {
        public WorkItemProvider Provider => provider;

        public Task<ImportedWorkItem> ImportAsync(
            WorkItemImportRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ImportedWorkItem(
                new ExternalReference(provider, "owner/repo#42"),
                "Stub item",
                "Stub body",
                status,
                new Uri("https://example.test/42"),
                ObservedAt));

        public Uri? WebUrl(ExternalReference reference) => null;
    }
}
