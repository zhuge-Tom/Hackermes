using Hackermes.Inspector.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class RepeaterWorkbenchHistoryViewModelTests
{
    [Fact]
    public async Task Selecting_round_shows_its_request_response_and_comparison_uses_stable_ids()
    {
        var first = new RepeaterRoundItem("draft-a", "A", "send-1", 1, "Completed",
            "10 ms", "POST /one", "HTTP 200 one", true);
        var second = new RepeaterRoundItem("draft-b", "B", "send-9", 9, "Completed",
            "20 ms", "POST /two", "HTTP 201 two", true);
        var service = new FakeService([
            new RepeaterDraftItem("draft-a", "A", "draft request", 1, 1, "Completed", "10 ms", "latest", [first]),
            new RepeaterDraftItem("draft-b", "B", "draft request", 1, 1, "Completed", "20 ms", "latest", [second])
        ]);
        var model = new RepeaterWorkbenchViewModel(service)
        {
            ViewedRound = second,
            LeftRound = first,
            RightRound = second,
            ComparisonSide = "request",
            ComparisonName = " regression pair "
        };

        Assert.Equal("POST /two", model.RequestEditor);
        Assert.Equal("HTTP 201 two", model.ResponseViewer);
        await model.CompareRoundsCommand.ExecuteAsync(null);

        Assert.Equal(("draft-a", "send-1", "draft-b", "send-9", "request"), service.Compared);
        Assert.Equal("structured comparison", model.ComparisonResult);

        await model.SaveRoundComparisonCommand.ExecuteAsync(null);
        Assert.Equal(("regression pair", "draft-a", "send-1", "draft-b", "send-9", "request"), service.Saved);
        Assert.Equal("saved comparison", model.ComparisonResult);
    }

    [Fact]
    public async Task Sending_passes_explicit_timeout_and_cancel_button_cancels_the_active_send()
    {
        var original = new RepeaterDraftItem("draft-a", "A", "GET https://example.test/", 1, 0,
            "Draft", "", "", []);
        var service = new FakeService([original]) { BlockSend = true };
        var model = new RepeaterWorkbenchViewModel(service) { TimeoutSeconds = 7.5m };

        var sending = model.SendCommand.ExecuteAsync(null);
        await service.SendStarted;
        Assert.Equal(TimeSpan.FromSeconds(7.5), service.SendTimeout);
        Assert.True(model.CancelSendCommand.CanExecute(null));

        model.CancelSendCommand.Execute(null);
        await sending;

        Assert.True(service.SendWasCancelled);
        Assert.False(model.IsBusy);
        Assert.False(model.IsSending);
        Assert.Contains("Cancelled", model.Status, StringComparison.Ordinal);
    }

    private sealed class FakeService(IReadOnlyList<RepeaterDraftItem> drafts) : IRepeaterWorkbenchService
    {
        private readonly TaskCompletionSource<bool> _sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RepeaterDraftItem> Drafts => drafts;
        public event Action? RepeaterChanged;
        public (string, string, string, string, string)? Compared { get; private set; }
        public (string, string, string, string, string, string)? Saved { get; private set; }
        public bool BlockSend { get; init; }
        public Task SendStarted => _sendStarted.Task;
        public TimeSpan? SendTimeout { get; private set; }
        public bool SendWasCancelled { get; private set; }
        public Task<string> CompareRoundsAsync(string leftDraftId, string leftResultId, string rightDraftId,
            string rightResultId, string side, CancellationToken cancellationToken)
        {
            Compared = (leftDraftId, leftResultId, rightDraftId, rightResultId, side);
            return Task.FromResult("structured comparison");
        }
        public Task<string> SaveRoundComparisonAsync(string name, string leftDraftId, string leftResultId,
            string rightDraftId, string rightResultId, string side, CancellationToken cancellationToken)
        {
            Saved = (name, leftDraftId, leftResultId, rightDraftId, rightResultId, side);
            return Task.FromResult("saved comparison");
        }
        public async Task<RepeaterDraftItem> SendAsync(
            string id, string name, string request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            SendTimeout = timeout;
            _sendStarted.TrySetResult(true);
            if (!BlockSend) return drafts[0];
            // Complete synchronously from the cancellation callback. This deliberately
            // exercises the ordering where SendAsync reaches its terminal state before
            // CancellationTokenSource.Cancel returns to the cancel command.
            var cancelled = new TaskCompletionSource<RepeaterDraftItem>();
            using (cancellationToken.Register(() =>
            {
                SendWasCancelled = true;
                cancelled.TrySetResult(drafts[0] with
                {
                    LatestStatus = "Cancelled",
                    LatestMetrics = "The send was cancelled."
                });
            }))
                return await cancelled.Task;
        }
        public Task DeleteAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearHistoryAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
