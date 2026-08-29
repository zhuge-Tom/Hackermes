using Hackermes.App.Views;
using Hackermes.App;
using Hackermes.Platform.Models;
using System.Linq;
using System.IO;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class DesktopToolCatalogTests
{
    [Fact]
    public void Catalog_ListsSelectedBundledHumanToolsWithOneNmapEntry()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "hackermes-missing-tools-" + System.Guid.NewGuid().ToString("N"));
        var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings
        {
            PrimaryToolRoot = Path.Combine(missingRoot, "primary"),
            SecondaryToolRoot = Path.Combine(missingRoot, "secondary")
        }, Path.Combine(missingRoot, "bundle"));

        Assert.Equal(51, tools.Count);
        Assert.DoesNotContain(tools, tool => tool.Id == "crypto.supersoft");
        Assert.DoesNotContain(tools, tool => tool.Id == "crypto.convert");
        Assert.Equal("加解密", tools[0].Category);
        Assert.Equal("编码与哈希", tools[0].Name);
        Assert.Single(tools, tool => tool.Id == "recon.nmap.terminal");
        Assert.DoesNotContain(tools, tool => tool.Id == "recon.nmap.quick");
        Assert.DoesNotContain(tools, tool => tool.Id == "recon.nmap.service");
        Assert.Equal("漏洞扫描", Assert.Single(tools, tool => tool.Id == "detect.wafw00f.terminal").Category);
        Assert.Equal("漏洞扫描", Assert.Single(tools, tool => tool.Id == "detect.unauthorized.terminal").Category);
        foreach (var id in new[]
                 {
                     "web.burp", "traffic.wireshark", "password.john.terminal",
                     "password.archpr", "reverse.ghidra"
                 })
        {
            var placeholder = Assert.Single(tools, tool => tool.Id == id);
            Assert.False(placeholder.Available);
            Assert.False(string.IsNullOrWhiteSpace(placeholder.Description));
            Assert.Contains("未接入", placeholder.UnavailableReason, System.StringComparison.Ordinal);
            Assert.Equal(DesktopToolAvailability.NotIntegrated, placeholder.Availability);
        }
        foreach (var id in new[] { "detect.apk-analyser", "reverse.x64dbg" })
        {
            var configuredTool = Assert.Single(tools, tool => tool.Id == id);
            Assert.False(configuredTool.Available);
            Assert.Contains("未找到", configuredTool.UnavailableReason, System.StringComparison.Ordinal);
        }
        Assert.Equal("漏洞利用", Assert.Single(tools, tool => tool.Id == "exploit.sqlmap.terminal").Category);
        Assert.Equal("漏洞利用", Assert.Single(tools, tool => tool.Id == "exploit.xss-fuzzer.terminal").Category);
        var dns = Assert.Single(tools, tool => tool.Id == "exploit.dnslog-sqli.terminal");
        Assert.Equal("漏洞利用", dns.Category);
        Assert.False(dns.Available);
        Assert.Contains("未找到", dns.UnavailableReason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_FallsBackToConfiguredRootsWhenDevelopmentBundleIsMissing()
    {
        var bundleRoot = Path.Combine(Path.GetTempPath(), "hackermes-empty-bundle-" + System.Guid.NewGuid().ToString("N"));
        var primaryRoot = Path.Combine(Path.GetTempPath(), "hackermes-primary-tools-" + System.Guid.NewGuid().ToString("N"));
        var secondaryRoot = Path.Combine(Path.GetTempPath(), "hackermes-secondary-tools-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);
        var nmapPath = Path.Combine(primaryRoot, "01-信息收集", "扫描接口", "zenmap(1)", "nmap.exe");
        var ctfPath = Path.Combine(secondaryRoot, "加密解密", "随波逐流", "[随波逐流]CTF编码工具 V7.3 20260506.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(nmapPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ctfPath)!);
        File.WriteAllBytes(nmapPath, [0x4d, 0x5a]);
        File.WriteAllBytes(ctfPath, [0x4d, 0x5a]);
        var settings = new SecurityToolsSettings
        {
            PrimaryToolRoot = primaryRoot,
            SecondaryToolRoot = secondaryRoot
        };

        try
        {
            var tools = DesktopToolCatalog.Describe(settings, bundleRoot);
            var nmap = Assert.Single(tools, tool => tool.Id == "recon.nmap.terminal");
            Assert.True(nmap.Available);
            Assert.Equal(nmapPath, nmap.Path, ignoreCase: true);
            var ctf = Assert.Single(tools, tool => tool.Id == "crypto.ctf");
            Assert.True(ctf.Available);
            Assert.Equal(ctfPath, ctf.Path, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
            Directory.Delete(primaryRoot, recursive: true);
            Directory.Delete(secondaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_RestoresApkAnalyzerAndX64dbgFromConfiguredRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-restored-tools-" + System.Guid.NewGuid().ToString("N"));
        var primaryRoot = Path.Combine(root, "primary");
        var secondaryRoot = Path.Combine(root, "secondary");
        var apkAnalyzer = Path.Combine(primaryRoot, "02-漏洞扫描", "漏了个大洞(APK)", "apk数据提取", "apkAnalyser.exe");
        var x64dbg = Path.Combine(secondaryRoot, "snapshot_2026-05-27_12-11", "release", "x96dbg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(apkAnalyzer)!);
        Directory.CreateDirectory(Path.GetDirectoryName(x64dbg)!);
        File.WriteAllBytes(apkAnalyzer, [0x4d, 0x5a]);
        File.WriteAllBytes(x64dbg, [0x4d, 0x5a]);

        try
        {
            var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings
            {
                PrimaryToolRoot = primaryRoot,
                SecondaryToolRoot = secondaryRoot
            }, Path.Combine(root, "missing-bundle"));

            var apk = Assert.Single(tools, tool => tool.Id == "detect.apk-analyser");
            Assert.True(apk.Available);
            Assert.Equal(DesktopToolKind.Gui, apk.Kind);
            Assert.Equal(apkAnalyzer, apk.Path, ignoreCase: true);

            var debugger = Assert.Single(tools, tool => tool.Id == "reverse.x64dbg");
            Assert.True(debugger.Available);
            Assert.Equal(DesktopToolKind.Gui, debugger.Kind);
            Assert.Equal(x64dbg, debugger.Path, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_PrefersBundledToolOverConfiguredExternalPath()
    {
        var bundleRoot = Path.Combine(Path.GetTempPath(), "hackermes-bundled-tools-" + System.Guid.NewGuid().ToString("N"));
        var bundledNmap = Path.Combine(bundleRoot, "recon.nmap.terminal", "nmap.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bundledNmap)!);
        File.WriteAllBytes(bundledNmap, [0x4d, 0x5a]);
        try
        {
            var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings
            {
                PrimaryToolRoot = @"Z:\missing-tools",
                SecondaryToolRoot = @"Y:\missing-tools"
            }, bundleRoot);

            var nmap = Assert.Single(tools, tool => tool.Id == "recon.nmap.terminal");
            Assert.True(nmap.Available);
            Assert.Equal(bundledNmap, nmap.Path);
            Assert.Equal(Path.GetDirectoryName(bundledNmap), nmap.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_AllSelectedToolsAreAvailableFromOneApplicationRelativeBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-selected-bundle-" + System.Guid.NewGuid().ToString("N"));
        var relativeFiles = new[]
        {
            @"_runtime\python\python.exe",
            @"crypto.ctf\[随波逐流]CTF编码工具 V7.3 20260506.exe",
            @"recon.nmap.terminal\nmap.exe",
            @"recon.dirsearch.terminal\dirsearch.py",
            @"recon.layer\Layer.exe",
            @"detect.wafw00f.terminal\wafw00f\main.py",
            @"detect.unauthorized.terminal\Unauthorized-Vul.py",
            @"exploit.sqlmap.terminal\sqlmap.py",
            @"exploit.xss-fuzzer.terminal\xssFuzz.py",
            @"exploit.dnslog-sqli.terminal\dnslogSql.py",
            @"recon.git-leak.terminal\GitHack.py",
            @"recon.svn-leak.terminal\SvnExploit.py",
            @"recon.ds-store.terminal\ds_store_exp.py",
            @"recon.swagger-api.terminal\swagger-hack2.0.py",
            @"detect.weblogic-t3.terminal\WeblogicScan.py",
            @"detect.fastjson-jndi.terminal\JsonExp.exe",
            @"exploit.vcenter.terminal\main.exe",
            @"exploit.heapdump.terminal\JDumpSpider-1.1-SNAPSHOT-full.jar",
            @"detect.oa-poc.terminal\oa_poc_runner.py",
            @"detect.shiro.terminal\shiro_tool.jar",
            @"detect.struts2.terminal\Struts2Scan.py",
            @"detect.nacos.terminal\nacos_probe.py",
            @"exploit.fastjson-payload.terminal\FastjsonExploit-0.1-beta2-all.jar",
            @"probe.cloud-aksk.terminal\cf.exe",
            @"gui.shiro-exploit.terminal\ShiroExploit.jar",
            @"gui.struts2-check.terminal\Struts2_19.21.jar",
            @"gui.thinkphp.terminal\ThinkPHP.jar",
            @"gui.tomcat-pass.terminal\TomcatPass.jar",
            @"gui.nacos-exploit.terminal\NacosExploitGUI_v4.0.jar",
            @"gui.xxl-job.terminal\xxl-jobExploitGUI_v1.0.jar",
            @"gui.jenkins-exploit.terminal\JenkinsExploit-GUI-1.3-SNAPSHOT.jar",
            @"gui.tongda-oa.terminal\TongdaOATool_V1.3.jar",
            @"gui.frchannel.terminal\FrChannelPlus.jar",
            @"gui.hikvision.terminal\HikvisionExploitGUI_v3.0.jar",
            @"gui.dahua.terminal\DahuaExploitGUI.jar",
            @"gui.myexploit.terminal\MYExploit.jar",
            @"gui.decrypt-tools.terminal\DecryptTools.jar",
            @"gui.mdat.terminal\Multiple.Database.Utilization.Tools-2.1.1-jar-with-dependencies.jar",
            @"gui.api-tool.terminal\API-T00L_v1.2.jar",
            @"_runtime\javafx\lib\javafx-base-21.0.5-win.jar"
        };
        try
        {
            foreach (var relative in relativeFiles)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, relative.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase)
                    ? [0x4d, 0x5a]
                    : [0x00]);
            }

            var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings
            {
                PrimaryToolRoot = Path.Combine(root, "missing-primary"),
                SecondaryToolRoot = Path.Combine(root, "missing-secondary")
            }, root);

            Assert.Equal(51, tools.Count);
            var placeholders = tools.Where(tool => tool.Id is "web.burp" or "traffic.wireshark" or "detect.apk-analyser"
                or "password.john.terminal" or "password.archpr" or "reverse.ghidra" or "reverse.x64dbg").ToArray();
            Assert.Equal(7, placeholders.Length);
            Assert.All(placeholders, tool => Assert.False(tool.Available));
            Assert.All(tools.Except(placeholders), tool => Assert.True(tool.Available, $"{tool.Id}: {tool.UnavailableReason}"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BundledGuiLaunchResolvesJavaAndJavaFxArguments()
    {
        // 回归：BundledTools 条目缺 Kind 时启动器会静默回退（0.12.0 实测事故）。
        var root = Path.Combine(Path.GetTempPath(), "hackermes-guilaunch-" + System.Guid.NewGuid().ToString("N"));
        var files = new[]
        {
            @"gui.tomcat-pass.terminal\TomcatPass.jar",
            @"gui.struts2-check.terminal\Struts2_19.21.jar",
            @"_runtime\javafx\lib\javafx-base-21.0.5-win.jar"
        };
        try
        {
            foreach (var relative in files)
            {
                var path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0x4D, 0x5A]);
            }

            var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings
            {
                PrimaryToolRoot = Path.Combine(root, "missing-primary"),
                SecondaryToolRoot = Path.Combine(root, "missing-secondary")
            }, root);

            var fx = Assert.Single(tools, tool => tool.Id == "gui.tomcat-pass");
            Assert.True(fx.Available, $"{fx.Id}: {fx.UnavailableReason}");
            var swing = Assert.Single(tools, tool => tool.Id == "gui.struts2-check");
            Assert.True(swing.Available, $"{swing.Id}: {swing.UnavailableReason}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void BasicCodecConverters_AreBuiltInAndNeverRequireExternalPaths()
    {
        var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings
        {
            PrimaryToolRoot = @"Z:\missing-tools",
            SecondaryToolRoot = @"Y:\missing-tools"
        });

        foreach (var id in new[] { "crypto.radix", "crypto.jwt", "util.timestamp",
                 "util.regex.tester", "web.url.parse" })
        {
            var tool = Assert.Single(tools, candidate => candidate.Id == id);
            Assert.Equal(DesktopToolKind.BuiltIn, tool.Kind);
            Assert.True(tool.Available);
            Assert.Null(tool.Path);
        }
    }

    [Fact]
    public void TeachingTerminal_PrintsGuidanceWithoutExecutingToolCommands()
    {
        var tool = new DesktopToolEntry(
            "test", "信息收集", "O'Brien 工具", "test", DesktopToolKind.TeachingTerminal, true,
            @"C:\工具 目录\tool.exe", @"C:\工具 目录", ["tool.exe --help", "tool.exe --target 127.0.0.1"]);

        var script = ToolLaunchService.BuildTeachingScript(tool);

        Assert.Contains("Write-Host '[Hackermes] O''Brien 工具'", script, System.StringComparison.Ordinal);
        Assert.Contains("Write-Host 'tool.exe --help'", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("; tool.exe --help", script, System.StringComparison.Ordinal);
        Assert.Contains("已明确授权", script, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GuiLaunch_UsesPerUserTempInsteadOfInheritedSystemTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-gui-temp-" + System.Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "tool.exe");
        var localAppData = Path.Combine(root, "LocalAppData");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(executable, [0x4d, 0x5a]);

        try
        {
            var start = ToolLaunchService.CreateGuiStartInfo(executable, localAppData);

            var expected = Path.Combine(localAppData, "Temp");
            Assert.False(start.UseShellExecute);
            Assert.Equal(expected, start.Environment["TEMP"], ignoreCase: true);
            Assert.Equal(expected, start.Environment["TMP"], ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowsTerminal_LaunchesPowerShellAsAnArgumentInsteadOfTreatingTeachingTextAsExecutable()
    {
        var tool = new DesktopToolEntry(
            "test", "信息收集", "Nmap", "test", DesktopToolKind.TeachingTerminal, true,
            @"C:\工具 目录\nmap.exe", @"C:\工具 目录", [".\\nmap.exe --help"]);
        var settings = new SecurityToolsSettings { TerminalMode = "WindowsTerminal" };

        var start = ToolLaunchService.CreateTeachingTerminalStartInfo(tool, settings, tool.WorkingDirectory!);

        Assert.Equal("wt.exe", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal("new-tab", start.ArgumentList[0]);
        Assert.Equal("--startingDirectory", start.ArgumentList[1]);
        Assert.Equal(tool.WorkingDirectory, start.ArgumentList[2]);
        Assert.Contains(Path.GetFileName(start.ArgumentList[3]), new[] { "pwsh.exe", "powershell.exe" },
            System.StringComparer.OrdinalIgnoreCase);
        Assert.Equal("-EncodedCommand", start.ArgumentList[6]);
        Assert.DoesNotContain(";", start.ArgumentList[7], System.StringComparison.Ordinal);
        var decoded = System.Text.Encoding.Unicode.GetString(System.Convert.FromBase64String(start.ArgumentList[7]));
        Assert.Contains("Write-Host", decoded, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TeachingTerminal_AddsBundledPythonRuntimeToPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "hackermes-python-bundle-" + System.Guid.NewGuid().ToString("N"));
        var python = Path.Combine(root, "_runtime", "python", "python.exe");
        var scriptPath = Path.Combine(root, "recon.dirsearch.terminal", "dirsearch.py");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllBytes(python, [0x4d, 0x5a]);
        File.WriteAllText(scriptPath, string.Empty);
        try
        {
            var tool = DesktopToolCatalog.Describe(new SecurityToolsSettings(), root)
                .Single(candidate => candidate.Id == "recon.dirsearch.terminal");

            var teachingScript = ToolLaunchService.BuildTeachingScript(tool);

            Assert.True(tool.Available);
            Assert.Contains(Path.GetDirectoryName(python)!, teachingScript, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("$env:PATH", teachingScript, System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
