using Hookmes.Traffic.Models;
using Hookmes.Traffic.Rules;
using System;
using System.Security.Cryptography;
using System.Text;

namespace Hookmes.Automation.Packet;

public sealed class TrafficRuleAuditBridge : IDisposable
{
    private readonly ITrafficRuleExecutionSource _source;
    private readonly IPacketAuditTrail _audit;

    public TrafficRuleAuditBridge(ITrafficRuleExecutionSource source, IPacketAuditTrail audit)
    {
        _source = source;
        _audit = audit;
        _source.RuleExecuted += OnRuleExecuted;
    }

    private void OnRuleExecuted(TrafficRuleExecutionEvent value)
    {
        try { _audit.Record(ToAuditEntry(value)); }
        catch { /* Audit failure must never change interception behavior. */ }
    }

    public static PacketAuditEntry ToAuditEntry(TrafficRuleExecutionEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new PacketAuditEntry(Guid.NewGuid().ToString("N"), value.Timestamp,
            "traffic-rule", PacketAuditOperation.RuleMatch,
            value.PacketId, value.Stage == TrafficStage.Response ? "response" : "request",
            Version(value.Before), Version(value.After),
            value.Result == TrafficRuleExecutionResult.Failed ? PacketAuditResult.Failed : PacketAuditResult.Succeeded,
            value.ErrorCode, value.RuleId, $"{value.Action}:{value.Result}");
    }

    private static PacketEditVersion Version(TrafficRulePacketMetadata value)
    {
        var metadata = $"{value.Method}|{value.Scheme}|{value.Host}|{value.PathHash}|{value.Status}|" +
                       $"{value.HeaderCount}|{value.BodyLength}|{value.State}";
        return new PacketEditVersion(value.BodyLength,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata))).ToLowerInvariant(), null);
    }

    public void Dispose() => _source.RuleExecuted -= OnRuleExecuted;
}
