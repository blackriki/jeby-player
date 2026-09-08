using System.Security.Cryptography;
using System.Text;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.Core.Tests;

[TestClass]
public sealed class AuthSessionStoreTests
{
    [TestMethod]
    public void CredentialTarget_UsesServerBaseHashAndUserId()
    {
        const string serverBase = "http://media.local:8096";
        const string userId = "user-1";
        const string accessToken = "test-access-token";

        var target = AuthSessionCredentialTarget.Create(serverBase, userId);
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(serverBase)))
            .ToLowerInvariant();

        Assert.AreEqual($"EmbyPlayer.AuthSession.{expectedHash}.{userId}", target);
        Assert.IsFalse(target.Contains(serverBase, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(target.Contains(accessToken, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SaveAsync_SavesMetadataAndTokenUsingCredentialTarget()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var tokenStore = new TestAuthSessionTokenStore();
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);
        var session = CreateSession();

        await sessionStore.SaveAsync(session, CancellationToken.None);

        var metadata = await settingsService.GetAuthSessionMetadataAsync(CancellationToken.None);
        Assert.IsNotNull(metadata);
        Assert.AreEqual(session.ServerBase, metadata.ServerBase);
        Assert.AreEqual(session.UserId, metadata.UserId);
        Assert.AreEqual(session.UserName, metadata.UserName);
        Assert.AreEqual(session.ServerId, metadata.ServerId);
        Assert.AreEqual(1, tokenStore.SaveCallCount);
        Assert.AreEqual(AuthSessionCredentialTarget.Create(session.ServerBase, session.UserId), tokenStore.LastSavedTarget);
        Assert.AreEqual(session.AccessToken, tokenStore.LastSavedToken);
    }

    [TestMethod]
    public async Task SaveAsync_DoesNotWriteAccessTokenToSettings()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var tokenStore = new TestAuthSessionTokenStore();
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);

        await sessionStore.SaveAsync(CreateSession(), CancellationToken.None);

        var fileContent = await File.ReadAllTextAsync(settingsFilePath);

        Assert.IsFalse(fileContent.Contains("test-access-token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("AccessToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fileContent.Contains("Pw", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoadAsync_RestoresSessionFromMetadataAndToken()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var tokenStore = new TestAuthSessionTokenStore();
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);
        var session = CreateSession();

        await sessionStore.SaveAsync(session, CancellationToken.None);
        var loadedSession = await sessionStore.LoadAsync(CancellationToken.None);

        Assert.IsNotNull(loadedSession);
        Assert.AreEqual(session.ServerBase, loadedSession.ServerBase);
        Assert.AreEqual(session.AccessToken, loadedSession.AccessToken);
        Assert.AreEqual(session.UserId, loadedSession.UserId);
        Assert.AreEqual(session.UserName, loadedSession.UserName);
        Assert.AreEqual(session.ServerId, loadedSession.ServerId);
    }

    [TestMethod]
    public async Task LoadAsync_MetadataWithoutToken_ClearsMetadataAndReturnsNull()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var tokenStore = new TestAuthSessionTokenStore();
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);

        await settingsService.SaveDeviceIdAsync("stable-device-id", CancellationToken.None);
        await settingsService.SaveAuthSessionMetadataAsync(
            new AuthSessionMetadata(
                "http://media.local:8096",
                "user-1",
                "Test User",
                "server-1"),
            CancellationToken.None);

        var loadedSession = await sessionStore.LoadAsync(CancellationToken.None);

        Assert.IsNull(loadedSession);
        Assert.IsNull(await settingsService.GetAuthSessionMetadataAsync(CancellationToken.None));
        Assert.AreEqual("http://media.local:8096", await settingsService.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual("stable-device-id", await settingsService.GetDeviceIdAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task LoadAsync_MetadataWithoutTokenAndMetadataCleanupFails_ThrowsTypedPartialFailure()
    {
        var settingsService = new RecordingAuthSettingsService
        {
            ClearAuthSessionMetadataAsyncHandler = _ => throw new IOException("metadata failure")
        };
        var sessionStore = new AuthSessionStore(settingsService, new TestAuthSessionTokenStore());

        var exception = await Assert.ThrowsExceptionAsync<AuthSessionClearPartialFailureException>(
            () => sessionStore.LoadAsync(CancellationToken.None));

        Assert.IsInstanceOfType<IOException>(exception.InnerException);
        Assert.AreEqual(1, settingsService.ClearAuthSessionMetadataCallCount);
    }

    [TestMethod]
    public async Task SaveAsync_MetadataAndTokenRollbackBothFail_PreservesBothExceptions()
    {
        var metadataException = new IOException("metadata failure");
        var rollbackException = new UnauthorizedAccessException("credential rollback failure");
        var settingsService = new RecordingAuthSettingsService
        {
            SaveAuthSessionMetadataAsyncHandler = (_, _) => throw metadataException
        };
        var tokenStore = new TestAuthSessionTokenStore
        {
            DeleteTokenAsyncHandler = (_, _) => throw rollbackException
        };
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);

        var exception = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => sessionStore.SaveAsync(CreateSession(), CancellationToken.None));

        CollectionAssert.AreEqual(
            new Exception[] { metadataException, rollbackException },
            exception.InnerExceptions.ToArray());
    }

    [TestMethod]
    public async Task ClearAsync_RemovesTokenAndMetadataButKeepsLastServerBaseAndDeviceId()
    {
        var settingsFilePath = CreateSettingsFilePath();
        var settingsService = new FileAppSettingsService(settingsFilePath);
        var tokenStore = new TestAuthSessionTokenStore();
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);
        var session = CreateSession();
        await settingsService.SaveLastServerBaseAsync(session.ServerBase, CancellationToken.None);
        await settingsService.SaveDeviceIdAsync("stable-device-id", CancellationToken.None);
        await sessionStore.SaveAsync(session, CancellationToken.None);

        await sessionStore.ClearAsync(CancellationToken.None);

        Assert.AreEqual(1, tokenStore.DeleteCallCount);
        Assert.AreEqual(AuthSessionCredentialTarget.Create(session.ServerBase, session.UserId), tokenStore.LastDeletedTarget);
        Assert.IsNull(await settingsService.GetAuthSessionMetadataAsync(CancellationToken.None));
        Assert.AreEqual(session.ServerBase, await settingsService.GetLastServerBaseAsync(CancellationToken.None));
        Assert.AreEqual("stable-device-id", await settingsService.GetDeviceIdAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ClearAsync_TokenDeletedButMetadataClearFails_ThrowsTypedPartialFailure()
    {
        var settingsService = new RecordingAuthSettingsService
        {
            ClearAuthSessionMetadataAsyncHandler = _ => throw new IOException("metadata failure")
        };
        var tokenStore = new TestAuthSessionTokenStore();
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);

        var exception = await Assert.ThrowsExceptionAsync<AuthSessionClearPartialFailureException>(
            () => sessionStore.ClearAsync(CancellationToken.None));

        Assert.AreEqual(1, tokenStore.DeleteCallCount);
        Assert.AreEqual(1, settingsService.ClearAuthSessionMetadataCallCount);
        Assert.IsInstanceOfType<IOException>(exception.InnerException);
    }

    [TestMethod]
    public async Task ClearAsync_TokenDeleteFails_DoesNotReportPartialCleanup()
    {
        var settingsService = new RecordingAuthSettingsService();
        var tokenStore = new TestAuthSessionTokenStore
        {
            DeleteTokenAsyncHandler = (_, _) => throw new IOException("credential failure")
        };
        var sessionStore = new AuthSessionStore(settingsService, tokenStore);

        await Assert.ThrowsExceptionAsync<IOException>(
            () => sessionStore.ClearAsync(CancellationToken.None));

        Assert.AreEqual(1, tokenStore.DeleteCallCount);
        Assert.AreEqual(0, settingsService.ClearAuthSessionMetadataCallCount);
    }

    private static AuthSession CreateSession()
    {
        return new AuthSession(
            "http://media.local:8096",
            "test-access-token",
            "user-1",
            "Test User",
            "server-1");
    }

    private static string CreateSettingsFilePath()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "EmbyPlayer.Tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directoryPath, "settings.json");
    }

    private sealed class TestAuthSessionTokenStore : IAuthSessionTokenStore
    {
        private readonly Dictionary<string, string> tokens = new();

        public Func<string, CancellationToken, Task>? DeleteTokenAsyncHandler { get; init; }

        public int SaveCallCount { get; private set; }

        public int DeleteCallCount { get; private set; }

        public string? LastSavedTarget { get; private set; }

        public string? LastSavedToken { get; private set; }

        public string? LastDeletedTarget { get; private set; }

        public Task SaveTokenAsync(
            string credentialTarget,
            string accessToken,
            CancellationToken cancellationToken)
        {
            SaveCallCount++;
            LastSavedTarget = credentialTarget;
            LastSavedToken = accessToken;
            tokens[credentialTarget] = accessToken;
            return Task.CompletedTask;
        }

        public Task<string?> LoadTokenAsync(
            string credentialTarget,
            CancellationToken cancellationToken)
        {
            tokens.TryGetValue(credentialTarget, out var token);
            return Task.FromResult<string?>(token);
        }

        public async Task DeleteTokenAsync(
            string credentialTarget,
            CancellationToken cancellationToken)
        {
            DeleteCallCount++;
            LastDeletedTarget = credentialTarget;
            if (DeleteTokenAsyncHandler is not null)
            {
                await DeleteTokenAsyncHandler(credentialTarget, cancellationToken);
            }

            tokens.Remove(credentialTarget);
        }
    }

    private sealed class RecordingAuthSettingsService : IAppSettingsService
    {
        public Func<CancellationToken, Task>? ClearAuthSessionMetadataAsyncHandler { get; init; }

        public Func<AuthSessionMetadata, CancellationToken, Task>? SaveAuthSessionMetadataAsyncHandler { get; init; }

        public int ClearAuthSessionMetadataCallCount { get; private set; }

        public Task<AuthSessionMetadata?> GetAuthSessionMetadataAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<AuthSessionMetadata?>(new AuthSessionMetadata(
                "http://media.local:8096",
                "user-1",
                "Test User",
                "server-1"));
        }

        public async Task ClearAuthSessionMetadataAsync(CancellationToken cancellationToken)
        {
            ClearAuthSessionMetadataCallCount++;
            if (ClearAuthSessionMetadataAsyncHandler is not null)
            {
                await ClearAuthSessionMetadataAsyncHandler(cancellationToken);
            }
        }

        public Task<string?> GetLastServerBaseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveLastServerBaseAsync(string serverBase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearLastServerBaseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetRecentServerBasesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveRecentServerBaseAsync(string serverBase, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetDeviceIdAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveDeviceIdAsync(string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerPreferences> GetPlayerPreferencesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SavePlayerPreferencesAsync(PlayerPreferences preferences, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PlayerPreferences> UpdatePlayerPreferencesAsync(Func<PlayerPreferences, PlayerPreferences> update, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetSearchHistoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSearchHistoryAsync(IReadOnlyList<string> searchHistory, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAuthSessionMetadataAsync(
            AuthSessionMetadata metadata,
            CancellationToken cancellationToken)
        {
            return SaveAuthSessionMetadataAsyncHandler?.Invoke(metadata, cancellationToken)
                ?? Task.CompletedTask;
        }
    }
}
