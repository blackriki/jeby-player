using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.People;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PersonViewModelTests
{
    [TestMethod]
    public async Task LoadAsync_SeparatesProfileAndWorksLoadingAndShowsEmptyFallbacks()
    {
        var context = new Context();
        var profile = new TaskCompletionSource<PersonLoadResult>();
        var works = new TaskCompletionSource<PersonWorksLoadResult>();
        context.Service.Profile = (_, _) => profile.Task;
        context.Service.Works = (_, _, _) => works.Task;
        var loading = context.ViewModel.LoadAsync(new("person-1"));
        Assert.IsTrue(context.ViewModel.IsInitialLoading);
        profile.SetResult(PersonLoadResult.Success(new("person-1", "Actor", null, null)));
        Assert.IsTrue(context.ViewModel.IsContentVisible);
        Assert.IsTrue(context.ViewModel.IsWorksInitialLoading);
        Assert.AreEqual("Actor", context.ViewModel.Name);
        Assert.AreEqual("暂无人物简介", context.ViewModel.Overview);
        works.SetResult(PersonWorksLoadResult.Success(Array.Empty<LibraryMediaItem>(), 0, false));
        await loading;
        Assert.IsTrue(context.ViewModel.IsWorksEmpty);
        Assert.IsFalse(context.ViewModel.IsInitialLoading);
        Assert.IsFalse(context.ViewModel.HasMore);
    }

    [TestMethod]
    public async Task PaginationUsesRawCursorDeduplicatesAndRetriesFailedPage()
    {
        var context = new Context();
        var fail = true;
        context.Service.Works = (_, start, _) => Task.FromResult(start == 0
            ? PersonWorksLoadResult.Success(Cards(0, 48), 51, true)
            : fail ? PersonWorksLoadResult.Failure(PersonLoadError.ServerTimeout)
                : PersonWorksLoadResult.Success(Cards(47, 5), 56, false));
        await context.ViewModel.LoadAsync(new("p1"));
        var first = context.ViewModel.Works[0];
        await Execute(context.ViewModel.LoadMoreCommand);
        Assert.IsTrue(context.ViewModel.IsWorksRefreshErrorVisible);
        Assert.AreEqual(48, context.ViewModel.Works.Count);
        Assert.AreEqual(51, context.Service.LastStart);
        fail = false;
        await Execute(context.ViewModel.RetryWorksCommand);
        Assert.AreEqual(51, context.Service.LastStart);
        Assert.AreEqual(52, context.ViewModel.Works.Count);
        Assert.AreSame(first, context.ViewModel.Works[0]);
        Assert.IsFalse(context.ViewModel.HasMore);
        Assert.IsFalse(context.ViewModel.LoadMoreCommand.CanExecute(null));
        Assert.IsNull(context.ViewModel.WorksErrorMessage);
    }

    [TestMethod]
    public async Task ReturnFromWorkPreservesPagesScrollAndOriginalDetailContext()
    {
        var context = new Context();
        context.Service.Works = (_, start, _) => Task.FromResult(PersonWorksLoadResult.Success(Cards(start, 48), start + 48, true));
        var source = new DetailNavigationParameter("series-1", AppPage.HomeSection, SelectedSeasonId: "season-2", FocusedEpisodeId: "episode-8");
        var target = new PersonNavigationParameter("p1", source);
        await context.ViewModel.LoadAsync(target);
        await Execute(context.ViewModel.LoadMoreCommand);
        context.ViewModel.ScrollOffset = 720;
        var cached = context.ViewModel.Works;
        object? navigated = null;
        context.Navigation.CurrentPageChanged += (_, e) => navigated = e.Parameter;
        context.ViewModel.OpenMediaCommand.Execute(context.ViewModel.Works[0]);
        var detail = (DetailNavigationParameter)navigated!;
        Assert.AreEqual(AppPage.Person, detail.ReturnPage);
        Assert.AreSame(target, detail.PersonReturnTarget);
        context.ViewModel.Deactivate();
        await context.ViewModel.LoadAsync(detail.PersonReturnTarget);
        Assert.AreSame(cached, context.ViewModel.Works);
        Assert.AreEqual(720d, context.ViewModel.ScrollOffset);
        Assert.AreEqual(1, context.Service.ProfileCalls);
        Assert.AreEqual(2, context.Service.WorksCalls);
        await Execute(context.ViewModel.LoadMoreCommand);
        Assert.AreEqual(96, context.Service.LastStart);
        context.ViewModel.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.Detail, context.Navigation.CurrentPage);
        Assert.AreSame(source, navigated);
    }

    [TestMethod]
    public async Task PersonSwitchCancelsAndRejectsLateUnauthorizedResponse()
    {
        var context = new Context();
        var old = new TaskCompletionSource<PersonLoadResult>();
        context.Service.Profile = (id, _) => id == "old" ? old.Task : Task.FromResult(Profile(id));
        var loading = context.ViewModel.LoadAsync(new("old"));
        var token = context.Service.LastToken;
        await context.ViewModel.LoadAsync(new("new"));
        old.SetResult(PersonLoadResult.Failure(PersonLoadError.Unauthorized));
        await loading;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual("new", context.ViewModel.Person!.Id);
        Assert.AreEqual(0, context.AuthStore.ClearCallCount);
        Assert.IsNotNull(context.Sessions.CurrentSession);
    }

    [TestMethod]
    public async Task LeavingDuringPaginationCancelsAndRejectsLateCards()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<PersonWorksLoadResult>();
        context.Service.Works = (_, start, _) => start == 0
            ? Task.FromResult(PersonWorksLoadResult.Success(Cards(0, 48), 48, true)) : pending.Task;
        await context.ViewModel.LoadAsync(new("p1"));
        var loading = Execute(context.ViewModel.LoadMoreCommand);
        Assert.IsTrue(context.ViewModel.IsLoadingMore);
        Assert.IsFalse(context.ViewModel.LoadMoreCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.RefreshCommand.CanExecute(null));
        context.ViewModel.Deactivate();
        pending.SetResult(PersonWorksLoadResult.Success(Cards(48, 4), 52, false));
        await loading;
        Assert.IsTrue(context.Service.LastToken.IsCancellationRequested);
        Assert.AreEqual(48, context.ViewModel.Works.Count);
        Assert.IsFalse(context.ViewModel.IsWorksLoading);
    }

    [TestMethod]
    public async Task NewSessionClearsPreviousProfileAndCardsBeforeLoading()
    {
        var context = new Context();
        context.Service.Works = (_, _, _) => Task.FromResult(PersonWorksLoadResult.Success(Cards(0, 4), 4, false));
        await context.ViewModel.LoadAsync(new("p1"));
        var pending = new TaskCompletionSource<PersonLoadResult>();
        context.Service.Profile = (_, _) => pending.Task;
        context.Sessions.SetSession(new("http://new.local", "new-token", "new-user", "New", "new-server"));
        var loading = context.ViewModel.LoadAsync(new("p1"));
        Assert.IsNull(context.ViewModel.Person);
        Assert.AreEqual(0, context.ViewModel.Works.Count);
        Assert.IsTrue(context.ViewModel.IsInitialLoading);
        pending.SetResult(Profile("p1"));
        await loading;
        context.ViewModel.ResetSession();
        Assert.IsNull(context.ViewModel.Person);
        Assert.AreEqual(0, context.ViewModel.Works.Count);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task UnauthorizedClearsSessionAndReturnsToLogin(bool worksFailure, bool cleanupFails)
    {
        var context = new Context();
        if (worksFailure) context.Service.Works = (_, _, _) => Task.FromResult(PersonWorksLoadResult.Failure(PersonLoadError.Unauthorized));
        else context.Service.Profile = (_, _) => Task.FromResult(PersonLoadResult.Failure(PersonLoadError.Unauthorized));
        if (cleanupFails) context.AuthStore.ClearAsyncHandler = _ => throw new IOException("Storage unavailable");
        await context.ViewModel.LoadAsync(new("p1"));
        Assert.AreEqual(1, context.AuthStore.ClearCallCount);
        Assert.IsNull(context.Sessions.CurrentSession);
        Assert.AreEqual(AppPage.Login, context.Navigation.CurrentPage);
        Assert.IsNull(context.ViewModel.Person);
        Assert.IsFalse(context.ViewModel.IsLoading);
        Assert.IsFalse(context.ViewModel.IsWorksLoading);
        StringAssert.Contains(context.LoginError, "失效");
    }

    [TestMethod]
    public async Task SessionReplacedDuringUnauthorizedCleanupIsPreserved()
    {
        var context = new Context();
        var cleanup = new TaskCompletionSource();
        context.AuthStore.ClearAsyncHandler = _ => cleanup.Task;
        context.Service.Profile = (_, _) => Task.FromResult(PersonLoadResult.Failure(PersonLoadError.Unauthorized));
        var loading = context.ViewModel.LoadAsync(new("p1"));
        var replacement = new AuthSession("http://new.local", "new-token", "new-user", "New", "new-server");
        context.Sessions.SetSession(replacement);
        cleanup.SetResult();
        await loading;
        Assert.AreSame(replacement, context.Sessions.CurrentSession);
        Assert.AreNotEqual(AppPage.Login, context.Navigation.CurrentPage);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ForbiddenShowsRetryWithoutClearingAuthentication(bool worksFailure)
    {
        var context = new Context();
        if (worksFailure) context.Service.Works = (_, _, _) => Task.FromResult(PersonWorksLoadResult.Failure(PersonLoadError.Forbidden));
        else context.Service.Profile = (_, _) => Task.FromResult(PersonLoadResult.Failure(PersonLoadError.Forbidden));
        await context.ViewModel.LoadAsync(new("p1"));
        Assert.IsTrue(worksFailure ? context.ViewModel.IsWorksErrorVisible : context.ViewModel.IsErrorVisible);
        StringAssert.Contains(worksFailure ? context.ViewModel.WorksErrorMessage : context.ViewModel.ErrorMessage, "权限");
        Assert.IsNotNull(context.Sessions.CurrentSession);
        Assert.AreEqual(0, context.AuthStore.ClearCallCount);
        context.Service.Profile = (id, _) => Task.FromResult(Profile(id));
        context.Service.Works = (_, _, _) => Task.FromResult(PersonWorksLoadResult.Success(Cards(0, 1), 1, false));
        await Execute(worksFailure ? context.ViewModel.RetryWorksCommand : context.ViewModel.RefreshCommand);
        Assert.AreEqual(1, context.ViewModel.Works.Count);
        Assert.IsNull(context.ViewModel.ErrorMessage);
        Assert.IsNull(context.ViewModel.WorksErrorMessage);
    }

    private static Task Execute(System.Windows.Input.ICommand command) => ((AsyncRelayCommand)command).ExecuteAsync();
    private static PersonLoadResult Profile(string id) => PersonLoadResult.Success(new(id, "Actor", "Overview", null));
    internal static IReadOnlyList<LibraryMediaItem> Cards(int start, int count) => Enumerable.Range(start, count)
        .Select(i => new LibraryMediaItem($"item-{i}", $"Movie {i}", "Movie", 2025, null, null)).ToArray();
    private sealed class Service : IPersonService
    {
        public Func<string, CancellationToken, Task<PersonLoadResult>> Profile { get; set; } = (id, _) => Task.FromResult(PersonViewModelTests.Profile(id));
        public Func<string, int, CancellationToken, Task<PersonWorksLoadResult>> Works { get; set; }
            = (_, _, _) => Task.FromResult(PersonWorksLoadResult.Success(Array.Empty<LibraryMediaItem>(), 0, false));
        public int ProfileCalls { get; private set; }
        public int WorksCalls { get; private set; }
        public int LastStart { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task<PersonLoadResult> LoadPersonAsync(AuthSession session, string personId, CancellationToken cancellationToken)
        { ProfileCalls++; LastToken = cancellationToken; return Profile(personId, cancellationToken); }
        public Task<PersonWorksLoadResult> LoadWorksAsync(AuthSession session, string personId, int startIndex, int limit, CancellationToken cancellationToken)
        { WorksCalls++; LastStart = startIndex; LastToken = cancellationToken; Assert.AreEqual(48, limit); return Works(personId, startIndex, cancellationToken); }
    }
    private sealed class Context
    {
        public Service Service { get; } = new();
        public CurrentSessionService Sessions { get; } = new();
        public TestAuthSessionStore AuthStore { get; } = new();
        public NavigationService Navigation { get; } = new();
        public string? LoginError { get; private set; }
        public PersonViewModel ViewModel { get; }
        public Context()
        {
            Sessions.SetSession(new("http://media.local", "test-token", "user-1", "Test User", "test-server"));
            ViewModel = new(Navigation, Service, Sessions, AuthStore, error => LoginError = error);
        }
    }
}
