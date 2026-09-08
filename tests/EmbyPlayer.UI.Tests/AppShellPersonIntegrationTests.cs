using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.People;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed partial class AppShellPersonIntegrationTests
{
    [DataTestMethod]
    [DataRow("Movie")]
    [DataRow("Series")]
    public async Task CastToPersonToWorkAndBackUsesShellRoutesAndPreservesPages(string type)
    {
        var context = new Context(type);
        context.Navigation.NavigateTo(AppPage.Detail, new DetailNavigationParameter("source", AppPage.Library));
        context.Detail.OpenPersonCommand.Execute(context.Detail.People.Single());
        Assert.AreEqual(AppPage.Person, context.Shell.CurrentPage);
        Assert.AreSame(context.Person, context.Shell.CurrentPageViewModel);
        Assert.AreEqual("person-1", context.PersonService.LastPersonId);
        Assert.AreEqual(48, context.Person.Works.Count);
        await ((AsyncRelayCommand)context.Person.LoadMoreCommand).ExecuteAsync();
        var works = context.Person.Works;
        context.Person.ScrollOffset = 720;
        context.Person.OpenMediaCommand.Execute(works[^1]);
        Assert.AreEqual(AppPage.Detail, context.Shell.CurrentPage);
        Assert.AreSame(context.Detail, context.Shell.CurrentPageViewModel);
        Assert.AreEqual("work-95", context.DetailService.LastItemId);
        context.Detail.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.Person, context.Shell.CurrentPage);
        Assert.AreSame(works, context.Person.Works);
        Assert.AreEqual(720d, context.Person.ScrollOffset);
        Assert.AreEqual(2, context.PersonService.WorksCalls);
        context.Person.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.Detail, context.Shell.CurrentPage);
        Assert.AreEqual("source", context.DetailService.LastItemId);
        context.Detail.BackCommand.Execute(null);
        Assert.AreEqual(AppPage.Library, context.Shell.CurrentPage);
    }

    [TestMethod]
    public async Task LeavingPersonCancelsRequestAndDiscardsLateUnauthorized()
    {
        var context = new Context();
        context.Navigation.NavigateTo(AppPage.Person, new PersonNavigationParameter("person-1"));
        var pending = new TaskCompletionSource<PersonLoadResult>();
        context.PersonService.Profile = (_, _) => pending.Task;
        var refresh = context.Person.RefreshAsync();
        var token = context.PersonService.LastToken;
        context.Navigation.NavigateTo(AppPage.Home);
        pending.SetResult(PersonLoadResult.Failure(PersonLoadError.Unauthorized));
        await refresh;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(AppPage.Home, context.Shell.CurrentPage);
        Assert.AreEqual(0, context.AuthStore.ClearCallCount);
        Assert.IsNotNull(context.Sessions.CurrentSession);
        Assert.IsFalse(context.Person.IsLoading);
    }

    [DataTestMethod]
    [DataRow(AppPage.Login)]
    [DataRow(AppPage.ServerConnection)]
    public void AuthenticationRouteResetsPersonProfileWorksAndScroll(AppPage page)
    {
        var context = new Context();
        context.Navigation.NavigateTo(AppPage.Person, new PersonNavigationParameter("person-1"));
        context.Person.ScrollOffset = 500;
        Assert.AreEqual(48, context.Person.Works.Count);
        context.Navigation.NavigateTo(page);
        Assert.AreEqual(page, context.Shell.CurrentPage);
        Assert.IsNull(context.Person.Person);
        Assert.AreEqual(0, context.Person.Works.Count);
        Assert.AreEqual(0d, context.Person.ScrollOffset);
        Assert.IsFalse(context.Person.HasMore);
    }

    [TestMethod]
    public void UnauthorizedFromPersonInitialLoadRoutesShellToLoginAndClearsData()
    {
        var context = new Context();
        context.PersonService.Profile = (_, _) => Task.FromResult(PersonLoadResult.Failure(PersonLoadError.Unauthorized));
        context.Navigation.NavigateTo(AppPage.Person, new PersonNavigationParameter("person-1"));
        Assert.AreEqual(AppPage.Login, context.Shell.CurrentPage);
        Assert.AreSame(context.Login, context.Shell.CurrentPageViewModel);
        Assert.IsNull(context.Sessions.CurrentSession);
        Assert.AreEqual(1, context.AuthStore.ClearCallCount);
        Assert.IsNull(context.Person.Person);
    }

    [TestMethod]
    public async Task ShellUnloadCancellationCancelsPersonRequest()
    {
        var context = new Context();
        context.Navigation.NavigateTo(AppPage.Person, new PersonNavigationParameter("person-1"));
        var pending = new TaskCompletionSource<PersonLoadResult>();
        context.PersonService.Profile = (_, _) => pending.Task;
        var refresh = context.Person.RefreshAsync();
        var token = context.PersonService.LastToken;
        context.Shell.CancelPendingOperations();
        pending.SetResult(PersonLoadResult.Success(new("late-person", "Late", null, null)));
        await refresh;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual("person-1", context.Person.Person!.Id);
    }

    [TestMethod]
    public void ShellDataTemplateActuallyCreatesPersonPageAndBindsLoadedWorks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var resources = LoadResources();
                app.Resources.MergedDictionaries.Add(resources);
                var context = new Context();
                context.Shell.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                context.Navigation.NavigateTo(AppPage.Person, new PersonNavigationParameter("person-1"));
                var shell = new AppShell { DataContext = context.Shell };
                var window = new Window
                {
                    Width = 1200, Height = 850, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = shell
                };
                try
                {
                    window.Show();
                    Pump();
                    var personPage = Descendants<PersonPage>(shell).Single();
                    Assert.AreSame(context.Person, personPage.DataContext);
                    Assert.IsTrue(personPage.IsVisible);
                    Assert.AreEqual(48, ((ItemsControl)personPage.FindName("PersonWorks")).Items.Count);
                    var workButton = Descendants<Button>(personPage).First(button => button.Command == context.Person.OpenMediaCommand);
                    workButton.Command.Execute(workButton.CommandParameter);
                    Pump();
                    Assert.AreEqual(AppPage.Detail, context.Shell.CurrentPage);
                    Assert.IsTrue(Descendants<MediaDetailPage>(shell).Single().IsVisible);
                    context.Detail.BackCommand.Execute(null);
                    Pump();
                    Assert.IsTrue(Descendants<PersonPage>(shell).Single().IsVisible);
                }
                finally
                {
                    window.Close();
                    app.Resources.MergedDictionaries.Remove(resources);
                }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static ResourceDictionary LoadResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var appXaml = File.ReadAllText(Path.Combine(directory.FullName, "src", "EmbyPlayer.App", "App.xaml"));
        const string startMarker = "<ResourceDictionary>";
        const string endMarker = "</ResourceDictionary>";
        var start = appXaml.IndexOf(startMarker, StringComparison.Ordinal);
        var end = appXaml.LastIndexOf(endMarker, StringComparison.Ordinal);
        var dictionary = appXaml[start..(end + endMarker.Length)].Replace(startMarker,
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" "
            + "xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">",
            StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(dictionary);
    }

    private sealed class PersonService : IPersonService
    {
        public string? LastPersonId { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public int WorksCalls { get; private set; }
        public Func<string, CancellationToken, Task<PersonLoadResult>> Profile { get; set; }
            = (id, _) => Task.FromResult(PersonLoadResult.Success(new(id, "Actor", null, null)));
        public Task<PersonLoadResult> LoadPersonAsync(AuthSession session, string personId, CancellationToken cancellationToken)
        {
            LastPersonId = personId;
            LastToken = cancellationToken;
            return Profile(personId, cancellationToken);
        }
        public Task<PersonWorksLoadResult> LoadWorksAsync(AuthSession session, string personId, int startIndex, int limit, CancellationToken cancellationToken)
        {
            WorksCalls++;
            return Task.FromResult(PersonWorksLoadResult.Success(Enumerable.Range(startIndex, limit)
                .Select(i => new LibraryMediaItem($"work-{i}", $"Work {i}", "Movie", 2025, null, null)).ToArray(),
                startIndex + limit, startIndex == 0));
        }
    }

    private sealed class Context
    {
        public NavigationService Navigation { get; } = new();
        public CurrentSessionService Sessions { get; } = new();
        public TestAuthSessionStore AuthStore { get; } = new();
        public TestMediaDetailService DetailService { get; } = new();
        public PersonService PersonService { get; } = new();
        public LoginViewModel Login { get; }
        public MediaDetailViewModel Detail { get; }
        public PersonViewModel Person { get; }
        public AppShellViewModel Shell { get; }
        public EmbyPlayer.Core.PlaybackQueue.InMemoryPlaybackQueueService Queue { get; } = new();
        public TestWatchLaterStore WatchLaterStore { get; } = new();
        public WatchLaterViewModel WatchLater { get; }
        public HomeViewModel Home { get; }
        public PlayerViewModel Player { get; }
        public Context(string type = "Movie", EmbyPlayer.Core.Home.IHomeService? homeService = null)
        {
            var settings = new TestAppSettingsService();
            var account = new AccountSessionService(AuthStore, Sessions, settings);
            var connection = new ServerConnectionViewModel(Navigation, new TestServerConnectionService(), settings);
            Login = new(Navigation, new TestAuthenticationService(), Sessions, AuthStore, settings, account);
            var home = new HomeViewModel(Navigation, homeService ?? new TestHomeService(), new TestPlaybackService(), Sessions, AuthStore, Login.ShowError,
                watchLaterStore: WatchLaterStore, playbackQueue: Queue);
            Home = home;
            var library = new LibraryViewModel(Navigation, new TestLibraryService(), Sessions, AuthStore, Login.ShowError);
            var search = new SearchViewModel(Navigation, new TestSearchService(), Sessions, AuthStore, settings, Login.ShowError);
            DetailService.LoadDetailAsyncHandler = (_, id, _) => Task.FromResult(MediaDetailLoadResult.Success(new MediaDetail(
                id, "Media", type, 2025, null, null, null, null, null, null, null, null,
                people: [new MediaPerson("person-1", "Actor", "Lead", "Actor", null)])));
            Detail = new(Navigation, DetailService, new TestSimilarMediaService(), new TestItemUserDataService(),
                new TestSeriesService(), new TestPlaybackService(), Sessions, AuthStore, Login.ShowError,
                watchLaterStore: WatchLaterStore, playbackQueue: Queue);
            Person = new(Navigation, PersonService, Sessions, AuthStore, Login.ShowError);
            var player = new PlayerViewModel(Navigation, new TestPlayerService(), new TestPlaybackReportService(), Sessions,
                AuthStore, Login.ShowError, new TestPlaybackReportScheduler(), playbackQueue: Queue);
            Player = player;
            WatchLater = new(Navigation, WatchLaterStore, Sessions, Login.ShowError);
            Shell = new(Navigation, connection, Login, home, library, search, Detail, player, settings,
                AuthStore, new TestAuthSessionValidator(), Sessions, account, new TestMediaLibraryScanService(), personViewModel: Person,
                watchLaterViewModel: WatchLater, playbackQueue: Queue);
            Sessions.SetSession(new("http://media.local", "test-token", "user-1", "User", "server-1"));
        }
    }
}
