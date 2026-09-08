using System.Globalization;
using System.Windows.Input;
using EmbyPlayer.Player;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private double subtitleDelaySeconds;
    private double subtitleScale = 1;
    private double subtitlePosition = 100;
    private bool isAdjustingSubtitles;
    private bool isReadingTechnicalInfo;
    private string? playerOptionsMessage;
    private PlayerTechnicalInfo? technicalInfo;

    public ICommand AdjustSubtitleCommand { get; private set; } = null!;
    public ICommand RefreshTechnicalInfoCommand { get; private set; } = null!;
    public double SubtitleDelaySeconds => subtitleDelaySeconds;
    public double SubtitleScale => subtitleScale;
    public double SubtitlePosition => subtitlePosition;
    public string SubtitleDelayText => subtitleDelaySeconds.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " 秒";
    public string SubtitleScaleText => subtitleScale.ToString("0%", CultureInfo.CurrentCulture);
    public string SubtitlePositionText => $"{subtitlePosition:0}%";
    public string? PlayerOptionsMessage
    {
        get => playerOptionsMessage;
        private set { playerOptionsMessage = value; OnPropertyChanged(); }
    }

    public bool CanAdjustSubtitles => CanUseTrackControls() && !isAdjustingSubtitles;
    public bool IsReadingTechnicalInfo
    {
        get => isReadingTechnicalInfo;
        private set { isReadingTechnicalInfo = value; OnPropertyChanged(); NotifyEnhancementCommandStates(); }
    }
    public string DecodedResolutionText => technicalInfo is { Width: > 0, Height: > 0 }
        ? $"{technicalInfo.Width} × {technicalInfo.Height}" : "暂不可用";
    public string DecodedVideoCodecText => string.IsNullOrWhiteSpace(technicalInfo?.VideoCodec)
        ? "暂不可用" : technicalInfo.VideoCodec.ToUpperInvariant();

    private void InitializeEnhancementCommands()
    {
        InitializeQualityCommands();
        InitializePlaybackSpeed();
        AdjustSubtitleCommand = new AsyncRelayCommand(
            parameter => AdjustSubtitleAsync(parameter as string), _ => CanAdjustSubtitles);
        RefreshTechnicalInfoCommand = new AsyncRelayCommand(
            RefreshTechnicalInfoAsync, () => CanUsePlaybackControls() && !IsReadingTechnicalInfo);
    }

    private void ResetPlaybackEnhancements(PlaybackReloadState? reloadState)
    {
        localSubtitleRestoreTask = null;
        pendingReloadState = reloadState;
        ResetPlaybackSpeed(reloadState);
        subtitleDelaySeconds = reloadState?.SubtitleDelay ?? 0;
        subtitleScale = reloadState?.SubtitleScale ?? 1;
        subtitlePosition = reloadState?.SubtitlePosition ?? 100;
        if (reloadState is null) { IsSwitchingQuality = false; }
        isAdjustingSubtitles = false;
        IsReadingTechnicalInfo = false;
        PlayerOptionsMessage = null;
        technicalInfo = null;
        NotifySubtitleAdjustmentsChanged();
        NotifyTechnicalInfoChanged();
    }

    public async Task AdjustSubtitleAsync(string? action)
    {
        if (!CanAdjustSubtitles) { return; }
        var instance = currentPlaybackInstanceId;
        var token = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
        isAdjustingSubtitles = true;
        NotifyEnhancementCommandStates();
        PlayerOptionsMessage = null;
        try
        {
            if (action == "reset")
            {
                if (!await ApplySubtitleAdjustmentAsync(instance, SubtitleAdjustmentKind.DelaySeconds, 0, token)) { return; }
                if (!await ApplySubtitleAdjustmentAsync(instance, SubtitleAdjustmentKind.Scale, 1, token)) { return; }
                await ApplySubtitleAdjustmentAsync(instance, SubtitleAdjustmentKind.Position, 100, token);
                return;
            }

            var adjustment = action switch
            {
                "earlier" => (Kind: SubtitleAdjustmentKind.DelaySeconds, Value: Math.Max(-60, subtitleDelaySeconds - 0.1)),
                "later" => (Kind: SubtitleAdjustmentKind.DelaySeconds, Value: Math.Min(60, subtitleDelaySeconds + 0.1)),
                "smaller" => (Kind: SubtitleAdjustmentKind.Scale, Value: Math.Max(0.5, subtitleScale - 0.1)),
                "larger" => (Kind: SubtitleAdjustmentKind.Scale, Value: Math.Min(3, subtitleScale + 0.1)),
                "up" => (Kind: SubtitleAdjustmentKind.Position, Value: Math.Max(0, subtitlePosition - 5)),
                "down" => (Kind: SubtitleAdjustmentKind.Position, Value: Math.Min(100, subtitlePosition + 5)),
                _ => (Kind: (SubtitleAdjustmentKind)(-1), Value: 0d)
            };
            if ((int)adjustment.Kind >= 0)
            {
                await ApplySubtitleAdjustmentAsync(instance, adjustment.Kind, Math.Round(adjustment.Value, 2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Subtitle adjustment failed: {exception.GetType().Name}");
            if (CanAcceptPlayerEvent(instance)) { PlayerOptionsMessage = "字幕调整失败，请重试"; }
        }
        finally
        {
            if (IsCurrentPlaybackInstance(instance))
            {
                isAdjustingSubtitles = false;
                NotifyEnhancementCommandStates();
            }
        }
    }

    private async Task<bool> ApplySubtitleAdjustmentAsync(long instance, SubtitleAdjustmentKind kind, double value, CancellationToken token)
    {
        if (!CanContinuePlaybackOperation(instance, token)) { return false; }
        var result = await playerService.SetSubtitleAdjustmentAsync(instance, kind, value, token).ConfigureAwait(true);
        if (!CanContinuePlaybackOperation(instance, token)) { return false; }
        if (!result.IsSuccess)
        {
            PlayerOptionsMessage = "字幕调整失败，请重试";
            return false;
        }

        switch (kind)
        {
            case SubtitleAdjustmentKind.DelaySeconds: subtitleDelaySeconds = value; break;
            case SubtitleAdjustmentKind.Scale: subtitleScale = value; break;
            case SubtitleAdjustmentKind.Position: subtitlePosition = value; break;
        }
        NotifySubtitleAdjustmentsChanged();
        return true;
    }

    public async Task RefreshTechnicalInfoAsync()
    {
        if (!CanUsePlaybackControls() || IsReadingTechnicalInfo) { return; }
        var instance = currentPlaybackInstanceId;
        var token = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
        IsReadingTechnicalInfo = true;
        try
        {
            var info = await playerService.GetTechnicalInfoAsync(instance, token).ConfigureAwait(true);
            if (CanContinuePlaybackOperation(instance, token))
            {
                technicalInfo = info;
                NotifyTechnicalInfoChanged();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Playback information unavailable: {exception.GetType().Name}");
            if (CanAcceptPlayerEvent(instance)) { technicalInfo = null; NotifyTechnicalInfoChanged(); }
        }
        finally
        {
            if (IsCurrentPlaybackInstance(instance)) { IsReadingTechnicalInfo = false; }
        }
    }

    private void NotifySubtitleAdjustmentsChanged()
    {
        OnPropertyChanged(nameof(SubtitleDelaySeconds));
        OnPropertyChanged(nameof(SubtitleScale));
        OnPropertyChanged(nameof(SubtitlePosition));
        OnPropertyChanged(nameof(SubtitleDelayText));
        OnPropertyChanged(nameof(SubtitleScaleText));
        OnPropertyChanged(nameof(SubtitlePositionText));
    }

    private void NotifyTechnicalInfoChanged()
    {
        NotifyQualityInfoChanged();
        OnPropertyChanged(nameof(DecodedResolutionText));
        OnPropertyChanged(nameof(DecodedVideoCodecText));
    }

    private void NotifyEnhancementCommandStates()
    {
        OnPropertyChanged(nameof(CanImportLocalSubtitle));
        OnPropertyChanged(nameof(CanChangePlaybackSpeed));
        (SelectPlaybackSpeedCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSwitchQuality));
        (SelectQualityCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAdjustSubtitles));
        (AdjustSubtitleCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (RefreshTechnicalInfoCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }
}
