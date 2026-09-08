using EmbyPlayer.UI.Navigation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class NavigationServiceTests
{
    [TestMethod]
    public void Constructor_SetsDefaultPageToServerConnection()
    {
        var navigationService = new NavigationService();

        Assert.AreEqual(AppPage.ServerConnection, navigationService.CurrentPage);
    }

    [TestMethod]
    public void NavigateTo_ChangesCurrentPage()
    {
        var navigationService = new NavigationService();

        navigationService.NavigateTo(AppPage.Home);

        Assert.AreEqual(AppPage.Home, navigationService.CurrentPage);
    }

    [TestMethod]
    public void NavigateTo_WithParameter_RaisesPageChangedParameter()
    {
        var navigationService = new NavigationService();
        object? parameter = null;
        navigationService.CurrentPageChanged += (_, args) => parameter = args.Parameter;

        navigationService.NavigateTo(AppPage.Library, "library-1");

        Assert.AreEqual(AppPage.Library, navigationService.CurrentPage);
        Assert.AreEqual("library-1", parameter);
    }

    [TestMethod]
    public void NavigateTo_SamePageRepeatedly_DoesNotThrowOrRaiseDuplicateChange()
    {
        var navigationService = new NavigationService();
        var eventCount = 0;
        navigationService.CurrentPageChanged += (_, _) => eventCount++;

        navigationService.NavigateTo(AppPage.Home);
        navigationService.NavigateTo(AppPage.Home);
        navigationService.NavigateTo(AppPage.Home);

        Assert.AreEqual(AppPage.Home, navigationService.CurrentPage);
        Assert.AreEqual(1, eventCount);
    }

    [TestMethod]
    public void NavigateTo_SamePageWithParameter_RaisesChangeForParameterizedReload()
    {
        var navigationService = new NavigationService();
        object? parameter = null;
        var eventCount = 0;
        navigationService.CurrentPageChanged += (_, args) =>
        {
            eventCount++;
            parameter = args.Parameter;
        };

        navigationService.NavigateTo(AppPage.Detail, "item-1");
        navigationService.NavigateTo(AppPage.Detail, "episode-1");

        Assert.AreEqual(AppPage.Detail, navigationService.CurrentPage);
        Assert.AreEqual(2, eventCount);
        Assert.AreEqual("episode-1", parameter);
    }
}
