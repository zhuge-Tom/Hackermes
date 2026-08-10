using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Hackermes.Assessment;
using Hackermes.Platform.Registries;
using System;
using System.Linq;

namespace Hackermes.App.Views;

/// <summary>Cross-platform Stage 7C workspace. All mutations go through the shared control plane.</summary>
public sealed class AssessmentWorkspaceView : UserControl, ITabActivationAware
{
    private readonly IAssessmentControlPlane _plane;
    private readonly ComboBox _jobs = new() { MinWidth = 360 };
    private readonly ListBox _scopes = new() { MinHeight = 120 };
    private readonly ListBox _plans = new() { MinHeight = 120 };
    private readonly ListBox _approvals = new() { MinHeight = 120 };
    private readonly TextBox _scopeName = new() { PlaceholderText = "范围名称" };
    private readonly TextBox _authorization = new() { PlaceholderText = "授权依据 / 工单号" };
    private readonly TextBox _operator = new() { PlaceholderText = "操作者身份", Text = Environment.UserName };
    private readonly TextBox _targets = new() { PlaceholderText = "精确目标，逗号分隔", Text = "127.0.0.1" };
    private readonly TextBox _scopeMinutes = new() { PlaceholderText = "有效分钟", Text = "60" };
    private readonly TextBox _planName = new() { PlaceholderText = "计划名称" };
    private readonly ComboBox _adapter = new() { MinWidth = 220 };
    private readonly TextBox _stepInput = new() { PlaceholderText = "结构化 JSON；simulation.echo 可输入普通文本", Text = "Stage 7 local acceptance", AcceptsReturn = true, MinHeight = 72 };
    private readonly TextBox _timeoutSeconds = new() { PlaceholderText = "超时秒数", Text = "30" };
    private readonly TextBox _approvalMinutes = new() { PlaceholderText = "审批有效分钟", Text = "30" };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _evidence = new();
    private readonly TextBox _evidenceDetail = DetailBox();
    private readonly ListBox _findings = new();
    private readonly TextBox _findingDetail = DetailBox();
    private readonly ListBox _audit = new();
    private readonly TextBox _auditDetail = DetailBox();
    private readonly TextBlock _auditVerification = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _report = DetailBox();
    private readonly TextBox _findingTitle = new() { PlaceholderText = "Finding 标题" };
    private readonly TextBox _findingDescription = new() { PlaceholderText = "说明", AcceptsReturn = true, MinHeight = 72 };
    private readonly ComboBox _severity = new() { ItemsSource = new[] { "Critical", "High", "Medium", "Low", "Info" }, SelectedIndex = 2 };
    private readonly ComboBox _confidence = new() { ItemsSource = new[] { "High", "Medium", "Low" }, SelectedIndex = 1 };
    private readonly ComboBox _reviewStatus = new() { ItemsSource = Enum.GetValues<AssessmentFindingStatus>(), SelectedIndex = 0 };
    private readonly TextBox _reviewer = new() { PlaceholderText = "复核人身份" };
    private readonly TextBox _reviewNote = new() { PlaceholderText = "复核说明", AcceptsReturn = true, MinHeight = 62 };

    public AssessmentWorkspaceView(IAssessmentControlPlane plane)
    {
        _plane = plane;
        Content = Build();
        _jobs.SelectionChanged += (_, _) => RefreshSelectedJob();
        _evidence.SelectionChanged += (_, _) => ShowEvidence();
        _findings.SelectionChanged += (_, _) => ShowFinding();
        _audit.SelectionChanged += (_, _) => ShowAudit();
        _adapter.ItemsSource = new[] { AuthorizedToolCatalog.SimulationEcho }
            .Concat(AuthorizedToolCatalog.Describe().Select(value => value.Id)).ToArray();
        _adapter.SelectedIndex = 0;
        RefreshAll();
    }

    public void OnTabActivated() => RefreshAll();

