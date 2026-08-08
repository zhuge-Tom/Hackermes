using Hackermes.Base.Events;
using Hackermes.Cdp;
using Hackermes.Cdp.Session;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Inspector.Services;

public sealed record DomNodeItem(int Depth, string NodeName, string? Id, string? Classes, string? Text,
    string Path = "", string? ResourceUrl = null, int ChildCount = 0, string? NodeKey = null)
{
    public string Display => $"<{NodeName.ToLowerInvariant()}" +
        (string.IsNullOrEmpty(Id) ? "" : $" id=\"{Id}\"") +
        (string.IsNullOrEmpty(Classes) ? "" : $" class=\"{Classes}\"") + ">";
}

public sealed record PageStorageItem(string Area, string Key, string Value);
public sealed record DomPropertyItem(string Name, string Value);
public sealed record DomCssRuleItem(string? RuleKey, string Selector, string CssText, string Source, bool IsInline);
public sealed record DomNodeDetails(
    string Selector,
    string Path,
    int ChildCount,
    IReadOnlyList<DomPropertyItem> Attributes,
    IReadOnlyList<DomPropertyItem> ComputedStyles,
    string? ResourceUrl,
    string? NodeKey = null,
    IReadOnlyList<DomCssRuleItem>? MatchedRules = null);
public sealed record DomCssEditResult(bool Applied, string? Error, string? StyleText);
public sealed record DomPickerMessage(string PageId, string Kind, string Path, string? NodeKey, string Selector);
public sealed record PageResourceItem(
    string Type,
    string Name,
    string Url,
    long TransferSize,
    double Duration,
    int ElementCount,
    string ElementSummary);

/// <summary>One bounded CDP seam for the Stage 2 DOM, storage and resource inspectors.</summary>
public sealed class PageInspectionService : IDisposable
{
    public const int MaximumItems = 2_000;
    public const int MaximumDomDepth = 32;
    public const int MaximumValueCharacters = 16_384;
    private readonly ICdpSessionRegistry _sessions;
    private readonly IEventBus _eventBus;
    private readonly IDisposable _activeSubscription;
    private readonly IDisposable _pickerRequestSubscription;
    private readonly IDisposable _navigationSubscription;
    private readonly Dictionary<string, IDisposable> _pickerSubscriptions = [];
    private string? _activePageId;

    public event Action<DomPickerMessage>? PickerMessageReceived;
    public event Action<string>? PageNavigated;
    public string? ActivePageId => _activePageId;

    public PageInspectionService(ICdpSessionRegistry sessions, IEventBus eventBus)
    {
        _sessions = sessions;
        _eventBus = eventBus;
        _activeSubscription = eventBus.SubscribeDisposable<ActiveContentTabChangedEvent>(value =>
        {
            if (value.TabId?.StartsWith("page-", StringComparison.Ordinal) == true) _activePageId = value.TabId;
        });
        _pickerRequestSubscription = eventBus.SubscribeDisposable<ElementPickerToggleRequestedEvent>(request =>
            _ = SetPickerEnabledAsync(request.PageId, request.Enabled, CancellationToken.None));
        _navigationSubscription = eventBus.SubscribeDisposable<BrowserPageNavigatedEvent>(navigation =>
        {
            if (string.Equals(navigation.PageId, _activePageId, StringComparison.Ordinal))
                UiThreadBridge.Post(() => PageNavigated?.Invoke(navigation.PageId));
        });
    }

