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
    int TimeoutSeconds, int MaxOutputBytes, string EvidenceSource,
    string? SecretReference = null, IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

/// <summary>A bundled, bounded resource (wordlist / payload corpus) a scan adapter may consume.</summary>
public sealed record AuthorizedToolResource(string Id, string Name, string Path);

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
    public const string ProbeSqlmapInject = "probe.sqlmap.inject";
    public const string ProbeUnauthorizedAccess = "probe.unauthorized.access";
    public const string ProbeParamCorpus = "probe.param.corpus";
    public const string ReconSubdomainEnum = "recon.subdomain.enum";
    public const string ReconGitLeakScan = "recon.git_leak.scan";
    public const string ReconSvnLeakScan = "recon.svn_leak.scan";
    public const string ReconDsStoreScan = "recon.ds_store.scan";
    public const string ReconSwaggerApiEnum = "recon.swagger_api.enum";
    public const string DetectWeblogicT3Scan = "detect.weblogic_t3.scan";
    public const string DetectFastjsonJndiScan = "detect.fastjson_jndi.scan";
    public const string ExploitVcenterVerify = "exploit.vcenter.verify";
    public const string ExploitHeapdumpAnalyze = "exploit.heapdump.analyze";
    public const string DetectOaPocList = "detect.oa_poc.list";
    public const string DetectOaPocProbe = "detect.oa_poc.probe";
    public const string DetectShiroScan = "detect.shiro.scan";
    public const string DetectStruts2Scan = "detect.struts2.scan";
    public const string DetectNacosScan = "detect.nacos.scan";
    public const string ProbeCloudAkskVerify = "probe.cloud_aksk.verify";
    public const string ExploitFastjsonPayload = "exploit.fastjson_payload.generate";
    public const string SimulationEcho = "simulation.echo";

    public static IReadOnlyList<AuthorizedToolDescriptor> Describe()
    {
        var nmap = NmapPath();
        var dirsearch = DirsearchPath();
        var python = PythonPath();
        var wordlist = DirsearchWordlistPath();
        var wafw00f = Wafw00fPath();
        var curl = CurlPath();
        var sqlmap = SqlmapPath();
        var unauthorized = UnauthorizedPath();
        var gitHack = GitHackScriptPath();
        var svnExploit = SvnExploitScriptPath();
        var dsStoreExp = DsStoreScriptPath();
        var swaggerHack = SwaggerHackScriptPath();
        var weblogicScan = WeblogicScanScriptPath();
        var fastjsonJndi = FastjsonJndiExePath();
        var vcenterKiller = VcenterKillerExePath();
        var heapdumpSpider = HeapdumpSpiderJarPath();
        var oaPocRunner = OaPocRunnerPath();
        var shiroJar = ShiroToolJarPath();
        var struts2Scan = Struts2ScanScriptPath();
        var nacosProbe = NacosProbeScriptPath();
        var fastjsonPayloadJar = FastjsonPayloadJarPath();
        var pythonReady = File.Exists(python);
        var javaReady = JavaPath() is { Length: > 0 } && File.Exists(JavaPath());
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
                "对精确 Web 目标发起单次 GET 探测，输出 URL 与状态码。", HttpxPath()),
            Descriptor(ProbeSqlmapInject, "漏洞验证 / 注入", "Sqlmap SQL 注入确认",
                "对单个授权参数运行有界 sqlmap（level 1 / risk 1 / Boolean+Error 技术），确认则报 High 并给出 PoC。",
                File.Exists(sqlmap) && File.Exists(python) ? python : string.Empty),
            Descriptor(ProbeUnauthorizedAccess, "漏洞验证 / 访问控制", "未授权访问检测",
                "对授权站点检测常见未授权访问端点（swagger/actuator/springboot/docker 等），命中报 High 并给出 URL PoC。",
                File.Exists(unauthorized) && File.Exists(python) ? python : string.Empty),
            Descriptor(ReconSubdomainEnum, "信息收集 / Web", "子域枚举",
                "对授权根域用内置子域字典做 DNS 枚举，输出解析成功的子域。",
                File.Exists(SubdomainEnumScriptPath()) && File.Exists(SubdomainWordlistPath()) && File.Exists(python) ? python : string.Empty),
            Descriptor(ProbeParamCorpus, "漏洞验证 / 注入", "语料候选探测",
                "用内置 payload 语料对一个参数做有界请求，输出行为差异候选（Medium，需复核）。",
                File.Exists(ParamCorpusProbeScriptPath()) && File.Exists(python) && CorpusResources().Any(item => item.Id != "subdomains") ? python : string.Empty),
            Descriptor(ReconGitLeakScan, "信息收集 / 泄露", "GitHack .git 泄露恢复",
                "当 /.git/ 疑似泄露（dirsearch 命中）时使用：还原远端 git 仓库并列出源码文件。建议超时 ≥180 秒。",
                File.Exists(gitHack) && pythonReady ? python : string.Empty),
            Descriptor(ReconSvnLeakScan, "信息收集 / 泄露", "SvnExploit .svn 泄露枚举",
                "当 /.svn/ 疑似泄露时使用：解析 wc.db 列出受版本控制的源码文件清单。",
                File.Exists(svnExploit) && pythonReady ? python : string.Empty),
            Descriptor(ReconDsStoreScan, "信息收集 / 泄露", "DS_Store 目录泄露枚举",
                "当 /.DS_Store 疑似泄露时使用：解析并递归枚举隐藏目录结构。",
                File.Exists(dsStoreExp) && pythonReady ? python : string.Empty),
            Descriptor(ReconSwaggerApiEnum, "信息收集 / 泄露", "Swagger API 自动枚举",
                "发现 swagger-ui / api-docs 后使用：解析文档并批量请求全部接口，输出可用 API 清单。",
                File.Exists(swaggerHack) && pythonReady ? python : string.Empty),
            Descriptor(DetectWeblogicT3Scan, "漏洞验证 / 中间件", "Weblogic T3 反序列化检测",
                "对精确主机的 T3 端口（默认 7001）检测已知 Weblogic 反序列化 CVE；命中报 High。",
                File.Exists(weblogicScan) && pythonReady ? python : string.Empty),
            Descriptor(DetectFastjsonJndiScan, "漏洞验证 / 反序列化", "Fastjson/Jackson JNDI 检测",
                "对精确 Web 端点发送带 payload 的 JSON（可配 ldap/rmi 回连地址定位可用 gadget）。",
                File.Exists(fastjsonJndi) ? fastjsonJndi : string.Empty),
            Descriptor(ExploitVcenterVerify, "漏洞利用 / 中间件", "vCenter 漏洞验证",
                "对已授权 vCenter 验证 CVE-2021-21972/21985/22005、CVE-2022-22954 与 Log4j；属于利用型操作，逐次审批。",
                File.Exists(vcenterKiller) && File.Exists(VcenterShellPath()) ? vcenterKiller : string.Empty),
            new(ExploitHeapdumpAnalyze, "漏洞验证 / 信息泄露", "Heapdump 敏感信息提取",
                "对已下载到工件库的 Java heapdump 文件提取数据库连接、Shiro key、云 AK/SK 等敏感项。需 Java 运行时。",
                File.Exists(heapdumpSpider) && javaReady && ArtifactRoot().Length > 0,
                !File.Exists(heapdumpSpider) ? "本地工具不存在"
                    : !javaReady ? "未找到 Java 运行时"
                    : ArtifactRoot().Length == 0 ? "未配置工件存储根目录" : null),
            Descriptor(DetectOaPocList, "漏洞验证 / OA", "OA POC 清单枚举",
                "列出内置 OA POC 库的模块与条目（泛微/致远/通达/用友等 97 条），用于按指纹选择探测模块。",
                File.Exists(oaPocRunner) && pythonReady ? python : string.Empty),
            Descriptor(DetectOaPocProbe, "漏洞验证 / OA", "OA POC 定向探测",
                "指纹命中国内 OA 后使用：对授权目标探测指定模块的 POC 集（word/status 匹配，命中报 High）。先 detect.oa_poc.list 查模块。",
                File.Exists(oaPocRunner) && pythonReady ? python : string.Empty),
            Descriptor(DetectShiroScan, "漏洞验证 / 反序列化", "Shiro rememberMe key 检测",
                "对授权 Web 端点爆破 Shiro rememberMe key（需 Java 运行时；命中后进入 gadget 菜单即视为可用）。",
                File.Exists(shiroJar) && javaReady ? JavaPath() : string.Empty),
            Descriptor(DetectStruts2Scan, "漏洞验证 / 中间件", "Struts2 全版本检测",
                "对授权 Web 端点检测 S2-001~S2-057 系列 OGNL 漏洞（-q 只保留命中；命中报 High）。",
                File.Exists(struts2Scan) && pythonReady ? python : string.Empty),
            Descriptor(DetectNacosScan, "漏洞验证 / 中间件", "Nacos 只读探测",
                "对授权 Nacos 端点检测未授权用户列表/配置读取/控制台暴露等只读项。",
                File.Exists(nacosProbe) && pythonReady ? python : string.Empty),
            Descriptor(ExploitFastjsonPayload, "漏洞利用 / 反序列化", "Fastjson payload 生成",
                "fastjson 命中后使用：生成指定 gadget 的利用 payload 文本作 PoC 证据（利用型，需同目标检测证据）。",
                File.Exists(fastjsonPayloadJar) && javaReady ? JavaPath() : string.Empty),
            Descriptor(ProbeCloudAkskVerify, "漏洞验证 / 云凭证", "云 AK/SK 只读验证",
                "验证评估中发现的云凭证（heapdump/git 泄露产出）：只读 ls/perm 列资源与权限，" +
                "凭证经 DPAPI 暂存、进程环境注入，计划与证据零密钥。先 cloud_credential_stage 取 token。" +
                "接管控制台等利用操作不做。",
                File.Exists(CloudCfPath()) ? CloudCfPath() : string.Empty)
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

        if (step.AdapterId == ReconSubdomainEnum)
        {
            var script = RequireFile(SubdomainEnumScriptPath(), "subdomain_enum");
            var python = RequireFile(PythonPath(), "Python");
            var wordlist = RequireFile(SubdomainWordlistPath(), "subdomain wordlist");
            var domain = RequiredText(root, "domain", 253).ToLowerInvariant();
            EnsureAuthorizedTarget(domain, allowedTargets);
            return new(step.AdapterId, python, Path.GetDirectoryName(script)!, [script, domain, wordlist], timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ExploitHeapdumpAnalyze)
        {
            var jar = RequireFile(HeapdumpSpiderJarPath(), "JDumpSpider");
            var java = RequireFile(JavaPath(), "Java runtime");
            var storeRoot = ArtifactRoot();
            if (storeRoot.Length == 0)
                throw new ArgumentException("The artifact store root is not configured for heapdump analysis.");
            var artifactName = RequiredText(root, "file", 120);
            if (artifactName.IndexOfAny(['/', '\\']) >= 0 || artifactName is "." or "..")
                throw new ArgumentException("file must be an artifact file name without path separators.");
            var fullPath = Path.GetFullPath(Path.Combine(storeRoot, artifactName));
            var normalizedRoot = Path.GetFullPath(storeRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The artifact file must stay inside the Hackermes artifact store.");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The artifact was not found in the Hackermes artifact store; download it first with agent_download_artifact.", artifactName);
            return new(step.AdapterId, java, Path.GetDirectoryName(jar)!, ["-Dfile.encoding=UTF-8", "-jar", jar, fullPath], timeout, output, step.AdapterId);
        }

        if (step.AdapterId == DetectOaPocList)
        {
            var runner = RequireFile(OaPocRunnerPath(), "OA POC runner");
            var python = RequireFile(PythonPath(), "Python");
            return new(step.AdapterId, python, Path.GetDirectoryName(runner)!, [runner, "--list"], timeout, output, step.AdapterId);
        }

        var target = RequiredText(root, "target", 253).ToLowerInvariant();
        EnsureAuthorizedTarget(target, allowedTargets);

        if (step.AdapterId == DetectWeblogicT3Scan)
        {
            var script = RequireFile(WeblogicScanScriptPath(), "WeblogicScan");
            var python = RequireFile(PythonPath(), "Python");
            var t3Port = OptionalInt(root, "port", 7001, 1, 65535);
            var arguments = new[] { script, "-u", target, "-p", t3Port.ToString(CultureInfo.InvariantCulture) };
            return new(step.AdapterId, python, ScratchWorkingDirectory(), arguments, timeout, output, step.AdapterId);
        }

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

        if (step.AdapterId == ProbeSqlmapInject)
        {
            var script = RequireFile(SqlmapPath(), "sqlmap");
            var python = RequireFile(PythonPath(), "Python");
            var parameter = RequiredText(root, "parameter", 64);
            var value = OptionalText(root, "value", 128, "1");
            var path = NormalizeRequestPath(RootOptionalText(root, "path"));
            var url = $"{scheme}://{FormatHost(target)}:{port}{path}?{parameter}={Uri.EscapeDataString(value)}";
            var arguments = new[]
            {
                script, "-u", url, "--batch", "--level", "1", "--risk", "1", "--technique=BE",
                "--threads", "1", "--timeout", Math.Min(20, timeout).ToString(CultureInfo.InvariantCulture),
                "--disable-coloring"
            };
            return new(step.AdapterId, python, Path.GetDirectoryName(script)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ProbeParamCorpus)
        {
            var script = RequireFile(ParamCorpusProbeScriptPath(), "param_corpus_probe");
            var python = RequireFile(PythonPath(), "Python");
            var parameter = RequiredText(root, "parameter", 64);
            var value = OptionalText(root, "value", 128, "1");
            var path = NormalizeRequestPath(RootOptionalText(root, "path"));
            var corpusId = OptionalText(root, "corpus", 64, "sqli-auth-bypass");
            var corpusFile = CorpusFile(corpusId) ?? throw new ArgumentException($"Unknown corpus '{corpusId}'.");
            var corpus = RequireFile(CorpusPath(corpusFile), "payload corpus");
            var baseUrl = $"{scheme}://{FormatHost(target)}:{port}{path}";
            var arguments = new[] { script, baseUrl, parameter, value, corpus, "40" };
            return new(step.AdapterId, python, Path.GetDirectoryName(script)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ProbeUnauthorizedAccess)
        {
            var script = RequireFile(UnauthorizedPath(), "Unauthorized-Vul");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var arguments = new[] { script, "-u", url, "-t", "1" };
            return new(step.AdapterId, python, Path.GetDirectoryName(script)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ReconGitLeakScan)
        {
            var script = RequireFile(GitHackScriptPath(), "GitHack");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}/.git/";
            return new(step.AdapterId, python, ScratchWorkingDirectory(), [script, url], timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ReconSvnLeakScan)
        {
            var script = RequireFile(SvnExploitScriptPath(), "SvnExploit");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}/.svn/";
            return new(step.AdapterId, python, ScratchWorkingDirectory(),
                [script, "-u", url, "--thread", "5"], timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ReconDsStoreScan)
        {
            var script = RequireFile(DsStoreScriptPath(), "ds_store_exp");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}/.DS_Store";
            return new(step.AdapterId, python, ScratchWorkingDirectory(), [script, url], timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ReconSwaggerApiEnum)
        {
            var script = RequireFile(SwaggerHackScriptPath(), "swagger-hack");
            var python = RequireFile(PythonPath(), "Python");
            // check() only inspects the URL it is given, so the agent supplies the
            // api-docs path discovered by dirsearch/unauthorized probes (default "/").
            var url = $"{scheme}://{FormatHost(target)}:{port}{NormalizeRequestPath(RootOptionalText(root, "path"))}";
            return new(step.AdapterId, python, ScratchWorkingDirectory(), [script, "-u", url], timeout, output, step.AdapterId);
        }

        if (step.AdapterId == DetectOaPocProbe)
        {
            var runner = RequireFile(OaPocRunnerPath(), "OA POC runner");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}";
            var module = RequiredText(root, "module", 64);
            if (module.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
                throw new ArgumentException("module may contain letters, digits, dashes and underscores only.");
            if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(runner)!, "book", module)))
                throw new ArgumentException($"Unknown OA POC module '{module}'; run detect.oa_poc.list to enumerate modules.");
            var arguments = new List<string> { runner, "--target", url, "--module", module,
                "--timeout", Math.Min(10, timeout).ToString(CultureInfo.InvariantCulture) };
            if (RootOptionalText(root, "poc") is { Length: > 0 } poc)
            {
                if (poc.Length > 120 || poc.IndexOfAny(['/', '\\']) >= 0)
                    throw new ArgumentException("poc must be a yaml file name without path separators.");
                arguments.AddRange(["--poc", poc]);
            }
            return new(step.AdapterId, python, Path.GetDirectoryName(runner)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ProbeCloudAkskVerify)
        {
            var executable = RequireFile(CloudCfPath(), "cf");
            var provider = OptionalText(root, "provider", 12, "alibaba").ToLowerInvariant();
            if (provider is not ("alibaba" or "aws" or "tencent" or "huawei"))
                throw new ArgumentException("provider must be alibaba, aws, tencent or huawei.");
            var command = OptionalText(root, "command", 8, "ls").ToLowerInvariant();
            if (command is not ("ls" or "perm"))
                throw new ArgumentException("command must be ls or perm (read-only verification).");
            var token = RequiredText(root, "credentialToken", 24);
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^cc-[0-9a-f]{16}$"))
                throw new ArgumentException("credentialToken must be an opaque cc- token issued by cloud_credential_stage; raw keys are never accepted.");
            // Only the opaque token travels in the plan/ticket; the ToolHost resolves the
            // DPAPI-staged credential itself and injects it as process environment.
            return new(step.AdapterId, executable, Path.GetDirectoryName(executable)!,
                [executable, provider, command], timeout, output, step.AdapterId,
                SecretReference: $"cloudcred.{token}");
        }

        if (step.AdapterId == DetectShiroScan)
        {
            var jar = RequireFile(ShiroToolJarPath(), "shiro_tool");
            var java = RequireFile(JavaPath(), "Java runtime");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            return new(step.AdapterId, java, Path.GetDirectoryName(jar)!,
                ["-Dfile.encoding=UTF-8", "-jar", jar, url], timeout, output, step.AdapterId);
        }

        if (step.AdapterId == DetectStruts2Scan)
        {
            var script = RequireFile(Struts2ScanScriptPath(), "Struts2Scan");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var arguments = new[] { script, "-u", url, "-q",
                "--timeout", Math.Min(10, timeout).ToString(CultureInfo.InvariantCulture) };
            return new(step.AdapterId, python, ScratchWorkingDirectory(), arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == DetectNacosScan)
        {
            var script = RequireFile(NacosProbeScriptPath(), "nacos_probe");
            var python = RequireFile(PythonPath(), "Python");
            var url = $"{scheme}://{FormatHost(target)}:{port}";
            return new(step.AdapterId, python, Path.GetDirectoryName(script)!,
                [script, "--target", url, "--timeout", Math.Min(8, timeout).ToString(CultureInfo.InvariantCulture)],
                timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ExploitFastjsonPayload)
        {
            var jar = RequireFile(FastjsonPayloadJarPath(), "FastjsonExploit");
            var java = RequireFile(JavaPath(), "Java runtime");
            var payload = RequiredText(root, "payload", 32);
            if (payload is not ("TemplatesImpl1" or "TemplatesImpl2" or "BasicDataSource1" or "BasicDataSource2"))
                throw new ArgumentException("payload must be one of: TemplatesImpl1, TemplatesImpl2, BasicDataSource1, BasicDataSource2.");
            var command = RequiredText(root, "command", 128);
            if (command.Any(char.IsControl))
                throw new ArgumentException("command must not contain control characters.");
            var arguments = new[]
            {
                "--add-opens", "java.xml/com.sun.org.apache.xalan.internal.xsltc.runtime=ALL-UNNAMED",
                "-jar", jar, payload, $"cmd:{command}"
            };
            return new(step.AdapterId, java, Path.GetDirectoryName(jar)!, arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == DetectFastjsonJndiScan)
        {            var executable = RequireFile(FastjsonJndiExePath(), "JsonExp");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var template = RequireFile(Path.Combine(Path.GetDirectoryName(executable)!, "template", "fastjson.txt"), "JsonExp payload template");
            // Measured behaviour (stage-3 calibration): JsonExp refuses to run without a
            // callback (-l/-r/--dnslog); the agent must supply the operator's listener.
            var hasCallback = RootOptionalText(root, "ldap") is { Length: > 0 } ||
                RootOptionalText(root, "rmi") is { Length: > 0 };
            if (!hasCallback)
                throw new ArgumentException("one callback address (ldap or rmi, run by the operator) is required for fastjson detection.");
            var arguments = new List<string> { executable, "-u", url, "-to", Math.Min(15, timeout).ToString(CultureInfo.InvariantCulture), "-f", template };
            if (RootOptionalText(root, "ldap") is { Length: > 0 } ldap)
                arguments.AddRange(["-l", NormalizeCallbackAddress(ldap)]);
            if (RootOptionalText(root, "rmi") is { Length: > 0 } rmi)
                arguments.AddRange(["-r", NormalizeCallbackAddress(rmi)]);
            var method = OptionalText(root, "method", 4, "post").ToLowerInvariant();
            if (method is not ("get" or "post"))
                throw new ArgumentException("method must be get or post.");
            arguments.AddRange(["-t", method]);
            return new(step.AdapterId, executable, ScratchWorkingDirectory(), arguments, timeout, output, step.AdapterId);
        }

        if (step.AdapterId == ExploitVcenterVerify)
        {
            var executable = RequireFile(VcenterKillerExePath(), "VcenterKiller");
            var url = $"{scheme}://{FormatHost(target)}:{port}/";
            var mode = RequiredText(root, "mode", 12);
            if (mode is not ("21972" or "21985" or "22005" or "22954" or "22972" or "log4center"))
                throw new ArgumentException("mode must be one of: 21972, 21985, 22005, 22954, 22972, log4center.");
            var arguments = new List<string> { executable, "-u", url, "-m", mode };
            if (RootOptionalText(root, "command") is { Length: > 0 } command)
                arguments.AddRange(["-c", BoundCommand(command)]);
            if (RootOptionalText(root, "action") is { Length: > 0 } action)
            {
                switch (action.ToLowerInvariant())
                {
                    case "scan":
                        arguments.AddRange(["-t", "scan"]);
                        break;
                    case "upload":
                        arguments.AddRange(["-f", RequireFile(VcenterShellPath(), "verification shell")]);
                        break;
                    case "getcookie":
                        break;
                    default:
                        throw new ArgumentException("action must be one of: scan, upload, getcookie.");
                }
            }
            if (RootOptionalText(root, "remote") is { Length: > 0 } remote)
                arguments.AddRange(["-r", NormalizeRemoteUrl(remote)]);
            return new(step.AdapterId, executable, ScratchWorkingDirectory(), arguments, timeout, output, step.AdapterId);
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
        if (adapterId == ReconSubdomainEnum)
        {
            var domain = RequiredText(root, "domain", 253).ToLowerInvariant();
            EnsureAuthorizedTarget(domain, allowedTargets);
            return JsonSerializer.Serialize(new { domain });
        }
        if (adapterId == DetectWeblogicT3Scan)
        {
            var t3Target = RequiredText(root, "target", 253).ToLowerInvariant();
            EnsureAuthorizedTarget(t3Target, allowedTargets);
            return JsonSerializer.Serialize(new { target = t3Target, port = OptionalInt(root, "port", 7001, 1, 65535) });
        }
        if (adapterId == ExploitHeapdumpAnalyze)
            return JsonSerializer.Serialize(new { file = RequiredText(root, "file", 120) });
        if (adapterId == DetectOaPocList)
            return "{}";
        if (adapterId is not (DirsearchQuick or Wafw00fQuick or HttpHeadersProbe or HttpxProbe or HttpGetProbe
            or ProbeSqlmapInject or ProbeUnauthorizedAccess or ProbeParamCorpus
            or ReconGitLeakScan or ReconSvnLeakScan or ReconDsStoreScan or ReconSwaggerApiEnum
            or DetectFastjsonJndiScan or ExploitVcenterVerify or DetectOaPocProbe
            or DetectShiroScan or DetectStruts2Scan or DetectNacosScan or ExploitFastjsonPayload
            or ProbeCloudAkskVerify))
            throw new ArgumentException($"Adapter '{adapterId}' is not registered.");
        var (webTarget, webScheme, webPort) = ReadWebEndpoint(root, allowedTargets);
        if (adapterId is HttpGetProbe or ReconSwaggerApiEnum)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                path = NormalizeRequestPath(RootOptionalText(root, "path"))
            });
        if (adapterId is ProbeSqlmapInject or ProbeParamCorpus)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                path = NormalizeRequestPath(RootOptionalText(root, "path")),
                parameter = RequiredText(root, "parameter", 64),
                value = OptionalText(root, "value", 128, "1"),
                corpus = OptionalText(root, "corpus", 64, "sqli-auth-bypass")
            });
        if (adapterId == ExploitFastjsonPayload)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                payload = RequiredText(root, "payload", 32),
                command = RequiredText(root, "command", 128)
            });
        if (adapterId == ProbeCloudAkskVerify)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                provider = OptionalText(root, "provider", 12, "alibaba").ToLowerInvariant(),
                command = OptionalText(root, "command", 8, "ls").ToLowerInvariant(),
                credentialToken = RequiredText(root, "credentialToken", 24)
            });
        if (adapterId == DetectOaPocProbe)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                module = RequiredText(root, "module", 64),
                poc = RootOptionalText(root, "poc") ?? string.Empty
            });
        if (adapterId == DetectFastjsonJndiScan)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                ldap = RootOptionalText(root, "ldap") ?? string.Empty,
                rmi = RootOptionalText(root, "rmi") ?? string.Empty,
                method = OptionalText(root, "method", 4, "post").ToLowerInvariant()
            });
        if (adapterId == ExploitVcenterVerify)
            return JsonSerializer.Serialize(new
            {
                target = webTarget, scheme = webScheme, port = webPort,
                mode = RequiredText(root, "mode", 12),
                command = RootOptionalText(root, "command") ?? string.Empty,
                action = RootOptionalText(root, "action") ?? string.Empty,
                remote = RootOptionalText(root, "remote") ?? string.Empty
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
    private static string SqlmapPath() => Environment.GetEnvironmentVariable("HACKERMES_SQLMAP_PATH") ?? Bundled("exploit.sqlmap.terminal", "sqlmap.py");
    private static string UnauthorizedPath() => Environment.GetEnvironmentVariable("HACKERMES_UNAUTHORIZED_PATH") ?? Bundled("detect.unauthorized.terminal", "Unauthorized-Vul.py");
    private static string SubdomainEnumScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_SUBDOMAIN_ENUM_PATH") ?? Bundled("recon.subdomain.terminal", "subdomain_enum.py");
    private static string SubdomainWordlistPath() => Environment.GetEnvironmentVariable("HACKERMES_SUBDOMAIN_WORDLIST") ?? Bundled("recon.subdomain.terminal", "subdomains.txt");
    private static string ParamCorpusProbeScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_PARAM_CORPUS_PROBE_PATH") ?? Bundled("probe.param.corpus.terminal", "param_corpus_probe.py");
    private static string GitHackScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_GITHACK_PATH") ?? Bundled("recon.git-leak.terminal", "GitHack.py");
    private static string SvnExploitScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_SVN_EXPLOIT_PATH") ?? Bundled("recon.svn-leak.terminal", "SvnExploit.py");
    private static string DsStoreScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_DS_STORE_EXP_PATH") ?? Bundled("recon.ds-store.terminal", "ds_store_exp.py");
    private static string SwaggerHackScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_SWAGGER_HACK_PATH") ?? Bundled("recon.swagger-api.terminal", "swagger-hack2.0.py");
    private static string WeblogicScanScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_WEBLOGIC_SCAN_PATH") ?? Bundled("detect.weblogic-t3.terminal", "WeblogicScan.py");
    private static string FastjsonJndiExePath() => Environment.GetEnvironmentVariable("HACKERMES_FASTJSON_JNDI_PATH") ?? Bundled("detect.fastjson-jndi.terminal", "JsonExp.exe");
    private static string VcenterKillerExePath() => Environment.GetEnvironmentVariable("HACKERMES_VCENTER_KILLER_PATH") ?? Bundled("exploit.vcenter.terminal", "main.exe");
    private static string VcenterShellPath() => Environment.GetEnvironmentVariable("HACKERMES_VCENTER_SHELL_PATH") ?? Bundled("exploit.vcenter.terminal", "shell-verify.jsp");
    private static string OaPocRunnerPath() => Environment.GetEnvironmentVariable("HACKERMES_OA_POC_RUNNER_PATH") ?? Bundled("detect.oa-poc.terminal", "oa_poc_runner.py");
    private static string ShiroToolJarPath() => Environment.GetEnvironmentVariable("HACKERMES_SHIRO_TOOL_PATH") ?? Bundled("detect.shiro.terminal", "shiro_tool.jar");
    private static string Struts2ScanScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_STRUTS2_SCAN_PATH") ?? Bundled("detect.struts2.terminal", "Struts2Scan.py");
    private static string NacosProbeScriptPath() => Environment.GetEnvironmentVariable("HACKERMES_NACOS_PROBE_PATH") ?? Bundled("detect.nacos.terminal", "nacos_probe.py");
    private static string FastjsonPayloadJarPath() => Environment.GetEnvironmentVariable("HACKERMES_FASTJSON_PAYLOAD_JAR") ?? Bundled("exploit.fastjson-payload.terminal", "FastjsonExploit-0.1-beta2-all.jar");
    private static string CloudCfPath() => Environment.GetEnvironmentVariable("HACKERMES_CF_PATH") ?? Bundled("probe.cloud-aksk.terminal", "cf.exe");

    /// <summary>
    /// Maps one staged cloud credential to the SDK-standard environment variables the
    /// vendored cf binary reads. Pure function — the values themselves only travel from
    /// the DPAPI secret store into the ToolHost child's process environment, never into
    /// the plan, the ticket or any log.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CloudCredentialEnvironment(
        string provider, string accessKey, string secretKey, string? sessionToken)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (provider)
        {
            case "alibaba":
                env["ALIBABA_CLOUD_ACCESS_KEY_ID"] = accessKey;
                env["ALIBABA_CLOUD_ACCESS_KEY_SECRET"] = secretKey;
                env["ALIBABACLOUD_ACCESS_KEY_ID"] = accessKey;
                env["ALIBABACLOUD_ACCESS_KEY_SECRET"] = secretKey;
                if (!string.IsNullOrEmpty(sessionToken))
                    env["ALIBABA_CLOUD_SECURITY_TOKEN"] = sessionToken;
                break;
            case "aws":
                env["AWS_ACCESS_KEY_ID"] = accessKey;
                env["AWS_SECRET_ACCESS_KEY"] = secretKey;
                env["AWS_ACCESS_KEY"] = accessKey;
                env["AWS_SECRET_KEY"] = secretKey;
                if (!string.IsNullOrEmpty(sessionToken))
                    env["AWS_SESSION_TOKEN"] = sessionToken;
                break;
            case "tencent":
                env["TENCENTCLOUD_SECRET_ID"] = accessKey;
                env["TENCENTCLOUD_SECRET_KEY"] = secretKey;
                break;
            case "huawei":
                env["HUAWEICLOUD_SDK_AK"] = accessKey;
                env["HUAWEICLOUD_SDK_SK"] = secretKey;
                break;
            default:
                throw new ArgumentException("provider must be alibaba, aws, tencent or huawei.");
        }
        return env;
    }
    private static string HeapdumpSpiderJarPath() => Environment.GetEnvironmentVariable("HACKERMES_HEAPDUMP_SPIDER_PATH") ?? Bundled("exploit.heapdump.terminal", "JDumpSpider-1.1-SNAPSHOT-full.jar");
    private static string JavaPath() => Environment.GetEnvironmentVariable("HACKERMES_JAVA_PATH") ?? FindOnPath("java.exe") ?? string.Empty;
    private static string ArtifactRoot() => Environment.GetEnvironmentVariable("HACKERMES_AGENT_ARTIFACT_ROOT") ?? string.Empty;

    /// <summary>Callback endpoint for JNDI payload injection: bounded host[:port][/path], no scheme or whitespace.</summary>
    private static string NormalizeCallbackAddress(string candidate)
    {
        var value = candidate.Trim();
        if (value.Length is 0 or > 200 || value.Any(char.IsWhiteSpace) || value.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("callback must be a bounded host:port[/path] address without a scheme.");
        var colon = value.IndexOf(':');
        var host = colon < 0 ? value : value[..colon];
        var rest = colon < 0 ? string.Empty : value[(colon + 1)..];
        if (host.Length == 0 || host.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("callback host may contain letters, digits, dots, dashes and underscores only.");
        if (rest.Length == 0)
            throw new ArgumentException("callback must include a port.");
        var portPart = rest.Split('/')[0];
        if (!int.TryParse(portPart, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("callback port must be a number from 1 to 65535.");
        return value;
    }

    /// <summary>Remote JNDI/LDAP URL for vCenter exploitation: bounded rmi:// or ldap:// URL.</summary>
    private static string NormalizeRemoteUrl(string candidate)
    {
        var value = candidate.Trim();
        if (value.Length is 0 or > 200 || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("remote must be a bounded rmi:// or ldap:// URL without whitespace.");
        if (!value.StartsWith("rmi://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("ldap://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("remote must start with rmi:// or ldap://.");
        return value;
    }

    /// <summary>Bounded verification command: no control characters or newlines.</summary>
    private static string BoundCommand(string candidate)
    {
        var value = candidate.Trim();
        if (value.Length is 0 or > 128 || value.Any(char.IsControl))
            throw new ArgumentException("command must be 1-128 characters without control characters.");
        return value;
    }

    /// <summary>
    /// One fresh scratch directory per invocation for adapters whose tools write artifacts
    /// (recovered sources, csv/log output) into their working directory. Keeps the bundled
    /// tools directory immutable and works under read-only installs; tools print absolute
    /// output paths so the evidence still points at the artifacts.
    /// </summary>
    private static string ScratchWorkingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hackermes-toolhost", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CorpusPath(string name) => Environment.GetEnvironmentVariable("HACKERMES_CORPUS_ROOT") is { Length: > 0 } env
        ? Path.Combine(env, name)
        : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(SubdomainEnumScriptPath()))) ?? string.Empty, "resources", "corpus", name);

    private static readonly (string Id, string Name, string File)[] CorpusEntries =
    [
        ("subdomains", "子域枚举字典", "subdomains.txt"),
        ("sqli-auth-bypass", "SQLi 认证绕过 payload", "sqli-auth-bypass.txt"),
        ("quick-sqli", "快速 SQLi payload", "quick-sqli.txt"),
        ("generic-sqli", "通用 SQLi payload", "generic-sqli.txt"),
        ("nosql", "NoSQL 注入 payload", "nosql.txt"),
        ("ldap-fuzzing", "LDAP 注入 fuzz", "ldap-fuzzing.txt"),
        ("special-chars", "特殊字符 fuzz", "special-chars.txt"),
        ("command-injection", "命令注入 payload", "command-injection-commix.txt")
    ];

    private static string? CorpusFile(string id) => CorpusEntries.FirstOrDefault(item => item.Id == id).File is { Length: > 0 } file ? file : null;

    /// <summary>Best-effort list of bundled wordlist/payload corpora available to scan adapters.</summary>
    public static IReadOnlyList<AuthorizedToolResource> CorpusResources() =>
        CorpusEntries
            .Select(item => new AuthorizedToolResource(item.Id, item.Name, CorpusPath(item.File)))
            .Where(item => File.Exists(item.Path))
            .ToList();
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
    /// <summary>
    /// Stage classification for the control-plane exploitation gate: exploitation adapters
    /// (target-tampering verification) may only run when detection-stage evidence exists
    /// for the same target — in an active scope or earlier in the same plan.
    /// </summary>
    public static bool IsExploitationStage(string adapterId) =>
        adapterId is ExploitVcenterVerify or ExploitFastjsonPayload;

    /// <summary>Recon/detection adapters whose evidence unlocks the exploitation stage.</summary>
    public static bool IsDetectionStage(string adapterId) => adapterId is
        NmapQuick or NmapService or DirsearchQuick or Wafw00fQuick or HttpHeadersProbe or HttpGetProbe
        or HttpxProbe or DnsResolve or ProbeSqlmapInject or ProbeUnauthorizedAccess or ProbeParamCorpus
        or ReconSubdomainEnum or ReconGitLeakScan or ReconSvnLeakScan or ReconDsStoreScan or ReconSwaggerApiEnum
        or DetectWeblogicT3Scan or DetectFastjsonJndiScan or DetectOaPocList or DetectOaPocProbe
        or DetectShiroScan or DetectStruts2Scan or DetectNacosScan or ProbeCloudAkskVerify;

    public static bool IsTargetInScope(string target, IReadOnlyList<string> allowedTargets)
    {
        var host = (target ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        foreach (var raw in allowedTargets)
        {
            var allowed = (raw ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            if (allowed.Length == 0) continue;
            if (allowed == "*") return true;
            if (allowed == host) return true;
            if (allowed.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = allowed[2..];
                if (suffix.Length > 0 &&
                    (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    private static void EnsureAuthorizedTarget(string target, IReadOnlyList<string> allowedTargets)
    {
        if (IsTargetInScope(target, allowedTargets)) return;
        throw new UnauthorizedAccessException($"Target '{target}' is outside the approved scope.");
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
