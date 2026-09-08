using EmbyPlayer.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class AssemblyMarkerTests
{
    [TestMethod]
    public void AssemblyMarker_ExposesExpectedAssemblyName()
    {
        Assert.AreEqual("EmbyPlayer.UI", UiAssemblyMarker.AssemblyName);
    }
}
