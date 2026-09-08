using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Home;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Search;
using EmbyPlayer.Core.Series;
using EmbyPlayer.Core.Servers;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Core.UserData;
using EmbyPlayer.Player;
using EmbyPlayer.UI.ViewModels;

namespace EmbyPlayer.UI.Tests;

internal sealed class TestServerConnectionService : IServerConnectionService
{
    public Func<string, CancellationToken, Task<ServerConnectionResult>>? ConnectAsyncHandler { get; set; }

    public int ConnectCallCount { get; private set; }

    public string? LastServerUrl { get; private set; }

    public Task<ServerConnectionResult> ConnectAsync(string serverUrl, CancellationToken cancellationToken)
    {
        ConnectCallCount++;
        LastServerUrl = serverUrl;

        return ConnectAsyncHandler?.Invoke(serverUrl, cancellationToken)
            ?? Task.FromResult(ServerConnectionResult.Failure(ServerConnectionError.ServerUnreachable));
    }
}

internal sealed class TestAppSettingsService : IAppSettingsService
{
    public string? LastServerBase { get; set; }

    public IReadOnlyList<string> RecentServerBases { get; set; } = Array.Empty<string>();

    public Func<CancellationToken, Task<IReadOnlyList<string>>>? GetRecentServerBasesAsyncHandler { get; set; }

    public Func<string, CancellationToken, Task>? RemoveRecentServerBaseAsyncHandler { get; set; }

    public int GetRecentServerBasesCallCount { get; private set; }

    public int RemoveRecentServerBaseCallCount { get; private set; }

    public string? LastRemovedServerBase { get; private set; }

    public string? DeviceId { get; set; }

    public AuthSessionMetadata? AuthSessionMetadata { get; set; }

    public PlayerPreferences PlayerPreferences { get; set; } = PlayerPreferences.Default;

    public IReadOnlyList<string> SearchHistory { get; set; } = Array.Empty<string>();

    public Func<CancellationToken, Task<string?>>? GetLastServerBaseAsyncHandler { get; set; }

    public Func<string, CancellationToken, Task>? SaveLastServerBaseAsyncHandler { get; set; }

    public Func<CancellationToken, Task>? ClearLastServerBaseAsyncHandler { get; set; }

    public Func<CancellationToken, Task<PlayerPreferences>>? GetPlayerPreferencesAsyncHandler { get; set; }

    public Func<PlayerPreferences, CancellationToken, Task>? SavePlayerPreferencesAsyncHandler { get; set; }

    public Func<Func<PlayerPreferences, PlayerPreferences>, CancellationToken, Task<PlayerPreferences>>?
        UpdatePlayerPreferencesAsyncHandler { get; set; }

    public string? SavedLastServerBase { get; private set; }

    public int SaveCallCount { get; private set; }

    public int ClearLastServerBaseCallCount { get; private set; }

    public int SavePlayerPreferencesCallCount { get; private set; }

    public int UpdatePlayerPreferencesCallCount { get; private set; }

    public int GetPlayerPreferencesCallCount { get; private set; }

    public Task<string?> GetLastServerBaseAsync(CancellationToken cancellationToken)
    {
        if (GetLastServerBaseAsyncHandler is not null)
        {
            return GetLastServerBaseAsyncHandler(cancellationToken);
        }

        return Task.FromResult(LastServerBase);
    }

    public async Task SaveLastServerBaseAsync(string serverBase, CancellationToken cancellationToken)
    {
        SaveCallCount++;
        if (SaveLastServerBaseAsyncHandler is not null)
        {
            await SaveLastServerBaseAsyncHandler(serverBase, cancellationToken).ConfigureAwait(false);
        }

        SavedLastServerBase = serverBase;
        LastServerBase = serverBase;
        RecentServerBases = new[] { serverBase }.Concat(RecentServerBases)
            .Distinct(StringComparer.Ordinal).Take(5).ToArray();
    }

    public Task<IReadOnlyList<string>> GetRecentServerBasesAsync(CancellationToken cancellationToken)
    {
        GetRecentServerBasesCallCount++;
        return GetRecentServerBasesAsyncHandler?.Invoke(cancellationToken) ?? Task.FromResult(RecentServerBases);
    }

    public async Task RemoveRecentServerBaseAsync(string serverBase, CancellationToken cancellationToken)
    {
        RemoveRecentServerBaseCallCount++;
        LastRemovedServerBase = serverBase;
        if (RemoveRecentServerBaseAsyncHandler is not null)
        {
            await RemoveRecentServerBaseAsyncHandler(serverBase, cancellationToken).ConfigureAwait(false);
        }
        RecentServerBases = RecentServerBases.Where(item => !string.Equals(item, serverBase, StringComparison.Ordinal)).ToArray();
    }

