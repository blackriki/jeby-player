using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EmbyPlayer.App.Security;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Emby;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.Services;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.App;

public partial class MainWindow : Window, IPlayerWindowHost
{
    private const double TitleBarHeight = 40d;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcDestroy = 0x0082;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const string TestDataRootEnvironmentVariable = "EMBYPLAYER_TEST_DATA_ROOT";

    private readonly AppShellViewModel appShellViewModel;
    private readonly HttpClient httpClient;
    private readonly MainWindowLifecycleCoordinator lifecycleCoordinator;
    private readonly IPlayerService playerService;
    private readonly ILocalPlaybackPreviewService localPlaybackPreviewService;
    private readonly bool isReleaseSmoke;
    private HwndSource? windowSource;
    private HwndSourceHook? windowMessageHook;
    private bool allowWindowClose;
    private bool isPlayerFullscreen;
    private bool normalCloseAccepted;
    private bool hasPlayerWindowLayout;
    private Rect? browsingWindowBounds;

    public MainWindow()
    {
        InitializeComponent();
        isReleaseSmoke = ReleaseSmokeLaunchPolicy.IsReleaseSmoke(Environment.GetCommandLineArgs());
        lifecycleCoordinator = new MainWindowLifecycleCoordinator(ApplicationDiagnosticLog.Shared);

        httpClient = new HttpClient(new EmbyApiDiagnosticHandler(new HttpClientHandler()));
        var navigationService = new NavigationService();
        var appSettingsService = CreateAppSettingsService();
        var currentSessionService = new CurrentSessionService();
        var deviceIdService = new SettingsDeviceIdService(appSettingsService);
        var authSessionStore = new AuthSessionStore(
            appSettingsService,
            new WindowsCredentialAuthSessionStore());
        var accountSessionService = new AccountSessionService(
            authSessionStore,
            currentSessionService,
            appSettingsService);
        var serverConnectionService = new EmbyServerConnectionService(httpClient);
        var authenticationService = new EmbyAuthenticationService(httpClient, deviceIdService);
        var authSessionValidator = new EmbyAuthSessionValidator(httpClient, deviceIdService);
        var homeService = new EmbyHomeService(httpClient, deviceIdService);
        var libraryService = new EmbyLibraryService(httpClient, deviceIdService);
        var mediaLibraryScanService = new EmbyMediaLibraryScanService(httpClient, deviceIdService);
        var localMediaSearchIndex = new LocalMediaSearchIndex(
            httpClient,
            deviceIdService,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmbyPlayer",
                "search-index"));
        var searchService = new EmbySearchService(
            httpClient,
            deviceIdService,
            localMediaSearchIndex);
        var mediaDetailService = new EmbyMediaDetailService(httpClient, deviceIdService);
        var similarMediaService = new EmbySimilarMediaService(httpClient, deviceIdService);
        var personService = new EmbyPersonService(httpClient, deviceIdService);
        var itemUserDataService = new EmbyItemUserDataService(httpClient, deviceIdService);
        var imageService = new EmbyImageService(
            httpClient,
            deviceIdService,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EmbyPlayer",
                "image-cache"));
        var seriesService = new EmbySeriesService(httpClient, deviceIdService);
        var nextEpisodeService = new EmbyNextEpisodeService(httpClient, deviceIdService);
        var playbackService = new EmbyPlaybackService(httpClient, deviceIdService);
        var playbackReportService = new EmbyPlaybackReportService(httpClient, deviceIdService);
        var playbackQueue = new EmbyPlayer.Core.PlaybackQueue.InMemoryPlaybackQueueService();
        var watchLaterStore = new EmbyPlayer.Core.WatchLater.FileWatchLaterStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyPlayer", "WatchLater"));
        playerService = new MpvPlayerService();
        localPlaybackPreviewService = new MpvPlaybackPreviewService();
        var serverConnectionViewModel = new ServerConnectionViewModel(
            navigationService,
            serverConnectionService,
            appSettingsService);
        var loginViewModel = new LoginViewModel(
            navigationService,
            authenticationService,
            currentSessionService,
            authSessionStore,
            appSettingsService,
            accountSessionService);
        var homeViewModel = new HomeViewModel(
            navigationService,
            homeService,
            playbackService,
            currentSessionService,
            authSessionStore,
            loginViewModel.ShowError,
            watchLaterStore: watchLaterStore,
            playbackQueue: playbackQueue);
        var libraryViewModel = new LibraryViewModel(
            navigationService,
            libraryService,
            currentSessionService,
            authSessionStore,
            loginViewModel.ShowError);
        var searchViewModel = new SearchViewModel(
            navigationService,
            searchService,
            currentSessionService,
            authSessionStore,
            appSettingsService,
            loginViewModel.ShowError);
        var mediaDetailViewModel = new MediaDetailViewModel(
            navigationService,
            mediaDetailService,
            similarMediaService,
            itemUserDataService,
            seriesService,
            playbackService,
            currentSessionService,
            authSessionStore,
            loginViewModel.ShowError,
            localMediaSearchIndex,
            watchLaterStore,
            playbackQueue);
        var playerViewModel = new PlayerViewModel(
            navigationService,
            playerService,
            playbackReportService,
            currentSessionService,
            authSessionStore,
            loginViewModel.ShowError,
            new PlaybackReportScheduler(),
            appSettingsService,
            nextEpisodeService,
            playbackService,
            playbackQueue,
            new EmbyPlaybackPreviewService(httpClient, deviceIdService),
            localPlaybackPreviewService);
        var watchLaterViewModel = new WatchLaterViewModel(navigationService, watchLaterStore,
            currentSessionService, loginViewModel.ShowError);
        var personViewModel = new PersonViewModel(navigationService, personService,
            currentSessionService, authSessionStore, loginViewModel.ShowError);
        var cacheManagementService = new EmbyCacheManagementService(imageService, localMediaSearchIndex);
        ImageLoaderServices.Configure(imageService, currentSessionService);
        appShellViewModel = new AppShellViewModel(
            navigationService,
            serverConnectionViewModel,
            loginViewModel,
            homeViewModel,
            libraryViewModel,
            searchViewModel,
            mediaDetailViewModel,
            playerViewModel,
            appSettingsService,
            authSessionStore,
            authSessionValidator,
            currentSessionService,
            accountSessionService,
            mediaLibraryScanService,
            localMediaSearchIndex,
            ApplicationDiagnosticLog.Shared,
            cacheManagementService,
            personViewModel,
            watchLaterViewModel,
            playbackQueue);

        Shell.DataContext = appShellViewModel;
        DataContext = appShellViewModel;
        appShellViewModel.PropertyChanged += OnAppShellPropertyChanged;
        appShellViewModel.SettingsViewModel.WindowCloseApproved += OnWindowCloseApproved;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        Dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
        Dispatcher.ShutdownFinished += OnDispatcherShutdownFinished;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        lifecycleCoordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.SourceInitialized);
        var handle = new WindowInteropHelper(this).Handle;
        windowSource = HwndSource.FromHwnd(handle);
        if (windowSource is not null)
        {
            windowSource.Disposed += OnWindowSourceDisposed;
            windowMessageHook = WindowMessageHook;
            windowSource.AddHook(windowMessageHook);
        }

        ApplyDarkWindowChrome();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        lifecycleCoordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.Loaded);
        try
        {
            await appShellViewModel.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            appShellViewModel.RecoverFromStartupFailure(exception);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        lifecycleCoordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.Unloaded);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        lifecycleCoordinator.RecordLifecycleEvent(
            MainWindowLifecycleEvent.VisibilityChanged,
            isVisible: IsVisible);
    }

    private void OnWindowSourceDisposed(object? sender, EventArgs e)
    {
        lifecycleCoordinator.RecordLifecycleEvent(
            MainWindowLifecycleEvent.HwndSourceDisposed,
            normalCloseAccepted: normalCloseAccepted);

        if (sender is HwndSource disposedSource)
        {
            disposedSource.Disposed -= OnWindowSourceDisposed;
            if (ReferenceEquals(windowSource, disposedSource))
            {
                windowSource = null;
                windowMessageHook = null;
            }
        }

        ScheduleUnexpectedWindowShutdown(MainWindowDestructionTrigger.HwndSourceDisposed);
    }

    private void OnDispatcherShutdownStarted(object? sender, EventArgs e)
    {
        lifecycleCoordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.DispatcherShutdownStarted);
    }

    private void OnDispatcherShutdownFinished(object? sender, EventArgs e)
    {
        lifecycleCoordinator.RecordLifecycleEvent(MainWindowLifecycleEvent.DispatcherShutdownFinished);
        Dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
        Dispatcher.ShutdownFinished -= OnDispatcherShutdownFinished;
        if (windowSource is not null)
        {
            windowSource.Disposed -= OnWindowSourceDisposed;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (appShellViewModel.CurrentPage == AppPage.Settings
            && appShellViewModel.SettingsViewModel.Shortcuts.CapturingRow is not null) return;
        if (e.Key == Key.System && e.SystemKey == Key.Space)
        {
            SystemCommands.ShowSystemMenu(this, PointToScreen(new Point(0, TitleBarHeight)));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && appShellViewModel.ActivateSearch())
        {
            e.Handled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        var pageCategory = GetPageCategory();
        try
        {
            if (windowSource is not null && windowMessageHook is not null)
            {
                windowSource.RemoveHook(windowMessageHook);
            }

            windowMessageHook = null;
            appShellViewModel.PropertyChanged -= OnAppShellPropertyChanged;
            appShellViewModel.SettingsViewModel.WindowCloseApproved -= OnWindowCloseApproved;
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            IsVisibleChanged -= OnIsVisibleChanged;
            PreviewKeyDown -= OnPreviewKeyDown;
            SourceInitialized -= OnSourceInitialized;
            Closing -= OnClosing;
            Closed -= OnClosed;
        }
        catch (Exception exception)
        {
            lifecycleCoordinator.RecordCleanupFailure("window", exception);
        }
        finally
        {
            lifecycleCoordinator.CompleteClose(
                isReleaseSmoke,
                pageCategory,
                RequestApplicationShutdown);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        var decision = lifecycleCoordinator.EvaluateClosing(
            isReleaseSmoke,
            allowWindowClose,
            appShellViewModel.SettingsViewModel.RequestWindowClose,
            GetPageCategory());
        if (decision == MainWindowClosingDecision.Close)
        {
            normalCloseAccepted = true;
            return;
        }

        e.Cancel = true;
        if (decision == MainWindowClosingDecision.Prepare)
        {
            await lifecycleCoordinator
                .PrepareCloseAsync(
                    PrepareForCloseAsync,
                    () => Dispatcher.BeginInvoke(Close),
                    RequestApplicationShutdown)
                .ConfigureAwait(true);
        }
    }

    private async Task PrepareForCloseAsync()
    {
        await RunCloseCleanupStepAsync("seek-preview", () => localPlaybackPreviewService.DisposeAsync().AsTask())
            .ConfigureAwait(true);
        if (!isReleaseSmoke)
        {
            await RunCloseCleanupStepAsync(
                    "playback-session",
                    appShellViewModel.PrepareForApplicationExitAsync)
                .ConfigureAwait(true);
            await RunCloseCleanupStepAsync(
                    "player",
                    () => playerService.DisposeAsync().AsTask())
                .ConfigureAwait(true);
        }

        await RunCloseCleanupStepAsync(
                "http-client",
                () =>
                {
                    httpClient.Dispose();
                    return Task.CompletedTask;
                })
            .ConfigureAwait(true);
    }

    private async Task RunCloseCleanupStepAsync(string phase, Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            lifecycleCoordinator.RecordCleanupFailure(phase, exception);
        }
    }

    private void OnWindowCloseApproved(object? sender, EventArgs e)
    {
        allowWindowClose = true;
        Dispatcher.BeginInvoke(Close);
    }

    private string GetPageCategory() => appShellViewModel.CurrentPage
        .ToString()
        .ToLowerInvariant();

    private FileAppSettingsService CreateAppSettingsService()
    {
        var testDataRoot = Environment.GetEnvironmentVariable(TestDataRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(testDataRoot) && !isReleaseSmoke)
        {
            return new FileAppSettingsService();
        }

        return new FileAppSettingsService(Path.Combine(
            string.IsNullOrWhiteSpace(testDataRoot)
                ? Path.Combine(
                    Path.GetTempPath(),
                    "EmbyPlayer",
                    "release-smoke",
                    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : testDataRoot,
            "settings.json"));
    }

    public void SetPlayerCaptionState(bool _, bool isFullscreen)
    {
        if (isPlayerFullscreen == isFullscreen)
        {
            return;
        }

        isPlayerFullscreen = isFullscreen;
        ApplyWindowLayout();
    }

    private void OnAppShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppShellViewModel.CurrentPage))
        {
            return;
        }

        if (appShellViewModel.CurrentPage != AppPage.Player)
        {
            isPlayerFullscreen = false;
        }

        ApplyWindowLayout();
    }

    private void ApplyWindowLayout()
    {
        var isPlayer = appShellViewModel.CurrentPage == AppPage.Player;
        ApplyPageWindowSize(isPlayer);
        ShellContainer.Margin = isPlayer
            ? new Thickness(0)
            : new Thickness(0, TitleBarHeight, 0, 0);

        var isCaptionVisible = !isPlayer;
        TitleBar.Visibility = isCaptionVisible ? Visibility.Visible : Visibility.Collapsed;
        MainWindowChrome.CaptionHeight = isCaptionVisible ? TitleBarHeight : 0;
        MainWindowChrome.ResizeBorderThickness = isPlayerFullscreen
            ? new Thickness(0)
            : new Thickness(6);
        RefreshMaximizedWindowBounds();
    }

    private void ApplyPageWindowSize(bool isPlayer)
    {
        if (hasPlayerWindowLayout == isPlayer)
        {
            return;
        }

        if (isPlayer && WindowState == WindowState.Normal)
        {
            browsingWindowBounds = new Rect(Left, Top, Width, Height);
        }

        hasPlayerWindowLayout = isPlayer;
        MinWidth = isPlayer ? 640 : 1100;
        MinHeight = isPlayer ? 360 : 700;

        if (!isPlayer)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (hasPlayerWindowLayout) return;
                MinWidth = 1100;
                MinHeight = 700;
            }));
            // Wait for PlayerPage.Unloaded to restore its fullscreen bounds first.
            // Do not change maximized/minimized state or a newly started playback page.
            if (browsingWindowBounds is { } bounds)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    if (hasPlayerWindowLayout || WindowState != WindowState.Normal) return;
                    Width = Math.Max(MinWidth, bounds.Width);
                    Height = Math.Max(MinHeight, bounds.Height);
                    Left = bounds.Left;
                    Top = bounds.Top;
                }));
            }

            browsingWindowBounds = null;
        }
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (TryGetNativeMessage(message, out var nativeMessage))
        {
            lifecycleCoordinator.RecordNativeMessage(nativeMessage, normalCloseAccepted);
            if (nativeMessage == MainWindowNativeMessage.WmDestroy)
            {
                ScheduleUnexpectedWindowShutdown(MainWindowDestructionTrigger.WmDestroy);
            }
            else if (nativeMessage == MainWindowNativeMessage.WmNcDestroy)
            {
                ScheduleUnexpectedWindowShutdown(MainWindowDestructionTrigger.WmNcDestroy);
            }
        }

        if (message != WmGetMinMaxInfo
            || lParam == IntPtr.Zero
            || !TryGetMonitorInfo(windowHandle, out var monitorInfo))
        {
            return IntPtr.Zero;
        }

        var bounds = CalculateMaximizedWindowBounds(monitorInfo);
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition = new NativePoint(bounds.X, bounds.Y);
        minMaxInfo.MaxSize = new NativePoint(bounds.Width, bounds.Height);
        // Handling WM_GETMINMAXINFO bypasses WPF's minimum tracking constraints.
        // Keep the native window at least as large as its DIP-based content layout.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        minMaxInfo.MinTrackSize = new NativePoint(
            Math.Max(minMaxInfo.MinTrackSize.X, (int)Math.Ceiling(MinWidth * dpi.DpiScaleX)),
            Math.Max(minMaxInfo.MinTrackSize.Y, (int)Math.Ceiling(MinHeight * dpi.DpiScaleY)));
        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
        handled = true;
        return IntPtr.Zero;
    }

    private void ScheduleUnexpectedWindowShutdown(MainWindowDestructionTrigger trigger)
    {
        var dispatcherShuttingDown = Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished;
        var decision = lifecycleCoordinator.EvaluateUnexpectedDestruction(
            trigger,
            normalCloseAccepted,
            dispatcherShuttingDown);
        if (decision != UnexpectedWindowDestructionDecision.Prepare)
        {
            if (decision == UnexpectedWindowDestructionDecision.ShutdownPrepared)
            {
                lifecycleCoordinator.CompleteUnexpectedPreparedClose(RequestApplicationShutdown);
            }

            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => _ = PrepareUnexpectedWindowShutdownAsync()));
        }
        catch (Exception exception)
        {
            lifecycleCoordinator.RecordCleanupFailure("unexpected-dispatch", exception);
            lifecycleCoordinator.CompleteUnexpectedCloseWithoutDispatch();
        }
    }

    private Task PrepareUnexpectedWindowShutdownAsync() =>
        lifecycleCoordinator.PrepareUnexpectedCloseAsync(
            PrepareForCloseAsync,
            RequestApplicationShutdown);

    private void RequestApplicationShutdown()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Application.Current?.Shutdown();
    }

    private static bool TryGetNativeMessage(int message, out MainWindowNativeMessage nativeMessage)
    {
        nativeMessage = message switch
        {
            WmClose => MainWindowNativeMessage.WmClose,
            WmDestroy => MainWindowNativeMessage.WmDestroy,
            WmNcDestroy => MainWindowNativeMessage.WmNcDestroy,
            _ => default
        };
        return message is WmClose or WmDestroy or WmNcDestroy;
    }

    private void RefreshMaximizedWindowBounds()
    {
        if (isPlayerFullscreen || WindowState != WindowState.Maximized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !TryGetMonitorInfo(handle, out var monitorInfo))
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top,
            monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static MaximizedWindowBounds CalculateMaximizedWindowBounds(MonitorInfo monitorInfo) =>
        MaximizedWindowBoundsCalculator.Calculate(
            monitorInfo.Monitor.ToNativePixelRect(),
            monitorInfo.WorkArea.ToNativePixelRect());

    private static bool TryGetMonitorInfo(IntPtr windowHandle, out MonitorInfo monitorInfo)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo);
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e)
    {
        MinimizePlayerWindow();
    }

    public void MinimizePlayerWindow()
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeRestoreButtonClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        ClosePlayerWindow();
    }

    public void ClosePlayerWindow()
    {
        SystemCommands.CloseWindow(this);
    }

    private void ApplyDarkWindowChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        _ = DwmSetWindowAttribute(
            hwnd,
            DwmwaUseImmersiveDarkMode,
            ref enabled,
            Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativePixelRect ToNativePixelRect() => new(Left, Top, Right, Bottom);
    }
}
