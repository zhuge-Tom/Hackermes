using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hackermes.Base;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hackermes.App;

/// <summary>Registers the application shell and its welcome content.</summary>
public sealed class CoreModule : IModule
{
    public string Name => "Core";

    public void RegisterServices(IServiceCollection services) => services.AddHackermesCoreServices();

    public void Initialize(IServiceProvider serviceProvider)
    {
        var dock = serviceProvider.GetRequiredService<IDockLayoutRegistry>();
        var eventBus = serviceProvider.GetRequiredService<IEventBus>();
        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Content,
            TabId = "welcome",
            Title = "Welcome",
            IconKey = "SemiIconHome",
            IsClosable = true,
            Order = 0,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "welcome",
                Title = "Welcome",
                Content = BuildWelcomeView(eventBus)
            }
        });
    }

    private static Control BuildWelcomeView(IEventBus eventBus)
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
            Text = "Hackermes",
            FontSize = 32,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Web debugging and automation workbench",
            FontSize = 14,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Open a browser page to inspect traffic, console output, DOM, storage, and resources.",
            FontSize = 12,
            Opacity = 0.5,
            TextAlignment = TextAlignment.Center,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var openBrowser = new Button
        {
            Content = "打开浏览器标签页",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Avalonia.Thickness(18, 8),
            Margin = new Avalonia.Thickness(0, 10, 0, 0)
        };
        openBrowser.Click += (_, _) => eventBus.Publish(new OpenBrowserTabRequestedEvent());
        panel.Children.Add(openBrowser);
        return panel;
    }
}
