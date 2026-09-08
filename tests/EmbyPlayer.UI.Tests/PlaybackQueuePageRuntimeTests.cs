using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbyPlayer.UI.Pages;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PlaybackQueuePageRuntimeTests
{
    [TestMethod]
    public void QueuePage_KeyboardPlayAndReorderControlsOperateOnActualQueue() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        foreach (var id in new[] { "a", "b", "c" }) queue.Add(new(id, "Movie " + id, "Movie", 2026));
        var completion = new TaskCompletionSource<string?>();
        string? requestedId = null;
        using var vm = new PlaybackQueueViewModel(queue, (item, _) => { requestedId = item.ItemId; return completion.Task; });
        WithPage(vm, 620, 620, page =>
        {
            Assert.IsFalse(Buttons(page, "Queue.MoveUp")[0].IsEnabled);
            PressEnter(Buttons(page, "Queue.MoveDown")[0]);
            CollectionAssert.AreEqual(new[] { "b", "a", "c" }, queue.Snapshot.Pending.Select(item => item.ItemId).ToArray());
            var position = Descendants<ComboBox>(page).Last();
            position.IsDropDownOpen = true;
            Pump();
            position.SelectedItem = 1;
            position.IsDropDownOpen = false;
            Pump();
            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, queue.Snapshot.Pending.Select(item => item.ItemId).ToArray());
            PressEnter(Buttons(page, "Queue.Play")[0]);
            Assert.AreEqual("c", requestedId);
            Assert.IsTrue(vm.IsBusy);
            Assert.IsTrue(Buttons(page, "Queue.Remove").All(button => !button.IsEnabled));
            Assert.IsTrue(Descendants<ComboBox>(page).All(combo => !combo.IsEnabled));
            completion.SetResult("Server unavailable; retry this item");
            Pump();
            Assert.IsFalse(vm.IsBusy);
            Assert.IsTrue(vm.HasError);
            Assert.AreEqual(3, queue.Snapshot.Pending.Count);
            PressEnter(Buttons(page, "Queue.Remove")[1]);
            CollectionAssert.AreEqual(new[] { "c", "a" }, queue.Snapshot.Pending.Select(item => item.ItemId).ToArray());
            PressEnter(Buttons(page, "Queue.ClearPending").Single());
            Assert.IsTrue(vm.IsEmpty);
        });
        await Task.CompletedTask;
    });

    [TestMethod]
    public void EmbeddedQueue_LongTitlesAndLastRowRemainReachableInSmallPopup() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        queue.SetCurrent(new("current", "当前播放的影片", "Movie"), queue.Snapshot.SessionVersion);
        for (var index = 1; index <= 5; index++) queue.Add(new(index.ToString(),
            $"第 {index} 项：很长的影片标题用于确认队列在紧凑窗口中仍然可以清晰显示并调整播放顺序", "Episode", 2026));
        using var vm = new PlaybackQueueViewModel(queue, (_, _) => Task.FromResult<string?>(null), () => { });
        WithPage(vm, 480, 540, page =>
        {
            page.IsEmbedded = true;
            Pump();
            Assert.AreEqual(Visibility.Collapsed, ((Button)page.FindName("QueueBackButton")).Visibility);
            var scroll = (ScrollViewer)page.FindName("QueueScrollViewer");
            Assert.IsTrue(scroll.ScrollableHeight > 0);
            var firstTitle = Descendants<TextBlock>(Buttons(page, "Queue.Play")[0])
                .First(block => block.Text.Contains("第 1 项", StringComparison.Ordinal));
            Assert.AreEqual(TextTrimming.CharacterEllipsis, firstTitle.TextTrimming);
            Assert.IsTrue(firstTitle.ActualWidth > 30);
            SavePreview(page);
            scroll.ScrollToBottom();
            Pump();
            var remove = Buttons(page, "Queue.Remove").Last();
            var bounds = remove.TransformToAncestor(scroll).TransformBounds(new Rect(remove.RenderSize));
            Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight + 0.1);
            Assert.IsTrue(bounds.Right <= scroll.ViewportWidth + 0.1);
            PressEnter(remove);
            Assert.AreEqual(4, queue.Snapshot.Pending.Count);
            var toggle = (ToggleButton)page.FindName("QueueAutoPlayToggle");
            toggle.IsChecked = false;
            Assert.IsFalse(queue.Snapshot.AutoPlayEnabled);
            PressEnter(Buttons(page, "Queue.ClearPending").Single());
            Assert.AreEqual("current", vm.CurrentItem!.ItemId);
            Assert.IsTrue(vm.IsPendingEmpty);
        });
        await Task.CompletedTask;
    });

    private static Button[] Buttons(DependencyObject page, string id) => Descendants<Button>(page)
        .Where(button => AutomationProperties.GetAutomationId(button) == id).ToArray();

    private static void PressEnter(Button button)
    {
        Assert.IsTrue(button.IsEnabled && button.Focusable);
        button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button), 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Pump();
    }

    private static void WithPage(PlaybackQueueViewModel vm, double width, double height, Action<PlaybackQueuePage> assertions)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var geometries = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
        var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
        app.Resources.MergedDictionaries.Add(geometries);
        app.Resources.MergedDictionaries.Add(icons);
        var resources = LoadResources();
        app.Resources.MergedDictionaries.Add(resources);
        var page = new PlaybackQueuePage { DataContext = vm };
        var window = new Window { Width = width, Height = height, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = page };
        try { window.Show(); Pump(); assertions(page); }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(resources);
            app.Resources.MergedDictionaries.Remove(icons);
            app.Resources.MergedDictionaries.Remove(geometries);
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

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln"))) directory = directory.Parent;
        return directory!.FullName;
    }

    private static ResourceDictionary LoadResources()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRoot(), "src", "EmbyPlayer.App", "App.xaml"));
        var start = xaml.IndexOf("<ResourceDictionary>", StringComparison.Ordinal);
        var end = xaml.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal);
        return (ResourceDictionary)XamlReader.Parse(xaml[start..(end + "</ResourceDictionary>".Length)].Replace(
            "<ResourceDictionary>", "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:controls=\"clr-namespace:EmbyPlayer.UI.Controls;assembly=EmbyPlayer.UI\" "
            + "xmlns:primitives=\"clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework\">", StringComparison.Ordinal));
    }

    private static void SavePreview(FrameworkElement page)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var directory = Path.Combine(FindRoot(), ".tmp", "personal-playback", "queue");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, "queue-preview.png"));
        encoder.Save(output);
    }
}
