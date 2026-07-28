using Hookmes.Platform.Services;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hookmes.AiPanel.Tools;

public sealed class InspectionToolAdapter(IConsoleQueryService console, INetworkQueryService network)
{
    public void RegisterAll(IAiToolRegistry registry)
    {
        registry.Register(new AiToolDefinition(
            "console_read", "读取页面控制台、未捕获异常和浏览器日志。",
            QuerySchema("level", "可选日志级别: error/warn/info/debug"), AiToolRisk.ReadOnly,
            (invocation, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(
                console.Read(ReadLast(invocation.Arguments), ReadString(invocation.Arguments, "level")))))));

        registry.Register(new AiToolDefinition(
            "network_list", "读取最近网络请求，包含状态、耗时和发起调用栈。",
            QuerySchema("failuresOnly", "是否只返回失败请求"), AiToolRisk.ReadOnly,
            (invocation, _) => ValueTask.FromResult(ToolResult.Ok(JsonSerializer.Serialize(
                network.Read(ReadLast(invocation.Arguments), ReadBool(invocation.Arguments, "failuresOnly")))))));
    }

    private static JsonElement QuerySchema(string extraName, string description) => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new System.Collections.Generic.Dictionary<string, object>
        {
            ["last"] = new { type = "integer", minimum = 1, maximum = 1000, description = "返回最近多少条" },
            [extraName] = new { description }
        },
        additionalProperties = false
    });

    private static int ReadLast(JsonElement args) => args.TryGetProperty("last", out var value) && value.TryGetInt32(out var n)
        ? Math.Clamp(n, 1, 1000) : 100;
    private static string? ReadString(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static bool ReadBool(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
