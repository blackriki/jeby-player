using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Pages;

public partial class SettingsPage : UserControl
{
    private static readonly SearchItem[] SearchItems =
    {
        new("自动播放下一集", "当前单集自然播放结束后，自动进入下一集。", "Playback"),
        new("记住上次音量", "下次播放时沿用最近一次调整后的音量。", "Playback"),
        new("默认音量", "首次播放没有历史音量时使用。", "Playback"),
        new("快进 / 快退步长", "调整方向键跳转秒数。", "Playback"),
        new("控制栏自动隐藏", "设置播放器控制层等待时间。", "Playback"),
        new("播放器快捷键", "自定义播放、快进退、音量、字幕和音轨组合键。", "Playback"),
        new("默认字幕语言", "优先匹配字幕语言。", "Tracks"),
        new("默认开启字幕", "新媒体字幕开关；同剧继续沿用手动选择。", "Tracks"),
        new("默认音轨语言", "优先匹配音轨语言。", "Tracks"),
        new("当前连接", "查看服务器和登录账户。", "Account"),
        new("搜索历史", "最近搜索记录保存在本机，可在搜索页管理或清除。", "LocalData"),
        new("图片缓存", "查看图片内存占用并清理缓存。", "LocalData"),
        new("本地搜索索引", "查看索引磁盘占用、清理或重建当前账户索引。", "LocalData"),
        new("导出诊断日志", "创建经过脱敏处理的排障压缩包。", "LocalData"),
        new(EmbyPlayer.Core.ApplicationIdentity.Name, "查看版本与构建信息。", "About")
    };

    private bool isInitialized;
    private SettingsViewModel? subscribedViewModel;

    public SettingsPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        isInitialized = true;
        ShowCategory("Playback");
        SubscribeToViewModel(DataContext as SettingsViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        subscribedViewModel?.CacheManagement.Cancel();
        subscribedViewModel?.Shortcuts.CancelCapture();
        SubscribeToViewModel(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SubscribeToViewModel(e.NewValue as SettingsViewModel);
        }
    }

    private void SubscribeToViewModel(SettingsViewModel? viewModel)
    {
        if (ReferenceEquals(subscribedViewModel, viewModel))
        {
            return;
        }

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel.CacheManagement.PropertyChanged -= OnCachePropertyChanged;
        }

