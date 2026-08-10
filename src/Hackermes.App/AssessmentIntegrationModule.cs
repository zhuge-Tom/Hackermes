using Hackermes.AiPanel.Tools;
using Hackermes.Assessment;
using Hackermes.Automation.Commands;
using Hackermes.Base;
using Hackermes.App.Views;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
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
        services.AddSingleton<ToolLaunchService>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var plane = serviceProvider.GetRequiredService<IAssessmentControlPlane>();
        var settings = serviceProvider.GetRequiredService<ISettingsService>();
        var launcher = serviceProvider.GetRequiredService<ToolLaunchService>();
        RegisterCli(serviceProvider.GetRequiredService<CommandRegistry>(), plane);
        RegisterAgent(serviceProvider.GetRequiredService<IAiToolRegistry>(), plane);
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

    public static void RegisterCli(CommandRegistry commands, IAssessmentControlPlane plane)
    {
        commands.Register(new CommandDefinition
        {
            Name = "assessment",
            Summary = "Manage authorized scopes, plans and isolated ToolHost jobs",
            Usage = "assessment scope|plan|approve|run|cancel|jobs|evidence|finding|audit|report ...",
            IsMutating = true,
            Handler = async (context, token) => await ExecuteCliAsync(context, plane, token).ConfigureAwait(false)
        });
    }

    private static async Task<CommandResult> ExecuteCliAsync(CommandContext context, IAssessmentControlPlane plane, System.Threading.CancellationToken ct)
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
                default:
                    return CommandResult.Fail("Usage: assessment tools | scope create <name> <authorization> <operator> <targets> [minutes] | plan create <scope> <name> <adapter> [seconds] <json> | approve/run/revoke/jobs | evidence <job> | evidence-verify <evidence> | findings <job> | finding create|review ... | audit <entity>|verify | report <job> [json|markdown|html]");
            }
        }
        catch (Exception exception) { return CommandResult.Fail(exception.Message); }
    }

    public static void RegisterAgent(IAiToolRegistry registry, IAssessmentControlPlane plane)
    {
        registry.Register(new AiToolDefinition("assessment_scopes", "List authorized assessment scopes.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(plane.Scopes)))));
        registry.Register(new AiToolDefinition("assessment_tools", "List bounded ToolHost adapters and local availability.", Schema(new { }), AiToolRisk.ReadOnly,
            (_, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(AuthorizedToolCatalog.Describe())))));
        registry.Register(new AiToolDefinition("assessment_create_scope", "Create an exact, time-bounded authorized target scope.", Schema(new { name = new { type = "string" }, authorization = new { type = "string" }, operatorId = new { type = "string" }, targets = new { type = "array", items = new { type = "string" } }, minutes = new { type = "integer" } }), AiToolRisk.Mutating,
            (call, _) => ValueTask.FromResult(CreateScope(plane, call.Arguments))));
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
    }

    private static ToolResult CreateScope(IAssessmentControlPlane plane, JsonElement args) => Try(() => plane.CreateScope(Text(args, "name"), Text(args, "authorization"), Text(args, "operatorId"), Strings(args, "targets"), DateTimeOffset.UtcNow.AddMinutes(Number(args, "minutes", 60))));
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
