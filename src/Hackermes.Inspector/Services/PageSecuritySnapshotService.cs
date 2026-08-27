using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Inspector.Services;

/// <summary>
/// Composes protocol metadata with a value-free DOM inventory captured in the
/// browser-owned named isolated world. The public seam is deliberately small; page
/// identity checks, navigation races, bounds and URL redaction stay behind it.
/// </summary>
public sealed class PageSecuritySnapshotService(
    IPageContextQueryService pages,
    IPageAgentRuntime runtime,
    INetworkSecurityMetadataQueryService network) : IPageSecuritySnapshotService
{
    public const int MaximumForms = 40;
    public const int MaximumExternalScripts = 100;
    public const int MaximumTitleCharacters = 256;
    public const int MaximumUrlCharacters = 2_048;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PageSecuritySnapshot> ReadAsync(
        string pageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageId))
            throw new InvalidOperationException("An exact browser pageId is required.");

        var before = pages.Read(pageId)
            ?? throw new InvalidOperationException("The selected browser page is no longer available.");
        if (!string.Equals(before.PageId, pageId, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected browser page did not match the requested pageId.");
        if (!before.IsCdpReady)
            throw new InvalidOperationException("The selected browser page is not ready for security inspection.");
        if (!Uri.TryCreate(before.Url, UriKind.Absolute, out var documentUri) ||
            (documentUri.Scheme != Uri.UriSchemeHttp && documentUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Security snapshots require an HTTP(S) browser page.");

        var capability = runtime.GetCapability(pageId);
        if (!string.Equals(capability.PageId, pageId, StringComparison.Ordinal) ||
            capability.IsolatedWorld != PageAgentWorldState.Ready)
            throw new InvalidOperationException(
                capability.Detail ?? "The selected page's isolated inspection world is unavailable.");

        var response = await runtime.EvaluateInIsolatedWorldAsync(
            pageId,
            DomSnapshotExpression,
            cancellationToken).ConfigureAwait(false);
        var dom = ReadDomResult(response);

        // Navigation or close between capture and composition must invalidate the whole
        // result. In particular, never combine DOM from one document with headers from another.
        var after = pages.Read(pageId);
        if (after is null ||
            !string.Equals(after.PageId, pageId, StringComparison.Ordinal) ||
            !string.Equals(after.Url, before.Url, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected browser page changed during security inspection.");

        var networkMetadata = network.ReadSecurityMetadata(pageId, before.Url);
        var safeUrl = SanitizeUrl(before.Url);
        var origin = ReadOrigin(before.Url);
        var snapshot = new PageSecuritySnapshot(
            pageId,
            safeUrl,
            origin,
            Truncate(before.Title, MaximumTitleCharacters),
            new PageSecurityTransportSnapshot(
                networkMetadata.HasDocumentResponse,
                Math.Clamp(networkMetadata.Status, 0, 999),
                string.Equals(documentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                networkMetadata.HasStrictTransportSecurity,
                networkMetadata.HasContentSecurityPolicy,
                networkMetadata.HasContentSecurityPolicyReportOnly,
                networkMetadata.ContentSecurityPolicyDirectives.Take(64).Select(value => Truncate(value, 64)).ToArray(),
                networkMetadata.ContentSecurityPolicyAllowsUnsafeInline,
                networkMetadata.ContentSecurityPolicyAllowsUnsafeEval,
                networkMetadata.ContentSecurityPolicyHasWildcardSource,
                networkMetadata.HasXContentTypeOptions,
                networkMetadata.HasFrameProtection,
                networkMetadata.HasReferrerPolicy,
                networkMetadata.HasPermissionsPolicy,
                networkMetadata.HasCrossOriginOpenerPolicy,
                networkMetadata.HasCrossOriginEmbedderPolicy,
                networkMetadata.HasCrossOriginResourcePolicy,
                networkMetadata.Cookies),
            NormalizeDom(dom));
        return snapshot with { Observations = PageSecurityObservations.From(snapshot.Transport, snapshot.Dom) };
    }

    private static PageSecurityDomSnapshot NormalizeDom(DomResult dom)
    {
        var forms = (dom.Forms ?? [])
            .Take(MaximumForms)
            .Select(form => new PageSecurityFormSnapshot(
                NormalizeMethod(form.Method),
                SanitizeUrl(form.Action),
                form.IsCrossOrigin,
                Math.Clamp(form.InputCount, 0, 10_000),
                Math.Clamp(form.PasswordInputCount, 0, 10_000),
                form.AutocompleteDisabled))
            .ToArray();
        var scripts = (dom.ExternalScripts ?? [])
            .Take(MaximumExternalScripts)
            .Select(script => new PageSecurityScriptSnapshot(
                SanitizeUrl(script.Source),
                ReadOrigin(script.Source),
                script.IsCrossOrigin,
                script.HasIntegrity,
                Truncate(script.CrossOriginMode, 32)))
            .ToArray();
        var formCount = Math.Clamp(dom.FormCount, 0, 100_000);
        var externalScriptCount = Math.Clamp(dom.ExternalScriptCount, 0, 100_000);
        return new PageSecurityDomSnapshot(
            formCount,
            Math.Max(0, formCount - forms.Length),
            forms,
            externalScriptCount,
            Math.Clamp(dom.InlineScriptCount, 0, 100_000),
            Math.Max(0, externalScriptCount - scripts.Length),
            scripts,
            Math.Clamp(dom.PasswordInputCount, 0, 100_000),
            Math.Clamp(dom.HiddenInputCount, 0, 100_000),
            Math.Clamp(dom.MixedContentResourceCount, 0, 100_000));
    }

    private static DomResult ReadDomResult(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("result", out var remote) ||
                !remote.TryGetProperty("value", out var value))
                throw new InvalidOperationException("The selected page returned no security snapshot value.");
            return value.Deserialize<DomResult>(JsonOptions)
                ?? throw new InvalidOperationException("The selected page returned an invalid security snapshot.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The selected page returned an invalid security snapshot.", exception);
        }
    }

    private static string SanitizeUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return string.Empty;
        var safe = uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped);
        return Truncate(safe, MaximumUrlCharacters);
    }

    private static string ReadOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return string.Empty;
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port);
        return Truncate(builder.Uri.GetLeftPart(UriPartial.Authority), 512);
    }

    private static string NormalizeMethod(string? value) => value?.ToUpperInvariant() switch
    {
        "GET" => "GET",
        "POST" => "POST",
        "DIALOG" => "DIALOG",
        _ => "OTHER"
    };

    private static string Truncate(string? value, int maximum) => string.IsNullOrEmpty(value)
        ? string.Empty
        : value.Length <= maximum ? value : value[..maximum];

    private sealed record DomResult(
        int FormCount,
        IReadOnlyList<DomForm>? Forms,
        int ExternalScriptCount,
        int InlineScriptCount,
        IReadOnlyList<DomScript>? ExternalScripts,
        int PasswordInputCount,
        int HiddenInputCount,
        int MixedContentResourceCount);

    private sealed record DomForm(
        string Method,
        string Action,
        bool IsCrossOrigin,
        int InputCount,
        int PasswordInputCount,
        bool AutocompleteDisabled);

    private sealed record DomScript(
        string Source,
        string Origin,
        bool IsCrossOrigin,
        bool HasIntegrity,
        string? CrossOriginMode);

    // The expression returns metadata only: no field values, DOM text, inline script
    // source, local/session storage, cookie strings, request bodies or credentials.
    private const string DomSnapshotExpression = """
        (() => {
          const MAX_FORMS = 40, MAX_SCRIPTS = 100;
          const safeUrl = value => {
            try { const u = new URL(value || '', document.baseURI); if (!/^https?:$/.test(u.protocol)) return ''; u.username = ''; u.password = ''; u.search = ''; u.hash = ''; return u.href.slice(0, 2048); }
            catch { return ''; }
          };
          const originOf = value => { try { const u = new URL(value || '', document.baseURI); return /^https?:$/.test(u.protocol) ? u.origin.slice(0, 512) : ''; } catch { return ''; } };
          const forms = Array.from(document.forms);
          const formItems = forms.slice(0, MAX_FORMS).map(form => {
            const inputs = Array.from(form.querySelectorAll('input,select,textarea,button'));
            const action = safeUrl(form.getAttribute('action') || document.location.href);
            return {
              method: String(form.method || 'get').slice(0, 16), action,
              isCrossOrigin: !!action && originOf(action) !== location.origin,
              inputCount: inputs.length,
              passwordInputCount: inputs.filter(input => input instanceof HTMLInputElement && input.type === 'password').length,
              autocompleteDisabled: String(form.autocomplete || '').toLowerCase() === 'off'
            };
          });
          const scripts = Array.from(document.scripts), external = scripts.filter(script => !!script.src);
          const scriptItems = external.slice(0, MAX_SCRIPTS).map(script => {
            const source = safeUrl(script.src);
            return { source, origin: originOf(source), isCrossOrigin: !!source && originOf(source) !== location.origin,
              hasIntegrity: !!script.integrity, crossOriginMode: script.crossOrigin ? String(script.crossOrigin).slice(0, 32) : null };
          });
          const allInputs = Array.from(document.querySelectorAll('input'));
          const resourceUrls = Array.from(document.querySelectorAll('[src],[href]')).flatMap(element => ['src','href'].map(name => element.getAttribute(name)).filter(Boolean));
          return {
            formCount: forms.length, forms: formItems,
            externalScriptCount: external.length, inlineScriptCount: scripts.length - external.length, externalScripts: scriptItems,
            passwordInputCount: allInputs.filter(input => input.type === 'password').length,
            hiddenInputCount: allInputs.filter(input => input.type === 'hidden').length,
            mixedContentResourceCount: location.protocol === 'https:' ? resourceUrls.filter(value => { try { return new URL(value, document.baseURI).protocol === 'http:'; } catch { return false; } }).length : 0
          };
        })()
        """;
}
