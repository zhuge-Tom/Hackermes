using Hackermes.Assessment;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Hackermes.App.Views;

public enum DesktopToolKind
{
    BuiltIn,
    TeachingTerminal,
    Gui,
    Shortcut,
    Batch
}

public enum DesktopToolAvailability
{
    Available,
    Unverified,
    NotIntegrated,
    Missing,
    DependencyMissing,
    Invalid
}

public sealed record DesktopToolEntry(
    string Id,
    string Category,
    string Name,
    string Description,
    DesktopToolKind Kind,
    bool Available,
    string? Path = null,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? Instructions = null,
    string? AdapterId = null,
    string? UnavailableReason = null,
    string? VerificationNote = null)
{
    public DesktopToolAvailability Availability => Available
        ? string.IsNullOrWhiteSpace(VerificationNote)
            ? DesktopToolAvailability.Available
            : DesktopToolAvailability.Unverified
        : UnavailableReason switch
        {
            { } reason when reason.StartsWith("未接入", StringComparison.Ordinal) => DesktopToolAvailability.NotIntegrated,
            { } reason when reason.StartsWith("未找到", StringComparison.Ordinal) => DesktopToolAvailability.Missing,
            { } reason when reason.StartsWith("文件格式无效", StringComparison.Ordinal) => DesktopToolAvailability.Invalid,
            _ => DesktopToolAvailability.DependencyMissing
        };
}

/// <summary>
/// 单次目录扫描内的 PATH 探测结果缓存。
/// 同一扫描里多个 Python 工具各自探测 python.exe/py.exe 会把同一份 PATH 逐项
/// <c>File.Exists</c> 数十遍；按文件名去重后每种运行时只探测一次。
/// 缓存生命周期 = 一次扫描，跨扫描不复用，避免外部环境变化造成陈旧判断。
/// </summary>
public sealed class PathProbeCache
{
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _probe;

    /// <summary>探测函数可注入：测试用它消除对宿主机 PATH 的依赖。</summary>
    public PathProbeCache(Func<string, string?>? probe = null) =>
        _probe = probe ?? ProbePath;

    public string? FindOnPath(string fileName)
    {
        if (_resolved.TryGetValue(fileName, out var cached)) return cached;
        var resolved = _probe(fileName);
        _resolved[fileName] = resolved;
        return resolved;
    }

    internal static string? ProbePath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => Path.Combine(path, fileName)).FirstOrDefault(File.Exists);
}

public static class DesktopToolCatalog
{
    public const string BuiltInCodec = "builtin.codec";

