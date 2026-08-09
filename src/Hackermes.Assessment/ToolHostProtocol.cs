using Hackermes.Platform.Services;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hackermes.Assessment;

public sealed record ToolHostTicket(string Nonce, string JobId, string PlanId, string ApprovalId, string ScopeId,
    string Actor, string[] AllowedTargets, AssessmentStep Step, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public sealed record ToolHostEnvelope(string Payload, string Signature);
public sealed record ToolHostResponse(bool Success, string Output, string? Error, int? ExitCode = null);

public sealed class ToolHostTicketSigner
{
    public const string SecretName = "assessment.toolhost.hmac.v1";
    private readonly ISecretStore _secrets;
    public ToolHostTicketSigner(ISecretStore secrets) => _secrets = secrets;

    public ToolHostEnvelope Issue(ToolHostTicket ticket)
    {
        var payload = JsonSerializer.Serialize(ticket);
        return new(payload, Sign(payload, GetOrCreateKey()));
    }

    public ToolHostTicket Verify(ToolHostEnvelope envelope)
    {
        var expected = Convert.FromHexString(Sign(envelope.Payload, GetOrCreateKey()));
        byte[] actual;
        try { actual = Convert.FromHexString(envelope.Signature); }
        catch (FormatException) { throw new UnauthorizedAccessException("ToolHost ticket signature is invalid."); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new UnauthorizedAccessException("ToolHost ticket signature is invalid.");
        var ticket = JsonSerializer.Deserialize<ToolHostTicket>(envelope.Payload) ?? throw new UnauthorizedAccessException("ToolHost ticket payload is invalid.");
        if (ticket.ExpiresAt <= DateTimeOffset.UtcNow || ticket.IssuedAt > DateTimeOffset.UtcNow.AddMinutes(1)) throw new UnauthorizedAccessException("ToolHost ticket is expired or not yet valid.");
        if (string.IsNullOrWhiteSpace(ticket.Nonce) || string.IsNullOrWhiteSpace(ticket.ApprovalId)) throw new UnauthorizedAccessException("ToolHost ticket is incomplete.");
        return ticket;
    }

    private byte[] GetOrCreateKey()
    {
        var encoded = _secrets.Get(SecretName);
        if (!string.IsNullOrWhiteSpace(encoded)) return Convert.FromBase64String(encoded);
        var key = RandomNumberGenerator.GetBytes(32);
        _secrets.Set(SecretName, Convert.ToBase64String(key));
        return key;
    }
    private static string Sign(string payload, byte[] key) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}
