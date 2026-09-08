using System.Globalization;
using System.Windows.Input;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private readonly SemaphoreSlim speedOperationGate = new(1, 1);
    private double selectedPlaybackSpeed = 1;
    private double playbackSpeed = 1;
    private bool isHoldSpeedRequested;
    public bool HasPlaybackSpeedError { get; private set; }
    public bool IsSpeedFeedbackVisible => IsTemporaryDoubleSpeed || HasPlaybackSpeedError;
    public string SpeedFeedbackText => HasPlaybackSpeedError
        ? $"速度调整失败 · 当前 {PlaybackSpeedText}" : "2× 播放中 · 松开恢复";
    public ICommand RetryPlaybackSpeedCommand { get; private set; } = null!;

    public double PlaybackSpeed => playbackSpeed;
    public double SelectedPlaybackSpeed => selectedPlaybackSpeed;
    public string PlaybackSpeedText => playbackSpeed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
    public bool IsTemporaryDoubleSpeed => isHoldSpeedRequested && playbackSpeed == 2;
    public bool CanChangePlaybackSpeed => CanUsePlaybackControls() && !IsPreparingNextEpisode && !isCompleted;
    public ICommand SelectPlaybackSpeedCommand { get; private set; } = null!;
    public IReadOnlyList<PlayerSpeedOption> PlaybackSpeedOptions => new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 3 }
        .Select(speed => new PlayerSpeedOption(speed,
            speed == 1 ? "1× · 正常" : speed.ToString("0.##", CultureInfo.InvariantCulture) + "×",
            (isHoldSpeedRequested ? selectedPlaybackSpeed : playbackSpeed) == speed)).ToArray();

    private void InitializePlaybackSpeed()
    {
        RetryPlaybackSpeedCommand = new AsyncRelayCommand(
            () => ApplyRequestedPlaybackSpeedAsync(currentPlaybackInstanceId), () => CanChangePlaybackSpeed && HasPlaybackSpeedError);
        SelectPlaybackSpeedCommand = new AsyncRelayCommand(
            value => value is double speed ? SetPlaybackSpeedAsync(speed) : Task.CompletedTask,
            value => value is double && CanChangePlaybackSpeed);
    }

    private void ResetPlaybackSpeed(PlaybackReloadState? state)
    {
        isHoldSpeedRequested = false;
        HasPlaybackSpeedError = false;
        selectedPlaybackSpeed = state?.PlaybackSpeed ?? 1;
        playbackSpeed = 1;
        NotifyPlaybackSpeed();
    }

    public async Task SetPlaybackSpeedAsync(double speed)
    {
        if (!CanChangePlaybackSpeed || !double.IsFinite(speed) || speed < 0.25 || speed > 4) return;
        selectedPlaybackSpeed = speed;
        await ApplyRequestedPlaybackSpeedAsync(currentPlaybackInstanceId);
    }

    public async Task BeginTemporaryDoubleSpeedAsync()
    {
        if (!CanChangePlaybackSpeed || !IsPlaying || isHoldSpeedRequested) return;
        isHoldSpeedRequested = true;
        await ApplyRequestedPlaybackSpeedAsync(currentPlaybackInstanceId);
    }

    public async Task EndTemporaryDoubleSpeedAsync()
    {
        if (!isHoldSpeedRequested) return;
        isHoldSpeedRequested = false;
        OnPropertyChanged(nameof(IsTemporaryDoubleSpeed));
        await ApplyRequestedPlaybackSpeedAsync(currentPlaybackInstanceId);
    }

    private async Task ApplyRequestedPlaybackSpeedAsync(long instance)
    {
        var token = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
        try
        {
            await speedOperationGate.WaitAsync(token);
            try
            {
                if (!CanContinuePlaybackOperation(instance, token)) return;
                // Read the latest intention after entering the gate: release may arrive before 2x completes.
                var target = isHoldSpeedRequested ? 2 : selectedPlaybackSpeed;
                if (target == playbackSpeed) { HasPlaybackSpeedError = false; PlayerOptionsMessage = null; NotifyPlaybackSpeed(); return; }
                var result = await playerService.SetPlaybackSpeedAsync(instance, target, token);
                if (!CanContinuePlaybackOperation(instance, token)) return;
                if (result.IsSuccess)
                {
                    playbackSpeed = target;
                    HasPlaybackSpeedError = false;
                    PlayerOptionsMessage = null;
                }
                else
                {
                    HasPlaybackSpeedError = true;
                    isHoldSpeedRequested = false;
                    PlayerOptionsMessage = "调整播放速度失败，请在倍速菜单中重试。";
                    ShowControlsOverlay();
                }
                NotifyPlaybackSpeed();
            }
            finally { speedOperationGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!CanContinuePlaybackOperation(instance, token)) return;
            System.Diagnostics.Trace.TraceWarning($"Playback speed change failed: {exception}");
            HasPlaybackSpeedError = true;
            isHoldSpeedRequested = false;
            PlayerOptionsMessage = "调整播放速度失败，请在倍速菜单中重试。";
            NotifyPlaybackSpeed();
            ShowControlsOverlay();
        }
    }

    private void NotifyPlaybackSpeed()
    {
        OnPropertyChanged(nameof(PlaybackSpeed));
        OnPropertyChanged(nameof(HasPlaybackSpeedError));
        OnPropertyChanged(nameof(IsSpeedFeedbackVisible));
        OnPropertyChanged(nameof(SpeedFeedbackText));
        (RetryPlaybackSpeedCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedPlaybackSpeed));
        OnPropertyChanged(nameof(PlaybackSpeedText));
        OnPropertyChanged(nameof(PlaybackSpeedOptions));
        OnPropertyChanged(nameof(IsTemporaryDoubleSpeed));
        OnPropertyChanged(nameof(CanChangePlaybackSpeed));
        (SelectPlaybackSpeedCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }
}

public sealed record PlayerSpeedOption(double Speed, string Label, bool IsSelected);
