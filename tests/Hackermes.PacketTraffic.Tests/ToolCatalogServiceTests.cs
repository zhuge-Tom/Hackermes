using Hackermes.App;
using Hackermes.App.Views;
using Hackermes.Platform.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class ToolCatalogServiceTests
{
    [Fact]
    public async Task RefreshAsync_PopulatesSnapshotAndRaisesChangedOnceOnMarshalledThread()
    {
        var tools = new[] { BuiltIn("a"), BuiltIn("b") };
        var marshalled = new List<Action>();
        var service = new ToolCatalogService(action => { marshalled.Add(action); action(); },
            _ => new ToolCatalogService.ScanResult(tools, null, false));
        var raised = 0;
        service.CatalogChanged += () => raised++;

        await service.RefreshAsync(new SecurityToolsSettings());

        Assert.Equal(tools, service.Snapshot);
        Assert.Equal(1, raised);
        Assert.Single(marshalled);
    }

    [Fact]
    public async Task RefreshAsync_ScanFailureKeepsPreviousSnapshotAndReportsNote()
    {
        var initial = new[] { BuiltIn("keep") };
        var failing = new ToolCatalogService(_ => { },
            _ => throw new InvalidOperationException("boom"));
        await failing.RefreshAsync(new SecurityToolsSettings());
        // 第一次扫描失败且没有旧快照时,快照保持为空而不是崩溃。
        Assert.Empty(failing.Snapshot);

        var resilient = new ToolCatalogService(_ => { },
            settings => settings.PrimaryToolRoot == "fail"
                ? throw new InvalidOperationException("scan exploded")
                : new ToolCatalogService.ScanResult(initial, null, false));
        await resilient.RefreshAsync(new SecurityToolsSettings());
        Assert.Single(resilient.Snapshot);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCallsAreSerializedByScanLock()
    {
        var inFlight = 0;
        var overlapObserved = false;
        var service = new ToolCatalogService(_ => { }, _ =>
        {
            if (Interlocked.Exchange(ref inFlight, 1) == 1) overlapObserved = true;
            Thread.Sleep(20);
            Interlocked.Exchange(ref inFlight, 0);
            return new ToolCatalogService.ScanResult([BuiltIn("x")], null, false);
        });

        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => service.RefreshAsync(new SecurityToolsSettings())));

        Assert.False(overlapObserved);
        Assert.Single(service.Snapshot);
    }

    [Fact]
    public void NormalizeRecentIds_MovesLaunchedToFrontDeduplicatesAndCaps()
    {
        var result = ToolCatalogService.NormalizeRecentIds(
            ["b", "a", "", null, "c", "d", "e", "f"], "c");

        Assert.Equal(["c", "b", "a", "d", "e"], result);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void NormalizeRecentIds_BlankLaunchKeepsExistingOrder()
    {
        var result = ToolCatalogService.NormalizeRecentIds(["x", "x", "y", null], "  ");

        Assert.Equal(["x", "y"], result);
    }

    private static DesktopToolEntry BuiltIn(string id) =>
        new(id, "分类", id, "描述", DesktopToolKind.BuiltIn, true);
}

