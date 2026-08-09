using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Hackermes.Browser.Services;

/// <summary>
/// A deliberately small allow-list of known background telemetry endpoints.  This is
/// not an ad blocker: patterns must be exact enough that normal page resources and
/// application APIs remain visible to an intercepting proxy.
/// </summary>
public static class BrowserTrafficNoiseFilter
{
    public static IReadOnlyList<string> KnownTelemetryUrlPatterns { get; } =
    [
        "*://www.bing.com/web/xlsc.aspx*"
    ];

    public static string BuildSetBlockedUrlsParameters(bool enabled) =>
        JsonSerializer.Serialize(new
        {
            urls = enabled ? KnownTelemetryUrlPatterns : Array.Empty<string>()
        });
}

public sealed record BrowserTelemetryFilterChangedEvent(bool Enabled);
