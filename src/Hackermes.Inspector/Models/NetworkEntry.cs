using CommunityToolkit.Mvvm.ComponentModel;
using Hackermes.Platform.Services;
using System;

namespace Hackermes.Inspector.Models;

/// <summary>
/// 一条网络请求。数据来自两处并在此合并:
/// <list type="bullet">
/// <item>CDP Network 域 —— 协议层事实(状态码、大小、时序、MIME)</item>
/// <item>Page Agent —— 发起处的调用栈,协议层给不了这个</item>
/// </list>
/// </summary>
public partial class NetworkEntry : ObservableObject
{
    public required string PageId { get; init; }

    /// <summary>CDP 的 requestId,是本条记录的主键。</summary>
    public required string RequestId { get; init; }

    public required string Method { get; init; }

    public required string Url { get; init; }

    public DateTime StartedAt { get; init; } = DateTime.Now;

    /// <summary>只取路径末段用于列表展示,完整 URL 在详情里看。</summary>
    public string ShortName
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return Url;

            var name = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : string.Empty;
            return string.IsNullOrEmpty(name) ? uri.Host : name;
        }
    }

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    [ObservableProperty]
    private int _status;

    [ObservableProperty]
    private string _statusText = "…";

    [ObservableProperty]
    private string _resourceType = string.Empty;

    [ObservableProperty]
    private string _mimeType = string.Empty;

    [ObservableProperty]
    private long _encodedBytes;

    [ObservableProperty]
    private double _durationMs;

    /// <summary>发起处调用栈。来自 Page Agent,CDP 的 initiator 往往为空或只有 URL。</summary>
    [ObservableProperty]
    private string? _initiatorStack;

    /// <summary>发起方式:fetch / xhr / 其他(由 Agent 标注,未匹配到时为空)。</summary>
    [ObservableProperty]
    private string? _initiatorKind;

    [ObservableProperty]
    private bool _isFailed;

    /// <summary>
    /// Value-free security facts derived from the response headers. Raw header and
    /// Set-Cookie values are never exposed through this model.
    /// </summary>
    public NetworkSecurityMetadata SecurityMetadata { get; internal set; } = NetworkSecurityMetadata.Empty;

    public string SizeText => EncodedBytes <= 0
        ? "—"
        : EncodedBytes < 1024
            ? $"{EncodedBytes} B"
            : EncodedBytes < 1024 * 1024
                ? $"{EncodedBytes / 1024.0:F1} KB"
                : $"{EncodedBytes / (1024.0 * 1024):F2} MB";

    public string DurationText => DurationMs <= 0 ? "—" : $"{DurationMs:F0} ms";

    partial void OnEncodedBytesChanged(long value) => OnPropertyChanged(nameof(SizeText));

    partial void OnDurationMsChanged(double value) => OnPropertyChanged(nameof(DurationText));
}
