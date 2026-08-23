using Hackermes.Inspector.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class InspectorImportExportViewModelTests
{
    [Fact]
    public async Task Selecting_structured_finding_points_binary_editor_at_exact_body_range()
    {
        var service = new WorkbenchServiceFake
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
        var service = new WorkbenchServiceFake();
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
    public async Task Traffic_workbench_uses_injected_picker_without_ui_dependency()
    {
        var service = new WorkbenchServiceFake();
        InspectorFileDialogRequest? saveRequest = null;
        var model = new TrafficWorkbenchViewModel(service)
        {
            FileDialogs = new InspectorFileDialogDelegates(
                (_, _) => Task.FromResult<string?>(null),
                (request, _) => { saveRequest = request; return Task.FromResult<string?>("chosen/session.har"); },
                (_, _) => Task.FromResult(true))
        };

        await model.ExportArchiveCommand.ExecuteAsync(null);

        Assert.Equal("chosen/session.har", service.Exported?.Path);
        Assert.Equal("chosen/session.har", model.ArchivePath);
        Assert.Equal("traffic.har", saveRequest!.SuggestedPath);
        Assert.Contains(saveRequest.FileTypes, type => type.Patterns.Contains("*.har"));
        Assert.Contains(saveRequest.FileTypes, type => type.Patterns.Contains("*.json"));
    }

    [Fact]
    public async Task Cancelled_picker_does_not_call_archive_service()
    {
        var service = new WorkbenchServiceFake();
        var model = new TrafficWorkbenchViewModel(service)
        {
            FileDialogs = new InspectorFileDialogDelegates(
                (_, _) => Task.FromResult<string?>(null),
                (_, _) => Task.FromResult<string?>(null),
                (_, _) => Task.FromResult(true))
        };

        await model.ImportArchiveCommand.ExecuteAsync(null);
        Assert.Null(service.ImportedPath);
    }

    [Fact]
    public async Task Rules_workbench_uses_json_picker_delegate()
    {
        var service = new RulesWorkbenchFake();
        InspectorFileDialogRequest? request = null;
        var model = new TrafficRulesViewModel(service)
        {
            FileDialogs = new InspectorFileDialogDelegates(
                (_, _) => Task.FromResult<string?>(null),
                (value, _) => { request = value; return Task.FromResult<string?>("chosen/rules.json"); },
                (_, _) => Task.FromResult(true))
        };

        await model.ExportRulesCommand.ExecuteAsync(null);
        Assert.Equal("chosen/rules.json", service.ExportedPath);
        Assert.Single(request!.FileTypes);
        Assert.Contains("*.json", request.FileTypes[0].Patterns);
    }

    [Fact]
    public async Task Replace_rules_import_requires_injected_confirmation()
    {
        var service = new RulesWorkbenchFake();
        string? confirmation = null;
        var model = new TrafficRulesViewModel(service)
        {
            FileDialogs = new InspectorFileDialogDelegates(
                (_, _) => Task.FromResult<string?>("chosen/rules.json"),
                (_, _) => Task.FromResult<string?>(null),
                (message, _) => { confirmation = message; return Task.FromResult(false); })
        };

        await model.ImportRulesCommand.ExecuteAsync(null);

        Assert.Null(service.Imported);
        Assert.Contains("Replace all current traffic rules", confirmation);
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
    public async Task Recent_paths_initialize_both_workbenches_and_are_remembered_after_success()
    {
        var recent = new RecentPathsFake("old/archive.har", "old/rules.json");
        var archive = new WorkbenchServiceFake();
        var trafficModel = new TrafficWorkbenchViewModel(archive, recent);
        var rules = new RulesWorkbenchFake();
        var rulesModel = new TrafficRulesViewModel(rules, recent);

        Assert.Equal("old/archive.har", trafficModel.ArchivePath);
        Assert.Equal("old/rules.json", rulesModel.RulesFilePath);

        trafficModel.ArchivePath = " new/archive.har ";
        rulesModel.RulesFilePath = " new/rules.json ";
        await trafficModel.ExportArchiveCommand.ExecuteAsync(null);
        await rulesModel.ImportRulesCommand.ExecuteAsync(null);

        Assert.Equal("normalized:new/archive.har", recent.RememberedArchive);
        Assert.Equal("normalized:new/rules.json", recent.RememberedRules);
        Assert.Equal("normalized:new/archive.har", archive.Exported?.Path);
        Assert.Equal("normalized:new/rules.json", rules.Imported?.Path);
    }

    [Fact]
    public async Task Binary_editor_exposes_and_discards_pending_draft()
    {
        var service = new WorkbenchServiceFake { DraftStatus = "Pending request: before -> after", DiscardResult = true };
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

    [Fact]
    public async Task Resolve_commands_surface_shared_final_state_and_audit_id()
    {
        var service = new WorkbenchServiceFake();
        var model = new TrafficWorkbenchViewModel(service)
        {
            Selected = new TrafficExchange("packet-1", DateTimeOffset.UtcNow, "POST", "https://example.test/", null,
                "POST / HTTP/1.1\r\n\r\n", "", IsIntercepted: true)
        };

        await model.ContinueCommand.ExecuteAsync(null);

        Assert.Contains("state Continued", model.Analysis);
        Assert.Contains("audit audit-test", model.Analysis);
    }

    [Fact]
    public void Annotation_filters_are_forwarded_as_trimmed_query_values()
    {
        var service = new WorkbenchServiceFake();
        var model = new TrafficWorkbenchViewModel(service);

        model.AnnotationTagFilter = " auth ";
        model.AnnotationStatusFilter = "Resolved";

        Assert.Equal("auth", service.LastQuery!.AnnotationTag);
        Assert.Equal("Resolved", service.LastQuery.AnnotationStatus);
        Assert.Equal(0, service.LastQuery.Offset);

        model.ClearAnnotationFiltersCommand.Execute(null);

        Assert.Null(service.LastQuery.AnnotationTag);
        Assert.Null(service.LastQuery.AnnotationStatus);
    }

    [Fact]
    public async Task Delete_annotation_removes_persisted_value_and_clears_editor()
    {
        var service = new WorkbenchServiceFake();
        service.Annotations["packet"] = new TrafficAnnotationItem(
            true, "auth,api", "Check token", "InReview", 4);
        var model = new TrafficWorkbenchViewModel(service)
        {
            Selected = service.Exchanges[0]
        };

        Assert.True(model.HasAnnotation);
        Assert.True(model.AnnotationStarred);

        await model.DeleteAnnotationCommand.ExecuteAsync(null);

        Assert.Equal("packet", service.DeletedAnnotationId);
        Assert.False(model.HasAnnotation);
        Assert.False(model.AnnotationStarred);
        Assert.Empty(model.AnnotationTags);
        Assert.Empty(model.AnnotationNote);
        Assert.Equal("Unreviewed", model.AnnotationStatus);
        Assert.Contains("Annotation deleted", model.Analysis);
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
        public Task UpdateRuleAsync(TrafficRuleDraft draft, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TrafficRuleDraft?> GetRuleAsync(string id, CancellationToken cancellationToken) => Task.FromResult<TrafficRuleDraft?>(null);
        public Task SetRuleEnabledAsync(string id, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveRuleAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MoveRuleAsync(string id, int targetIndex, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecentPathsFake(string? archive, string? rules) : IRecentTrafficPathService
    {
        public string? LastArchivePath => archive;
        public string? LastRulesPath => rules;
        public string? RememberedArchive { get; private set; }
        public string? RememberedRules { get; private set; }
        public string NormalizePath(string path) => "normalized:" + path.Trim();
        public void RememberArchivePath(string path) => RememberedArchive = path;
        public void RememberRulesPath(string path) => RememberedRules = path;
    }
}
