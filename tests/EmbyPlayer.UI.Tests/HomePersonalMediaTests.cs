using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.WatchLater;
using EmbyPlayer.UI.Navigation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class HomeViewModelTests
{
    [TestMethod]
    public async Task WatchLaterPreview_RefreshesOnReturnWhileNetworkSnapshotIsFresh()
    {
        var context = CreateContext();
        await context.ViewModel.NavigateAsync();
        Assert.IsFalse(context.ViewModel.HasWatchLaterSection);
        context.WatchLaterStore.Items.Add(new("saved", "Saved", null, "Movie", 2025));
        await context.ViewModel.NavigateAsync();
        Assert.AreEqual(1, context.HomeService.LoadCallCount);
        Assert.IsTrue(context.ViewModel.HasWatchLaterSection);
        Assert.AreEqual("saved", context.ViewModel.WatchLaterPreview.Single().ItemId);
        object? parameter = null;
        context.NavigationService.CurrentPageChanged += (_, args) => parameter = args.Parameter;
        context.ViewModel.OpenWatchLaterItemCommand.Execute(context.ViewModel.WatchLaterPreview.Single());
        Assert.AreEqual(AppPage.Detail, context.NavigationService.CurrentPage);
        Assert.AreEqual(new DetailNavigationParameter("saved", AppPage.Home), parameter);
        context.WatchLaterStore.Items.Clear();
        await context.ViewModel.NavigateAsync();
        Assert.IsFalse(context.ViewModel.HasWatchLaterSection);
    }

    [TestMethod]
    public async Task WatchLaterPreview_LimitsPostersAndRecoversFromLocalReadFailure()
    {
        var context = CreateContext();
        context.WatchLaterStore.Load = (_, _) => throw new System.IO.IOException("Unavailable");
        await context.ViewModel.NavigateAsync();
        Assert.IsTrue(context.ViewModel.HasLoadedSuccessfully);
        Assert.IsTrue(context.ViewModel.HasWatchLaterPreviewError);
        Assert.IsTrue(context.ViewModel.HasWatchLaterSection);
        context.WatchLaterStore.Load = null;
        for (var index = 0; index < 20; index++)
            context.WatchLaterStore.Items.Add(new($"saved-{index}", "Saved", null, "Movie", null));
        await context.ViewModel.NavigateAsync();
        Assert.IsFalse(context.ViewModel.HasWatchLaterPreviewError);
        Assert.AreEqual(12, context.ViewModel.WatchLaterPreview.Count);
    }

    [TestMethod]
    public async Task WatchLaterPreview_IgnoresOldAccountReadAfterSessionChange()
    {
        var context = CreateContext();
        var pending = new TaskCompletionSource<IReadOnlyList<WatchLaterItem>>();
        context.WatchLaterStore.Load = (_, _) => pending.Task;
        var oldLoad = context.ViewModel.NavigateAsync();
        context.CurrentSessionService.SetSession(new AuthSession("http://media.local:8096", "new-token", "user-2", "Other", "server-1"));
        context.WatchLaterStore.Load = null;
        await context.ViewModel.NavigateAsync();
        pending.SetResult([new("old", "Old account item", null, "Movie", null)]);
        await oldLoad;
        Assert.IsFalse(context.ViewModel.HasWatchLaterSection);
    }

    [TestMethod]
    public async Task WatchLaterPreview_ResetRejectsLateCompletionAndOldCardCommands()
    {
        var context = CreateContext();
        var saved = new WatchLaterItem("saved", "Saved", null, "Movie", null);
        context.WatchLaterStore.Items.Add(saved);
        await context.ViewModel.NavigateAsync();
        var pending = new TaskCompletionSource<IReadOnlyList<WatchLaterItem>>();
        context.WatchLaterStore.Load = (_, _) => pending.Task;
        var load = context.ViewModel.NavigateAsync();
        context.ViewModel.ResetPersonalMedia();
        pending.SetResult([saved]);
        await load;
        Assert.IsFalse(context.ViewModel.HasWatchLaterSection);
        var previousPage = context.NavigationService.CurrentPage;
        context.ViewModel.OpenWatchLaterItemCommand.Execute(saved);
        Assert.AreEqual(previousPage, context.NavigationService.CurrentPage);
    }

    [TestMethod]
    public void HomeQueueEntry_TracksPendingItemsAndDisappearsOnSessionReset()
    {
        var context = CreateContext();
        context.Queue.SetSession(context.Session.ServerBase, context.Session.UserId);
        var notifications = new List<string?>();
        context.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        Assert.IsFalse(context.ViewModel.HasPendingQueue);
        context.Queue.Add(new("first", "First", "Movie"));
        Assert.IsTrue(context.ViewModel.HasPendingQueue);
        Assert.AreEqual("待播 1 项", context.ViewModel.PendingQueueText);
        context.Queue.SetCurrent(context.Queue.Snapshot.Pending.Single(), context.Queue.Snapshot.SessionVersion);
        Assert.IsFalse(context.ViewModel.HasPendingQueue, "The playing item does not count as pending.");
        context.Queue.Add(new("second", "Second", "Movie"));
        context.Queue.SetSession(null, null);
        Assert.IsFalse(context.ViewModel.HasPendingQueue);
        CollectionAssert.Contains(notifications, nameof(context.ViewModel.HasPendingQueue));
    }
}