    public async Task ClearLastServerBaseAsync(CancellationToken cancellationToken)
    {
        ClearLastServerBaseCallCount++;
        if (ClearLastServerBaseAsyncHandler is not null)
        {
            await ClearLastServerBaseAsyncHandler(cancellationToken).ConfigureAwait(false);
        }

        LastServerBase = null;
    }

    public Task<string?> GetDeviceIdAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(DeviceId);
    }

    public Task SaveDeviceIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        DeviceId = deviceId;
        return Task.CompletedTask;
    }

    public Task<PlayerPreferences> GetPlayerPreferencesAsync(CancellationToken cancellationToken)
    {
        GetPlayerPreferencesCallCount++;
        if (GetPlayerPreferencesAsyncHandler is not null)
        {
            return GetPlayerPreferencesAsyncHandler(cancellationToken);
        }

        return Task.FromResult(PlayerPreferences);
    }

    public async Task SavePlayerPreferencesAsync(
        PlayerPreferences preferences,
        CancellationToken cancellationToken)
    {
        SavePlayerPreferencesCallCount++;
        if (SavePlayerPreferencesAsyncHandler is not null)
        {
            await SavePlayerPreferencesAsyncHandler(preferences, cancellationToken).ConfigureAwait(false);
        }

        PlayerPreferences = preferences.Normalize();
    }

    public async Task<PlayerPreferences> UpdatePlayerPreferencesAsync(
        Func<PlayerPreferences, PlayerPreferences> update,
        CancellationToken cancellationToken)
    {
        UpdatePlayerPreferencesCallCount++;
        var updatedPreferences = UpdatePlayerPreferencesAsyncHandler is null
            ? update(PlayerPreferences)
            : await UpdatePlayerPreferencesAsyncHandler(update, cancellationToken).ConfigureAwait(false);
        PlayerPreferences = updatedPreferences.Normalize();
        return PlayerPreferences;
    }

    public Task<IReadOnlyList<string>> GetSearchHistoryAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(SearchHistory);
    }

    public Task SaveSearchHistoryAsync(
        IReadOnlyList<string> searchHistory,
        CancellationToken cancellationToken)
    {
        SearchHistory = searchHistory.ToArray();
        return Task.CompletedTask;
    }

    public Task<AuthSessionMetadata?> GetAuthSessionMetadataAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(AuthSessionMetadata);
    }

    public Task SaveAuthSessionMetadataAsync(
        AuthSessionMetadata metadata,
        CancellationToken cancellationToken)
    {
        AuthSessionMetadata = metadata;
        return Task.CompletedTask;
    }

    public Task ClearAuthSessionMetadataAsync(CancellationToken cancellationToken)
    {
        AuthSessionMetadata = null;
        return Task.CompletedTask;
    }
}

internal sealed class TestMediaLibraryScanService : IMediaLibraryScanService
{
    public Func<AuthSession, CancellationToken, Task<MediaLibraryScanResult>>? RequestScanAsyncHandler { get; set; }

    public int RequestCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public Task<MediaLibraryScanResult> RequestScanAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        RequestCallCount++;
        LastSession = session;
        return RequestScanAsyncHandler?.Invoke(session, cancellationToken)
            ?? Task.FromResult(MediaLibraryScanResult.Success());
    }
}

internal sealed class TestAuthenticationService : IAuthenticationService
{
    public Func<string, string, string, CancellationToken, Task<AuthenticationResult>>? AuthenticateAsyncHandler { get; set; }

    public int AuthenticateCallCount { get; private set; }

    public string? LastServerBase { get; private set; }

    public string? LastUserName { get; private set; }

    public string? LastPassword { get; private set; }

    public Task<AuthenticationResult> AuthenticateAsync(
        string serverBase,
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        AuthenticateCallCount++;
        LastServerBase = serverBase;
        LastUserName = userName;
        LastPassword = password;

        return AuthenticateAsyncHandler?.Invoke(serverBase, userName, password, cancellationToken)
            ?? Task.FromResult(AuthenticationResult.Failure(AuthenticationError.LoginFailed));
    }
}

internal sealed class TestAuthSessionStore : IAuthSessionStore
{
    public Func<AuthSession, CancellationToken, Task>? SaveAsyncHandler { get; set; }

    public Func<CancellationToken, Task<AuthSession?>>? LoadAsyncHandler { get; set; }

    public Func<CancellationToken, Task>? ClearAsyncHandler { get; set; }

