using Hackermes.Automation.Commands;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Hackermes.App;

/// <summary>
/// Local governance surface for the shared ECDSA signing identity (traffic audit +
/// assessment report exports). Deliberately CLI-only: adopting, rotating and revoking
/// signing keys are operator decisions and are kept out of the Agent tool surface.
/// </summary>
internal static class SigningKeysCommandRegistrar
{
    public static void Register(CommandRegistry commands, PacketAuditSigningKey key, AuditKeyTrustFile trust)
    {
        commands.Register(new CommandDefinition
        {
            Name = "signing-keys",
            Summary = "Govern the shared ECDSA signing identity (audit + assessment reports)",
            Usage = "signing-keys list | adopt [note] | rotate [note] | revoke <keyId> [note]",
            IsMutating = true,
            Handler = (context, _) => Task.FromResult(Execute(context, key, trust))
        });
    }

    private static CommandResult Execute(CommandContext context, PacketAuditSigningKey key, AuditKeyTrustFile trust)
    {
        try
        {
            switch (context.Arg(0)?.ToLowerInvariant())
            {
                case "list":
                    return CommandResult.Ok(Format(trust, key));
                case "adopt":
                    if (trust.TrustFileExists) return CommandResult.Fail("The trust file already exists.");
                    trust.RecordInitialGeneration(key.KeyId, key.PublicKey, context.Rest(1));
                    return CommandResult.Ok($"adopted keyId={key.KeyId} policy=allowlist");
                case "rotate":
                    if (!trust.TrustFileExists) trust.RecordInitialGeneration(key.KeyId, key.PublicKey, "auto-adopt before rotate");
                    var previousKeyId = key.KeyId;
                    key.Rotate(trust, context.Rest(1));
                    return CommandResult.Ok($"rotated previous={previousKeyId} active={key.KeyId}");
                case "revoke":
                    var revoked = trust.Revoke(Required(context, 1, "keyId"), context.Rest(2));
                    var warning = key.KeyId.Trim().Equals(Required(context, 1, "keyId").Trim(), StringComparison.OrdinalIgnoreCase)
                        ? " warning=active-key-revoked rotate before exporting again"
                        : string.Empty;
                    return CommandResult.Ok($"revoked={Required(context, 1, "keyId")} known={revoked.ToString().ToLowerInvariant()}{warning}");
                default:
                    return CommandResult.Fail("Usage: signing-keys list | adopt [note] | rotate [note] | revoke <keyId> [note]");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            return CommandResult.Fail(exception.Message);
        }
    }

    private static string Format(AuditKeyTrustFile trust, PacketAuditSigningKey key)
    {
        var lines = trust.Generations
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .Select(entry =>
                $"{entry.KeyId[..Math.Min(16, entry.KeyId.Length)]}… status={entry.Status.ToString().ToLowerInvariant()} " +
                $"created={entry.CreatedAtUtc:O}" +
                (entry.RotatedAtUtc is { } rotated ? $" rotated={rotated:O}" : string.Empty) +
                (entry.RevokedAtUtc is { } revokedAt ? $" revokedAt={revokedAt:O}" : string.Empty) +
                (string.IsNullOrWhiteSpace(entry.Note) ? string.Empty : $" note={entry.Note}"));
        var header = $"active={key.KeyId} policy={(trust.TrustFileExists ? "allowlist" : "legacy-pinning")}";
        var entries = lines.ToArray();
        return entries.Length == 0 ? header : header + Environment.NewLine + string.Join(Environment.NewLine, entries);
    }

    private static string Required(CommandContext context, int index, string name) =>
        context.Arg(index) ?? throw new ArgumentException($"Missing {name}.");
}
