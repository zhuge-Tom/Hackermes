using System.Collections.Generic;
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
    PageSecurityDomSnapshot Dom);

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
