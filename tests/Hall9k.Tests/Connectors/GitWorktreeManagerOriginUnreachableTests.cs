using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The two throw sites the launch network backoff keys on, against a real repository whose origin
/// has been deleted so that every fetch fails locally and deterministically: both raise the typed
/// <see cref="OriginUnreachableException"/> carrying git's own error, rather than a bare
/// <see cref="WorktreeException"/> the launcher would have to parse prose to classify.
/// </summary>
[Trait("Category", "RealProcessSpawn")]
public sealed class GitWorktreeManagerOriginUnreachableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-origin-gone-{Guid.NewGuid():N}");

    public void Dispose() => TemporaryTree.TryDelete(_root);

    private async Task<string> SeedRepositoryWithoutItsOriginAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        string origin = Path.Combine(_root, "origin.git");
        string repository = Path.Combine(_root, "repo");
        await TestGit.RunAsync(_root, ["init", "--bare", "-b", "main", origin], cancellationToken);
        await TestGit.RunAsync(_root, ["clone", origin, repository], cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# origin gone\n", cancellationToken);
        await TestGit.RunAsync(repository, ["add", "-A"], cancellationToken);
        await TestGit.RunAsync(repository, TestGit.CommitAs("commit", "-qm", "init"), cancellationToken);
        await TestGit.RunAsync(repository, ["push", "-q", "origin", "main"], cancellationToken);
        TemporaryTree.Delete(origin);
        return repository;
    }

    [Fact]
    public async Task A_pull_request_checkout_whose_fetch_cannot_read_origin_raises_the_typed_exception()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        string repository = await SeedRepositoryWithoutItsOriginAsync(cts.Token);
        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);

        Func<Task> checkout = () => worktrees.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(repository, 12, Guid.NewGuid(), Guid.NewGuid(), string.Empty), cts.Token);

        OriginUnreachableException thrown = (await checkout.Should().ThrowAsync<OriginUnreachableException>()).Which;
        thrown.GitError.Should().NotBeNullOrWhiteSpace();
        thrown.Message.Should().Contain(thrown.GitError);
    }

    [Fact]
    public async Task Resuming_a_branch_this_node_does_not_hold_when_origin_cannot_be_read_raises_the_typed_exception()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        string repository = await SeedRepositoryWithoutItsOriginAsync(cts.Token);
        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);

        Func<Task> resume = () => worktrees.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(repository, "task/pushed-by-the-other-node", Guid.NewGuid(), Guid.NewGuid()),
            cts.Token);

        OriginUnreachableException thrown = (await resume.Should().ThrowAsync<OriginUnreachableException>()).Which;
        thrown.GitError.Should().NotBeNullOrWhiteSpace();
        thrown.Message.Should().Contain("whether origin still holds it is unknown");
    }
}
