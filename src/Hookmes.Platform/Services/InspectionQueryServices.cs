using System.Collections.Generic;

namespace Hookmes.Platform.Services;

/// <summary>检查面板向 AI 暴露的只读查询契约，避免功能模块横向引用。</summary>
public interface IConsoleQueryService
{
    IReadOnlyList<ConsoleObservation> Read(int last = 100, string? level = null);
}

public sealed record ConsoleObservation(string At, string Level, string Text, string? Source);

public interface INetworkQueryService
{
    IReadOnlyList<NetworkObservation> Read(int last = 100, bool failuresOnly = false);
}

public sealed record NetworkObservation(
    string RequestId, string Method, string Url, int Status, string StatusText,
    bool IsFailed, double DurationMs, string? InitiatorKind, string? InitiatorStack);
