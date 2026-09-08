using System.Buffers.Binary;
using System.Net;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Playback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class EmbyPlaybackPreviewServiceTests
{
    [TestMethod]
    public async Task FramesUsePreviousTimestampAndOneAuthenticatedDownload()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.AreEqual("test-token", request.Headers.GetValues("X-Emby-Token").Single());
            Assert.IsFalse(request.RequestUri!.Query.Contains("token"));
            StringAssert.Contains(request.RequestUri.AbsoluteUri, "/Videos/item/index.bif?Width=320");
            return Task.FromResult(Response(Archive()));
        });
        var service = Create(handler);
        var session = Session();
        foreach (var (seconds, expected) in new[] { (-1, 0), (0, 0), (9, 0), (10, 10), (19, 10), (999, 20) })
        {
            var result = await service.GetPreviewAsync(session, "item", TimeSpan.FromSeconds(seconds), default);
            Assert.AreEqual(PlaybackPreviewError.None, result.Error);
            Assert.AreEqual(TimeSpan.FromSeconds(expected), result.ImagePosition);
            Assert.AreEqual((byte)(expected / 10), result.ImageBytes![2]);
        }
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CacheNeverCrossesItemOrSession()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(Archive())));
        var service = Create(handler);
        var session = Session();
        await service.GetPreviewAsync(session, "one", TimeSpan.Zero, default);
        await service.GetPreviewAsync(session, "two", TimeSpan.Zero, default);
        await service.GetPreviewAsync(Session(), "two", TimeSpan.Zero, default);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task UnsupportedFallsBackPrefixThenCachesTimeOnly()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = Create(handler);
        var session = Session();
        for (var i = 0; i < 3; i++)
        {
            var result = await service.GetPreviewAsync(session, "one", TimeSpan.Zero, default);
            Assert.AreEqual(PlaybackPreviewError.Unavailable, result.Error);
            Assert.IsNull(result.ImageBytes);
        }
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task PrefixFallbackRetrievesArchive()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/emby/")
            ? Response(Archive()) : new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.AreEqual(PlaybackPreviewError.None, (await Create(handler).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error);
        Assert.AreEqual(2, handler.Calls);
    }

    [DataTestMethod]
    [DataRow(true, PlaybackPreviewError.Unavailable)]
    [DataRow(false, PlaybackPreviewError.InvalidResponse)]
    public async Task EmptyBifIsTimeOnlyOnlyWhenTerminalEntryIsValid(bool validTerminal, PlaybackPreviewError expected)
    {
        var bytes = new byte[72];
        new byte[] { 137, 66, 73, 70, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), validTerminal ? uint.MaxValue : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68), 72);
        using var handler = new Handler((_, _) => Task.FromResult(Response(bytes)));
        var service = Create(handler);
        var session = Session();
        var result = await service.GetPreviewAsync(session, "one", TimeSpan.FromSeconds(10), default);
        Assert.AreEqual(expected, result.Error);
        Assert.IsNull(result.ImageBytes);
        await service.GetPreviewAsync(session, "one", TimeSpan.FromSeconds(20), default);
        Assert.AreEqual(1, handler.Calls, "Empty BIF should not be downloaded for every pointer movement.");
    }

    [DataTestMethod]
    [DataRow(401, PlaybackPreviewError.Unauthorized)]
    [DataRow(403, PlaybackPreviewError.Forbidden)]
    [DataRow(500, PlaybackPreviewError.ServerError)]
    public async Task HttpErrorsDoNotFallback(int status, PlaybackPreviewError expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        Assert.AreEqual(expected, (await Create(handler).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CancellationDoesNotPoisonCache()
    {
        var blocked = true;
        using var handler = new Handler(async (_, token) =>
        {
            if (blocked) await Task.Delay(Timeout.Infinite, token);
            return Response(Archive());
        });
        var service = Create(handler);
        var session = Session();
        using var cts = new CancellationTokenSource(50);
        Assert.AreEqual(PlaybackPreviewError.Cancelled, (await service.GetPreviewAsync(session, "one", TimeSpan.Zero, cts.Token)).Error);
        blocked = false;
        Assert.AreEqual(PlaybackPreviewError.None, (await service.GetPreviewAsync(session, "one", TimeSpan.Zero, default)).Error);
    }

    [TestMethod]
    public async Task InvalidArchiveCannotEscapeBounds()
    {
        foreach (var offset in new[] { 0, 8, 12, 64, 68, 72, 76, 92 })
        {
            var bytes = Archive();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), uint.MaxValue);
            using var handler = new Handler((_, _) => Task.FromResult(Response(bytes)));
            Assert.AreEqual(PlaybackPreviewError.InvalidResponse,
                (await Create(handler).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error, $"offset {offset}");
        }
    }

    [TestMethod]
    public async Task OversizedDeclaredResponseIsRejectedBeforeRead()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Response(Archive());
            response.Content.Headers.ContentLength = 33 * 1024 * 1024;
            return Task.FromResult(response);
        });
        Assert.AreEqual(PlaybackPreviewError.InvalidResponse,
            (await Create(handler).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error);
    }

    [TestMethod]
    public async Task ZeroMultiplierUsesSeconds()
    {
        var bytes = Archive();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 20);
        using var handler = new Handler((_, _) => Task.FromResult(Response(bytes)));
        Assert.AreEqual(TimeSpan.FromSeconds(10),
            (await Create(handler).GetPreviewAsync(Session(), "one", TimeSpan.FromSeconds(15), default)).ImagePosition);
    }

    [TestMethod]
    public async Task ChunkedOversizeCannotBypassDownloadLimit()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[32 * 1024 * 1024 + 1]))
        }));
        Assert.AreEqual(PlaybackPreviewError.InvalidResponse,
            (await Create(handler).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error);
    }

    [TestMethod]
    public async Task TimestampOverflowAndNonJpegFrameUseTimeOnly()
    {
        foreach (var overflow in new[] { true, false })
        {
            var bytes = Archive();
            if (overflow)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), uint.MaxValue);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), uint.MaxValue - 1);
            }
            else bytes[96] = 0;
            using var handler = new Handler((_, _) => Task.FromResult(Response(bytes)));
            Assert.AreEqual(PlaybackPreviewError.InvalidResponse,
                (await Create(handler).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error);
        }
    }

    [TestMethod]
    public async Task TransportFailureAndTimeoutRemainNonfatal()
    {
        using var unreachable = new Handler((_, _) => throw new HttpRequestException());
        Assert.AreEqual(PlaybackPreviewError.ServerUnreachable,
            (await Create(unreachable).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error);
        using var timeout = new Handler((_, _) => throw new OperationCanceledException());
        Assert.AreEqual(PlaybackPreviewError.ServerTimeout,
            (await Create(timeout).GetPreviewAsync(Session(), "one", TimeSpan.Zero, default)).Error);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private static byte[] Archive()
    {
        var bytes = new byte[108];
        new byte[] { 137, 66, 73, 70, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 10000);
        for (var i = 0; i <= 3; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64 + i * 8), i == 3 ? uint.MaxValue : (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68 + i * 8), (uint)(96 + i * 4));
            if (i < 3) new byte[] { 255, 216, (byte)i, 217 }.CopyTo(bytes, 96 + i * 4);
        }
        return bytes;
    }

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static AuthSession Session() => new("http://media.local", "test-token", "user", "User", "server");
    private static EmbyPlaybackPreviewService Create(Handler handler) => new(new HttpClient(handler), new Device());
    private sealed class Device : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult("device");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
