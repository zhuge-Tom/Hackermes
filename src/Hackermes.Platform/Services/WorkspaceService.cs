using Hackermes.Base.Diagnostics;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using System;
using System.IO;

namespace Hackermes.Platform.Services;

/// <summary>
/// 工作区 = 一个目录;数据库 = 该目录下的 <c>.hackermes.db</c>。
/// <para>
/// 本服务只负责"当前打开的是哪个目录"并广播 <see cref="ProjectOpenedEvent"/>。
/// <strong>它不建库也不建表</strong> —— 各模块的 store 订阅事件后自行惰性建表,
/// 数据库文件由第一个真正写数据的 store 隐式创建(SQLite 打开即创建)。
/// </para>
/// </summary>
public interface IWorkspaceService
{
    Workspace? Current { get; }

    bool HasWorkspace { get; }

    void Open(string directory);

    void Close();
}

public sealed record Workspace(string Directory)
{
    public const string DatabaseFileName = ".hackermes.db";

    public string Name => Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar));

    public string DatabasePath => Path.Combine(Directory, DatabaseFileName);

    /// <summary>侧边栏应隐藏此文件,避免用户误删正在使用的数据库。</summary>
    public static bool IsProtectedFile(string fileName) =>
        fileName.StartsWith(DatabaseFileName, StringComparison.OrdinalIgnoreCase);
}

public sealed class WorkspaceService : IWorkspaceService
{
    private readonly IEventBus _eventBus;
    private readonly IAppLogger _logger;

    public WorkspaceService(IEventBus eventBus, IAppLogger logger)
    {
        _eventBus = eventBus;
        _logger = logger.ForCategory(nameof(WorkspaceService));
    }

    public Workspace? Current { get; private set; }

    public bool HasWorkspace => Current is not null;

    public void Open(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("工作区目录不能为空", nameof(directory));

        var full = Path.GetFullPath(directory);

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"工作区目录不存在: {full}");

        if (Current is not null)
            Close();

        Current = new Workspace(full);
        _logger.Info($"打开工作区: {full}");

        _eventBus.Publish(new ProjectOpenedEvent(full, Current.DatabasePath));
    }

    public void Close()
    {
        if (Current is null)
            return;

        _logger.Info($"关闭工作区: {Current.Directory}");
        Current = null;
        _eventBus.Publish(new ProjectClosedEvent());
    }
}