    public static IReadOnlyList<DesktopToolEntry> Describe(SecurityToolsSettings settings, string? bundledRoot = null,
        PathProbeCache? probeCache = null)
    {
        var probes = probeCache ?? new PathProbeCache();
        var primary = settings.PrimaryToolRoot;
        var secondary = settings.SecondaryToolRoot;
        DesktopToolEntry[] tools =
        [
            BuiltIn(BuiltInCodec, "加解密", "编码与哈希", "Base64、URL、Hex、SHA 与 MD5，可复制结果继续处理。"),
            BuiltIn("crypto.radix", "加解密", "进制转换", "Hackermes 内置二、八、十、十六进制转换。"),
            BuiltIn("crypto.jwt", "加解密", "JWT 解码", "解码 JWT 的 Header 与 Payload，纯进程内计算。"),
            BuiltIn("util.timestamp", "实用工具", "时间戳转换", "Unix 时间戳与日期时间双向转换，自动识别秒/毫秒。"),
            BuiltIn("util.regex.tester", "实用工具", "正则测试器", "对文本试运行正则表达式并列出全部匹配与分组。"),
            Gui("crypto.ctf", "加解密", "随波逐流 CTF 编码工具", "综合 CTF 编码、解码与识别界面。", secondary, "加密解密", "随波逐流", "[随波逐流]CTF编码工具 V7.3 20260506.exe"),
            Terminal("recon.nmap.terminal", "信息收集", "Nmap", "在原生终端使用完整 Nmap 参数。",
                Combine(primary, "01-信息收集", "扫描接口", "zenmap(1)", "nmap.exe"),
                ["常用示例（请将目标替换为已授权目标）：", ".\\nmap.exe -sT -Pn -n -p 80,443 127.0.0.1", ".\\nmap.exe -sV --version-light -p 80,443 127.0.0.1", "完整帮助： .\\nmap.exe --help"])
                with { AdapterId = AuthorizedToolCatalog.NmapQuick },
            PythonTerminal("recon.dirsearch.terminal", "信息收集", "Dirsearch", "在原生终端使用完整目录发现参数。",
                Combine(primary, "01-信息收集", "目录扫描", "dirsearch_mulu", "dirsearch", "dirsearch.py"), probes,
                ["常用示例（请将 URL 替换为已授权目标）：", "python .\\dirsearch.py -u https://authorized.example -e php,html,js", "完整帮助： python .\\dirsearch.py --help"])
                with { AdapterId = AuthorizedToolCatalog.DirsearchQuick },
            Gui("recon.layer", "信息收集", "Layer 子域名挖掘机", "使用原生图形界面进行子域名信息收集。", primary, "01-信息收集", "子域名", "子域名挖掘机5.0修改版", "Layer.exe"),

            NotBundled("web.burp", "Web 与流量", "Burp Suite", "拦截、检查和修改浏览器 HTTP/HTTPS 请求与响应；可与内部浏览器的 Burp 代理模式配合使用。"),
            BuiltIn("web.url.parse", "Web 与流量", "URL 解析", "拆解 URL 的协议、主机、端口、路径与查询参数，纯进程内计算。"),
            NotBundled("traffic.wireshark", "Web 与流量", "Wireshark", "抓取并分析网络数据包，支持协议分层、显示过滤器和会话排查。"),

            PythonModuleTerminal("detect.wafw00f.terminal", "漏洞扫描", "Wafw00f", "在原生终端识别 Web 应用防火墙。",
                Combine(primary, "02-漏洞扫描", "wafw00f-2.4.2", "wafw00f-2.4.2", "wafw00f", "main.py"), probes,
                ["常用示例（请将 URL 替换为已授权目标）：", "python -m wafw00f.main https://authorized.example", "完整帮助： python -m wafw00f.main --help"])
                with { AdapterId = AuthorizedToolCatalog.Wafw00fQuick },
            PythonTerminal("detect.unauthorized.terminal", "漏洞扫描", "未授权访问扫描", "在教学终端中使用 Unauthorized-Vul 的完整参数。",
                Combine(primary, "02-漏洞扫描", "自动扫描漏洞", "Unauthorized_VUl", "Unauthorized-Vul.py"), probes,
                ["仅对已明确授权的目标使用：", "python .\\Unauthorized-Vul.py --help", "Loopback 示例： python .\\Unauthorized-Vul.py -u http://127.0.0.1:8080 -s swagger -t 1"]),
            Gui("detect.apk-analyser", "漏洞扫描", "APK Analyzer", "检查 Android APK 的清单、组件、权限、资源与包结构，辅助移动应用安全分析。",
                primary, "02-漏洞扫描", "漏了个大洞(APK)", "apk数据提取", "apkAnalyser.exe") with
            {
                VerificationNote = "本地程序没有数字签名、版本或厂商信息；请仅在确认文件来源可信时使用。"
            },
            PythonTerminal("exploit.sqlmap.terminal", "漏洞利用", "SQLmap", "在原生终端保留 SQLmap 的完整参数能力。",
                Combine(primary, "03-漏洞利用", "SQL", "sqlmap", "sqlmap.py"), probes,
                ["仅对明确授权目标使用：", "python .\\sqlmap.py -u \"https://authorized.example/item?id=1\" --batch", "完整帮助： python .\\sqlmap.py -h"]),
            PythonTerminal("exploit.xss-fuzzer.terminal", "漏洞利用", "XSS Fuzzer", "在教学终端中使用 XSS 参数模糊测试器。",
                Combine(primary, "03-漏洞利用", "XSS", "xssfuzz", "xssFuzz", "xssFuzz.py"), probes,
                ["仅对已明确授权的目标使用：", "python .\\xssFuzz.py --help", "示例目标请使用自建靶场或 loopback。"]),
            PythonTerminal("exploit.dnslog-sqli.terminal", "漏洞利用", "DNSLog SQL 注入", "DNSLog 盲注辅助工具（内置副本已兼容 Python 3）。",
                Combine(primary, "03-漏洞利用", "DNS注入", "DnslogSqlinj", "dnslogSql.py"), probes,
                ["先在 config.py 配置你自己的 APItoken 与 DNSurl。", "完整帮助：python .\\dnslogSql.py --help", "仅对明确授权的目标使用。"]),

            NotBundled("password.john.terminal", "密码审计", "John the Ripper", "对获授权的密码哈希执行字典、规则与增量模式审计。"),
            NotBundled("password.archpr", "密码审计", "ARCHPR", "恢复和审计获授权的压缩包密码，支持常见 ZIP、RAR 等归档格式。"),

            NotBundled("reverse.ghidra", "逆向分析", "Ghidra", "反汇编、反编译并分析可执行文件、函数、符号和交叉引用。"),
            Gui("reverse.x64dbg", "逆向分析", "x64dbg", "在 Windows 上动态调试 x86/x64 程序，检查寄存器、内存、断点与调用流程。",
                secondary, "snapshot_2026-05-27_12-11", "release", "x96dbg.exe"),


        ];
        var root = bundledRoot ?? Path.Combine(AppContext.BaseDirectory, "tools");
        return tools.Select(tool => PreferBundled(tool, root, probes)).ToArray();
    }

