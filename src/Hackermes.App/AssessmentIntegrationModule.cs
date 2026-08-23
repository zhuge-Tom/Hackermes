using Hackermes.AiPanel.Tools;
using Hackermes.Assessment;
using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using Hackermes.Base;
using Hackermes.App.Views;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Hackermes.App;

/// <summary>Registers the same authorized-assessment control plane for the in-app CLI and Agent.</summary>
public sealed class AssessmentIntegrationModule : IModule
{
    public string Name => "Assessment Integration";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ToolHostTicketSigner>();
        services.AddSingleton<IAssessmentExecutionHost, ExternalToolHost>();
        services.AddSingleton<IAssessmentControlPlane, AssessmentControlPlane>();
        services.AddSingleton<IAssessmentReportSigningKey>(serviceProvider =>
            (PacketAuditSigningKey)serviceProvider.GetRequiredService<IPacketAuditSigningKey>());
        services.AddSingleton<IAssessmentReportExportService>(serviceProvider =>
            new AssessmentReportExportService(
                serviceProvider.GetRequiredService<IAssessmentControlPlane>(),
                serviceProvider.GetRequiredService<IAssessmentReportSigningKey>(),
                serviceProvider.GetRequiredService<IAssessmentReportTrustPolicy>()));
        services.AddSingleton<ToolLaunchService>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var plane = serviceProvider.GetRequiredService<IAssessmentControlPlane>();
        var settings = serviceProvider.GetRequiredService<ISettingsService>();
        var launcher = serviceProvider.GetRequiredService<ToolLaunchService>();
        var reports = serviceProvider.GetRequiredService<IAssessmentReportExportService>();
        RegisterCli(serviceProvider.GetRequiredService<CommandRegistry>(), plane, reports);
        RegisterAgent(serviceProvider.GetRequiredService<IAiToolRegistry>(), plane,
            serviceProvider.GetRequiredService<IPageContextQueryService>(), reports);
        AuthorizedToolsView? toolsView = null;
        serviceProvider.GetRequiredService<IDockLayoutRegistry>().RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Left, TabId = "security-tools", Title = "安全工具",
            IconKey = "SemiIconFolder", IsClosable = false, Order = 0,
            HeaderActionCommand = new ActionCommand(() => OpenSecurityToolsSettings(settings, () => toolsView?.RefreshCatalog())),
            HeaderActionToolTip = "安全工具配置",
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "security-tools", Title = "安全工具",
                Content = toolsView = new AuthorizedToolsView(settings, launcher)
            }
        });
        serviceProvider.GetRequiredService<IDockLayoutRegistry>().RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content, TabId = "authorized-assessment", Title = "授权评估",
            IconKey = "SemiIconFolder", IsClosable = true, Order = 20,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "authorized-assessment", Title = "授权评估",
                Content = new AssessmentWorkspaceView(plane)
            }
        });
    }

    private static void OpenSecurityToolsSettings(ISettingsService settings, Action refresh)
    {
        var dialog = new SecurityToolsSettingsWindow(settings);
        dialog.Closed += (_, _) => refresh();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            _ = dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    private sealed class ActionCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    public static void RegisterCli(CommandRegistry commands, IAssessmentControlPlane plane,
        IAssessmentReportExportService? reports = null)
    {
        commands.Register(new CommandDefinition
        {
            Name = "assessment",
            Summary = "Manage authorized scopes, plans and isolated ToolHost jobs",
            Usage = "assessment scope|plan|approve|run|cancel|jobs|evidence|finding|audit|report|report-export|report-verify ...",
            IsMutating = true,
            Handler = async (context, token) => await ExecuteCliAsync(context, plane, reports, token).ConfigureAwait(false)
        });
    }

    private static async Task<CommandResult> ExecuteCliAsync(CommandContext context, IAssessmentControlPlane plane,
        IAssessmentReportExportService? reports, System.Threading.CancellationToken ct)
    {
        try
        {
            switch (context.Arg(0)?.ToLowerInvariant())
            {
                case "tools":
                    return CommandResult.Ok(string.Join(Environment.NewLine, AuthorizedToolCatalog.Describe().Select(value => $"{value.Id} [{value.Category}] available={value.Available} {value.Name}")));
                case "scope" when context.Arg(1)?.Equals("create", StringComparison.OrdinalIgnoreCase) == true:
                    var scope = plane.CreateScope(Required(context, 2, "name"), Required(context, 3, "authorization"), Required(context, 4, "operator"),
                        Required(context, 5, "targets").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), DateTimeOffset.UtcNow.AddMinutes(Int(context, 6, 60)));
                    return CommandResult.Ok($"scope={scope.Id}");
                case "scope":
                    return CommandResult.Ok(string.Join(Environment.NewLine, plane.Scopes.Select(value => $"{value.Id} {value.Name} expires={value.ExpiresAt:O} revoked={value.Revoked}")));
                case "plan" when context.Arg(1)?.Equals("create", StringComparison.OrdinalIgnoreCase) == true:
                    var adapter = Required(context, 4, "adapter");
                    var hasTimeout = int.TryParse(context.Arg(5), out var timeout);
                    var plan = plane.CreatePlan(Required(context, 2, "scope"), Required(context, 3, "name"), [new AssessmentStep(adapter, context.Rest(hasTimeout ? 6 : 5), hasTimeout ? timeout : 30)], "cli");
                    return CommandResult.Ok($"plan={plan.Id} hash={plan.VersionHash}");
                case "plan":
                    return CommandResult.Ok(string.Join(Environment.NewLine, plane.Plans.Select(value => $"{value.Id} scope={value.ScopeId} steps={value.Steps.Count} {value.Name}")));
                case "approve":
                    var approval = plane.Approve(Required(context, 1, "plan"), Required(context, 2, "operator"), DateTimeOffset.UtcNow.AddMinutes(Int(context, 3, 30)));
                    return CommandResult.Ok($"approval={approval.Id}");
                case "run":
                    var job = await plane.StartAsync(Required(context, 1, "plan"), Required(context, 2, "approval"), Required(context, 3, "operator"), ct).ConfigureAwait(false);
                    return CommandResult.Ok($"job={job.Id} status={job.Status}");
                case "cancel":
                    return CommandResult.Ok(plane.Cancel(Required(context, 1, "job"), Required(context, 2, "operator"), context.Rest(3)) ? "cancelled" : "not-cancelled");
                case "revoke" when context.Arg(1)?.Equals("approval", StringComparison.OrdinalIgnoreCase) == true:
                    return CommandResult.Ok(plane.RevokeApproval(Required(context, 2, "approval"), Required(context, 3, "operator"), context.Rest(4)) ? "revoked" : "not-revoked");
                case "revoke" when context.Arg(1)?.Equals("scope", StringComparison.OrdinalIgnoreCase) == true:
                    return CommandResult.Ok(plane.RevokeScope(Required(context, 2, "scope"), Required(context, 3, "operator"), context.Rest(4)) ? "revoked" : "not-revoked");
                case "jobs":
                    return CommandResult.Ok(string.Join(Environment.NewLine, plane.Jobs.Select(value => $"{value.Id} {value.Status} scope={value.ScopeId} failure={value.Failure}")));
                case "cases":
                    return CommandResult.Ok(string.Join(Environment.NewLine, plane.ReadCases().Select(value =>
                        $"{value.Job.Id} {value.Job.Status} scope={value.Scope.Name} plan={value.Plan.Name} " +
                        $"canCancel={value.AvailableActions.CanCancelJob} canExport={value.AvailableActions.CanExportReport}")));
                case "evidence":
                    return CommandResult.Ok(string.Join(Environment.NewLine, plane.Evidence(Required(context, 1, "job")).Select(value => $"{value.Id} {value.Source} {value.Sha256} {value.Content}")));
                case "evidence-verify":
                    return CommandResult.Ok(JsonSerializer.Serialize(plane.VerifyEvidence(Required(context, 1, "evidence"))));
                case "finding" when context.Arg(1)?.Equals("create", StringComparison.OrdinalIgnoreCase) == true:
                    var finding = plane.CreateFinding(Required(context, 2, "job"), Required(context, 3, "evidence"),
                        Required(context, 4, "title"), Required(context, 7, "description"), Required(context, 5, "severity"),
                        Required(context, 6, "confidence"), "cli");
                    return CommandResult.Ok($"finding={finding.Id} status={finding.Status}");
                case "finding" when context.Arg(1)?.Equals("review", StringComparison.OrdinalIgnoreCase) == true:
                    if (!Enum.TryParse<AssessmentFindingStatus>(Required(context, 3, "status"), true, out var reviewStatus))
                        return CommandResult.Fail("Invalid finding review status.");
                    var reviewed = plane.ReviewFinding(Required(context, 2, "finding"), reviewStatus,
                        Required(context, 4, "reviewer"), context.Rest(5));
                    return CommandResult.Ok($"finding={reviewed.Id} status={reviewed.Status} reviewer={reviewed.ReviewedBy}");
                case "findings":
                    return CommandResult.Ok(string.Join(Environment.NewLine, plane.Findings(Required(context, 1, "job"))
                        .Select(value => $"{value.Id} [{value.Severity}] {value.Status} evidence={value.EvidenceId} {value.Title}")));
                case "audit" when context.Arg(1)?.Equals("verify", StringComparison.OrdinalIgnoreCase) == true:
                    return CommandResult.Ok(JsonSerializer.Serialize(plane.VerifyAudit()));
                case "audit":
                    return CommandResult.Ok(string.Join(Environment.NewLine, plane.AuditForEntity(Required(context, 1, "entity"), Int(context, 2, 100))
                        .Select(value => $"{value.Timestamp:O} {value.Actor} {value.Action} {value.EntityId} {value.Detail}")));
                case "report":
                    return CommandResult.Ok(plane.ExportReport(Required(context, 1, "job"), context.Arg(2) ?? "json"));
                case "report-export" when reports is not null:
                {
                    var path = Required(context, 1, "path");
                    var content = reports.Export(Required(context, 2, "job"));
                    await System.IO.File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
                    return CommandResult.Ok($"Exported signed assessment report to {System.IO.Path.GetFullPath(path)}.");
                }
                case "report-verify" when reports is not null:
                    return VerifyReportFile(reports, context);
                case "report-export" or "report-verify":
                    return CommandResult.Fail("This assessment backend does not support signed report exports.");
                default:
                    return CommandResult.Fail("Usage: assessment tools | scope create <name> <authorization> <operator> <targets> [minutes] | plan create <scope> <name> <adapter> [seconds] <json> | approve/run/revoke/jobs | evidence <job> | evidence-verify <evidence> | findings <job> | finding create|review ... | audit <entity>|verify | report <job> [json|markdown|html] | report-export <path> <job> | report-verify <path> [keyId]");
            }
        }
        catch (Exception exception) { return CommandResult.Fail(exception.Message); }
    }

    private static CommandResult VerifyReportFile(IAssessmentReportExportService reports, CommandContext context)
    {
        var path = Required(context, 1, "path");
        if (new System.IO.FileInfo(path).Length > AssessmentReportExportService.MaximumContentBytes)
            return CommandResult.Fail($"Signed report exceeds {AssessmentReportExportService.MaximumContentBytes} bytes.");
        var verification = reports.Verify(System.IO.File.ReadAllText(path), context.Arg(2));
        var output = $"valid={verification.Valid.ToString().ToLowerInvariant()}\tkeyId={verification.KeyId ?? "-"}\t" +
            $"job={verification.JobId ?? "-"}\texportedAt={verification.ExportedAt?.ToString("O") ?? "-"}\t" +
            $"error={verification.ErrorCode ?? "-"}";
        return verification.Valid ? CommandResult.Ok(output) : CommandResult.Fail(output);
    }

    public static void RegisterAgent(IAiToolRegistry registry, IAssessmentControlPlane plane,
        IPageContextQueryService? pageContexts = null, IAssessmentReportExportService? reports = null)
    {
        registry.Register(new AiToolDefinition("assessment_scopes", "List authorized assessment scopes.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(plane.Scopes)))));
        registry.Register(new AiToolDefinition("assessment_cases", "List coherent authorized assessment cases and their currently available actions.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(plane.ReadCases())))));
        registry.Register(new AiToolDefinition("assessment_tools", "List bounded ToolHost adapters and local availability.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(AuthorizedToolCatalog.Describe())))));
        registry.Register(new AiToolDefinition("assessment_create_scope", "Create an exact, time-bounded authorized target scope when no browser page is attached. Browser-bound sessions must use assessment_create_scope_from_page.", Schema(new { name = new { type = "string" }, authorization = new { type = "string" }, operatorId = new { type = "string" }, targets = new { type = "array", items = new { type = "string" } }, minutes = new { type = "integer" } }), AiToolRisk.Mutating,
            (call, _) => ValueTask.FromResult(call.PageId is null
                ? CreateScope(plane, call.Arguments)
                : ToolResult.Fail("A browser page is attached. Use assessment_create_scope_from_page so the model cannot substitute another target."))));
        if (pageContexts is not null)
        {
            registry.Register(new AiToolDefinition("assessment_create_scope_from_page", "Create an exact, time-bounded authorized scope for the attached browser page. The target is derived from the page URL; no target argument is accepted.", Schema(new
            {
                name = new { type = "string" }, authorization = new { type = "string" },
                operatorId = new { type = "string" }, minutes = new { type = "integer" }
            }), AiToolRisk.Mutating,
                (call, _) => ValueTask.FromResult(CreateScopeFromPage(plane, pageContexts, call)),
                (call, _) => ValueTask.FromResult(PrepareScopeFromPage(pageContexts, call))));
        }
        registry.Register(new AiToolDefinition("assessment_create_plan", "Create a bounded plan using a registered ToolHost adapter.", Schema(new { scopeId = new { type = "string" }, name = new { type = "string" }, adapterId = new { type = "string" }, input = new { type = "string", description = "Structured JSON; arbitrary commands are rejected." }, timeoutSeconds = new { type = "integer" } }), AiToolRisk.Mutating,
            (call, _) => ValueTask.FromResult(CreatePlan(plane, call.Arguments))));
        registry.Register(new AiToolDefinition("assessment_approve", "Request an approval grant for an unchanged assessment plan.", Schema(new { planId = new { type = "string" }, operatorId = new { type = "string" }, minutes = new { type = "integer" } }), AiToolRisk.Mutating,
            (call, _) => ValueTask.FromResult(Approve(plane, call.Arguments))));
        registry.Register(new AiToolDefinition("assessment_run", "Run an approved local simulation plan and return its job status.", Schema(new { planId = new { type = "string" }, approvalId = new { type = "string" }, operatorId = new { type = "string" } }), AiToolRisk.Dangerous,
            (call, token) => RunAsync(plane, call.Arguments, token)));
        registry.Register(new AiToolDefinition("assessment_report", "Read the redacted JSON, Markdown or HTML report for an assessment job.", Schema(new
        {
            jobId = new { type = "string" }, format = new { type = "string", @enum = new[] { "json", "markdown", "html" } }
        }), AiToolRisk.ReadOnly,
            (call, _) => ValueTask.FromResult(ReadReport(plane, call.Arguments))));
        registry.Register(new AiToolDefinition("assessment_evidence", "List redacted evidence for one assessment job.", Schema(new { jobId = new { type = "string" } }), AiToolRisk.ReadOnly,
            (call, _) => ValueTask.FromResult(Try(() => plane.Evidence(Text(call.Arguments, "jobId"))))));
        registry.Register(new AiToolDefinition("assessment_verify_evidence", "Verify one evidence item's SHA-256 integrity.", Schema(new { evidenceId = new { type = "string" } }), AiToolRisk.ReadOnly,
            (call, _) => ValueTask.FromResult(Try(() => plane.VerifyEvidence(Text(call.Arguments, "evidenceId"))))));
        registry.Register(new AiToolDefinition("assessment_findings", "List findings and human-review state for one assessment job.", Schema(new { jobId = new { type = "string" } }), AiToolRisk.ReadOnly,
            (call, _) => ValueTask.FromResult(Try(() => plane.Findings(Text(call.Arguments, "jobId"))))));
        registry.Register(new AiToolDefinition("assessment_create_finding", "Create a bounded finding linked to existing evidence; it remains unreviewed.", Schema(new
        {
            jobId = new { type = "string" }, evidenceId = new { type = "string" }, title = new { type = "string" },
            description = new { type = "string" }, severity = new { type = "string", @enum = new[] { "Critical", "High", "Medium", "Low", "Info" } },
            confidence = new { type = "string", @enum = new[] { "High", "Medium", "Low" } }
        }), AiToolRisk.Mutating, (call, _) => ValueTask.FromResult(CreateFinding(plane, call.Arguments))));
        registry.Register(new AiToolDefinition("assessment_review_finding", "Record an attributed review decision for a finding.", Schema(new
        {
            findingId = new { type = "string" }, status = new { type = "string", @enum = Enum.GetNames<AssessmentFindingStatus>() },
            reviewer = new { type = "string" }, note = new { type = "string" }
        }), AiToolRisk.Mutating, (call, _) => ValueTask.FromResult(ReviewFinding(plane, call.Arguments))));
        registry.Register(new AiToolDefinition("assessment_verify_audit", "Verify the append-only assessment audit hash chain.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(Try(plane.VerifyAudit))));
        if (reports is not null)
        {
            registry.Register(new AiToolDefinition("assessment_report_export",
                "Export the signed ECDSA assessment report document for one job. Requires confirmation.",
                Schema(new { jobId = new { type = "string" } }), AiToolRisk.Dangerous,
                (call, _) => ValueTask.FromResult(ExportReport(reports, call.Arguments))));
            registry.Register(new AiToolDefinition("assessment_report_verify",
                "Verify a signed assessment report document offline through its embedded public key.",
                Schema(new { content = new { type = "string" }, expectedKeyId = new { type = "string" } }), AiToolRisk.ReadOnly,
                (call, _) => ValueTask.FromResult(VerifyReport(reports, call.Arguments))));
        }
    }

    private static ToolResult ExportReport(IAssessmentReportExportService reports, JsonElement args)
    {
        try { return ToolResult.Ok(reports.Export(Text(args, "jobId"))); }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private static ToolResult VerifyReport(IAssessmentReportExportService reports, JsonElement args)
    {
        var expectedKeyId = Text(args, "expectedKeyId");
        var verification = reports.Verify(Text(args, "content"),
            expectedKeyId.Length > 0 ? expectedKeyId : null);
        var json = JsonSerializer.Serialize(verification);
        return verification.Valid ? ToolResult.Ok(json) : ToolResult.Fail(json);
    }

    private static ToolResult CreateScope(IAssessmentControlPlane plane, JsonElement args) => Try(() => plane.CreateScope(Text(args, "name"), Text(args, "authorization"), Text(args, "operatorId"), Strings(args, "targets"), DateTimeOffset.UtcNow.AddMinutes(Number(args, "minutes", 60))));
    private const string PageBindingArgument = "__hackermesPageBinding";

    private static ToolInvocation PrepareScopeFromPage(IPageContextQueryService pageContexts, ToolInvocation call)
    {
        if (string.IsNullOrWhiteSpace(call.PageId)) throw new InvalidOperationException("No active browser page is attached.");
        var page = pageContexts.Read(call.PageId) ??
            throw new InvalidOperationException("The attached browser page is unavailable or has been closed.");
        var binding = ReadPageBinding(page);
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool arguments must be a JSON object.");

        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in call.Arguments.EnumerateObject())
            if (!string.Equals(property.Name, PageBindingArgument, StringComparison.Ordinal))
                arguments[property.Name] = property.Value.Clone();
        arguments[PageBindingArgument] = JsonSerializer.SerializeToElement(binding);
        return call with { Arguments = JsonSerializer.SerializeToElement(arguments) };
    }

    private static ToolResult CreateScopeFromPage(IAssessmentControlPlane plane, IPageContextQueryService pageContexts, ToolInvocation call)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(call.PageId) ||
                !call.Arguments.TryGetProperty(PageBindingArgument, out var bindingJson))
                return ToolResult.Fail("The browser target was not frozen before authorization.");
            var frozen = bindingJson.Deserialize<BrowserScopeBinding>() ??
                throw new InvalidOperationException("The frozen browser target is invalid.");
            if (!string.Equals(frozen.PageId, call.PageId, StringComparison.Ordinal))
                return ToolResult.Fail("The frozen browser target does not match the attached page.");
            var page = pageContexts.Read(call.PageId);
            if (page is null) return ToolResult.Fail("The attached browser page is unavailable or has been closed.");
            var current = ReadPageBinding(page);
            if (current != frozen)
                return ToolResult.Fail("The attached page navigated after authorization. Review and approve the new target.");

            var scope = plane.CreateScope(Text(call.Arguments, "name"), Text(call.Arguments, "authorization"),
                Text(call.Arguments, "operatorId"), [frozen.Target],
                DateTimeOffset.UtcNow.AddMinutes(Number(call.Arguments, "minutes", 60)));
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                Scope = scope, frozen.PageId, frozen.Origin, frozen.Target,
                frozen.Scheme, frozen.Port
            }));
        }
        catch (Exception exception) { return ToolResult.Fail(exception.Message); }
    }

    private static BrowserScopeBinding ReadPageBinding(PageContextObservation page)
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.IdnHost) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The attached page must have an absolute HTTP(S) URL without user information.");
        var target = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (target.Length == 0) throw new ArgumentException("The attached page host is invalid.");
        var host = target.Contains(':', StringComparison.Ordinal) ? $"[{target}]" : target;
        var origin = $"{uri.Scheme}://{host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";
        return new BrowserScopeBinding(page.PageId, origin, target, uri.Scheme, uri.Port);
    }

    private sealed record BrowserScopeBinding(string PageId, string Origin, string Target, string Scheme, int Port);
    private static ToolResult CreatePlan(IAssessmentControlPlane plane, JsonElement args) => Try(() => plane.CreatePlan(Text(args, "scopeId"), Text(args, "name"), [new AssessmentStep(Text(args, "adapterId"), Text(args, "input"), Number(args, "timeoutSeconds", 30))], "agent"));
    private static ToolResult Approve(IAssessmentControlPlane plane, JsonElement args) => Try(() => plane.Approve(Text(args, "planId"), Text(args, "operatorId"), DateTimeOffset.UtcNow.AddMinutes(Number(args, "minutes", 30))));
    private static async ValueTask<ToolResult> RunAsync(IAssessmentControlPlane plane, JsonElement args, System.Threading.CancellationToken ct) { try { var job = await plane.StartAsync(Text(args, "planId"), Text(args, "approvalId"), Text(args, "operatorId"), ct).ConfigureAwait(false); return ToolResult.Ok(JsonSerializer.Serialize(job)); } catch (Exception ex) { return ToolResult.Fail(ex.Message); } }
    private static ToolResult ReadReport(IAssessmentControlPlane plane, JsonElement args) => Try(() => plane.ExportReport(Text(args, "jobId"), Text(args, "format") is { Length: > 0 } format ? format : "json"));
    private static ToolResult CreateFinding(IAssessmentControlPlane plane, JsonElement args) => Try(() => plane.CreateFinding(
        Text(args, "jobId"), Text(args, "evidenceId"), Text(args, "title"), Text(args, "description"),
        Text(args, "severity"), Text(args, "confidence"), "agent"));
    private static ToolResult ReviewFinding(IAssessmentControlPlane plane, JsonElement args) => Try(() => plane.ReviewFinding(
        Text(args, "findingId"), Enum.Parse<AssessmentFindingStatus>(Text(args, "status"), true),
        Text(args, "reviewer"), Text(args, "note")));
    private static ToolResult Try<T>(Func<T> value) { try { return ToolResult.Ok(JsonSerializer.Serialize(value())); } catch (Exception ex) { return ToolResult.Fail(ex.Message); } }
    private static JsonElement Schema(object properties) => JsonSerializer.SerializeToElement(new { type = "object", properties, additionalProperties = false });
    private static string Text(JsonElement args, string name) => args.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;
    private static int Number(JsonElement args, string name, int fallback) => args.TryGetProperty(name, out var item) && item.TryGetInt32(out var value) ? value : fallback;
    private static string[] Strings(JsonElement args, string name) => args.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? string.Empty).ToArray() : [];
    private static string Required(CommandContext context, int index, string name) => context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
    private static int Int(CommandContext context, int index, int fallback) => int.TryParse(context.Arg(index), out var value) ? value : fallback;
}