    public AuthSession? SavedSession { get; private set; }

    public int SaveCallCount { get; private set; }

    public int LoadCallCount { get; private set; }

    public int ClearCallCount { get; private set; }

    public Task SaveAsync(AuthSession session, CancellationToken cancellationToken)
    {
        SaveCallCount++;
        SavedSession = session;
        return SaveAsyncHandler?.Invoke(session, cancellationToken) ?? Task.CompletedTask;
    }

    public Task<AuthSession?> LoadAsync(CancellationToken cancellationToken)
    {
        LoadCallCount++;
        return LoadAsyncHandler?.Invoke(cancellationToken) ?? Task.FromResult<AuthSession?>(SavedSession);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ClearCallCount++;
        if (ClearAsyncHandler is not null)
        {
            await ClearAsyncHandler(cancellationToken).ConfigureAwait(false);
        }

        SavedSession = null;
    }
}

internal sealed class TestAuthSessionValidator : IAuthSessionValidator
{
    public Func<AuthSession, CancellationToken, Task<AuthSessionValidationResult>>? ValidateAsyncHandler { get; set; }

    public int ValidateCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public Task<AuthSessionValidationResult> ValidateAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        ValidateCallCount++;
        LastSession = session;
        return ValidateAsyncHandler?.Invoke(session, cancellationToken)
            ?? Task.FromResult(AuthSessionValidationResult.Valid());
    }
}

internal sealed class TestHomeService : IHomeService
{
    public Func<AuthSession, CancellationToken, Task<HomeLoadResult>>? LoadHomeAsyncHandler { get; set; }

    public Func<AuthSession, HomeSectionKind, int, int, CancellationToken, Task<HomeSectionItemsLoadResult>>? LoadSectionAsyncHandler { get; set; }

    public int LoadSectionCallCount { get; private set; }

    public AuthSession? LastSectionSession { get; private set; }

    public HomeSectionKind? LastSection { get; private set; }

    public int LastSectionStartIndex { get; private set; }

    public int LastSectionLimit { get; private set; }

    public CancellationToken LastSectionCancellationToken { get; private set; }

    public int LoadCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public Task<HomeSectionItemsLoadResult> LoadSectionAsync(
        AuthSession session,
        HomeSectionKind section,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        LoadSectionCallCount++;
        LastSectionSession = session;
        LastSection = section;
        LastSectionStartIndex = startIndex;
        LastSectionLimit = limit;
        LastSectionCancellationToken = cancellationToken;
        return LoadSectionAsyncHandler?.Invoke(session, section, startIndex, limit, cancellationToken)
            ?? Task.FromResult(HomeSectionItemsLoadResult.Success(Array.Empty<MediaCard>(), startIndex, false));
    }

    public Task<HomeLoadResult> LoadHomeAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        LoadCallCount++;
        LastSession = session;
        return LoadHomeAsyncHandler?.Invoke(session, cancellationToken)
            ?? Task.FromResult(HomeLoadResult.Success(new HomeData(
                session.UserName,
                Array.Empty<MediaCard>(),
                Array.Empty<MediaCard>(),
                Array.Empty<MediaLibrary>())));
    }
}

internal sealed class TestLibraryService : ILibraryService
{
    public Func<AuthSession, CancellationToken, Task<LibraryLoadResult>>? LoadLibrariesAsyncHandler { get; set; }

    public Func<AuthSession, LibraryItem, int, int, CancellationToken, Task<LibraryItemsLoadResult>>? LoadLibraryItemsAsyncHandler { get; set; }

    public Func<AuthSession, LibraryItem, int, int, LibraryQuery, CancellationToken, Task<LibraryItemsLoadResult>>? LoadLibraryQueryAsyncHandler { get; set; }

    public LibraryQuery? LastQuery { get; private set; }

    public int LoadLibrariesCallCount { get; private set; }

    public int LoadLibraryItemsCallCount { get; private set; }

    public AuthSession? LastLibrariesSession { get; private set; }

    public AuthSession? LastItemsSession { get; private set; }

    public LibraryItem? LastLibrary { get; private set; }

    public int LastStartIndex { get; private set; }

    public int LastLimit { get; private set; }