    /// <summary>
    /// 把声明式清单（tools.json）里的一条自定义工具变成目录条目。
    /// 路径安全边界：相对路径只允许落在内置工具根目录内；绝对路径必须位于
    /// 已配置的两个授权根目录之一内 —— 与内置条目共用同一套逃逸检查哲学。
    /// 返回 null 表示该条目无效（reason 给出人读原因）。
    /// </summary>
    internal static DesktopToolEntry? CreateCustomEntry(ToolManifestEntry entry, string bundledRoot,
        SecurityToolsSettings settings, PathProbeCache probes)
    {
        var root = Path.GetFullPath(bundledRoot);
        string fullPath;
        if (Path.IsPathRooted(entry.Path))
        {
            try { fullPath = Path.GetFullPath(entry.Path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
            if (!UnderRoot(fullPath, settings.PrimaryToolRoot) && !UnderRoot(fullPath, settings.SecondaryToolRoot))
                return null;
        }
        else
        {
            fullPath = Path.GetFullPath(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!UnderRoot(fullPath, root)) return null;
        }

        var exists = File.Exists(fullPath);
        var available = exists;
        string? unavailableReason = exists ? null : Missing(fullPath);
        if (exists && !IsLaunchableFile(fullPath))
        {
            available = false;
            unavailableReason = "文件格式无效：" + fullPath;
        }
        if (exists && (entry.RequiresPython || fullPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
        {
            var runtime = probes.FindOnPath("python.exe") ?? probes.FindOnPath("py.exe");
            if (runtime is null)
            {
                available = false;
                unavailableReason = "已找到工具源码，但尚未找到 Python 3 运行环境";
            }
        }

        return new DesktopToolEntry(entry.Id, entry.Category, entry.Name, entry.Description,
            entry.Kind, available, fullPath, Path.GetDirectoryName(fullPath),
            entry.Instructions, UnavailableReason: unavailableReason);
    }

    private static bool UnderRoot(string fullPath, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullRoot = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar));
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed record BundledDescriptor(
        string EntryPoint,
        string? WorkingDirectory = null,
        DesktopToolKind? Kind = null,
        bool RequiresPython = false,
        bool RequiresJava21 = false);

    private static readonly IReadOnlyDictionary<string, BundledDescriptor> BundledTools =
        new Dictionary<string, BundledDescriptor>(StringComparer.Ordinal)
        {
            ["crypto.ctf"] = new("crypto.ctf/[随波逐流]CTF编码工具 V7.3 20260506.exe"),
            ["recon.nmap.terminal"] = new("recon.nmap.terminal/nmap.exe"),
            ["recon.dirsearch.terminal"] = new("recon.dirsearch.terminal/dirsearch.py", RequiresPython: true),
            ["recon.layer"] = new("recon.layer/Layer.exe"),
            ["detect.wafw00f.terminal"] = new("detect.wafw00f.terminal/wafw00f/main.py", ".", RequiresPython: true),
            ["detect.unauthorized.terminal"] = new("detect.unauthorized.terminal/Unauthorized-Vul.py", RequiresPython: true),
            ["exploit.sqlmap.terminal"] = new("exploit.sqlmap.terminal/sqlmap.py", RequiresPython: true),
            ["exploit.xss-fuzzer.terminal"] = new("exploit.xss-fuzzer.terminal/xssFuzz.py", RequiresPython: true),
            ["exploit.dnslog-sqli.terminal"] = new("exploit.dnslog-sqli.terminal/dnslogSql.py", RequiresPython: true),
        };

    private static DesktopToolEntry PreferBundled(DesktopToolEntry tool, string bundledRoot, PathProbeCache probes)
    {
        if (!BundledTools.TryGetValue(tool.Id, out var descriptor)) return tool;
        var root = Path.GetFullPath(bundledRoot);
        var entryPoint = Path.GetFullPath(Path.Combine(root, descriptor.EntryPoint.Replace('/', Path.DirectorySeparatorChar)));
        if (!entryPoint.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bundled tool entry escapes the tools directory: {tool.Id}");
        // 普通开发构建不会把 third_party/tools 复制到 bin/tools。此时保留前面已经在
        // 用户明确配置的 Primary/SecondaryToolRoot 下解析出的条目；发布包包含内置副本时
        // 仍优先使用应用相对路径，保证可移植安装不依赖机器上的历史目录。
        if (!File.Exists(entryPoint)) return tool;

        var toolRoot = Path.GetDirectoryName(entryPoint)!;
        if (!string.IsNullOrWhiteSpace(descriptor.WorkingDirectory))
        {
            var idRoot = Path.Combine(root, tool.Id);
            toolRoot = Path.GetFullPath(Path.Combine(idRoot, descriptor.WorkingDirectory));
        }
        var available = IsLaunchableFile(entryPoint);
        string? unavailableReason = available ? null : "文件格式无效：" + entryPoint;
        var bundledPython = Path.Combine(root, "_runtime", "python", "python.exe");
        if (descriptor.RequiresPython && !File.Exists(bundledPython) &&
            probes.FindOnPath("python.exe") is null && probes.FindOnPath("py.exe") is null)
        {
            available = false;
            unavailableReason = "内置工具源码已找到，但尚未找到 Python 3 运行环境";
        }
        if (descriptor.RequiresJava21)
        {
            var java = probes.FindOnPath("java.exe");
            available = available && java is not null && IsJava21OrNewer(java);
            unavailableReason = available ? null : "内置 Ghidra 已找到，但尚未找到 Java 21 或更高版本";
        }
        return tool with
        {
            Kind = descriptor.Kind ?? tool.Kind,
            Available = available,
            Path = entryPoint,
            WorkingDirectory = toolRoot,
            UnavailableReason = unavailableReason
        };
    }

    private static DesktopToolEntry BuiltIn(string id, string category, string name, string description) =>
        new(id, category, name, description, DesktopToolKind.BuiltIn, true);

    private static DesktopToolEntry NotBundled(string id, string category, string name, string description) =>
        new(id, category, name, description, DesktopToolKind.Gui, false,
            UnavailableReason: "未接入；当前版本不随程序分发");

    private static DesktopToolEntry Gui(string id, string category, string name, string description, string root, params string[] parts) =>
        FileEntry(id, category, name, description, DesktopToolKind.Gui, Combine(root, parts));

    private static DesktopToolEntry Shortcut(string id, string category, string name, string description, string root, params string[] parts) =>
        FileEntry(id, category, name, description, DesktopToolKind.Shortcut, Combine(root, parts));

    private static DesktopToolEntry Batch(string id, string category, string name, string description, string root, params string[] parts)
    {
        var path = Combine(root, parts);
        if (!File.Exists(path)) return FileEntry(id, category, name, description, DesktopToolKind.Batch, path);
        var java = FindOnPath("java.exe");
        var available = java is not null && IsJava21OrNewer(java);
        return new(id, category, name, description, DesktopToolKind.Batch, available, path,
            Path.GetDirectoryName(path), UnavailableReason: available ? null : "未找到 Ghidra 所需的 Java 21 或更高版本");
    }

    private static DesktopToolEntry Terminal(string id, string category, string name, string description, string path, IReadOnlyList<string> instructions)
    {
        var exists = File.Exists(path);
        var available = exists && IsLaunchableFile(path);
        return new(id, category, name, description, DesktopToolKind.TeachingTerminal, available, path,
            Path.GetDirectoryName(path), instructions,
            UnavailableReason: !exists ? Missing(path) : available ? null : "文件格式无效：" + path);
    }

    private static DesktopToolEntry PythonTerminal(string id, string category, string name, string description,
        string path, PathProbeCache probes, IReadOnlyList<string> instructions)
    {
        var runtime = probes.FindOnPath("python.exe") ?? probes.FindOnPath("py.exe");
        var exists = File.Exists(path) && runtime is not null;
        return new(id, category, name, description, DesktopToolKind.TeachingTerminal, exists, path,
            Path.GetDirectoryName(path), instructions,
            UnavailableReason: exists ? null : !File.Exists(path) ? Missing(path) : "未找到 Python 3 运行环境");
    }

    private static DesktopToolEntry PythonModuleTerminal(string id, string category, string name, string description,
        string mainPath, PathProbeCache probes, IReadOnlyList<string> instructions)
    {
        var runtime = probes.FindOnPath("python.exe") ?? probes.FindOnPath("py.exe");
        var packageRoot = Path.GetDirectoryName(Path.GetDirectoryName(mainPath)!)!;
        var exists = File.Exists(mainPath) && Directory.Exists(packageRoot) && runtime is not null;
        return new(id, category, name, description, DesktopToolKind.TeachingTerminal, exists, mainPath,
            packageRoot, instructions,
            UnavailableReason: exists ? null : !File.Exists(mainPath) ? Missing(mainPath) : "未找到 Python 3 运行环境");
    }

    private static DesktopToolEntry UnavailableTerminal(string id, string category, string name, string description,
        string path, string reason) =>
        new(id, category, name, description, DesktopToolKind.TeachingTerminal, false, path,
            Path.GetDirectoryName(path), UnavailableReason: File.Exists(path) ? reason : Missing(path));

    private static DesktopToolEntry FileEntry(string id, string category, string name, string description, DesktopToolKind kind, string path)
    {
        var exists = File.Exists(path);
        var valid = exists && IsLaunchableFile(path);
        return new(id, category, name, description, kind, valid, path, Path.GetDirectoryName(path),
            UnavailableReason: !exists ? Missing(path) : valid ? null : "文件格式无效：" + path);
    }

    private static bool IsLaunchableFile(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string Missing(string path) => "未找到：" + path;
    private static string Combine(string root, params string[] parts) => Path.Combine([root, .. parts]);
    private static string? FindOnPath(string fileName) => PathProbeCache.ProbePath(fileName);

    private static bool IsJava21OrNewer(string java)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = java,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(2_000))
            {
                try { process?.Kill(); } catch { }
                return false;
            }
            var version = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            var marker = version.IndexOf("version \"", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return false;
            marker += "version \"".Length;
            var end = version.IndexOfAny(['.', '"'], marker);
            return end > marker && int.TryParse(version[marker..end], out var major) && major >= 21;
        }
        catch { return false; }
    }
}
