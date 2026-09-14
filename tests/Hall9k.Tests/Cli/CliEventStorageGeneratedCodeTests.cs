using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Marten's <c>TypeLoadMode.Auto</c> finds this pre-generated event-storage class by name in the
/// entry assembly and never checks whether it still matches the current model (cycle-1 pre-PR
/// review, both lenses) — unlike the document-storage providers beside it, whose file names carry
/// a mapping hash and so regenerate on their own the moment the model changes. A stale copy here
/// means every event <c>CliStore.Open()</c> appends loses its origin headers silently. No test
/// that actually appends through a store can catch a regression of this shape under
/// <c>dotnet test</c>: the test host, not <c>h9k.dll</c>, is the entry assembly there, so Marten
/// generates fresh code regardless of what this committed file says. Pinning the committed source
/// itself is what is left.
/// </summary>
public sealed class CliEventStorageGeneratedCodeTests
{
    [Fact]
    public void The_committed_event_storage_insert_carries_the_headers_column()
    {
        string path = Path.Combine(
            PublishTestSupport.FindRepositoryRoot(),
            "src", "Hall9k.Cli", "Internal", "Generated", "EventStore", "EventStorage.cs");
        string source = File.ReadAllText(path);

        source.Should().Contain(
            "insert into public.mt_events (data, type, mt_dotnet_type, id, stream_id, version, timestamp, tenant_id, headers, seq_id) values (",
            "the CLI's pre-generated event append must persist the headers column HeadersEnabled turns on, " +
            "or every event h9k appends silently loses its origin metadata");
    }
}