    public Task<LibraryLoadResult> LoadLibrariesAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        LoadLibrariesCallCount++;
        LastLibrariesSession = session;
        return LoadLibrariesAsyncHandler?.Invoke(session, cancellationToken)
            ?? Task.FromResult(LibraryLoadResult.Success(Array.Empty<LibraryItem>()));
    }

    public Task<LibraryItemsLoadResult> LoadLibraryItemsAsync(
        AuthSession session,
        LibraryItem library,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        return LoadLibraryItemsAsync(session, library, startIndex, limit, new LibraryQuery(), cancellationToken);
    }

    public Task<LibraryItemsLoadResult> LoadLibraryItemsAsync(
        AuthSession session,
        LibraryItem library,
        int startIndex,
        int limit,
        LibraryQuery query,
        CancellationToken cancellationToken)
    {
        LoadLibraryItemsCallCount++;
        LastQuery = query;
        LastItemsSession = session;
        LastLibrary = library;
        LastStartIndex = startIndex;
        LastLimit = limit;
        return LoadLibraryQueryAsyncHandler?.Invoke(session, library, startIndex, limit, query, cancellationToken)
            ?? LoadLibraryItemsAsyncHandler?.Invoke(session, library, startIndex, limit, cancellationToken)
            ?? Task.FromResult(LibraryItemsLoadResult.Success(Array.Empty<LibraryMediaItem>()));
    }
}

internal sealed class TestSearchService : ISearchService
{
    public Func<AuthSession, string, CancellationToken, Task<SearchLoadResult>>? SearchAsyncHandler { get; set; }

    public Func<AuthSession, CancellationToken, Task<SearchLoadResult>>? LoadFavoritesAsyncHandler { get; set; }

    public int SearchCallCount { get; private set; }

    public int LoadFavoritesCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public string? LastKeyword { get; private set; }

    public CancellationToken LastSearchCancellationToken { get; private set; }

    public Task<SearchLoadResult> SearchAsync(
        AuthSession session,
        string keyword,
        CancellationToken cancellationToken)
    {
        SearchCallCount++;
        LastSession = session;
        LastKeyword = keyword;
        LastSearchCancellationToken = cancellationToken;
        return SearchAsyncHandler?.Invoke(session, keyword, cancellationToken)
            ?? Task.FromResult(SearchLoadResult.Success(Array.Empty<SearchResultItem>()));
    }

    public Task<SearchLoadResult> LoadFavoritesAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        LoadFavoritesCallCount++;
        LastSession = session;
        return LoadFavoritesAsyncHandler?.Invoke(session, cancellationToken)
            ?? Task.FromResult(SearchLoadResult.Success(Array.Empty<SearchResultItem>()));
    }
}

internal sealed class TestLocalMediaSearchIndex : ILocalMediaSearchIndex
{
    public int PrepareCallCount { get; private set; }

    public int RefreshCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public Task PrepareAsync(AuthSession session, CancellationToken cancellationToken)
    {
        PrepareCallCount++;
        LastSession = session;
        return Task.CompletedTask;
    }

    public Task RefreshAsync(AuthSession session, CancellationToken cancellationToken)
    {
        RefreshCallCount++;
        LastSession = session;
        return Task.CompletedTask;
    }

    public Task<LocalMediaSearchQueryResult> SearchAsync(
        AuthSession session,
        string keyword,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(LocalMediaSearchQueryResult.Empty);
    }
}

internal sealed class TestMediaDetailService : IMediaDetailService
{
    public Func<AuthSession, string, CancellationToken, Task<MediaDetailLoadResult>>? LoadDetailAsyncHandler { get; set; }

    public int LoadCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public string? LastItemId { get; private set; }

    public Task<MediaDetailLoadResult> LoadDetailAsync(
        AuthSession session,
        string itemId,
        CancellationToken cancellationToken)
    {
        LoadCallCount++;
        LastSession = session;
        LastItemId = itemId;
        return LoadDetailAsyncHandler?.Invoke(session, itemId, cancellationToken)
            ?? Task.FromResult(MediaDetailLoadResult.Success(new MediaDetail(
                itemId,
                "Detail Item",
                "Movie",
                2024,
                TimeSpan.FromMinutes(90).Ticks,
                "Overview",
                new[] { "Drama" },
                8.1,
                40,
                TimeSpan.FromMinutes(12).Ticks,
                null,
                null)));
    }
}

internal sealed class TestSimilarMediaService : ISimilarMediaService
{
    public Func<AuthSession, string, CancellationToken, Task<SimilarMediaLoadResult>>? LoadSimilarAsyncHandler { get; set; }

    public int LoadCallCount { get; private set; }

    public string? LastItemId { get; private set; }