    private Control Build()
    {
        var refresh = new Button { Content = "刷新" };
        refresh.Click += (_, _) => RefreshAll();
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 8),
            Children = { new TextBlock { Text = "任务", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold }, _jobs, refresh }
        };
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(10, 0, 10, 8), Children = { _summary, _status } };
        var top = new StackPanel { Children = { toolbar, header } };

        var tabs = new TabControl
        {
            ItemsSource = new[]
            {
                Tab("范围与执行", BuildLifecycleTab()), Tab("证据", BuildEvidenceTab()), Tab("Finding 与复核", BuildFindingTab()),
                Tab("审计链", BuildAuditTab()), Tab("报告", BuildReportTab())
            }
        };
        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        root.Children.Add(top);
        root.Children.Add(tabs);
        return root;
    }

    private Control BuildLifecycleTab()
    {
        var createScope = new Button { Content = "创建授权范围" };
        createScope.Click += (_, _) => CreateScope();
        var revokeScope = new Button { Content = "撤销选中范围" };
        revokeScope.Click += (_, _) => RevokeScope();
        var createPlan = new Button { Content = "为选中范围创建计划" };
        createPlan.Click += (_, _) => CreatePlan();
        var approve = new Button { Content = "一次性批准选中计划" };
        approve.Click += (_, _) => ApprovePlan();
        var revokeApproval = new Button { Content = "撤销选中审批" };
        revokeApproval.Click += (_, _) => RevokeApproval();
        var run = new Button { Content = "执行选中审批" };
        run.Click += async (_, _) => await RunApprovedPlanAsync();
        var cancel = new Button { Content = "取消当前任务" };
        cancel.Click += (_, _) => CancelSelectedJob();

        var scopePanel = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 6,
            Children = { Label("1. 授权范围"), _scopes, _scopeName, _authorization, _operator, _targets,
                Labeled("有效分钟", _scopeMinutes), createScope, revokeScope }
        };
        var planPanel = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 6,
            Children = { Label("2. 固定计划"), _plans, _planName, Labeled("适配器", _adapter), _stepInput,
                Labeled("单步超时（秒）", _timeoutSeconds), createPlan }
        };
        var approvalPanel = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 6,
            Children = { Label("3. 审批与执行"), _approvals, Labeled("审批有效分钟", _approvalMinutes), approve,
                run, revokeApproval, cancel,
                new TextBlock { Text = "每张审批票据只能执行一次。范围、计划哈希、目标和超时会在执行前再次校验。", TextWrapping = TextWrapping.Wrap }
            }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        Grid.SetColumn(scopePanel, 0); Grid.SetColumn(planPanel, 1); Grid.SetColumn(approvalPanel, 2);
        grid.Children.Add(scopePanel); grid.Children.Add(planPanel); grid.Children.Add(approvalPanel);
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private Control BuildEvidenceTab()
    {
        var verify = new Button { Content = "验证 SHA-256" };
        verify.Click += (_, _) => VerifySelectedEvidence();
        return Split(_evidence, new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { verify, _evidenceDetail } });
    }

    private Control BuildFindingTab()
    {
        var create = new Button { Content = "从选中证据创建 Finding" };
        create.Click += (_, _) => CreateFinding();
        var review = new Button { Content = "保存复核结论" };
        review.Click += (_, _) => ReviewFinding();
        var form = new StackPanel
        {
            Margin = new Thickness(12), Spacing = 7,
            Children =
            {
                Label("新建 Finding"), _findingTitle, Labeled("严重性", _severity), Labeled("置信度", _confidence),
                _findingDescription, create, new Separator(), Label("人工复核"), Labeled("状态", _reviewStatus),
                _reviewer, _reviewNote, review, new Separator(), Label("选中项"), _findingDetail
            }
        };
        return Split(_findings, form);
    }

    private Control BuildAuditTab()
    {
        var verify = new Button { Content = "验证完整审计链" };
        verify.Click += (_, _) => VerifyAudit();
        var panel = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { verify, _auditVerification } };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(Split(_audit, _auditDetail));
        return panel;
    }

    private Control BuildReportTab()
    {
        var json = new Button { Content = "生成 JSON" };
        json.Click += (_, _) => GenerateReport("json");
        var markdown = new Button { Content = "生成 Markdown" };
        markdown.Click += (_, _) => GenerateReport("markdown");
        var html = new Button { Content = "生成 HTML" };
        html.Click += (_, _) => GenerateReport("html");
        var copy = new Button { Content = "复制" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_report.Text ?? string.Empty);
                SetStatus("报告已复制。", false);
            }
        };
        var panel = new DockPanel { Margin = new Thickness(10) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { json, markdown, html, copy } };
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Top);
        panel.Children.Add(actions);
        panel.Children.Add(_report);
        return panel;
    }

    private void RefreshAll()
    {
        var selectedScope = (_scopes.SelectedItem as ScopeItem)?.Value.Id;
        var selectedPlan = (_plans.SelectedItem as PlanItem)?.Value.Id;
        var selectedApproval = (_approvals.SelectedItem as ApprovalItem)?.Value.Id;
        var scopes = _plane.Scopes.OrderByDescending(value => value.CreatedAt).Select(value => new ScopeItem(value)).ToArray();
        var plans = _plane.Plans.OrderByDescending(value => value.CreatedAt).Select(value => new PlanItem(value)).ToArray();
        var approvals = _plane.Approvals.OrderByDescending(value => value.ExpiresAt).Select(value => new ApprovalItem(value)).ToArray();
        _scopes.ItemsSource = scopes;
        _plans.ItemsSource = plans;
        _approvals.ItemsSource = approvals;
        _scopes.SelectedItem = scopes.FirstOrDefault(value => value.Value.Id == selectedScope) ?? scopes.FirstOrDefault();
        _plans.SelectedItem = plans.FirstOrDefault(value => value.Value.Id == selectedPlan) ?? plans.FirstOrDefault();
        _approvals.SelectedItem = approvals.FirstOrDefault(value => value.Value.Id == selectedApproval) ?? approvals.FirstOrDefault();
        var selected = SelectedJob?.Value.Id;
        var items = _plane.Jobs.OrderByDescending(value => value.CreatedAt).Select(value => new JobItem(value)).ToArray();
        _jobs.ItemsSource = items;
        _jobs.SelectedItem = items.FirstOrDefault(value => value.Value.Id == selected) ?? items.FirstOrDefault();
        if (items.Length == 0)
        {
            _summary.Text = "暂无评估任务。可由授权评估 CLI 或 Agent 创建范围、计划和审批后运行。";
            ClearJobViews();
        }
        else RefreshSelectedJob();
    }

    private void CreateScope()
    {
        try
        {
            var minutes = PositiveInt(_scopeMinutes.Text, "范围有效分钟", 1, 10_080);
            _plane.CreateScope(_scopeName.Text ?? string.Empty, _authorization.Text ?? string.Empty,
                _operator.Text ?? string.Empty, (_targets.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                DateTimeOffset.UtcNow.AddMinutes(minutes));
            RefreshAll();
            SetStatus("授权范围已创建。", false);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private void CreatePlan()
    {
        try
        {
            var scope = (_scopes.SelectedItem as ScopeItem)?.Value ?? throw new InvalidOperationException("请先选择授权范围。");
            var timeout = PositiveInt(_timeoutSeconds.Text, "超时秒数", 1, 120);
            _plane.CreatePlan(scope.Id, _planName.Text ?? string.Empty,
                [new AssessmentStep(_adapter.SelectedItem?.ToString() ?? AuthorizedToolCatalog.SimulationEcho, _stepInput.Text ?? string.Empty, timeout)],
                _operator.Text ?? string.Empty);
            RefreshAll();
            SetStatus("固定计划已创建并绑定范围哈希。", false);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private void ApprovePlan()
    {
        try
        {
            var plan = (_plans.SelectedItem as PlanItem)?.Value ?? throw new InvalidOperationException("请先选择计划。");
            var minutes = PositiveInt(_approvalMinutes.Text, "审批有效分钟", 1, 1_440);
            _plane.Approve(plan.Id, _operator.Text ?? string.Empty, DateTimeOffset.UtcNow.AddMinutes(minutes));
            RefreshAll();
            SetStatus("一次性审批票据已签发。", false);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private async System.Threading.Tasks.Task RunApprovedPlanAsync()
    {
        try
        {
            var approval = (_approvals.SelectedItem as ApprovalItem)?.Value ?? throw new InvalidOperationException("请先选择审批票据。");
            SetStatus("任务执行中…", false);
            var job = await _plane.StartAsync(approval.PlanId, approval.Id, _operator.Text ?? string.Empty);
            RefreshAll();
            _jobs.SelectedItem = (_jobs.ItemsSource as JobItem[])?.FirstOrDefault(value => value.Value.Id == job.Id);
            SetStatus($"任务结束：{job.Status}。", job.Status is AssessmentJobStatus.Failed or AssessmentJobStatus.Cancelled);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private void CancelSelectedJob()
    {
        var job = SelectedJob?.Value;
        if (job is null) { SetStatus("请先选择任务。", true); return; }
        SetStatus(_plane.Cancel(job.Id, _operator.Text ?? string.Empty, "workbench cancellation") ? "已请求取消任务。" : "任务无法取消。", false);
        RefreshAll();
    }

    private void RevokeScope()
    {
        var scope = (_scopes.SelectedItem as ScopeItem)?.Value;
        if (scope is null) { SetStatus("请先选择范围。", true); return; }
        SetStatus(_plane.RevokeScope(scope.Id, _operator.Text ?? string.Empty, "workbench revocation") ? "范围已撤销。" : "范围无法撤销。", false);
        RefreshAll();
    }

    private void RevokeApproval()
    {
        var approval = (_approvals.SelectedItem as ApprovalItem)?.Value;
        if (approval is null) { SetStatus("请先选择审批。", true); return; }
        SetStatus(_plane.RevokeApproval(approval.Id, _operator.Text ?? string.Empty, "workbench revocation") ? "审批已撤销。" : "审批无法撤销。", false);
        RefreshAll();
    }

    private void RefreshSelectedJob()
    {
        var job = SelectedJob?.Value;
        if (job is null) { ClearJobViews(); return; }
        var scope = _plane.Scopes.FirstOrDefault(value => value.Id == job.ScopeId);
        var plan = _plane.Plans.FirstOrDefault(value => value.Id == job.PlanId);
        _summary.Text = $"{job.Status} · 范围 {scope?.Name ?? job.ScopeId} · 计划 {plan?.Name ?? job.PlanId} · 请求人 {job.RequestedBy}";
        _evidence.ItemsSource = _plane.Evidence(job.Id).OrderByDescending(value => value.Timestamp).Select(value => new EvidenceItem(value)).ToArray();
        _findings.ItemsSource = _plane.Findings(job.Id).OrderByDescending(value => value.CreatedAt).Select(value => new FindingItem(value)).ToArray();
        _audit.ItemsSource = _plane.AuditForEntity(job.Id, 500).Select(value => new AuditItem(value)).ToArray();
        _evidenceDetail.Text = string.Empty;
        _findingDetail.Text = string.Empty;
        VerifyAudit();
    }

    private void ClearJobViews()
    {
        _evidence.ItemsSource = Array.Empty<EvidenceItem>();
        _findings.ItemsSource = Array.Empty<FindingItem>();
        _audit.ItemsSource = Array.Empty<AuditItem>();
        _evidenceDetail.Text = _findingDetail.Text = _report.Text = string.Empty;
    }

    private void ShowEvidence()
    {
        if (_evidence.SelectedItem is not EvidenceItem item) return;
        _evidenceDetail.Text = $"ID: {item.Value.Id}{Environment.NewLine}来源: {item.Value.Source}{Environment.NewLine}时间: {item.Value.Timestamp:O}{Environment.NewLine}SHA-256: {item.Value.Sha256}{Environment.NewLine}已脱敏: {item.Value.Redacted}{Environment.NewLine}{Environment.NewLine}{item.Value.Content}";
    }

    private void ShowFinding()
    {
        if (_findings.SelectedItem is not FindingItem item) return;
        var value = item.Value;
        _findingDetail.Text = $"ID: {value.Id}{Environment.NewLine}证据: {value.EvidenceId}{Environment.NewLine}严重性: {value.Severity}{Environment.NewLine}置信度: {value.Confidence}{Environment.NewLine}状态: {value.Status}{Environment.NewLine}复核人: {value.ReviewedBy}{Environment.NewLine}复核时间: {value.ReviewedAt:O}{Environment.NewLine}说明: {value.Description}{Environment.NewLine}复核意见: {value.ReviewNote}";
        if (Enum.TryParse<AssessmentFindingStatus>(value.Status, true, out var status)) _reviewStatus.SelectedItem = status;
        _reviewer.Text = value.ReviewedBy ?? string.Empty;
        _reviewNote.Text = value.ReviewNote ?? string.Empty;
    }

    private void VerifySelectedEvidence()
    {
        if (_evidence.SelectedItem is not EvidenceItem item) { SetStatus("请先选择证据。", true); return; }
        var result = _plane.VerifyEvidence(item.Value.Id);
        SetStatus(result.Valid ? $"证据 {result.EvidenceId} 完整性有效。" : $"证据验证失败：{result.ErrorCode}", !result.Valid);
    }

    private void ShowAudit()
    {
        if (_audit.SelectedItem is not AuditItem item) return;
        var value = item.Value;
        _auditDetail.Text = $"ID: {value.Id}{Environment.NewLine}时间: {value.Timestamp:O}{Environment.NewLine}操作者: {value.Actor}{Environment.NewLine}动作: {value.Action}{Environment.NewLine}实体: {value.EntityId}{Environment.NewLine}详情: {value.Detail}{Environment.NewLine}前序哈希: {value.PreviousHash}{Environment.NewLine}条目哈希: {value.IntegrityHash}";
    }

    private void VerifyAudit()
    {
        var result = _plane.VerifyAudit();
        _auditVerification.Text = result.Valid ? $"完整性有效，共 {result.CheckedEntries} 条。" : $"验证失败：{result.ErrorCode}，条目 {result.EntryId}";
        _auditVerification.Foreground = result.Valid ? Brushes.SeaGreen : Brushes.IndianRed;
    }

    private void CreateFinding()
    {
        try
        {
            var job = SelectedJob?.Value ?? throw new InvalidOperationException("请先选择任务。");
            var evidence = (_evidence.SelectedItem as EvidenceItem)?.Value ?? _plane.Evidence(job.Id).FirstOrDefault()
                ?? throw new InvalidOperationException("当前任务没有证据。");
            _plane.CreateFinding(job.Id, evidence.Id, _findingTitle.Text ?? string.Empty, _findingDescription.Text ?? string.Empty,
                _severity.SelectedItem?.ToString() ?? "Medium", _confidence.SelectedItem?.ToString() ?? "Medium",
                string.IsNullOrWhiteSpace(_reviewer.Text) ? "workbench" : _reviewer.Text!);
            _findingTitle.Text = _findingDescription.Text = string.Empty;
            RefreshSelectedJob();
            SetStatus("Finding 已创建并写入审计链。", false);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private void ReviewFinding()
    {
        try
        {
            var finding = (_findings.SelectedItem as FindingItem)?.Value ?? throw new InvalidOperationException("请先选择 Finding。");
            var actor = (_reviewer.Text ?? string.Empty).Trim();
            if (_reviewStatus.SelectedItem is not AssessmentFindingStatus status) throw new InvalidOperationException("请选择复核状态。");
            _plane.ReviewFinding(finding.Id, status, actor, _reviewNote.Text ?? string.Empty);
            RefreshSelectedJob();
            SetStatus("复核结论已保存并写入审计链。", false);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private void GenerateReport(string format)
    {
        try
        {
            var job = SelectedJob?.Value ?? throw new InvalidOperationException("请先选择任务。");
            _report.Text = _plane.ExportReport(job.Id, format);
            SetStatus($"已生成 {format.ToUpperInvariant()} 报告。", false);
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private void SetStatus(string message, bool error)
    {
        _status.Text = message;
        _status.Foreground = error ? Brushes.IndianRed : Brushes.SeaGreen;
    }

    private JobItem? SelectedJob => _jobs.SelectedItem as JobItem;
    private static int PositiveInt(string? text, string name, int min, int max) =>
        int.TryParse(text, out var value) && value >= min && value <= max ? value : throw new ArgumentException($"{name}必须在 {min} 到 {max} 之间。");
    private static TabItem Tab(string title, Control content) => new() { Header = title, Content = content };
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };
    private static Control Labeled(string label, Control editor) => new StackPanel { Spacing = 3, Children = { Label(label), editor } };
    private static TextBox DetailBox() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private static Grid Split(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*") };
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 1);
        grid.Children.Add(left); grid.Children.Add(right);
        return grid;
    }

    private sealed record JobItem(AssessmentJob Value)
    {
        public override string ToString() => $"{Value.CreatedAt:MM-dd HH:mm} · {Value.Status} · {Value.Id[..Math.Min(8, Value.Id.Length)]}";
    }
    private sealed record ScopeItem(AssessmentScope Value)
    {
        public override string ToString() => $"{Value.Name} · {(Value.Revoked ? "已撤销" : $"至 {Value.ExpiresAt:MM-dd HH:mm}")} · {string.Join(',', Value.Targets)}";
    }
    private sealed record PlanItem(AssessmentPlan Value)
    {
        public override string ToString() => $"{Value.Name} · {Value.Steps.Count} 步 · {Value.Id[..Math.Min(8, Value.Id.Length)]}";
    }
    private sealed record ApprovalItem(AssessmentApproval Value)
    {
        public override string ToString() => $"{Value.Id[..Math.Min(8, Value.Id.Length)]} · {(Value.Revoked ? "已撤销" : Value.ConsumedAt is not null ? "已使用" : $"至 {Value.ExpiresAt:HH:mm}")}";
    }
    private sealed record EvidenceItem(AssessmentEvidence Value)
    {
        public override string ToString() => $"{Value.Timestamp:HH:mm:ss} · {Value.Source} · {Value.Id[..Math.Min(8, Value.Id.Length)]}";
    }
    private sealed record FindingItem(AssessmentFinding Value)
    {
        public override string ToString() => $"[{Value.Severity}] {Value.Title} · {Value.Status}";
    }
    private sealed record AuditItem(AssessmentAuditEntry Value)
    {
        public override string ToString() => $"{Value.Timestamp:HH:mm:ss} · {Value.Actor} · {Value.Action} · {Value.EntityId}";
    }
}
