using Hackermes.AiPanel.Mcp;
using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Tools;
using Hackermes.AiPanel.ViewModels;
using Hackermes.AiPanel.Views;
using Hackermes.Base;
using Hackermes.Base.Events;
using Hackermes.Platform.Registries;
using Hackermes.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;

namespace Hackermes.AiPanel;

public sealed class AiPanelModule : IModule
{
    public string Name => "AI Panel";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IAiToolRegistry, AiToolRegistry>();
        services.AddSingleton<CommandToolAdapter>();
        services.AddSingleton<InspectionToolAdapter>();
        services.AddSingleton<DefaultToolPolicyGate>();
        services.AddSingleton<IToolPolicyGate>(sp => sp.GetRequiredService<DefaultToolPolicyGate>());
        services.AddSingleton<IToolConfirmationService, AvaloniaToolConfirmationService>();
        services.AddSingleton<AiToolDispatcher>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<OpenAiCompatibleClient>();
        services.AddSingleton<IOpenAiChatClient>(sp => sp.GetRequiredService<OpenAiCompatibleClient>());
        services.AddSingleton<StdioMcpBridge>();
        services.AddSingleton<IMcpBridge>(sp => sp.GetRequiredService<StdioMcpBridge>());
        services.AddSingleton<McpToolAdapter>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        var aiSettings = serviceProvider.GetRequiredService<ISettingsService>().Load().Ai;
        var client = serviceProvider.GetRequiredService<OpenAiCompatibleClient>();
        client.Endpoint = AiProviderPresets.ResolveChatEndpoint(aiSettings.Endpoint);
        client.ApiKey = serviceProvider.GetRequiredService<ISecretStore>().Get("ai.apiKey");
        serviceProvider.GetRequiredService<DefaultToolPolicyGate>().SetTrustedMode(aiSettings.TrustedMode);

        serviceProvider.GetRequiredService<CommandToolAdapter>()
            .RegisterAll(serviceProvider.GetRequiredService<IAiToolRegistry>());
        serviceProvider.GetRequiredService<InspectionToolAdapter>()
            .RegisterAll(serviceProvider.GetRequiredService<IAiToolRegistry>());
        _ = serviceProvider.GetRequiredService<McpToolAdapter>().InitializeAsync(aiSettings);

        var dock = serviceProvider.GetRequiredService<IDockLayoutRegistry>();
        dock.RegisterTab(new DockTabRegistration
        {
            Region = DockPosition.Right,
            TabId = "ai-chat",
            Title = "AI 助手",
            IconKey = "SemiIconRobot",
            IsClosable = false,
            Order = 0,
            CreateTab = () => new DockTabItemViewModel
            {
                Id = "ai-chat",
                Title = "AI 助手",
                Content = new AiChatView(
                    serviceProvider.GetRequiredService<ISettingsService>(),
                    serviceProvider.GetRequiredService<ISecretStore>(),
                    client,
                    serviceProvider.GetRequiredService<DefaultToolPolicyGate>())
                {
                    DataContext = new AiChatViewModel(
                        serviceProvider.GetRequiredService<IOpenAiChatClient>(),
                        serviceProvider.GetRequiredService<IAiToolRegistry>(),
                        serviceProvider.GetRequiredService<AiToolDispatcher>(),
                        serviceProvider.GetRequiredService<IEventBus>(),
                        aiSettings.MaxToolRounds)
                    {
                        Model = aiSettings.Model
                    }
                }
            }
        });
    }

}