    public string? LastItemType { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<SimilarMediaLoadResult> LoadSimilarAsync(
        AuthSession session,
        string itemId,
        string itemType,
        CancellationToken cancellationToken)
    {
        LoadCallCount++;
        LastItemId = itemId;
        LastItemType = itemType;
        LastCancellationToken = cancellationToken;
        return LoadSimilarAsyncHandler?.Invoke(session, itemId, cancellationToken)
            ?? Task.FromResult(SimilarMediaLoadResult.Success(Array.Empty<SimilarMediaItem>()));
    }
}

internal sealed class TestItemUserDataService : IItemUserDataService
{
    public Func<AuthSession, string, bool, CancellationToken, Task<ItemUserDataResult>>? SetFavoriteAsyncHandler { get; set; }

    public Func<AuthSession, string, bool, CancellationToken, Task<ItemUserDataResult>>? SetPlayedAsyncHandler { get; set; }

    public int SetFavoriteCallCount { get; private set; }

    public int SetPlayedCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public string? LastItemId { get; private set; }

    public bool? LastIsFavorite { get; private set; }

    public string? LastPlayedItemId { get; private set; }

    public bool? LastIsPlayed { get; private set; }

    public Task<ItemUserDataResult> SetFavoriteAsync(
        AuthSession session,
        string itemId,
        bool isFavorite,
        CancellationToken cancellationToken)
    {
        SetFavoriteCallCount++;
        LastSession = session;
        LastItemId = itemId;
        LastIsFavorite = isFavorite;

        return SetFavoriteAsyncHandler?.Invoke(session, itemId, isFavorite, cancellationToken)
            ?? Task.FromResult(ItemUserDataResult.Success());
    }

    public Task<ItemUserDataResult> SetPlayedAsync(
        AuthSession session,
        string itemId,
        bool isPlayed,
        CancellationToken cancellationToken)
    {
        SetPlayedCallCount++;
        LastSession = session;
        LastPlayedItemId = itemId;
        LastIsPlayed = isPlayed;

        return SetPlayedAsyncHandler?.Invoke(session, itemId, isPlayed, cancellationToken)
            ?? Task.FromResult(ItemUserDataResult.Success());
    }
}

internal sealed class TestSeriesService : ISeriesService
{
    public Func<AuthSession, string, CancellationToken, Task<SeriesSeasonsLoadResult>>? LoadSeasonsAsyncHandler { get; set; }

    public Func<AuthSession, string, string, CancellationToken, Task<SeriesEpisodesLoadResult>>? LoadEpisodesAsyncHandler { get; set; }

    public int LoadSeasonsCallCount { get; private set; }

    public int LoadEpisodesCallCount { get; private set; }

    public AuthSession? LastSeasonsSession { get; private set; }

    public AuthSession? LastEpisodesSession { get; private set; }

    public string? LastSeriesId { get; private set; }

    public string? LastEpisodesSeriesId { get; private set; }

    public string? LastSeasonId { get; private set; }

    public Task<SeriesSeasonsLoadResult> LoadSeasonsAsync(
        AuthSession session,
        string seriesId,
        CancellationToken cancellationToken)
    {
        LoadSeasonsCallCount++;
        LastSeasonsSession = session;
        LastSeriesId = seriesId;
        return LoadSeasonsAsyncHandler?.Invoke(session, seriesId, cancellationToken)
            ?? Task.FromResult(SeriesSeasonsLoadResult.Success(Array.Empty<SeasonInfo>()));
    }

    public Task<SeriesEpisodesLoadResult> LoadEpisodesAsync(
        AuthSession session,
        string seriesId,
        string seasonId,
        CancellationToken cancellationToken)
    {
        LoadEpisodesCallCount++;
        LastEpisodesSession = session;
        LastEpisodesSeriesId = seriesId;
        LastSeasonId = seasonId;
        return LoadEpisodesAsyncHandler?.Invoke(session, seriesId, seasonId, cancellationToken)
            ?? Task.FromResult(SeriesEpisodesLoadResult.Success(Array.Empty<EpisodeInfo>()));
    }
}

internal sealed class TestPlaybackService : IPlaybackService
{
    public Func<AuthSession, PlaybackStartRequest, CancellationToken, Task<PlaybackLoadResult>>? PreparePlaybackAsyncHandler { get; set; }

