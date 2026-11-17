using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Scaffolding.EasyTier;

namespace PCL.Core.Test;

[TestClass]
public class P2pReadinessTest
{
    private static ETPlayerInfo Peer(string name, ETConnectionType cost, bool host = false) => new()
    {
        Hostname = name, Cost = cost, IsHost = host
    };

    [TestMethod]
    public void RendezvousAndLocalNodeDoNotCountAsPlayers()
    {
        var peers = new[] {
            Peer("PublicServer_starvm", ETConnectionType.P2P),
            Peer("some-rendezvous", ETConnectionType.P2P),
            Peer("H|self", ETConnectionType.Local, true)
        };
        Assert.HasCount(0, P2pReadiness.RoomPeers(peers));
        Assert.IsFalse(P2pReadiness.CanJoinHost(peers));
    }

    [TestMethod]
    public void AnotherJoinerDoesNotProveHostConnectivity()
    {
        Assert.IsFalse(P2pReadiness.CanJoinHost(new[] { Peer("J|friend", ETConnectionType.P2P) }));
    }

    [TestMethod]
    public void RelayHostIsNotReadyButDirectHostIs()
    {
        Assert.IsFalse(P2pReadiness.CanJoinHost(new[] { Peer("H|host", ETConnectionType.Relay, true) }));
        Assert.IsTrue(P2pReadiness.CanJoinHost(new[] { Peer("H|host", ETConnectionType.P2P, true) }));
    }

    [TestMethod]
    public void UnknownNatIsNotOpenInternet()
    {
        Assert.AreEqual(CliNetTest.NatType.Unknown, CliNetTest.GetNatTypeViaCode(0));
        Assert.AreEqual(CliNetTest.NatType.Unknown, CliNetTest.GetNatTypeViaCode(99));
        Assert.AreEqual(CliNetTest.NatType.Symmetric, CliNetTest.GetNatTypeViaCode(6));
    }

    [TestMethod]
    public void PrivateAndDocumentationIpv6AreNotPublic()
    {
        Assert.IsFalse(CliNetTest.IsPublicIPv6("fe80::1"));
        Assert.IsFalse(CliNetTest.IsPublicIPv6("fd00::1"));
        Assert.IsFalse(CliNetTest.IsPublicIPv6("2001:db8::1"));
        Assert.IsTrue(CliNetTest.IsPublicIPv6("2606:4700:4700::1111"));
    }
}