        subscribedViewModel = viewModel;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            subscribedViewModel.CacheManagement.PropertyChanged += OnCachePropertyChanged;
        }
    }

    private void OnCachePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CacheManagementViewModel.IsConfirmationOpen)
            && subscribedViewModel?.CacheManagement.IsConfirmationOpen == true)
        {
            Dispatcher.BeginInvoke(() =>
            {
                CacheCancelButton.BringIntoView();
                CacheCancelButton.Focus();
            }, DispatcherPriority.Input);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsRestoreDefaultsDialogOpen)
            && subscribedViewModel?.IsRestoreDefaultsDialogOpen == true)
        {
            Dispatcher.BeginInvoke(
                () => RestoreCancelButton.Focus(),
                DispatcherPriority.Input);
        }
        else if (e.PropertyName == nameof(SettingsViewModel.IsUnsavedLeaveDialogOpen)
            && subscribedViewModel?.IsUnsavedLeaveDialogOpen == true)
        {
            Dispatcher.BeginInvoke(
                () => ContinueEditingButton.Focus(),
                DispatcherPriority.Input);
        }
        else if (e.PropertyName == nameof(SettingsViewModel.IsAccountActionDialogOpen)
            && subscribedViewModel?.IsAccountActionDialogOpen == true)
        {
            Dispatcher.BeginInvoke(
                () => AccountActionCancelButton.Focus(),
                DispatcherPriority.Input);
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width >= 1500)
        {
            SidebarColumn.Width = new GridLength(290);
            LayoutGapColumn.Width = new GridLength(34);
            return;
        }

        if (e.NewSize.Width >= 1250)
        {
            SidebarColumn.Width = new GridLength(276);
            LayoutGapColumn.Width = new GridLength(34);
            return;
        }

        SidebarColumn.Width = new GridLength(250);
        LayoutGapColumn.Width = new GridLength(26);
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string category })
        {
            SettingsSearchTextBox.Text = string.Empty;
            ShowCategory(category);
        }
    }

    private void ShowCategory(string category)
    {
        subscribedViewModel?.Shortcuts.CancelCapture();
        SearchResultsPanel.Visibility = Visibility.Collapsed;
        PlaybackPanel.Visibility = category == "Playback" ? Visibility.Visible : Visibility.Collapsed;
        TracksPanel.Visibility = category == "Tracks" ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility = category == "Account" ? Visibility.Visible : Visibility.Collapsed;
        LocalDataPanel.Visibility = category == "LocalData" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = category == "About" ? Visibility.Visible : Visibility.Collapsed;

        PlaybackCategoryButton.IsChecked = category == "Playback";
        TracksCategoryButton.IsChecked = category == "Tracks";
        AccountCategoryButton.IsChecked = category == "Account";
        LocalDataCategoryButton.IsChecked = category == "LocalData";
        AboutCategoryButton.IsChecked = category == "About";
        ContentScroller.ScrollToTop();
    }

    private void OnSettingsSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!isInitialized)
        {
            return;
        }

        var keyword = SettingsSearchTextBox.Text.Trim();
        if (keyword.Length == 0)
        {
            var selectedCategory = PlaybackCategoryButton.IsChecked == true ? "Playback"
                : TracksCategoryButton.IsChecked == true ? "Tracks"
                : AccountCategoryButton.IsChecked == true ? "Account"
                : LocalDataCategoryButton.IsChecked == true ? "LocalData"
                : "About";
            ShowCategory(selectedCategory);
            return;
        }

        PlaybackPanel.Visibility = Visibility.Collapsed;
        TracksPanel.Visibility = Visibility.Collapsed;
        AccountPanel.Visibility = Visibility.Collapsed;
        LocalDataPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        SearchResultsPanel.Visibility = Visibility.Visible;
        SearchResultsList.Children.Clear();

        var matches = SearchItems
            .Where(item => item.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                || item.Description.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        SearchSummaryText.Text = matches.Length == 0
            ? $"没有找到与“{keyword}”相关的设置。"
            : $"找到 {matches.Length} 项与“{keyword}”相关的设置。";
        NoSearchResultsPanel.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var match in matches)
        {
            SearchResultsList.Children.Add(CreateSearchResult(match));
        }

        ContentScroller.ScrollToTop();
    }

    private Button CreateSearchResult(SearchItem item)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = item.Title,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        copy.Children.Add(new TextBlock
        {
            Text = item.Description,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            FontSize = 13
        });
        content.Children.Add(copy);

        var category = new TextBlock
        {
            Text = CategoryDisplayName(item.Category),
            Foreground = (System.Windows.Media.Brush)FindResource("SettingsMutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(category, 1);
        content.Children.Add(category);

        var button = new Button
        {
            Tag = item.Category,
            Content = content,
            Style = (Style)FindResource("SettingsSearchResultButton"),
            ToolTip = $"转到{item.Title}"
        };
        button.Click += OnSearchResultClick;
        return button;
    }

    private void OnSearchResultClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string category })
        {
            SettingsSearchTextBox.Text = string.Empty;
            ShowCategory(category);
        }
    }

    private static string CategoryDisplayName(string category) => category switch
    {
        "Playback" => "播放",
        "Tracks" => "字幕与音轨",
        "Account" => "服务器与账户",
        "LocalData" => "本地数据",
        _ => "关于"
    };

    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (viewModel.Shortcuts.Capture(key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && viewModel.IsAnyDialogOpen)
        {
            viewModel.CancelDialogCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && viewModel.CacheManagement.IsConfirmationOpen)
        {
            viewModel.CacheManagement.CancelConfirmationCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var isBackGesture = effectiveKey == Key.BrowserBack
            || (effectiveKey == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (!isBackGesture)
        {
            return;
        }

        viewModel.NavigateHomeCommand.Execute(null);
        e.Handled = true;
    }

    private sealed record SearchItem(string Title, string Description, string Category);

    private void OnShortcutCaptureLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is Button { DataContext: ShortcutSettingRow row }
            && ReferenceEquals(viewModel.Shortcuts.CapturingRow, row)) viewModel.Shortcuts.CancelCapture();
    }
}
