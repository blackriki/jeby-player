using System.Net.Http;
using System.Text.Json;
using EmbyPlayer.UI.Navigation;
using EmbyPlayer.UI.ViewModels;

internal static partial class Program
{
    private static async Task RunAudioSwitchStageAsync(PlayerViewModel vm, ServerOutcome outcome,
        ServerMpvLog log, Func<Func<bool>, Task> until)
    {
        var first = outcome.Reports[0];
        var original = vm.AudioTracks.SingleOrDefault(track => track.IsSelected && track.MpvTrackId.HasValue && track.MediaStreamIndex.HasValue);
        var alternate = vm.AudioTracks.FirstOrDefault(track => track.MpvTrackId.HasValue && track.MediaStreamIndex.HasValue
            && track.MpvTrackId != original?.MpvTrackId && track.MediaStreamIndex != original?.MediaStreamIndex);
        if (original is null || alternate is null) throw new CheckNotRun("audio-native-mapped-pair-unavailable");
        outcome.Pass("audio-native-mapped-pair");
        outcome.Details["nativeAudioTrackCount"] = vm.AudioTracks.Count;
        var evidence = new List<object>();
        outcome.Details["audioSelections"] = evidence;
        foreach (var (target, check) in new[] { (alternate, "audio-switch-native-vm-and-report"), (original, "audio-restore-native-vm-and-report") })
        {
            var baseline = first.ProgressSuccessCount;
            var offset = log.CaptureOffset();
            await vm.SelectAudioTrackAsync(target);
            Require(log.HasAudioSelectionAfter(offset, target.MpvTrackId!.Value), check + "-native-readback");
            await until(() => vm.AudioTracks.Any(track => track.IsSelected && track.MpvTrackId == target.MpvTrackId
                    && track.MediaStreamIndex == target.MediaStreamIndex)
                && Volatile.Read(ref first.LastAudioProgress) is { } report
                && report.Sequence > baseline && report.StreamIndex == target.MediaStreamIndex);
            var succeeded = Volatile.Read(ref first.LastAudioProgress)!;
            evidence.Add(new { mpvTrackId = target.MpvTrackId, embyStreamIndex = target.MediaStreamIndex,
                successfulProgressSequence = succeeded.Sequence, freshNativeReadback = true });
            outcome.Pass(check);
        }
        await ((AsyncRelayCommand)vm.BackCommand).ExecuteAsync();
        Require(first.StoppedAttemptCount == 1 && first.StoppedSuccessCount == 1 && first.FailedCount == 0
            && Volatile.Read(ref first.LastStoppedAudioIndex) == original.MediaStreamIndex, "audio-single-stopped-with-restored-index");
        outcome.Pass("audio-single-stopped-with-restored-index");
        outcome.Details["serverWatchHistory"] = "one-sample-short-playback-progress-left-as-recorded";
    }

    private sealed record AudioReportEvidence(int Sequence, int? StreamIndex);

    // Observe the existing detail GET; HttpContent remains buffered and owned by the product caller.
    private sealed class AudioMetadataInspector(Uri origin) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        private string? detailSuffix;
        public int AudioTrackCount { get; private set; }
        public bool DiscoveryCompleted { get; set; }
        public void InspectDetail(string userId, string itemId)
        {
            detailSuffix = "/Users/" + Uri.EscapeDataString(userId) + "/Items/" + Uri.EscapeDataString(itemId);
            AudioTrackCount = 0;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!DiscoveryCompleted)
                Require(request.Method == HttpMethod.Get && request.Content is null && request.RequestUri is { } uri
                    && uri.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port,
                    "audio-discovery-request-outside-scope");
            var response = await base.SendAsync(request, token);
            if (DiscoveryCompleted || !response.IsSuccessStatusCode || detailSuffix is null
                || request.RequestUri?.AbsolutePath.EndsWith(detailSuffix, StringComparison.Ordinal) != true) return response;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                AudioTrackCount = CountAudio(document.RootElement);
                if (document.RootElement.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                    foreach (var source in sources.EnumerateArray()) AudioTrackCount = Math.Max(AudioTrackCount, CountAudio(source));
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        private static int CountAudio(JsonElement source)
        {
            if (!source.TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array) return 0;
            return streams.EnumerateArray().Where(stream => stream.TryGetProperty("Type", out var type) && type.GetString() == "Audio"
                    && stream.TryGetProperty("Index", out var index) && index.TryGetInt32(out _))
                .Select(stream => stream.GetProperty("Index").GetInt32()).Distinct().Count();
        }
    }
}
