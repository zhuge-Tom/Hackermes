using System;
using System.Text;

namespace Hackermes.AiPanel.Runtime;

/// <summary>
/// Lightweight mixed-script token estimator (dsh token-meter lineage, heuristic tier):
/// CJK characters price ≈1 token each, everything else ≈4 chars/token. Not exact — it is
/// consistent, monotonic and provider-agnostic, which is what budget decisions need.
/// </summary>
public static class AgentTokenMeter
{
    public const double NonCjkCharsPerToken = 4.0;

    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cjk = 0;
        var other = 0;
        foreach (var chunk in text.EnumerateRunes())
        {
            if (IsCjk(chunk.Value)) cjk++;
            else other++;
        }
        return cjk + (int)Math.Ceiling(other / NonCjkCharsPerToken);
    }

    private static bool IsCjk(int codePoint) =>
        (codePoint >= 0x2E80 && codePoint <= 0x9FFF) ||   // radicals, kana, CJK unified
        (codePoint >= 0x3400 && codePoint <= 0x4DBF) ||
        (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||
        (codePoint >= 0x20000 && codePoint <= 0x2FA1F);   // ext-B+
}
