using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class DevelopmentSafetyScriptTests
{
    [DataTestMethod]
    [DataRow("settings.json")]
    [DataRow(".pfx")]
    [DataRow("ls-files --cached --others --exclude-standard")]
    [DataRow("$maximumTextBytes = 4MB")]
    [DataRow("Test-ContainsNullByte")]
    [DataRow("PayloadRoot")]
    [DataRow("AdditionalFile")]
    public async Task CheckSensitiveFilesScript_EnumeratesRepositoryAndPayloadTextFailClosed(string expectedPattern)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "check-sensitive-files.ps1");
        var script = await File.ReadAllTextAsync(scriptPath);

        StringAssert.Contains(script, expectedPattern);
    }

    [DataTestMethod]
    [DataRow("api_key\\s*=")]
    [DataRow("AccessToken[\"'']?\\s*[:=]")]
    [DataRow("X-Emby-Token")]
    [DataRow("hardcoded private server url")]
    public async Task CheckSensitiveFilesScript_CoversSensitiveContentPatterns(string expectedPattern)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "check-sensitive-files.ps1");
        var script = await File.ReadAllTextAsync(scriptPath);

        StringAssert.Contains(script, expectedPattern);
    }

    [DataTestMethod]
    [DataRow("UiOnly")]
    [DataRow("CoreOnly")]
    [DataRow("EmbyOnly")]
    [DataRow("FullNoPlayer")]
    public async Task BuildTestScript_SupportsFocusedTestSwitches(string expectedSwitch)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "build-test.ps1");
        var script = await File.ReadAllTextAsync(scriptPath);

        StringAssert.Contains(script, expectedSwitch);
    }

    [TestMethod]
    public async Task RunAppScript_BuildsAndOnlyStopsCurrentOutputProcess()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "run-app.ps1");
        var script = await File.ReadAllTextAsync(scriptPath);

        StringAssert.Contains(script, "dotnet build");
        StringAssert.Contains(script, "EmbyPlayer.App");
        StringAssert.Contains(script, "StartsWith($outputDirFull");
        StringAssert.Contains(script, "Start-Process");
    }

    [TestMethod]
    public async Task CleanBuildArtifactsScript_SupportsDryRunAndOnlyTargetsBinObj()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "clean-build-artifacts.ps1");
        var script = await File.ReadAllTextAsync(scriptPath);

        StringAssert.Contains(script, "DryRun");
        StringAssert.Contains(script, "bin");
        StringAssert.Contains(script, "obj");
        Assert.IsFalse(script.Contains("runtimes", StringComparison.OrdinalIgnoreCase));
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
