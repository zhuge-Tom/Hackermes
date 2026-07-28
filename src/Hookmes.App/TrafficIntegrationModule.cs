using Hookmes.Automation.Commands;
using Hookmes.Automation.Packet;
using Hookmes.AiPanel.Tools;
using Hookmes.Base;
using Hookmes.Inspector.ViewModels;
using Hookmes.Inspector.Views;
using Hookmes.Platform.Registries;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hookmes.App;

public sealed class TrafficIntegrationModule : IModule
{
    public string Name => "Traffic Integration";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<TrafficIntegrationService>();
        services.AddSingleton<IPacketCommandService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<ITrafficWorkbenchService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var integration = serviceProvider.GetRequiredService<TrafficIntegrationService>();
        PacketCommandRegistrar.Register(serviceProvider.GetRequiredService<CommandRegistry>(), integration);
        TrafficAiToolRegistrar.Register(serviceProvider.GetRequiredService<IAiToolRegistry>(), integration);

        serviceProvider.GetRequiredService<IDockLayoutRegistry>().RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "traffic-workbench",
            Title = "数据包",
            IconKey = "SemiIconSwap",
            IsClosable = false,
            Order = 1,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "traffic-workbench", Title = "数据包",
                Content = new TrafficWorkbenchView { DataContext = new TrafficWorkbenchViewModel(integration) }
            }
        });
    }
}
