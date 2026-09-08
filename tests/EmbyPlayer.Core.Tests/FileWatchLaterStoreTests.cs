using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.WatchLater;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class FileWatchLaterStoreTests
{
    [TestMethod]
    public async Task AddAndRemovePersistExactEpisodeIdentityAcrossRestart()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        var session = Session();
        var episode = Item("episode-7", "Episode") with { Title = "第七集", ProductionYear = 2025 };
        await store.AddAsync(session, episode, default);
        var restarted = new FileWatchLaterStore(directory.Path);
        CollectionAssert.AreEqual(new[] { episode }, (await restarted.LoadAsync(session, default)).ToArray());
        Assert.IsTrue(await restarted.IsSavedAsync(session, "EPISODE-7", default));
        await restarted.RemoveAsync(session, "episode-7", default);
        Assert.IsFalse(await new FileWatchLaterStore(directory.Path).IsSavedAsync(session, "episode-7", default));
    }

    [TestMethod]
    public async Task ScopeCanonicalizesHostSchemePortButPreservesPathAndUserIdentity()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        await store.AddAsync(Session("HTTP://MEDIA.LOCAL:80/Emby/"), Item("one"), default);
        Assert.AreEqual(1, (await store.LoadAsync(Session("http://media.local/Emby", token: "rotated-token"), default)).Count);
        foreach (var session in new[] { Session("http://media.local/emby"), Session("https://media.local/Emby"),
            Session("http://media.local/Emby", user: "other-user"), Session("http://other.local/Emby") })
            Assert.AreEqual(0, (await store.LoadAsync(session, default)).Count);
        Assert.AreEqual(1, Directory.GetFiles(directory.Path, "*.json").Length);
    }

    [TestMethod]
    public async Task AddingExistingItemUpdatesMetadataAndMovesToFrontWithoutLimitingList()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        var session = Session();
        for (var index = 0; index < 27; index++) await store.AddAsync(session, Item(index.ToString()), default);
        await store.AddAsync(session, Item(" 0 ", "Series") with { Title = "Updated" }, default);
        var items = await store.LoadAsync(session, default);
        Assert.AreEqual(27, items.Count);
        Assert.AreEqual("0", items[0].ItemId);
        Assert.AreEqual("Updated", items[0].Title);
        Assert.AreEqual("Series", items[0].MediaType);
        Assert.AreEqual("26", items[1].ItemId);
    }

    [DataTestMethod]
    [DataRow("http://media.local/Items/one/Images/Primary?api_key=test-access-token")]
    [DataRow("http://media.local/Items/one/Images/Primary?%74oken=unknown-token")]
    [DataRow("http://media.local/Items/one/Images/Primary?tag=test-access-token")]
    [DataRow("http://media.local/Items/test-access-token/Images/Primary")]
    [DataRow("http://other.local/Items/one/Images/Primary")]
    [DataRow("http://user:password@media.local/Items/one/Images/Primary")]
    [DataRow("http://media.local/Items/one/Images/Primary#secret")]
    public async Task UnsafeImageUrlsAreNeverPersisted(string imageUrl)
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        var session = Session();
        await store.AddAsync(session, Item("one") with { ImageUrl = imageUrl }, default);
        await store.AddAsync(session, Item("two"), default);
        Assert.IsNull((await store.LoadAsync(session, default))[1].ImageUrl);
        foreach (var file in Directory.GetFiles(directory.Path))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.IsFalse(text.Contains(session.AccessToken, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("ImageUrl", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("password", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("AccessToken", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task SafeAuthenticatedImageMetadataPersistsAndProxyPathIsEnforced()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        var session = Session("https://media.local/Emby");
        const string imageUrl = "https://media.local/Emby/Items/one/Images/Primary?tag=image-tag&maxWidth=360&quality=90";
        await store.AddAsync(session, Item("one") with { ImageUrl = imageUrl }, default);
        await store.AddAsync(session, Item("two") with { ImageUrl = "https://media.local/Other/Items/two/Images/Primary" }, default);
        var items = await new FileWatchLaterStore(directory.Path).LoadAsync(session, default);
        Assert.IsNull(items[0].ImageUrl);
        Assert.AreEqual(imageUrl, items[1].ImageUrl);
    }

    [TestMethod]
    public async Task ConcurrentMutationsDoNotDropItemsOrOverwriteOtherScopes()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        var sessions = new[] { Session(), Session(user: "second-user") };
        await Task.WhenAll(sessions.SelectMany(session => Enumerable.Range(0, 15)
            .Select(index => store.AddAsync(session, Item(index.ToString()), default))));
        foreach (var session in sessions) Assert.AreEqual(15, (await store.LoadAsync(session, default)).Count);
    }

    [TestMethod]
    public async Task CancelledMutationLeavesPersistedListUnchanged()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        await store.AddAsync(Session(), Item("one"), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => store.RemoveAsync(Session(), "one", cancellation.Token));
        Assert.IsTrue(await store.IsSavedAsync(Session(), "one", default));
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task LockedFileReportsIoFailureAndDoesNotReturnEmptyOrClaimMutationSucceeded()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        await store.AddAsync(Session(), Item("one"), default);
        var file = Directory.GetFiles(directory.Path, "*.json").Single();
        await using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsExceptionAsync<IOException>(() => store.LoadAsync(Session(), default));
            await Assert.ThrowsExceptionAsync<IOException>(() => store.AddAsync(Session(), Item("two"), default));
        }
        Assert.AreEqual("one", (await store.LoadAsync(Session(), default)).Single().ItemId);
    }

    [TestMethod]
    public async Task FailedReplacementRetainsOriginalAndCleansTemporaryFile()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        await store.AddAsync(Session(), Item("one"), default);
        var file = Directory.GetFiles(directory.Path, "*.json").Single();
        await using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsExceptionAsync<IOException>(() => store.AddAsync(Session(), Item("two"), default));
        Assert.AreEqual("one", (await store.LoadAsync(Session(), default)).Single().ItemId);
        Assert.AreEqual(0, Directory.GetFiles(directory.Path, "*.tmp").Length);
    }

    [TestMethod]
    public async Task CorruptPrimaryRecoversValidBackupAndRepairsPrimary()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        await store.AddAsync(Session(), Item("one"), default);
        await store.AddAsync(Session(), Item("two"), default);
        var file = Directory.GetFiles(directory.Path, "*.json").Single();
        await File.WriteAllTextAsync(file, "{broken");
        Assert.AreEqual("one", (await store.LoadAsync(Session(), default)).Single().ItemId);
        Assert.AreEqual("one", (await new FileWatchLaterStore(directory.Path).LoadAsync(Session(), default)).Single().ItemId);
        StringAssert.Contains(await File.ReadAllTextAsync(file), "\"Version\": 1");
    }

    [DataTestMethod]
    [DataRow("{broken")]
    [DataRow("{}")]
    [DataRow("{\"Version\":2,\"Items\":[]}")]
    [DataRow("{\"Version\":1,\"Items\":[null]}")]
    public async Task CorruptListRequiresExplicitResetAndArchivesOnlyItsScope(string corrupt)
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        await store.AddAsync(Session(), Item("one"), default);
        var file = Directory.GetFiles(directory.Path, "*.json").Single();
        await store.AddAsync(Session(user: "other"), Item("safe"), default);
        await File.WriteAllTextAsync(file, corrupt);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.LoadAsync(Session(), default));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.AddAsync(Session(), Item("two"), default));
        Assert.AreEqual(corrupt, await File.ReadAllTextAsync(file));
        await store.ResetCorruptedAsync(Session(), default);
        Assert.AreEqual(0, (await store.LoadAsync(Session(), default)).Count);
        Assert.AreEqual(corrupt, await File.ReadAllTextAsync(Directory.GetFiles(directory.Path, "*.corrupt-*").Single()));
        Assert.AreEqual("safe", (await store.LoadAsync(Session(user: "other"), default)).Single().ItemId);
        await store.AddAsync(Session(), Item("new"), default);
        Assert.AreEqual("new", (await store.LoadAsync(Session(), default)).Single().ItemId);
    }

    [TestMethod]
    public async Task ResetDoesNotDiscardListRepairedSinceErrorWasShown()
    {
        using var directory = new TestDirectory();
        var store = new FileWatchLaterStore(directory.Path);
        await store.AddAsync(Session(), Item("one"), default);
        await store.ResetCorruptedAsync(Session(), default);
        Assert.IsTrue(await store.IsSavedAsync(Session(), "one", default));
    }

    private static AuthSession Session(string server = "http://media.local", string user = "user-1", string token = "test-access-token")
        => new(server, token, user, "User", "server-id");
    private static WatchLaterItem Item(string id, string type = "Movie") => new(id, "Title " + id, null, type, 2024);

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EmbyPlayer-WatchLater-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