    public async Task<IReadOnlyList<DomNodeItem>> ReadDomAsync(CancellationToken cancellationToken)
    {
        const string expression = "(()=>{const o=[],w=(n,d,p)=>{if(!n||o.length>=2000||d>32||n.id==='__hackermes-inspector-overlay__'||n.id==='__hackermes-inspector-preview__')return;" +
            "const s=window.__hackermesInspectorStore||(window.__hackermesInspectorStore={next:0,nodes:new Map(),keys:new WeakMap(),nextRule:0,rules:new Map(),ruleKeys:new WeakMap()});const key=e=>{let k=s.keys.get(e);if(!k){k='n'+(++s.next);s.keys.set(e,k)}s.nodes.set(k,e);return k};if(n.nodeType===1){let resourceUrl=null,source=n.getAttribute('src')||n.getAttribute('href'),tag=n.nodeName.toLowerCase();try{if(source)resourceUrl=new URL(source,document.baseURI).href}catch{}o.push({depth:d,nodeName:n.nodeName,id:n.id||null,classes:typeof n.className==='string'?n.className:null,text:(n.childElementCount===0&&!['script','style','noscript'].includes(tag)?(n.textContent||'').trim().slice(0,160):null),path:p.join('/'),resourceUrl,childCount:n.children.length,nodeKey:key(n)})};" +
            "for(let i=0;i<(n.children||[]).length;i++)w(n.children[i],d+1,p.concat(i))};w(document.documentElement,0,[]);return o})()";
        return await EvaluateAsync<DomNodeItem[]>(expression, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<DomNodeDetails?> ReadDomNodeDetailsAsync(string path, string? nodeKey, CancellationToken cancellationToken)
    {
        var expression = "(()=>{const e=" + ResolveElementExpression(path, nodeKey) + ";if(!e)return null;" +
            "const selector=e.tagName.toLowerCase()+(e.id?'#'+e.id:'')+(typeof e.className==='string'&&e.className.trim()?'.'+e.className.trim().split(/\\s+/).join('.'):'');" +
            "const names=['display','position','width','height','margin-top','margin-right','margin-bottom','margin-left','padding-top','padding-right','padding-bottom','padding-left','color','background-color','font-family','font-size','line-height','border-top-width','border-top-style','border-top-color','z-index'];const computed=getComputedStyle(e);" +
            "const store=window.__hackermesInspectorStore||(window.__hackermesInspectorStore={next:0,nodes:new Map(),keys:new WeakMap(),nextRule:0,rules:new Map(),ruleKeys:new WeakMap()});store.rules||(store.rules=new Map());store.ruleKeys||(store.ruleKeys=new WeakMap());store.nextRule||(store.nextRule=0);const ruleKey=r=>{let k=store.ruleKeys.get(r);if(!k){k='r'+(++store.nextRule);store.ruleKeys.set(r,k)}store.rules.set(k,r);return k};const matched=[{ruleKey:null,selector:'element.style',cssText:e.getAttribute('style')||'',source:'inline',isInline:true}];const walk=(rules,source)=>{for(const r of rules||[]){if(matched.length>=80)break;try{if(r.type===CSSRule.STYLE_RULE&&e.matches(r.selectorText))matched.push({ruleKey:ruleKey(r),selector:r.selectorText,cssText:r.style.cssText,source,isInline:false});else if(r.cssRules)walk(r.cssRules,source)}catch{}}};for(const sheet of Array.from(document.styleSheets).slice(0,200)){try{walk(sheet.cssRules,sheet.href||'<style>')}catch{}}return {selector,path:" + JsonSerializer.Serialize(path) +
            ",childCount:e.children.length,attributes:Array.from(e.attributes).slice(0,80).map(a=>({name:a.name,value:a.value.slice(0,512)})),computedStyles:names.map(name=>({name,value:computed.getPropertyValue(name)})).filter(x=>x.value),resourceUrl:(()=>{const raw=e.getAttribute('src')||e.getAttribute('href');try{return raw?new URL(raw,document.baseURI).href:null}catch{return null}})(),nodeKey:" + JsonSerializer.Serialize(nodeKey) + ",matchedRules:matched}})()";
        return await EvaluateAsync<DomNodeDetails>(expression, cancellationToken).ConfigureAwait(false);
    }

    public Task<DomNodeDetails?> ReadDomNodeDetailsAsync(string path, CancellationToken cancellationToken) =>
        ReadDomNodeDetailsAsync(path, null, cancellationToken);

    public async Task<bool> HighlightDomElementAsync(string path, string? nodeKey, CancellationToken cancellationToken)
    {
        var expression = BuildRevealExpression(path, nodeKey, scrollIntoView: true, durationMilliseconds: 1_800);
        return await EvaluateAsync<bool>(expression, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HighlightDomElementAsync(string path, CancellationToken cancellationToken) =>
        HighlightDomElementAsync(path, null, cancellationToken);

    public async Task<bool> PreviewDomElementAsync(string path, string? nodeKey, CancellationToken cancellationToken)
    {
        var expression = BuildRevealExpression(path, nodeKey, scrollIntoView: false, durationMilliseconds: 650);
        return await EvaluateAsync<bool>(expression, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildRevealExpression(string path, string? nodeKey, bool scrollIntoView, int durationMilliseconds)
    {
        var scroll = scrollIntoView
            ? "element.scrollIntoView({block:'center',inline:'center',behavior:'auto'});"
            : string.Empty;
        return $"(()=>{{const element={ResolveElementExpression(path, nodeKey)};if(!element)return false;{scroll}" +
            "let overlay=document.getElementById('__hackermes-inspector-preview__');if(!overlay){overlay=document.createElement('div');overlay.id='__hackermes-inspector-preview__';overlay.style.cssText='position:fixed;pointer-events:none;z-index:2147483646;box-sizing:border-box;border:2px solid #1677ff;background:rgba(22,119,255,.16);';document.documentElement.appendChild(overlay)}" +
            "const rect=element.getBoundingClientRect();overlay.style.display='block';overlay.style.left=rect.left+'px';overlay.style.top=rect.top+'px';overlay.style.width=Math.max(1,rect.width)+'px';overlay.style.height=Math.max(1,rect.height)+'px';const token=String(Date.now())+Math.random();overlay.dataset.token=token;" +
            $"setTimeout(()=>{{if(overlay.dataset.token===token)overlay.style.display='none'}},{durationMilliseconds});return true}})()";
    }

    public async Task<DomCssEditResult> ApplyInlineCssAsync(string path, string? nodeKey, string cssText, CancellationToken cancellationToken)
    {
        if (cssText.Length > MaximumValueCharacters)
            return new DomCssEditResult(false, $"CSS is limited to {MaximumValueCharacters} characters.", null);

        var expression = $"(()=>{{const element={ResolveElementExpression(path, nodeKey)};if(!element)return {{applied:false,error:'The selected element no longer exists.',styleText:null}};try{{element.setAttribute('style',{JsonSerializer.Serialize(cssText)});return {{applied:true,error:null,styleText:element.getAttribute('style')}}}}catch(error){{return {{applied:false,error:String(error),styleText:null}}}}}})()";
        return await EvaluateAsync<DomCssEditResult>(expression, cancellationToken).ConfigureAwait(false)
            ?? new DomCssEditResult(false, "The page returned no CSS edit result.", null);
    }

    public async Task<DomCssEditResult> ApplyCssRuleAsync(string ruleKey, string cssText, CancellationToken cancellationToken)
    {
        if (cssText.Length > MaximumValueCharacters)
            return new DomCssEditResult(false, $"CSS is limited to {MaximumValueCharacters} characters.", null);
        var key = JsonSerializer.Serialize(ruleKey);
        var expression = $"(()=>{{const rule=window.__hackermesInspectorStore?.rules?.get({key});if(!rule)return {{applied:false,error:'The selected stylesheet rule is no longer available. Refresh the element.',styleText:null}};try{{rule.style.cssText={JsonSerializer.Serialize(cssText)};return {{applied:true,error:null,styleText:rule.style.cssText}}}}catch(error){{return {{applied:false,error:String(error),styleText:null}}}}}})()";
        return await EvaluateAsync<DomCssEditResult>(expression, cancellationToken).ConfigureAwait(false)
            ?? new DomCssEditResult(false, "The page returned no CSS edit result.", null);
    }

    public async Task SetPickerEnabledAsync(string pageId, bool enabled, CancellationToken cancellationToken)
    {
        var session = _sessions.Get(pageId);
        if (session is null)
        {
            _eventBus.Publish(new ElementPickerStateChangedEvent(pageId, false, "The browser page is no longer available."));
            return;
        }

        try
        {
            await EnsurePickerBindingAsync(session, cancellationToken).ConfigureAwait(false);
            var script = BuildPickerScript(enabled);
            await session.SendAsync("Runtime.evaluate", CdpJson.Params(("expression", script), ("returnByValue", true)), cancellationToken).ConfigureAwait(false);
            _eventBus.Publish(new ElementPickerStateChangedEvent(pageId, enabled));
            if (enabled) _eventBus.Publish(new SwitchDockTabRequestedEvent(Hackermes.Platform.Registries.DockPosition.Bottom, "dom-inspector"));
        }
        catch (Exception exception)
        {
            _eventBus.Publish(new ElementPickerStateChangedEvent(pageId, false, exception.Message));
        }
    }

    private async Task EnsurePickerBindingAsync(ICdpSession session, CancellationToken cancellationToken)
    {
        if (_pickerSubscriptions.ContainsKey(session.PageId)) return;
        try
        {
            await session.SendAsync("Runtime.addBinding", CdpJson.Params(("name", "__hackermes_inspector_pick__")), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Another activation may already have added the process-local binding.
        }
        _pickerSubscriptions[session.PageId] = await session.SubscribeAsync(
            "Runtime.bindingCalled", args => OnPickerBindingCalled(session.PageId, args), cancellationToken).ConfigureAwait(false);
    }

    private void OnPickerBindingCalled(string pageId, CdpEventArgs args)
    {
        if (!string.Equals(CdpJson.TryGetString(args.ParametersJson, "name"), "__hackermes_inspector_pick__", StringComparison.Ordinal)) return;
        var rawPayload = CdpJson.TryGetString(args.ParametersJson, "payload");
        if (string.IsNullOrWhiteSpace(rawPayload)) return;
        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            var root = document.RootElement;
            if (!string.Equals(root.TryGetProperty("t", out var type) ? type.GetString() : null, "inspector", StringComparison.Ordinal)) return;
            var kind = root.TryGetProperty("k", out var kindValue) ? kindValue.GetString() : null;
            if (kind is "pick" or "cancel") _eventBus.Publish(new ElementPickerStateChangedEvent(pageId, false));
            if (kind is not ("pick" or "hover")) return;
            var path = root.TryGetProperty("path", out var pathValue) ? pathValue.GetString() : null;
            if (path is null) return;
            var nodeKey = root.TryGetProperty("nodeKey", out var keyValue) ? keyValue.GetString() : null;
            var selector = root.TryGetProperty("selector", out var selectorValue) ? selectorValue.GetString() ?? "element" : "element";
            UiThreadBridge.Post(() => PickerMessageReceived?.Invoke(new DomPickerMessage(pageId, kind, path, nodeKey, selector)));
        }
        catch (JsonException) { }
    }

    private static string ResolveElementExpression(string path, string? nodeKey)
    {
        var steps = SerializePath(path);
        var key = JsonSerializer.Serialize(nodeKey);
        return $"(()=>{{const s=window.__hackermesInspectorStore;let e={key}&&s?.nodes?.get({key});if(!e){{e=document.documentElement;for(const step of {steps}){{e=e?.children[step];if(!e)break}}}}return e||null}})()";
    }

    private static string BuildPickerScript(bool enabled) => PickerScript.Replace("__HACKERMES_PICKER_ENABLED__", enabled ? "true" : "false", StringComparison.Ordinal);

    private const string PickerScript = """
        (() => {
          const enabled = __HACKERMES_PICKER_ENABLED__;
          const global = globalThis;
          const store = global.__hackermesInspectorStore || (global.__hackermesInspectorStore = { next: 0, nodes: new Map(), keys: new WeakMap() });
          const state = global.__hackermesInspectorPicker || (global.__hackermesInspectorPicker = { installed: false, enabled: false, overlay: null, label: null, target: null, lastKey: null, lastSent: 0 });
          const keyFor = element => { let key = store.keys.get(element); if (!key) { key = 'n' + (++store.next); store.keys.set(element, key); } store.nodes.set(key, element); return key; };
          const describe = element => element.tagName.toLowerCase() + (element.id ? '#' + element.id : '') + (typeof element.className === 'string' && element.className.trim() ? '.' + element.className.trim().split(/\s+/).slice(0, 3).join('.') : '');
          const pathFor = element => { const steps = []; for (let current = element; current && current !== document.documentElement; current = current.parentElement) { const parent = current.parentElement; if (!parent) break; steps.unshift(Array.prototype.indexOf.call(parent.children, current)); } return steps.join('/'); };
          const hide = () => { if (state.overlay) state.overlay.style.display = 'none'; };
          const ensureOverlay = () => {
            if (state.overlay?.isConnected) return;
            const overlay = document.createElement('div'); overlay.id = '__hackermes-inspector-overlay__';
            overlay.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;display:none;box-sizing:border-box;border:2px solid #1677ff;background:rgba(22,119,255,.14);';
            const label = document.createElement('div'); label.style.cssText = 'position:absolute;left:-2px;top:-24px;max-width:420px;padding:3px 6px;background:#1677ff;color:white;font:12px/18px Consolas,monospace;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;';
            overlay.appendChild(label); document.documentElement.appendChild(overlay); state.overlay = overlay; state.label = label;
          };
          const show = element => { if (!element || element.id === '__hackermes-inspector-overlay__' || element.id === '__hackermes-inspector-preview__') return; ensureOverlay(); const rect = element.getBoundingClientRect(); const overlay = state.overlay; overlay.style.display = 'block'; overlay.style.left = rect.left + 'px'; overlay.style.top = rect.top + 'px'; overlay.style.width = Math.max(1, rect.width) + 'px'; overlay.style.height = Math.max(1, rect.height) + 'px'; state.label.textContent = describe(element) + '  ' + Math.round(rect.width) + ' × ' + Math.round(rect.height); state.target = element; };
          const send = (kind, element) => { const binding = global.__hackermes_inspector_pick__; if (typeof binding !== 'function') return; try { binding(JSON.stringify({ t: 'inspector', k: kind, path: element ? pathFor(element) : '', nodeKey: element ? keyFor(element) : null, selector: element ? describe(element) : '' })); } catch {} };
          if (!state.installed) {
            state.installed = true;
            document.addEventListener('mousemove', event => { if (!state.enabled || !(event.target instanceof Element)) return; const target = event.target; show(target); const key = keyFor(target), now = Date.now(); if (key !== state.lastKey || now - state.lastSent > 160) { state.lastKey = key; state.lastSent = now; send('hover', target); } }, true);
            document.addEventListener('click', event => { if (!state.enabled || !(event.target instanceof Element)) return; event.preventDefault(); event.stopImmediatePropagation(); const target = event.target; state.enabled = false; send('pick', target); hide(); }, true);
            document.addEventListener('keydown', event => { if (state.enabled && event.key === 'Escape') { state.enabled = false; hide(); send('cancel', null); } }, true);
          }
          state.enabled = enabled;
          if (!enabled) hide();
          return { enabled };
        })()
        """;

    public async Task<IReadOnlyList<PageStorageItem>> ReadStorageAsync(CancellationToken cancellationToken)
    {
        const string expression = "(()=>{const o=[],add=(a,s)=>{for(let i=0;i<s.length&&o.length<2000;i++){const k=s.key(i);o.push({area:a,key:k,value:(s.getItem(k)||'').slice(0,16384)})}};" +
            "try{add('localStorage',localStorage)}catch{}try{add('sessionStorage',sessionStorage)}catch{}" +
            "for(const p of document.cookie.split(';')){if(o.length>=2000)break;const i=p.indexOf('=');if(i>=0)o.push({area:'cookie',key:p.slice(0,i).trim(),value:p.slice(i+1).trim().slice(0,16384)})}return o})()";
        return await EvaluateAsync<PageStorageItem[]>(expression, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<PageResourceItem>> ReadResourcesAsync(CancellationToken cancellationToken)
    {
        const string expression = "(()=>{const sources=new Map(),add=(u,label)=>{if(!u)return;try{u=new URL(u,document.baseURI).href}catch{return}const match=sources.get(u)||{count:0,labels:[]};match.count++;if(match.labels.length<3)match.labels.push(label);sources.set(u,match)},label=e=>{let v=e.tagName.toLowerCase();if(e.id)v+='#'+e.id;return v};for(const e of document.querySelectorAll('[src],[href]')){add(e.getAttribute('src'),label(e));add(e.getAttribute('href'),label(e))}return performance.getEntriesByType('resource').slice(-2000).map(x=>{const match=sources.get(x.name),a=match?.labels||[],extra=match&&match.count>a.length?' (+'+(match.count-a.length)+')':'';return {type:x.initiatorType||'resource',name:(x.name.split('/').pop()||x.name).slice(0,256),url:x.name,transferSize:Math.max(0,Math.trunc(x.transferSize||0)),duration:Number((x.duration||0).toFixed(2)),elementCount:match?.count||0,elementSummary:a.length?a.join(', ')+extra:'No DOM source'}})})()";
        return await EvaluateAsync<PageResourceItem[]>(expression, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<int> HighlightResourceElementsAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var target = JsonSerializer.Serialize(url);
        var expression = $"(()=>{{const target={target},found=[];for(const e of document.querySelectorAll('[src],[href]')){{for(const a of ['src','href']){{const raw=e.getAttribute(a);if(!raw)continue;try{{if(new URL(raw,document.baseURI).href===target){{found.push(e);break}}}}catch{{}}}}}}for(const e of found){{const old=e.style.outline,oldOffset=e.style.outlineOffset;e.style.setProperty('outline','2px solid #1677ff','important');e.style.setProperty('outline-offset','2px','important');setTimeout(()=>{{e.style.outline=old;e.style.outlineOffset=oldOffset}},1800)}}return found.length}})()";
        return await EvaluateAsync<int>(expression, cancellationToken).ConfigureAwait(false);
    }

    public void OpenResourceInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only HTTP(S) resource URLs can be opened in a browser tab.", nameof(url));
        _eventBus.Publish(new OpenBrowserTabRequestedEvent(uri.AbsoluteUri));
    }

    private static string SerializePath(string path)
    {
        var steps = string.IsNullOrEmpty(path)
            ? Array.Empty<int>()
            : path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
        return JsonSerializer.Serialize(steps);
    }

    private async Task<T?> EvaluateAsync<T>(string expression, CancellationToken cancellationToken)
    {
        var session = ResolveSession() ?? throw new InvalidOperationException("Open and select a browser page first.");
        var parameters = JsonSerializer.Serialize(new { expression, returnByValue = true, awaitPromise = true });
        var response = await session.SendAsync("Runtime.evaluate", parameters, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.TryGetProperty("exceptionDetails", out _))
            throw new InvalidOperationException("The page rejected the inspection expression.");
        if (!document.RootElement.TryGetProperty("result", out var remote) ||
            !remote.TryGetProperty("value", out var value)) return default;
        return value.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private ICdpSession? ResolveSession()
    {
        if (_activePageId is not null && _sessions.Get(_activePageId) is { } active) return active;
        return _sessions.All.LastOrDefault();
    }

    public void Dispose()
    {
        _activeSubscription.Dispose();
        _pickerRequestSubscription.Dispose();
        _navigationSubscription.Dispose();
        foreach (var subscription in _pickerSubscriptions.Values) subscription.Dispose();
        _pickerSubscriptions.Clear();
    }
}
