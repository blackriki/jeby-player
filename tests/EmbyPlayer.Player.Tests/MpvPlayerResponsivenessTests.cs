using System.Collections.Concurrent;
using System.Diagnostics;
using EmbyPlayer.Core.Diagnostics;
using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Player.Tests;

[TestClass]
public sealed class MpvPlayerResponsivenessTests
{
    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    [DataRow("initialize")]
    [DataRow("seek")]
    [DataRow("stop")]
    public async Task NativeOperationsReturnPromptlyWhenNativeCallIsSlow(string operation)
    {
        var native = new SlowNative();
        await using var service = new MpvPlayerService(native, new Diagnostics());
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) => { if (status.State == PlayerPlaybackState.Playing) playing.TrySetResult(); };
        var info = new PlaybackInfo("test", "Test", null,
            new("source", "mkv", true, true, false, new Dictionary<string, string>()), "test.mkv", false, false,
            TimeSpan.FromMinutes(1).Ticks, 0, Array.Empty<PlaybackTrack>(), Array.Empty<PlaybackSubtitle>());
        var request = new PlayerLoadRequest(info, new IntPtr(1), 42);
        if (operation != "initialize")
        {
            Assert.IsTrue((await service.LoadAsync(request, default)).IsSuccess);
            await playing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        native.SlowOperation = operation;
        var watch = Stopwatch.StartNew();
        var invocation = new TaskCompletionSource<(Task<PlayerOperationResult> Pending, long Elapsed, int Caller)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var callerWatch = Stopwatch.StartNew();
                var pending = operation switch
                {
                    "initialize" => service.LoadAsync(request, default),
                    "seek" => service.SeekAsync(42, TimeSpan.FromSeconds(20), default),
                    _ => service.StopAsync(default)
                };
                invocation.SetResult((pending, callerWatch.ElapsedMilliseconds, Environment.CurrentManagedThreadId));
            }
            catch (Exception exception) { invocation.SetException(exception); }
        }) { IsBackground = true };
        thread.Start();
        var (pendingOperation, callerElapsed, caller) = await invocation.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue((await pendingOperation).IsSuccess);
        var totalElapsed = watch.ElapsedMilliseconds;
        native.SlowOperation = null;
        TestContext.WriteLine($"operation={operation}; callerReturnMs={callerElapsed}; totalMs={totalElapsed}; nativeThread={native.SlowThread}; callerThread={caller}");
        Assert.IsTrue(totalElapsed >= 250, "Fixture must really perform the delayed native operation.");
        Assert.IsTrue(callerElapsed < 150, $"{operation} blocked its caller for {callerElapsed} ms before returning its Task.");
        Assert.AreNotEqual(caller, native.SlowThread, "Native delay must execute outside the calling thread.");
    }

    private sealed class Diagnostics : IPlayerDiagnostics { public void Write(string message) { } }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledLoadRetiresExistingHandleWithoutStartingMedia(bool replacing)
    {
        var native = new SlowNative();
        await using var service = new MpvPlayerService(native, new Diagnostics());
        var info = new PlaybackInfo("test", "Test", null,
            new("source", "mkv", true, true, false, new Dictionary<string, string>()), "test.mkv", false, false,
            TimeSpan.FromMinutes(1).Ticks, 0, Array.Empty<PlaybackTrack>(), Array.Empty<PlaybackSubtitle>());
        var request = new PlayerLoadRequest(info, new IntPtr(1), 42);
        if (replacing) Assert.IsTrue((await service.LoadAsync(request, default)).IsSuccess);
        native.SlowOperation = replacing ? "stop" : "initialize";
        using var cancellation = new CancellationTokenSource();
        var pending = service.LoadAsync(request with { PlaybackInstanceId = 43 }, cancellation.Token);
        Assert.IsTrue(native.DelayedCallStarted.Wait(2000));
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pending);
        native.SlowOperation = null;
        Assert.AreEqual(1, native.CreateCount);
        Assert.AreEqual(1, native.DestroyCount);
        Assert.AreEqual(replacing ? 1 : 0, native.LoadCount, "Cancelled initialization/replacement must not issue a new loadfile.");
    }

    [TestMethod]
    public async Task CancellationAfterHandleDetachStillDestroysItAfterEventReaderExits()
    {
        var native = new SlowNative();
        await using var service = new MpvPlayerService(native, new Diagnostics());
        var info = new PlaybackInfo("test", "Test", null,
            new("source", "mkv", true, true, false, new Dictionary<string, string>()), "test.mkv", false, false,
            TimeSpan.FromMinutes(1).Ticks, 0, Array.Empty<PlaybackTrack>(), Array.Empty<PlaybackSubtitle>());
        Assert.IsTrue((await service.LoadAsync(new(info, new IntPtr(1), 42), default)).IsSuccess);
        native.BlockEventReader = true;
        Assert.IsTrue(native.EventReaderBlocked.Wait(2000));
        using var cancellation = new CancellationTokenSource();
        var stop = service.StopAsync(cancellation.Token);
        cancellation.Cancel();
        native.ReleaseEventReader.Set();
        try { await stop; } catch (OperationCanceledException) { }
        await Task.Delay(30);
        Assert.AreEqual(1, native.DestroyCount, "Caller cancellation must not abandon an already detached native handle.");
        Assert.IsFalse(native.DestroyedWhileReading);
    }
    [TestMethod]
    public async Task ImmediateStopCannotOvertakeAnEarlierLoad()
    {
        var native = new SlowNative();
        await using var service = new MpvPlayerService(native, new Diagnostics());
        var info = new PlaybackInfo("test", "Test", null,
            new("source", "mkv", true, true, false, new Dictionary<string, string>()), "test.mkv", false, false,
            TimeSpan.FromMinutes(1).Ticks, 0, Array.Empty<PlaybackTrack>(), Array.Empty<PlaybackSubtitle>());
        for (var instance = 1; instance <= 64; instance++)
        {
            var load = service.LoadAsync(new(info, new IntPtr(1), instance), default);
            var stop = service.StopAsync(default);
            Assert.IsTrue((await load).IsSuccess);
            Assert.IsTrue((await stop).IsSuccess);
            Assert.AreEqual(native.CreateCount, native.DestroyCount,
                "Stop must include every load requested before it, even when the load worker has not run yet.");
        }
    }

    private sealed class SlowNative : IMpvNativeApi
    {
        private readonly ConcurrentQueue<MpvEventSnapshot> events = new();
        public string? SlowOperation;
        public int SlowThread;
        public readonly ManualResetEventSlim DelayedCallStarted = new();
        public int CreateCount;
        public int LoadCount;
        public volatile bool BlockEventReader;
        public readonly ManualResetEventSlim EventReaderBlocked = new();
        public readonly ManualResetEventSlim ReleaseEventReader = new();
        private volatile bool reading;
        public int DestroyCount;
        public bool DestroyedWhileReading;
        private void Delay(string operation)
        {
            if (SlowOperation != operation) return;
            SlowThread = Environment.CurrentManagedThreadId;
            DelayedCallStarted.Set();
            Thread.Sleep(300);
        }
        public IntPtr Create() { CreateCount++; return new(7); }
        public int Initialize(IntPtr context) { Delay("initialize"); return 0; }
        public int RequestLogMessages(IntPtr context, string minimumLevel) => 0;
        public int SetOptionString(IntPtr context, string name, string value) => 0;
        public int SetPropertyString(IntPtr context, string name, string value) => 0;
        public int Command(IntPtr context, IReadOnlyList<string> args)
        {
            Delay(args[0]);
            if (args[0] == "loadfile") { LoadCount++; events.Enqueue(new(MpvEventId.FileLoaded, 0)); }
            return 0;
        }
        public MpvEventSnapshot WaitEvent(IntPtr context, double timeout)
        {
            reading = true;
            try
            {
                if (BlockEventReader) { EventReaderBlocked.Set(); ReleaseEventReader.Wait(2000); }
                if (events.TryDequeue(out var item)) return item;
                Thread.Sleep(5); return new(MpvEventId.None, 0);
            }
            finally { reading = false; }
        }
        public string? GetPropertyString(IntPtr context, string name) => name == "track-list" ? "[]" : "1";
        public bool? GetFlagProperty(IntPtr context, string name) => name == "seekable";
        public double? GetDoubleProperty(IntPtr context, string name) => name == "duration" ? 60 : 0;
        public void Wakeup(IntPtr context) { }
        public void TerminateDestroy(IntPtr context) { DestroyCount++; DestroyedWhileReading |= reading; }
    }
}
