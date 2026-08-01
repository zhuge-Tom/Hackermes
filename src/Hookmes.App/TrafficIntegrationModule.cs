using Hookmes.Automation.Commands;
using Hookmes.Automation.Packet;
using Hookmes.AiPanel.Tools;
using Hookmes.Base;
using Hookmes.Inspector.ViewModels;
using Hookmes.Inspector.Views;
using Hookmes.Platform.Registries;
using Hookmes.Traffic.Rules;
using Hookmes.Traffic.Repeater;
using Hookmes.Traffic.Comparison;
using Hookmes.Traffic.Annotations;
using Hookmes.Traffic.History;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;

namespace Hookmes.App;

public sealed class TrafficIntegrationModule : IModule
{
    public string Name => "Traffic Integration";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IPacketAuditTrail>(_ => new PacketAuditTrail(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hookmes", "traffic-audit.v1.json")));
        services.AddSingleton<TrafficRuleAuditBridge>();
        services.AddSingleton<TrafficIntegrationService>();
        services.AddSingleton<IPacketCommandService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketArchiveService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketBodyReadService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketBodyEditService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketAuditQueryService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketCommitService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<ITrafficWorkbenchService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<ITrafficRuleWorkbenchService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IRecentTrafficPathService, RecentTrafficPathService>();
        services.AddSingleton<IRepeaterWorkbenchService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<TrafficComparisonAdapter>();
        services.AddSingleton<ITrafficComparerWorkbenchService>(sp => sp.GetRequiredService<TrafficComparisonAdapter>());
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        _ = serviceProvider.GetRequiredService<TrafficRuleAuditBridge>();
        var integration = serviceProvider.GetRequiredService<TrafficIntegrationService>();
        PacketCommandRegistrar.Register(serviceProvider.GetRequiredService<CommandRegistry>(), integration);
        TrafficAiToolRegistrar.Register(serviceProvider.GetRequiredService<IAiToolRegistry>(), integration);
        TrafficRuleToolRegistrar.Register(
            serviceProvider.GetRequiredService<CommandRegistry>(),
            serviceProvider.GetRequiredService<IAiToolRegistry>(),
            serviceProvider.GetRequiredService<ITrafficRuleManager>());
        RepeaterToolRegistrar.Register(
            serviceProvider.GetRequiredService<CommandRegistry>(),
            serviceProvider.GetRequiredService<IAiToolRegistry>(),
            serviceProvider.GetRequiredService<IRepeaterService>());
        TrafficComparisonToolRegistrar.Register(
            serviceProvider.GetRequiredService<CommandRegistry>(),
            serviceProvider.GetRequiredService<IAiToolRegistry>(),
            serviceProvider.GetRequiredService<ITrafficComparisonService>());
        TrafficAnnotationToolRegistrar.Register(
            serviceProvider.GetRequiredService<CommandRegistry>(),
            serviceProvider.GetRequiredService<IAiToolRegistry>(),
            serviceProvider.GetRequiredService<ITrafficAnnotationService>());
        TrafficHistoryToolRegistrar.Register(
            serviceProvider.GetRequiredService<CommandRegistry>(),
            serviceProvider.GetRequiredService<IAiToolRegistry>(),
            serviceProvider.GetRequiredService<ITrafficHistoryManagementService>());

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
                Content = new TrafficWorkbenchView
                {
                    DataContext = new TrafficWorkbenchViewModel(integration,
                        serviceProvider.GetRequiredService<IRecentTrafficPathService>())
                }
            }
        });

        serviceProvider.GetRequiredService<IDockLayoutRegistry>().RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "traffic-comparer",
            Title = "Comparer",
            IconKey = "SemiIconDiff",
            IsClosable = false,
            Order = 4,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "traffic-comparer", Title = "Comparer",
                Content = new TrafficComparerView
                {
                    DataContext = new TrafficComparerViewModel(serviceProvider.GetRequiredService<ITrafficComparerWorkbenchService>())
                }
            }
        });

        serviceProvider.GetRequiredService<IDockLayoutRegistry>().RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "traffic-repeater",
            Title = "Repeater",
            IconKey = "SemiIconSend",
            IsClosable = false,
            Order = 3,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "traffic-repeater", Title = "Repeater",
                Content = new RepeaterWorkbenchView { DataContext = new RepeaterWorkbenchViewModel(integration) }
            }
        });

        serviceProvider.GetRequiredService<IDockLayoutRegistry>().RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Bottom,
            TabId = "traffic-rules",
            Title = "流量规则",
            IconKey = "SemiIconFilter",
            IsClosable = false,
            Order = 2,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "traffic-rules", Title = "流量规则",
                Content = new TrafficRulesView
                {
                    DataContext = new TrafficRulesViewModel(integration,
                        serviceProvider.GetRequiredService<IRecentTrafficPathService>())
                }
            }
        });

        TrafficSelfTestRunner.TryStart(serviceProvider);
    }
}
