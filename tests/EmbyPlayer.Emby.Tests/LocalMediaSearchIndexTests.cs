using System.Net;
using System.Text.Json;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Emby.Tests;

[TestClass]
public sealed class LocalMediaSearchIndexTests
{
    [TestMethod]
    public async Task PrepareAsync_DoesNotLoadOldDiskDocumentWhileCacheMaintenanceOwnsFiles()
    {
        using var directory = new TemporaryDirectory();
        var handler = new RecordingHandler((_, _) => Task.FromResult(CreatePage(new[] { CreateItem("old", "Old Movie") }, 1)));
        await CreateIndex(handler, directory.Path).PrepareAsync(CreateSession(), CancellationToken.None);
        var index = CreateIndex(new RecordingHandler((_, _) => Task.FromResult(CreatePage(Array.Empty<object>(), 0))), directory.Path);
        var gate = (SemaphoreSlim)typeof(LocalMediaSearchIndex)
            .GetField("cacheWriteGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(index)!;
        await gate.WaitAsync();
        Task prepare;
        try
        {
            prepare = index.PrepareAsync(CreateSession(), CancellationToken.None);
            Assert.IsFalse(prepare.IsCompleted, "Disk hydration must wait for cache maintenance.");
            foreach (var path in Directory.GetFiles(directory.Path)) File.Delete(path);
        }
        finally { gate.Release(); }
        await prepare;
        Assert.AreEqual(0, (await index.SearchAsync(CreateSession(), "Old", CancellationToken.None)).Matches.Count);
    }

    [TestMethod]
    public async Task ClearCacheAsync_ReportsOwnedBytesPreservesOtherFilesAndReplacesMemoryIndex()
    {
        using var directory = new TemporaryDirectory();
        var name = "Old Movie";
        var handler = new RecordingHandler((_, _) => Task.FromResult(CreatePage(new[] { CreateItem("movie", name) }, 1)));
        var index = CreateIndex(handler, directory.Path);
        await index.PrepareAsync(CreateSession(), CancellationToken.None);
        var expectedBytes = Directory.GetFiles(directory.Path).Sum(path => new FileInfo(path).Length);
        Assert.IsTrue(expectedBytes > 0);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), "preferences");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "session.dat"), "protected credentials");
        var nested = Directory.CreateDirectory(Path.Combine(directory.Path, "unrelated"));
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "keep.json"), "keep");
        Assert.AreEqual(expectedBytes, await index.GetDiskCacheBytesAsync(CancellationToken.None));

        await index.ClearCacheAsync(CancellationToken.None);

        Assert.AreEqual(0L, await index.GetDiskCacheBytesAsync(CancellationToken.None));
        Assert.AreEqual("preferences", await File.ReadAllTextAsync(Path.Combine(directory.Path, "settings.json")));
        Assert.AreEqual("protected credentials", await File.ReadAllTextAsync(Path.Combine(directory.Path, "session.dat")));
        Assert.IsTrue(File.Exists(Path.Combine(nested.FullName, "keep.json")));
        name = "New Movie";
        Assert.AreEqual(0, (await index.SearchAsync(CreateSession(), "Old", CancellationToken.None)).Matches.Count);
        Assert.AreEqual("New Movie", (await index.SearchAsync(CreateSession(), "New", CancellationToken.None)).Matches.Single().Item.Name);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ClearCacheAsync_CancelsBackgroundBuildAndSuppressesItsLateWrite()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = CreateIndex(new RecordingHandler((_, token) => { started.SetResult(token); return response.Task; }), directory.Path);
        var prepare = index.PrepareAsync(CreateSession(), CancellationToken.None);
        var requestToken = await started.Task;

        await index.ClearCacheAsync(CancellationToken.None);
        Assert.IsTrue(requestToken.IsCancellationRequested);
        response.SetResult(CreatePage(new[] { CreateItem("old", "Old Movie") }, 1));
        await prepare;

        Assert.AreEqual(0, Directory.GetFiles(directory.Path).Length);
        Assert.AreEqual(0L, await index.GetDiskCacheBytesAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task RebuildCacheAsync_CancellationLeavesNoIndexAndNextSearchCanRecover()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        var index = CreateIndex(new RecordingHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return CreatePage(new[] { CreateItem("new", "New Movie") }, 1);
        }), directory.Path);
        using var cancellation = new CancellationTokenSource();
        var rebuild = index.RebuildCacheAsync(CreateSession(), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        try { await rebuild; Assert.Fail("Canceled rebuild must not succeed."); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0L, await index.GetDiskCacheBytesAsync(CancellationToken.None));

        var result = await index.SearchAsync(CreateSession(), "New", CancellationToken.None);
        Assert.AreEqual("New Movie", result.Matches.Single().Item.Name);
        Assert.IsTrue(await index.GetDiskCacheBytesAsync(CancellationToken.None) > 0);
    }

    [TestMethod]
    public async Task RebuildCacheAsync_RefreshesExistingDiskAndMemory()
    {
        using var directory = new TemporaryDirectory();
        var name = "Old Movie";
        var index = CreateIndex(new RecordingHandler((_, _) => Task.FromResult(CreatePage(new[] { CreateItem("movie", name) }, 1))), directory.Path);
        await index.PrepareAsync(CreateSession(), CancellationToken.None);
        name = "Fresh Movie";
        await index.RebuildCacheAsync(CreateSession(), CancellationToken.None);
        Assert.AreEqual("Fresh Movie", (await index.SearchAsync(CreateSession(), "Fresh", CancellationToken.None)).Matches.Single().Item.Name);
        StringAssert.Contains(await File.ReadAllTextAsync(Directory.GetFiles(directory.Path).Single()), "Fresh Movie");
    }

    [TestMethod]
    public async Task ClearCacheAsync_RejectsFilesystemRootWithoutDeletingAnything()
    {
        var index = CreateIndex(new RecordingHandler((_, _) => throw new AssertFailedException()), Path.GetPathRoot(Path.GetTempPath())!);
        await Assert.ThrowsExceptionAsync<IOException>(() => index.ClearCacheAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SearchAsync_ColdIndexReadsEveryPageAndFindsMiddleChineseKeyword()
    {
        using var directory = new TemporaryDirectory();
        var handler = new RecordingHandler((request, _) =>
        {
            var startIndex = GetQueryInt(request.RequestUri!, "StartIndex");
            var items = startIndex == 0
                ? Enumerable.Range(0, 400).Select(index => CreateItem($"item-{index}", $"Title {index}"))
                : Enumerable.Range(400, 51).Select(index =>
                    CreateItem(
                        $"item-{index}",
                        index == 450 ? "金特务" : $"Title {index}"));
            return Task.FromResult(CreatePage(items, 451));
        });
        var indexService = CreateIndex(handler, directory.Path);

        var result = await indexService.SearchAsync(
            CreateSession(),
            "特务",
            CancellationToken.None);

        Assert.AreEqual(451, result.TotalIndexedItems);
        Assert.AreEqual("金特务", result.Matches.Single().Item.Name);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(0, GetQueryInt(handler.Requests[0], "StartIndex"));
        Assert.AreEqual(400, GetQueryInt(handler.Requests[1], "StartIndex"));
        Assert.IsTrue(result.LastBuildDuration > TimeSpan.Zero);

        await indexService.SearchAsync(CreateSession(), "Title 1", CancellationToken.None);
        Assert.AreEqual(2, handler.Requests.Count, "A search must not download the library again.");
    }

    [TestMethod]
    public async Task SearchAsync_RanksNameMatchesBeforeOriginalTitleAndSeriesName()
    {
        using var directory = new TemporaryDirectory();
        var items = new object[]
        {
            CreateItem("series", "Other Episode", seriesName: "特务系列", type: "Episode"),
            CreateItem("contains", "金特务"),
            CreateItem("original", "Other Movie", originalTitle: "特务代号"),
            CreateItem("prefix", "特务局"),
            CreateItem("exact", "特务"),
            CreateItem("english", "Secret Agent")
        };
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(CreatePage(items, items.Length)));
        var indexService = CreateIndex(handler, directory.Path);

        var chineseResult = await indexService.SearchAsync(
            CreateSession(),
            "特务",
            CancellationToken.None);
        var fullTitleResult = await indexService.SearchAsync(
            CreateSession(),
            "金特务",
            CancellationToken.None);
        var englishResult = await indexService.SearchAsync(
            CreateSession(),
            "secret agent",
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "exact", "prefix", "contains", "original", "series" },
            chineseResult.Matches.Select(match => match.Item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                SearchMatchSource.Name,
                SearchMatchSource.Name,
                SearchMatchSource.Name,
                SearchMatchSource.OriginalTitle,
                SearchMatchSource.SeriesName
            },
            chineseResult.Matches.Select(match => match.MatchSource).ToArray());
        Assert.AreEqual("contains", fullTitleResult.Matches.Single().Item.Id);
        Assert.AreEqual("english", englishResult.Matches.Single().Item.Id);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SearchAsync_RecordsSortNameAsDirectTitleMatch()
    {
        using var directory = new TemporaryDirectory();
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(CreatePage(
                new[] { CreateItem("sort", "Display Name", sortName: "Agent Archive") },
                1)));
        var indexService = CreateIndex(handler, directory.Path);

        var result = await indexService.SearchAsync(
            CreateSession(),
            "archive",
            CancellationToken.None);

        Assert.AreEqual("sort", result.Matches.Single().Item.Id);
        Assert.AreEqual(SearchMatchSource.SortName, result.Matches.Single().MatchSource);
    }

    [TestMethod]
    public async Task SearchAsync_IsolatesIndexesByServerAndUser()
    {
        using var directory = new TemporaryDirectory();
        var handler = new RecordingHandler((request, _) =>
        {
            var userId = request.RequestUri!.AbsolutePath.Split('/')[2];
            var name = request.RequestUri.Host switch
            {
                "server-a.local" when userId == "user-1" => "Alpha Movie",
                "server-a.local" => "Beta Movie",
                _ => "Gamma Movie"
            };
            return Task.FromResult(CreatePage(new[] { CreateItem($"{request.RequestUri.Host}-{userId}", name) }, 1));
        });
        var indexService = CreateIndex(handler, directory.Path);
        var serverAUser1 = CreateSession("http://server-a.local:8096", "server-a", "user-1");
        var serverAUser2 = CreateSession("http://server-a.local:8096", "server-a", "user-2");
        var serverBUser1 = CreateSession("http://server-b.local:8096", "server-b", "user-1");

        var alpha = await indexService.SearchAsync(serverAUser1, "Alpha", CancellationToken.None);
        var beta = await indexService.SearchAsync(serverAUser2, "Beta", CancellationToken.None);
        var gamma = await indexService.SearchAsync(serverBUser1, "Gamma", CancellationToken.None);

        Assert.AreEqual("Alpha Movie", alpha.Matches.Single().Item.Name);
        Assert.AreEqual("Beta Movie", beta.Matches.Single().Item.Name);
        Assert.AreEqual("Gamma Movie", gamma.Matches.Single().Item.Name);
        Assert.AreEqual(3, Directory.GetFiles(directory.Path, "*.json").Length);
    }

    [TestMethod]
    public async Task SearchAsync_LoadsPersistentIndexBeforeFailedBackgroundRefresh()
    {
        using var directory = new TemporaryDirectory();
        var firstHandler = new RecordingHandler((_, _) =>
            Task.FromResult(CreatePage(new[] { CreateItem("spy-1", "金特务") }, 1)));
        var firstService = CreateIndex(firstHandler, directory.Path);
        await firstService.PrepareAsync(CreateSession(), CancellationToken.None);

        var failingHandler = new RecordingHandler((_, _) =>
            throw new HttpRequestException("server unavailable"));
        var secondService = CreateIndex(failingHandler, directory.Path);

        var result = await secondService.SearchAsync(
            CreateSession(),
            "特务",
            CancellationToken.None);

        Assert.AreEqual("金特务", result.Matches.Single().Item.Name);
        Assert.AreEqual(1, result.TotalIndexedItems);
        Assert.IsTrue(result.LastLoadDuration >= TimeSpan.Zero);
    }

    [TestMethod]
    public async Task SearchAsync_CancellationStopsWaitingForInitialIndex()
    {
        using var directory = new TemporaryDirectory();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, _) =>
        {
            requestStarted.TrySetResult();
            await releaseRequest.Task;
            return CreatePage(Array.Empty<object>(), 0);
        });
        var indexService = CreateIndex(handler, directory.Path);
        using var cancellationSource = new CancellationTokenSource();

        var searchTask = indexService.SearchAsync(
            CreateSession(),
            "特务",
            cancellationSource.Token);
        await requestStarted.Task;
        cancellationSource.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => searchTask);
        releaseRequest.TrySetResult();
        await indexService.PrepareAsync(CreateSession(), CancellationToken.None);
    }

    private static LocalMediaSearchIndex CreateIndex(
        RecordingHandler handler,
        string directory)
    {
        return new LocalMediaSearchIndex(
            new HttpClient(handler),
            new TestDeviceIdService(),
            directory);
    }

    private static AuthSession CreateSession(
        string serverBase = "http://media.local:8096",
        string serverId = "server-1",
        string userId = "user-1")
    {
        return new AuthSession(
            serverBase,
            "test-token",
            userId,
            "Test User",
            serverId);
    }

    private static object CreateItem(
        string id,
        string name,
        string? originalTitle = null,
        string? seriesName = null,
        string type = "Movie",
        string? sortName = null)
    {
        return new
        {
            Id = id,
            Name = name,
            OriginalTitle = originalTitle,
            SortName = sortName ?? name,
            SeriesName = seriesName,
            Type = type,
            ProductionYear = 2025,
            ImageTags = new { Primary = $"image-{id}" }
        };
    }

    private static HttpResponseMessage CreatePage(IEnumerable<object> items, int totalRecordCount)
    {
        var json = JsonSerializer.Serialize(new
        {
            Items = items,
            TotalRecordCount = totalRecordCount
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private static int GetQueryInt(Uri uri, string key)
    {
        var prefix = $"{key}=";
        var value = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))[prefix.Length..];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }

            return handler(request, cancellationToken);
        }
    }

    private sealed class TestDeviceIdService : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult("test-device-id");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "EmbyPlayer.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
