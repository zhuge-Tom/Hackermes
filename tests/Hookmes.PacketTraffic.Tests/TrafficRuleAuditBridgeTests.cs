using Hookmes.Automation.Packet;
using Hookmes.Traffic.Models;
using Hookmes.Traffic.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Hookmes.PacketTraffic.Tests;

public sealed class TrafficRuleAuditBridgeTests
{
    [Fact]
    public void Rule_events_map_to_metadata_only_audit_entries()
    {
        var source = new RuleSource();
        var audit = new MemoryAudit();
        using var bridge = new TrafficRuleAuditBridge(source, audit);
        var before = Metadata(10, TrafficState.Paused);
        var after = Metadata(20, TrafficState.Fulfilled);

        source.Emit(new TrafficRuleExecutionEvent("sanitize", "packet", "page", TrafficStage.Response,
            TrafficRuleAction.FulfillResponse, TrafficRuleExecutionResult.Succeeded,
            before, after, DateTimeOffset.UtcNow));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PacketAuditOperation.RuleMatch, entry.Operation);
        Assert.Equal(PacketAuditResult.Succeeded, entry.Result);
        Assert.Equal("response", entry.Side);
        Assert.Equal("traffic-rule", entry.EntryPoint);
        Assert.Equal("sanitize", entry.RuleId);
        Assert.Equal("FulfillResponse:Succeeded", entry.RuleAction);
        Assert.Equal(10, entry.Before.Length);
        Assert.Equal(20, entry.After.Length);
        Assert.NotEqual(entry.Before.Sha256, entry.After.Sha256);
        var serialized = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("Authorization", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-value", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failed_execution_preserves_safe_error_code_and_dispose_unsubscribes()
    {
        var source = new RuleSource();
        var audit = new MemoryAudit();
        var bridge = new TrafficRuleAuditBridge(source, audit);
        var metadata = Metadata(0, TrafficState.Paused);

        source.Emit(new TrafficRuleExecutionEvent("block", "packet", "page", TrafficStage.Request,
            TrafficRuleAction.Fail, TrafficRuleExecutionResult.Failed, metadata, metadata,
            DateTimeOffset.UtcNow, "InvalidOperationException"));
        bridge.Dispose();
        source.Emit(new TrafficRuleExecutionEvent("block", "packet-2", "page", TrafficStage.Request,
            TrafficRuleAction.Fail, TrafficRuleExecutionResult.Succeeded, metadata, metadata, DateTimeOffset.UtcNow));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PacketAuditResult.Failed, entry.Result);
        Assert.Equal("InvalidOperationException", entry.ErrorCode);
    }

    [Fact]
    public void Traffic_rule_event_contract_has_no_raw_header_or_body_payload()
    {
        var names = typeof(TrafficRulePacketMetadata).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("Headers", names);
        Assert.DoesNotContain("Body", names);
        Assert.DoesNotContain("Url", names);
        Assert.Contains("PathHash", names);
    }

    private static TrafficRulePacketMetadata Metadata(long bodyLength, TrafficState state) =>
        new("POST", "https", "example.test", new string('a', 64), 200, 3, bodyLength, state);

    private sealed class RuleSource : ITrafficRuleExecutionSource
    {
        public event Action<TrafficRuleExecutionEvent>? RuleExecuted;
        public void Emit(TrafficRuleExecutionEvent value) => RuleExecuted?.Invoke(value);
    }

    private sealed class MemoryAudit : IPacketAuditTrail
    {
        public List<PacketAuditEntry> Entries { get; } = [];
        public void Record(PacketAuditEntry entry) => Entries.Add(entry);
        public IReadOnlyList<PacketAuditEntry> Query(PacketAuditQuery query) => Entries;
    }
}
