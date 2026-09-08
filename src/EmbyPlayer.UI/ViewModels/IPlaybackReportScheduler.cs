namespace EmbyPlayer.UI.ViewModels;

public interface IPlaybackReportScheduler
{
    event EventHandler<long>? Tick;

    void Start(long playbackInstanceId, TimeSpan interval);

    void Stop(long playbackInstanceId);

    void StopAll();
}
