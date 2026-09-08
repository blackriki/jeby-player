using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.WatchLater;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class WatchLaterViewModelTests
{
    [TestMethod]
    public async Task InitialLoadShowsLoadingThenEmptyAndCanRefreshFromLocalStore()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<IReadOnlyList<WatchLaterItem>>();
        context.Store.Load = (_, _) => pending.Task;
        var loading = context.ViewModel.LoadAsync();
        Assert.IsTrue(context.ViewModel.IsInitialLoading);
        Assert.IsFalse(context.ViewModel.IsEmpty);
        Assert.IsFalse(context.ViewModel.RefreshCommand.CanExecute(null));
        pending.SetResult(Array.Empty<WatchLaterItem>());
        await loading;
        Assert.IsTrue(context.ViewModel.IsEmpty);
        context.Store.Load = null;
        context.Store.Items.Add(Item("one"));
        await Execute(context.ViewModel.RefreshCommand);
        Assert.AreEqual("one", context.ViewModel.Items.Single().ItemId);
        Assert.IsFalse(context.ViewModel.IsEmpty);
    }

    [TestMethod]
    public async Task OpeningEpisodePreservesExactIdAndWatchLaterReturnPage()
    {
        var context = new Context();
        context.Store.Items.Add(Item("episode-8") with { MediaType = "Episode" });
        await context.ViewModel.LoadAsync();
        object? parameter = null;
        context.Navigation.CurrentPageChanged += (_, e) => parameter = e.Parameter;
        context.ViewModel.OpenMediaCommand.Execute(context.ViewModel.Items[0]);
        Assert.AreEqual(AppPage.Detail, context.Navigation.CurrentPage);
        var detail = (DetailNavigationParameter)parameter!;
        Assert.AreEqual("episode-8", detail.ItemId);
        Assert.AreEqual(AppPage.WatchLater, detail.ReturnPage);
        context.ViewModel.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.Home, context.Navigation.CurrentPage);
    }

    [TestMethod]
    public async Task RemoveWaitsForDurableSuccessAndFailureRetryKeepsItsOriginalItem()
    {
        var context = new Context();
        var one = Item("one");
        var two = Item("two");
        context.Store.Items.AddRange(new[] { one, two });
        await context.ViewModel.LoadAsync();
        var pending = new TaskCompletionSource();
        context.Store.Remove = (_, _, _) => pending.Task;
        var removing = Execute(context.ViewModel.RemoveCommand, one);
        Assert.IsTrue(context.ViewModel.IsRefreshing);
        Assert.AreEqual(2, context.ViewModel.Items.Count);
        Assert.IsFalse(context.ViewModel.RemoveCommand.CanExecute(two));
        pending.SetException(new IOException("disk unavailable"));
        await removing;
        Assert.AreEqual(2, context.ViewModel.Items.Count);
        Assert.IsTrue(context.ViewModel.IsErrorVisible);
        context.Store.Remove = null;
        await Execute(context.ViewModel.RetryCommand);
        Assert.AreEqual("two", context.ViewModel.Items.Single().ItemId);
        Assert.AreEqual("one", context.Store.LastRemovedId);
        Assert.IsFalse(context.ViewModel.IsErrorVisible);
    }

    [TestMethod]
    public async Task LoadFailureCanRetryWithoutPretendingListIsEmpty()
    {
        var context = new Context();
        context.Store.Load = (_, _) => throw new UnauthorizedAccessException();
        await context.ViewModel.LoadAsync();
        Assert.IsTrue(context.ViewModel.IsErrorVisible);
        Assert.IsFalse(context.ViewModel.IsEmpty);
        Assert.IsFalse(context.ViewModel.IsCorrupted);
        Assert.AreEqual(AppPage.ServerConnection, context.Navigation.CurrentPage);
        context.Store.Load = null;
        await Execute(context.ViewModel.RetryCommand);
        Assert.IsTrue(context.ViewModel.IsEmpty);
    }

    [TestMethod]
    public async Task CorruptedListOnlyResetsAfterUserOpensAndConfirmsPrompt()
    {
        var context = new Context();
        context.Store.Load = (_, _) => throw new InvalidDataException();
        await context.ViewModel.LoadAsync();
        Assert.IsTrue(context.ViewModel.IsCorrupted);
        Assert.AreEqual(0, context.Store.ResetCalls);
        await Execute(context.ViewModel.ConfirmResetCommand);
        Assert.AreEqual(0, context.Store.ResetCalls);
        context.ViewModel.ResetCorruptedCommand.Execute(null);
        Assert.IsTrue(context.ViewModel.IsResetConfirmationVisible);
        context.ViewModel.CancelResetCommand.Execute(null);
        Assert.IsFalse(context.ViewModel.IsResetConfirmationVisible);
        Assert.AreEqual(0, context.Store.ResetCalls);
        context.ViewModel.ResetCorruptedCommand.Execute(null);
        context.Store.Load = null;
        await Execute(context.ViewModel.ConfirmResetCommand);
        Assert.AreEqual(1, context.Store.ResetCalls);
        Assert.IsTrue(context.ViewModel.IsEmpty);
        Assert.IsFalse(context.ViewModel.IsResetConfirmationVisible);
    }

    [TestMethod]
    public async Task ReturningReloadsStoreChangesButPreservesScroll()
    {
        var context = new Context();
        context.Store.Items.Add(Item("one"));
        await context.ViewModel.LoadAsync();
        context.ViewModel.ScrollOffset = 580;
        context.ViewModel.Deactivate();
        context.Store.Items.Add(Item("two"));
        await context.ViewModel.LoadAsync();
        Assert.AreEqual(2, context.ViewModel.Items.Count);
        Assert.AreEqual(580d, context.ViewModel.ScrollOffset);
    }

    [TestMethod]
    public async Task LeavingCancelsAndIgnoresLateLoadWithoutShowingError()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<IReadOnlyList<WatchLaterItem>>();
        context.Store.Load = (_, _) => pending.Task;
        var loading = context.ViewModel.LoadAsync();
        var token = context.Store.LastToken;
        context.ViewModel.Deactivate();
        Assert.IsTrue(token.IsCancellationRequested);
        pending.SetResult(new[] { Item("late") });
        await loading;
        Assert.AreEqual(0, context.ViewModel.Items.Count);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.IsNull(context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task NewSessionClearsOldCardsAndIgnoresPendingResult()
    {
        var context = new Context();
        context.Store.Items.Add(Item("old"));
        await context.ViewModel.LoadAsync();
        context.ViewModel.ScrollOffset = 400;
        var pending = new TaskCompletionSource<IReadOnlyList<WatchLaterItem>>();
        context.Store.Load = (_, _) => pending.Task;
        var oldLoad = context.ViewModel.LoadAsync();
        context.Sessions.SetSession(Session("new-user"));
        context.Store.Load = null;
        context.Store.Items.Clear();
        context.Store.Items.Add(Item("new"));
        await context.ViewModel.LoadAsync();
        pending.SetException(new IOException());
        await oldLoad;
        Assert.AreEqual("new", context.ViewModel.Items.Single().ItemId);
        Assert.AreEqual(0d, context.ViewModel.ScrollOffset);
        Assert.IsNull(context.ViewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task StaleRemoveCommandCannotMutateNewAccountAndResetCancelsCurrentWork()
    {
        var context = new Context();
        var item = Item("one");
        context.Store.Items.Add(item);
        await context.ViewModel.LoadAsync();
        context.Sessions.SetSession(Session("new-user"));
        await Execute(context.ViewModel.RemoveCommand, item);
        Assert.IsNull(context.Store.LastRemovedId);
        var pending = new TaskCompletionSource<IReadOnlyList<WatchLaterItem>>();
        context.Store.Load = (_, _) => pending.Task;
        var loading = context.ViewModel.LoadAsync();
        var token = context.Store.LastToken;
        context.ViewModel.ResetSession();
        pending.SetResult(new[] { item });
        await loading;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(0, context.ViewModel.Items.Count);
    }

    [TestMethod]
    public async Task MissingSessionNavigatesToLoginWithoutReadingStorage()
    {
        var context = new Context();
        context.Sessions.ClearSession();
        await context.ViewModel.LoadAsync();
        Assert.AreEqual(AppPage.Login, context.Navigation.CurrentPage);
        Assert.AreEqual("请先登录", context.LoginError);
        Assert.AreEqual(0, context.Store.LoadCalls);
    }

    internal static WatchLaterItem Item(string id) => new(id, "作品 " + id, null, "Movie", 2024);
    internal static AuthSession Session(string user = "user-1") => new("http://media.local", "test-token", user, "User", "server-1");
    private static Task Execute(System.Windows.Input.ICommand command, object? parameter = null)
        => ((AsyncRelayCommand)command).ExecuteAsync(parameter);

    private sealed class Context
    {
        public Context()
        {
            Sessions.SetSession(Session());
            ViewModel = new(Navigation, Store, Sessions, message => LoginError = message);
        }
        public NavigationService Navigation { get; } = new();
        public CurrentSessionService Sessions { get; } = new();
        public TestWatchLaterStore Store { get; } = new();
        public WatchLaterViewModel ViewModel { get; }
        public string? LoginError { get; private set; }
    }
}

internal sealed class TestWatchLaterStore : IWatchLaterStore
{
    public List<WatchLaterItem> Items { get; } = new();
    public Func<AuthSession, CancellationToken, Task<IReadOnlyList<WatchLaterItem>>>? Load { get; set; }
    public Func<AuthSession, string, CancellationToken, Task>? Remove { get; set; }
    public int LoadCalls { get; private set; }
    public int ResetCalls { get; private set; }
    public string? LastRemovedId { get; private set; }
    public CancellationToken LastToken { get; private set; }
    public Task<IReadOnlyList<WatchLaterItem>> LoadAsync(AuthSession session, CancellationToken cancellationToken)
    {
        LoadCalls++;
        LastToken = cancellationToken;
        return Load?.Invoke(session, cancellationToken) ?? Task.FromResult<IReadOnlyList<WatchLaterItem>>(Items.ToArray());
    }
    public Task<bool> IsSavedAsync(AuthSession session, string itemId, CancellationToken cancellationToken)
        => Task.FromResult(Items.Any(item => item.ItemId == itemId));
    public Task AddAsync(AuthSession session, WatchLaterItem item, CancellationToken cancellationToken)
    {
        Items.Add(item);
        return Task.CompletedTask;
    }
    public Task RemoveAsync(AuthSession session, string itemId, CancellationToken cancellationToken)
    {
        LastRemovedId = itemId;
        LastToken = cancellationToken;
        if (Remove is not null) return Remove(session, itemId, cancellationToken);
        Items.RemoveAll(item => item.ItemId == itemId);
        return Task.CompletedTask;
    }
    public Task ResetCorruptedAsync(AuthSession session, CancellationToken cancellationToken)
    {
        ResetCalls++;
        Items.Clear();
        return Task.CompletedTask;
    }
}
