using Hookmes.Automation.Commands;
using Hookmes.Automation.Execution;
using Hookmes.Automation.Recording;
using Hookmes.Automation.Timeline;
using Hookmes.Automation.Views;
using Hookmes.Base;
using Hookmes.Base.Diagnostics;
using Hookmes.Cdp.Session;
using Hookmes.Platform.Services;
using Hookmes.Platform.Registries;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace Hookmes.Automation;

/// <summary>
/// 自动化模块。只提供能力,不注册任何 UI ——
/// 终端 REPL 与(阶段 4 的)AI 工具都是它的前端。
/// </summary>
public sealed class AutomationModule : IModule
{
    public string Name => "Automation";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ActionExecutor>();
        services.AddSingleton<ActionTimelineStore>();
        services.AddSingleton<ActionPersistence>();
        services.AddSingleton<ActionRecorder>();
        services.AddSingleton<CommandRegistry>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var dock = serviceProvider.GetRequiredService<IDockLayoutRegistry>();
        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "timeline",
            Title = "时间线",
            IconKey = "SemiIconHistory",
            IsClosable = false,
            Order = 3,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "timeline",
                Title = "时间线",
                Content = new TimelineView(serviceProvider.GetRequiredService<ActionTimelineStore>())
            }
        });
        TryRunCommandSmokeTest(serviceProvider);
    }

    /// <summary>
    /// 诊断模式下跑一串命令,验证"解析 → 定位 → 真实输入事件"整条链路。
    /// <para>
    /// 包含负面用例:禁用元素应当失败而不是假装成功。自动化工具最怕的就是
    /// 动作静默失效,断言却通过。
    /// </para>
    /// </summary>
    private static void TryRunCommandSmokeTest(IServiceProvider serviceProvider)
    {
        if (Environment.GetEnvironmentVariable("HOOKMES_SELFTEST") != "1")
            return;

        var registry = serviceProvider.GetRequiredService<ICdpSessionRegistry>();
        var commands = serviceProvider.GetRequiredService<CommandRegistry>();
        var log = serviceProvider.GetRequiredService<IAppLogger>().ForCategory("SmokeTest");

        registry.SessionOpened += session =>
        {
            StartupPerformance.RunAfterDelay(() => _ = RunAsync(session.PageId), 5000);
        };

        async Task RunAsync(string pageId)
        {
            // 前 5 条应当成功,最后一条应当失败 —— 断言的是"失败被如实报告"。
            var script = new (string Command, bool ExpectSuccess)[]
            {
                ("dom h1", true),
                ("rec start", true),
                ("click #test-btn", true),
                ("eval document.getElementById('btn-result').textContent", true),
                ("type #test-input 你好 Hookmes", true),
                ("eval document.getElementById('input-echo').textContent", true),
                ("rec stop", true),
                ("replay", true),
                ("assert text #btn-result 点击成功", true),
                ("click #disabled-btn", false)
            };

            var passed = 0;

            foreach (var (command, expectSuccess) in script)
            {
                var result = await commands.ExecuteAsync(command, pageId).ConfigureAwait(false);
                var asExpected = result.Success == expectSuccess;

                if (asExpected)
                    passed++;

                // 多行输出压成一行,求值结果就在第二行 —— 那才是要断言的东西。
                var flat = result.Output.Replace("\r", string.Empty).Replace('\n', '│');
                log.Info($"{(asExpected ? "✓" : "✗")} 「{command}」→ {flat}");
            }

            log.Info($"命令冒烟测试: {passed}/{script.Length} 符合预期");
        }
    }
}
