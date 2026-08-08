using Hackermes.Platform.Models;
using System.Text.Json.Serialization;

namespace Hackermes.Platform.Serialization;

/// <summary>System.Text.Json 源生成上下文 —— 无反射序列化,AOT 与裁剪友好。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GeneralSettings))]
[JsonSerializable(typeof(LayoutSettings))]
[JsonSerializable(typeof(BrowserSettings))]
[JsonSerializable(typeof(TerminalSettings))]
[JsonSerializable(typeof(AiSettings))]
[JsonSerializable(typeof(McpServerSettings))]
[JsonSerializable(typeof(TrafficSettings))]
public partial class AppSettingsJsonContext : JsonSerializerContext
{
}
