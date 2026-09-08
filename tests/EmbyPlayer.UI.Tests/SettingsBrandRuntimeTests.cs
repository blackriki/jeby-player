using System.Windows;
using System.Windows.Controls;
using ApplicationIdentity = EmbyPlayer.Core.ApplicationIdentity;
using EmbyPlayer.UI.Pages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

public sealed partial class SettingsCacheRuntimeTests
{
    [DataTestMethod]
    [DataRow(1100, 700)]
    [DataRow(1280, 800)]
    public void About_ShowsAssemblyVersionAndBrandWithinWindow(int width, int height)
    {
        RunOnSta(() =>
        {
            var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var geometries = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Resources/Icons.xaml", UriKind.Relative) };
            var icons = new ResourceDictionary { Source = new Uri("/EmbyPlayer.UI;component/Styles/IconButtons.xaml", UriKind.Relative) };
            application.Resources.MergedDictionaries.Add(geometries);
            application.Resources.MergedDictionaries.Add(icons);
            var resources = LoadResources();
            application.Resources.MergedDictionaries.Add(resources);
            var page = new SettingsPage();
            var window = new Window
            {
                Content = page, Width = width, Height = height, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            try
            {
                window.Show();
                Pump();
                ((RadioButton)page.FindName("AboutCategoryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                var about = (Grid)page.FindName("AboutPanel");
                Assert.IsTrue(about.IsVisible);
                var texts = Descendants<TextBlock>(about).ToArray();
                CollectionAssert.Contains(texts.Select(text => text.Text).ToArray(), ApplicationIdentity.Name);
                foreach (var value in new[] { ApplicationIdentity.Version, ApplicationIdentity.Build })
                {
                    var text = texts.Single(text => text.Text == value);
                    var position = text.TranslatePoint(new Point(), about);
                    Assert.IsTrue(text.ActualWidth > 0);
                    Assert.IsTrue(position.X >= 0 && position.X + text.ActualWidth <= about.ActualWidth + 1,
                        "Version information must stay within the about panel at minimum window size.");
                }
                Assert.IsFalse(Descendants<TextBlock>(page).Any(text => text.Text is
                    "暂不可用" or "版本信息暂不可用" or "将在下一阶段接入" or "Emby Player"));
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
}
