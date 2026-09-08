using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Home;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class HomePageXamlRuntimeTests
{
    [TestMethod]
    public void HomePage_InitializesWithApplicationResources_OnSta()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            Application.Current!.Resources.MergedDictionaries.Add(LoadApplicationResources());

            var page = new HomePage();
            page.ApplyTemplate();

            Assert.IsNotNull(page.Resources["HomeMediaSectionTemplate"]);
        });
    }

    [TestMethod]
    public void ViewAllButtons_ActivateEveryBoundSectionWithEnterAndHaveOneBorderlessStyle()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            var resources = LoadApplicationResources();
            Application.Current!.Resources.MergedDictionaries.Add(resources);
            var sessions = new CurrentSessionService();
            sessions.SetSession(new AuthSession("http://media.local", "test-token", "user-1", "User", "server-1"));
            var service = new TestHomeService
            {
                LoadHomeAsyncHandler = (_, _) => Task.FromResult(HomeLoadResult.Success(new HomeData(
                    "User", Array.Empty<MediaCard>(), Array.Empty<MediaCard>(), Array.Empty<MediaLibrary>(),
                    new[]
                    {
                        HomeMediaSection.Success("movies", "电影", Array.Empty<MediaCard>()),
                        HomeMediaSection.Success("series", "电视节目", Array.Empty<MediaCard>()),
                        HomeMediaSection.Success("animation", "动画", Array.Empty<MediaCard>()),
                        HomeMediaSection.Success("boxsets", "合集", Array.Empty<MediaCard>())
                    })))
            };
            var navigation = new NavigationService();
            var viewModel = new HomeViewModel(navigation, service, new TestPlaybackService(), sessions,
                new TestAuthSessionStore(), _ => { });
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var page = new HomePage { DataContext = viewModel };
            var window = CreateOffscreenWindow(page);
            try
            {
                window.Show();
                Pump();
                var buttons = Descendants<Button>(page)
                    .Where(button => AutomationProperties.GetAutomationId(button).StartsWith("Home.ViewAll.", StringComparison.Ordinal))
                    .ToArray();
                Assert.AreEqual(6, buttons.Length);
                var expectedSections = new Dictionary<string, HomeSectionKind>
                {
                    ["ContinueWatching"] = HomeSectionKind.ContinueWatching,
                    ["RecentlyAdded"] = HomeSectionKind.RecentlyAdded,
                    ["movies"] = HomeSectionKind.Movies,
                    ["series"] = HomeSectionKind.Series,
                    ["animation"] = HomeSectionKind.Animation,
                    ["boxsets"] = HomeSectionKind.BoxSets
                };
                object? parameter = null;
                navigation.CurrentPageChanged += (_, args) => parameter = args.Parameter;
                foreach (var button in buttons)
                {
                    var id = AutomationProperties.GetAutomationId(button)["Home.ViewAll.".Length..];
                    Assert.AreSame(page.Resources["ViewAllButtonStyle"], button.Style);
                    Assert.IsTrue(button.IsTabStop && button.Focusable);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                    var chrome = (Border)VisualTreeHelper.GetChild(button, 0);
                    Assert.AreEqual(new Thickness(0), chrome.BorderThickness);
                    Assert.AreEqual(new CornerRadius(0), chrome.CornerRadius);
                    button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Enter)
                    {
                        RoutedEvent = Keyboard.KeyDownEvent
                    });
                    Assert.AreEqual(AppPage.HomeSection, navigation.CurrentPage);
                    Assert.AreEqual(expectedSections[id], parameter, $"The {id} button must activate its own section.");
                }
            }
            finally
            {
                window.Close();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    [TestMethod]
    public void HomeSectionPage_RecreationRestoresRealScrollOffsetAndKeepsPaginatedCards()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            var resources = LoadApplicationResources();
            Application.Current!.Resources.MergedDictionaries.Add(resources);
            var sessions = new CurrentSessionService();
            sessions.SetSession(new AuthSession("http://media.local", "test-token", "user-1", "User", "server-1"));
            var service = new TestHomeService
            {
                LoadSectionAsyncHandler = (_, _, start, limit, _) => Task.FromResult(HomeSectionItemsLoadResult.Success(
                    Enumerable.Range(start, limit).Select(index => new MediaCard($"item-{index}", $"Movie {index}",
                        "Movie", 2024, null, 35)).ToArray(), start + limit, true))
            };
            var viewModel = new HomeSectionViewModel(new NavigationService(), service, sessions,
                new TestAuthSessionStore(), _ => { });
            viewModel.LoadAsync(HomeSectionKind.Movies).GetAwaiter().GetResult();
            viewModel.LoadMoreAsync().GetAwaiter().GetResult();
            var page = new HomeSectionPage { DataContext = viewModel };
            var window = CreateOffscreenWindow(page);
            try
            {
                window.Show();
                Pump();
                var scroll = (ScrollViewer)page.FindName("SectionScrollViewer");
                Assert.IsTrue(scroll.ScrollableHeight > 720);
                scroll.ScrollToVerticalOffset(720);
                Pump();
                Assert.AreEqual(720d, viewModel.ScrollOffset, 0.1);
                window.Content = null;
                Pump();
                viewModel.Deactivate();
                viewModel.LoadAsync().GetAwaiter().GetResult();
                var returnedPage = new HomeSectionPage { DataContext = viewModel };
                window.Content = returnedPage;
                Pump();

                var returnedScroll = (ScrollViewer)returnedPage.FindName("SectionScrollViewer");
                Assert.AreEqual(720d, returnedScroll.VerticalOffset, 0.1);
                Assert.AreEqual(720d, viewModel.ScrollOffset, 0.1);
                Assert.AreEqual(96, ((ItemsControl)returnedPage.FindName("SectionItems")).Items.Count);
                Assert.AreEqual(2, service.LoadSectionCallCount);
                viewModel.RefreshAsync().GetAwaiter().GetResult();
                Pump();
                Assert.AreEqual(0d, returnedScroll.VerticalOffset, 0.1);
                Assert.AreEqual(48, viewModel.Items.Count);
            }
            finally
            {
                window.Close();
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
    }

    private static void EnsureApplication()
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
    }

    private static Window CreateOffscreenWindow(object content) => new()
    {
        Width = 1200, Height = 850, Left = -10000, Top = -10000,
        ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = content
    };

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

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var root = FindRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "EmbyPlayer.App", "App.xaml"));
        const string startMarker = "<ResourceDictionary>";
        const string endMarker = "</ResourceDictionary>";
        var start = appXaml.IndexOf(startMarker, StringComparison.Ordinal);
        var end = appXaml.LastIndexOf(endMarker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start, "App.xaml must define its application ResourceDictionary.");

        var dictionaryXaml = appXaml[start..(end + endMarker.Length)]
            .Replace(
                startMarker,
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
                + "xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" "
                + "xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">",
                StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(dictionaryXaml);
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
