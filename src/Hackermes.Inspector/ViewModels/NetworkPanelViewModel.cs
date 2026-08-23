using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Mvvm;
using Hackermes.Inspector.Models;
using Hackermes.Inspector.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hackermes.Inspector.ViewModels;

public partial class NetworkPanelViewModel : ViewModelBase
{
    private readonly NetworkStore _store;

    public NetworkPanelViewModel(NetworkStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    /// <summary>过滤后的视图。直接绑 store 的集合会让过滤无处安放。</summary>
    public ObservableCollection<NetworkEntry> Visible { get; } = [];

    [ObservableProperty]
    private NetworkEntry? _selected;

    /// <summary>Burp 风格的请求原始报文(请求行 + 头部块 + 请求体)。</summary>
    [ObservableProperty]
    private string? _requestView;

    /// <summary>Burp 风格的响应原始报文;响应体在选中后懒加载。</summary>
    [ObservableProperty]
    private string? _responseView;

    private bool _isBodyLoading;

    partial void OnSelectedChanged(NetworkEntry? value)
    {
        if (value is null)
        {
            RequestView = ResponseView = null;
            return;
        }

        RequestView = Services.HttpPacketFormatter.FormatRequest(
            value.Method, value.Url, value.RequestHeadersJson, value.RequestBody);
        ResponseView = value.ResponseHeadersJson is null
            ? "（暂无响应记录）"
            : Services.HttpPacketFormatter.FormatResponse(value.Status, StatusPhrase(value), value.Url,
                value.ResponseHeadersJson, value.ResponseBodyText);

        // 响应体只在点开这一条时才取,避免对全量记录放大流量。
        if (!value.IsResponseBodyLoaded && value.ResponseHeadersJson is not null && !_isBodyLoading)
            _ = LoadBodyAsync(value);
    }

    private async System.Threading.Tasks.Task LoadBodyAsync(NetworkEntry entry)
    {
        _isBodyLoading = true;
        try
        {
            var body = await _store.LoadResponseBodyAsync(entry);
            entry.ResponseBodyText = body;
            entry.DetailError = null;
        }
        catch (Exception exception)
        {
            entry.ResponseBodyText = $"(响应体读取失败: {exception.Message})";
            entry.DetailError = exception.Message;
        }
        finally
        {
            entry.IsResponseBodyLoaded = true;
            _isBodyLoading = false;
            if (ReferenceEquals(Selected, entry) && entry.ResponseHeadersJson is not null)
                ResponseView = Services.HttpPacketFormatter.FormatResponse(entry.Status, StatusPhrase(entry),
                    entry.Url, entry.ResponseHeadersJson, entry.ResponseBodyText);
        }
    }

    private static string StatusPhrase(NetworkEntry entry)
    {
        // StatusText 存的是 "200 OK" 形态;格式化器自己拼状态码,这里只留短语部分。
        var prefix = entry.Status.ToString();
        var text = entry.StatusText;
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return text;
        return text.Length > prefix.Length ? text[(prefix.Length + 1)..] : string.Empty;
    }

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _onlyFailed;

    [ObservableProperty]
    private string _summary = "尚无请求";

    partial void OnFilterTextChanged(string value) => Refresh();

    partial void OnOnlyFailedChanged(bool value) => Refresh();

    [RelayCommand]
    private void Clear() => _store.Clear();

    private void OnStoreChanged() => Refresh();

    private void Refresh()
    {
        var keyword = FilterText?.Trim();

        var query = _store.Entries.AsEnumerable();

        if (OnlyFailed)
            query = query.Where(e => e.IsFailed);

        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(e =>
                e.Url.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || e.Method.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || e.ResourceType.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query.ToArray();

        // 整体重建而不是增量 diff:2000 条上限下重建足够快,而增量同步的
        // 边界条件(过滤条件变化 + 并发插入)很容易出错。
        Visible.Clear();
        foreach (var entry in filtered)
            Visible.Add(entry);

        var total = _store.Entries.Count;
        var failed = _store.Entries.Count(e => e.IsFailed);
        var withStack = _store.Entries.Count(e => !string.IsNullOrEmpty(e.InitiatorStack));

        Summary = total == 0
            ? "尚无请求"
            : $"共 {total} 条 · 失败 {failed} · 含调用栈 {withStack}" +
              (filtered.Length == total ? string.Empty : $" · 显示 {filtered.Length}");
    }

    protected override void OnDispose() => _store.Changed -= OnStoreChanged;
}