public sealed class ToolManifestTests
{
    [Fact]
    public void Load_MissingFileReturnsEmptyWithoutSkip()
    {
        var root = NewTempDir();

        var entries = ToolManifest.Load(root, out var skipped);

        Assert.Empty(entries);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Load_BrokenJsonIsReportedDistinctlyFromInvalidEntries()
    {
        var root = NewTempDir();
        File.WriteAllText(ToolManifest.PathFor(root), "{ not json ");

        var entries = ToolManifest.Load(root, out var skipped);

        Assert.Empty(entries);
        Assert.Equal(-1, skipped);
    }

    [Fact]
    public void Load_ParsesValidEntriesAndSkipsMalformedOrDuplicateOnes()
    {
        var root = NewTempDir();
        File.WriteAllText(ToolManifest.PathFor(root), """
            {
              "version": 1,
              "tools": [
                { "id": "custom.one", "category": "自定义", "name": "工具一",
                  "description": "第一个", "kind": "Gui", "path": "one.exe" },
                { "id": "custom.one", "category": "自定义", "name": "重复 id",
                  "description": "", "kind": "Gui", "path": "dup.exe" },
                { "id": "custom.bad-kind", "category": "自定义", "name": "坏类型",
                  "description": "", "kind": "Macro", "path": "bad.exe" },
                { "id": "", "category": "自定义", "name": "空 id",
                  "description": "", "kind": "Gui", "path": "empty.exe" }
              ]
            }
            """);

        var entries = ToolManifest.Load(root, out var skipped);

        var entry = Assert.Single(entries);
        Assert.Equal("custom.one", entry.Id);
        Assert.Equal(DesktopToolKind.Gui, entry.Kind);
        Assert.Equal("one.exe", entry.Path);
        Assert.Equal(3, skipped);
    }

    [Fact]
    public void Load_RespectsEntryLimitAndInstructionBounds()
    {
        var root = NewTempDir();
        var tools = string.Join(",", Enumerable.Range(0, ToolManifest.MaxEntries + 5)
            .Select(index =>
                $$"""{ "id": "custom.t{{index}}", "category": "C", "name": "N", "description": "", "kind": "Shortcut", "path": "p{{index}}.lnk" }"""));
        File.WriteAllText(ToolManifest.PathFor(root), $$"""{ "version": 1, "tools": [{{tools}}] }""");

        var entries = ToolManifest.Load(root, out var skipped);

        Assert.Equal(ToolManifest.MaxEntries, entries.Count);
        Assert.Equal(5, skipped);
    }

    [Fact]
    public void CreateCustomEntry_RelativePathMustStayInsideBundledRoot()
    {
        var root = NewTempDir();
        var probes = new PathProbeCache(_ => null);
        var settings = new SecurityToolsSettings();

        var escaped = new ToolManifestEntry("x", "C", "N", "", DesktopToolKind.Gui, @"..\..\evil.exe", false, null);
        var inside = new ToolManifestEntry("y", "C", "N", "", DesktopToolKind.Gui, @"sub\tool.exe", false, null);

        Assert.Null(DesktopToolCatalog.CreateCustomEntry(escaped, root, settings, probes));
        var resolved = DesktopToolCatalog.CreateCustomEntry(inside, root, settings, probes);
        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(root), resolved!.Path!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateCustomEntry_AbsolutePathOnlyInsideConfiguredRoots()
    {
        var primary = NewTempDir();
        var outside = NewTempDir();
        var probes = new PathProbeCache(_ => null);
        var settings = new SecurityToolsSettings { PrimaryToolRoot = primary };

        var allowedPath = Path.Combine(primary, "tool.exe");
        var blockedPath = Path.Combine(outside, "tool.exe");

        var allowed = DesktopToolCatalog.CreateCustomEntry(
            new ToolManifestEntry("a", "C", "N", "", DesktopToolKind.Gui, allowedPath, false, null),
            NewTempDir(), settings, probes);
        var blocked = DesktopToolCatalog.CreateCustomEntry(
            new ToolManifestEntry("b", "C", "N", "", DesktopToolKind.Gui, blockedPath, false, null),
            NewTempDir(), settings, probes);

        Assert.NotNull(allowed);
        Assert.False(allowed!.Available); // 文件不存在 → 标记不可用但路径已接受
        Assert.Null(blocked);
    }

    [Fact]
    public void CreateCustomEntry_ExistingExecutableBecomesAvailable()
    {
        var root = NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllBytes(Path.Combine(root, "sub", "tool.exe"), [0x4d, 0x5a]);
        var probes = new PathProbeCache(_ => null);

        var tool = DesktopToolCatalog.CreateCustomEntry(
            new ToolManifestEntry("ok", "自定义", "可用工具", "说明", DesktopToolKind.TeachingTerminal,
                "sub/tool.exe", false, ["--help"]),
            root, new SecurityToolsSettings(), probes);

        Assert.NotNull(tool);
        Assert.True(tool!.Available);
        Assert.Null(tool.UnavailableReason);
        Assert.Equal(["--help"], tool.Instructions);
    }

    [Fact]
    public void PathProbeCache_DedupesProbesPerFileNameWithinOneScan()
    {
        var calls = 0;
        var probes = new PathProbeCache(_ => { calls++; return null; });

        Assert.Null(probes.FindOnPath("python.exe"));
        Assert.Null(probes.FindOnPath("python.exe"));
        Assert.Null(probes.FindOnPath("py.exe"));

        Assert.Equal(2, calls);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hackermes-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed class ToolSearchFilterTests
{
    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        Assert.True(Matches("任意", null));
        Assert.True(Matches("任意", ""));
        Assert.True(Matches("任意", "   "));
    }

    [Fact]
    public void TokensMatchAcrossNameDescriptionCategoryAndIdCaseInsensitively()
    {
        var tool = new DesktopToolEntry("recon.nmap.terminal", "信息收集", "Nmap 端口扫描",
            "在原生终端使用完整 Nmap 参数。", DesktopToolKind.TeachingTerminal, true);

        Assert.True(ToolSearchFilter.Matches(tool, "nmap"));
        Assert.True(ToolSearchFilter.Matches(tool, "信息 终端"));
        Assert.True(ToolSearchFilter.Matches(tool, "RECON 参数"));
        Assert.False(ToolSearchFilter.Matches(tool, "nmap sqlmap"));
        Assert.False(ToolSearchFilter.Matches(tool, "burp"));
    }

    private static bool Matches(string name, string? query) =>
        ToolSearchFilter.Matches(
            new DesktopToolEntry("t", "分类", name, "描述", DesktopToolKind.BuiltIn, true), query);
}

public sealed class ToolCatalogPresentationTests
{
    [Fact]
    public void Group_MovesNotIntegratedToolsToOneTrailingGroup()
    {
        var available = new DesktopToolEntry("a", "信息收集", "Nmap", "", DesktopToolKind.Gui, true);
        var notIntegrated = new DesktopToolEntry("b", "Web 与流量", "Burp", "", DesktopToolKind.Gui, false,
            UnavailableReason: "未接入；当前版本不随程序分发");

        var groups = ToolCatalogPresentation.Group([notIntegrated, available]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("信息收集", groups[0].Category);
        Assert.Equal(ToolCatalogPresentation.NotIntegratedCategory, groups[1].Category);
        Assert.Equal("b", Assert.Single(groups[1].Tools).Id);
    }

    [Fact]
    public void StatusLabel_DistinguishesMissingUnverifiedAndNotIntegrated()
    {
        var missing = new DesktopToolEntry("m", "C", "M", "", DesktopToolKind.Gui, false,
            UnavailableReason: @"未找到：C:\missing.exe");
        var unverified = new DesktopToolEntry("u", "C", "U", "", DesktopToolKind.Gui, true,
            VerificationNote: "未签名");
        var notIntegrated = new DesktopToolEntry("n", "C", "N", "", DesktopToolKind.Gui, false,
            UnavailableReason: "未接入；未配置");

        Assert.Equal("缺失", ToolCatalogPresentation.StatusLabel(missing));
        Assert.Equal("未验证", ToolCatalogPresentation.StatusLabel(unverified));
        Assert.Equal("未接入", ToolCatalogPresentation.StatusLabel(notIntegrated));
    }

    [Theory]
    [InlineData("crypto.radix", "二进制 → 十进制")]
    [InlineData("crypto.jwt", "JWT 解码")]
    [InlineData("builtin.codec", null)]
    public void PreferredCodecOperation_MapsSpecificShortcuts(string id, string? expected) =>
        Assert.Equal(expected, ToolCatalogPresentation.PreferredCodecOperation(id));
}
