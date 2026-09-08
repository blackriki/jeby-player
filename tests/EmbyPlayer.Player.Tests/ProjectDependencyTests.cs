using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Player.Tests;

[TestClass]
public sealed class ProjectDependencyTests
{
    [TestMethod]
    public async Task PlayerProject_DoesNotReferenceMpvOrPlayerLibraries()
    {
        var repositoryRoot = FindRepositoryRoot();
        var playerProjectPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.Player", "EmbyPlayer.Player.csproj");
        var projectContent = await File.ReadAllTextAsync(playerProjectPath);

        Assert.IsFalse(projectContent.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectContent.Contains("mpv", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectContent.Contains("vlc", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(projectContent.Contains("libvlc", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CoreAndEmbyProjects_DoNotReferencePlayerProject()
    {
        var repositoryRoot = FindRepositoryRoot();
        var coreProjectPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.Core", "EmbyPlayer.Core.csproj");
        var embyProjectPath = Path.Combine(repositoryRoot, "src", "EmbyPlayer.Emby", "EmbyPlayer.Emby.csproj");

        var coreProjectContent = await File.ReadAllTextAsync(coreProjectPath);
        var embyProjectContent = await File.ReadAllTextAsync(embyProjectPath);

        Assert.IsFalse(coreProjectContent.Contains("EmbyPlayer.Player.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(embyProjectContent.Contains("EmbyPlayer.Player.csproj", StringComparison.OrdinalIgnoreCase));
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
