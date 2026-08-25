using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Where a run's directory goes (ruled 2026-08-23, backlog 49): under the owning task's own
/// directory when the project has a home — there is no top-level runs/ in the home — and the
/// platform-global location otherwise. Resolved once, at dispatch, and recorded on
/// RunDispatched; see <see cref="Hall9k.Domain.Features.Run.RunAggregate"/> and
/// <see cref="Hall9k.Domain.Features.Run.Projections.RunDetails"/> for the fallback a stream
/// written before this existed replays through.
/// </summary>
public sealed class RunPathsTests
{
    [Fact]
    public void With_no_home_a_new_run_falls_back_to_the_platform_global_location()
    {
        Guid runId = DomainId.New();

        RunPaths.ResolveDirectory(ProjectHome.None, "abc12345-some-task", runId)
            .Should().Be(RunPaths.GlobalDirectory(runId), "a project with no home has nowhere else to put it");
    }

    [Fact]
    public void With_a_home_a_new_run_lands_under_its_owning_tasks_own_directory()
    {
        Guid runId = DomainId.New();
        ProjectHome home = ProjectHome.Parse(Path.Combine(Path.GetTempPath(), "hall9k-runpaths-home"));

        string directory = RunPaths.ResolveDirectory(home, "98ac05ef-project-home", runId);

        directory.Should().Be(
            Path.Combine(home.Value, "tasks", "98ac05ef-project-home", "runs", runId.ToString()),
            "the task directory is the whole story of the task, and that includes every run");
    }

    [Fact]
    public void The_global_directory_is_keyed_by_the_run_id_alone()
    {
        Guid runId = DomainId.New();

        RunPaths.GlobalDirectory(runId).Should().Be(Path.Combine(RunPaths.Root, "runs", runId.ToString()));
    }

    // ResolveCurrentDirectory (backlog 51 cycle 1): RunDispatched.RunDirectory is recorded once
    // and never updated, but the render sweep relocates a task's whole directory into or out of
    // tasks/_archive/ as it crosses the terminal boundary, carrying every run underneath it.
    public sealed class ResolveCurrentDirectoryTests : IDisposable
    {
        private readonly string _home = Directory.CreateTempSubdirectory("hall9k-runpaths-resolve-").FullName;

        [Fact]
        public void A_directory_that_still_exists_at_the_recorded_path_is_returned_unchanged()
        {
            string recorded = Path.Combine(_home, "tasks", "abc12345-still-live", "runs", "some-run");
            Directory.CreateDirectory(recorded);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(recorded);
        }

        [Fact]
        public void A_run_whose_task_archived_resolves_to_the_directory_under_tasks_archive()
        {
            string recorded = Path.Combine(_home, "tasks", "abc12345-closed-out", "runs", "some-run");
            string actual = Path.Combine(_home, "tasks", "_archive", "abc12345-closed-out", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the task's whole directory moved into tasks/_archive/ on a later sweep, taking this run with it");
        }

        [Fact]
        public void A_run_whose_task_reopened_resolves_to_the_directory_back_under_tasks()
        {
            string recorded = Path.Combine(_home, "tasks", "_archive", "abc12345-reopened", "runs", "some-run");
            string actual = Path.Combine(_home, "tasks", "abc12345-reopened", "runs", "some-run");
            Directory.CreateDirectory(actual);

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(actual,
                "the task moved back out of tasks/_archive/ once it was reopened, taking this run with it");
        }

        [Fact]
        public void Neither_the_recorded_nor_the_alternate_path_existing_falls_back_to_the_recorded_path()
        {
            string recorded = Path.Combine(_home, "tasks", "abc12345-gone", "runs", "some-run");

            RunPaths.ResolveCurrentDirectory(recorded).Should().Be(recorded,
                "a caller with neither location on disk (a foreign machine) keeps its existing not-found handling");
        }

        public void Dispose() => Directory.Delete(_home, recursive: true);
    }
}
