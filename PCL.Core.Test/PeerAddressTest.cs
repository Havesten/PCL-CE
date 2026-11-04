using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link;

namespace PCL.Core.Test;

[TestClass]
public class PeerAddressTest
{
    [TestMethod]
    public void ParsesIpv4WithPort()
    {
        Assert.IsTrue(PeerAddress.TryParse("1.2.3.4:11010", out var urls, out _));
        CollectionAssert.AreEqual(new[] { "tcp://1.2.3.4:11010", "udp://1.2.3.4:11010" }, urls);
    }

    [TestMethod]
    public void UsesDefaultPortWhenMissing()
    {
        Assert.IsTrue(PeerAddress.TryParse("1.2.3.4", out var urls, out _));
        Assert.AreEqual("tcp://1.2.3.4:11010", urls[0]);
        Assert.AreEqual("udp://1.2.3.4:11010", urls[1]);
    }

    [TestMethod]
    public void ParsesDomain()
    {
        Assert.IsTrue(PeerAddress.TryParse("relay.example.com:25565", out var urls, out _));
        Assert.AreEqual("tcp://relay.example.com:25565", urls[0]);
    }

    [TestMethod]
    public void ParsesBracketedIpv6()
    {
        Assert.IsTrue(PeerAddress.TryParse("[2001:db8::1]:11010", out var urls, out _));
        Assert.AreEqual("tcp://[2001:db8::1]:11010", urls[0]);
        Assert.AreEqual("udp://[2001:db8::1]:11010", urls[1]);
    }

    [TestMethod]
    public void ParsesBareIpv6WithDefaultPort()
    {
        Assert.IsTrue(PeerAddress.TryParse("2001:db8::1", out var urls, out _));
        Assert.AreEqual("tcp://[2001:db8::1]:11010", urls[0]);
    }

    [TestMethod]
    public void KeepsExplicitScheme()
    {
        Assert.IsTrue(PeerAddress.TryParse("udp://1.2.3.4:11010", out var urls, out _));
        Assert.HasCount(1, urls);
        Assert.AreEqual("udp://1.2.3.4:11010", urls[0]);

        Assert.IsTrue(PeerAddress.TryParse("tcp://relay.example.com:7000", out var urls2, out _));
        Assert.HasCount(1, urls2);
    }

    [TestMethod]
    public void RejectsBadInput()
    {
        Assert.IsFalse(PeerAddress.TryParse(null, out _, out _));
        Assert.IsFalse(PeerAddress.TryParse("", out _, out _));
        Assert.IsFalse(PeerAddress.TryParse("   ", out _, out _));
        Assert.IsFalse(PeerAddress.TryParse("1.2.3.4:0", out _, out _));
        Assert.IsFalse(PeerAddress.TryParse("1.2.3.4:70000", out _, out _));
        Assert.IsFalse(PeerAddress.TryParse("1.2.3.4:abc", out _, out _));
        Assert.IsFalse(PeerAddress.TryParse("[2001:db8::1", out _, out _));
        Assert.IsFalse(PeerAddress.TryParse("tcp://", out _, out _));
    }

    [TestMethod]
    public void SplitsHostAndPort()
    {
        Assert.IsTrue(PeerAddress.TrySplitHostPort("10.0.0.5:1234", out var h1, out var p1, out _));
        Assert.AreEqual("10.0.0.5", h1);
        Assert.AreEqual(1234, p1);

        Assert.IsTrue(PeerAddress.TrySplitHostPort("10.0.0.5", out var h2, out var p2, out _));
        Assert.AreEqual("10.0.0.5", h2);
        Assert.AreEqual(PeerAddress.DefaultPort, p2);

        Assert.IsTrue(PeerAddress.TrySplitHostPort("fe80::1", out var h3, out _, out _));
        Assert.AreEqual("fe80::1", h3);
    }

    [TestMethod]
    public void ListenUrlsRespectIpv6Only()
    {
        var v4 = PeerAddress.ListenUrls(11010, false).ToList();
        CollectionAssert.AreEqual(new[] { "tcp://0.0.0.0:11010", "udp://0.0.0.0:11010" }, v4);

        var v6 = PeerAddress.ListenUrls(11010, true).ToList();
        CollectionAssert.AreEqual(new[] { "tcp://[::]:11010", "udp://[::]:11010" }, v6);
    }
}
