using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class ApplicationIdentityTests
{
    [DataTestMethod]
    [DataRow("1.0.0-beta.1+abc123", "1.0.0-beta.1", "abc123")]
    [DataRow("1.2.3+build.42.sha.abc123", "1.2.3", "build.42.sha.abc123")]
    [DataRow("1.0.0-beta.1", "1.0.0-beta.1", "本地构建")]
    [DataRow("1.0.0+", "1.0.0", "本地构建")]
    public void ParseVersion_PreservesReleaseLabelAndSeparatesBuildMetadata(
        string fullVersion, string expectedVersion, string expectedBuild)
    {
        var (version, build) = ApplicationIdentity.ParseVersion(fullVersion);

        Assert.AreEqual(expectedVersion, version);
        Assert.AreEqual(expectedBuild, build);
    }

    [TestMethod]
    public void Identity_UsesCoreAssemblyInformationalVersion()
    {
        var assemblyVersion = typeof(ApplicationIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.AreEqual("Jeby Player", ApplicationIdentity.Name);
        Assert.AreEqual(assemblyVersion, ApplicationIdentity.FullVersion);
        var expected = ApplicationIdentity.ParseVersion(assemblyVersion);
        Assert.AreEqual(expected.Version, ApplicationIdentity.Version);
        Assert.AreEqual(expected.Build, ApplicationIdentity.Build);
    }
}
