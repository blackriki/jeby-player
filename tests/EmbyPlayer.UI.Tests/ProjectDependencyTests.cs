using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class ProjectDependencyTests
{
    [TestMethod]
    public async Task UiProject_DoesNotReferenceEmbyProject()
    {
        var repositoryRoot = FindRepositoryRoot();
        var uiProjectPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "EmbyPlayer.UI.csproj");

        var projectContent = await File.ReadAllTextAsync(uiProjectPath);

        Assert.IsFalse(projectContent.Contains("EmbyPlayer.Emby.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task UiProject_DoesNotReferenceAppProjectOrSecurityImplementation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var uiProjectPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.UI", "EmbyPlayer.UI.csproj");
        var projectContent = await File.ReadAllTextAsync(uiProjectPath);

        Assert.IsFalse(projectContent.Contains("EmbyPlayer.App.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectContent.Contains("App\\Security", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectContent.Contains("WindowsCredentialAuthSessionStore", StringComparison.OrdinalIgnoreCase));
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
