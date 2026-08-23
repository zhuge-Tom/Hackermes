using Hackermes.Automation.Commands;
using Hackermes.Automation.Packet;
using Hackermes.AiPanel.Tools;
using Hackermes.Assessment;
using Hackermes.Base;
using Hackermes.Base.Events;
using Hackermes.Inspector.ViewModels;
using Hackermes.Inspector.Views;
using Hackermes.Platform.Events;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using Hackermes.Traffic.Rules;
using Hackermes.Traffic.Repeater;
using Hackermes.Traffic.Comparison;
using Hackermes.Traffic.Annotations;
using Hackermes.Traffic.History;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;

namespace Hackermes.App;

public sealed class TrafficIntegrationModule : IModule
{
    public string Name => "Traffic Integration";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(_ => new OperatorIdentityDirectory(AppDataPaths.Resolve("operator-identities.v1.json")));
        services.AddSingleton<IPacketAuditTrail>(sp => new PacketAuditTrail(
            AppDataPaths.Resolve("traffic-audit.v1.json"),
            () => sp.GetRequiredService<OperatorIdentityDirectory>().ResolveActiveName()
                ?? sp.GetRequiredService<ISettingsService>().Load().Traffic.OperatorName
                ?? Environment.UserName));
        services.AddSingleton<PacketAuditSigningKey>();
        services.AddSingleton<IPacketAuditSigningKey>(sp => sp.GetRequiredService<PacketAuditSigningKey>());
        services.AddSingleton(_ => new AuditKeyTrustFile(AppDataPaths.Resolve("audit-signing-trust.v1.json")));
        services.AddSingleton<PacketAuditTrustPolicy>();
        services.AddSingleton<IPacketAuditTrustPolicy>(sp => sp.GetRequiredService<PacketAuditTrustPolicy>());
        services.AddSingleton<IAssessmentReportTrustPolicy>(sp => sp.GetRequiredService<PacketAuditTrustPolicy>());
        services.AddSingleton<IPacketAuditExportService>(sp => new PacketAuditExportService(
            sp.GetRequiredService<IPacketAuditTrail>(),
            sp.GetRequiredService<IPacketAuditSigningKey>(),
            sp.GetRequiredService<IPacketAuditTrustPolicy>()));
        services.AddSingleton<TrafficRuleAuditBridge>();
        services.AddSingleton<TrafficIntegrationService>();
        services.AddSingleton<IPacketCommandService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketArchiveService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketBodyReadService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketBodyEditService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
        services.AddSingleton<IPacketInterceptionModeService>(sp => sp.GetRequiredService<TrafficIntegrationService>());
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
        SigningKeysCommandRegistrar.Register(serviceProvider.GetRequiredService<CommandRegistry>(),
            serviceProvider.GetRequiredService<PacketAuditSigningKey>(),
            serviceProvider.GetRequiredService<AuditKeyTrustFile>());
        IdentityCommandRegistrar.Register(serviceProvider.GetRequiredService<CommandRegistry>(),
            serviceProvider.GetRequiredService<OperatorIdentityDirectory>());
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
        RegisterWorkspacePolicyIsolation(serviceProvider, integration);

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

    /// <summary>
    /// Routes the traffic history policy file to the active workspace
    /// (<workspace>/.hackermes/traffic-history-policy.json) and falls back to the global
    /// file when no workspace is open. Every switch immediately applies the new policy
    /// and notifies workbench views so their statistics stay current without a manual
    /// refresh (notification fires after the switch, so observers see the final state).
    /// </summary>
    private static void RegisterWorkspacePolicyIsolation(IServiceProvider serviceProvider, TrafficIntegrationService integration)
    {
        var eventBus = serviceProvider.GetRequiredService<IEventBus>();
        var policies = serviceProvider.GetRequiredService<ITrafficHistoryPolicyStore>();
        var history = serviceProvider.GetRequiredService<ITrafficHistoryManagementService>();
        eventBus.SubscribeDisposable<ProjectOpenedEvent>(e =>
        {
            policies.SwitchStorage(Path.Combine(e.Directory, ".hackermes", "traffic-history-policy.json"), "workspace");
            history.Cleanup();
            integration.NotifyHistoryPolicyChanged();
        });
        eventBus.SubscribeDisposable<ProjectClosedEvent>(_ =>
        {
            policies.SwitchStorage(AppDataPaths.Resolve("traffic-history-policy.json"),
                TrafficHistoryPolicyStore.GlobalSource);
            history.Cleanup();
            integration.NotifyHistoryPolicyChanged();
        });
    }
}
