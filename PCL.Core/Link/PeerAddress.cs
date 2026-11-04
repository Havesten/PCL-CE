using System;
using System.Collections.Generic;

namespace PCL.Core.Link;

/// <summary>
///     手动直连模式下的对端地址解析。
/// </summary>
/// <remarks>
///     纯直连不经过任何公共节点，所以双方必须显式知道对方在哪。用户可能填出各种写法，
///     这里统一收敛成 EasyTier 的 <c>-p</c> 参数：
///     <list type="bullet">
///         <item><c>1.2.3.4:11010</c></item>
///         <item><c>1.2.3.4</c>（端口取默认 11010）</item>
///         <item><c>[2001:db8::1]:11010</c> 与裸 IPv6 <c>2001:db8::1</c></item>
///         <item><c>relay.example.com:11010</c></item>
///         <item>已经写好的 <c>tcp://…</c> / <c>udp://…</c>，原样使用，不再补 udp/tcp</item>
///     </list>
/// </remarks>
public static class PeerAddress
{
    /// <summary>EasyTier 的默认监听端口。</summary>
    public const int DefaultPort = 11010;

    /// <summary>
    ///     解析对端地址，输出可直接塞进 <c>-p</c> 的 URL 列表。
    /// </summary>
    /// <param name="input">用户输入的原始文本。</param>
    /// <param name="urls">解析结果；未显式指定协议时会同时给出 tcp 与 udp 两条。</param>
    /// <param name="message">失败原因，成功时为空字符串。</param>
    public static bool TryParse(string? input, out string[] urls, out string message)
    {
        urls = [];
        message = string.Empty;

        var raw = input?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            message = "对端地址不能为空";
            return false;
        }

        // 已经写了协议前缀，原样交给 EasyTier，不再猜测
        string[] schemes = ["tcp://", "udp://", "ws://", "wss://", "quic://"];
        foreach (var scheme in schemes)
            if (raw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                if (raw.Length <= scheme.Length || raw[scheme.Length..].Trim().Length == 0)
                {
                    message = "协议后面缺少地址";
                    return false;
                }

                urls = [raw];
                return true;
            }

        if (!TrySplitHostPort(raw, out var host, out var port, out message)) return false;

        if (host.Length == 0)
        {
            message = "缺少主机名或 IP";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            message = "端口需要填 1-65535 之间的数字";
            return false;
        }

        var hostInUrl = host.Contains(':') ? $"[{host}]" : host;
        urls = [$"tcp://{hostInUrl}:{port}", $"udp://{hostInUrl}:{port}"];
        return true;
    }

    /// <summary>
    ///     把 <c>host[:port]</c> 拆开，兼容裸 IPv6 与带方括号的 IPv6。
    /// </summary>
    public static bool TrySplitHostPort(string raw, out string host, out int port, out string message)
    {
        host = string.Empty;
        port = DefaultPort;
        message = string.Empty;

        var text = raw.Trim();
        if (text.Length == 0)
        {
            message = "对端地址不能为空";
            return false;
        }

        // [IPv6]:port 或 [IPv6]
        if (text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                message = "IPv6 地址缺少右方括号";
                return false;
            }

            host = text[1..close];
            var rest = text[(close + 1)..].Trim();
            if (rest.Length == 0) return true;
            if (!rest.StartsWith(':'))
            {
                message = "地址格式不对，应为 [IPv6]:端口";
                return false;
            }

            return TryParsePort(rest[1..], out port, out message);
        }

        var firstColon = text.IndexOf(':');
        var lastColon = text.LastIndexOf(':');

        // 没有端口
        if (firstColon < 0)
        {
            host = text;
            return true;
        }

        // 多个冒号且没有 IPv4:port 的形状 → 裸 IPv6，不带端口
        if (firstColon != lastColon)
        {
            host = text;
            return true;
        }

        host = text[..lastColon];
        return TryParsePort(text[(lastColon + 1)..], out port, out message);
    }

    private static bool TryParsePort(string text, out int port, out string message)
    {
        port = DefaultPort;
        message = string.Empty;

        if (!int.TryParse(text.Trim(), out var value))
        {
            message = "端口不是有效数字";
            return false;
        }

        port = value;
        return true;
    }

    /// <summary>
    ///     拼出本机监听用的 <c>-l</c> 参数。IPv6 专用模式只监听 <c>[::]</c>。
    /// </summary>
    public static IEnumerable<string> ListenUrls(int port, bool ipv6Only)
    {
        var host = ipv6Only ? "[::]" : "0.0.0.0";
        yield return $"tcp://{host}:{port}";
        yield return $"udp://{host}:{port}";
    }
}
