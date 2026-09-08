using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Home;
using EmbyPlayer.Core.Images;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class AppShellPersonIntegrationTests
{
    // Opt-in release illustration capture. Production pages, synthetic data, no HTTP or MPV.
    [TestMethod]
    [TestCategory("ReleaseScreenshots")]
    public void CapturePublicProductScreenshots()
    {
        if (Environment.GetEnvironmentVariable("JEBY_CAPTURE_SCREENSHOTS") != "1") return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "EmbyPlayer.sln"))) root = root.Parent;
                Assert.IsNotNull(root);
                var output = Path.Combine(root.FullName, "docs", "images");
                Directory.CreateDirectory(output);
                var artwork = new ReleaseArtwork();
                var titles = new[] { "远山来信", "深蓝旅程", "城市微光", "星河之间", "夏日回声", "风的方向" };
                var cards = titles.Select((title, i) => new MediaCard($"demo-{i}", title, "Movie", 2026,
                    $"poster-{i}", 22 + i * 8, "原创演示媒体", heroImageUrl: $"backdrop-{i}",
                    resumePositionTicks: TimeSpan.FromMinutes(12 + i).Ticks)).ToArray();
                var service = new TestHomeService { LoadHomeAsyncHandler = (_, _) => Task.FromResult(HomeLoadResult.Success(
                    new HomeData("Demo", cards, cards, [new("movies", "电影", "movies"), new("series", "电视剧", "tvshows")],
                    [HomeMediaSection.Success("movies", "电影", cards)]))) };
                var context = new Context(homeService: service);
                ImageLoaderServices.Configure(artwork, context.Sessions);
                context.DetailService.LoadDetailAsyncHandler = (_, id, _) => Task.FromResult(MediaDetailLoadResult.Success(
                    new MediaDetail(id, titles[0], "Movie", 2026, TimeSpan.FromMinutes(96).Ticks,
                        "沿着山脊与河流，寻找一封未曾寄出的信。穿过清晨的薄雾，在旅途中重新发现日常生活的温度。\n本页面使用虚构媒体与原创几何插画，展示播放器的实际界面。",
                        ["旅行", "剧情"], 8.6, 35, TimeSpan.FromMinutes(34).Ticks, "poster-0", "backdrop-0")));
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(icons);
                var iconStyles = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(iconStyles);
                var resources = LoadResources();
                app.Resources.MergedDictionaries.Add(resources);
                var shell = new AppShell { DataContext = context.Shell };
                var window = new Window { Content = shell, Width = 1440, Height = 960, WindowStyle = WindowStyle.None,
                    Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
                try
                {
                    context.Shell.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                    context.Sessions.SetSession(new("http://media.local", "test-token", "user-1", "Demo", "server-1"));
                    context.Navigation.NavigateTo(AppPage.Home);
                    window.Show(); Pump(); Pump();
                    SaveReleaseVisual(shell, Path.Combine(output, "home.png"));
                    context.Navigation.NavigateTo(AppPage.Detail, "demo-0"); Pump(); Pump();
                    SaveReleaseVisual(shell, Path.Combine(output, "details.png"));
                    context.Navigation.NavigateTo(AppPage.Settings); Pump(); Pump();
                    SaveReleaseVisual(shell, Path.Combine(output, "settings.png"));
                    Assert.AreEqual(3, Directory.GetFiles(output, "*.png").Count(path =>
                        new[] { "home.png", "details.png", "settings.png" }.Contains(Path.GetFileName(path))));
                }
                finally
                {
                    window.Close();
                    app.Resources.MergedDictionaries.Remove(resources);
                    app.Resources.MergedDictionaries.Remove(icons);
                    app.Resources.MergedDictionaries.Remove(iconStyles);
                }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static void SaveReleaseVisual(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class ReleaseArtwork : IImageService
    {
        private readonly Dictionary<string, byte[]> images = new();
        public Task<ImageLoadResult> LoadImageAsync(AuthSession session, string imageUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!images.TryGetValue(imageUrl, out var bytes))
            {
                var index = int.Parse(imageUrl.Split('-')[1], CultureInfo.InvariantCulture);
                var wide = imageUrl.StartsWith("backdrop", StringComparison.Ordinal);
                var width = wide ? 1600 : 480; var height = wide ? 900 : 720;
                var hues = new[] { "#426C69", "#315F86", "#7D5667", "#4A527C", "#967050", "#5B7365" };
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    var color = (Color)ColorConverter.ConvertFromString(hues[index]);
                    drawing.DrawRectangle(new LinearGradientBrush(color, Color.FromRgb(9, 22, 31), 90), null, new Rect(0, 0, width, height));
                    drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(240, 218, 164)), null, new Point(width * .73, height * .29), width * .1, width * .1);
                    for (var layer = 0; layer < 4; layer++)
                    {
                        var geometry = new StreamGeometry();
                        using (var shape = geometry.Open())
                        {
                            shape.BeginFigure(new Point(0, height), true, true);
                            shape.LineTo(new Point(0, height * (.48 + layer * .1)), true, false);
                            shape.BezierTo(new Point(width * .25, height * (.2 + layer * .16)), new Point(width * .58, height * (.83 - layer * .04)), new Point(width, height * (.42 + layer * .13)), true, false);
                            shape.LineTo(new Point(width, height), true, false);
                        }
                        drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb((byte)(23 + layer * 5), (byte)(53 + layer * 7), (byte)(57 + layer * 6))), null, geometry);
                    }
                    if (!wide)
                    {
                        var title = new[] { "远山来信", "深蓝旅程", "城市微光", "星河之间", "夏日回声", "风的方向" }[index];
                        drawing.DrawText(new FormattedText(title, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                            new Typeface("Microsoft YaHei"), 52, Brushes.White, 1), new Point(45, height - 155));
                        drawing.DrawText(new FormattedText("JEBY · DEMO COLLECTION", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), 16, Brushes.LightGray, 1), new Point(48, height - 72));
                    }
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream); bytes = stream.ToArray(); images[imageUrl] = bytes;
            }
            return Task.FromResult(ImageLoadResult.Success(bytes, "image/png"));
        }
    }
}
