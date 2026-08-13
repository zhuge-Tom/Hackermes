using Hackermes.AiPanel.Mcp;
using Hackermes.AiPanel.Agent;
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
        services.AddSingleton<PageContextToolAdapter>();
        services.AddSingleton<PageSecuritySnapshotToolAdapter>();
        services.AddSingleton<DefaultToolPolicyGate>();
        services.AddSingleton<IToolPolicyGate>(sp => sp.GetRequiredService<DefaultToolPolicyGate>());
        services.AddSingleton<IToolConfirmationService, AvaloniaToolConfirmationService>();
        services.AddSingleton<AiToolDispatcher>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IAgentSkillStore, AgentSkillStore>();
        services.AddSingleton<IAgentMemoryStore, AgentMemoryStore>();
        services.AddSingleton<AgentContextCompactor>();
        services.AddSingleton<IAgentArtifactStore, AgentArtifactStore>();
        services.AddSingleton<AgentWorkflowToolAdapter>();
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
        client.Endpoint = AiProviderPresets.ResolveChatEndpoint(aiSettings.Endpoint, aiSettings.ChatCompletionsPath);
        client.ApiKey = serviceProvider.GetRequiredService<ISecretStore>().Get("ai.apiKey");
        serviceProvider.GetRequiredService<DefaultToolPolicyGate>().SetMode(aiSettings.PermissionMode);

        AgentWorkflowCommandRegistrar.Register(
            serviceProvider.GetRequiredService<Hackermes.Automation.Commands.CommandRegistry>(),
            serviceProvider.GetRequiredService<ISettingsService>(),
            serviceProvider.GetRequiredService<DefaultToolPolicyGate>(),
            serviceProvider.GetRequiredService<IAgentSkillStore>(),
            serviceProvider.GetRequiredService<IAgentMemoryStore>(),
            serviceProvider.GetRequiredService<IAgentArtifactStore>());

        serviceProvider.GetRequiredService<CommandToolAdapter>()
            .RegisterAll(serviceProvider.GetRequiredService<IAiToolRegistry>());
        serviceProvider.GetRequiredService<InspectionToolAdapter>()
            .RegisterAll(serviceProvider.GetRequiredService<IAiToolRegistry>());
        serviceProvider.GetRequiredService<PageContextToolAdapter>()
            .RegisterAll(serviceProvider.GetRequiredService<IAiToolRegistry>());
        serviceProvider.GetRequiredService<PageSecuritySnapshotToolAdapter>()
            .RegisterAll(serviceProvider.GetRequiredService<IAiToolRegistry>());
        serviceProvider.GetRequiredService<AgentWorkflowToolAdapter>()
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
                    serviceProvider.GetRequiredService<DefaultToolPolicyGate>(),
                    serviceProvider.GetRequiredService<IAgentSkillStore>(),
                    serviceProvider.GetRequiredService<IAgentMemoryStore>())
                {
                    DataContext = new AiChatViewModel(
                        serviceProvider.GetRequiredService<IOpenAiChatClient>(),
                        serviceProvider.GetRequiredService<IAiToolRegistry>(),
                        serviceProvider.GetRequiredService<AiToolDispatcher>(),
                        serviceProvider.GetRequiredService<IEventBus>(),
                        serviceProvider.GetRequiredService<ISettingsService>(),
                        serviceProvider.GetRequiredService<IAgentSkillStore>(),
                        serviceProvider.GetRequiredService<IAgentMemoryStore>(),
                        serviceProvider.GetRequiredService<AgentContextCompactor>())
                    {
                        Model = aiSettings.Model
                    }
                }
            }
        });
    }

}
