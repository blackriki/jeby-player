using EmbyPlayer.Player;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Player.Tests;

[TestClass]
public sealed class AssemblyMarkerTests
{
    [TestMethod]
    public void AssemblyMarker_ExposesExpectedAssemblyName()
    {
        Assert.AreEqual("EmbyPlayer.Player", PlayerAssemblyMarker.AssemblyName);
    }
}
