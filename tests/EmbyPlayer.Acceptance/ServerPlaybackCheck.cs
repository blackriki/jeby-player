using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using EmbyPlayer.App.Security;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Home;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Emby;
using EmbyPlayer.Player;
using EmbyPlayer.UI.Controls;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;

internal static partial class Program
{
    private static async Task RunServerPlaybackAsync(string artifactRoot, string outputRoot,
        CheckResult result, CancellationToken cancellationToken, bool audioOnly = false, string? sessionDataDirectory = null)
    {
        BindServerMpv(artifactRoot, result);
        var snapshotRoot = Path.Combine(outputRoot, "server-settings");
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(audioOnly ? 90 : 180));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerOutcome? outcome = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                var local = new ServerOutcome(audioOnly);
                try { await RunServerOnStaAsync(snapshotRoot, local, budget.Token, audioOnly, sessionDataDirectory); }
                catch (Exception exception)
                {
                    local.NotRun = exception is CheckNotRun;
                    local.Error = exception is CheckFailure failure ? failure.Code
                        : exception is CheckNotRun notRun ? notRun.Code
                        : exception is OperationCanceledException ? "server-playback-cancelled-or-timeout"
                        : "server-playback-exception";
                    local.ExceptionType = exception.GetType().Name;
                }
                finally
                {
                    outcome = local;
                    if (local.HostReleased) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    finished.TrySetResult();
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Server playback acceptance" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            // Cancellation gives the normal VM exit path time to finish its final report and release MPV.
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(audioOnly ? 105 : 195));
            Require(thread.Join(TimeSpan.FromSeconds(2)), "server-sta-still-live-resources-retained");
        }
        catch
        {
            budget.Cancel();
            // Never inspect the worker's dictionaries or delete files while it can still use them.
            result.Details["serverWorkflow"] = "failed-cleanup-or-thread-exit-unverified";
            result.Details["settingsSnapshot"] = "retained-if-created";
            result.Details["serverWatchHistory"] = "may-have-changed-left-as-recorded";
            throw new CheckFailure("server-sta-timeout-or-cleanup-unverified");
        }

        budget.Dispose();
        Require(outcome is not null, "server-outcome-missing");
        foreach (var check in outcome!.Checks)
            result.Checks.Add(new { name = check.Key, status = check.Value });
        foreach (var detail in outcome.Details) result.Details[detail.Key] = detail.Value;
        result.Details["reports"] = outcome.Reports;
        result.Details["staExit"] = "verified";
        if (outcome.ExceptionType is not null) result.Details["serverExceptionType"] = outcome.ExceptionType;
        // This directory was created by this check under the fresh output root; the worker has exited.
        if (Directory.Exists(snapshotRoot)) Directory.Delete(snapshotRoot, recursive: true);
        result.Details["settingsSnapshot"] = "removed-after-thread-exit";
        if (outcome.NotRun) throw new CheckNotRun(outcome.Error!);
        Require(outcome.Error is null, outcome.Error ?? "server-workflow-incomplete");
    }

    private static void BindServerMpv(string artifactRoot, CheckResult result)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(artifactRoot, "release-manifest.json")));
        var root = manifest.RootElement;
        var binary = root.GetProperty("mpv").GetProperty("binary");
        const string name = "libmpv-2.dll";
        var entry = root.GetProperty("files").EnumerateArray().Single(
            item => item.GetProperty("relativePath").GetString() == name);
        var path = Path.Combine(artifactRoot, name);
        var hash = HashFile(path);
        Require(binary.GetProperty("fileName").GetString() == name
            && binary.GetProperty("architecture").GetString() == "AMD64"
            && new FileInfo(path).Length == binary.GetProperty("sizeBytes").GetInt64()
            && new FileInfo(path).Length == entry.GetProperty("sizeBytes").GetInt64()
            && hash.Equals(binary.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)
            && hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase),
            "server-native-artifact-mismatch");
        var library = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(MpvPlayerService).Assembly,
            (requested, _, _) => requested is "libmpv-2" or "libmpv-2.dll" ? library : IntPtr.Zero);
        result.Assert("server-native-artifact-binding", true);
        result.Details["nativeSha256"] = hash;
    }

    private static async Task RunServerOnStaAsync(string snapshotRoot, ServerOutcome outcome, CancellationToken token, bool audioOnly,
        string? sessionDataDirectory)
    {
        Directory.CreateDirectory(snapshotRoot);
        var source = Path.Combine(sessionDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyPlayer"), "settings.json");
        if (!File.Exists(source)) throw new CheckNotRun("server-saved-settings-unavailable");
        foreach (var suffix in new[] { "", ".bak" })
        {
            if (!File.Exists(source + suffix)) continue;
            await using var input = new FileStream(source + suffix, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            await using var copy = new FileStream(Path.Combine(snapshotRoot, "settings.json" + suffix),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await input.CopyToAsync(copy, token);
        }
        var settings = new FileAppSettingsService(Path.Combine(snapshotRoot, "settings.json"));
        var session = await new AuthSessionStore(settings, new WindowsCredentialAuthSessionStore()).LoadAsync(token);
        if (session is null) throw new CheckNotRun("server-saved-secure-session-unavailable");
        if (string.IsNullOrWhiteSpace(await settings.GetDeviceIdAsync(token)))
            throw new CheckNotRun("server-saved-device-id-unavailable");
        var devices = new SettingsDeviceIdService(settings);
        await settings.SavePlayerPreferencesAsync(PlayerPreferences.Default with
            { DefaultVolume = 0, AutoPlayNextEpisode = false, RememberLastVolume = false }, token);
        outcome.Pass("server-secure-session-and-isolated-settings");
        var audioMetadata = audioOnly ? new AudioMetadataInspector(new Uri(session.ServerBase)) : null;
        using var http = new HttpClient(audioMetadata is null ? new HttpClientHandler { AllowAutoRedirect = false } : audioMetadata)
            { Timeout = TimeSpan.FromSeconds(15) };
        var details = new EmbyMediaDetailService(http, devices);
        var home = new EmbyHomeService(http, devices);
        var selected = await SelectServerCandidateAsync(session!, details, home, http, devices, outcome, token, audioMetadata);
        if (audioMetadata is not null) audioMetadata.DiscoveryCompleted = true;
        var playback = new EmbyPlaybackService(http, devices);
        var info = await PrepareServerPlaybackAsync(playback, session!, selected, 0, token);
        if (audioOnly && info.AudioTracks.Select(track => track.Index).Distinct().Count() < 2)
            throw new CheckNotRun("audio-playback-info-pair-unavailable");
        outcome.Pass("server-prepare-from-zero");

        var log = new ServerMpvLog();
        var player = new MpvPlayerService();
        var reports = new ServerReportObserver(new EmbyPlaybackReportService(http, devices), outcome.Reports);
        var current = new CurrentSessionService();
        current.SetSession(session!);
        var navigation = new NavigationService();
        DetailNavigationParameter? returned = null;
        navigation.CurrentPageChanged += (_, e) =>
        {
            if (e.Parameter is DetailNavigationParameter detail) returned = detail;
        };
        var scheduler = new PlaybackReportScheduler();
        scheduler.Tick += (_, instance) =>
        {
            if (instance is 1 or 2) Interlocked.Increment(ref outcome.Reports[(int)instance - 1].PeriodicTicks);
        };
        PlayerProgressChangedEventArgs? progress = null;
        var failed = 0;
        var completed = 0;
        player.ProgressChanged += (_, e) => Volatile.Write(ref progress, e);
        player.StatusChanged += (_, e) =>
        {
            if (e.State == PlayerPlaybackState.Failed) Interlocked.Exchange(ref failed, 1);
            if (e.State == PlayerPlaybackState.Completed) Interlocked.Increment(ref completed);
        };
        var vm = new PlayerViewModel(navigation, player, reports, current, null, null, scheduler, settings);
        var host = new PlayerVideoHost();
        var window = new Window { Title = "Server playback acceptance", Content = host,
            Width = 640, Height = 360, Left = -32000, Top = -32000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None };
        var hostDestroyed = false;
        host.HostDestroyed += (_, _) => hostDestroyed = true;
        var first = outcome.Reports[0];
        var second = outcome.Reports[1];
        async Task UntilAsync(Func<bool> predicate)
        {
            while (!predicate())
            {
                Require(Volatile.Read(ref failed) == 0 && string.IsNullOrEmpty(vm.ErrorMessage), "server-native-playback-failed");
                Require(first.FailedCount + second.FailedCount == 0, "server-report-" + reports.LastError);
                await Task.Delay(50, token);
            }
            token.ThrowIfCancellationRequested();
        }
        try
        {
            outcome.HostReleased = false;
            window.Show();
            await UntilAsync(() => host.HostHandle != IntPtr.Zero);
            outcome.Pass("server-real-video-host");
            navigation.NavigateTo(AppPage.Player);
            vm.Load(new PlayerNavigationParameter(info, new DetailNavigationParameter(selected.Id)));
            await vm.AttachVideoHostAsync(host.HostHandle);
            outcome.Details["serverWatchHistory"] = "one-sample-playback-attempted-left-as-recorded";
            await UntilAsync(() => vm.IsPlaying && vm.CanSeek && first.PlayingSuccessCount == 1
                && Volatile.Read(ref progress)?.Position is { TotalSeconds: > 0 and < 5 });
            if (audioOnly)
            {
                await RunAudioSwitchStageAsync(vm, outcome, log, UntilAsync);
                return;
            }
            var startPosition = Volatile.Read(ref progress)!.Position!.Value.TotalSeconds;
            var watch = Stopwatch.StartNew();
            await UntilAsync(() => watch.Elapsed.TotalSeconds >= 20.5 && first.PeriodicTickCount >= 2
                && first.ProgressSuccessCount >= 2 && Volatile.Read(ref progress)?.Position is { TotalSeconds: >= 20 });
            var rate = (Volatile.Read(ref progress)!.Position!.Value.TotalSeconds - startPosition) / watch.Elapsed.TotalSeconds;
            Require(rate is >= 0.85 and <= 1.15, "server-initial-normal-rate-unverified");
            outcome.Details["initialPlaybackWallSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2);
            outcome.Details["initialPositionToWallRate"] = Math.Round(rate, 3);
            outcome.Pass("server-twenty-seconds-two-real-report-periods");

            await ((AsyncRelayCommand)vm.PauseCommand).ExecuteAsync();
            await UntilAsync(() => vm.IsPaused && Volatile.Read(ref progress)?.IsPaused == true && first.PausedSuccessCount > 0);
            var resumeReports = first.UnpausedSuccessCount;
            await ((AsyncRelayCommand)vm.PlayCommand).ExecuteAsync();
            await UntilAsync(() => vm.IsPlaying && Volatile.Read(ref progress)?.IsPaused == false
                && first.UnpausedSuccessCount > resumeReports);
            outcome.Pass("server-pause-resume-native-state-and-reports");

            var runtime = Volatile.Read(ref progress)!.Duration!.Value.TotalSeconds;
            await vm.SeekToPercentAsync(10);
            await UntilAsync(() => Volatile.Read(ref progress)?.Position is { } position
                && Math.Abs(position.TotalSeconds - runtime * 0.1) <= 3);
            var seekTicks = first.PeriodicTickCount;
            var seekWatch = Stopwatch.StartNew();
            await UntilAsync(() => seekWatch.Elapsed.TotalSeconds >= 10 && first.PeriodicTickCount > seekTicks
                && Volatile.Read(ref progress)?.Position is { } position && position.TotalSeconds >= runtime * 0.1 + 10);
            outcome.Pass("server-real-ten-percent-seek-and-ten-seconds-playback");
            await ((AsyncRelayCommand)vm.BackCommand).ExecuteAsync();
            Require(first.StoppedAttemptCount == 1 && first.StoppedSuccessCount == 1
                && returned?.PlaybackState is { IsSynchronized: true, IsPlayed: false }, "server-first-stopped-outcome-mismatch");
            outcome.Pass("server-back-single-successful-stopped");
            var detailAfterBack = await ReadServerDetailAsync(details, session!, selected.Id, completed: false, token);
            var serverPosition = detailAfterBack.ResumePositionTicks.GetValueOrDefault();
            var delta = Math.Abs(TimeSpan.FromTicks(serverPosition - first.LastStoppedPosition).TotalSeconds);
            Require(serverPosition > 0 && !detailAfterBack.IsPlayed && delta <= 5, "server-resume-position-readback-mismatch");
            outcome.Details["serverStoppedPositionDeltaSeconds"] = Math.Round(delta, 3);
            var resumeHome = await home.LoadHomeAsync(session!, token);
            Require(resumeHome.IsSuccess, "server-resume-home-" + resumeHome.Error);
            Require(resumeHome.Data!.ContinueWatching.Any(card => card.Id == selected.Id), "server-resume-continue-absent");
            outcome.Pass("server-detail-and-home-continue-readback");

            reports.Phase = 1;
            info = await PrepareServerPlaybackAsync(playback, session!, selected, serverPosition, token);
            Volatile.Write(ref progress, null);
            returned = null;
            navigation.NavigateTo(AppPage.Player);
            vm.Load(new PlayerNavigationParameter(info, new DetailNavigationParameter(selected.Id)));
            await vm.AttachVideoHostAsync(host.HostHandle);
            await UntilAsync(() => vm.IsPlaying && vm.CanSeek && second.PlayingSuccessCount == 1
                && Volatile.Read(ref progress)?.Position is { } position
                && Math.Abs(position.TotalSeconds - TimeSpan.FromTicks(serverPosition).TotalSeconds) <= 5);
            outcome.Pass("server-reprepare-and-native-resume-from-readback");
            runtime = Volatile.Read(ref progress)!.Duration!.Value.TotalSeconds;
            var tailTarget = runtime - 45;
            await vm.SeekToPercentAsync(tailTarget / runtime * 100);
            await UntilAsync(() => Volatile.Read(ref progress)?.Position is { } position
                && Math.Abs(position.TotalSeconds - tailTarget) <= 3);
            var tailStart = Volatile.Read(ref progress)!.Position!.Value.TotalSeconds;
            var tail = Stopwatch.StartNew();
            await UntilAsync(() => Volatile.Read(ref completed) == 1 && second.StoppedSuccessCount == 1
                && second.EndPositionProgressSuccessCount >= 1);
            var tailRate = (runtime - tailStart) / tail.Elapsed.TotalSeconds;
            Require(tail.Elapsed.TotalSeconds >= 38 && tailRate is >= 0.75 and <= 1.15,
                "server-natural-tail-normal-rate-unverified");
            Require(second.StoppedAttemptCount == 1, "server-eof-stopped-not-once");
            outcome.Details["naturalTailWallSeconds"] = Math.Round(tail.Elapsed.TotalSeconds, 2);
            outcome.Details["tailPositionToWallRate"] = Math.Round(tailRate, 3);
            outcome.Details["playbackMethod"] = "zero-start-then-real-seeks-ten-percent-and-end-minus-45-seconds";
            outcome.Details["rateEvidence"] = "public-position-events-versus-wall-clock-no-speed-command";
            outcome.Pass("server-natural-eof-after-real-seek-at-normal-rate");
            await ((AsyncRelayCommand)vm.BackCommand).ExecuteAsync();
            Require(second.StoppedAttemptCount == 1
                && returned?.PlaybackState is { IsSynchronized: true, IsPlayed: true }, "server-eof-back-outcome-mismatch");
            outcome.Pass("server-eof-back-reuses-single-stopped");
            var finalDetail = await ReadServerDetailAsync(details, session!, selected.Id, completed: true, token);
            var finalHome = await home.LoadHomeAsync(session!, token);
            Require(finalHome.IsSuccess, "server-final-home-" + finalHome.Error);
            Require(finalDetail.IsPlayed && !finalHome.Data!.ContinueWatching.Any(card => card.Id == selected.Id),
                "server-played-or-continue-state-unconfirmed");
            Require(first.FailedCount + second.FailedCount == 0, "server-report-" + reports.LastError);
            outcome.Details["serverWatchHistory"] = "one-sample-now-watched-left-as-recorded";
            outcome.Pass("server-played-and-continue-removal-readback");
        }
        finally
        {
            scheduler.StopAll();
            // The caller bounds cleanup and retains the host if this work is still running.
            await vm.PrepareForApplicationExitAsync();
            await player.DisposeAsync();
            var counters = log.ReadAppendedCounters();
            outcome.Details["nativeLogCounters"] = counters;
            Require(counters.Created > 0 && counters.Created == counters.Destroyed && counters.CleanupFailures == 0,
                "server-native-cleanup-unverified-host-retained");
            host.Dispose();
            window.Close();
            Require(hostDestroyed && host.HostHandle == IntPtr.Zero, "server-host-release-unverified");
            outcome.HostReleased = true;
            outcome.Pass("server-native-and-video-host-cleanup");
            if (audioOnly)
                Require(counters.Created == 1 && counters.SuccessfulEof == 0, "audio-unexpected-native-lifecycle");
            if (outcome.Checks.TryGetValue("server-natural-eof-after-real-seek-at-normal-rate", out var eofStatus) && eofStatus == "Passed")
                Require(counters.Created == 2 && counters.SuccessfulEof == 1, "server-native-eof-log-unverified");
        }
    }

    private static async Task<MediaDetail> SelectServerCandidateAsync(AuthSession session, EmbyMediaDetailService details,
        EmbyHomeService home, HttpClient http, IDeviceIdService devices, ServerOutcome outcome, CancellationToken token,
        AudioMetadataInspector? audioMetadata = null)
    {
        var response = await home.LoadHomeAsync(session, token);
        Require(response.IsSuccess, "server-candidate-home-" + response.Error);
        var data = response.Data!;
        var inspected = new HashSet<string>(StringComparer.Ordinal);
        var continueIds = data.ContinueWatching.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
        async Task<MediaDetail?> InspectAsync(string id, string type)
        {
            if (inspected.Count >= 20 || type is not ("Movie" or "Episode") || !inspected.Add(id)) return null;
            outcome.Details["candidatesInspected"] = inspected.Count;
            audioMetadata?.InspectDetail(session.UserId, id);
            var loaded = await details.LoadDetailAsync(session, id, token);
            Require(loaded.IsSuccess, "server-candidate-detail-" + loaded.Error);
            var item = loaded.Detail!;
            return item.Type is "Movie" or "Episode" && !item.IsPlayed && !continueIds.Contains(item.Id)
                && item.ResumePositionTicks.GetValueOrDefault() == 0
                && item.RunTimeTicks > TimeSpan.FromMinutes(3).Ticks
                && (audioMetadata is null || audioMetadata.AudioTrackCount >= 2) ? item : null;
        }
        MediaDetail? selected = null;
        foreach (var card in data.RecentlyAdded.Concat(data.MediaSections.Where(section => section.IsSuccess)
                     .SelectMany(section => section.Items)).OrderBy(card => card.IsPlayed || card.ResumePositionTicks > 0))
        {
            selected = await InspectAsync(card.Id, card.Type);
            if (selected is not null || inspected.Count >= 20) break;
        }
        if (selected is null && inspected.Count < 20)
        {
            var libraries = new EmbyLibraryService(http, devices);
            var loaded = await libraries.LoadLibrariesAsync(session, token);
            Require(loaded.IsSuccess, "server-candidate-libraries-" + loaded.Error);
            foreach (var library in loaded.Libraries.Take(2))
            {
                var page = await libraries.LoadLibraryItemsAsync(session, library, 0, 20 - inspected.Count, token);
                Require(page.IsSuccess, "server-candidate-library-page-" + page.Error);
                foreach (var item in page.Items)
                {
                    selected = await InspectAsync(item.Id, item.Type);
                    if (selected is not null || inspected.Count >= 20) break;
                }
                if (selected is not null || inspected.Count >= 20) break;
            }
        }
        if (selected is null)
            throw new CheckNotRun(audioMetadata is null ? "server-unwatched-zero-resume-candidate-unavailable-in-bounded-sample"
                : "audio-multi-track-candidate-unavailable-in-bounded-sample");
        if (audioMetadata is not null) outcome.Details["metadataAudioTrackCount"] = audioMetadata.AudioTrackCount;
        outcome.Details["sampleType"] = selected!.Type;
        outcome.Pass("server-bounded-unwatched-zero-resume-candidate");
        return selected;
    }

    private static async Task<PlaybackInfo> PrepareServerPlaybackAsync(EmbyPlaybackService playback, AuthSession session,
        MediaDetail detail, long positionTicks, CancellationToken token)
    {
        var result = await playback.PreparePlaybackAsync(session,
            new PlaybackStartRequest(detail.Id, "Acceptance sample", positionTicks, detail.Type), token);
        Require(result.IsSuccess, "server-playback-prepare-" + result.Error);
        var info = result.PlaybackInfo!;
        Require(!info.RequiresTranscoding && info.StartPositionTicks == positionTicks
            && info.RunTimeTicks > TimeSpan.FromMinutes(3).Ticks
            && Uri.TryCreate(info.PlaybackPath, UriKind.Absolute, out var source)
            && Uri.TryCreate(session.ServerBase, UriKind.Absolute, out var server)
            && source.Scheme == server.Scheme && source.Host == server.Host && source.Port == server.Port,
            "server-direct-same-origin-playback-unavailable");
        return info;
    }

    private static async Task<MediaDetail> ReadServerDetailAsync(EmbyMediaDetailService details, AuthSession session,
        string id, bool completed, CancellationToken token)
    {
        MediaDetail? detail = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await details.LoadDetailAsync(session, id, token);
            Require(result.IsSuccess, "server-detail-readback-" + result.Error);
            detail = result.Detail!;
            if (completed ? detail.IsPlayed : detail.ResumePositionTicks.GetValueOrDefault() > 0) break;
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        return detail!;
    }

    private sealed class ServerOutcome
    {
        public ServerOutcome(bool audioOnly = false)
        {
            if (!audioOnly) return;
            Checks.Clear();
            foreach (var name in new[] { "server-secure-session-and-isolated-settings", "server-bounded-unwatched-zero-resume-candidate",
                         "server-prepare-from-zero", "server-real-video-host", "audio-native-mapped-pair",
                         "audio-switch-native-vm-and-report", "audio-restore-native-vm-and-report",
                         "audio-single-stopped-with-restored-index", "server-native-and-video-host-cleanup" })
                Checks.Add(name, "NotRun");
            Details["scope"] = "short-audio-switch-and-restore";
            Details["otherMediaTypeAndTracks"] = "other-media-type-and-subtitles-not-tested";
        }

        public bool HostReleased { get; set; } = true;
        public bool NotRun { get; set; }
        public string? Error { get; set; }
        public string? ExceptionType { get; set; }
        public Dictionary<string, string> Checks { get; } = new[]
        {
            "server-secure-session-and-isolated-settings", "server-bounded-unwatched-zero-resume-candidate",
            "server-prepare-from-zero", "server-real-video-host", "server-twenty-seconds-two-real-report-periods",
            "server-pause-resume-native-state-and-reports", "server-real-ten-percent-seek-and-ten-seconds-playback",
            "server-back-single-successful-stopped", "server-detail-and-home-continue-readback",
            "server-reprepare-and-native-resume-from-readback", "server-natural-eof-after-real-seek-at-normal-rate",
            "server-eof-back-reuses-single-stopped", "server-played-and-continue-removal-readback",
            "server-native-and-video-host-cleanup"
        }.ToDictionary(name => name, _ => "NotRun");
        public Dictionary<string, object?> Details { get; } = new()
        {
            ["serverWatchHistory"] = "not-changed", ["visualUi"] = "not-tested-by-server-workflow",
            ["otherMediaTypeAndTracks"] = "not-tested-by-single-sample-workflow",
            ["mpvLog"] = "normal-product-log-appended-raw-content-not-exported"
        };
        public ServerReportPhase[] Reports { get; } = [new(), new()];
        public void Pass(string name) => Checks[name] = "Passed";
    }

    private sealed class ServerReportPhase
    {
        internal int PlayingAttempts, PlayingSuccesses, ProgressAttempts, ProgressSuccesses, StoppedAttempts,
            StoppedSuccesses, PausedSuccesses, UnpausedSuccesses, EndPositionProgressSuccesses, Failures, PeriodicTicks;
        internal long LastStoppedTicks;
        internal AudioReportEvidence? LastAudioProgress;
        internal int LastStoppedAudioIndex = -1;
        public int PlayingAttemptCount => Volatile.Read(ref PlayingAttempts);
        public int PlayingSuccessCount => Volatile.Read(ref PlayingSuccesses);
        public int ProgressAttemptCount => Volatile.Read(ref ProgressAttempts);
        public int ProgressSuccessCount => Volatile.Read(ref ProgressSuccesses);
        public int StoppedAttemptCount => Volatile.Read(ref StoppedAttempts);
        public int StoppedSuccessCount => Volatile.Read(ref StoppedSuccesses);
        public int PausedSuccessCount => Volatile.Read(ref PausedSuccesses);
        public int UnpausedSuccessCount => Volatile.Read(ref UnpausedSuccesses);
        public int EndPositionProgressSuccessCount => Volatile.Read(ref EndPositionProgressSuccesses);
        public int FailedCount => Volatile.Read(ref Failures);
        public int PeriodicTickCount => Volatile.Read(ref PeriodicTicks);
        internal long LastStoppedPosition => Interlocked.Read(ref LastStoppedTicks);
    }

    private sealed class ServerReportObserver(IPlaybackReportService inner, ServerReportPhase[] phases) : IPlaybackReportService
    {
        public int Phase { get; set; }
        public PlaybackReportError LastError { get; private set; }
        public async Task<PlaybackReportResult> ReportPlayingAsync(AuthSession session, PlaybackReportStartRequest request,
            CancellationToken token)
        {
            var phase = phases[Phase];
            Interlocked.Increment(ref phase.PlayingAttempts);
            var reply = await inner.ReportPlayingAsync(session, request, token);
            if (reply.IsSuccess) Interlocked.Increment(ref phase.PlayingSuccesses); else Failed(phase, reply.Error);
            return reply;
        }
        public async Task<PlaybackReportResult> ReportProgressAsync(AuthSession session, PlaybackReportProgressRequest request,
            CancellationToken token)
        {
            var phase = phases[Phase];
            Interlocked.Increment(ref phase.ProgressAttempts);
            var reply = await inner.ReportProgressAsync(session, request, token);
            if (reply.IsSuccess)
            {
                var sequence = Interlocked.Increment(ref phase.ProgressSuccesses);
                Volatile.Write(ref phase.LastAudioProgress, new AudioReportEvidence(sequence, request.AudioStreamIndex));
                if (request.IsPaused) Interlocked.Increment(ref phase.PausedSuccesses);
                else Interlocked.Increment(ref phase.UnpausedSuccesses);
                if (request.RunTimeTicks > 0 && request.PositionTicks >= request.RunTimeTicks)
                    Interlocked.Increment(ref phase.EndPositionProgressSuccesses);
            }
            else Failed(phase, reply.Error);
            return reply;
        }
        public async Task<PlaybackReportResult> ReportStoppedAsync(AuthSession session, PlaybackReportStoppedRequest request,
            CancellationToken token)
        {
            var phase = phases[Phase];
            Interlocked.Increment(ref phase.StoppedAttempts);
            var reply = await inner.ReportStoppedAsync(session, request, token);
            if (reply.IsSuccess)
            {
                Interlocked.Exchange(ref phase.LastStoppedTicks, request.PositionTicks);
                Volatile.Write(ref phase.LastStoppedAudioIndex, request.AudioStreamIndex ?? -1);
                Interlocked.Increment(ref phase.StoppedSuccesses);
            }
            else Failed(phase, reply.Error);
            return reply;
        }
        private void Failed(ServerReportPhase phase, PlaybackReportError error)
        {
            LastError = error;
            Interlocked.Increment(ref phase.Failures);
        }
    }

    private sealed class ServerMpvLog
    {
        private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbyPlayer", "logs", "mpv.log");
        private readonly long start;
        public ServerMpvLog() => start = File.Exists(path) ? new FileInfo(path).Length : 0;
        public long CaptureOffset() => new FileInfo(path).Length;
        public bool HasAudioSelectionAfter(long offset, int mpvTrackId) => ReadLinesAfter(offset).Any(line =>
            line.Contains($" audio-track-set-verify expectedTrackId={mpvTrackId} aidMatches=True ", StringComparison.Ordinal)
            && line.Contains(" trackSelected=True ", StringComparison.Ordinal));
        private string[] ReadLinesAfter(long offset)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Require(stream.Length >= offset && stream.Length - offset <= 4 * 1024 * 1024, "server-native-log-unavailable");
            stream.Position = offset;
            using var reader = new StreamReader(stream, Utf8);
            return reader.ReadToEnd().Split('\n');
        }
        public ServerNativeCounters ReadAppendedCounters()
        {
            var lines = ReadLinesAfter(start);
            return new ServerNativeCounters(
                lines.Count(line => line.Contains(" mpv-create result=True", StringComparison.Ordinal)),
                lines.Count(line => line.Contains(" handle-destroyed", StringComparison.Ordinal)),
                lines.Count(line => line.Contains(" event=end-file reason=eof error=0 ", StringComparison.Ordinal)),
                lines.Count(line => line.Contains("destroySkipped=True", StringComparison.Ordinal)
                    || line.Contains("handle-destroy-failed", StringComparison.Ordinal)));
        }
    }

    private sealed record ServerNativeCounters(int Created, int Destroyed, int SuccessfulEof, int CleanupFailures);
}
