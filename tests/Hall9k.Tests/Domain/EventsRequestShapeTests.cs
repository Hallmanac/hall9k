using FluentAssertions;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The four catch-up request shapes, and which of them an answering node serves from below its own
/// replication switch-on point (<c>EventCatchUpResponder.AnswerAsync</c>). Three do: the two a
/// human typed, and a brand-new node's own bootstrap (task 74a7cd0b, Decisions Log
/// #260). The gap-fill is the one that keeps the bound, so the two properties
/// have to keep telling it apart from the bootstrap it shares two null fields with.
/// </summary>
public sealed class EventsRequestShapeTests
{
    [Fact]
    public void A_bootstrap_is_every_field_unset()
    {
        EventReplicationCodec.EventsRequestRecord bootstrap = new(
            DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, ForStreamId: null);

        bootstrap.IsBootstrap.Should().BeTrue();
        bootstrap.IsExplicitAsk.Should().BeFalse("nobody typed it — the sweep minted it");
    }

    [Fact]
    public void A_gap_fill_names_an_origin_node_and_is_neither()
    {
        EventReplicationCodec.EventsRequestRecord gapFill = new(
            DomainId.New(), DomainId.New(), SinceOriginSequence: 30, ForStreamId: null);

        gapFill.IsBootstrap.Should().BeFalse("it names the origin whose history has a hole in it");
        gapFill.IsExplicitAsk.Should().BeFalse();
    }

    [Fact]
    public void A_stream_request_is_an_explicit_ask_and_never_a_bootstrap()
    {
        EventReplicationCodec.EventsRequestRecord streamAsk = new(
            DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, DomainId.New());

        streamAsk.IsExplicitAsk.Should().BeTrue();
        streamAsk.IsBootstrap.Should().BeFalse();
    }

    /// <summary>
    /// <c>h9k project pull --since all</c> sends zero, not null, and the two shapes are told apart
    /// by the field being set at all rather than by its value — the same distinction
    /// <c>EventCatchUpCoordinator.RequestBootstrapAsync</c> already queries on, so a pull can never
    /// read as the one bootstrap of a node's life, nor suppress it.
    /// </summary>
    [Fact]
    public void A_project_pull_from_zero_is_an_explicit_ask_rather_than_a_bootstrap()
    {
        EventReplicationCodec.EventsRequestRecord pull = new(
            DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, ForStreamId: null, SinceGlobalSequence: 0);

        pull.IsExplicitAsk.Should().BeTrue();
        pull.IsBootstrap.Should().BeFalse();
    }

    /// <summary>
    /// An envelope from a sender on a build older than <c>SinceGlobalSequence</c> carries three
    /// fields, and still decodes as exactly the shape it was sent as.
    /// </summary>
    [Fact]
    public void A_three_field_envelope_from_an_older_sender_still_decodes_as_its_own_shape()
    {
        Guid requestId = DomainId.New();
        Guid originNodeId = DomainId.New();

        EventReplicationCodec.EventsRequestRecord bootstrap = EventReplicationCodec.DecodeRequest(
            $$"""{"requestId":"{{requestId}}","forOriginNodeId":null,"sinceOriginSequence":0,"forStreamId":null}""")!;
        bootstrap.IsBootstrap.Should().BeTrue();

        EventReplicationCodec.EventsRequestRecord gapFill = EventReplicationCodec.DecodeRequest(
            $$"""{"requestId":"{{requestId}}","forOriginNodeId":"{{originNodeId}}","sinceOriginSequence":12,"forStreamId":null}""")!;
        gapFill.IsBootstrap.Should().BeFalse();
    }
}
