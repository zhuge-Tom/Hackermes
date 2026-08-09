using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hackermes.Platform.Models;

/// <summary>
/// 应用配置根。全部显式 <see cref="JsonPropertyNameAttribute"/>,
/// 序列化走源生成上下文 <c>AppSettingsJsonContext</c>(无反射,AOT 友好)。
/// <para>敏感字段(API Key)不落在这里,由 <c>ISecretStore</c> 单独经 DPAPI 保管。</para>
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("general")]
    public GeneralSettings General { get; set; } = new();

    [JsonPropertyName("layout")]
    public LayoutSettings Layout { get; set; } = new();

    [JsonPropertyName("browser")]
    public BrowserSettings Browser { get; set; } = new();

    [JsonPropertyName("terminal")]
    public TerminalSettings Terminal { get; set; } = new();

    [JsonPropertyName("ai")]
    public AiSettings Ai { get; set; } = new();

    [JsonPropertyName("traffic")]
    public TrafficSettings Traffic { get; set; } = new();

    [JsonPropertyName("securityTools")]
    public SecurityToolsSettings SecurityTools { get; set; } = new();
}

/// <summary>Non-sensitive defaults for the authorized local-tool launcher.</summary>
public sealed class SecurityToolsSettings
{
    [JsonPropertyName("primaryToolRoot")]
    public string PrimaryToolRoot { get; set; } = @"E:\tool";

    [JsonPropertyName("secondaryToolRoot")]
    public string SecondaryToolRoot { get; set; } = @"F:\racetools";

    [JsonPropertyName("terminalMode")]
    public string TerminalMode { get; set; } = "Auto";

    [JsonPropertyName("wslDistribution")]
    public string WslDistribution { get; set; } = string.Empty;

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    [JsonPropertyName("defaultTimeoutSeconds")]
    public int DefaultTimeoutSeconds { get; set; } = 120;

}

/// <summary>Non-sensitive traffic UI state. Packet and rule contents are never stored here.</summary>
public sealed class TrafficSettings
{
    [JsonPropertyName("lastArchivePath")]
    public string? LastArchivePath { get; set; }

    [JsonPropertyName("lastRulesPath")]
    public string? LastRulesPath { get; set; }
}

public sealed class GeneralSettings
{
    [JsonPropertyName("isDarkMode")]
    public bool IsDarkMode { get; set; } = true;

    [JsonPropertyName("lastProjectPath")]
    public string? LastProjectPath { get; set; }

    [JsonPropertyName("autoOpenLastProject")]
    public bool AutoOpenLastProject { get; set; } = true;

    [JsonPropertyName("confirmOnExit")]
    public bool ConfirmOnExit { get; set; } = true;
}

/// <summary>
/// 只持久化面板可见性与选中的 Tab。<strong>不保存</strong> Tab 列表与分隔条尺寸 ——
/// Tab 由各模块在启动时重新注册,存下来反而会与代码变更冲突。
/// </summary>
public sealed class LayoutSettings
{
    [JsonPropertyName("leftPanelVisible")]
    public bool LeftPanelVisible { get; set; } = true;

    [JsonPropertyName("rightPanelVisible")]
    public bool RightPanelVisible { get; set; } = true;

    [JsonPropertyName("bottomPanelVisible")]
    public bool BottomPanelVisible { get; set; } = true;

    [JsonPropertyName("leftSelectedTabId")]
    public string? LeftSelectedTabId { get; set; }

    [JsonPropertyName("rightSelectedTabId")]
    public string? RightSelectedTabId { get; set; }

    [JsonPropertyName("bottomSelectedTabId")]
    public string? BottomSelectedTabId { get; set; }

    [JsonPropertyName("contentSelectedTabId")]
    public string? ContentSelectedTabId { get; set; }

    // 默认值按 1120×720 的初始窗口调校:三个面板加起来不该吃掉中间的浏览区。
    [JsonPropertyName("leftPanelWidth")]
    public double LeftPanelWidth { get; set; } = 240;

    [JsonPropertyName("rightPanelWidth")]
    public double RightPanelWidth { get; set; } = 300;

