using System.Collections.Generic;
using System.Linq;
using PCL.Core.App;
using PCL.Core.Link.Scaffolding.EasyTier;

namespace PCL.Core.Link.EasyTier;

public enum P2pAvailability
{
    Unknown,
    Possible,
    Unavailable
}

public sealed record P2pAvailabilityResult(bool CanAttempt, P2pAvailability Availability, string Summary);

/// <summary>只把房间中的真实玩家直连作为成功依据，节点在线和路由已发现都不等于直连成功。</summary>
public static class P2pReadiness
{
    /// <summary>按本机检测结果评估所选协议；检测失败和严格 NAT 不证明两端无法直连。</summary>
    public static P2pAvailabilityResult Assess(CliNetTest.NetStatus? status, LinkIpMode mode)
    {
        if (status is null)
            return new(true, P2pAvailability.Unknown, "尚无法判断 P2P 可用性，可重新检测或尝试连接。");

        if (mode == LinkIpMode.Ipv6Only)
            return status.SupportIPv6
                ? new(true, P2pAvailability.Possible, "可通过 IPv6 尝试 P2P 联机。")
                : new(false, P2pAvailability.Unavailable, "当前网络未检测到可用公网 IPv6，请改用自动或仅 IPv4。");

        if (mode == LinkIpMode.Auto && status.SupportIPv6)
            return new(true, P2pAvailability.Possible, "可尝试 P2P 联机。");

        static bool HasEvaluatedPath(CliNetTest.NatType type) => type is
            CliNetTest.NatType.OpenInternet or CliNetTest.NatType.NoPat or
            CliNetTest.NatType.FullCone or CliNetTest.NatType.Restricted or
            CliNetTest.NatType.PortRestricted or CliNetTest.NatType.SymmetricEasy or
            CliNetTest.NatType.Symmetric;

        // SymUdpFirewall 仅说明 STUN 的 UDP 映射/过滤行为；不能据此断言 TCP 或对端不可达。
        if (!HasEvaluatedPath(status.UdpNatType) && !HasEvaluatedPath(status.TcpNatType))
            return new(true, P2pAvailability.Unknown, "当前检测结果不足以确认 P2P 可用性，可重新检测或尝试连接。");

        if (status.UdpNatType is CliNetTest.NatType.SymmetricFirewall or CliNetTest.NatType.UdpBlocked)
            return new(true, P2pAvailability.Possible, "UDP 连接受限，可尝试 TCP 联机。");

        if (status.UdpNatType is CliNetTest.NatType.Symmetric or CliNetTest.NatType.SymmetricEasy)
            return new(true, P2pAvailability.Possible, "可尝试 P2P 联机，当前 NAT 条件较严格。");

        return new(true, P2pAvailability.Possible, "可尝试 P2P 联机。");
    }

    public static List<ETPlayerInfo> RoomPeers(IEnumerable<ETPlayerInfo> peers) => peers
        .Where(p => p.Cost != ETConnectionType.Local &&
                    (p.Hostname.StartsWith("H|") || p.Hostname.StartsWith("J|"))).ToList();

    public static bool CanJoinHost(IEnumerable<ETPlayerInfo> peers) => RoomPeers(peers)
        .Any(p => p.IsHost && p.Cost == ETConnectionType.P2P);
}
