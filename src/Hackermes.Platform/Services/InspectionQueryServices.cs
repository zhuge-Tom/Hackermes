using System.Collections.Generic;

namespace Hackermes.Platform.Services;

/// <summary>检查面板向 AI 暴露的只读查询契约，避免功能模块横向引用。</summary>
public interface IConsoleQueryService
{
    IReadOnlyList<ConsoleObservation> Read(int last = 100, string? level = null, string? pageId = null);
}

public sealed record ConsoleObservation(string At, string Level, string Text, string? Source);

public interface INetworkQueryService
{
    IReadOnlyList<NetworkObservation> Read(int last = 100, bool failuresOnly = false, string? pageId = null);
}

public sealed record NetworkObservation(
    string RequestId, string Method, string Url, int Status, string StatusText,
    bool IsFailed, double DurationMs, string? InitiatorKind, string? InitiatorStack);

/// <summary>
/// Exposes a read-only snapshot of one explicitly identified embedded-browser page.
/// Implementations must match <paramref name="pageId"/> exactly and must never fall
/// back to another open page.
/// </summary>
public interface IPageContextQueryService
{
    PageContextObservation? Read(string pageId);
}

public sealed record PageContextObservation(
    string PageId,
    string Url,
    string Title,
    bool IsCdpReady,
    bool IsPageAgentReady);
