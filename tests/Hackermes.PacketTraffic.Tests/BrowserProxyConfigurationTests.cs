using Hackermes.Browser.Services;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class BrowserProxyConfigurationTests
{
    [Theory]
    [InlineData(null, BrowserProxyMode.Direct)]
    [InlineData("", BrowserProxyMode.Direct)]
    [InlineData("unknown", BrowserProxyMode.Direct)]
    [InlineData("DIRECT", BrowserProxyMode.Direct)]
    [InlineData("burp", BrowserProxyMode.Burp)]
    [InlineData("BURP", BrowserProxyMode.Burp)]
    public void ParseMode_UsesSafeDirectFallback(string? value, BrowserProxyMode expected) =>
        Assert.Equal(expected, BrowserProxyConfiguration.ParseMode(value));

    [Fact]
    public void DirectMode_BypassesSystemProxy()
    {
        var configuration = BrowserProxyConfiguration.Create(BrowserProxyMode.Direct);

        Assert.Equal("--no-proxy-server", configuration.AdditionalBrowserArguments);
        Assert.EndsWith("Direct", configuration.UserDataFolder);
    }

    [Fact]
    public void BurpMode_IsFixedToLoopbackListener()
    {
        var configuration = BrowserProxyConfiguration.Create(BrowserProxyMode.Burp);

        Assert.Contains("--proxy-server=http://127.0.0.1:8080", configuration.AdditionalBrowserArguments);
        Assert.Contains("--proxy-bypass-list=<-loopback>", configuration.AdditionalBrowserArguments);
        Assert.Contains("--disable-background-networking", configuration.AdditionalBrowserArguments);
        Assert.DoesNotContain("0.0.0.0", configuration.AdditionalBrowserArguments);
        Assert.EndsWith("Burp", configuration.UserDataFolder);
    }

    [Fact]
    public void NoiseFilter_BlocksOnlyTheCataloguedBingTelemetryEndpoint()
    {
        var enabled = BrowserTrafficNoiseFilter.BuildSetBlockedUrlsParameters(true);
        var disabled = BrowserTrafficNoiseFilter.BuildSetBlockedUrlsParameters(false);

        Assert.Contains("www.bing.com/web/xlsc.aspx", enabled);
        Assert.DoesNotContain("www.bing.com/*", enabled);
        Assert.DoesNotContain("https://*", enabled);
        Assert.Equal("{\"urls\":[]}", disabled);
    }
}
