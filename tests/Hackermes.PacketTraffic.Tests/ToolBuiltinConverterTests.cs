using Hackermes.App.Views;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class ToolBuiltinConverterTests
{
    [Fact]
    public void DecodeJwt_ExtractsHeaderPayloadAndSignature()
    {
        var token = BuildJwt();
        var decoded = CodecWorkbenchWindow.DecodeJwt(token);

        Assert.Contains("\"alg\":\"HS256\"", decoded.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("alice", decoded, StringComparison.Ordinal);
        Assert.Contains("SIGNATURE", decoded, StringComparison.Ordinal);
        Assert.Contains("c2ln", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeJwt_RejectsWrongSegmentCount()
    {
        Assert.Throws<InvalidDataException>(() => CodecWorkbenchWindow.DecodeJwt("only-one-segment"));
        Assert.Throws<InvalidDataException>(() => CodecWorkbenchWindow.DecodeJwt("a.b.c.d"));
    }

    [Fact]
    public void ParseUrlStructure_SplitsComponentsAndDecodesParameters()
    {
        var parsed = CodecWorkbenchWindow.ParseUrlStructure(
            "https://user:pw@example.com:8443/a%20b/?q=%E5%AE%89%E5%85%A8&x=1#frag");

        Assert.Contains("协议: https", parsed, StringComparison.Ordinal);
        Assert.Contains("主机: example.com", parsed, StringComparison.Ordinal);
        Assert.Contains("端口: 8443", parsed, StringComparison.Ordinal);
        Assert.Contains("用户信息: user:pw", parsed, StringComparison.Ordinal);
        Assert.Contains("路径: /a b/", parsed, StringComparison.Ordinal);
        Assert.Contains("q = 安全", parsed, StringComparison.Ordinal);
        Assert.Contains("x = 1", parsed, StringComparison.Ordinal);
        Assert.Contains("片段: #frag", parsed, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseUrlStructure_RejectsRelativeInput() =>
        Assert.Throws<InvalidDataException>(() =>
            CodecWorkbenchWindow.ParseUrlStructure("/just/a/path"));

    [Fact]
    public void ConvertTimestamp_ConvertsBothDirections()
    {
        var fromSeconds = CodecWorkbenchWindow.ConvertTimestamp("1700000000");
        // 1700000000 秒固定对应这个 UTC 时刻，与时区无关。
        Assert.Contains("2023-11-14 22:13:20", fromSeconds, StringComparison.Ordinal);

        var fromMillis = CodecWorkbenchWindow.ConvertTimestamp("1700000000000");
        Assert.Contains("ISO:", fromMillis, StringComparison.Ordinal);

        var input = "2023-11-14 22:13:20";
        var expectedSeconds = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal).ToUnixTimeSeconds();
        var toUnix = CodecWorkbenchWindow.ConvertTimestamp(input);
        Assert.Contains(expectedSeconds.ToString(), toUnix, StringComparison.Ordinal);

        // ≥13 位按毫秒解释：结果必须等于毫秒语义的换算而不是秒语义。
        var millis = CodecWorkbenchWindow.ConvertTimestamp("9999999999999");
        var asMillis = DateTimeOffset.FromUnixTimeMilliseconds(9_999_999_999_999)
            .UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Contains(asMillis, millis, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertTimestamp_RejectsGarbage() =>
        Assert.Throws<InvalidDataException>(() => CodecWorkbenchWindow.ConvertTimestamp("not-a-date"));

    [Theory]
    [InlineData("(?i)password\\s*=\\s*\\S+", "a\npassword = Abc123\nx=1", 1)]
    [InlineData(@"\d+", "a1b22c333", 3)]
    [InlineData("(z)", "abc", 0)]
    public void RunRegex_CountsMatchesAndListsGroups(string pattern, string input, int expected)
    {
        var output = RegexTesterWindow.RunRegex(pattern, input, ignoreCase: true, multiline: true,
            out var summary);

        if (expected == 0) Assert.Equal("(无匹配)", output);
        Assert.Contains(expected.ToString(), summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RunRegex_ListsNamedGroups()
    {
        var output = RegexTesterWindow.RunRegex("(?<user>alice)", "hi alice", true, true, out _);

        Assert.Contains("[1] @3..8: alice", output, StringComparison.Ordinal);
        Assert.Contains("组user: alice", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_RegisterContainsNewBuiltinConverters()
    {
        foreach (var name in new[] { "JWT 解码", "URL 结构解析", "时间戳 ↔ 日期" })
            Assert.Contains(CodecWorkbenchWindow.Operations, operation => operation.Name == name);
    }

    private static string BuildJwt()
    {
        static string Segment(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Segment("{\"alg\":\"HS256\"}")}.{Segment("{\"sub\":\"alice\"}")}.c2ln";
    }
}