    public int PrepareCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public PlaybackStartRequest? LastRequest { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<PlaybackLoadResult> PreparePlaybackAsync(
        AuthSession session,
        PlaybackStartRequest request,
        CancellationToken cancellationToken)
    {
        PrepareCallCount++;
        LastSession = session;
        LastRequest = request;
        LastCancellationToken = cancellationToken;
        return PreparePlaybackAsyncHandler?.Invoke(session, request, cancellationToken)
            ?? Task.FromResult(PlaybackLoadResult.Success(CreatePlaybackInfo(request)));
    }

    private static PlaybackInfo CreatePlaybackInfo(PlaybackStartRequest request)
    {
        return new PlaybackInfo(
            request.ItemId,
            request.Title,
            "play-session-1",
            new PlaybackMediaSource(
                "media-source-1",
                "mkv",
                SupportsDirectPlay: true,
                SupportsDirectStream: true,
                SupportsTranscoding: false,
                new Dictionary<string, string>
                {
                    ["X-Test-Header"] = "header-value"
                }),
            "http://media.local:8096/Videos/item-1/stream.mkv",
            RequiresTranscoding: false,
            RequiresTokenInUrl: false,
            TimeSpan.FromMinutes(90).Ticks,
            request.StartPositionTicks,
            new[]
            {
                new PlaybackTrack(1, "jpn", "aac", "Japanese AAC 2.0", true)
            },
            new[]
            {
                new PlaybackSubtitle(2, "chi", "srt", "中文 SRT", false, true, "External")
            });
    }
}

internal sealed class TestNextEpisodeService : INextEpisodeService
{
    public Func<AuthSession, string, CancellationToken, Task<NextEpisodeResult>>? GetNextEpisodeAsyncHandler { get; set; }

    public int CallCount { get; private set; }

    public string? LastItemId { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<NextEpisodeResult> GetNextEpisodeAsync(
        AuthSession session,
        string currentItemId,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastItemId = currentItemId;
        LastCancellationToken = cancellationToken;
        return GetNextEpisodeAsyncHandler?.Invoke(session, currentItemId, cancellationToken)
            ?? Task.FromResult(NextEpisodeResult.Success(null));
    }
}

internal sealed class TestPlaybackReportService : IPlaybackReportService
{
    public Func<AuthSession, PlaybackReportStartRequest, CancellationToken, Task<PlaybackReportResult>>? ReportPlayingAsyncHandler { get; set; }

    public Func<AuthSession, PlaybackReportProgressRequest, CancellationToken, Task<PlaybackReportResult>>? ReportProgressAsyncHandler { get; set; }

    public Func<AuthSession, PlaybackReportStoppedRequest, CancellationToken, Task<PlaybackReportResult>>? ReportStoppedAsyncHandler { get; set; }

    public int PlayingCallCount { get; private set; }

    public int ProgressCallCount { get; private set; }

    public int StoppedCallCount { get; private set; }

    public AuthSession? LastSession { get; private set; }

    public PlaybackReportStartRequest? LastStartRequest { get; private set; }

    public PlaybackReportProgressRequest? LastProgressRequest { get; private set; }

    public PlaybackReportStoppedRequest? LastStoppedRequest { get; private set; }

    public Task<PlaybackReportResult> ReportPlayingAsync(
        AuthSession session,
        PlaybackReportStartRequest request,
        CancellationToken cancellationToken)
    {
        PlayingCallCount++;
        LastSession = session;
        LastStartRequest = request;
        return ReportPlayingAsyncHandler?.Invoke(session, request, cancellationToken)
            ?? Task.FromResult(PlaybackReportResult.Success());
    }

    public Task<PlaybackReportResult> ReportProgressAsync(
        AuthSession session,
        PlaybackReportProgressRequest request,
        CancellationToken cancellationToken)
    {
        ProgressCallCount++;
        LastSession = session;
        LastProgressRequest = request;
        return ReportProgressAsyncHandler?.Invoke(session, request, cancellationToken)
            ?? Task.FromResult(PlaybackReportResult.Success());
    }

    public Task<PlaybackReportResult> ReportStoppedAsync(
        AuthSession session,
        PlaybackReportStoppedRequest request,
        CancellationToken cancellationToken)
    {
        StoppedCallCount++;
        LastSession = session;
        LastStoppedRequest = request;
        return ReportStoppedAsyncHandler?.Invoke(session, request, cancellationToken)
            ?? Task.FromResult(PlaybackReportResult.Success());
    }
}

internal sealed class TestPlaybackReportScheduler : IPlaybackReportScheduler
{
    public event EventHandler<long>? Tick;

    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public int StopAllCallCount { get; private set; }

    public long LastStartedPlaybackInstanceId { get; private set; }

    public TimeSpan LastInterval { get; private set; }

    public long LastStoppedPlaybackInstanceId { get; private set; }

    public void Start(long playbackInstanceId, TimeSpan interval)
    {
        StartCallCount++;
        LastStartedPlaybackInstanceId = playbackInstanceId;
        LastInterval = interval;
    }

