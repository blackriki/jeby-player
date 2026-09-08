using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class AccountSessionServiceTests
{
    [TestMethod]
    public async Task LogoutAsync_ClearsAuthenticationBeforeRuntimeSessionAndPreservesServer()
    {
        var calls = new List<string>();
        var authStore = new RecordingAuthSessionStore(calls);
        var currentSession = new RecordingCurrentSessionService(calls);
        var settings = new RecordingAppSettingsService(calls);
        var service = new AccountSessionService(authStore, currentSession, settings);

        await service.LogoutAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "authentication", "runtime" }, calls);
        Assert.IsNull(currentSession.CurrentSession);
        Assert.AreEqual(0, settings.ClearLastServerBaseCallCount);
    }

    [TestMethod]
    public async Task SwitchServerAsync_ClearsAuthenticationRuntimeAndServerInStrictOrder()
    {
        var calls = new List<string>();
        var authStore = new RecordingAuthSessionStore(calls);
        var currentSession = new RecordingCurrentSessionService(calls);
        var settings = new RecordingAppSettingsService(calls);
        var service = new AccountSessionService(authStore, currentSession, settings);

        await service.SwitchServerAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "authentication", "runtime", "server" }, calls);
        Assert.IsNull(currentSession.CurrentSession);
        Assert.AreEqual(1, settings.ClearLastServerBaseCallCount);
    }

    [TestMethod]
    public async Task SwitchServerAsync_AuthenticationFailure_DoesNotClearRuntimeOrServer()
    {
        var calls = new List<string>();
        var authStore = new RecordingAuthSessionStore(calls)
        {
            ClearAsyncHandler = _ => throw new IOException("credential failure")
        };
        var currentSession = new RecordingCurrentSessionService(calls);
        var settings = new RecordingAppSettingsService(calls);
        var service = new AccountSessionService(authStore, currentSession, settings);

        await Assert.ThrowsExceptionAsync<IOException>(
            () => service.SwitchServerAsync(CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "authentication" }, calls);
        Assert.IsNotNull(currentSession.CurrentSession);
        Assert.AreEqual(0, settings.ClearLastServerBaseCallCount);
    }

    [TestMethod]
    public async Task LogoutAsync_PartialAuthenticationCleanup_ClearsRuntimeAndPropagatesTypedFailure()
    {
        var calls = new List<string>();
        var authStore = new RecordingAuthSessionStore(calls)
        {
            ClearAsyncHandler = _ => throw new AuthSessionClearPartialFailureException(
                new IOException("metadata failure"))
        };
        var currentSession = new RecordingCurrentSessionService(calls);
        var settings = new RecordingAppSettingsService(calls);
        var service = new AccountSessionService(authStore, currentSession, settings);

        await Assert.ThrowsExceptionAsync<AuthSessionClearPartialFailureException>(
            () => service.LogoutAsync(CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "authentication", "runtime" }, calls);
        Assert.IsNull(currentSession.CurrentSession);
        Assert.AreEqual(0, settings.ClearLastServerBaseCallCount);
    }

    [TestMethod]
    public async Task SwitchServerAsync_PartialAuthenticationCleanup_ClearsRuntimeButKeepsServer()
    {
        var calls = new List<string>();
        var authStore = new RecordingAuthSessionStore(calls)
        {
            ClearAsyncHandler = _ => throw new AuthSessionClearPartialFailureException(
                new IOException("metadata failure"))
        };
        var currentSession = new RecordingCurrentSessionService(calls);
        var settings = new RecordingAppSettingsService(calls);
        var service = new AccountSessionService(authStore, currentSession, settings);

        await Assert.ThrowsExceptionAsync<AuthSessionClearPartialFailureException>(
            () => service.SwitchServerAsync(CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "authentication", "runtime" }, calls);
        Assert.IsNull(currentSession.CurrentSession);
        Assert.AreEqual(0, settings.ClearLastServerBaseCallCount);
    }

    [TestMethod]
    public async Task SwitchServerAsync_ServerClearFailure_LeavesAuthenticationAndRuntimeCleared()
    {
        var calls = new List<string>();
        var authStore = new RecordingAuthSessionStore(calls);
        var currentSession = new RecordingCurrentSessionService(calls);
        var settings = new RecordingAppSettingsService(calls)
        {
            ClearLastServerBaseAsyncHandler = _ => throw new IOException("settings failure")
        };
        var service = new AccountSessionService(authStore, currentSession, settings);

        var exception = await Assert.ThrowsExceptionAsync<SwitchServerPartialFailureException>(
            () => service.SwitchServerAsync(CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "authentication", "runtime", "server" }, calls);
        Assert.IsNull(currentSession.CurrentSession);
        Assert.IsInstanceOfType<IOException>(exception.InnerException);
    }

    private static AuthSession CreateSession() => new(
        "http://media.local:8096",
        "test-token",
        "user-1",
        "Test User",
        "server-1");

    private sealed class RecordingAuthSessionStore(List<string> calls) : IAuthSessionStore
    {
        public Func<CancellationToken, Task>? ClearAsyncHandler { get; init; }

        public Task SaveAsync(AuthSession session, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuthSession?> LoadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task ClearAsync(CancellationToken cancellationToken)
        {
            calls.Add("authentication");
            if (ClearAsyncHandler is not null)
            {
                await ClearAsyncHandler(cancellationToken);
            }
        }
    }

    private sealed class RecordingCurrentSessionService(List<string> calls) : ICurrentSessionService
    {
        public AuthSession? CurrentSession { get; private set; } = CreateSession();

        public void SetSession(AuthSession session)
        {
            CurrentSession = session;
        }

        public void ClearSession()
        {
            calls.Add("runtime");
            CurrentSession = null;
        }
    }

    private sealed class RecordingAppSettingsService(List<string> calls) : IAppSettingsService
    {
        public Func<CancellationToken, Task>? ClearLastServerBaseAsyncHandler { get; init; }

        public int ClearLastServerBaseCallCount { get; private set; }

        public async Task ClearLastServerBaseAsync(CancellationToken cancellationToken)
        {
            ClearLastServerBaseCallCount++;
            calls.Add("server");
            if (ClearLastServerBaseAsyncHandler is not null)
            {
                await ClearLastServerBaseAsyncHandler(cancellationToken);
            }
        }

        public Task<string?> GetLastServerBaseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveLastServerBaseAsync(string serverBase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetRecentServerBasesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveRecentServerBaseAsync(string serverBase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetDeviceIdAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveDeviceIdAsync(string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerPreferences> GetPlayerPreferencesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SavePlayerPreferencesAsync(PlayerPreferences preferences, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerPreferences> UpdatePlayerPreferencesAsync(Func<PlayerPreferences, PlayerPreferences> update, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetSearchHistoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSearchHistoryAsync(IReadOnlyList<string> searchHistory, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthSessionMetadata?> GetAuthSessionMetadataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAuthSessionMetadataAsync(AuthSessionMetadata metadata, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearAuthSessionMetadataAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
