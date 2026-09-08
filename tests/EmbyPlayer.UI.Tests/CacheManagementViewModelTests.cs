using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Caching;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class CacheManagementViewModelTests
{
    [TestMethod]
    public async Task UsageAndClearImages_RequireConfirmationAndDoNotChangeOtherData()
    {
        var context = new Context();
        await context.ViewModel.RefreshUsageAsync();
        Assert.AreEqual("内存占用 2.0 KB", context.ViewModel.ImageUsageText);
        Assert.AreEqual("磁盘占用 4.0 KB", context.ViewModel.SearchIndexUsageText);
        context.ViewModel.ClearImagesCommand.Execute(null);
        Assert.IsTrue(context.ViewModel.IsConfirmationOpen);
        Assert.AreEqual(0, context.Service.ImageClears);
        Assert.IsFalse(context.ViewModel.RefreshUsageCommand.CanExecute(null));
        context.ViewModel.CancelConfirmationCommand.Execute(null);
        Assert.AreEqual(0, context.Service.ImageClears);

        context.ViewModel.ClearImagesCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmCommand).ExecuteAsync();
        Assert.AreEqual(1, context.Service.ImageClears);
        Assert.AreEqual(0, context.Service.IndexClears);
        Assert.AreEqual(0, context.Store.ClearCallCount);
        Assert.AreEqual("内存占用 0 B", context.ViewModel.ImageUsageText);
        Assert.AreEqual("磁盘占用 4.0 KB", context.ViewModel.SearchIndexUsageText);
        Assert.IsTrue(context.ViewModel.HasStatus);
    }

    [TestMethod]
    public async Task ClearSearchIndex_LeavesImagesAndCanRebuildCurrentAccount()
    {
        var context = new Context();
        context.ViewModel.ClearSearchIndexCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmCommand).ExecuteAsync();
        Assert.AreEqual(1, context.Service.IndexClears);
        Assert.AreEqual(0, context.Service.ImageClears);
        Assert.AreEqual("磁盘占用 0 B", context.ViewModel.SearchIndexUsageText);
        context.ViewModel.RebuildSearchIndexCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmCommand).ExecuteAsync();
        Assert.AreSame(context.Sessions.CurrentSession, context.Service.RebuiltSession);
        Assert.AreEqual("磁盘占用 1.0 KB", context.ViewModel.SearchIndexUsageText);
    }

    [TestMethod]
    public async Task UsageFailure_HasRetryAndDisablesMaintenanceUntilRequestFinishes()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<CacheUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Service.Read = _ => pending.Task;
        var load = context.ViewModel.RefreshUsageAsync();
        Assert.IsTrue(context.ViewModel.IsBusy);
        Assert.IsFalse(context.ViewModel.ClearImagesCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.ClearSearchIndexCommand.CanExecute(null));
        Assert.IsFalse(context.ViewModel.RebuildSearchIndexCommand.CanExecute(null));
        pending.SetException(Failure(CacheOperationError.StorageUnavailable));
        await load;
        Assert.IsTrue(context.ViewModel.HasError);
        Assert.IsFalse(context.ViewModel.IsBusy);
        context.Service.Read = _ => Task.FromResult(new CacheUsage(0, 0));
        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.AreEqual("内存占用 0 B", context.ViewModel.ImageUsageText);
    }

    [TestMethod]
    public async Task FailedRebuild_RetryRepeatsTheConfirmedActionAndPreservesSession()
    {
        var context = new Context();
        var calls = 0;
        context.Service.Rebuild = (_, _) => ++calls == 1 ? Task.FromException(Failure(CacheOperationError.Forbidden)) : Task.CompletedTask;
        context.ViewModel.RebuildSearchIndexCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmCommand).ExecuteAsync();
        Assert.IsTrue(context.ViewModel.HasError);
        Assert.IsNotNull(context.Sessions.CurrentSession);
        Assert.AreEqual(0, context.Store.ClearCallCount);
        await ((AsyncRelayCommand)context.ViewModel.RetryCommand).ExecuteAsync();
        Assert.AreEqual(2, calls);
        Assert.IsFalse(context.ViewModel.HasError);
        Assert.IsTrue(context.ViewModel.HasStatus);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancelOrSessionReplacement_RejectsLateUnauthorized(bool cancel)
    {
        var context = new Context();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        context.Service.Rebuild = (_, token) => { requestToken = token; return pending.Task; };
        context.ViewModel.RebuildSearchIndexCommand.Execute(null);
        var rebuild = ((AsyncRelayCommand)context.ViewModel.ConfirmCommand).ExecuteAsync();
        if (cancel) context.ViewModel.Cancel();
        else context.Sessions.SetSession(new AuthSession(Session.ServerBase, "replacement-token", "other-user", "Other User", Session.ServerId));
        pending.SetException(Failure(CacheOperationError.Unauthorized));
        await rebuild;
        Assert.AreEqual(cancel, requestToken.IsCancellationRequested);
        Assert.IsNotNull(context.Sessions.CurrentSession);
        Assert.AreEqual(0, context.Store.ClearCallCount);
        Assert.AreEqual(0, context.ExpiredCount);
        Assert.IsFalse(context.ViewModel.IsBusy);
    }

    [TestMethod]
    public async Task UnauthorizedRebuild_ClearsRuntimeAndNavigatesEvenWhenPersistentCleanupFails()
    {
        var context = new Context();
        context.Store.ClearAsyncHandler = _ => throw new IOException("disk unavailable");
        context.Service.Rebuild = (_, _) => Task.FromException(Failure(CacheOperationError.Unauthorized));
        context.ViewModel.RebuildSearchIndexCommand.Execute(null);
        await ((AsyncRelayCommand)context.ViewModel.ConfirmCommand).ExecuteAsync();
        Assert.IsNull(context.Sessions.CurrentSession);
        Assert.AreEqual(1, context.Store.ClearCallCount);
        Assert.AreEqual(1, context.ExpiredCount);
    }

    [TestMethod]
    public async Task Cancel_RejectsLateUsageAndNextLoadCanRefresh()
    {
        var context = new Context();
        var pending = new TaskCompletionSource<CacheUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Service.Read = _ => pending.Task;
        var load = context.ViewModel.RefreshUsageAsync();
        context.ViewModel.Cancel();
        pending.SetResult(new CacheUsage(9999, 9999));
        await load;
        Assert.AreEqual("尚未读取", context.ViewModel.ImageUsageText);
        context.Service.Read = _ => Task.FromResult(new CacheUsage(8, 16));
        await context.ViewModel.RefreshUsageAsync();
        Assert.AreEqual("内存占用 8 B", context.ViewModel.ImageUsageText);
    }

    internal static readonly AuthSession Session = new("http://media.local", "test-token", "user", "User", "server");
    private static CacheOperationException Failure(CacheOperationError error) => new(error, new IOException("test failure"));

    private sealed class Context
    {
        public Context()
        {
            Sessions.SetSession(Session);
            ViewModel = new CacheManagementViewModel(Service, Sessions, Store, () => ExpiredCount++);
        }
        public CurrentSessionService Sessions { get; } = new();
        public TestAuthSessionStore Store { get; } = new();
        public CacheService Service { get; } = new();
        public CacheManagementViewModel ViewModel { get; }
        public int ExpiredCount { get; private set; }
    }

    internal sealed class CacheService : ICacheManagementService
    {
        private CacheUsage usage = new(2048, 4096);
        public Func<CancellationToken, Task<CacheUsage>>? Read { get; set; }
        public Func<AuthSession, CancellationToken, Task>? Rebuild { get; set; }
        public int ImageClears { get; private set; }
        public int IndexClears { get; private set; }
        public AuthSession? RebuiltSession { get; private set; }
        public Task<CacheUsage> GetUsageAsync(CancellationToken cancellationToken) => Read?.Invoke(cancellationToken) ?? Task.FromResult(usage);
        public Task ClearImagesAsync(CancellationToken cancellationToken) { ImageClears++; usage = usage with { ImageMemoryBytes = 0 }; return Task.CompletedTask; }
        public Task ClearSearchIndexAsync(CancellationToken cancellationToken) { IndexClears++; usage = usage with { SearchIndexDiskBytes = 0 }; return Task.CompletedTask; }
        public Task RebuildSearchIndexAsync(AuthSession session, CancellationToken cancellationToken)
        {
            RebuiltSession = session;
            usage = usage with { SearchIndexDiskBytes = 1024 };
            return Rebuild?.Invoke(session, cancellationToken) ?? Task.CompletedTask;
        }
    }
}
