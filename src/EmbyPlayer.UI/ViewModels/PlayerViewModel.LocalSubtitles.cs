using EmbyPlayer.Player;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class PlayerViewModel
{
    private bool isImportingLocalSubtitle;
    private Task? localSubtitleRestoreTask;
    public bool CanImportLocalSubtitle => CanUseTrackControls() && !isImportingLocalSubtitle && trackOperationsInFlight == 0;

    private void RestoreLocalSubtitleAfterReady()
    {
        if (pendingReloadState?.LocalSubtitlePath is not { } path || localSubtitleRestoreTask is not null) return;
        localSubtitleRestoreTask = ImportLocalSubtitleCoreAsync(path);
    }

    private string WithLocalSubtitleRestoreStatus(string message) => pendingReloadState?.LocalSubtitlePath is { } path
        && !SubtitleTracks.Any(t => t.IsSelected && string.Equals(t.LocalFilePath, path, StringComparison.OrdinalIgnoreCase))
            ? message + "；本地字幕未能恢复，请在字幕菜单重新载入" : message;

    public async Task ImportLocalSubtitleAsync(string filePath)
    {
        if (!CanImportLocalSubtitle) return;
        await ImportLocalSubtitleCoreAsync(filePath).ConfigureAwait(true);
    }

    private async Task ImportLocalSubtitleCoreAsync(string filePath)
    {
        var instance = currentPlaybackInstanceId;
        var token = playbackLifecycleCancellation?.Token ?? CancellationToken.None;
        isImportingLocalSubtitle = true;
        Interlocked.Increment(ref trackOperationsInFlight);
        NotifyEnhancementCommandStates();
        OnPropertyChanged(nameof(CanImportLocalSubtitle));
        PlayerOptionsMessage = "正在载入本地字幕…";
        try
        {
            var result = await playerService.ImportLocalSubtitleAsync(instance, filePath, token).ConfigureAwait(true);
            if (!CanAcceptPlayerEvent(instance)) return;
            if (!result.IsSuccess)
            {
                PlayerOptionsMessage = "字幕加载失败，请检查文件是否可读，或选择 SRT、ASS、SSA、VTT 字幕";
                return;
            }
            var selected = result.Tracks.FirstOrDefault(t => t.Type == "sub" && t.IsSelected == true && t.LocalFilePath is not null);
            if (selected is null)
            {
                PlayerOptionsMessage = "字幕未能选中，请重新选择文件";
                return;
            }
            hasManualSubtitleSelection = true;
            manualSubtitleMpvTrackId = selected.Id;
            manualSubtitleStreamIndex = null;
            rememberedSubtitleTrack = null;
            ApplyMpvTracks(new PlayerStatusChangedEventArgs(instance, PlayerPlaybackState.Playing, tracks: result.Tracks));
            activeSubtitleStreamIndex = null;
            QueueTrackSelectionProgressReport(instance, "local-subtitle");
            PlayerOptionsMessage = "本地字幕已载入，仅用于当前作品";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Local subtitle import failed: {exception.GetType().Name}");
            if (CanAcceptPlayerEvent(instance)) PlayerOptionsMessage = "字幕加载失败，请重新选择文件";
        }
        finally
        {
            Interlocked.Decrement(ref trackOperationsInFlight);
            isImportingLocalSubtitle = false;
            NotifyEnhancementCommandStates();
            OnPropertyChanged(nameof(CanImportLocalSubtitle));
        }
    }
}
