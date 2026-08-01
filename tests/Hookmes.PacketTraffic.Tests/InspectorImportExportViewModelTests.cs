using Hookmes.Inspector.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class InspectorImportExportViewModelTests
{
    [Fact]
    public async Task Selecting_structured_finding_points_binary_editor_at_exact_body_range()
    {
        var service = new ArchiveWorkbenchFake
        {
            StructuredFindings = [new TrafficFindingItem("High", "body-risk", "Inspect bytes", "Request", "Body", "token", null, null, 17, 5)]
        };
        var model = new TrafficWorkbenchViewModel(service);
        model.Selected = service.Exchanges[0];

        await model.AnalyzeCommand.ExecuteAsync(null);
        model.SelectedFinding = model.Findings[0];

        Assert.Equal("request", model.BinarySide);
        Assert.Equal("17", model.BinaryOffset);
        Assert.Equal("5", model.BinaryCount);
        Assert.Contains("Binary editor target", model.FindingTarget);
    }

    [Fact]
    public async Task Traffic_workbench_forwards_archive_path_filter_and_reports_counts()
    {
        var service = new ArchiveWorkbenchFake();
        var model = new TrafficWorkbenchViewModel(service)
        {
            ArchivePath = " captures/session.har ",
            ArchiveFilter = " api/v1 "
        };

        await model.ExportArchiveCommand.ExecuteAsync(null);
        Assert.Equal(("captures/session.har", "api/v1"), service.Exported);
        Assert.Contains("7 packet(s)", model.Analysis);

        await model.ImportArchiveCommand.ExecuteAsync(null);
        Assert.Equal("captures/session.har", service.ImportedPath);
        Assert.Contains("3 packet(s)", model.Analysis);
    }

    [Fact]
    public async Task Rules_workbench_forwards_replace_or_merge_mode()
    {
        var service = new RulesWorkbenchFake();
        var model = new TrafficRulesViewModel(service)
        {
            RulesFilePath = " rules/team.json ",
            MergeImport = true
        };

        await model.ExportRulesCommand.ExecuteAsync(null);
        Assert.Equal("rules/team.json", service.ExportedPath);
        await model.ImportRulesCommand.ExecuteAsync(null);

        Assert.Equal(("rules/team.json", true), service.Imported);
        Assert.Contains("merge", model.Status);
    }

    [Fact]
    public async Task Binary_editor_exposes_and_discards_pending_draft()
    {
        var service = new ArchiveWorkbenchFake { DraftStatus = "Pending request: before -> after", DiscardResult = true };
        var model = new TrafficWorkbenchViewModel(service)
        {
            Selected = new TrafficExchange("packet-1", DateTimeOffset.UtcNow, "POST", "https://example.test/", null, "POST / HTTP/1.1\r\n\r\n", ""),
            BinarySide = "request"
        };

        await model.RefreshBinaryDraftCommand.ExecuteAsync(null);
        Assert.Contains("before -> after", model.BinaryDraftStatus);
        await model.DiscardBinaryDraftCommand.ExecuteAsync(null);
        Assert.Contains("restored", model.BinaryDraftStatus);
        Assert.Equal(("packet-1", "request"), service.Discarded);
    }

    private sealed class RulesWorkbenchFake : ITrafficRuleWorkbenchService
    {
        public IReadOnlyList<TrafficRuleItem> Rules => [];
        public event Action? RulesChanged;
        public string? ExportedPath { get; private set; }
        public (string Path, bool Merge)? Imported { get; private set; }
        public Task ExportRulesFileAsync(string path, CancellationToken cancellationToken) { ExportedPath = path; return Task.CompletedTask; }
        public Task<int> ImportRulesFileAsync(string path, bool merge, CancellationToken cancellationToken) { Imported = (path, merge); return Task.FromResult(4); }
        public Task AddRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveRuleAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MoveRuleAsync(string id, int targetIndex, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ArchiveWorkbenchFake : ITrafficWorkbenchService
    {
        public IReadOnlyList<TrafficExchange> Exchanges =>
            [new("packet", DateTimeOffset.UtcNow, "POST", "https://example.test", null, "POST / HTTP/1.1\r\n\r\nbody", "")];
        public IReadOnlyList<TrafficFindingItem> StructuredFindings { get; init; } = [];
        public bool IsInterceptEnabled { get; set; }
        public bool IsResponseInterceptEnabled { get; set; }
        public event Action? Changed;
        public (string Path, string? Filter)? Exported { get; private set; }
        public string? ImportedPath { get; private set; }
        public string? DraftStatus { get; init; }
        public bool DiscardResult { get; init; }
        public (string Id, string Side)? Discarded { get; private set; }
        public TrafficExchangePage Query(TrafficExchangeFilter filter) => new([], 0, filter.Offset, filter.Limit);
        public Task<int> ExportArchiveFileAsync(string path, string? filter, CancellationToken cancellationToken) { Exported = (path, filter); return Task.FromResult(7); }
        public Task<int> ImportArchiveFileAsync(string path, CancellationToken cancellationToken) { ImportedPath = path; return Task.FromResult(3); }
        public Task<TrafficOperationResult> AnalyzeAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.FromResult(new TrafficOperationResult(true, "ok"));
        public Task<IReadOnlyList<TrafficFindingItem>> AnalyzeFindingsAsync(string exchangeId, string side, string rawPacket, CancellationToken cancellationToken) => Task.FromResult(StructuredFindings);
        public Task<TrafficOperationResult> ReplayAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.FromResult(new TrafficOperationResult(true, "ok"));
        public Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DropAsync(string exchangeId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> CreateRepeaterAsync(string exchangeId, CancellationToken cancellationToken) => Task.FromResult("draft");
        public Task<string> EditBinaryBodyAsync(string exchangeId, string side, string kind, long offset, long count, string data, string encoding, CancellationToken cancellationToken) => Task.FromResult("ok");
        public Task<string> ReadBinaryBodyAsync(string exchangeId, string side, long offset, int count, string encoding, CancellationToken cancellationToken) => Task.FromResult("");
        public Task<TrafficBinaryBodyInfo> GetBinaryBodyInfoAsync(string exchangeId, string side, CancellationToken cancellationToken) =>
            Task.FromResult(new TrafficBinaryBodyInfo(0, new string('0', 64), null, null));
        public Task<string?> GetBinaryDraftStatusAsync(string exchangeId, string side, CancellationToken cancellationToken) => Task.FromResult(DraftStatus);
        public Task<bool> DiscardBinaryDraftAsync(string exchangeId, string side, CancellationToken cancellationToken) { Discarded = (exchangeId, side); return Task.FromResult(DiscardResult); }
        public IReadOnlyList<TrafficAuditItem> GetAudit(string exchangeId, int limit = 100) => [];
        public TrafficHistoryOverview GetHistoryOverview() => new(0, 0, 0, null, null, 5000, 256L * 1024 * 1024, 30, true);
        public string PreviewHistoryCleanup() => "No entries would be removed.";
        public Task<TrafficHistoryOverview> UpdateHistoryPolicyAsync(int maxEntries, long maxBytes, int retentionDays, bool autoPrune, CancellationToken cancellationToken) =>
            Task.FromResult(new TrafficHistoryOverview(0, 0, 0, null, null, maxEntries, maxBytes, retentionDays, autoPrune));
        public Task<string> CleanupTrafficHistoryAsync(CancellationToken cancellationToken) => Task.FromResult("No entries removed.");
        public Task ClearTrafficHistoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IReadOnlyList<TrafficParameterItem> ReadParameters(string rawPacket) => [];
        public string SetParameter(string rawPacket, string location, string name, int occurrence, string value) => rawPacket;
        public TrafficAnnotationItem? GetAnnotation(string exchangeId) => null;
        public Task<TrafficAnnotationItem> SaveAnnotationAsync(string exchangeId, bool starred, string tags, string note, string status, CancellationToken cancellationToken) => Task.FromResult(new TrafficAnnotationItem(starred, tags, note, status, 1));
    }
}
