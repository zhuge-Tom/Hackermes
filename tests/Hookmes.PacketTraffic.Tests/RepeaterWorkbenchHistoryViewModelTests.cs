using Hookmes.Inspector.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

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
            ComparisonSide = "request"
        };

        Assert.Equal("POST /two", model.RequestEditor);
        Assert.Equal("HTTP 201 two", model.ResponseViewer);
        await model.CompareRoundsCommand.ExecuteAsync(null);

        Assert.Equal(("draft-a", "send-1", "draft-b", "send-9", "request"), service.Compared);
        Assert.Equal("structured comparison", model.ComparisonResult);
    }

    private sealed class FakeService(IReadOnlyList<RepeaterDraftItem> drafts) : IRepeaterWorkbenchService
    {
        public IReadOnlyList<RepeaterDraftItem> Drafts => drafts;
        public event Action? RepeaterChanged;
        public (string, string, string, string, string)? Compared { get; private set; }
        public Task<string> CompareRoundsAsync(string leftDraftId, string leftResultId, string rightDraftId,
            string rightResultId, string side, CancellationToken cancellationToken)
        {
            Compared = (leftDraftId, leftResultId, rightDraftId, rightResultId, side);
            return Task.FromResult("structured comparison");
        }
        public Task<RepeaterDraftItem> SendAsync(string id, string name, string request, CancellationToken cancellationToken) =>
            Task.FromResult(drafts[0]);
        public Task DeleteAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearHistoryAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
