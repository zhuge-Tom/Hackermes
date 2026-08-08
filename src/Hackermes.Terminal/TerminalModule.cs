using Hackermes.Automation.Commands;
using Hackermes.Base;
using Hackermes.Base.Events;
using Hackermes.Platform.Registries;
using Hackermes.Terminal.ViewModels;
using Hackermes.Terminal.Views;
using Hackermes.Terminal.Services;
using Hackermes.Dock.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hackermes.Terminal;

/// <summary>
/// 终端模块。领域命令 REPL 与真实系统 shell(PTY)以双会话形式共处底部面板。
/// </summary>
public sealed class TerminalModule : IModule
{
    public string Name => "Terminal";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ShellCommandService>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var dock = serviceProvider.GetRequiredService<IDockLayoutRegistry>();
        var commands = serviceProvider.GetRequiredService<CommandRegistry>();
        var eventBus = serviceProvider.GetRequiredService<IEventBus>();
        var shellService = serviceProvider.GetRequiredService<ShellCommandService>();

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "system-shell",
            Title = "System Shell",
            IconKey = "SemiIconTerminal",
            IsClosable = false,
            Order = 1,
            CreateTab = () =>
            {
                var view = new SystemShellView(shellService);
                return new DockTabItemViewModel
                {
                    Id = "system-shell",
                    Title = view.ShellName,
                    Content = TabContent.NonReloadable(view)
                };
            }
        });

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "console-repl",
            Title = "控制台命令",
            IconKey = "SemiIconTerminal",
            IsClosable = false,
            Order = 2,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "console-repl",
                Title = "控制台命令",
                Content = new ConsoleReplView { DataContext = new ConsoleReplViewModel(commands, eventBus, shellService) }
            }
        });
    }
}