    public void Stop(long playbackInstanceId)
    {
        StopCallCount++;
        LastStoppedPlaybackInstanceId = playbackInstanceId;
    }

    public void StopAll()
    {
        StopAllCallCount++;
    }

    public void RaiseTick(long playbackInstanceId)
    {
        Tick?.Invoke(this, playbackInstanceId);
    }
}

internal sealed class TestPlayerService : IPlayerService
{
    public Func<long, string, CancellationToken, Task<LocalSubtitleImportResult>>? LocalSubtitleHandler { get; set; }
    public Task<LocalSubtitleImportResult> ImportLocalSubtitleAsync(long instance, string path, CancellationToken token)
        => LocalSubtitleHandler?.Invoke(instance, path, token)
            ?? Task.FromResult(new LocalSubtitleImportResult(false, Array.Empty<PlayerTrackInfo>()));
    public Func<long, double, CancellationToken, Task<PlayerOperationResult>>? PlaybackSpeedHandler { get; set; }
    public List<(long Instance, double Speed)> PlaybackSpeedCalls { get; } = new();

    public Task<PlayerOperationResult> SetPlaybackSpeedAsync(long instance, double speed, CancellationToken token)
    {
        PlaybackSpeedCalls.Add((instance, speed));
        return PlaybackSpeedHandler?.Invoke(instance, speed, token) ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Func<long, SubtitleAdjustmentKind, double, CancellationToken, Task<PlayerOperationResult>>? SubtitleAdjustmentHandler { get; set; }
    public Func<long, CancellationToken, Task<PlayerTechnicalInfo?>>? TechnicalInfoHandler { get; set; }
    public List<(long Instance, SubtitleAdjustmentKind Kind, double Value)> SubtitleAdjustments { get; } = new();

    public Task<PlayerOperationResult> SetSubtitleAdjustmentAsync(long instance, SubtitleAdjustmentKind kind, double value, CancellationToken token)
    {
        SubtitleAdjustments.Add((instance, kind, value));
        return SubtitleAdjustmentHandler?.Invoke(instance, kind, value, token) ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerTechnicalInfo?> GetTechnicalInfoAsync(long instance, CancellationToken token)
        => TechnicalInfoHandler?.Invoke(instance, token) ?? Task.FromResult<PlayerTechnicalInfo?>(null);

    public event EventHandler<PlayerStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<PlayerProgressChangedEventArgs>? ProgressChanged;

    public Func<PlayerLoadRequest, CancellationToken, Task<PlayerOperationResult>>? LoadAsyncHandler { get; set; }

    public Func<CancellationToken, Task<PlayerOperationResult>>? PlayAsyncHandler { get; set; }

    public Func<CancellationToken, Task<PlayerOperationResult>>? PauseAsyncHandler { get; set; }

    public Func<long, TimeSpan, CancellationToken, Task<PlayerOperationResult>>? SeekAsyncHandler { get; set; }

    public Func<int, CancellationToken, Task<PlayerOperationResult>>? SetVolumeAsyncHandler { get; set; }

    public Func<bool, CancellationToken, Task<PlayerOperationResult>>? SetMuteAsyncHandler { get; set; }

    public Func<long, int, CancellationToken, Task<PlayerOperationResult>>? SelectAudioTrackAsyncHandler { get; set; }

    public Func<long, int, CancellationToken, Task<PlayerOperationResult>>? SelectSubtitleTrackAsyncHandler { get; set; }

    public Func<long, int, CancellationToken, Task<PlayerOperationResult>>? SelectExternalSubtitleAsyncHandler { get; set; }

    public Func<long, CancellationToken, Task<PlayerOperationResult>>? DisableSubtitleAsyncHandler { get; set; }

    public Func<CancellationToken, Task<PlayerOperationResult>>? StopAsyncHandler { get; set; }

    public int LoadCallCount { get; private set; }

    public int PlayCallCount { get; private set; }

    public int PauseCallCount { get; private set; }

    public int SeekCallCount { get; private set; }

    public int SetVolumeCallCount { get; private set; }

    public int SetMuteCallCount { get; private set; }

    public int SelectAudioTrackCallCount { get; private set; }

    public int SelectSubtitleTrackCallCount { get; private set; }

    public int SelectExternalSubtitleCallCount { get; private set; }

    public int DisableSubtitleCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public List<string> Calls { get; } = new();

    public PlayerLoadRequest? LastLoadRequest { get; private set; }

    public long LastSeekPlaybackInstanceId { get; private set; }

    public TimeSpan LastSeekPosition { get; private set; }

    public int LastVolume { get; private set; }

    public bool LastMute { get; private set; }

    public long LastAudioTrackPlaybackInstanceId { get; private set; }

    public int LastAudioTrackIndex { get; private set; }

    public long LastSubtitleTrackPlaybackInstanceId { get; private set; }

    public int LastSubtitleTrackIndex { get; private set; }

    public int LastExternalSubtitleStreamIndex { get; private set; }

    public long LastDisableSubtitlePlaybackInstanceId { get; private set; }

    public Task<PlayerOperationResult> LoadAsync(
        PlayerLoadRequest request,
        CancellationToken cancellationToken)
    {
        LoadCallCount++;
        Calls.Add($"load:{request.PlaybackInstanceId}:{request.VideoHostHandle}");
        LastLoadRequest = request;
        return LoadAsyncHandler?.Invoke(request, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> PlayAsync(CancellationToken cancellationToken)
    {
        PlayCallCount++;
        Calls.Add("play");
        return PlayAsyncHandler?.Invoke(cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> PauseAsync(CancellationToken cancellationToken)
    {
        PauseCallCount++;
        Calls.Add("pause");
        return PauseAsyncHandler?.Invoke(cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> SeekAsync(
        long playbackInstanceId,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        SeekCallCount++;
        LastSeekPlaybackInstanceId = playbackInstanceId;
        LastSeekPosition = position;
        Calls.Add($"seek:{playbackInstanceId}:{position.TotalSeconds:0.###}");
        return SeekAsyncHandler?.Invoke(playbackInstanceId, position, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> SetVolumeAsync(
        int volume,
        CancellationToken cancellationToken)
    {
        SetVolumeCallCount++;
        LastVolume = volume;
        Calls.Add($"volume:{volume}");
        return SetVolumeAsyncHandler?.Invoke(volume, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> SetMuteAsync(
        bool isMuted,
        CancellationToken cancellationToken)
    {
        SetMuteCallCount++;
        LastMute = isMuted;
        Calls.Add($"mute:{isMuted}");
        return SetMuteAsyncHandler?.Invoke(isMuted, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> SelectAudioTrackAsync(
        long playbackInstanceId,
        int trackIndex,
        CancellationToken cancellationToken)
    {
        SelectAudioTrackCallCount++;
        LastAudioTrackPlaybackInstanceId = playbackInstanceId;
        LastAudioTrackIndex = trackIndex;
        Calls.Add($"audio:{playbackInstanceId}:{trackIndex}");
        return SelectAudioTrackAsyncHandler?.Invoke(playbackInstanceId, trackIndex, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> SelectSubtitleTrackAsync(
        long playbackInstanceId,
        int trackIndex,
        CancellationToken cancellationToken)
    {
        SelectSubtitleTrackCallCount++;
        LastSubtitleTrackPlaybackInstanceId = playbackInstanceId;
        LastSubtitleTrackIndex = trackIndex;
        Calls.Add($"subtitle:{playbackInstanceId}:{trackIndex}");
        return SelectSubtitleTrackAsyncHandler?.Invoke(playbackInstanceId, trackIndex, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> SelectExternalSubtitleAsync(
        long playbackInstanceId,
        int mediaStreamIndex,
        CancellationToken cancellationToken)
    {
        SelectExternalSubtitleCallCount++;
        LastSubtitleTrackPlaybackInstanceId = playbackInstanceId;
        LastExternalSubtitleStreamIndex = mediaStreamIndex;
        Calls.Add($"external-subtitle:{playbackInstanceId}:{mediaStreamIndex}");
        return SelectExternalSubtitleAsyncHandler?.Invoke(playbackInstanceId, mediaStreamIndex, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> DisableSubtitleAsync(
        long playbackInstanceId,
        CancellationToken cancellationToken)
    {
        DisableSubtitleCallCount++;
        LastDisableSubtitlePlaybackInstanceId = playbackInstanceId;
        Calls.Add($"subtitle-off:{playbackInstanceId}");
        return DisableSubtitleAsyncHandler?.Invoke(playbackInstanceId, cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public Task<PlayerOperationResult> StopAsync(CancellationToken cancellationToken)
    {
        StopCallCount++;
        Calls.Add("stop");
        return StopAsyncHandler?.Invoke(cancellationToken)
            ?? Task.FromResult(PlayerOperationResult.Success());
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void RaiseStatusChanged(PlayerStatusChangedEventArgs eventArgs)
    {
        StatusChanged?.Invoke(this, eventArgs);
    }

    public void RaiseProgressChanged(PlayerProgressChangedEventArgs eventArgs)
    {
        ProgressChanged?.Invoke(this, eventArgs);
    }
}
