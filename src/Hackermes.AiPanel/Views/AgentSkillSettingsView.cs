using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hackermes.AiPanel.Agent;
using System;
using System.Linq;

namespace Hackermes.AiPanel.Views;

/// <summary>Inline editor shared by the AI settings page and the standalone dialog.</summary>
public sealed class AgentSkillSettingsView : UserControl
{
    private readonly IAgentSkillStore _store;
    private readonly ComboBox _skills = new();
    private readonly TextBox _id = new() { IsReadOnly = true };
    private readonly TextBox _name = new() { PlaceholderText = "例如：Web 登录检查" };
    private readonly TextBox _instructions = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 130,
        PlaceholderText = "描述触发条件、执行步骤、结果格式和停止条件。"
    };
    private readonly TextBox _tools = new() { PlaceholderText = "例如：page_query, packet_query（留空表示不额外收窄）" };
    private readonly CheckBox _enabled = new() { Content = "启用此 Skill", IsChecked = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public AgentSkillSettingsView(IAgentSkillStore store)
    {
        _store = store;
        _skills.SelectionChanged += (_, _) => LoadSelected();
        Content = Build();
        Refresh();
    }

    private Control Build()
    {
        var intro = new Border
        {
            Padding = new Avalonia.Thickness(12),
            Background = new SolidColorBrush(Color.FromArgb(22, 35, 110, 230)),
            CornerRadius = new Avalonia.CornerRadius(6),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "AI 自动搭建", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "已启用。Agent 在任务中可按需要创建或更新 Skill；人工也可在下方新建和维护。Skill 不能绕过当前权限模式与审批。",
                        Opacity = .72,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        var form = new StackPanel { Spacing = 9 };
        form.Children.Add(intro);
        form.Children.Add(Field("已保存的 Skill", _skills));
        form.Children.Add(Field("ID（自动生成）", _id));
        form.Children.Add(Field("名称", _name));
        form.Children.Add(Field("工作流说明", _instructions));
        form.Children.Add(Field("允许工具", _tools));
        form.Children.Add(_enabled);
        form.Children.Add(new TextBlock
        {
            Text = "工具列表只会缩小此 Skill 可见的工具范围；实际调用仍由 ToolHost、授权范围和审批链路控制。",
            Opacity = .65,
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(_status);

        var create = new Button { Content = "新建" };
        create.Click += (_, _) => ClearEditor();
        var remove = new Button { Content = "删除" };
        remove.Click += (_, _) => Remove();
        var save = new Button { Content = "保存 Skill" };
        save.Click += (_, _) => Save();
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { create, remove, save }
        });

        return new ScrollViewer { Content = form, Padding = new Avalonia.Thickness(4, 10, 4, 4) };
    }

    private void Refresh(string? selectId = null)
    {
        var snapshot = _store.Snapshot();
        _skills.ItemsSource = snapshot;
        _skills.SelectedItem = snapshot.FirstOrDefault(skill => string.Equals(skill.Id, selectId, StringComparison.Ordinal))
                               ?? snapshot.FirstOrDefault();
        if (_skills.SelectedItem is null) ClearEditor();
    }

    private void LoadSelected()
    {
        if (_skills.SelectedItem is not AgentSkill skill) return;
        _id.Text = skill.Id;
        _name.Text = skill.Name;
        _instructions.Text = skill.Instructions;
        _tools.Text = string.Join(", ", skill.ToolNames);
        _enabled.IsChecked = skill.Enabled;
        SetStatus(string.Empty, false);
    }

    private void ClearEditor()
    {
        _skills.SelectedItem = null;
        _id.Text = string.Empty;
        _name.Text = string.Empty;
        _instructions.Text = string.Empty;
        _tools.Text = string.Empty;
        _enabled.IsChecked = true;
        SetStatus(string.Empty, false);
    }

    private void Save()
    {
        try
        {
            var saved = _store.Upsert(new AgentSkill
            {
                Id = _id.Text ?? string.Empty,
                Name = _name.Text ?? string.Empty,
                Instructions = _instructions.Text ?? string.Empty,
                Enabled = _enabled.IsChecked == true,
                ToolNames = (_tools.Text ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            });
            Refresh(saved.Id);
            SetStatus("Skill 已保存。", true);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, false);
        }
    }

    private void Remove()
    {
        if (_store.Remove(_id.Text ?? string.Empty))
        {
            Refresh();
            SetStatus("Skill 已删除。", true);
        }
        else
        {
            SetStatus("请选择一个要删除的 Skill。", false);
        }
    }

    private void SetStatus(string text, bool success)
    {
        _status.Text = text;
        _status.Foreground = success ? Brushes.SeaGreen : Brushes.IndianRed;
    }

    private static Control Field(string label, Control editor) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, editor }
    };
}
