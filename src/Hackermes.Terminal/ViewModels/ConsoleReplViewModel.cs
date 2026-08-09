using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Automation.Commands;
using Hackermes.Base.Events;
using Hackermes.Base.Mvvm;
using Hackermes.Platform.Events;
using Hackermes.Terminal.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Threading;

namespace Hackermes.Terminal.ViewModels;

public sealed record ReplLine(string Text, ReplLineKind Kind);

public enum ReplLineKind
{
    Input,
    Output,
    Error,
    Hint
}

/// <summary>
/// 领域命令 REPL。
/// <para>
/// 这里敲的命令和 AI 调用的工具走同一套 <see cref="CommandRegistry"/> 定义,
/// 最终落到同一个动作执行器上 —— 人和机器操作页面的路径完全一致。
/// </para>
/// </summary>
public partial class ConsoleReplViewModel : ViewModelBase
{
    private const int MaxLines = 1000;

    private readonly CommandRegistry _commands;
    private readonly ShellCommandService _shellCommands;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _history = [];
    private int _historyCursor = -1;

    public ConsoleReplViewModel(CommandRegistry commands, IEventBus eventBus, ShellCommandService shellCommands)
    {
        _commands = commands;
        _shellCommands = shellCommands;

        // 当前作用的页面跟随 Content 区的活动标签。
        SubscribeEvent<ActiveContentTabChangedEvent>(eventBus, e =>
        {
            ActivePageId = e.TabId is { } id && id.StartsWith("page-", StringComparison.Ordinal) ? id : null;
            UpdatePrompt(e.Title);
        });

        // 标签切过来时页面往往还没加载完,标题稍后才有 —— 补一次。
        SubscribeEvent<UpdateDockTabTitleEvent>(eventBus, e =>
        {
            if (string.Equals(e.TabId, ActivePageId, StringComparison.Ordinal))
                UpdatePrompt(e.Title);
        });

        Lines.Add(new ReplLine("Hackermes 控制台命令：输入 help；安全工具先输入 assessment tools。不要在“系统 Shell”或 PowerShell 中输入 assessment。", ReplLineKind.Hint));
    }

    public ObservableCollection<ReplLine> Lines { get; } = [];

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private string _prompt = "(无活动页面)";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>命令作用的页面。为空时只有 help 之类的命令可用。</summary>
    public string? ActivePageId { get; private set; }

    private void UpdatePrompt(string? title) =>
        Prompt = ActivePageId is null
            ? "(无活动页面)"
            : string.IsNullOrEmpty(title) ? ActivePageId : title;

    [RelayCommand]
    private async Task SubmitAsync()
    {
        var line = Input?.Trim();

        if (string.IsNullOrEmpty(line) || IsBusy)
            return;

        Input = string.Empty;
        _history.Add(line);
        _historyCursor = _history.Count;

        Append(new ReplLine("› " + line, ReplLineKind.Input));

        if (line is "clear" or "cls")
        {
            Lines.Clear();
            return;
        }

        IsBusy = true;

        try
        {
            if (line.StartsWith('!'))
            {
                await ExecuteShellAsync(line[1..].Trim()).ConfigureAwait(true);
                return;
            }

            var result = await _commands.ExecuteAsync(line, ActivePageId).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.Output))
            {
                foreach (var part in result.Output.Split('\n'))
                    Append(new ReplLine(part, result.Success ? ReplLineKind.Output : ReplLineKind.Error));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteShellAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            Append(new ReplLine("Usage: !<system command>", ReplLineKind.Hint));
            return;
        }

        try
        {
            var result = await _shellCommands.ExecuteAsync(command, _lifetime.Token).ConfigureAwait(true);

            foreach (var part in result.StandardOutput.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Append(new ReplLine(part.TrimEnd('\r'), ReplLineKind.Output));

            foreach (var part in result.StandardError.TrimEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Append(new ReplLine(part.TrimEnd('\r'), ReplLineKind.Error));

            if (result.ExitCode != 0)
                Append(new ReplLine($"Process exited with code {result.ExitCode}.", ReplLineKind.Error));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Append(new ReplLine($"Shell command failed: {ex.Message}", ReplLineKind.Error));
        }
    }

    /// <summary>上下键翻历史。</summary>
    public void HistoryPrevious()
    {
        if (_history.Count == 0)
            return;

        _historyCursor = Math.Max(0, _historyCursor - 1);
        Input = _history[_historyCursor];
    }

    public void HistoryNext()
    {
        if (_history.Count == 0)
            return;

        _historyCursor++;

        if (_historyCursor >= _history.Count)
        {
            _historyCursor = _history.Count;
            Input = string.Empty;
            return;
        }

        Input = _history[_historyCursor];
    }

    private void Append(ReplLine line)
    {
        Lines.Add(line);

        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
    }

    protected override void OnDispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
