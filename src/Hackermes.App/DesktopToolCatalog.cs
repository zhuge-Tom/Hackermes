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
            NotBundled("recon.git-leak.terminal", "信息收集", "GitHack .git 泄露恢复", "解析暴露的 /.git/ 并还原源码；Agent 适配器 recon.git_leak.scan。"),
            NotBundled("recon.svn-leak.terminal", "信息收集", "SvnExploit .svn 泄露枚举", "解析暴露的 /.svn/wc.db 并列出受控文件；Agent 适配器 recon.svn_leak.scan。"),
            NotBundled("recon.ds-store.terminal", "信息收集", "DS_Store 目录枚举", "解析暴露的 /.DS_Store 并还原目录结构；Agent 适配器 recon.ds_store.scan。"),
            NotBundled("recon.swagger-api.terminal", "信息收集", "Swagger API 枚举", "解析 Swagger 文档并批量请求接口；Agent 适配器 recon.swagger_api.enum。"),
            NotBundled("detect.weblogic-t3.terminal", "漏洞扫描", "WeblogicScan T3 检测", "检测 Weblogic T3 反序列化 CVE；Agent 适配器 detect.weblogic_t3.scan。"),
            NotBundled("detect.fastjson-jndi.terminal", "漏洞扫描", "JsonExp Fastjson 检测", "发送带 payload 的 JSON 检测 Fastjson/Jackson；Agent 适配器 detect.fastjson_jndi.scan。"),
            NotBundled("exploit.vcenter.terminal", "漏洞利用", "VcenterKiller 验证", "验证 vCenter CVE（仅限已授权目标）；Agent 适配器 exploit.vcenter.verify。"),
            NotBundled("exploit.heapdump.terminal", "漏洞利用", "JDumpSpider 堆转储分析", "从 heapdump 提取敏感凭据（需 Java）；Agent 适配器 exploit.heapdump.analyze。"),
            NotBundled("detect.oa-poc.terminal", "漏洞扫描", "OA POC 定向探测", "国内 OA 漏洞 POC 库（泛微/致远/通达/用友等 97 条）；Agent 适配器 detect.oa_poc.probe。"),
            NotBundled("detect.shiro.terminal", "漏洞扫描", "Shiro key 检测", "Shiro rememberMe key 爆破（需 Java）；Agent 适配器 detect.shiro.scan。"),
            NotBundled("detect.struts2.terminal", "漏洞扫描", "Struts2 全版本检测", "S2-001~057 系列 OGNL 漏洞检测；Agent 适配器 detect.struts2.scan。"),
            NotBundled("detect.nacos.terminal", "漏洞扫描", "Nacos 只读探测", "未授权用户列表/配置读取/控制台暴露检测；Agent 适配器 detect.nacos.scan。"),
            NotBundled("exploit.fastjson-payload.terminal", "漏洞利用", "Fastjson payload 生成", "生成指定 gadget 的利用 payload 文本；Agent 适配器 exploit.fastjson_payload.generate。"),
            NotBundled("probe.cloud-aksk.terminal", "漏洞利用", "云 AK/SK 只读验证", "验证云凭证并列出只读资源（cf）；Agent 适配器 probe.cloud_aksk.verify。"),

            NotBundled("password.john.terminal", "密码审计", "John the Ripper", "对获授权的密码哈希执行字典、规则与增量模式审计。"),
            NotBundled("password.archpr", "密码审计", "ARCHPR", "恢复和审计获授权的压缩包密码，支持常见 ZIP、RAR 等归档格式。"),

            NotBundled("reverse.ghidra", "逆向分析", "Ghidra", "反汇编、反编译并分析可执行文件、函数、符号和交叉引用。"),
            Gui("reverse.x64dbg", "逆向分析", "x64dbg", "在 Windows 上动态调试 x86/x64 程序，检查寄存器、内存、断点与调用流程。",
                secondary, "snapshot_2026-05-27_12-11", "release", "x96dbg.exe"),

            NotBundled("gui.shiro-exploit", "漏洞利用", "ShiroExploit", "Shiro rememberMe key 爆破与反序列化利用（内置 ysoserial）。",
                "shiro-exploit/ShiroExploit.jar"),
            NotBundled("gui.struts2-check", "漏洞利用", "Struts2 全版本检测", "Struts2 S2-001~057 全版本漏洞检测界面。", "struts2-check/Struts2_19.21.jar"),
            NotBundled("gui.thinkphp", "漏洞利用", "ThinkPHP 综合利用", "ThinkPHP 多版本 RCE 检测与利用。", "thinkphp/ThinkPHP.jar"),
            NotBundled("gui.tomcat-pass", "漏洞利用", "TomcatPass", "Tomcat Manager 弱口令爆破与 WAR 部署。", "tomcat-pass/TomcatPass.jar"),
            NotBundled("gui.nacos-exploit", "漏洞利用", "NacosExploitGUI", "Nacos 未授权/添加用户/身份绕过/Derby SQL 综合利用。", "nacos-exploit/NacosExploitGUI_v4.0.jar"),
            NotBundled("gui.xxl-job", "漏洞利用", "XXL-JOB ExploitGUI", "XXL-JOB 默认 token、executor 未授权与 GLUE RCE 利用。", "xxl-job/xxl-jobExploitGUI_v1.0.jar"),
            NotBundled("gui.jenkins-exploit", "漏洞利用", "JenkinsExploit-GUI", "Jenkins 未授权控制台、文件读取与 RCE 利用。", "jenkins-exploit/JenkinsExploit-GUI-1.3-SNAPSHOT.jar"),
            NotBundled("gui.tongda-oa", "漏洞利用", "通达OA综合利用", "通达 OA 任意文件上传/下载、SQL 注入综合利用。", "tongda-oa/TongdaOATool_V1.3.jar"),
            NotBundled("gui.frchannel", "漏洞利用", "帆软 FrChannelPlus", "帆软 FineReport 反序列化、文件读取综合利用。", "frchannel/FrChannelPlus.jar"),
            NotBundled("gui.hikvision", "漏洞利用", "海康威视综合利用", "海康设备 CVE-2021-36260 命令注入与弱口令利用。", "hikvision/HikvisionExploitGUI_v3.0.jar"),
            NotBundled("gui.dahua", "漏洞利用", "大华综合利用", "大华设备登录绕过弱口令与文件下载利用。", "dahua/DahuaExploitGUI.jar"),
            NotBundled("gui.myexploit", "漏洞利用", "MYExploit 综合利用", "OA、数据库、中间件常见漏洞图形化综合利用面板。", "myexploit/MYExploit.jar"),
            NotBundled("gui.decrypt-tools", "加解密", "DecryptTools 综合加解密", "常见加密编码转换与各产品配置文件专用解密。", "decrypt-tools/DecryptTools.jar"),
            NotBundled("gui.mdat", "漏洞利用", "MDAT 数据库综合利用", "MySQL/SQLServer/Oracle/Redis 等数据库综合利用（UDF、文件读写）。", "mdat/Multiple.Database.Utilization.Tools-2.1.1-jar-with-dependencies.jar"),
            NotBundled("gui.api-tool", "Web 与流量", "API-T00L", "互联网厂商常见 API 接口利用与发包测试。", "api-tool/API-T00L_v1.2.jar"),


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
        bool RequiresJava21 = false,
        bool RequiresJavaFx = false,
        bool LegacyJavaFx = false);

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
            ["recon.git-leak.terminal"] = new("recon.git-leak.terminal/GitHack.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["recon.svn-leak.terminal"] = new("recon.svn-leak.terminal/SvnExploit.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["recon.ds-store.terminal"] = new("recon.ds-store.terminal/ds_store_exp.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["recon.swagger-api.terminal"] = new("recon.swagger-api.terminal/swagger-hack2.0.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["detect.weblogic-t3.terminal"] = new("detect.weblogic-t3.terminal/WeblogicScan.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["detect.fastjson-jndi.terminal"] = new("detect.fastjson-jndi.terminal/JsonExp.exe", Kind: DesktopToolKind.TeachingTerminal),
            ["exploit.vcenter.terminal"] = new("exploit.vcenter.terminal/main.exe", Kind: DesktopToolKind.TeachingTerminal),
            ["exploit.heapdump.terminal"] = new("exploit.heapdump.terminal/JDumpSpider-1.1-SNAPSHOT-full.jar", Kind: DesktopToolKind.TeachingTerminal),
            ["detect.oa-poc.terminal"] = new("detect.oa-poc.terminal/oa_poc_runner.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["detect.shiro.terminal"] = new("detect.shiro.terminal/shiro_tool.jar", Kind: DesktopToolKind.TeachingTerminal),
            ["detect.struts2.terminal"] = new("detect.struts2.terminal/Struts2Scan.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["detect.nacos.terminal"] = new("detect.nacos.terminal/nacos_probe.py", Kind: DesktopToolKind.TeachingTerminal, RequiresPython: true),
            ["exploit.fastjson-payload.terminal"] = new("exploit.fastjson-payload.terminal/FastjsonExploit-0.1-beta2-all.jar", Kind: DesktopToolKind.TeachingTerminal),
            ["probe.cloud-aksk.terminal"] = new("probe.cloud-aksk.terminal/cf.exe", Kind: DesktopToolKind.TeachingTerminal),
            ["gui.shiro-exploit"] = new("gui.shiro-exploit.terminal/ShiroExploit.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true, LegacyJavaFx: true),
            ["gui.struts2-check"] = new("gui.struts2-check.terminal/Struts2_19.21.jar", Kind: DesktopToolKind.Gui),
            ["gui.thinkphp"] = new("gui.thinkphp.terminal/ThinkPHP.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.tomcat-pass"] = new("gui.tomcat-pass.terminal/TomcatPass.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.nacos-exploit"] = new("gui.nacos-exploit.terminal/NacosExploitGUI_v4.0.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.xxl-job"] = new("gui.xxl-job.terminal/xxl-jobExploitGUI_v1.0.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.jenkins-exploit"] = new("gui.jenkins-exploit.terminal/JenkinsExploit-GUI-1.3-SNAPSHOT.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true, LegacyJavaFx: true),
            ["gui.tongda-oa"] = new("gui.tongda-oa.terminal/TongdaOATool_V1.3.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.frchannel"] = new("gui.frchannel.terminal/FrChannelPlus.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.hikvision"] = new("gui.hikvision.terminal/HikvisionExploitGUI_v3.0.jar", Kind: DesktopToolKind.Gui),
            ["gui.dahua"] = new("gui.dahua.terminal/DahuaExploitGUI.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.myexploit"] = new("gui.myexploit.terminal/MYExploit.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.decrypt-tools"] = new("gui.decrypt-tools.terminal/DecryptTools.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.mdat"] = new("gui.mdat.terminal/Multiple.Database.Utilization.Tools-2.1.1-jar-with-dependencies.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
            ["gui.api-tool"] = new("gui.api-tool.terminal/API-T00L_v1.2.jar", Kind: DesktopToolKind.Gui, RequiresJavaFx: true),
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
        if (descriptor.RequiresJavaFx)
        {
            var java = probes.FindOnPath("java.exe");
            var fxLib = Path.Combine(root, "_runtime", "javafx", "lib");
            available = available && java is not null && Directory.Exists(fxLib);
            unavailableReason = available ? null : "尚未找到 Java 运行时或内置 JavaFX 模块（_runtime/javafx/lib）";
        }
        if (descriptor.LegacyJavaFx)
        {
            // 老工具专用栈：内置 Java 11 JRE + JavaFX 11 模块，与 21 栈完全隔离。
            var legacyJava = Path.Combine(root, "_runtime", "java11", "bin", "java.exe");
            var legacyFx = Path.Combine(root, "_runtime", "javafx11", "lib");
            available = available && File.Exists(legacyJava) && Directory.Exists(legacyFx);
            unavailableReason = available ? null : "需要内置 Java 11 运行时（_runtime/java11）与 JavaFX 11 模块（_runtime/javafx11/lib）";
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

    /// <summary>
    /// Resolves the launch command for a bundled GUI jar: JavaFX tools run against the
    /// bundled OpenJFX modules, Swing tools run on the plain JVM. Returns false when the
    /// entry is not a bundled GUI jar or a Java runtime is unavailable.
    /// </summary>
    public static bool TryGetBundledGuiLaunch(string toolId, out string java,
        out IReadOnlyList<string> arguments, out string? workingDirectory, out string? unavailableReason)
    {
        java = string.Empty;
        arguments = [];
        workingDirectory = null;
        unavailableReason = null;
        if (!BundledTools.TryGetValue(toolId, out var descriptor) || descriptor.Kind is not DesktopToolKind.Gui)
        {
            unavailableReason = "该工具不在内置清单中。";
            return false;
        }
        var runtime = FindJavaRuntime();
        if (runtime is null)
        {
            unavailableReason = "未找到 Java 运行时（PATH 与常见安装位置均未命中）；请安装 Java 21+。";
            return false;
        }
        var jarPath = Path.Combine(
            AppContext.BaseDirectory, "tools", descriptor.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(jarPath))
        {
            unavailableReason = $"内置工具文件缺失：{jarPath}";
            return false;
        }
        java = runtime;
        workingDirectory = Path.GetDirectoryName(jarPath);
        if (descriptor.RequiresJavaFx)
        {
            // 双运行时隔离：老 ControlsFX 工具跑在独立的 Java 11 + JavaFX 11 栈上，
            // 其余走当前栈；两个环境的模块目录完全分开，不混用。
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
            var fxLib = Path.Combine(toolsDir, "_runtime", "javafx", "lib");
            if (descriptor.LegacyJavaFx)
            {
                var legacyJava = Path.Combine(toolsDir, "_runtime", "java11", "bin", "java.exe");
                var legacyFxLib = Path.Combine(toolsDir, "_runtime", "javafx11", "lib");
                if (!File.Exists(legacyJava))
                {
                    unavailableReason = "需要内置 Java 11 运行时（tools/_runtime/java11）。";
                    return false;
                }
                if (!Directory.Exists(legacyFxLib))
                {
                    unavailableReason = "缺少内置 JavaFX 11 模块（tools/_runtime/javafx11/lib）。";
                    return false;
                }
                java = legacyJava;
                fxLib = legacyFxLib;
            }
            else if (!Directory.Exists(fxLib))
            {
                unavailableReason = "内置 JavaFX 模块缺失（tools/_runtime/javafx/lib）。";
                return false;
            }
            arguments =
            [
                "--module-path", fxLib,
                "--add-modules", "javafx.controls,javafx.fxml,javafx.web",
                "--add-opens", "java.base/java.lang=ALL-UNNAMED",
                "--add-opens", "java.base/java.util=ALL-UNNAMED",
                "--add-opens", "java.base/java.lang.reflect=ALL-UNNAMED",
                "--add-opens", "java.desktop/java.awt=ALL-UNNAMED",
                "--add-opens", "java.xml/com.sun.org.apache.xalan.internal.xsltc.runtime=ALL-UNNAMED",
                "--add-opens", "javafx.base/com.sun.javafx.runtime=ALL-UNNAMED",
                "--add-opens", "javafx.controls/com.sun.javafx.scene.control=ALL-UNNAMED",
                "--add-opens", "javafx.graphics/com.sun.javafx.scene=ALL-UNNAMED",
                "--add-opens", "javafx.graphics/com.sun.javafx.stage=ALL-UNNAMED",
                "-jar", jarPath
            ];
        }
        else if (toolId == "gui.hikvision")
        {
            // HikvisionExploitGUI 是 Java 8 + ClassFinal 加密 jar：
            // 1) 必须 -javaagent 指向自身解密类文件
            // 2) 必须 Java 8 运行时（解密后的字节码没有 StackMapTable，Java 9+ 拒绝加载）
            var java8 = FindJava8Runtime();
            if (java8 is null)
            {
                unavailableReason = "需要 Java 8 运行时（未在常见安装路径找到）。";
                return false;
            }
            java = java8;
            arguments = [$"-javaagent:{jarPath}", "-jar", jarPath];
        }
        else
        {
            arguments = ["-jar", jarPath];
        }
        return true;
    }

    private static string? FindJavaRuntime()
    {
        // GUI 进程的 PATH 可能与终端不同（双击启动走 Explorer 环境），除 PATH 外
        // 再探测常见安装位置与 Oracle javapath。
        var candidates = new List<string>();
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var javaRoot = Path.Combine(Environment.GetFolderPath(root), "Java");
            if (Directory.Exists(javaRoot))
                foreach (var dir in Directory.GetDirectories(javaRoot, "jdk-*").OrderByDescending(d => d))
                    candidates.Add(Path.Combine(dir, "bin", "java.exe"));
        }
        var javapath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Oracle", "Java", "javapath", "java.exe");
        candidates.Add(javapath);
        candidates.Add("java.exe");
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        foreach (var candidate in new[] { "java.exe", "java" })
        {
            var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => Path.Combine(path, candidate))
                .FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// 查找 Java 8 运行时：ClassFinal 加密的老工具（如 HikvisionExploitGUI）只能在
    /// Java 8 上运行，因为它们引用了 JavaFX 8 独有的内部类。
    /// </summary>
    internal static string? FindJava8Runtime()
    {
        var candidates = new List<string>();
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var javaRoot = Path.Combine(Environment.GetFolderPath(root), "Java");
            if (!Directory.Exists(javaRoot)) continue;
            foreach (var dir in Directory.GetDirectories(javaRoot, "*1.8*").OrderByDescending(d => d))
                candidates.Add(Path.Combine(dir, "bin", "java.exe"));
            foreach (var dir in Directory.GetDirectories(javaRoot, "jre8*").OrderByDescending(d => d))
                candidates.Add(Path.Combine(dir, "bin", "java.exe"));
        }
        // Oracle javapath 也可能有 Java 8（如果它是默认版本）
        var common = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Oracle", "Java", "javapath");
        if (Directory.Exists(common))
        {
            var javapathJava = Path.Combine(common, "java.exe");
            if (File.Exists(javapathJava))
            {
                try
                {
                    var start = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = javapathJava, Arguments = "-version", UseShellExecute = false,
                        RedirectStandardError = true, CreateNoWindow = true
                    };
                    using var p = System.Diagnostics.Process.Start(start);
                    if (p is not null)
                    {
                        var output = p.StandardError.ReadToEnd();
                        p.WaitForExit(3000);
                        if (output.Contains("\"1.8.")) return javapathJava;
                    }
                }
                catch { }
            }
        }
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static DesktopToolEntry BuiltIn(string id, string category, string name, string description) =>
        new(id, category, name, description, DesktopToolKind.BuiltIn, true);

    private static DesktopToolEntry NotBundled(string id, string category, string name, string description) =>
        new(id, category, name, description, DesktopToolKind.Gui, false,
            UnavailableReason: "未接入；当前版本不随程序分发");

    /// <summary>Bundled GUI tool declared without a user-root copy; PreferBundled activates it from bin/tools.</summary>
    private static DesktopToolEntry NotBundled(string id, string category, string name, string description,
        string bundledRelativeEntry) =>
        new(id, category, name, description, DesktopToolKind.Gui, false,
            Path: Path.Combine(AppContext.BaseDirectory, "tools", bundledRelativeEntry.Replace('/', Path.DirectorySeparatorChar)),
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
