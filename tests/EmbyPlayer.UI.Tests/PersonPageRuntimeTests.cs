using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.People;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PersonPageRuntimeTests
{
    [TestMethod]
    public void PageRendersAuthenticatedImagesClickableWorksAndRestoresScrollAfterRecreation()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ExercisePage(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static void ExercisePage()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = Resources();
        app.Resources.MergedDictionaries.Add(resources);
        var sessions = new CurrentSessionService();
        sessions.SetSession(new("http://media.local", "test-token", "user-1", "User", "server-1"));
        var navigation = new NavigationService();
        var service = new Service();
        var viewModel = new PersonViewModel(navigation, service, sessions, new TestAuthSessionStore(), _ => { });
        var target = new PersonNavigationParameter("p1", new("movie-source", AppPage.Library));
        viewModel.LoadAsync(target).GetAwaiter().GetResult();
        ((AsyncRelayCommand)viewModel.LoadMoreCommand).ExecuteAsync().GetAwaiter().GetResult();
        var page = new PersonPage { DataContext = viewModel };
        var window = new Window
        {
            Width = 1200, Height = 850, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = page
        };
        try
        {
            window.Show();
            Pump();
            Assert.AreEqual(96, ((ItemsControl)page.FindName("PersonWorks")).Items.Count);
            Assert.AreEqual(97, Descendants<AuthenticatedImage>(page).Count());
            Assert.IsTrue(Descendants<TextBlock>(page).Any(text => text.Text == "Actor" && text.ActualWidth > 0));
            var button = Descendants<Button>(page).First(item => item.Command == viewModel.OpenMediaCommand);
            Assert.AreSame(viewModel.Works[0], button.CommandParameter);
            button.Command.Execute(button.CommandParameter);
            Assert.AreEqual(AppPage.Detail, navigation.CurrentPage);
            var scroll = (ScrollViewer)page.FindName("PersonScrollViewer");
            Assert.IsTrue(scroll.ScrollableHeight > 720);
            scroll.ScrollToVerticalOffset(720);
            Pump();
            Assert.AreEqual(720d, viewModel.ScrollOffset, 0.1);
            window.Content = null;
            Pump();
            viewModel.Deactivate();
            viewModel.LoadAsync(target).GetAwaiter().GetResult();
            var returnedPage = new PersonPage { DataContext = viewModel };
            window.Content = returnedPage;
            Pump();
            Assert.AreEqual(720d, ((ScrollViewer)returnedPage.FindName("PersonScrollViewer")).VerticalOffset, 0.1);
            Assert.AreEqual(720d, viewModel.ScrollOffset, 0.1);
            Assert.AreEqual(2, service.WorksCalls);
            Assert.AreEqual(96, ((ItemsControl)returnedPage.FindName("PersonWorks")).Items.Count);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(resources);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static ResourceDictionary Resources()
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

    private sealed class Service : IPersonService
    {
        public int WorksCalls { get; private set; }
        public Task<PersonLoadResult> LoadPersonAsync(AuthSession session, string personId, CancellationToken cancellationToken)
            => Task.FromResult(PersonLoadResult.Success(new(personId, "Actor", "Biography", null)));
        public Task<PersonWorksLoadResult> LoadWorksAsync(AuthSession session, string personId, int startIndex, int limit, CancellationToken cancellationToken)
        {
            WorksCalls++;
            return Task.FromResult(PersonWorksLoadResult.Success(PersonViewModelTests.Cards(startIndex, limit), startIndex + limit, true));
        }
    }
}
