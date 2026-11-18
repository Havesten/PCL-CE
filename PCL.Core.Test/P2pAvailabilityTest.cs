using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Scaffolding.EasyTier;

namespace PCL.Core.Test;

[TestClass]
public class P2pAvailabilityTest
{
    private static CliNetTest.NetStatus Status(CliNetTest.NatType udp = CliNetTest.NatType.Unknown,
        CliNetTest.NatType tcp = CliNetTest.NatType.Unknown, bool ipv6 = false) => new()
    {
        UdpNatType = udp,
        TcpNatType = tcp,
        SupportIPv6 = ipv6
    };

    [TestMethod]
    [DataRow(LinkIpMode.Auto)]
    [DataRow(LinkIpMode.Ipv4Only)]
    [DataRow(LinkIpMode.Ipv6Only)]
    public void MissingDetectionDoesNotProveP2pUnavailable(LinkIpMode mode)
    {
        var result = P2pReadiness.Assess(null, mode);
        Assert.IsTrue(result.CanAttempt);
        Assert.AreEqual(P2pAvailability.Unknown, result.Availability);
    }

    [TestMethod]
    [DataRow(LinkIpMode.Auto)]
    [DataRow(LinkIpMode.Ipv4Only)]
    public void UnknownNatDoesNotDisableIpv4Attempt(LinkIpMode mode)
    {
        var result = P2pReadiness.Assess(Status(), mode);
        Assert.IsTrue(result.CanAttempt);
        Assert.AreEqual(P2pAvailability.Unknown, result.Availability);
    }

    [TestMethod]
    public void Ipv6OnlyRequiresAnObservedPublicIpv6Address()
    {
        var status = Status(CliNetTest.NatType.OpenInternet, CliNetTest.NatType.OpenInternet);
        var unavailable = P2pReadiness.Assess(status, LinkIpMode.Ipv6Only);
        Assert.IsFalse(unavailable.CanAttempt);
        Assert.AreEqual(P2pAvailability.Unavailable, unavailable.Availability);
        Assert.IsTrue(P2pReadiness.Assess(status, LinkIpMode.Auto).CanAttempt);
        Assert.IsTrue(P2pReadiness.Assess(status, LinkIpMode.Ipv4Only).CanAttempt);
    }

    [TestMethod]
    public void PublicIpv6OffersAPossiblePathWithoutIpv4NatResults()
    {
        var status = Status(ipv6: true);
        Assert.AreEqual(P2pAvailability.Possible, P2pReadiness.Assess(status, LinkIpMode.Auto).Availability);
        Assert.AreEqual(P2pAvailability.Possible, P2pReadiness.Assess(status, LinkIpMode.Ipv6Only).Availability);
        Assert.AreEqual(P2pAvailability.Unknown, P2pReadiness.Assess(status, LinkIpMode.Ipv4Only).Availability);
    }

    [TestMethod]
    [DataRow(CliNetTest.NatType.Symmetric)]
    [DataRow(CliNetTest.NatType.SymmetricEasy)]
    public void StrictNatStillPermitsAConnectionAttempt(CliNetTest.NatType type)
    {
        var result = P2pReadiness.Assess(Status(type, type), LinkIpMode.Ipv4Only);
        Assert.IsTrue(result.CanAttempt);
        Assert.AreEqual(P2pAvailability.Possible, result.Availability);
    }

    [TestMethod]
    [DataRow(CliNetTest.NatType.SymmetricFirewall)]
    [DataRow(CliNetTest.NatType.UdpBlocked)]
    public void UdpRestrictionsDoNotDisableAKnownTcpPath(CliNetTest.NatType type)
    {
        var result = P2pReadiness.Assess(Status(type, CliNetTest.NatType.FullCone), LinkIpMode.Auto);
        Assert.IsTrue(result.CanAttempt);
        Assert.AreEqual(P2pAvailability.Possible, result.Availability);
        Assert.IsTrue(result.Summary.Contains("TCP"));
    }

    [TestMethod]
    public void FirewallClassificationsDoNotProveEveryTransportIsBlocked()
    {
        var result = P2pReadiness.Assess(Status(CliNetTest.NatType.SymmetricFirewall,
            CliNetTest.NatType.SymmetricFirewall), LinkIpMode.Auto);
        Assert.IsTrue(result.CanAttempt);
        Assert.AreEqual(P2pAvailability.Unknown, result.Availability);
    }
}
