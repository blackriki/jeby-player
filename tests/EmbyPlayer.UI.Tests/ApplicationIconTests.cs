using System.Buffers.Binary;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class ApplicationIconTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [TestMethod]
    public void AppProject_WiresApplicationIconAndKeepsCleanMasterSource()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "EmbyPlayer.App", "EmbyPlayer.App.csproj");
        var project = XDocument.Load(projectPath);

        var applicationIcon = project.Descendants("ApplicationIcon").Single().Value;
        Assert.AreEqual(@"Assets\app-icon.ico", applicationIcon);
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(projectPath)!, applicationIcon)));

        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(projectPath)!, @"Assets\app-icon.png")));
        Assert.IsFalse(project.Descendants("Resource").Any(element => string.Equals(
            element.Attribute("Include")?.Value,
            @"Assets\app-icon.png",
            StringComparison.Ordinal)));
        Assert.IsFalse(project.Descendants("Content").Any(element => string.Equals(
            element.Attribute("Include")?.Value,
            @"Assets\app-icon.png",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApplicationIcon_ContainsEveryRequired32BitSize()
    {
        var root = FindRepositoryRoot();
        var iconPath = Path.Combine(root, "src", "EmbyPlayer.App", "Assets", "app-icon.ico");
        var bytes = File.ReadAllBytes(iconPath);

        Assert.IsTrue(bytes.Length >= 6);
        Assert.AreEqual((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)));
        Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));

        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        Assert.AreEqual(ExpectedSizes.Length, (int)count);

        var sizes = new List<int>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = 6 + index * 16;
            Assert.IsTrue(bytes.Length >= offset + 16);

            var width = bytes[offset] == 0 ? 256 : bytes[offset];
            var height = bytes[offset + 1] == 0 ? 256 : bytes[offset + 1];
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6, 2));
            var imageLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8, 4));
            var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12, 4));

            Assert.AreEqual(width, height);
            Assert.AreEqual((ushort)32, bitCount);
            Assert.IsTrue(imageLength > 0);
            Assert.IsTrue((ulong)imageOffset + imageLength <= (ulong)bytes.Length);
            sizes.Add(width);
        }

        CollectionAssert.AreEqual(ExpectedSizes, sizes.Order().ToArray());
    }

    [TestMethod]
    public async Task AppResources_DoNotRetainObsoleteWindowIconPlaceholder()
    {
        var root = FindRepositoryRoot();
        var appXaml = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "EmbyPlayer.App", "App.xaml"));

        Assert.IsFalse(appXaml.Contains("AppWindowIcon", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
