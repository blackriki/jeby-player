using EmbyPlayer.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class AssemblyMarkerTests
{
    [TestMethod]
    public void AssemblyMarker_ExposesExpectedAssemblyName()
    {
        Assert.AreEqual("EmbyPlayer.Core", CoreAssemblyMarker.AssemblyName);
    }
}
