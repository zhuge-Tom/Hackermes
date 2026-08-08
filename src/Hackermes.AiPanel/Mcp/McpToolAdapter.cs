using Hackermes.AiPanel.Tools;
using Hackermes.Base.Diagnostics;
using Hackermes.Platform.Models;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Mcp;

public sealed class McpToolAdapter(IMcpBridge bridge, IAiToolRegistry registry, IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForCategory(nameof(McpToolAdapter));

    public async Task InitializeAsync(AiSettings settings, CancellationToken ct = default)
    {
        foreach (var server in settings.McpServers)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await bridge.ConnectAsync(new McpStdioServer(server.Id, server.Command, server.Arguments), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.Error($"MCP server 连接失败: {server.Id}", ex); }
        }

        try
        {
            await foreach (var tool in bridge.EnumerateToolsAsync(ct).ConfigureAwait(false))
            {
                var serverId = tool.ServerId;
                var remoteName = tool.Name;
                var safeName = $"mcp_{Sanitize(serverId)}_{Sanitize(remoteName)}";
                if (safeName.Length > 64) safeName = safeName[..64];
                if (registry.TryGet(safeName, out _))
                {
                    _logger.Warn($"MCP 工具名冲突，已跳过: {safeName}");
                    continue;
                }
                registry.Register(new AiToolDefinition(
                    safeName, $"[{serverId}] {tool.Description}", tool.InputSchema, AiToolRisk.Mutating,
                    (invocation, token) => bridge.InvokeAsync(serverId,
                        invocation with { ToolName = remoteName }, token)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.Error("MCP 工具枚举失败", ex); }
    }

    private static string Sanitize(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var c in value) output.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        return output.ToString();
    }
}
