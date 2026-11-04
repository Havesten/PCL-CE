using System;
using PCL.Core.Utils.Exts;

namespace PCL.Core.Link;

/// <summary>
///     P2P 房间码：把「网络名称 + 网络密钥 + 游戏端口」压成一段人眼可读、可以口头报出来的短串。
/// </summary>
/// <remarks>
///     房间码只由 Base32 字母表构成，长度固定 18 位，按 4-4-4-6 分组显示：
///     <code>ABCD-EFGH-JKLM-NPQRST</code>
///     前 8 位是网络名称，中间 6 位是网络密钥，后 4 位是端口（Base32）。
///     解析时会忽略大小写、空格与连字符，用户从聊天窗口里复制粘贴不需要手动清理。
/// </remarks>
public static class LinkCode
{
    /// <summary>
    ///     Base32 字母表。去掉了容易与数字混淆的 I、O、0、1。
    /// </summary>
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>网络名称长度。</summary>
    public const int NameLength = 8;

    /// <summary>网络密钥长度。</summary>
    public const int SecretLength = 6;

    /// <summary>端口段的字符数。32^4 = 1048576，足以覆盖 65535。</summary>
    private const int PortLength = 4;

    /// <summary>房间码总长度。</summary>
    public const int TotalLength = NameLength + SecretLength + PortLength;

    /// <summary>生成一个随机网络名称。</summary>
    public static string NewName() => RandomChars(NameLength);

    /// <summary>生成一个随机网络密钥。</summary>
    public static string NewSecret() => RandomChars(SecretLength);

    /// <summary>
    ///     把网络参数编码为房间码。
    /// </summary>
    /// <param name="name">网络名称，只允许 Base32 字母表中的字符。</param>
    /// <param name="secret">网络密钥，要求同上。</param>
    /// <param name="port">游戏端口。</param>
    public static string Build(string name, string secret, int port)
    {
        if (!TryNormalize(name, NameLength, out var n))
            throw new ArgumentException("网络名称必须是 " + NameLength + " 位 Base32 字符", nameof(name));
        if (!TryNormalize(secret, SecretLength, out var s))
            throw new ArgumentException("网络密钥必须是 " + SecretLength + " 位 Base32 字符", nameof(secret));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口需在 1-65535 之间");

        // Base32 里的 0 是 '2'，补位也用 '2'
        var portCode = port.ToString().FromB10ToB32().PadLeft(PortLength, Alphabet[0]);
        return Format(n + s + portCode);
    }

    /// <summary>
    ///     解析房间码。允许大小写混用，允许带空格与连字符。
    /// </summary>
    public static bool TryParse(string? code, out string name, out string secret, out int port)
    {
        name = string.Empty;
        secret = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(code)) return false;

        var cleaned = Clean(code);
        if (cleaned.Length != TotalLength) return false;
        foreach (var c in cleaned)
            if (!Alphabet.Contains(c))
                return false;

        name = cleaned[..NameLength];
        secret = cleaned.Substring(NameLength, SecretLength);

        var portCode = cleaned[(NameLength + SecretLength)..];
        try
        {
            var value = int.Parse(portCode.FromB32ToB10());
            if (value is < 1 or > 65535) return false;
            port = value;
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     把房间码按 4-4-4-6 分组，便于阅读与抄写。
    /// </summary>
    public static string Format(string raw)
    {
        var code = Clean(raw);
        if (code.Length != TotalLength) return code;

        return string.Join("-",
            code[..4],
            code.Substring(4, 4),
            code.Substring(8, 4),
            code[12..]);
    }

    /// <summary>去掉空格与连字符，并统一转为大写。</summary>
    private static string Clean(string raw) =>
        raw.Replace(" ", "").Replace("-", "").Replace("\u2014", "").Trim().ToUpperInvariant();

    private static bool TryNormalize(string? value, int length, out string result)
    {
        result = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var cleaned = Clean(value);
        if (cleaned.Length != length) return false;
        foreach (var c in cleaned)
            if (!Alphabet.Contains(c))
                return false;
        result = cleaned;
        return true;
    }

    private static string RandomChars(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        return new string(chars);
    }
}
