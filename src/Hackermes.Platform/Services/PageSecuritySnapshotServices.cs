using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Platform.Services;

/// <summary>
/// Reads one bounded, value-free security snapshot for an exact embedded-browser page.
/// Implementations must fail closed when the page is unknown, closed, navigates during
/// capture, or its isolated world is unavailable. They must never select another page.
/// </summary>
public interface IPageSecuritySnapshotService
{
    Task<PageSecuritySnapshot> ReadAsync(
        string pageId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies protocol-derived security metadata for one exact page. Raw response header
/// and cookie values intentionally never cross this contract.
/// </summary>
public interface INetworkSecurityMetadataQueryService
{
    NetworkSecurityMetadata ReadSecurityMetadata(string pageId, string documentUrl);
}

public sealed record PageSecuritySnapshot(
    string PageId,
    string Url,
    string Origin,
    string Title,
    PageSecurityTransportSnapshot Transport,
    PageSecurityDomSnapshot Dom)
{
    public IReadOnlyList<PageSecurityObservation> Observations { get; init; } = [];
}

public sealed record PageSecurityObservation(string Code, string Severity, string Message);

public static class PageSecurityObservations
{
    public static IReadOnlyList<PageSecurityObservation> From(
        PageSecurityTransportSnapshot transport,
        PageSecurityDomSnapshot dom)
    {
        var items = new List<PageSecurityObservation>();
        if (transport.IsHttps && !transport.HasStrictTransportSecurity)
            items.Add(new("missing-hsts", "Warning", "The document response has no Strict-Transport-Security header."));
        if (!transport.HasContentSecurityPolicy)
            items.Add(new("missing-csp", "Warning", "The document response has no Content-Security-Policy header."));
        if (transport.ContentSecurityPolicyAllowsUnsafeInline)
            items.Add(new("csp-unsafe-inline", "Warning", "The Content-Security-Policy allows unsafe-inline."));
        if (transport.ContentSecurityPolicyAllowsUnsafeEval)
            items.Add(new("csp-unsafe-eval", "Warning", "The Content-Security-Policy allows unsafe-eval."));
        if (transport.ContentSecurityPolicyHasWildcardSource)
            items.Add(new("csp-wildcard-src", "Warning", "The Content-Security-Policy includes a wildcard source."));
        if (!transport.HasXContentTypeOptions)
            items.Add(new("missing-xcto", "Info", "The document response has no X-Content-Type-Options header."));
        if (!transport.HasFrameProtection)
            items.Add(new("missing-frame-protection", "Warning", "The document response has no frame protection."));
        if (transport.Cookies.SetCookieCount > 0 && transport.Cookies.SecureCount < transport.Cookies.SetCookieCount)
            items.Add(new("cookie-missing-secure", "Warning", "One or more Set-Cookie headers are missing the Secure attribute."));
        if (transport.Cookies.SetCookieCount > 0 && transport.Cookies.HttpOnlyCount < transport.Cookies.SetCookieCount)
            items.Add(new("cookie-missing-httponly", "Warning", "One or more Set-Cookie headers are missing the HttpOnly attribute."));
        if (dom.MixedContentResourceCount > 0)
            items.Add(new("mixed-content", "High", "The page includes mixed-content resources."));
        if (dom.Forms.Any(form => form.IsCrossOrigin))
            items.Add(new("cross-origin-form", "Info", "The page contains a cross-origin form."));
        if (dom.ExternalScripts.Any(script => !script.HasIntegrity))
            items.Add(new("script-missing-integrity", "Info", "An external script is missing an integrity attribute."));
        return items;
    }
}

public sealed record PageSecurityTransportSnapshot(
    bool HasDocumentResponse,
    int Status,
    bool IsHttps,
    bool HasStrictTransportSecurity,
    bool HasContentSecurityPolicy,
    bool HasContentSecurityPolicyReportOnly,
    IReadOnlyList<string> ContentSecurityPolicyDirectives,
    bool ContentSecurityPolicyAllowsUnsafeInline,
    bool ContentSecurityPolicyAllowsUnsafeEval,
    bool ContentSecurityPolicyHasWildcardSource,
    bool HasXContentTypeOptions,
    bool HasFrameProtection,
    bool HasReferrerPolicy,
    bool HasPermissionsPolicy,
    bool HasCrossOriginOpenerPolicy,
    bool HasCrossOriginEmbedderPolicy,
    bool HasCrossOriginResourcePolicy,
    PageSecurityCookieSummary Cookies);

public sealed record PageSecurityCookieSummary(
    int SetCookieCount,
    int SecureCount,
    int HttpOnlyCount,
    int SameSiteStrictCount,
    int SameSiteLaxCount,
    int SameSiteNoneCount,
    int PartitionedCount);

public sealed record PageSecurityDomSnapshot(
    int FormCount,
    int TruncatedFormCount,
    IReadOnlyList<PageSecurityFormSnapshot> Forms,
    int ExternalScriptCount,
    int InlineScriptCount,
    int TruncatedScriptCount,
    IReadOnlyList<PageSecurityScriptSnapshot> ExternalScripts,
    int PasswordInputCount,
    int HiddenInputCount,
    int MixedContentResourceCount);

public sealed record PageSecurityFormSnapshot(
    string Method,
    string Action,
    bool IsCrossOrigin,
    int InputCount,
    int PasswordInputCount,
    bool AutocompleteDisabled);

public sealed record PageSecurityScriptSnapshot(
    string Source,
    string Origin,
    bool IsCrossOrigin,
    bool HasIntegrity,
    string? CrossOriginMode);

public sealed record NetworkSecurityMetadata(
    bool HasDocumentResponse,
    int Status,
    bool HasStrictTransportSecurity,
    bool HasContentSecurityPolicy,
    bool HasContentSecurityPolicyReportOnly,
    IReadOnlyList<string> ContentSecurityPolicyDirectives,
    bool ContentSecurityPolicyAllowsUnsafeInline,
    bool ContentSecurityPolicyAllowsUnsafeEval,
    bool ContentSecurityPolicyHasWildcardSource,
    bool HasXContentTypeOptions,
    bool HasFrameProtection,
    bool HasReferrerPolicy,
    bool HasPermissionsPolicy,
    bool HasCrossOriginOpenerPolicy,
    bool HasCrossOriginEmbedderPolicy,
    bool HasCrossOriginResourcePolicy,
    PageSecurityCookieSummary Cookies)
{
    public static NetworkSecurityMetadata Empty { get; } = new(
        false, 0, false, false, false, [], false, false, false,
        false, false, false, false, false, false, false,
        new PageSecurityCookieSummary(0, 0, 0, 0, 0, 0, 0));
}
