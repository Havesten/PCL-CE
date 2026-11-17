using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Link.EasyTier;

/// <summary>只把房间中的真实玩家直连作为成功依据，节点在线和路由已发现都不等于直连成功。</summary>
public static class P2pReadiness
{
    public static List<ETPlayerInfo> RoomPeers(IEnumerable<ETPlayerInfo> peers) => peers
        .Where(p => p.Cost != ETConnectionType.Local &&
                    (p.Hostname.StartsWith("H|") || p.Hostname.StartsWith("J|"))).ToList();

    public static bool CanJoinHost(IEnumerable<ETPlayerInfo> peers) => RoomPeers(peers)
        .Any(p => p.IsHost && p.Cost == ETConnectionType.P2P);
}
