using Hall9k.Daemon.Closeout;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// An <see cref="IRemoteParentReader"/> that never observes anything, for a
/// <see cref="StackedParentWatch"/> constructed in a test that never exercises the
/// merged-elsewhere resolution's own live-read step — every call records itself and answers
/// <see cref="RemoteParentRead.Unobserved"/>, which is exactly what a caller sees when nothing
/// could be read, so a test using this fake exercises StackedParentWatch's own git-reachability
/// and recorded-base fallbacks precisely as it did before that resolution step existed.
/// </summary>
internal sealed class NoOpRemoteParentReader : IRemoteParentReader
{
    public List<int> Reads { get; } = [];

    public Task<RemoteParentRead> ReadAsync(
        string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken)
    {
        Reads.Add(pullRequestNumber);
        return Task.FromResult(RemoteParentRead.Unobserved("this fake never observes anything"));
    }
}
