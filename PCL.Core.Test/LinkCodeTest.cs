using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link;

namespace PCL.Core.Test;

[TestClass]
public class LinkCodeTest
{
    [TestMethod]
    public void BuildAndParseRoundTrip()
    {
        foreach (var port in new[] { 1, 25565, 25566, 32767, 32768, 65535 })
        {
            var code = LinkCode.Build("ALB4L69D", "8M2V29", port);
            Assert.IsTrue(LinkCode.TryParse(code, out var name, out var secret, out var parsed),
                $"端口 {port} 生成的码无法解析：{code}");
            Assert.AreEqual("ALB4L69D", name);
            Assert.AreEqual("8M2V29", secret);
            Assert.AreEqual(port, parsed);
        }
    }

    [TestMethod]
    public void CodeHasFixedShape()
    {
        var code = LinkCode.Build("ALB4L69D", "8M2V29", 25565);
        Assert.AreEqual("ALB4-L69D-8M2V-292SYX", code);
        Assert.AreEqual(18, code.Replace("-", "").Length);
        Assert.AreEqual(4, code.Split('-').Length); // 4-4-4-6，四段三个连字符
    }

    [TestMethod]
    public void ParseToleratesCaseSpaceAndDash()
    {
        Assert.IsTrue(LinkCode.TryParse("alb4-l69d-8m2v-292syx", out _, out _, out var p1));
        Assert.AreEqual(25565, p1);

        Assert.IsTrue(LinkCode.TryParse("  ALB4L69D8M2V292SYX  ", out _, out _, out var p2));
        Assert.AreEqual(25565, p2);

        Assert.IsTrue(LinkCode.TryParse("ALB4 L69D 8M2V 292SYX", out _, out _, out var p3));
        Assert.AreEqual(25565, p3);
    }

    [TestMethod]
    public void ParseRejectsBrokenCodes()
    {
        Assert.IsFalse(LinkCode.TryParse(null, out _, out _, out _));
        Assert.IsFalse(LinkCode.TryParse("", out _, out _, out _));
        Assert.IsFalse(LinkCode.TryParse("   ", out _, out _, out _));

        // 少一位
        Assert.IsFalse(LinkCode.TryParse("ALB4-L69D-8M2V-292SY", out _, out _, out _));
        // 多一位
        Assert.IsFalse(LinkCode.TryParse("ALB4-L69D-8M2V-292SYXX", out _, out _, out _));
        // 含字母表外的字符（I、O、0、1 都被排除）
        Assert.IsFalse(LinkCode.TryParse("ALB4-L69D-8M2V-292SYI", out _, out _, out _));
        // 端口段为 0
        Assert.IsFalse(LinkCode.TryParse("ALB4L69D8M2V292222", out _, out _, out _));
    }

    [TestMethod]
    public void RandomPartsFitAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var name = LinkCode.NewName();
            var secret = LinkCode.NewSecret();
            Assert.AreEqual(LinkCode.NameLength, name.Length);
            Assert.AreEqual(LinkCode.SecretLength, secret.Length);

            var code = LinkCode.Build(name, secret, 25565);
            Assert.IsTrue(LinkCode.TryParse(code, out var n, out var s, out _));
            Assert.AreEqual(name, n);
            Assert.AreEqual(secret, s);
        }
    }
}
