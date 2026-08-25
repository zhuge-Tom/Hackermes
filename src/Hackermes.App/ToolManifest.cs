using Hackermes.App.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hackermes.App;

/// <summary>tools.json 里的一条用户自定义工具声明（解析后、未做可用性判定）。</summary>
public sealed record ToolManifestEntry(
    string Id,
    string Category,
    string Name,
    string Description,
    DesktopToolKind Kind,
    string Path,
    bool RequiresPython,
    IReadOnlyList<string>? Instructions);

/// <summary>
/// 声明式工具清单解析。用户把 <c>tools.json</c> 放进内置工具根目录即可接入自己的工具，
/// 无需改代码重编译。解析刻意用 <see cref="JsonDocument"/> 手工逐字段读取 ——
/// 与设置序列化的源生成上下文隔离，坏字段/缺字段都按"跳过该条"处理而不是让整个清单失效。
/// </summary>
public static class ToolManifest
{
    public const string FileName = "tools.json";
    public const int MaxEntries = 200;

    public static string PathFor(string bundledRoot) => Path.Combine(bundledRoot, FileName);

    /// <summary>读取并校验清单。<paramref name="skipped"/> 返回被拒绝的条数；文件缺失返回空列表。</summary>
    public static IReadOnlyList<ToolManifestEntry> Load(string bundledRoot, out int skipped)
    {
        skipped = 0;
        var file = PathFor(bundledRoot);
        if (!File.Exists(file)) return [];

        JsonDocument document;
        try
        {
            using var buffered = File.OpenRead(file);
            document = JsonDocument.Parse(buffered);
        }
        catch (JsonException)
        {
            skipped = -1; // 整份 JSON 无法解析，与"个别条目无效"区分开。
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("tools", out var tools) ||
                tools.ValueKind != JsonValueKind.Array)
            {
                skipped = -1;
                return [];
            }

            var entries = new List<ToolManifestEntry>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in tools.EnumerateArray())
            {
                if (entries.Count >= MaxEntries) { skipped++; continue; }
                var entry = ReadEntry(item);
                if (entry is null || !seenIds.Add(entry.Id)) { skipped++; continue; }
                entries.Add(entry);
            }
            return entries;
        }
    }

    private static ToolManifestEntry? ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = ReadString(item, "id");
        var category = ReadString(item, "category");
        var name = ReadString(item, "name");
        var path = ReadString(item, "path");
        if (id is null || category is null || name is null || path is null) return null;
        // id 同时是布局持久化键：沿用审计身份规则的宽度约束。
        if (id.Length > 64 || category.Length > 32 || name.Length > 64 || path.Length > 260) return null;
        if (HasControlChar(id) || HasControlChar(name) || HasControlChar(category)) return null;
        if (path.AsSpan().ContainsAny(['\r', '\n', '\t'])) return null;

        var kind = ReadString(item, "kind") switch
        {
            "Gui" => DesktopToolKind.Gui,
            "TeachingTerminal" => DesktopToolKind.TeachingTerminal,
            "Batch" => DesktopToolKind.Batch,
            "Shortcut" => DesktopToolKind.Shortcut,
            _ => (DesktopToolKind?)null
        };
        if (kind is null) return null;

        var instructions = new List<string>();
        if (item.TryGetProperty("instructions", out var rawInstructions) &&
            rawInstructions.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in rawInstructions.EnumerateArray())
            {
                if (instructions.Count >= 12) break;
                if (line.ValueKind == JsonValueKind.String &&
                    line.GetString() is { Length: > 0 } text && text.Length <= 256)
                    instructions.Add(text);
            }
        }

        return new ToolManifestEntry(id, category, name,
            ReadString(item, "description") ?? string.Empty, kind.Value, path,
            item.TryGetProperty("requiresPython", out var requiresPython) &&
            requiresPython.ValueKind == JsonValueKind.True,
            instructions.Count > 0 ? instructions : null);
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text : null;

    private static bool HasControlChar(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character)) return true;
        return false;
    }
}
