using Hackermes.AiPanel.Agent;
using Hackermes.Platform.Models;
using System;
using System.Linq;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AgentAssessmentPromptTests
{
    [Fact]
    public void System_prompt_routes_browser_assessment_through_authorized_control_plane()
    {
        var messages = new AgentContextCompactor().BuildRequest(
            [], new AgentMemoryDocument(), [], new AiSettings { MaxContextCharacters = 24_000 });

        var system = Assert.Single(messages).Content ?? string.Empty;
        Assert.Contains("page_context", system, StringComparison.Ordinal);
        Assert.Contains("assessment_create_scope_from_page", system, StringComparison.Ordinal);
        Assert.Contains("one-time approval", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ToolHost", system, StringComparison.Ordinal);
        Assert.Contains("Never invent or substitute a target", system, StringComparison.Ordinal);
        Assert.Contains("do not claim a vulnerability without tool evidence", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("packet_analyze", system, StringComparison.Ordinal);
        Assert.Contains("assessment_create_finding", system, StringComparison.Ordinal);
        Assert.Contains("codes, not confirmed vulnerabilities", system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assessment_create_scope ", system, StringComparison.Ordinal);
        Assert.True(system.Length < 7_000, $"System safety guidance unexpectedly grew to {system.Length} characters.");
    }

    [Fact]
    public void Full_access_prompt_prefers_one_call_assessment_without_repeated_confirmation()
    {
        var messages = new AgentContextCompactor().BuildRequest(
            [], new AgentMemoryDocument(), [], new AiSettings
            {
                MaxContextCharacters = 24_000,
                PermissionMode = AiPermissionMode.FullAccess
            });

        var system = Assert.Single(messages).Content ?? string.Empty;
        Assert.Contains("assessment_authorize_and_run", system, StringComparison.Ordinal);
        Assert.Contains("without another confirmation", system, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("every Mutating or Dangerous call asks", system, StringComparison.OrdinalIgnoreCase);
    }
}
