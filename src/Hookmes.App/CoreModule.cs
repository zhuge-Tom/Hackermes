using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hookmes.Base;
using Hookmes.Platform.Registries;
using Hookmes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hookmes.App;

/// <summary>
/// 核心模块:注册平台层服务,并占位登记四个区域的默认 Tab。
/// <para>
/// 这些占位内容会随各功能模块接入被逐一替换 ——
/// 阶段 0 保留它们是为了让五区域布局、懒物化与布局持久化都能被真实验证到。
/// </para>
/// </summary>
public sealed class CoreModule : IModule
{
    public string Name => "Core";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddHookmesCoreServices();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var dock = serviceProvider.GetRequiredService<IDockLayoutRegistry>();

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = "welcome",
            Title = "欢迎",
            IconKey = "SemiIconHome",
            IsClosable = true,
            Order = 0,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "welcome",
                Title = "欢迎",
                Content = BuildWelcomeView()
            }
        });

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Left,
            TabId = "explorer",
            Title = "资源",
            IconKey = "SemiIconFolder",
            IsClosable = false,
            Order = 0,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "explorer",
                Title = "资源",
                Content = BuildPlaceholder("资源树", "阶段 2 由 Sidebar 模块接管")
            }
        });

        // 底部区已由 Inspector 模块的网络/控制台面板接管,这里不再放占位页。

        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Right,
            TabId = "ai",
            Title = "AI 助手",
            IconKey = "SemiIconComment",
            IsClosable = false,
            Order = 0,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "ai",
                Title = "AI 助手",
                Content = BuildPlaceholder("AI 助手", "阶段 4 由 AiPanel 模块接管")
            }
        });
    }

    private static Control BuildWelcomeView()
    {
        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Hookmes",
            FontSize = 32,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = "网页调试自动化工作台",
            FontSize = 14,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = "阶段 0 · 骨架已就位\n五区域布局 · Tab 懒物化 · 叠层保活 · 布局持久化",
            FontSize = 12,
            Opacity = 0.5,
            TextAlignment = TextAlignment.Center,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        return panel;
    }

    private static Control BuildPlaceholder(string title, string hint)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            Margin = new Avalonia.Thickness(16),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 11,
            Opacity = 0.45,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        return panel;
    }
}
