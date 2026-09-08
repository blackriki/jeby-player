using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class ApplicationCompositionTests
{
    [TestMethod]
    public async Task ProductionEmbyServicesShareSingleDiagnosticHttpClient()
    {
        var root = FindRepositoryRoot();
        var compositionPath = Path.Combine(root, "src", "EmbyPlayer.App", "MainWindow.xaml.cs");
        var composition = await File.ReadAllTextAsync(compositionPath);

        StringAssert.Contains(
            composition,
            "httpClient = new HttpClient(new EmbyApiDiagnosticHandler(new HttpClientHandler()));");
        Assert.AreEqual(1, CountOccurrences(composition, "new HttpClient("));
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(fragment, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += fragment.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new AssertFailedException("Repository root was not found.");
    }
}
