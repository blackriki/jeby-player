using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Player.Tests;

public sealed partial class MpvPlayerServiceLifecycleTests
{
    [TestMethod]
    public async Task LocalSubtitle_UsesArgumentArrayAndNeverMapsLocalTrackToServerIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"本地 字幕-{Guid.NewGuid():N}.srt");
        await File.WriteAllTextAsync(path, "1\n00:00:00,000 --> 00:00:02,000\nTest\n");
        try
        {
            var native = new FakeMpvNativeApi { AddedExternalSubtitleFfIndex = 3 };
            native.Events.Enqueue(new MpvEventSnapshot(MpvEventId.FileLoaded, 0));
            var diagnostics = new FakePlayerDiagnostics();
            await using var service = new MpvPlayerService(native, diagnostics);
            var ready = new TaskCompletionSource();
            service.StatusChanged += (_, e) => { if (e.State == PlayerPlaybackState.Playing) ready.TrySetResult(); };
            var request = CreateRequest();
            await service.LoadAsync(request, CancellationToken.None);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var result = await service.ImportLocalSubtitleAsync(request.PlaybackInstanceId, path, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess);
            var selected = result.Tracks.Single(t => t.Type == "sub" && t.IsSelected == true);
            Assert.AreEqual(path, selected.LocalFilePath);
            Assert.IsNull(selected.MediaStreamIndex);
            CollectionAssert.AreEqual(new[] { "sub-add", path, "cached", Path.GetFileName(path) }, native.CommandArguments.Last(a => a[0] == "sub-add").ToArray());
            Assert.IsFalse(diagnostics.Messages.Any(m => m.Contains(path, StringComparison.Ordinal)));
            var count = native.CommandArguments.Count(a => a[0] == "sub-add");
            Assert.IsFalse((await service.ImportLocalSubtitleAsync(request.PlaybackInstanceId + 1, path, CancellationToken.None)).IsSuccess);
            Assert.AreEqual(count, native.CommandArguments.Count(a => a[0] == "sub-add"));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task LocalSubtitle_RejectsUnsupportedMissingOrNonLocalPathsWithoutNativeLoad()
    {
        var native = new FakeMpvNativeApi();
        await using var service = CreateService(native);
        foreach (var path in new[] { "https://example.test/a.srt", "relative.srt", Path.Combine(Path.GetTempPath(), "missing-file.srt"), Path.Combine(Path.GetTempPath(), "movie.mp4") })
            Assert.IsFalse((await service.ImportLocalSubtitleAsync(1, path, CancellationToken.None)).IsSuccess);
        Assert.IsFalse(native.CommandArguments.Any(a => a[0] == "sub-add"));
    }
}
