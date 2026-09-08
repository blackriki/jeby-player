using EmbyPlayer.Core.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class CurrentSessionServiceTests
{
    [TestMethod]
    public void CurrentSession_DefaultsToNull()
    {
        var service = new CurrentSessionService();

        Assert.IsNull(service.CurrentSession);
    }

    [TestMethod]
    public void SetSession_StoresRuntimeSession()
    {
        var service = new CurrentSessionService();
        var session = new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "测试用户",
            "server-1");

        service.SetSession(session);

        Assert.AreSame(session, service.CurrentSession);
    }

    [TestMethod]
    public void ClearSession_ClearsRuntimeSession()
    {
        var service = new CurrentSessionService();
        service.SetSession(new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "Test User",
            "server-1"));

        service.ClearSession();

        Assert.IsNull(service.CurrentSession);
    }

    [TestMethod]
    public void AuthSession_AllowsEmptyServerId()
    {
        var session = new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "测试用户",
            string.Empty);

        Assert.AreEqual(string.Empty, session.ServerId);
    }
}
