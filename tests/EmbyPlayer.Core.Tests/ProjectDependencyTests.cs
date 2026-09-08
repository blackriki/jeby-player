using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class ProjectDependencyTests
{
    [TestMethod]
    public async Task CoreProject_DoesNotReferenceImplementationProjects()
    {
        var coreProject = await ReadProjectAsync("EmbyPlayer.Core");

        AssertDoesNotReference(coreProject, "EmbyPlayer.UI.csproj");
        AssertDoesNotReference(coreProject, "EmbyPlayer.Emby.csproj");
        AssertDoesNotReference(coreProject, "EmbyPlayer.Player.csproj");
        AssertDoesNotReference(coreProject, "EmbyPlayer.App.csproj");
    }

    [TestMethod]
    public async Task EmbyProject_DoesNotReferenceUiOrPlayerProjects()
    {
        var embyProject = await ReadProjectAsync("EmbyPlayer.Emby");

        AssertDoesNotReference(embyProject, "EmbyPlayer.UI.csproj");
        AssertDoesNotReference(embyProject, "EmbyPlayer.Player.csproj");
    }

    [TestMethod]
    public async Task PlayerProject_DoesNotReferenceUiOrEmbyProjects()
    {
        var playerProject = await ReadProjectAsync("EmbyPlayer.Player");

        AssertDoesNotReference(playerProject, "EmbyPlayer.UI.csproj");
        AssertDoesNotReference(playerProject, "EmbyPlayer.Emby.csproj");
    }

    private static async Task<string> ReadProjectAsync(string projectName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", projectName, $"{projectName}.csproj");
        return await File.ReadAllTextAsync(projectPath);
    }

    private static void AssertDoesNotReference(string projectContent, string forbiddenProject)
    {
        Assert.IsFalse(
            projectContent.Contains(forbiddenProject, StringComparison.OrdinalIgnoreCase),
            $"Project must not reference {forbiddenProject}.");
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
