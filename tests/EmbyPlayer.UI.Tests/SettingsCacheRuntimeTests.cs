using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed partial class SettingsCacheRuntimeTests
{
    [TestMethod]
    public void CacheButtons_UseLiveBindingsConfirmationKeyboardAndScrollableLayout()
    {
        RunOnSta(() =>
        {
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // The standalone XAML reader resolves SettingsSidebarIcon's BasedOn eagerly.
            var geometries = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
            var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
            application.Resources.MergedDictionaries.Add(geometries);
            application.Resources.MergedDictionaries.Add(icons);
            var resources = LoadResources();
            application.Resources.MergedDictionaries.Add(resources);
            var service = new CacheManagementViewModelTests.CacheService();
            var sessions = new CurrentSessionService();
            sessions.SetSession(CacheManagementViewModelTests.Session);
            var preferences = new TestAppSettingsService();
            var store = new TestAuthSessionStore();
            var navigation = new NavigationService();
            navigation.NavigateTo(AppPage.Settings);
            var viewModel = new SettingsViewModel(navigation, preferences, sessions,
                new TestMediaLibraryScanService(), store, new AccountSessionService(store, sessions, preferences), _ => { }, service);
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var page = new SettingsPage { DataContext = viewModel };
            var window = new Window
            {
                Content = page, Width = 980, Height = 680, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            try
            {
                window.Show();
                Pump();
                ((RadioButton)page.FindName("LocalDataCategoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                var panel = (StackPanel)page.FindName("CacheManagementPanel");
                Assert.IsTrue(panel.IsVisible);
                Assert.AreSame(viewModel.CacheManagement, panel.DataContext);
                var texts = Descendants<TextBlock>(panel).Select(text => text.Text).ToArray();
                CollectionAssert.Contains(texts, "内存占用 2.0 KB");
                CollectionAssert.Contains(texts, "磁盘占用 4.0 KB");
                var clear = FindButton(panel, "清理图片缓存");
                clear.BringIntoView();
                Pump();
                Assert.IsTrue(clear.IsEnabled && clear.Focusable && clear.IsTabStop);
                ActivateWithEnter(clear);
                Pump();
                Assert.IsTrue(viewModel.CacheManagement.IsConfirmationOpen);
                Assert.AreEqual(0, service.ImageClears);
                Assert.IsFalse(clear.IsEnabled);
                var cancel = FindButton(panel, "取消缓存操作");
                Assert.IsTrue(cancel.IsVisible && cancel.IsEnabled);
                Assert.AreSame(cancel, FocusManager.GetFocusedElement(window));
                var confirm = FindButton(panel, "确认缓存操作");
                var scroll = Ancestor<ScrollViewer>(panel);
                Assert.IsNotNull(scroll);
                Assert.IsTrue(scroll.ScrollableHeight > 0);
                var bounds = confirm.TransformToAncestor(scroll).TransformBounds(new Rect(confirm.RenderSize));
                Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight + 2, "Confirmation must be brought into the viewport.");
                ActivateWithEnter(confirm);
                Pump();
                Assert.AreEqual(1, service.ImageClears);
                Assert.IsFalse(viewModel.CacheManagement.IsConfirmationOpen);
                Assert.IsFalse(viewModel.IsDirty, "Cache maintenance must not edit player preferences.");
                Assert.IsTrue(Descendants<TextBlock>(panel).Any(text => text.Text == "内存占用 0 B"));
                Assert.AreEqual(0, store.ClearCallCount);
                ActivateWithEnter(FindButton(panel, "清理搜索索引"));
                Pump();
                page.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(page), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump();
                Assert.IsFalse(viewModel.CacheManagement.IsConfirmationOpen);
                Assert.AreEqual(0, service.IndexClears);
            }
            finally
            {
                window.Close();
                application.Resources.MergedDictionaries.Remove(resources);
                application.Resources.MergedDictionaries.Remove(icons);
                application.Resources.MergedDictionaries.Remove(geometries);
            }
        });
    }

    private static Button FindButton(DependencyObject root, string name) => Descendants<Button>(root)
        .Single(button => AutomationProperties.GetName(button) == name);

    private static void ActivateWithEnter(Button button) => button.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });

    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
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
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private static ResourceDictionary LoadResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var xaml = File.ReadAllText(Path.Combine(directory.FullName, "src", "EmbyPlayer.App", "App.xaml"));
        const string start = "<ResourceDictionary>";
        const string end = "</ResourceDictionary>";
        var dictionary = xaml[xaml.IndexOf(start, StringComparison.Ordinal)..(xaml.LastIndexOf(end, StringComparison.Ordinal) + end.Length)]
            .Replace(start, "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
                + "xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" "
                + "xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">", StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(dictionary);
    }
}
