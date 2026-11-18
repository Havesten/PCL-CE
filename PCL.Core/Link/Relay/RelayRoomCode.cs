using System;
using System.Linq;

namespace PCL.Core.Link.Relay;

public static class RelayRoomCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static bool TryNormalize(string? raw, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 128) return false;
        var compact = new string(raw.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (compact.Length != 25) return false;
        if (compact[..5].Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))) return false;
        if (compact[5..].Any(c => Alphabet.IndexOf(c) < 0)) return false;
        code = string.Join('-', Enumerable.Range(0, 5).Select(i => compact.Substring(i * 5, 5)));
        return true;
    }
}
