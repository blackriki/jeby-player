using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Servers;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class ServerConnectionPageRuntimeTests
{
    [TestMethod]
    public void RecentServerControls_SelectAndRemoveViaKeyboardWithoutAutomaticallyConnecting()
    {
        RunOnSta(() =>
        {
            var settings = new TestAppSettingsService
            {
                RecentServerBases = new[] { "http://recent.local:8096", "https://older.local/emby" }
            };
            var pendingConnection = new TaskCompletionSource<ServerConnectionResult>();
            var connection = new TestServerConnectionService { ConnectAsyncHandler = (_, _) => pendingConnection.Task };
            var viewModel = new ServerConnectionViewModel(new NavigationService(), connection, settings);
            viewModel.LoadLastServerBaseAsync(CancellationToken.None).GetAwaiter().GetResult();
            WithPage(viewModel, 760, 820, (page, _) =>
            {
                var input = (TextBox)page.FindName("ServerUrlInput");
                var connect = (Button)page.FindName("ConnectButton");
                var list = (ItemsControl)page.FindName("RecentServersList");
                var select = FindButtons(page, "RecentServer.Select")[0];
                Assert.AreEqual(string.Empty, input.Text);
                Assert.IsFalse(connect.IsEnabled);
                PressEnter(select);

                Assert.AreEqual("http://recent.local:8096", input.Text);
                Assert.IsTrue(connect.IsEnabled);
                Assert.AreEqual(0, connection.ConnectCallCount);
                Assert.AreEqual(0, settings.SaveCallCount);
                PressEnter(FindButtons(page, "RecentServer.Remove")[1]);

                Assert.AreEqual(1, list.Items.Count);
                Assert.AreEqual("http://recent.local:8096", input.Text);
                Assert.AreEqual("https://older.local/emby", settings.LastRemovedServerBase);
                Assert.AreEqual(0, connection.ConnectCallCount);
                PressEnter(connect);
                Assert.AreEqual(1, connection.ConnectCallCount);
                Assert.IsFalse(input.IsEnabled);
                Assert.IsFalse(connect.IsEnabled);
                Assert.IsFalse(FindButtons(page, "RecentServer.Select")[0].IsEnabled);
                Assert.IsFalse(FindButtons(page, "RecentServer.Remove")[0].IsEnabled);

                pendingConnection.SetResult(ServerConnectionResult.Failure(ServerConnectionError.ServerUnreachable));
                Pump();
                Assert.IsTrue(input.IsEnabled);
                Assert.IsTrue(connect.IsEnabled);
                Assert.IsTrue(FindButtons(page, "RecentServer.Remove")[0].IsEnabled);
                PressEnter(FindButtons(page, "RecentServer.Remove")[0]);
                Assert.AreEqual(0, list.Items.Count);
                Assert.IsTrue(viewModel.IsRecentServersEmpty);
                Assert.AreEqual("http://recent.local:8096", input.Text);
            });
        });
    }

    [TestMethod]
    public void FiveRecentServers_LongAddressesRemainReachableInSmallWindow()
    {
        RunOnSta(() =>
        {
            var addresses = Enumerable.Range(1, 5).Select(index =>
                $"https://server-{index}.example.invalid/a-long-base-path-for-a-media-server/emby").ToArray();
            var settings = new TestAppSettingsService { RecentServerBases = addresses };
            var viewModel = new ServerConnectionViewModel(new NavigationService(), new TestServerConnectionService(), settings);
            viewModel.LoadLastServerBaseAsync(CancellationToken.None,
                ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage).GetAwaiter().GetResult();
            WithPage(viewModel, 480, 390, (page, _) =>
            {
                var scroll = (ScrollViewer)page.FindName("ServerConnectionScrollViewer");
                var card = (Border)page.FindName("ConnectionCard");
                Assert.IsTrue(scroll.ScrollableHeight > 0, "An expanded connection card must scroll in a short window.");
                Assert.IsTrue(card.ActualWidth <= scroll.ViewportWidth - 48 + 0.1,
                    "The card must shrink to fit its horizontal margins.");
                var select = FindButtons(page, "RecentServer.Select").Last();
                Assert.AreEqual(addresses[^1], select.ToolTip);
                Assert.AreEqual(TextTrimming.CharacterEllipsis, Descendants<TextBlock>(select).Single().TextTrimming);
                scroll.ScrollToBottom();
                Pump();

                var remove = FindButtons(page, "RecentServer.Remove").Last();
                var bounds = remove.TransformToAncestor(scroll).TransformBounds(new Rect(remove.RenderSize));
                Assert.IsTrue(bounds.Top >= 0);
                Assert.IsTrue(bounds.Bottom <= scroll.ViewportHeight + 0.1,
                    "The last remove button must be fully reachable after scrolling.");
                PressEnter(remove);
                Assert.AreEqual(4, viewModel.RecentServerBases.Count);
                Assert.AreEqual(string.Empty, viewModel.ServerUrl);
                Assert.AreEqual(ServerConnectionNavigationParameter.SwitchServerPartialFailureMessage, viewModel.ErrorMessage);
            });
        });
    }

    private static Button[] FindButtons(DependencyObject page, string id) => Descendants<Button>(page)
        .Where(button => AutomationProperties.GetAutomationId(button) == id).ToArray();

    private static void PressEnter(Button button)
    {
        Assert.IsTrue(button.Focusable && button.IsTabStop);
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        Pump();
    }

    private static void WithPage(ServerConnectionViewModel viewModel, double width, double height,
        Action<ServerConnectionPage, Window> assertions)
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = LoadApplicationResources();
        application.Resources.MergedDictionaries.Add(resources);
        var page = new ServerConnectionPage { DataContext = viewModel };
        var window = new Window
        {
            Width = width, Height = height, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = page
        };
        try
        {
            window.Show();
            Pump();
            assertions(page, window);
        }
        finally
        {
            window.Close();
            application.Resources.MergedDictionaries.Remove(resources);
        }
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

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EmbyPlayer.sln"))) root = root.Parent;
        var xaml = File.ReadAllText(Path.Combine(root!.FullName, "src", "EmbyPlayer.App", "App.xaml"));
        var start = xaml.IndexOf("<ResourceDictionary>", StringComparison.Ordinal);
        var end = xaml.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(xaml[start..(end + "</ResourceDictionary>".Length)].Replace(
            "<ResourceDictionary>",
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" "
            + "xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">",
            StringComparison.Ordinal));
    }
}
