using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.Service.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies the sync queue collapses repeated requests and reports a full queue instead of dropping a request silently.</summary>
[Trait("Category", "Sync")]
public class SyncDispatcherTests
{
    [Fact]
    public void A_repeated_request_for_the_same_connection_and_trigger_is_collapsed_while_it_waits()
    {
        var dispatcher = new ChannelSyncDispatcher();
        var connection = Guid.NewGuid();

        dispatcher.TryEnqueue(Request(connection, SyncTrigger.Manual)).Should().Be(EnqueueResult.Queued);
        dispatcher.TryEnqueue(Request(connection, SyncTrigger.Manual)).Should().Be(EnqueueResult.AlreadyQueued);
        dispatcher.TryEnqueue(Request(connection, SyncTrigger.Manual)).Should().Be(EnqueueResult.AlreadyQueued);

        dispatcher.Reader.Count.Should().Be(1);
    }

    [Fact]
    public void A_post_link_request_is_never_collapsed_into_a_manual_one_for_the_same_connection()
    {
        var dispatcher = new ChannelSyncDispatcher();
        var connection = Guid.NewGuid();

        dispatcher.TryEnqueue(Request(connection, SyncTrigger.Manual)).Should().Be(EnqueueResult.Queued);
        dispatcher.TryEnqueue(Request(connection, SyncTrigger.PostLink)).Should().Be(EnqueueResult.Queued);

        dispatcher.Reader.Count.Should().Be(2);
    }

    [Fact]
    public void Requests_for_different_connections_are_independent()
    {
        var dispatcher = new ChannelSyncDispatcher();

        dispatcher.TryEnqueue(Request(Guid.NewGuid(), SyncTrigger.Manual)).Should().Be(EnqueueResult.Queued);
        dispatcher.TryEnqueue(Request(Guid.NewGuid(), SyncTrigger.Manual)).Should().Be(EnqueueResult.Queued);

        dispatcher.Reader.Count.Should().Be(2);
    }

    [Fact]
    public void A_finished_request_can_be_queued_again()
    {
        var dispatcher = new ChannelSyncDispatcher();
        var request = Request(Guid.NewGuid(), SyncTrigger.Manual);

        dispatcher.TryEnqueue(request).Should().Be(EnqueueResult.Queued);
        dispatcher.Reader.TryRead(out var read).Should().BeTrue();
        dispatcher.TryEnqueue(request).Should().Be(EnqueueResult.AlreadyQueued);

        dispatcher.Complete(read!);

        dispatcher.TryEnqueue(request).Should().Be(EnqueueResult.Queued);
    }

    [Fact]
    public void A_full_queue_reports_full_and_does_not_leave_the_request_marked_as_waiting()
    {
        var dispatcher = new ChannelSyncDispatcher();

        for (var index = 0; index < ChannelSyncDispatcher.Capacity; index++)
        {
            dispatcher.TryEnqueue(Request(Guid.NewGuid(), SyncTrigger.Manual)).Should().Be(EnqueueResult.Queued);
        }

        var overflow = Request(Guid.NewGuid(), SyncTrigger.PostLink);
        dispatcher.TryEnqueue(overflow).Should().Be(EnqueueResult.Full);

        dispatcher.Reader.TryRead(out var first).Should().BeTrue();
        dispatcher.Complete(first!);

        dispatcher.TryEnqueue(overflow).Should().Be(EnqueueResult.Queued);
    }

    private static SyncRequest Request(Guid connectionId, SyncTrigger trigger)
    {
        return new SyncRequest(connectionId, trigger, FetchContext.Background);
    }
}
