using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class WatchLaterPageRuntimeTests
{
    [TestMethod]
    public void PageRendersPosterActionsAtSmallSizeAndRestoresScrollAfterDetailReturn()
        => RunSta(() => WithPage((window, page, viewModel, store, navigation) =>
        {
            store.Items.AddRange(Enumerable.Range(0, 40).Select(index => WatchLaterViewModelTests.Item(index.ToString())));
            viewModel.LoadAsync().GetAwaiter().GetResult();
            Pump();
            var items = (ItemsControl)page.FindName("WatchLaterItems");
            Assert.AreEqual(40, items.Items.Count);
            Assert.AreEqual(40, Descendants<AuthenticatedImage>(items).Count());
            var open = Descendants<Button>(items).First(button => button.Command == viewModel.OpenMediaCommand);
            var remove = Descendants<Button>(items).First(button => button.Command == viewModel.RemoveCommand);
            Assert.IsTrue(open.ActualWidth > 150);
            Assert.IsTrue(remove.ActualWidth > 20);
            Assert.AreEqual(open.CommandParameter, remove.CommandParameter);
            Assert.IsTrue(remove.TranslatePoint(new Point(remove.ActualWidth, 0), page).X < page.ActualWidth);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            open.Command.Execute(open.CommandParameter);
            Assert.AreEqual(AppPage.Detail, navigation.CurrentPage);
            ((AsyncRelayCommand)remove.Command).ExecuteAsync(remove.CommandParameter).GetAwaiter().GetResult();
            Pump();
            Assert.AreEqual(39, items.Items.Count);
            Assert.AreEqual("1", viewModel.Items[0].ItemId);
            var scroll = (ScrollViewer)page.FindName("WatchLaterScrollViewer");
            Assert.IsTrue(scroll.ScrollableHeight > 720);
            scroll.ScrollToVerticalOffset(720);
            Pump();
            Assert.AreEqual(720d, viewModel.ScrollOffset, 0.1);
            window.Content = null;
            Pump();
            viewModel.Deactivate();
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var returned = new WatchLaterPage { DataContext = viewModel };
            window.Content = returned;
            Pump();
            Assert.AreEqual(720d, ((ScrollViewer)returned.FindName("WatchLaterScrollViewer")).VerticalOffset, 0.1);
            Assert.AreEqual(39, ((ItemsControl)returned.FindName("WatchLaterItems")).Items.Count);
        }));

    [TestMethod]
    public void ErrorResetConfirmationAndEmptyStateAreBoundAndUsable()
        => RunSta(() => WithPage((_, page, viewModel, store, _) =>
        {
            store.Load = (_, _) => throw new InvalidDataException();
            viewModel.LoadAsync().GetAwaiter().GetResult();
            Pump();
            var error = Descendants<TextBlock>(page).Single(text => AutomationProperties.GetAutomationId(text) == "WatchLater.Error");
            Assert.IsTrue(error.IsVisible);
            StringAssert.Contains(error.Text, "损坏");
            var reset = FindButton(page, "WatchLater.Reset");
            Assert.IsTrue(reset.IsVisible);
            Assert.IsTrue(reset.IsEnabled);
            reset.Command.Execute(null);
            Pump();
            var confirm = FindButton(page, "WatchLater.ConfirmReset");
            Assert.IsTrue(confirm.IsVisible);
            Assert.IsTrue(confirm.ActualWidth > 40);
            Assert.IsTrue(confirm.TranslatePoint(new Point(0, confirm.ActualHeight), page).Y < page.ActualHeight);
            Assert.AreEqual(0, store.ResetCalls);
            store.Load = null;
            ((AsyncRelayCommand)confirm.Command).ExecuteAsync().GetAwaiter().GetResult();
            Pump();
            Assert.AreEqual(1, store.ResetCalls);
            Assert.IsFalse(confirm.IsVisible);
            Assert.IsFalse(error.IsVisible);
            Assert.IsTrue(Descendants<TextBlock>(page).Any(text => text.Text == "还没有稍后观看的作品" && text.IsVisible));
        }));

    private static Button FindButton(DependencyObject page, string id)
        => Descendants<Button>(page).Single(button => AutomationProperties.GetAutomationId(button) == id);

    private static void WithPage(Action<Window, WatchLaterPage, WatchLaterViewModel, TestWatchLaterStore, NavigationService> exercise)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = Resources();
        app.Resources.MergedDictionaries.Add(resources);
        var sessions = new CurrentSessionService();
        sessions.SetSession(WatchLaterViewModelTests.Session());
        var navigation = new NavigationService();
        var store = new TestWatchLaterStore();
        var viewModel = new WatchLaterViewModel(navigation, store, sessions, _ => { });
        var page = new WatchLaterPage { DataContext = viewModel };
        var window = new Window
        {
            Width = 850, Height = 650, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = page
        };
        try { window.Show(); Pump(); exercise(window, page, viewModel, store, navigation); }
        finally { window.Close(); app.Resources.MergedDictionaries.Remove(resources); }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
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
}
