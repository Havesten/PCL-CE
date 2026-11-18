using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Relay;

public sealed record RelayGameServer(IPEndPoint Endpoint, string Name, string Version)
{
    public override string ToString() => $"{Name}（{Version}）";
}

/// <summary>以有界的 Minecraft 状态查询与随机 Ping/Pong 确认游戏端口。</summary>
public static class MinecraftServerProbe
{
    private const int MaxPacketLength = 64 * 1024;

    public static Task<RelayGameServer?> ProbeAsync(IPEndPoint endpoint, TimeSpan timeout,
        CancellationToken ct = default) => ProbeAsync(endpoint.Address.ToString(), endpoint.Port, timeout, ct);

    public static async Task<RelayGameServer?> ProbeAsync(string host, int port, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (port is < 1 or > 65535 || timeout <= TimeSpan.Zero) return null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        using var modernBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        modernBudget.CancelAfter(TimeSpan.FromTicks(timeout.Ticks * 2 / 3));
        var modern = await TryModernAsync(host, port, modernBudget.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (modern is not null) return modern;
        if (budget.IsCancellationRequested) return null;
        var legacy = await TryLegacyAsync(host, port, budget.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return legacy;
    }

    private static async Task<RelayGameServer?> TryModernAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var stream = client.GetStream();
            using var handshake = new MemoryStream();
            WriteVarInt(handshake, 0);
            WriteVarInt(handshake, -1);
            var address = Encoding.UTF8.GetBytes(host);
            if (address.Length > 255) return null;
            WriteVarInt(handshake, address.Length);
            handshake.Write(address);
            handshake.WriteByte((byte)(port >> 8));
            handshake.WriteByte((byte)port);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), ct).ConfigureAwait(false);
            await WritePacketAsync(stream, new byte[] { 0 }, ct).ConfigureAwait(false);

            var statusPacket = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
            using var status = new MemoryStream(statusPacket, false);
            if (await ReadVarIntAsync(status, ct).ConfigureAwait(false) != 0) return null;
            var jsonLength = await ReadVarIntAsync(status, ct).ConfigureAwait(false);
            if (jsonLength <= 0 || jsonLength != status.Length - status.Position) return null;
            var json = new byte[jsonLength];
            await status.ReadExactlyAsync(json, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Object ||
                !version.TryGetProperty("name", out var versionName) || versionName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(versionName.GetString()) ||
                !version.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out _) ||
                !root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Object ||
                !players.TryGetProperty("online", out var online) || !online.TryGetInt32(out var onlineCount) || onlineCount < 0 ||
                !players.TryGetProperty("max", out var maximum) || !maximum.TryGetInt32(out var maxCount) || maxCount < 0 ||
                !root.TryGetProperty("description", out var description) ||
                description.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array)) return null;

            var ping = new byte[9];
            ping[0] = 1;
            RandomNumberGenerator.Fill(ping.AsSpan(1));
            await WritePacketAsync(stream, ping, ct).ConfigureAwait(false);
            var pong = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
            if (!pong.AsSpan().SequenceEqual(ping)) return null;
            var text = new StringBuilder();
            AppendDescription(description, text, 0);
            return new RelayGameServer(RemoteEndpoint(client),
                CleanText(text.ToString(), 80, "Minecraft 房间"),
                CleanText(versionName.GetString(), 40, "Minecraft"));
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or OperationCanceledException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<RelayGameServer?> TryLegacyAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0xfe, 0x01 }, ct).ConfigureAwait(false);
            var header = new byte[3];
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            if (header[0] != 0xff) return null;
            var chars = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
            if (chars is < 1 or > 8192) return null;
            var payload = new byte[chars * 2];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            var response = new UnicodeEncoding(true, false, true).GetString(payload);
            var parts = response.Split('\0');
            if (parts.Length != 6 || parts[0] != "§1" || !int.TryParse(parts[1], out var protocol) || protocol < 0 ||
                string.IsNullOrWhiteSpace(parts[2]) || !int.TryParse(parts[4], out var online) || online < 0 ||
                !int.TryParse(parts[5], out var maximum) || maximum < 0) return null;
            return new RelayGameServer(RemoteEndpoint(client),
                CleanText(parts[3], 80, "Minecraft 房间"), CleanText(parts[2], 40, "Minecraft"));
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static IPEndPoint RemoteEndpoint(TcpClient client)
    {
        var endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        return endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;
    }

    internal static string CleanText(string? value, int limit, string fallback = "")
    {
        var text = new StringBuilder();
        var formatting = false;
        foreach (var c in value ?? string.Empty)
        {
            if (formatting) { formatting = false; continue; }
            if (c == '§') { formatting = true; continue; }
            if (char.IsControl(c) || c is '[' or ']') continue;
            if (text.Length >= limit) break;
            text.Append(c);
        }
        return string.IsNullOrWhiteSpace(text.ToString()) ? fallback : text.ToString().Trim();
    }

    private static void AppendDescription(JsonElement element, StringBuilder text, int depth)
    {
        if (depth > 8 || text.Length >= 160) return;
        if (element.ValueKind == JsonValueKind.String) text.Append(element.GetString());
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) AppendDescription(child, text, depth + 1);
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("text", out var content)) AppendDescription(content, text, depth + 1);
            if (element.TryGetProperty("extra", out var extra)) AppendDescription(extra, text, depth + 1);
        }
    }

    internal static void WriteVarInt(Stream stream, int value)
    {
        var remaining = unchecked((uint)value);
        do
        {
            var current = (byte)(remaining & 127);
            remaining >>= 7;
            stream.WriteByte(remaining == 0 ? current : (byte)(current | 128));
        } while (remaining != 0);
    }

    internal static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var value = 0;
        var next = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            await stream.ReadExactlyAsync(next, ct).ConfigureAwait(false);
            if (index == 4 && (next[0] & 0xf8) != 0) throw new InvalidDataException("Invalid packet length");
            value |= (next[0] & 127) << (index * 7);
            if ((next[0] & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid packet length");
    }

    internal static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length is <= 0 or > MaxPacketLength) throw new InvalidDataException("Invalid Minecraft packet size");
        var packet = new byte[length];
        await stream.ReadExactlyAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    internal static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
    }
}
