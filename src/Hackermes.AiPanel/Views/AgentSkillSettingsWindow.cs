using Avalonia.Controls;
using Hackermes.AiPanel.Agent;

namespace Hackermes.AiPanel.Views;

/// <summary>Small first-party editor for persistent Agent workflow skills.</summary>
public sealed class AgentSkillSettingsWindow : Window
{
    public AgentSkillSettingsWindow(IAgentSkillStore store)
    {
        Title = "Skill 工作流"; Width = 620; Height = 600; MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new AgentSkillSettingsView(store) { Margin = new Avalonia.Thickness(18) };
    }
}
