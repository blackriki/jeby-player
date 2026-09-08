using EmbyPlayer.Core.Servers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class ServerUrlNormalizerTests
{
    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Normalize_EmptyUrl_Fails(string serverUrl)
    {
        var result = ServerUrlNormalizer.Normalize(serverUrl);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ServerConnectionError.EmptyServerUrl, result.Error);
    }

    [TestMethod]
    public void Normalize_UrlWithoutScheme_UsesHttp()
    {
        var result = ServerUrlNormalizer.Normalize("192.168.1.100:8096");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("http://192.168.1.100:8096", result.Server!.ServerBase);
    }

    [TestMethod]
    public void Normalize_HttpUrl_KeepsHttp()
    {
        var result = ServerUrlNormalizer.Normalize("http://media.local:8096");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("http://media.local:8096", result.Server!.ServerBase);
    }

    [TestMethod]
    public void Normalize_HttpsUrl_KeepsHttps()
    {
        var result = ServerUrlNormalizer.Normalize("https://media.example.com");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("https://media.example.com", result.Server!.ServerBase);
    }

    [TestMethod]
    public void Normalize_FtpUrl_FailsWithUnsupportedScheme()
    {
        var result = ServerUrlNormalizer.Normalize("ftp://media.example.com");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ServerConnectionError.UnsupportedScheme, result.Error);
    }

    [TestMethod]
    public void Normalize_TrailingSlash_RemovesSlash()
    {
        var result = ServerUrlNormalizer.Normalize("http://media.local:8096/");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("http://media.local:8096", result.Server!.ServerBase);
    }

    [TestMethod]
    public void Normalize_ServerBase_DoesNotForceEmbyPath()
    {
        var result = ServerUrlNormalizer.Normalize("http://media.local:8096");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("http://media.local:8096", result.Server!.ServerBase);
        Assert.IsFalse(result.Server.ServerBase.EndsWith("/emby", StringComparison.OrdinalIgnoreCase));
    }
}
