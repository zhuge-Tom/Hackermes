using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AiChatViewLayoutTests
{
    [Fact]
    public void Tool_call_uses_the_expander_template_instead_of_the_plain_message_template()
    {
        var (document, controls, xaml) = LoadView();
        var templates = Assert.Single(document.Descendants(controls + "ItemsControl.DataTemplates"))
            .Elements(controls + "DataTemplate")
            .ToArray();

        // Derived templates precede the base template (first assignable template wins):
        // reasoning rows first, then tool-call expanders, then plain conversation turns.
        Assert.Equal("vm:AiReasoningLine", templates[0].Attribute(xaml + "DataType")?.Value);
        Assert.Equal("vm:AiToolCallLine", templates[1].Attribute(xaml + "DataType")?.Value);
        Assert.NotNull(templates[1].Descendants(controls + "Expander").SingleOrDefault());
        Assert.Equal("vm:AiChatLine", templates[2].Attribute(xaml + "DataType")?.Value);
    }

    [Fact]
    public void Session_actions_use_path_icons_for_new_export_and_settings()
    {
        var (document, controls, _) = LoadView();
        var tips = new[] { "新建会话", "导出会话转录", "设置" };
        foreach (var tip in tips)
        {
            var button = Assert.Single(document.Descendants(controls + "Button"),
                element => string.Equals(element.Attribute("ToolTip.Tip")?.Value, tip, StringComparison.Ordinal));
            Assert.NotNull(button.Descendants(controls + "PathIcon").SingleOrDefault());
            Assert.Null(button.Attribute("Content"));
        }
    }

    [Fact]
    public void Multiline_prompt_starts_text_at_the_top()
    {
        var (document, controls, _) = LoadView();
        var prompt = Assert.Single(document.Descendants(controls + "TextBox"),
            element => string.Equals(element.Attribute("AcceptsReturn")?.Value, "True", StringComparison.Ordinal));

        Assert.Equal("Top", prompt.Attribute("VerticalContentAlignment")?.Value);
        Assert.Equal("Auto", prompt.Attribute("ScrollViewer.VerticalScrollBarVisibility")?.Value);
        Assert.Contains("Enter 发送", prompt.Attribute("PlaceholderText")?.Value);
        Assert.Contains("Ctrl+Enter 换行", prompt.Attribute("PlaceholderText")?.Value);
    }

    private static (XDocument Document, XNamespace Controls, XNamespace Xaml) LoadView()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "AiChatView.axaml");
        var document = XDocument.Load(path);
        return (document, "https://github.com/avaloniaui", "http://schemas.microsoft.com/winfx/2006/xaml");
    }
}
