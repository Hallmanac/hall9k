using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k project show</c>'s "Take policy" row (idea 202383dc, item 5): <c>auto</c> is both the
/// platform's untouched default and a choice an operator can explicitly re-affirm, and the row
/// must say which of the two it is seeing — the same distinction <c>ClaimGateRow</c>'s own doc
/// already applies (Decisions Log #161; independent pre-PR review, cycle 6, conformance lens, low).
/// </summary>
public sealed class ProjectTakePolicyRowTests
{
    [Fact]
    public void Auto_never_recorded_reads_as_the_untouched_default()
    {
        ProjectDetails project = Project();
        project.TakePolicy = TakePolicy.Auto;

        string row = ProjectShowCommand.TakePolicyRow(project, recorded: false);

        row.Should().Contain("nothing recorded here");
        row.Should().NotContain("explicit");
    }

    [Fact]
    public void Auto_explicitly_recorded_says_so_rather_than_reading_as_the_untouched_default()
    {
        ProjectDetails project = Project();
        project.TakePolicy = TakePolicy.Auto;

        string row = ProjectShowCommand.TakePolicyRow(project, recorded: true);

        row.Should().Contain("explicit");
        row.Should().NotContain("nothing recorded here");
    }

    [Fact]
    public void Ask_names_the_holders_own_doors_regardless_of_recorded()
    {
        ProjectDetails project = Project();
        project.TakePolicy = TakePolicy.Ask;

        string row = ProjectShowCommand.TakePolicyRow(project, recorded: true);

        row.Should().Contain("h9k task grant");
        row.Should().Contain("h9k task refuse");
    }

    private static ProjectDetails Project() => new()
    {
        Id = DomainId.New(),
        Name = "alpha",
    };
}

/// <summary>
/// <c>h9k project show</c>'s "Take timeout" row: <c>TakeTimeoutMinutes</c> is an
/// <c>Optional&lt;int?&gt;</c>, so an operator can explicitly reset it back to null, and a null
/// read must not silently collapse that into "nobody ever touched this" (class sweep off the
/// TakePolicy fix, independent pre-PR review, cycle 6, conformance lens).
/// </summary>
public sealed class ProjectTakeTimeoutRowTests
{
    [Fact]
    public void Null_never_recorded_reads_as_the_untouched_default()
    {
        ProjectDetails project = new() { Id = DomainId.New(), Name = "alpha", TakeTimeoutMinutes = null };

        string row = ProjectShowCommand.TakeTimeoutRow(project, recorded: false);

        row.Should().Contain("nothing recorded here");
        row.Should().NotContain("explicit");
    }

    [Fact]
    public void Null_explicitly_recorded_says_so_rather_than_reading_as_the_untouched_default()
    {
        ProjectDetails project = new() { Id = DomainId.New(), Name = "alpha", TakeTimeoutMinutes = null };

        string row = ProjectShowCommand.TakeTimeoutRow(project, recorded: true);

        row.Should().Contain("explicit");
        row.Should().NotContain("nothing recorded here");
    }

    [Fact]
    public void An_explicit_number_is_shown_regardless_of_recorded()
    {
        ProjectDetails project = new() { Id = DomainId.New(), Name = "alpha", TakeTimeoutMinutes = 45 };

        string row = ProjectShowCommand.TakeTimeoutRow(project, recorded: true);

        row.Should().Contain("45 minute(s)");
    }
}
