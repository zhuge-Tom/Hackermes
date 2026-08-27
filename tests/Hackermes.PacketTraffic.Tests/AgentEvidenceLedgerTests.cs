using Hackermes.AiPanel.OpenAI;
using Hackermes.AiPanel.Runtime;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class AgentEvidenceLedgerTests
{
    [Fact]
    public void Observing_packet_query_json_ids_populates_render()
    {
        var ledger = new AgentEvidenceLedger();
        ledger.Observe("packet_query",
            """{"items":[{"id":"pkt-alpha"},{"packetId":"pkt-beta"}]}""",
            success: true);

        var render = ledger.Render();
        Assert.Contains("【证据台账】", render, StringComparison.Ordinal);
        Assert.Contains("packets:", render, StringComparison.Ordinal);
        Assert.Contains("pkt-alpha", render, StringComparison.Ordinal);
        Assert.Contains("pkt-beta", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Observing_packet_analyze_findings_populates_observations()
    {
        var ledger = new AgentEvidenceLedger();
        ledger.Observe("packet_analyze",
            """{"findings":[{"code":"missing-hsts","message":"no HSTS"},{"code":"sensitive-header"}]}""",
            success: true);

        var render = ledger.Render();
        Assert.Contains("observations:", render, StringComparison.Ordinal);
        Assert.Contains("missing-hsts (packet_analyze)", render, StringComparison.Ordinal);
        Assert.Contains("sensitive-header (packet_analyze)", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Observing_spill_locator_keeps_it()
    {
        var ledger = new AgentEvidenceLedger();
        ledger.Observe("read_spill",
            "preview\nspill:0123456789abcdef0123456789abcdef\nmore",
            success: true);

        var render = ledger.Render();
        Assert.Contains("spills:", render, StringComparison.Ordinal);
        Assert.Contains("spill:0123456789abcdef0123456789abcdef", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_tool_adds_error_line()
    {
        var ledger = new AgentEvidenceLedger();
        ledger.Observe("packet_query", "boom first line\nsecond line should drop", success: false);

        var render = ledger.Render();
        Assert.Contains("errors:", render, StringComparison.Ordinal);
        Assert.Contains("boom first line", render, StringComparison.Ordinal);
        Assert.DoesNotContain("second line should drop", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Cap_does_not_grow_unbounded()
    {
        var ledger = new AgentEvidenceLedger();
        for (var index = 0; index < 40; index++)
            ledger.Observe("packet_query", "{\"id\":\"id-" + index.ToString("D2") + "\"}", success: true);

        var render = ledger.Render();
        Assert.True(render.Length <= 1500, $"render length {render.Length}");
        Assert.Contains("id-39", render, StringComparison.Ordinal);
        Assert.Contains("id-08", render, StringComparison.Ordinal);
        Assert.DoesNotContain("id-00", render, StringComparison.Ordinal);
        Assert.DoesNotContain("id-07", render, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pre_step_hook_appends_ephemeral_when_non_empty_and_proceeds_when_empty()
    {
        var ledger = new AgentEvidenceLedger();
        var hook = new EvidenceLedgerPreStepHook(ledger);
        var input = new PreStepInput(1, 1, []);

        var empty = await hook.BeforeStepAsync(input, CancellationToken.None);
        Assert.IsType<PreStepDecision.ProceedDecision>(empty);

        ledger.Observe("packet_query", """{"id":"pkt-hook"}""", success: true);
        var decision = await hook.BeforeStepAsync(input, CancellationToken.None);
        var ephemeral = Assert.IsType<PreStepDecision.EphemeralDecision>(decision);
        var message = Assert.Single(ephemeral.Appendix);
        Assert.Equal("user", message.Role);
        Assert.Contains("【上下文注入·证据台账】", message.Content, StringComparison.Ordinal);
        Assert.Contains("pkt-hook", message.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_ledger_renders_empty_string()
    {
        Assert.Equal(string.Empty, new AgentEvidenceLedger().Render());
    }
}
