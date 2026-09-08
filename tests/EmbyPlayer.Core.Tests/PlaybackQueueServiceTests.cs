using EmbyPlayer.Core.PlaybackQueue;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class PlaybackQueueServiceTests
{
    [TestMethod]
    public void Add_DeduplicatesCurrentAndPendingAndRejectsNonPlayableTypes()
    {
        var queue = CreateQueue();
        Assert.IsTrue(queue.Add(Item("movie")));
        Assert.IsFalse(queue.Add(Item("movie") with { Title = "Alternate title" }));
        Assert.IsTrue(queue.Add(Item("episode") with { MediaType = "episode" }));
        Assert.IsFalse(queue.Add(Item("series") with { MediaType = "Series" }));
        Assert.IsFalse(queue.Add(Item("audio") with { MediaType = "Audio" }));
        Assert.IsFalse(queue.Add(Item(" ")));
        Assert.IsTrue(queue.SetCurrent(Item("movie"), queue.Snapshot.SessionVersion));
        Assert.IsFalse(queue.Add(Item("movie")));
        Assert.AreEqual("Episode", queue.Snapshot.Pending.Single().MediaType);
    }

    [TestMethod]
    public void ReorderAndRemove_ControlTheNextSequenceWithoutConsumingOnPeek()
    {
        var queue = CreateQueue();
        foreach (var id in new[] { "a", "b", "c", "d" }) queue.Add(Item(id));
        Assert.IsTrue(queue.Move("d", 0));
        Assert.IsTrue(queue.Move("a", 2));
        CollectionAssert.AreEqual(new[] { "d", "b", "a", "c" }, queue.Snapshot.Pending.Select(item => item.ItemId).ToArray());
        Assert.IsTrue(queue.Remove("b"));
        Assert.IsTrue(queue.TryGetNext(out var next));
        Assert.AreEqual("d", next!.ItemId);
        Assert.IsTrue(queue.TryGetNext(out var repeated));
        Assert.AreEqual(next, repeated, "Preparing playback must not consume the candidate.");
        foreach (var expected in new[] { "d", "a", "c" })
        {
            Assert.IsTrue(queue.TryGetNext(out next));
            Assert.AreEqual(expected, next!.ItemId);
            Assert.IsTrue(queue.SetCurrent(next, queue.Snapshot.SessionVersion));
            Assert.AreEqual(expected, queue.Snapshot.Current!.ItemId);
        }
        Assert.IsFalse(queue.TryGetNext(out next));
        Assert.IsNull(next);
        Assert.AreEqual("c", queue.Snapshot.Current!.ItemId);
    }

    [TestMethod]
    public void CurrentSelection_RemovesOnlyChosenPendingItemAndKeepsItsArtwork()
    {
        var queue = CreateQueue();
        var candidate = Item("b") with { ImageUrl = "https://images.example.invalid/poster", Title = "Queue title" };
        queue.SetCurrent(Item("playing"), queue.Snapshot.SessionVersion);
        queue.Add(Item("a"));
        queue.Add(candidate);
        queue.Add(Item("c"));
        Assert.IsTrue(queue.SetCurrent(Item("b"), queue.Snapshot.SessionVersion));
        Assert.AreEqual(candidate, queue.Snapshot.Current);
        CollectionAssert.AreEqual(new[] { "a", "c" }, queue.Snapshot.Pending.Select(item => item.ItemId).ToArray());
        Assert.IsFalse(queue.Remove("b"), "Removing pending items must not stop or remove the current item.");
        queue.ClearPending();
        Assert.AreEqual(candidate, queue.Snapshot.Current);
        Assert.AreEqual(0, queue.Snapshot.Pending.Count);
    }

    [TestMethod]
    public void SessionIdentity_NormalizesServerAndPreservesSameAccountQueue()
    {
        var queue = CreateQueue();
        queue.Add(Item("a"));
        queue.SetAutoPlayEnabled(false);
        var before = queue.Snapshot;
        queue.SetSession(" HTTP://MEDIA.LOCAL:8096/emby/ ", "user-1");
        Assert.AreSame(before, queue.Snapshot);
        Assert.IsFalse(queue.Snapshot.AutoPlayEnabled);
    }

    [DataTestMethod]
    [DataRow("http://different.local:8096/emby", "user-1")]
    [DataRow("http://media.local:8096/emby", "user-2")]
    [DataRow(null, null)]
    public void ChangingSession_ClearsPrivateItemsAndRejectsOldCompletion(string? server, string? user)
    {
        var queue = CreateQueue();
        var previousVersion = queue.Snapshot.SessionVersion;
        queue.SetCurrent(Item("a"), previousVersion);
        queue.Add(Item("b"));
        queue.SetAutoPlayEnabled(false);
        queue.SetSession(server, user);
        Assert.IsNull(queue.Snapshot.Current);
        Assert.AreEqual(0, queue.Snapshot.Pending.Count);
        Assert.IsTrue(queue.Snapshot.AutoPlayEnabled);
        Assert.IsTrue(queue.Snapshot.SessionVersion > previousVersion);
        Assert.IsFalse(queue.SetCurrent(Item("b"), previousVersion));
        Assert.IsFalse(queue.ClearCurrent(previousVersion));
        Assert.AreEqual(server is not null, queue.Snapshot.HasSession);
    }

    [TestMethod]
    public void LogoutThenSameAccountLogin_DoesNotRestoreDiscardedQueue()
    {
        var queue = CreateQueue();
        queue.Add(Item("a"));
        var oldVersion = queue.Snapshot.SessionVersion;
        queue.SetSession(null, null);
        Assert.IsFalse(queue.Add(Item("b")));
        queue.SetSession("http://media.local:8096/emby", "user-1");
        Assert.AreEqual(0, queue.Snapshot.Pending.Count);
        Assert.IsFalse(queue.SetCurrent(Item("a"), oldVersion));
    }

    [TestMethod]
    public void SnapshotsStayImmutableAndNoOpEditsDoNotNotify()
    {
        var queue = CreateQueue();
        var changes = 0;
        queue.Changed += (_, _) => changes++;
        queue.Add(Item("a"));
        var saved = queue.Snapshot;
        queue.Add(Item("b"));
        Assert.AreEqual(1, saved.Pending.Count);
        Assert.ThrowsException<NotSupportedException>(() => ((IList<PlaybackQueueItem>)saved.Pending).Add(Item("c")));
        Assert.IsFalse(queue.Move("a", -1));
        Assert.IsFalse(queue.Move("a", 2));
        Assert.IsFalse(queue.Move("a", 0));
        Assert.IsFalse(queue.Remove("unknown"));
        Assert.IsFalse(queue.Add(Item("a")));
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void AutoPlaySetting_DoesNotConsumeOrReorderManualNextSelection()
    {
        var queue = CreateQueue();
        queue.Add(Item("a"));
        queue.SetAutoPlayEnabled(false);
        Assert.IsTrue(queue.TryGetNext(out var candidate));
        Assert.AreEqual("a", candidate!.ItemId);
        Assert.AreEqual(1, queue.Snapshot.Pending.Count);
    }

    [TestMethod]
    public void ConcurrentAdds_DoNotLoseOrDuplicateItems()
    {
        var queue = CreateQueue();
        Parallel.For(0, 100, index => queue.Add(Item((index % 20).ToString())));
        Assert.AreEqual(20, queue.Snapshot.Pending.Count);
        Assert.AreEqual(20, queue.Snapshot.Pending.Select(item => item.ItemId).Distinct().Count());
    }

    private static InMemoryPlaybackQueueService CreateQueue()
    {
        var queue = new InMemoryPlaybackQueueService();
        queue.SetSession("http://media.local:8096/emby", "user-1");
        return queue;
    }

    private static PlaybackQueueItem Item(string id) => new(id, id, "Movie");
}
