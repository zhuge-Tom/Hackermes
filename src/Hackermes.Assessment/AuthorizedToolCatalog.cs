using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hackermes.Assessment;

public sealed record AuthorizedToolDescriptor(
    string Id, string Category, string Name, string Description, bool Available, string? UnavailableReason = null);

public sealed record AuthorizedToolInvocation(
    string AdapterId, string ExecutablePath, string WorkingDirectory, IReadOnlyList<string> Arguments,
    int TimeoutSeconds, int MaxOutputBytes, string EvidenceSource);

/// <summary>
/// Fixed adapters for locally supplied reconnaissance tools. Inputs are structured JSON and are
/// converted to ProcessStartInfo.ArgumentList entries; arbitrary command lines are never accepted.
/// </summary>
public static class AuthorizedToolCatalog
{
    public const string NmapQuick = "recon.nmap.quick";
    public const string NmapService = "recon.nmap.service";
    public const string DirsearchQuick = "recon.dirsearch.quick";
    public const string Wafw00fQuick = "recon.wafw00f.quick";
    public const string HttpHeadersProbe = "recon.http.headers";
    public const string HttpxProbe = "recon.httpx.probe";
    public const string HttpGetProbe = "recon.http.get";
    public const string DnsResolve = "recon.dns.resolve";
    public const string SimulationEcho = "simulation.echo";

    public static IReadOnlyList<AuthorizedToolDescriptor> Describe()
    {
        var nmap = NmapPath();
        var dirsearch = DirsearchPath();
        var python = PythonPath();
        var wordlist = DirsearchWordlistPath();
        var wafw00f = Wafw00fPath();
        var curl = CurlPath();
        return
        [
            Descriptor(NmapQuick, "信息收集 / 网络", "Nmap 快速端口", "TCP connect；精确主机，最多 64 个端口。", nmap),
            NmapServiceDescriptor(nmap),
            Descriptor(DirsearchQuick, "信息收集 / Web", "Dirsearch 常见路径", "低并发、非递归，使用随工具提供的小型字典。",
                File.Exists(dirsearch) && File.Exists(python) && File.Exists(wordlist) ? python : string.Empty),
            Descriptor(Wafw00fQuick, "Web 检测", "Wafw00f 基础识别", "精确 Web 目标、固定超时和有界输出的 WAF 识别。",
                File.Exists(wafw00f) && File.Exists(python) ? python : string.Empty),
            Descriptor(HttpHeadersProbe, "信息收集 / Web", "HTTP 响应头探测",
                "对精确 Web 目标发起单次 HEAD 请求，返回状态行与响应头。", curl),
            Descriptor(HttpGetProbe, "信息收集 / Web", "HTTP GET 探测",
                "对精确 Web 目标的固定路径发起单次 GET，返回状态、响应头与有界正文。", curl),
            Descriptor(DnsResolve, "信息收集 / 网络", "DNS 解析",
                "解析授权范围内的精确主机名，输出有界的 DNS 应答。", NslookupPath()),
            Descriptor(HttpxProbe, "信息收集 / Web", "Httpx 存活探测",
                "对精确 Web 目标发起单次 GET 探测，输出 URL 与状态码。", HttpxPath())
        ];
    }

    public static AssessmentStep NormalizeStep(AssessmentStep step, IReadOnlyList<string> allowedTargets)
    {
        var invocation = BuildInvocation(step, allowedTargets);
        return step with
        {
            Input = NormalizeJson(step.AdapterId, step.Input, allowedTargets),
            TimeoutSeconds = invocation.TimeoutSeconds,
            MaxOutputBytes = invocation.MaxOutputBytes
        };
    }

