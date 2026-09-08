using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Library;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class LibraryToolbarTests
{
    [DataTestMethod]
    [DataRow(1280)]
    [DataRow(1266)]
    [DataRow(1100)]
    [DataRow(1088)]
    public void Toolbar_AlignsToFullPosterColumnsAcrossResultsAndLoading(int width)
    {
        RunOnSta(() =>
        {
            if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Application.Current!.Resources.MergedDictionaries.Add(LoadApplicationResources());
            var count = 18;
            LibraryItemsLoadResult Result() => LibraryItemsLoadResult.Success(Enumerable.Range(0, count)
                .Select(index => new LibraryMediaItem($"fixture-{index}", $"Fixture {index}", "Movie", 2024, null, null)).ToArray(), count);
            var service = new TestLibraryService
            {
                LoadLibrariesAsyncHandler = (_, _) => Task.FromResult(LibraryLoadResult.Success(
                    [new LibraryItem("fixture-library", "Long library title that must truncate before the compact sorting and filtering toolbar", "movies")])),
                LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) => Task.FromResult(Result())
            };
            var session = new CurrentSessionService();
            session.SetSession(new AuthSession("http://media.local", "fixture-token", "fixture-user", "Fixture", "fixture-server"));
            var vm = new LibraryViewModel(new NavigationService(), service, session, new TestAuthSessionStore(), _ => { });
            vm.LoadAsync().GetAwaiter().GetResult();
            var page = new LibraryPage { DataContext = vm };
            var host = (StackPanel)page.FindName("LibraryContentHost");
            var header = (Grid)page.FindName("LibraryContentHeader");
            var media = (ItemsControl)page.FindName("LibraryPosterGrid");
            var sort = (Button)page.FindName("SortButton");
            var toolbar = (StackPanel)sort.Parent;
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
            double LayoutAndAssert()
            {
                page.Measure(new Size(width, 700));
                page.Arrange(new Rect(0, 0, width, 700));
                page.UpdateLayout();
                var cardWidth = (double)page.Resources["LibraryPosterWidth"];
                var cardMargin = (Thickness)page.Resources["LibraryPosterMargin"];
                var stride = cardWidth + cardMargin.Left + cardMargin.Right;
                var expectedWidth = Math.Floor(host.ActualWidth / stride) * stride - cardMargin.Right;
                Assert.AreEqual(expectedWidth, header.ActualWidth, 0.01);
                Assert.AreEqual(Bounds(host).Left + expectedWidth, Bounds(toolbar).Right, 0.01);
                Assert.IsTrue(Bounds((FrameworkElement)header.Children[0]).Right <= Bounds(sort).Left);
                foreach (var button in toolbar.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible))
                {
                    Assert.AreEqual(42, button.ActualHeight, 0.01);
                    Assert.AreEqual(Bounds(header).Top, Bounds(button).Top, 0.01);
                    Assert.IsTrue(Bounds(button).Right <= Bounds(host).Right);
                }
                return expectedWidth;
            }
            var initialWidth = LayoutAndAssert();
            Assert.AreEqual(Bounds(header).Right, Descendants<Button>(media).Max(card => Bounds(card).Right), 0.01);

            foreach (var resultCount in new[] { 1, 0, 18 })
            {
                count = resultCount;
                ((AsyncRelayCommand)vm.RetryCommand).ExecuteAsync().GetAwaiter().GetResult();
                Assert.AreEqual(initialWidth, LayoutAndAssert(), "Sparse and empty results retain full column capacity.");
            }
            count = 1;
            vm.UpdateQueryAsync(new LibraryQuery(LibrarySortField.DateAdded, LibrarySortDirection.Descending,
                LibraryWatchedFilter.Unwatched, true)).GetAwaiter().GetResult();
            Assert.AreEqual(initialWidth, LayoutAndAssert());

            var pending = new TaskCompletionSource<LibraryItemsLoadResult>();
            service.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) => pending.Task;
            var loading = vm.UpdateQueryAsync(new LibraryQuery());
            Assert.IsTrue(vm.IsInitialItemsLoading);
            Assert.AreEqual(initialWidth, LayoutAndAssert(), "The width source remains arranged while result content is collapsed.");
            count = 18;
            pending.SetResult(Result());
            loading.GetAwaiter().GetResult();
            Assert.AreEqual(initialWidth, LayoutAndAssert());
        });
    }

    [TestMethod]
    public void OpenedNativeMenus_BindToLibraryQueryAndKeepSingleSelection()
    {
        RunOnSta(() =>
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            Application.Current!.Resources.MergedDictionaries.Add(LoadApplicationResources());
            var service = new TestLibraryService
            {
                LoadLibrariesAsyncHandler = (_, _) => Task.FromResult(LibraryLoadResult.Success(
                    [new LibraryItem("fixture-library", "Fixture", "movies")])),
                LoadLibraryItemsAsyncHandler = (_, _, _, _, _) => Task.FromResult(
                    LibraryItemsLoadResult.Success(Array.Empty<LibraryMediaItem>(), 0))
            };
            var session = new CurrentSessionService();
            session.SetSession(new AuthSession("http://media.local", "fixture-token", "fixture-user", "Fixture", "fixture-server"));
            var vm = new LibraryViewModel(new NavigationService(), service, session, new TestAuthSessionStore(), _ => { });
            vm.LoadAsync().GetAwaiter().GetResult();
            var page = new LibraryPage { DataContext = vm };
            var window = new Window
            {
                Width = 1100, Height = 700, Left = -10000, Top = -10000,
                ShowActivated = true, ShowInTaskbar = false, Opacity = 0, Content = page
            };
            var sort = (Button)page.FindName("SortButton");
            var filter = (Button)page.FindName("FilterButton");
            var idleColor = ((SolidColorBrush)sort.Foreground).Color;
            var activeColor = ((SolidColorBrush)page.FindResource("SettingsFocusBrush")).Color;
            void AssertIdleAppearance(Button button)
            {
                Assert.IsFalse(button.IsMouseOver, "The test pointer is outside the offscreen toolbar.");
                Assert.AreEqual(idleColor, ((SolidColorBrush)button.Foreground).Color,
                    "Retained input focus or active filters must not leave the action highlighted.");
                var chrome = (Border)VisualTreeHelper.GetChild(button, 0);
                Assert.AreEqual((byte)0, ((SolidColorBrush)chrome.Background).Color.A);
                Assert.AreEqual(new Thickness(0), chrome.BorderThickness);
                Assert.AreEqual(new CornerRadius(0), chrome.CornerRadius);
                Assert.IsNotNull(button.FocusVisualStyle, "Keyboard navigation still needs a focus cue.");
            }
            void CloseAndAssertIdle(Button button)
            {
                Assert.AreEqual(activeColor, ((SolidColorBrush)button.Foreground).Color);
                button.ContextMenu.IsOpen = false;
                Pump();
                Assert.IsFalse(button.ContextMenu.IsOpen);
                Assert.IsTrue(button.IsKeyboardFocused, "Reproduce focus restored after menu dismissal.");
                AssertIdleAppearance(button);
            }
            void AssertSwitchWithoutReopening(Button previous, Button next)
            {
                var previousClosed = false;
                var openedCount = 0;
                var closedCount = 0;
                RoutedEventHandler onPreviousClosed = (_, _) => previousClosed = true;
                RoutedEventHandler onNextOpened = (_, _) => openedCount++;
                RoutedEventHandler onNextClosed = (_, _) => closedCount++;
                previous.ContextMenu.Closed += onPreviousClosed;
                next.ContextMenu.Opened += onNextOpened;
                next.ContextMenu.Closed += onNextClosed;
                try
                {
                    previous.ContextMenu.IsOpen = false;
                    next.Focus();
                    next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpPopupClose();
                    Assert.IsTrue(previousClosed, "Exercise the old popup's delayed Closed callback.");
                    Assert.IsFalse(previous.ContextMenu.IsOpen);
                    Assert.IsTrue(next.ContextMenu.IsOpen, "Closing the old menu must not dismiss the new menu.");
                    Assert.IsTrue(next.IsKeyboardFocused || next.ContextMenu.IsKeyboardFocusWithin,
                        "Delayed closure must not steal focus from the requested menu or its trigger.");
                    Assert.AreEqual(1, openedCount, "The new menu opens once.");
                    Assert.AreEqual(0, closedCount, "The new menu stays open throughout the switch.");
                }
                finally
                {
                    previous.ContextMenu.Closed -= onPreviousClosed;
                    next.ContextMenu.Opened -= onNextOpened;
                    next.ContextMenu.Closed -= onNextClosed;
                }
            }
            sort.ContextMenu.Opacity = 0;
            filter.ContextMenu.Opacity = 0;
            try
            {
                window.Show();
                sort.Focus();
                Pump();
                Assert.IsTrue(sort.IsKeyboardFocused);
                Open(sort);
                Assert.AreSame(vm, sort.ContextMenu.DataContext);
                Assert.IsNotNull(PresentationSource.FromVisual(sort.ContextMenu), "Exercise an opened native popup presentation.");
                Assert.AreEqual("名称 ↑", vm.SortSummary);
                Assert.IsTrue(FindItem(sort, "LibrarySortTitle").IsChecked);
                Assert.IsTrue(FindItem(sort, "LibrarySortAscending").IsChecked);
                CloseAndAssertIdle(sort);
                Open(sort);
                AssertSwitchWithoutReopening(sort, filter);
                AssertSwitchWithoutReopening(filter, sort);
                sort.ContextMenu.IsOpen = false;
                Open(sort); // Reopening the same native popup must wait until its Closed event returns.

                Invoke(FindItem(sort, "LibrarySortDateAdded"));
                Assert.AreEqual(LibrarySortField.DateAdded, service.LastQuery!.SortField);
                Open(sort);
                Assert.IsFalse(FindItem(sort, "LibrarySortTitle").IsChecked);
                Assert.IsTrue(FindItem(sort, "LibrarySortDateAdded").IsChecked);
                var requests = service.LoadLibraryItemsCallCount;
                Invoke(FindItem(sort, "LibrarySortDateAdded"));
                Open(sort);
                Assert.IsTrue(FindItem(sort, "LibrarySortDateAdded").IsChecked, "Invoking a selected radio choice keeps its check.");
                Assert.AreEqual(requests, service.LoadLibraryItemsCallCount);
                Invoke(FindItem(sort, "LibrarySortDescending"));
                Assert.AreEqual("添加时间 ↓", vm.SortSummary);
                Open(sort);
                Invoke(FindItem(sort, "LibrarySortYear"));
                Assert.AreEqual(LibrarySortField.Year, service.LastQuery!.SortField);

                Open(filter);
                Assert.AreSame(vm, filter.ContextMenu.DataContext);
                CloseAndAssertIdle(filter);
                Open(filter);
                Invoke(FindItem(filter, "LibraryWatchUnwatched"));
                Open(filter);
                Invoke(FindItem(filter, "LibraryFavoriteOnly"));
                Assert.AreEqual(LibraryWatchedFilter.Unwatched, service.LastQuery!.WatchedFilter);
                Assert.IsTrue(service.LastQuery.FavoritesOnly);
                Assert.AreEqual("筛选 2", vm.FilterSummary);
                StringAssert.Contains(AutomationProperties.GetName(filter), "未观看");
                Open(filter);
                Assert.IsTrue(FindItem(filter, "LibraryWatchUnwatched").IsChecked);
                Assert.IsFalse(FindItem(filter, "LibraryWatchAll").IsChecked);
                Assert.IsTrue(FindItem(filter, "LibraryFavoriteOnly").IsChecked);
                Assert.IsFalse(FindItem(filter, "LibraryFavoriteAll").IsChecked);
                CloseAndAssertIdle(filter);
                Assert.AreEqual("筛选 2", vm.FilterSummary, "Active conditions remain visible through their count.");
                Open(filter);
                Invoke(FindItem(filter, "LibraryWatchWatched"));
                Assert.AreEqual(LibraryWatchedFilter.Watched, service.LastQuery!.WatchedFilter);
                Open(filter);
                Invoke(FindItem(filter, "LibraryWatchAll"));
                Open(filter);
                Invoke(FindItem(filter, "LibraryFavoriteAll"));
                Assert.AreEqual("筛选", vm.FilterSummary);

                requests = service.LoadLibraryItemsCallCount;
                var reset = Descendants<Button>(page).Single(button => AutomationProperties.GetAutomationId(button) == "LibraryResetQuery");
                reset.Focus();
                Pump();
                Assert.IsTrue(reset.IsKeyboardFocused);
                AssertIdleAppearance(reset);
                reset.Command.Execute(reset.CommandParameter);
                Pump();
                Assert.AreEqual(new LibraryQuery(), service.LastQuery);
                Assert.AreEqual(requests + 1, service.LoadLibraryItemsCallCount);
                Assert.AreEqual(Visibility.Collapsed, reset.Visibility);

                var pending = new TaskCompletionSource<LibraryItemsLoadResult>();
                service.LoadLibraryQueryAsyncHandler = (_, _, _, _, _, _) => pending.Task;
                vm.SortField = LibrarySortField.Year;
                Pump();
                var refresh = Descendants<Button>(page).Single(button => AutomationProperties.GetAutomationId(button) == "LibraryRefreshQuery");
                Assert.IsTrue(sort.IsEnabled);
                Assert.IsTrue(filter.IsEnabled);
                Assert.IsFalse(refresh.IsEnabled, "Refresh retains the existing command's loading guard.");
                pending.SetResult(LibraryItemsLoadResult.Success(Array.Empty<LibraryMediaItem>(), 0));
                Pump();
                Assert.IsTrue(refresh.IsEnabled);
                refresh.Focus();
                Pump();
                Assert.IsTrue(refresh.IsKeyboardFocused);
                AssertIdleAppearance(refresh);
                pending = new TaskCompletionSource<LibraryItemsLoadResult>();
                refresh.Command.Execute(refresh.CommandParameter);
                Pump();
                Assert.IsFalse(refresh.IsEnabled);
                Assert.AreEqual(0.42, refresh.Opacity, 0.01);
                pending.SetResult(LibraryItemsLoadResult.Success(Array.Empty<LibraryMediaItem>(), 0));
                Pump();
                Assert.IsTrue(refresh.IsEnabled);
                refresh.Focus();
                Pump();
                Assert.IsTrue(refresh.IsKeyboardFocused);
                AssertIdleAppearance(refresh);
                Open(sort);
                filter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                page.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                PumpPopupClose();
                Assert.IsFalse(sort.ContextMenu.IsOpen);
                Assert.IsFalse(filter.ContextMenu.IsOpen, "An outside click cancels a pending menu switch.");
                Open(sort);
                filter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Content = null;
                PumpPopupClose();
                Assert.IsFalse(sort.ContextMenu.IsOpen, "Leaving the page closes its native menu.");
                Assert.IsFalse(filter.ContextMenu.IsOpen, "Leaving the page cancels a pending menu switch.");
            }
            finally
            {
                sort.ContextMenu.IsOpen = false;
                filter.ContextMenu.IsOpen = false;
                window.Close();
            }
        });
    }

    private static void Open(Button button)
    {
        var frame = new DispatcherFrame();
        RoutedEventHandler onOpened = (_, _) => frame.Continue = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timeout.Tick += (_, _) => frame.Continue = false;
        button.ContextMenu.Opened += onOpened;
        try
        {
            timeout.Start();
            button.Focus();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!button.ContextMenu.IsOpen) Dispatcher.PushFrame(frame);
        }
        finally
        {
            timeout.Stop();
            button.ContextMenu.Opened -= onOpened;
        }
        Pump();
        Assert.IsTrue(button.ContextMenu.IsOpen);
    }

    private static MenuItem FindItem(Button button, string id) => button.ContextMenu.Items.OfType<MenuItem>()
        .Single(item => AutomationProperties.GetAutomationId(item) == id);

    private static void Invoke(MenuItem item)
    {
        ((IInvokeProvider)new MenuItemAutomationPeer(item)).Invoke();
        Pump();
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

    private static void PumpPopupClose()
    {
        // Native popup fade-out defers Closed by 150 ms; keep processing the dispatcher beyond it.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
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
            "<ResourceDictionary>", "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">",
            StringComparison.Ordinal));
    }
}
