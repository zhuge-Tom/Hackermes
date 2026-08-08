using Hackermes.Automation.Execution;
using Hackermes.Automation.Model;
using Hackermes.Automation.Recording;
using Hackermes.Automation.Timeline;
using Hackermes.Base.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Automation.Commands;

/// <summary>
/// 领域命令注册表与调度器。
/// <para>
/// 内置命令在这里定义一次,终端 REPL 与 AI 工具共享同一份实现。
/// </para>
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ActionExecutor _executor;
    private readonly IAppLogger _logger;
    private readonly ActionRecorder _recorder;
    private readonly ActionTimelineStore _timeline;
    private readonly ActionPersistence _persistence;

    public CommandRegistry(ActionExecutor executor, ActionRecorder recorder, IAppLogger logger,
        ActionTimelineStore timeline, ActionPersistence persistence)
    {
        _executor = executor;
        _recorder = recorder;
        _timeline = timeline;
        _persistence = persistence;
        _logger = logger.ForCategory(nameof(CommandRegistry));

        RegisterBuiltins();
    }

    public IReadOnlyCollection<CommandDefinition> All =>
        _commands.Values.DistinctBy(c => c.Name).OrderBy(c => c.Name).ToArray();

    public void Register(CommandDefinition definition)
    {
        _commands[definition.Name] = definition;

        foreach (var alias in definition.Aliases)
            _commands[alias] = definition;
    }

    public CommandDefinition? Find(string name) =>
        _commands.TryGetValue(name, out var definition) ? definition : null;

    /// <summary>解析并执行一行命令。</summary>
    public async Task<CommandResult> ExecuteAsync(string input, string? pageId, CancellationToken ct = default)
    {
        var tokens = CommandLineParser.Tokenize(input);

        if (tokens.Count == 0)
            return CommandResult.Ok();

        var definition = Find(tokens[0]);

        if (definition is null)
            return CommandResult.Fail($"未知命令: {tokens[0]}(输入 help 查看可用命令)");

        var context = new CommandContext
        {
            Args = tokens.Skip(1).ToArray(),
            PageId = pageId,
            RawInput = input,
            RawArguments = ExtractRawArguments(input, tokens[0])
        };

        try
        {
            return await definition.Handler(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            _logger.Error($"命令 {definition.Name} 执行失败", ex);
            return CommandResult.Fail(ex.Message);
        }
    }

    #region 内置命令

    private void RegisterBuiltins()
    {
        Register(new CommandDefinition
        {
            Name = "open",
            Aliases = ["go", "goto"],
            Summary = "导航到指定地址",
            Usage = "open <url>",
            IsMutating = true,
            Handler = (ctx, ct) => RunActionAsync(ctx, ct,
                ActionDescriptor.Navigate(NormalizeUrl(ctx.Rest(0)), ActionOrigin.Repl),
                requireArgs: 1, missing: "缺少地址")
        });

        Register(new CommandDefinition
        {
            Name = "click",
            Summary = "点击元素(真实鼠标事件)",
            Usage = "click <选择器>",
            IsMutating = true,
            Handler = (ctx, ct) => RunActionAsync(ctx, ct,
                ActionDescriptor.Click(TargetSelector.Css(ctx.Rest(0)), ActionOrigin.Repl),
                requireArgs: 1, missing: "缺少选择器")
        });

        Register(new CommandDefinition
        {
            Name = "type",
            Summary = "向输入框键入文本",
            Usage = "type <选择器> <文本>",
            IsMutating = true,
            Handler = async (ctx, ct) =>
            {
                if (ctx.Args.Count < 2)
                    return CommandResult.Fail("用法: type <选择器> <文本>");

                var action = ActionDescriptor.Type(
                    TargetSelector.Css(ctx.Args[0]), ctx.Rest(1), clearFirst: true, ActionOrigin.Repl);

                return await ExecuteAndFormatAsync(ctx, action, ct).ConfigureAwait(false);
            }
        });

        Register(new CommandDefinition
        {
            Name = "hover",
            Summary = "悬停到元素",
            Usage = "hover <选择器>",
            IsMutating = true,
            Handler = (ctx, ct) => RunActionAsync(ctx, ct, new ActionDescriptor
            {
                Kind = ActionKind.Hover,
                Target = TargetSelector.Css(ctx.Rest(0)),
                Origin = ActionOrigin.Repl
            }, requireArgs: 1, missing: "缺少选择器")
        });

        Register(new CommandDefinition
        {
            Name = "press",
            Summary = "发送按键",
            Usage = "press <键名>   例如 press Enter",
            IsMutating = true,
            Handler = (ctx, ct) => RunActionAsync(ctx, ct, new ActionDescriptor
            {
                Kind = ActionKind.Press,
                Origin = ActionOrigin.Repl,
                Args = new Dictionary<string, string?>(StringComparer.Ordinal) { ["key"] = ctx.Arg(0) }
            }, requireArgs: 1, missing: "缺少键名")
        });

        Register(new CommandDefinition
        {
            Name = "eval",
            Summary = "在页面中执行 JavaScript 并返回结果",
            Usage = "eval <表达式>",
            IsMutating = true,
            // 用 RawArguments 而非分词结果:JS 里的引号必须原样保留。
            Handler = (ctx, ct) => RunActionAsync(ctx, ct,
                ActionDescriptor.Evaluate(ctx.RawArguments, ActionOrigin.Repl),
                requireArgs: 1, missing: "缺少表达式")
        });

        Register(new CommandDefinition
        {
            Name = "wait",
            Summary = "等待元素出现",
            Usage = "wait <选择器>",
            Handler = (ctx, ct) => RunActionAsync(ctx, ct,
                ActionDescriptor.WaitFor("selector", ctx.Rest(0), ActionOrigin.Repl),
                requireArgs: 1, missing: "缺少选择器")
        });

        Register(new CommandDefinition
        {
            Name = "dom",
            Summary = "查询元素并报告其状态(可见性、可交互性、文本)",
            Usage = "dom <选择器>",
            Handler = async (ctx, ct) =>
            {
                if (ctx.Args.Count == 0)
                    return CommandResult.Fail("用法: dom <选择器>");

                if (ctx.PageId is null)
                    return CommandResult.Fail("没有活动的页面");

                // 属性选择器里常带引号,同样要用原始文本。
                var selector = ctx.RawArguments;
                var expression = $$"""
                    (function (sel) {
                      var list = document.querySelectorAll(sel);
                      if (list.length === 0) return '未匹配到元素';
                      var out = [];
                      for (var i = 0; i < Math.min(list.length, 10); i++) {
                        var el = list[i];
                        var r = el.getBoundingClientRect();
                        var s = getComputedStyle(el);
                        var vis = r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
                        out.push('[' + i + '] <' + el.tagName.toLowerCase() + '>'
                          + ' ' + Math.round(r.width) + 'x' + Math.round(r.height)
                          + (vis ? ' 可见' : ' 不可见')
                          + (el.disabled ? ' 已禁用' : '')
                          + ' "' + (el.innerText || el.value || '').trim().slice(0, 50) + '"');
                      }
                      if (list.length > 10) out.push('… 共 ' + list.length + ' 个');
                      return out.join('\n');
                    })({{System.Text.Json.JsonSerializer.Serialize(selector)}})
                    """;

                var result = await _executor.ExecuteAsync(
                    ctx.PageId,
                    ActionDescriptor.Evaluate(expression, ActionOrigin.Repl),
                    ct).ConfigureAwait(false);

                return result.Success
                    ? CommandResult.Ok(result.Value ?? "(无结果)")
                    : CommandResult.Fail(result.Error ?? "查询失败");
            }
        });

        Register(new CommandDefinition
        {
            Name = "snap",
            Summary = "截取当前页面并返回 PNG 的 base64 长度",
            Usage = "snap",
            Handler = async (ctx, ct) =>
            {
                if (ctx.PageId is null)
                    return CommandResult.Fail("没有活动的页面");

                var result = await _executor.ExecuteAsync(
                    ctx.PageId,
                    new ActionDescriptor { Kind = ActionKind.Screenshot, Origin = ActionOrigin.Repl },
                    ct).ConfigureAwait(false);

                return result.Success
                    ? CommandResult.Ok($"截图成功({result.Value?.Length ?? 0} 字节 base64)")
                    : CommandResult.Fail(result.Error ?? "截图失败");
            }
        });

        Register(new CommandDefinition
        {
            Name = "assert",
            Summary = "Assert page state (exists/gone/text/expression)",
            Usage = "assert exists|gone <selector> | assert text <selector> <text> | assert expression <js>",
            Handler = async (ctx, ct) =>
            {
                if (ctx.Args.Count < 2) return CommandResult.Fail("Usage: assert exists|gone <selector> | assert text <selector> <text> | assert expression <js>");
                var kind = ctx.Args[0].ToLowerInvariant();
                ActionDescriptor action;
                if (kind is "exists" or "gone")
                {
                    action = new ActionDescriptor { Kind = ActionKind.Assert, Origin = ActionOrigin.Repl,
                        Target = TargetSelector.Css(ctx.Args[1]),
                        Args = new Dictionary<string, string?> { ["assertion"] = kind } };
                }
                else if (kind == "text" && ctx.Args.Count >= 3)
                {
                    action = new ActionDescriptor { Kind = ActionKind.Assert, Origin = ActionOrigin.Repl,
                        Target = TargetSelector.Css(ctx.Args[1]),
                        Args = new Dictionary<string, string?> { ["assertion"] = kind, ["value"] = ctx.Rest(2) } };
                }
                else if (kind == "expression")
                {
                    var expression = ctx.RawArguments.Length > kind.Length
                        ? ctx.RawArguments[(kind.Length + 1)..] : string.Empty;
                    action = new ActionDescriptor { Kind = ActionKind.Assert, Origin = ActionOrigin.Repl,
                        Args = new Dictionary<string, string?> { ["assertion"] = kind, ["value"] = expression } };
                }
                else return CommandResult.Fail($"Unsupported assertion: {kind}");
                return await ExecuteAndFormatAsync(ctx, action, ct).ConfigureAwait(false);
            }
        });

        Register(new CommandDefinition
        {
            Name = "rec",
            Summary = "控制人工操作录制",
            Usage = "rec start|stop|status|clear",
            Handler = (ctx, _) => Task.FromResult(HandleRecording(ctx))
        });

        Register(new CommandDefinition
        {
            Name = "timeline",
            Summary = "Show, clear, save or load the unified action timeline",
            Usage = "timeline [count|failed|clear|save <file>|load <file>]",
            Handler = HandleTimelineAsync
        });

        Register(new CommandDefinition
        {
            Name = "save",
            Summary = "Save the current replay script as JSON",
            Usage = "save <file>",
            Handler = async (ctx, ct) =>
            {
                if (ctx.Args.Count == 0) return CommandResult.Fail("Usage: save <file>");
                var actions = _recorder.Snapshot();
                await _persistence.SaveScriptAsync(ctx.Rest(0), actions, ct).ConfigureAwait(false);
                return CommandResult.Ok($"Saved {actions.Count} actions to {System.IO.Path.GetFullPath(ctx.Rest(0))}");
            }
        });

        Register(new CommandDefinition
        {
            Name = "load",
            Summary = "Load a JSON replay script",
            Usage = "load <file>",
            Handler = async (ctx, ct) =>
            {
                if (ctx.Args.Count == 0) return CommandResult.Fail("Usage: load <file>");
                var actions = await _persistence.LoadScriptAsync(ctx.Rest(0), ct).ConfigureAwait(false);
                _recorder.Replace(actions);
                return CommandResult.Ok($"Loaded {actions.Count} actions from {System.IO.Path.GetFullPath(ctx.Rest(0))}");
            }
        });

        Register(new CommandDefinition
        {
            Name = "replay",
            Summary = "回放最近录制的动作",
            Usage = "replay",
            IsMutating = true,
            Handler = async (ctx, ct) =>
            {
                if (ctx.PageId is null) return CommandResult.Fail("没有活动的页面");
                if (_recorder.IsRecording) return CommandResult.Fail("请先用 rec stop 停止录制,再回放");
                if (_recorder.Count == 0) return CommandResult.Fail("没有可回放的动作");
                var result = await _recorder.ReplayAsync(ctx.PageId, ct).ConfigureAwait(false);
                return result.Failure is null
                    ? CommandResult.Ok($"回放完成: {result.Completed}/{result.Completed}")
                    : CommandResult.Fail($"第 {result.Completed + 1} 步失败: {result.Failure.Error}");
            }
        });

        Register(new CommandDefinition
        {
            Name = "help",
            Aliases = ["?"],
            Summary = "列出所有命令",
            Usage = "help [命令名]",
            Handler = (ctx, _) => Task.FromResult(CommandResult.Ok(BuildHelp(ctx.Arg(0))))
        });
    }

    private async Task<CommandResult> HandleTimelineAsync(CommandContext ctx, CancellationToken ct)
    {
        var operation = ctx.Arg(0)?.ToLowerInvariant();
        if (operation == "clear")
        {
            _timeline.Clear();
            return CommandResult.Ok("Timeline cleared.");
        }
        if (operation == "save")
        {
            if (ctx.Args.Count < 2) return CommandResult.Fail("Usage: timeline save <file>");
            var entries = _timeline.Snapshot();
            await _persistence.SaveTimelineAsync(ctx.Rest(1), entries, ct).ConfigureAwait(false);
            return CommandResult.Ok($"Saved {entries.Count} timeline entries.");
        }
        if (operation == "load")
        {
            if (ctx.Args.Count < 2) return CommandResult.Fail("Usage: timeline load <file>");
            var entries = await _persistence.LoadTimelineAsync(ctx.Rest(1), ct).ConfigureAwait(false);
            _timeline.Replace(entries);
            return CommandResult.Ok($"Loaded {entries.Count} timeline entries.");
        }

        var failedOnly = operation == "failed";
        var count = int.TryParse(operation, out var parsed) && parsed > 0 ? parsed : 20;
        var snapshot = _timeline.Snapshot(count, failuresOnly: failedOnly);
        if (snapshot.Count == 0) return CommandResult.Ok("Timeline is empty.");
        var lines = snapshot.Select(x =>
            $"#{x.Sequence} {x.Timestamp:HH:mm:ss.fff} [{x.Action.Origin}] {(x.Result.Success ? "OK" : "FAIL")} " +
            $"{x.Action.Describe()}{(x.Result.Success ? string.Empty : " — " + x.Result.Error)}");
        return CommandResult.Ok(string.Join(Environment.NewLine, lines));
    }

    private CommandResult HandleRecording(CommandContext ctx)
    {
        return ctx.Arg(0)?.ToLowerInvariant() switch
        {
            "start" when ctx.PageId is null => CommandResult.Fail("没有活动的页面"),
            "start" => StartRecording(ctx.PageId!),
            "stop" => CommandResult.Ok($"录制已停止,共 {_recorder.Stop().Count} 个动作"),
            "status" => CommandResult.Ok(_recorder.IsRecording ? $"正在录制,已捕获 {_recorder.Count} 个动作" : $"未录制,保留 {_recorder.Count} 个动作"),
            "clear" => ClearRecording(),
            _ => CommandResult.Fail("用法: rec start|stop|status|clear")
        };
    }

    private CommandResult StartRecording(string pageId)
    {
        _recorder.Start(pageId);
        return CommandResult.Ok("录制已开始；请在页面中操作,完成后输入 rec stop");
    }

    private CommandResult ClearRecording()
    {
        _recorder.Clear();
        return CommandResult.Ok("录制已清空");
    }

    private string BuildHelp(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var one = Find(name);
            return one is null
                ? $"没有名为 {name} 的命令"
                : $"{one.Name} — {one.Summary}\n用法: {one.Usage}";
        }

        var sb = new StringBuilder("可用命令:\n");

        foreach (var command in All)
            sb.Append("  ").Append(command.Usage.PadRight(30)).Append(command.Summary).Append('\n');

        sb.Append("\n以 ! 开头的输入将作为系统命令执行。");
        return sb.ToString();
    }

    private async Task<CommandResult> RunActionAsync(
        CommandContext ctx,
        CancellationToken ct,
        ActionDescriptor action,
        int requireArgs,
        string missing)
    {
        if (ctx.Args.Count < requireArgs)
            return CommandResult.Fail(missing);

        return await ExecuteAndFormatAsync(ctx, action, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> ExecuteAndFormatAsync(CommandContext ctx, ActionDescriptor action, CancellationToken ct)
    {
        if (ctx.PageId is null)
            return CommandResult.Fail("没有活动的页面,请先新建标签页");

        var result = await _executor.ExecuteAsync(ctx.PageId, action, ct).ConfigureAwait(false);

        if (!result.Success)
            return CommandResult.Fail(result.Error ?? "执行失败");

        var note = new StringBuilder(action.Describe());
        note.Append("  ✓ ").Append($"{result.Elapsed.TotalMilliseconds:F0} ms");

        // 命中靠后的候选说明页面结构变了,值得提示。
        if (result.MatchedIndex > 0)
            note.Append($"(用了第 {result.MatchedIndex + 1} 个候选选择器)");

        if (!string.IsNullOrEmpty(result.Value))
            note.Append('\n').Append(result.Value);

        return CommandResult.Ok(note.ToString());
    }

    /// <summary>取命令名之后的原始文本。按首个 token 在原串中的位置切,保留其后的一切字符。</summary>
    private static string ExtractRawArguments(string input, string commandToken)
    {
        var index = input.IndexOf(commandToken, StringComparison.OrdinalIgnoreCase);

        return index < 0
            ? string.Empty
            : input[(index + commandToken.Length)..].Trim();
    }

    private static string NormalizeUrl(string input)
    {
        var text = input.Trim();

        if (text.Length == 0)
            return text;

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return "https://" + text;
    }

    #endregion
}