    public static AuthorizedToolInvocation BuildInvocation(AssessmentStep step, IReadOnlyList<string> allowedTargets)
    {
        var timeout = Math.Clamp(step.TimeoutSeconds, 1, 600);
        var output = Math.Clamp(step.MaxOutputBytes, 4_096, 262_144);
        if (step.AdapterId == SimulationEcho)
            return new(step.AdapterId, string.Empty, string.Empty, [], timeout, output, SimulationEcho);

        using var document = ParseObject(step.Input);
        var root = document.RootElement;
        var target = RequiredText(root, "target", 253).ToLowerInvariant();
        EnsureAuthorizedTarget(target, allowedTargets);

        if (step.AdapterId is NmapQuick or NmapService)
        {
            var ports = NormalizePorts(RequiredText(root, "ports", 512));
            var executable = RequireFile(NmapPath(), "Nmap");
            if (step.AdapterId == NmapService)
                _ = RequireFile(NmapServiceRuntimePath(executable), "Nmap nselib/lpeg-utility.lua");
            var arguments = new List<string> { "-sT", "-Pn", "-n", "--reason", "--max-retries", "1", "--host-timeout", timeout.ToString(CultureInfo.InvariantCulture) + "s" };
            if (step.AdapterId == NmapService) arguments.AddRange(["-sV", "--version-light"]);
            arguments.AddRange(["-p", ports, target, "-oN", "-"]);
            return new(step.AdapterId, executable, Path.GetDirectoryName(executable)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == DnsResolve)
        {
            var executable = RequireFile(NslookupPath(), "nslookup");
            return new(step.AdapterId, executable, Path.GetDirectoryName(executable)!, [target], timeout, output, step.AdapterId);
        }

        var (_, scheme, port) = ReadWebEndpoint(root);

        if (step.AdapterId == DirsearchQuick)
        {
            var script = RequireFile(DirsearchPath(), "dirsearch");
            var python = RequireFile(PythonPath(), "Python");
            var wordlist = RequireFile(DirsearchWordlistPath(), "dirsearch wordlist");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var arguments = new[] { script, "-u", url, "-w", wordlist, "--threads", "2", "--timeout", "3", "--retries", "0", "--max-time", timeout.ToString(CultureInfo.InvariantCulture) };
            return new(step.AdapterId, python, Path.GetDirectoryName(script)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == Wafw00fQuick)
        {
            var main = RequireFile(Wafw00fPath(), "Wafw00f");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var arguments = new[]
            {
                "-m", "wafw00f.main", url, "--no-colors", "-T",
                timeout.ToString(CultureInfo.InvariantCulture), "-o", "-", "-f", "json"
            };
            var packageRoot = Path.GetDirectoryName(Path.GetDirectoryName(main)!)!;
            return new(step.AdapterId, python, packageRoot, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == HttpHeadersProbe)
        {
            var executable = RequireFile(CurlPath(), "curl");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var arguments = new[]
            {
                "-sS", "-I", "--connect-timeout",
                Math.Min(10, timeout).ToString(CultureInfo.InvariantCulture),
                "--max-time", timeout.ToString(CultureInfo.InvariantCulture), url
            };
            return new(step.AdapterId, executable, Path.GetDirectoryName(executable)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == HttpGetProbe)
        {
            var executable = RequireFile(CurlPath(), "curl");
            var url = $"{scheme}://{FormatHost(target)}:{port}{NormalizeRequestPath(RootOptionalText(root, "path"))}";
            var arguments = new[]
            {
                "-sS", "-D", "-", "-o", "-",
                "--connect-timeout", Math.Min(10, timeout).ToString(CultureInfo.InvariantCulture),
                "--max-time", timeout.ToString(CultureInfo.InvariantCulture), url
            };
            return new(step.AdapterId, executable, Path.GetDirectoryName(executable)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == HttpxProbe)
        {
            var executable = RequireFile(HttpxPath(), "httpx");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var arguments = new[]
            {
                "-u", url, "-status-code", "-no-color", "-threads", "1",
                "-timeout", timeout.ToString(CultureInfo.InvariantCulture)
            };
            return new(step.AdapterId, executable, Path.GetDirectoryName(executable)!, arguments, timeout, output, step.AdapterId);
        }

        throw new ArgumentException($"Adapter '{step.AdapterId}' is not registered.");
    }

    private static string NormalizeJson(string adapterId, string input, IReadOnlyList<string> allowedTargets)
    {
        if (adapterId == SimulationEcho) return input[..Math.Min(input.Length, 262_144)];
        using var document = ParseObject(input);
        var root = document.RootElement;
        if (adapterId is NmapQuick or NmapService)
        {
            var target = RequiredText(root, "target", 253).ToLowerInvariant();
            EnsureAuthorizedTarget(target, allowedTargets);
            return JsonSerializer.Serialize(new { target, ports = NormalizePorts(RequiredText(root, "ports", 512)) });
        }
        if (adapterId == DnsResolve)
        {
            var dnsTarget = RequiredText(root, "target", 253).ToLowerInvariant();
            EnsureAuthorizedTarget(dnsTarget, allowedTargets);
            return JsonSerializer.Serialize(new { target = dnsTarget });
        }
        if (adapterId is not (DirsearchQuick or Wafw00fQuick or HttpHeadersProbe or HttpxProbe or HttpGetProbe))
            throw new ArgumentException($"Adapter '{adapterId}' is not registered.");
        var (webTarget, webScheme, webPort) = ReadWebEndpoint(root, allowedTargets);
        if (adapterId == HttpGetProbe)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                path = NormalizeRequestPath(RootOptionalText(root, "path"))
            });
        return JsonSerializer.Serialize(new { target = webTarget, scheme = webScheme, port = webPort });
    }

    /// <summary>Shared exact-target + http(s) endpoint shape for every web adapter.</summary>
    private static (string Target, string Scheme, int Port) ReadWebEndpoint(JsonElement root, IReadOnlyList<string>? allowedTargets = null)
    {
        var target = RequiredText(root, "target", 253).ToLowerInvariant();
        if (allowedTargets is not null) EnsureAuthorizedTarget(target, allowedTargets);
        var scheme = OptionalText(root, "scheme", 5, "http").ToLowerInvariant();
        if (scheme is not ("http" or "https")) throw new ArgumentException("scheme must be http or https.");
        var port = OptionalInt(root, "port", scheme == "https" ? 443 : 80, 1, 65535);
        return (target, scheme, port);
    }

    private static AuthorizedToolDescriptor Descriptor(string id, string category, string name, string description, string path) =>
        File.Exists(path) ? new(id, category, name, description, true) : new(id, category, name, description, false, "本地工具或运行时不存在");
    private static AuthorizedToolDescriptor NmapServiceDescriptor(string nmap)
    {
        if (!File.Exists(nmap))
            return new(NmapService, "信息收集 / 网络", "Nmap 基础服务", "在限定端口上进行轻量服务版本识别。",
                false, "本地工具或运行时不存在");
        return File.Exists(NmapServiceRuntimePath(nmap))
            ? new(NmapService, "信息收集 / 网络", "Nmap 基础服务", "在限定端口上进行轻量服务版本识别。", true)
            : new(NmapService, "信息收集 / 网络", "Nmap 基础服务", "在限定端口上进行轻量服务版本识别。",
                false, "Nmap 版本识别运行时不完整：缺少 nselib/lpeg-utility.lua");
    }
    private static string NmapServiceRuntimePath(string nmap) =>
        Path.Combine(Path.GetDirectoryName(nmap) ?? string.Empty, "nselib", "lpeg-utility.lua");
    private static string BundledRoot() => Environment.GetEnvironmentVariable("HACKERMES_BUNDLED_TOOLS_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "tools");
    private static string Bundled(params string[] parts) => Path.Combine([BundledRoot(), .. parts]);
    private static string NmapPath() => Environment.GetEnvironmentVariable("HACKERMES_NMAP_PATH") ?? Bundled("recon.nmap.terminal", "nmap.exe");
    private static string DirsearchPath() => Environment.GetEnvironmentVariable("HACKERMES_DIRSEARCH_PATH") ?? Bundled("recon.dirsearch.terminal", "dirsearch.py");
    private static string DirsearchWordlistPath() => Environment.GetEnvironmentVariable("HACKERMES_DIRSEARCH_WORDLIST") ?? Bundled("recon.dirsearch.terminal", "db", "templates", "admin.txt");
    private static string Wafw00fPath() => Environment.GetEnvironmentVariable("HACKERMES_WAFW00F_PATH") ?? Bundled("detect.wafw00f.terminal", "wafw00f", "main.py");
    private static string HttpxPath() => Environment.GetEnvironmentVariable("HACKERMES_HTTPX_PATH") ?? Bundled("recon.httpx.terminal", "httpx.exe");
    private static string CurlPath() => Environment.GetEnvironmentVariable("HACKERMES_CURL_PATH")
        ?? FindOnPath("curl.exe")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
    private static string NslookupPath() => Environment.GetEnvironmentVariable("HACKERMES_NSLOOKUP_PATH")
        ?? FindOnPath("nslookup.exe")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nslookup.exe");
    private static string? RootOptionalText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Bounded URL path for GET probes: absolute, whitespace/control-free, at most 256 characters.</summary>
    private static string NormalizeRequestPath(string? candidate)
    {
        var path = string.IsNullOrWhiteSpace(candidate) ? "/" : candidate.Trim();
        if (!path.StartsWith('/') || path.Length > 256)
            throw new ArgumentException("path must start with '/' and be at most 256 characters.");
        if (path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("path must not contain whitespace or control characters.");
        return path;
    }
    private static string PythonPath() => Environment.GetEnvironmentVariable("HACKERMES_PYTHON_PATH") ?? Bundled("_runtime", "python", "python.exe");
    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists);
    private static string RequireFile(string path, string label) => File.Exists(path) ? Path.GetFullPath(path) : throw new FileNotFoundException($"{label} is unavailable.", path);
    private static JsonDocument ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4_096) throw new ArgumentException("Tool input must be bounded JSON.");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new ArgumentException("Tool input must be a JSON object."); }
        return document;
    }
    private static string RequiredText(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new ArgumentException($"'{name}' is required.");
        var text = value.GetString()!.Trim(); return text.Length <= max ? text : throw new ArgumentException($"'{name}' is too long.");
    }
    private static string OptionalText(JsonElement root, string name, int max, string fallback) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? fallback).Trim()[..Math.Min((value.GetString() ?? fallback).Trim().Length, max)] : fallback;
    private static int OptionalInt(JsonElement root, string name, int fallback, int min, int max) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? Math.Clamp(number, min, max) : fallback;
    private static void EnsureAuthorizedTarget(string target, IReadOnlyList<string> allowedTargets)
    {
        // A scope created with the wildcard target authorizes every exact target ("全部授权").
        if (allowedTargets.Any(value => string.Equals(value.Trim(), "*", StringComparison.Ordinal))) return;
        if (!allowedTargets.Any(value => string.Equals(value.Trim(), target, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"Target '{target}' is outside the approved exact-target scope.");
    }
    private static string NormalizePorts(string value)
    {
        var ports = new SortedSet<int>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) throw new ArgumentException("Ports must be comma-separated numbers from 1 to 65535.");
            ports.Add(port);
            if (ports.Count > 64) throw new ArgumentException("At most 64 ports are allowed.");
        }
        if (ports.Count == 0) throw new ArgumentException("At least one port is required.");
        return string.Join(',', ports);
    }
    private static string FormatHost(string target) => target.Contains(':', StringComparison.Ordinal) ? $"[{target}]" : target;
}
