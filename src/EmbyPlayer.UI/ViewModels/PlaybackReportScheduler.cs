namespace EmbyPlayer.UI.ViewModels;

public sealed class PlaybackReportScheduler : IPlaybackReportScheduler
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellationTokenSource;
    private long activePlaybackInstanceId;

    public event EventHandler<long>? Tick;

    public void Start(long playbackInstanceId, TimeSpan interval)
    {
        StopAll();
        var cancellation = new CancellationTokenSource();
        lock (gate)
        {
            activePlaybackInstanceId = playbackInstanceId;
            cancellationTokenSource = cancellation;
        }

        _ = RunAsync(playbackInstanceId, interval, cancellation.Token);
    }

    public void Stop(long playbackInstanceId)
    {
        lock (gate)
        {
            if (playbackInstanceId != activePlaybackInstanceId)
            {
                return;
            }
        }

        StopAll();
    }

    public void StopAll()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            activePlaybackInstanceId = 0;
            cancellation = cancellationTokenSource;
            cancellationTokenSource = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task RunAsync(long playbackInstanceId, TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Tick?.Invoke(this, playbackInstanceId);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
