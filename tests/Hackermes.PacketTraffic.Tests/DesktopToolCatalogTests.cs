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
        var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings());

        Assert.Equal(19, tools.Count);
        Assert.DoesNotContain(tools, tool => tool.Id == "crypto.supersoft");
        Assert.Equal("加解密", tools[0].Category);
        Assert.Equal("内置编码与哈希", tools[0].Name);
        Assert.Single(tools, tool => tool.Id == "recon.nmap.terminal");
        Assert.DoesNotContain(tools, tool => tool.Id == "recon.nmap.quick");
        Assert.DoesNotContain(tools, tool => tool.Id == "recon.nmap.service");
        Assert.Equal("漏洞扫描", Assert.Single(tools, tool => tool.Id == "detect.wafw00f.terminal").Category);
        Assert.Equal("漏洞扫描", Assert.Single(tools, tool => tool.Id == "detect.unauthorized.terminal").Category);
        foreach (var id in new[]
                 {
                     "web.burp", "traffic.wireshark", "detect.apk-analyser", "password.john.terminal",
                     "password.archpr", "reverse.ghidra", "reverse.x64dbg"
                 })
        {
            var placeholder = Assert.Single(tools, tool => tool.Id == id);
            Assert.False(placeholder.Available);
            Assert.False(string.IsNullOrWhiteSpace(placeholder.Description));
            Assert.Contains("未内置", placeholder.UnavailableReason, System.StringComparison.Ordinal);
        }
        Assert.Equal("漏洞利用", Assert.Single(tools, tool => tool.Id == "exploit.sqlmap.terminal").Category);
        Assert.Equal("漏洞利用", Assert.Single(tools, tool => tool.Id == "exploit.xss-fuzzer.terminal").Category);
        var dns = Assert.Single(tools, tool => tool.Id == "exploit.dnslog-sqli.terminal");
        Assert.Equal("漏洞利用", dns.Category);
        Assert.False(dns.Available);
        Assert.Contains("不会回退", dns.UnavailableReason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_DoesNotFallBackToConfiguredExternalRoots()
    {
        var bundleRoot = Path.Combine(Path.GetTempPath(), "hackermes-empty-bundle-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleRoot);
        var settings = new SecurityToolsSettings
        {
            PrimaryToolRoot = @"C:\missing primary 工具",
            SecondaryToolRoot = @"D:\missing secondary 工具"
        };

        try
        {
            var tools = DesktopToolCatalog.Describe(settings, bundleRoot);
            var nmap = Assert.Single(tools, tool => tool.Id == "recon.nmap.terminal");
            Assert.StartsWith(bundleRoot, nmap.Path, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(settings.PrimaryToolRoot, nmap.Path!, System.StringComparison.OrdinalIgnoreCase);
            Assert.False(nmap.Available);
            Assert.Contains("不会回退", nmap.UnavailableReason, System.StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
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
            @"exploit.dnslog-sqli.terminal\dnslogSql.py"
        };
        try
        {
            foreach (var relative in relativeFiles)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0x00]);
            }

            var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings(), root);

            Assert.Equal(19, tools.Count);
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
    public void BasicCodecConverters_AreBuiltInAndNeverRequireExternalPaths()
    {
        var tools = DesktopToolCatalog.Describe(new SecurityToolsSettings
        {
            PrimaryToolRoot = @"Z:\missing-tools",
            SecondaryToolRoot = @"Y:\missing-tools"
        });

        foreach (var id in new[] { "crypto.convert", "crypto.radix" })
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
        Assert.EndsWith("powershell.exe", start.ArgumentList[3], System.StringComparison.OrdinalIgnoreCase);
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
