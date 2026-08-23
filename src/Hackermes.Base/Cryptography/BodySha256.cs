using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Hackermes.Base.Cryptography;

/// <summary>
/// Memoizes body SHA-256 digests by array identity. Traffic bodies are immutable byte[]
/// instances (every edit allocates a new array), so reference equality is a sufficient
/// freshness check and a hit can never return a stale digest. Entries die with their
/// arrays in a ConditionalWeakTable, keeping memory bounded without explicit eviction.
/// Lives in Base so both the Automation packet surface and the Traffic comparison
/// snapshots can share one cache without layering violations.
/// </summary>
public static class BodySha256
{
    private static readonly ConditionalWeakTable<byte[], string> Cache = new();

    public static string Of(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Cache.TryGetValue(body, out var sha256)) return sha256;

        sha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        Cache.AddOrUpdate(body, sha256);
        return sha256;
    }
}
