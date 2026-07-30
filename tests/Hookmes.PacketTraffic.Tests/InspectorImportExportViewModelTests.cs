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
        public IReadOnlyList<TrafficExchange> Exchanges => [];
        public bool IsInterceptEnabled { get; set; }
        public bool IsResponseInterceptEnabled { get; set; }
        public event Action? Changed;
        public (string Path, string? Filter)? Exported { get; private set; }
        public string? ImportedPath { get; private set; }
        public TrafficExchangePage Query(TrafficExchangeFilter filter) => new([], 0, filter.Offset, filter.Limit);
        public Task<int> ExportArchiveFileAsync(string path, string? filter, CancellationToken cancellationToken) { Exported = (path, filter); return Task.FromResult(7); }
        public Task<int> ImportArchiveFileAsync(string path, CancellationToken cancellationToken) { ImportedPath = path; return Task.FromResult(3); }
        public Task<TrafficOperationResult> AnalyzeAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.FromResult(new TrafficOperationResult(true, "ok"));
        public Task<TrafficOperationResult> ReplayAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.FromResult(new TrafficOperationResult(true, "ok"));
        public Task ContinueAsync(string exchangeId, string request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DropAsync(string exchangeId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FulfillAsync(string exchangeId, string response, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> CreateRepeaterAsync(string exchangeId, CancellationToken cancellationToken) => Task.FromResult("draft");
        public Task<string> EditBinaryBodyAsync(string exchangeId, string side, string kind, long offset, long count, string data, string encoding, CancellationToken cancellationToken) => Task.FromResult("ok");
        public Task<string> ReadBinaryBodyAsync(string exchangeId, string side, long offset, int count, string encoding, CancellationToken cancellationToken) => Task.FromResult("");
        public IReadOnlyList<TrafficParameterItem> ReadParameters(string rawPacket) => [];
        public string SetParameter(string rawPacket, string location, string name, int occurrence, string value) => rawPacket;
        public TrafficAnnotationItem? GetAnnotation(string exchangeId) => null;
        public Task<TrafficAnnotationItem> SaveAnnotationAsync(string exchangeId, bool starred, string tags, string note, string status, CancellationToken cancellationToken) => Task.FromResult(new TrafficAnnotationItem(starred, tags, note, status, 1));
    }
}