    [JsonPropertyName("bottomPanelHeight")]
    public double BottomPanelHeight { get; set; } = 200;
}

public sealed class BrowserSettings
{
    [JsonPropertyName("homePage")]
    public string HomePage { get; set; } = "https://www.bing.com/";

    /// <summary>
    /// Proxy mode used only by Hackermes' embedded browser. Supported values are
    /// <c>direct</c> and <c>burp</c>; it never changes the operating-system proxy.
    /// </summary>
    [JsonPropertyName("proxyMode")]
    public string ProxyMode { get; set; } = "direct";

    /// <summary>
    /// Removes only explicitly catalogued page telemetry before it reaches an
    /// intercepting proxy. It does not filter ordinary site requests.
    /// </summary>
    [JsonPropertyName("suppressKnownTelemetry")]
    public bool SuppressKnownTelemetry { get; set; } = true;

    /// <summary>是否注入 Page Agent。关闭后仍可用 CDP 只读能力,属于降级而非失效。</summary>
    [JsonPropertyName("pageAgentEnabled")]
    public bool PageAgentEnabled { get; set; } = true;

    /// <summary>禁用 Page Agent 的站点(host 通配符)。用于绕开有反调试检测的页面。</summary>
    [JsonPropertyName("pageAgentDisabledHosts")]
    public List<string> PageAgentDisabledHosts { get; set; } = new();

    /// <summary>网络记录保留的响应体大小上限,超过只存元数据。</summary>
    [JsonPropertyName("maxCapturedBodyBytes")]
    public int MaxCapturedBodyBytes { get; set; } = 512 * 1024;
}

public sealed class TerminalSettings
{
    /// <summary>留空表示按平台自动选择(Windows 用 %ComSpec%)。</summary>
    [JsonPropertyName("shellPath")]
    public string? ShellPath { get; set; }

    [JsonPropertyName("fontSize")]
    public double FontSize { get; set; } = 13;

    [JsonPropertyName("scrollbackLines")]
    public int ScrollbackLines { get; set; } = 5000;
}

public sealed class AiSettings
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// OpenAI-compatible chat route, kept separate from the base URL so private
    /// gateways can expose a custom route without changing client code.
    /// </summary>
    [JsonPropertyName("chatCompletionsPath")]
    public string ChatCompletionsPath { get; set; } = "/chat/completions";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-5-mini";

    /// <summary>
    /// Legacy compatibility switch. Existing installs with this flag enabled migrate to
    /// <see cref="AiPermissionMode.FullAccess"/> when settings are next normalized.
    /// </summary>
    [JsonPropertyName("trustedMode")]
    public bool TrustedMode { get; set; }

    /// <summary>Agent capability policy. Defaults to per-operation approval.</summary>
    [JsonPropertyName("permissionMode")]
    public AiPermissionMode PermissionMode { get; set; } = AiPermissionMode.RequestApproval;

    [JsonPropertyName("maxToolRounds")]
    public int MaxToolRounds { get; set; } = 12;

    /// <summary>Bounded character budget used when preparing a model request.</summary>
    [JsonPropertyName("maxContextCharacters")]
    public int MaxContextCharacters { get; set; } = 24_000;

    /// <summary>Number of recent human/assistant messages retained after compaction.</summary>
    [JsonPropertyName("maxRecentMessages")]
    public int MaxRecentMessages { get; set; } = 16;

    [JsonPropertyName("memoryEnabled")]
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>Maximum size of one HTTPS artifact cached for an approved Agent download.</summary>
    [JsonPropertyName("maxToolDownloadBytes")]
    public int MaxToolDownloadBytes { get; set; } = 128 * 1024 * 1024;

    [JsonPropertyName("mcpServers")]
    public List<McpServerSettings> McpServers { get; set; } = new();
}

/// <summary>
/// Mirrors the three interaction modes exposed by Codex-style agents. The policy gate,
/// rather than individual tools, owns their enforcement.
/// </summary>
public enum AiPermissionMode
{
    RequestApproval,
    HelpApproval,
    FullAccess
}

public sealed class McpServerSettings
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();
}
