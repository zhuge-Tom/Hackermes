using Hackermes.AiPanel.Tools;
using Hackermes.Automation.Commands;
using Hackermes.Platform.Models;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Agent;

/// <summary>Human CLI management surface for the same Agent state used by the settings window.</summary>
public static class AgentWorkflowCommandRegistrar
{
    public static void Register(CommandRegistry commands, ISettingsService settings, DefaultToolPolicyGate policy,
        IAgentSkillStore skills, IAgentMemoryStore memory, IAgentArtifactStore artifacts)
    {
        commands.Register(new CommandDefinition
        {
            Name = "agent",
            Summary = "Inspect or configure Agent permissions, Skill workflows, memory and cached artifacts",
            Usage = "agent status|mode <request|help|full>|skills|memory [show|clear]|download <https-url> [file] [sha256]",
            IsMutating = true,
            Handler = async (context, token) => await ExecuteAsync(context, settings, policy, skills, memory, artifacts, token).ConfigureAwait(false)
        });
    }

    private static async Task<CommandResult> ExecuteAsync(CommandContext context, ISettingsService settings, DefaultToolPolicyGate policy,
        IAgentSkillStore skills, IAgentMemoryStore memory, IAgentArtifactStore artifacts, System.Threading.CancellationToken ct)
    {
        var operation = context.Arg(0)?.ToLowerInvariant() ?? "status";
        switch (operation)
        {
            case "status":
                var ai = settings.Load().Ai;
                return CommandResult.Ok($"mode={ai.PermissionMode}\nmemoryEnabled={ai.MemoryEnabled}\nskills={skills.Snapshot().Count}\ncontextLimit={ai.MaxContextCharacters}\ndownloadLimit={ai.MaxToolDownloadBytes}");
            case "mode":
                if (!TryParseMode(context.Arg(1), out var mode)) return CommandResult.Fail("Usage: agent mode request|help|full");
                settings.Update(value => { value.Ai.PermissionMode = mode; value.Ai.TrustedMode = false; }, SettingsSection.Ai);
                policy.SetMode(mode);
                return CommandResult.Ok($"Agent permission mode set to {mode}.");
            case "skills":
                var snapshot = skills.Snapshot();
                return CommandResult.Ok(snapshot.Count == 0 ? "No persistent skills." : string.Join(Environment.NewLine,
                    snapshot.Select(skill => $"{skill.Id}  {(skill.Enabled ? "enabled" : "disabled")}  {skill.Name}  tools={string.Join(',', skill.ToolNames)}")));
            case "memory":
                if (string.Equals(context.Arg(1), "clear", StringComparison.OrdinalIgnoreCase))
                {
                    memory.Clear();
                    return CommandResult.Ok("Persistent Agent memory cleared.");
                }
                var state = memory.Load();
                return CommandResult.Ok($"notes={state.Notes}\nsummary={state.Summary}\nrecentMessages={state.RecentMessages.Count}");
            case "download":
                if (!Uri.TryCreate(context.Arg(1), UriKind.Absolute, out var uri)) return CommandResult.Fail("Usage: agent download <https-url> [file] [sha256]");
                var artifact = await artifacts.DownloadAsync(uri, context.Arg(2), context.Arg(3), ct).ConfigureAwait(false);
                return CommandResult.Ok($"cached={artifact.Path}\nbytes={artifact.Bytes}\nsha256={artifact.Sha256}\nexecuted=false");
            default:
                return CommandResult.Fail("Usage: agent status|mode <request|help|full>|skills|memory [show|clear]|download <https-url> [file] [sha256]");
        }
    }

    private static bool TryParseMode(string? value, out AiPermissionMode mode)
    {
        mode = value?.ToLowerInvariant() switch
        {
            "request" or "requestapproval" => AiPermissionMode.RequestApproval,
            "help" or "helpapproval" => AiPermissionMode.HelpApproval,
            "full" or "fullaccess" => AiPermissionMode.FullAccess,
            _ => AiPermissionMode.RequestApproval
        };
        return value?.ToLowerInvariant() is "request" or "requestapproval" or "help" or "helpapproval" or "full" or "fullaccess";
    }
}
