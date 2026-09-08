using EmbyPlayer.Emby;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class AssemblyMarkerTests
{
    [TestMethod]
    public void AssemblyMarker_ExposesExpectedAssemblyName()
    {
        Assert.AreEqual("EmbyPlayer.Emby", EmbyAssemblyMarker.AssemblyName);
    }
}
