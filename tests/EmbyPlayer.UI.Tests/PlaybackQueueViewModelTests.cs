using System.Windows.Threading;
using EmbyPlayer.Core.PlaybackQueue;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class PlaybackQueueViewModelTests
{
    [TestMethod]
    public void ExternalTransition_DisablesQueueActionsWithoutCancelingOwnPlayback() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        queue.Add(new("next", "Next", "Movie"));
        queue.Add(new("last", "Last", "Movie"));
        var completion = new TaskCompletionSource<string?>();
        CancellationToken captured = default;
        using var vm = new PlaybackQueueViewModel(queue, (_, token) => { captured = token; return completion.Task; });
        var row = vm.PendingItems[0];
        vm.SetTransitionActive(true);
        Assert.IsFalse(vm.IsBusy);
        Assert.IsFalse(vm.CanEdit);
        Assert.IsFalse(vm.PlayCommand.CanExecute(row));
        vm.RemoveCommand.Execute(row);
        vm.MoveTo(row, 1);
        vm.ClearPendingCommand.Execute(null);
        vm.AutoPlayEnabled = false;
        Assert.AreEqual("next", queue.Snapshot.Pending[0].ItemId);
        Assert.AreEqual(2, queue.Snapshot.Pending.Count);
        Assert.IsTrue(queue.Snapshot.AutoPlayEnabled);
        vm.SetTransitionActive(false);
        var play = vm.PlayAsync(row.Item);
        vm.SetTransitionActive(true);
        Assert.IsTrue(vm.IsBusy);
        Assert.IsFalse(captured.IsCancellationRequested);
        completion.SetResult(null);
        await play;
        Assert.IsFalse(vm.IsBusy);
        Assert.IsFalse(vm.CanEdit);
        vm.SetTransitionActive(false);
        Assert.IsTrue(vm.CanEdit);
    });

    [TestMethod]
    public void FailedPreparation_KeepsPendingAndCurrentAndDisablesEditsWhileBusy() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        queue.SetCurrent(new("playing", "Current", "Movie"), queue.Snapshot.SessionVersion);
        queue.Add(new("next", "Next", "Episode"));
        var completion = new TaskCompletionSource<string?>();
        using var vm = new PlaybackQueueViewModel(queue, (_, _) => completion.Task);
        var row = vm.PendingItems.Single();
        var play = vm.PlayAsync(row.Item);
        Assert.IsTrue(vm.IsBusy);
        Assert.IsFalse(vm.CanEdit);
        Assert.IsFalse(vm.RemoveCommand.CanExecute(row));
        vm.RemoveCommand.Execute(row);
        vm.ClearPendingCommand.Execute(null);
        vm.MoveTo(row, 0);
        Assert.AreEqual(1, queue.Snapshot.Pending.Count);
        completion.SetResult("Could not prepare playback");
        await play;
        Assert.IsFalse(vm.IsBusy);
        Assert.AreEqual("Could not prepare playback", vm.ErrorMessage);
        Assert.AreEqual("playing", queue.Snapshot.Current!.ItemId);
        Assert.AreEqual("next", queue.Snapshot.Pending.Single().ItemId);
    });

    [TestMethod]
    public void SuccessfulPreparation_IsConsumedOnlyByThePlaybackOwner() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        queue.Add(new("next", "Next", "Movie"));
        var completion = new TaskCompletionSource<string?>();
        using var vm = new PlaybackQueueViewModel(queue, async (item, _) =>
        {
            await completion.Task;
            queue.SetCurrent(item, queue.Snapshot.SessionVersion);
            return null;
        });
        var play = vm.PlayAsync(vm.PendingItems.Single().Item);
        Assert.IsNull(queue.Snapshot.Current);
        Assert.AreEqual(1, queue.Snapshot.Pending.Count);
        completion.SetResult(null);
        await play;
        Assert.IsFalse(vm.HasError);
        Assert.AreEqual("next", vm.CurrentItem!.ItemId);
        Assert.IsFalse(vm.HasPending);
    });

    [TestMethod]
    public void Deactivate_CancelsPreparationAndIgnoresLateResultWithoutClearingQueue() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        queue.Add(new("next", "Next", "Movie"));
        var first = new TaskCompletionSource<string?>();
        var calls = 0;
        CancellationToken captured = default;
        using var vm = new PlaybackQueueViewModel(queue, (_, token) =>
        {
            captured = token;
            return ++calls == 1 ? first.Task : Task.FromResult<string?>(null);
        });
        var play = vm.PlayAsync(vm.PendingItems.Single().Item);
        vm.Deactivate();
        Assert.IsTrue(captured.IsCancellationRequested);
        Assert.IsFalse(vm.IsBusy);
        Assert.AreEqual(1, vm.PendingItems.Count);
        await vm.PlayAsync(vm.PendingItems.Single().Item);
        first.SetResult("late error");
        await play;
        Assert.IsFalse(vm.HasError);
        Assert.IsFalse(vm.IsBusy);
    });

    [TestMethod]
    public void SessionSwitch_CancelsAndRejectsStaleRowCommands() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        queue.Add(new("same-id", "Old account item", "Movie"));
        var completion = new TaskCompletionSource<string?>();
        CancellationToken captured = default;
        using var vm = new PlaybackQueueViewModel(queue, (_, token) => { captured = token; return completion.Task; });
        var oldRow = vm.PendingItems.Single();
        var play = vm.PlayAsync(oldRow.Item);
        queue.SetSession("http://media.local", "other-user");
        queue.Add(new("same-id", "New account item", "Movie"));
        Assert.IsTrue(captured.IsCancellationRequested);
        Assert.IsFalse(vm.RemoveCommand.CanExecute(oldRow));
        Assert.IsFalse(vm.PlayCommand.CanExecute(oldRow));
        vm.RemoveCommand.Execute(oldRow);
        completion.SetResult("old account error");
        await play;
        Assert.IsFalse(vm.HasError);
        Assert.AreEqual("New account item", vm.PendingItems.Single().Title);
    });

    [TestMethod]
    public void BackgroundServiceNotification_IsAppliedOnTheViewDispatcher() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        using var vm = new PlaybackQueueViewModel(queue, (_, _) => Task.FromResult<string?>(null));
        var uiThread = Environment.CurrentManagedThreadId;
        var wrongThread = false;
        vm.PropertyChanged += (_, _) => wrongThread |= Environment.CurrentManagedThreadId != uiThread;
        await Task.Run(() => queue.Add(new("next", "Next", "Movie")));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(1, vm.PendingItems.Count);
        Assert.IsFalse(wrongThread);
    });

    [TestMethod]
    public void Back_CancelsThenNavigatesWithoutRemovingItems() => PlaybackQueueTestHost.Run(async () =>
    {
        var queue = PlaybackQueueTestHost.CreateQueue();
        queue.Add(new("next", "Next", "Movie"));
        var completion = new TaskCompletionSource<string?>();
        CancellationToken captured = default;
        var navigated = false;
        using var vm = new PlaybackQueueViewModel(queue, (_, token) => { captured = token; return completion.Task; },
            () => { Assert.IsTrue(captured.IsCancellationRequested); navigated = true; });
        var play = vm.PlayAsync(vm.PendingItems.Single().Item);
        vm.BackCommand.Execute(null);
        Assert.IsTrue(navigated);
        completion.SetCanceled(captured);
        await play;
        Assert.AreEqual(1, queue.Snapshot.Pending.Count);
    });
}

internal static class PlaybackQueueTestHost
{
    public static InMemoryPlaybackQueueService CreateQueue()
    {
        var queue = new InMemoryPlaybackQueueService();
        queue.SetSession("http://media.local", "user-1");
        return queue;
    }

    public static void Run(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception exception) { error = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)), "Queue interaction did not finish within its test deadline.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
