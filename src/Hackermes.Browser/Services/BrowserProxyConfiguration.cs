using System;
using System.IO;
using Hackermes.Base;

namespace Hackermes.Browser.Services;

/// <summary>
/// Proxy modes exposed by the built-in browser proxy plug-in.  The Burp endpoint is
/// deliberately loopback-only: the UI must never turn into an arbitrary proxy launcher.
/// </summary>
public enum BrowserProxyMode
{
    Direct,
    Burp
}

public sealed record BrowserProxyConfiguration(
    BrowserProxyMode Mode,
    string DisplayName,
    string AdditionalBrowserArguments,
    string UserDataFolder)
{
    public const string ProfileRootEnvironmentVariable = "HACKERMES_BROWSER_PROFILE_ROOT";
    public const string BurpHost = "127.0.0.1";
    public const int BurpPort = 8080;
    public const string BurpEndpoint = "127.0.0.1:8080";

    public static BrowserProxyMode ParseMode(string? value) =>
        string.Equals(value, "burp", StringComparison.OrdinalIgnoreCase)
            ? BrowserProxyMode.Burp
            : BrowserProxyMode.Direct;

    public static string ToSetting(BrowserProxyMode mode) =>
        mode == BrowserProxyMode.Burp ? "burp" : "direct";

    public static BrowserProxyConfiguration Create(BrowserProxyMode mode)
    {
        var profileName = mode == BrowserProxyMode.Burp ? "Burp" : "Direct";
        var overrideRoot = Environment.GetEnvironmentVariable(ProfileRootEnvironmentVariable);
        var baseProfileRoot = string.IsNullOrWhiteSpace(overrideRoot)
            ? AppDataPaths.Resolve("Browser", "WebView2")
            : ResolveOverrideRoot(overrideRoot);
        var profileRoot = Path.Combine(baseProfileRoot, profileName);

        return mode == BrowserProxyMode.Burp
            ? new BrowserProxyConfiguration(
                mode,
                $"Burp :{BurpPort}",
                $"--proxy-server=http://{BurpEndpoint} --proxy-bypass-list=<-loopback> --disable-background-networking",
                profileRoot)
            : new BrowserProxyConfiguration(
                mode,
                "直连",
                "--no-proxy-server",
                profileRoot);
    }

    private static string ResolveOverrideRoot(string value)
    {
        if (!Path.IsPathFullyQualified(value))
            throw new InvalidOperationException(
                $"{ProfileRootEnvironmentVariable} must be an absolute path.");

        return Path.GetFullPath(value);
    }
}

/// <summary>Broadcast after the user changes the browser proxy plug-in mode.</summary>
public sealed record BrowserProxyModeChangedEvent(BrowserProxyMode Mode);
