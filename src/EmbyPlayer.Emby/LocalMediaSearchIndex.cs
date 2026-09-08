using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyPlayer.Core;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Search;

namespace EmbyPlayer.Emby;

public sealed class LocalMediaSearchIndex : ILocalMediaSearchIndex
{
    private const int IndexFormatVersion = 1;
    private const int PageSize = 400;
    private const string SupportedItemTypes = "Movie,Series,Episode,BoxSet,Video";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IDeviceIdService deviceIdService;
    private readonly HttpClient httpClient;
    private readonly string indexDirectory;
    private readonly ConcurrentDictionary<string, ScopeState> states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim cacheWriteGate = new(1, 1);
    private readonly object cacheSync = new();
    private CancellationTokenSource cacheLifetime = new();
    private int cacheGeneration;

    public LocalMediaSearchIndex(
        HttpClient httpClient,
        IDeviceIdService deviceIdService,
        string indexDirectory)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.deviceIdService = deviceIdService ?? throw new ArgumentNullException(nameof(deviceIdService));

        if (string.IsNullOrWhiteSpace(indexDirectory))
        {
            throw new ArgumentException("Index directory is required.", nameof(indexDirectory));
        }

        this.indexDirectory = Path.GetFullPath(indexDirectory);
    }

    public async Task<long> GetDiskCacheBytesAsync(CancellationToken cancellationToken)
    {
        await cacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => GetCacheFiles().Sum(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new FileInfo(path).Length;
            }), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheWriteGate.Release();
        }
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        await cacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource previousLifetime;
            lock (cacheSync)
            {
                previousLifetime = cacheLifetime;
                cacheLifetime = new CancellationTokenSource();
                cacheGeneration++;
                states.Clear();
            }

            previousLifetime.Cancel();
            previousLifetime.Dispose();
            await Task.Run(() =>
            {
                foreach (var path in GetCacheFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(path);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheWriteGate.Release();
        }
    }

    public async Task RebuildCacheAsync(AuthSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await ClearCacheAsync(cancellationToken).ConfigureAwait(false);
        var state = GetState(session);
        await state.InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task refreshTask;
        try
        {
            state.LoadAttempted = true;
            state.RefreshStarted = true;
            refreshTask = RefreshCoreAsync(session, state, GetIndexFilePath(session), cancellationToken, true);
            state.RefreshTask = refreshTask;
        }
        finally
        {
            state.InitializationGate.Release();
        }
        await refreshTask.ConfigureAwait(false);
    }

    public Task PrepareAsync(AuthSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return EnsureAvailableAsync(session, cancellationToken);
    }

    public async Task RefreshAsync(AuthSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = GetState(session);
        await state.InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        Task refreshTask;
        try
        {
            if (state.RefreshTask is null || state.RefreshTask.IsCompleted)
            {
                state.RefreshStarted = true;
                state.RefreshTask = RefreshCoreAsync(session, state, GetIndexFilePath(session));
            }

            refreshTask = state.RefreshTask;
        }
        finally
        {
            state.InitializationGate.Release();
        }

        await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalMediaSearchQueryResult> SearchAsync(
        AuthSession session,
        string keyword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return LocalMediaSearchQueryResult.Empty;
        }

        var state = GetState(session);
        await EnsureAvailableAsync(session, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (state.Generation != Volatile.Read(ref cacheGeneration))
        {
            return LocalMediaSearchQueryResult.Empty;
        }

        IndexDocument? document;
        TimeSpan? loadDuration;
        TimeSpan? buildDuration;
        lock (state.SyncRoot)
        {
            document = state.Document;
            loadDuration = state.LastLoadDuration;
            buildDuration = state.LastBuildDuration ?? GetStoredBuildDuration(document);
        }

        if (document is null)
        {
            return new LocalMediaSearchQueryResult(
                Array.Empty<LocalMediaSearchMatch>(),
                0,
                buildDuration,
                loadDuration,
                null);
        }

        var normalizedKeyword = Normalize(keyword);
        var matches = document.Items
            .Select(item => CreateMatch(item, normalizedKeyword))
            .Where(match => match.Rank < int.MaxValue)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Item.SortName ?? match.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(match => match.Item.ProductionYear)
            .ThenBy(match => match.Item.Id, StringComparer.Ordinal)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        if (state.Generation != Volatile.Read(ref cacheGeneration)) return LocalMediaSearchQueryResult.Empty;
        return new LocalMediaSearchQueryResult(
            matches,
            document.Items.Count,
            buildDuration,
            loadDuration,
            document.ApiBase);
    }

    private async Task EnsureAvailableAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        var state = GetState(session);
        await state.InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        Task refreshTask;
        bool hasDocument;
        try
        {
            if (!state.LoadAttempted)
            {
                state.LoadAttempted = true;
                await LoadFromDiskAsync(state, GetIndexFilePath(session), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!state.RefreshStarted || state.RefreshTask is { IsCanceled: true } or { IsFaulted: true })
            {
                state.RefreshStarted = true;
                state.RefreshTask = RefreshCoreAsync(session, state, GetIndexFilePath(session));
            }

            refreshTask = state.RefreshTask ?? Task.CompletedTask;
            lock (state.SyncRoot)
            {
                hasDocument = state.Document is not null;
            }
        }
        finally
        {
            state.InitializationGate.Release();
        }

        if (!hasDocument)
        {
            await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadFromDiskAsync(
        ScopeState state,
        string filePath,
        CancellationToken cancellationToken)
    {
        await cacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadDocumentAsync(state, filePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cacheWriteGate.Release();
        }
    }

    private static async Task ReadDocumentAsync(
        ScopeState state, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<IndexDocument>(json, JsonOptions);
            if (document?.Version != IndexFormatVersion || document.Items is null)
            {
                return;
            }

            lock (state.SyncRoot)
            {
                state.Document = document;
            }
        }
        catch (JsonException exception)
        {
            Debug.WriteLine($"Unable to load local media search index: {exception.Message}");
        }
        catch (IOException exception)
        {
            Debug.WriteLine($"Unable to read local media search index: {exception.Message}");
        }
        finally
        {
            stopwatch.Stop();
            lock (state.SyncRoot)
            {
                state.LastLoadDuration = stopwatch.Elapsed;
            }
        }
    }

    private async Task RefreshCoreAsync(AuthSession session, ScopeState state, string filePath,
        CancellationToken cancellationToken = default, bool propagateFailure = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var buildVersion = Interlocked.Increment(ref state.BuildVersion);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.CacheToken);
        var token = linkedCancellation.Token;
        try
        {
            var document = await BuildIndexAsync(session, token).ConfigureAwait(false);
            stopwatch.Stop();
            document.BuildDurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            await cacheWriteGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (state.Generation != Volatile.Read(ref cacheGeneration)
                    || buildVersion != Volatile.Read(ref state.BuildVersion)) return;
                await Task.Run(() => WriteAtomicallyAsync(filePath, document, token), token).ConfigureAwait(false);
                lock (state.SyncRoot)
                {
                    state.Document = document;
                    state.LastBuildDuration = stopwatch.Elapsed;
                }
            }
            finally
            {
                cacheWriteGate.Release();
            }
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            if (propagateFailure) throw;
            Debug.WriteLine($"Unable to refresh local media search index: {exception.Message}");
        }
    }

    private async Task<IndexDocument> BuildIndexAsync(
        AuthSession session,
        CancellationToken cancellationToken)
    {
        var indexedItems = new Dictionary<string, LocalMediaSearchItem>(StringComparer.Ordinal);
        var startIndex = 0;
        string? apiBase = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpointPath = BuildIndexPath(session.UserId, startIndex);
            IndexEndpointResult endpointResult;
            if (apiBase is null)
            {
                endpointResult = await GetWithFallbackAsync(session, endpointPath, cancellationToken)
                    .ConfigureAwait(false);
                apiBase = endpointResult.ApiBase;
            }
            else
            {
                endpointResult = await GetStringAsync(
                        BuildEndpointUri(apiBase, endpointPath),
                        session.AccessToken,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!endpointResult.IsSuccess || string.IsNullOrWhiteSpace(endpointResult.Content))
            {
                throw new HttpRequestException("Unable to build local media search index.", null, endpointResult.StatusCode);
            }

            var page = JsonSerializer.Deserialize<IndexPageResponse>(endpointResult.Content, JsonOptions)
                ?? throw new JsonException("The media index response was empty.");
            var pageItems = page.Items ?? throw new JsonException("The media index response has no item list.");

            foreach (var item in pageItems)
            {
                if (item is null) throw new JsonException("The media index contains a null item.");
                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                indexedItems[item.Id] = new LocalMediaSearchItem(
                    item.Id,
                    item.Name,
                    item.OriginalTitle,
                    item.SortName,
                    item.SeriesName,
                    item.Type ?? string.Empty,
                    item.ProductionYear,
                    item.ImageTags?.Primary,
                    item.IndexNumber,
                    item.ParentIndexNumber);
            }

            var receivedCount = pageItems.Count;
            if (receivedCount == 0)
            {
                break;
            }

            startIndex += receivedCount;
            if (page.TotalRecordCount is int totalRecordCount && startIndex >= totalRecordCount)
            {
                break;
            }

            if (page.TotalRecordCount is null && receivedCount < PageSize)
            {
                break;
            }
        }

        return new IndexDocument
        {
            Version = IndexFormatVersion,
            ApiBase = apiBase ?? session.ServerBase.TrimEnd('/'),
            BuiltAtUtc = DateTimeOffset.UtcNow,
            Items = indexedItems.Values
                .OrderBy(item => item.SortName ?? item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList()
        };
    }

    private async Task<IndexEndpointResult> GetWithFallbackAsync(
        AuthSession session,
        string endpointPath,
        CancellationToken cancellationToken)
    {
        var serverBase = session.ServerBase.TrimEnd('/');
        var primaryResult = await GetStringAsync(
                BuildEndpointUri(serverBase, endpointPath),
                session.AccessToken,
                cancellationToken)
            .ConfigureAwait(false);
        if (!primaryResult.CanFallback)
        {
            return primaryResult.WithApiBase(serverBase);
        }

        var embyApiBase = $"{serverBase}/emby";
        var fallbackResult = await GetStringAsync(
                BuildEndpointUri(embyApiBase, endpointPath),
                session.AccessToken,
                cancellationToken)
            .ConfigureAwait(false);
        return fallbackResult.WithApiBase(embyApiBase);
    }

    private async Task<IndexEndpointResult> GetStringAsync(
        Uri endpointUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var deviceId = await deviceIdService
            .GetOrCreateDeviceIdAsync(cancellationToken)
            .ConfigureAwait(false);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpointUri);
        request.Headers.TryAddWithoutValidation("X-Emby-Token", accessToken);
        request.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            $"Emby Client=\"{ApplicationIdentity.Name}\", Device=\"Windows\", DeviceId=\"{deviceId}\", Version=\"{ApplicationIdentity.Version}\"");

        try
        {
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return IndexEndpointResult.FallbackAllowed();
            }

            if (!response.IsSuccessStatusCode)
            {
                return IndexEndpointResult.Failure(response.StatusCode);
            }

            var content = await response.Content
                .ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content)) throw new JsonException("The media index response was empty.");
            return IndexEndpointResult.Success(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("The media index request timed out.", exception);
        }
    }

    private async Task WriteAtomicallyAsync(
        string filePath,
        IndexDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The media index path has no directory.");
        ValidateCacheDirectory();
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private ScopeState GetState(AuthSession session)
    {
        lock (cacheSync)
        {
            return states.GetOrAdd(GetScopeHash(session), _ => new ScopeState(cacheGeneration, cacheLifetime.Token));
        }
    }

    private IEnumerable<string> GetCacheFiles()
    {
        ValidateCacheDirectory();
        if (!Directory.Exists(indexDirectory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(indexDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var parts = Path.GetFileName(path).Split('.');
                return parts[0].Length == 64 && parts[0].All(char.IsAsciiHexDigit)
                    && (parts.Length == 2 && parts[1] == "json"
                        || parts.Length == 4 && parts[1] == "json"
                        && Guid.TryParseExact(parts[2], "N", out _) && parts[3] == "tmp")
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
            });
    }

    private void ValidateCacheDirectory()
    {
        if (string.Equals(indexDirectory.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetPathRoot(indexDirectory)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A filesystem root cannot be used as the search cache directory.");
        for (var directory = new DirectoryInfo(indexDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A redirected directory cannot be used for search cache maintenance.");
        }
    }

    private string GetIndexFilePath(AuthSession session)
    {
        return Path.Combine(indexDirectory, $"{GetScopeHash(session)}.json");
    }

    private static string GetScopeHash(AuthSession session)
    {
        var scope = string.Join(
            '\n',
            session.ServerId.Trim(),
            session.ServerBase.TrimEnd('/').ToUpperInvariant(),
            session.UserId.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
    }

    private static LocalMediaSearchMatch CreateMatch(
        LocalMediaSearchItem item,
        string normalizedKeyword)
    {
        var name = Normalize(item.Name);
        if (string.Equals(name, normalizedKeyword, StringComparison.Ordinal))
        {
            return new LocalMediaSearchMatch(item, 0, SearchMatchSource.Name);
        }

        if (name.StartsWith(normalizedKeyword, StringComparison.Ordinal))
        {
            return new LocalMediaSearchMatch(item, 1, SearchMatchSource.Name);
        }

        if (name.Contains(normalizedKeyword, StringComparison.Ordinal))
        {
            return new LocalMediaSearchMatch(item, 2, SearchMatchSource.Name);
        }

        if (Normalize(item.OriginalTitle).Contains(normalizedKeyword, StringComparison.Ordinal))
        {
            return new LocalMediaSearchMatch(item, 3, SearchMatchSource.OriginalTitle);
        }

        if (Normalize(item.SortName).Contains(normalizedKeyword, StringComparison.Ordinal))
        {
            return new LocalMediaSearchMatch(item, 3, SearchMatchSource.SortName);
        }

        if (Normalize(item.SeriesName).Contains(normalizedKeyword, StringComparison.Ordinal))
        {
            return new LocalMediaSearchMatch(item, 4, SearchMatchSource.SeriesName);
        }

        return new LocalMediaSearchMatch(item, int.MaxValue, SearchMatchSource.Unknown);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Normalize(NormalizationForm.FormKC).ToUpper(CultureInfo.InvariantCulture);
    }

    private static TimeSpan? GetStoredBuildDuration(IndexDocument? document)
    {
        return document?.BuildDurationMilliseconds is > 0
            ? TimeSpan.FromMilliseconds(document.BuildDurationMilliseconds)
            : null;
    }

    private static string BuildIndexPath(string userId, int startIndex)
    {
        return $"/Users/{Uri.EscapeDataString(userId)}/Items"
            + "?Recursive=true"
            + $"&IncludeItemTypes={SupportedItemTypes}"
            + $"&StartIndex={startIndex}"
            + $"&Limit={PageSize}"
            + "&Fields=OriginalTitle,SortName,SeriesName,ProductionYear,PrimaryImageAspectRatio,IndexNumber,ParentIndexNumber"
            + "&EnableUserData=false"
            + "&EnableImages=true&ImageTypeLimit=1&EnableImageTypes=Primary"
            + "&SortBy=SortName&SortOrder=Ascending";
    }

    private static Uri BuildEndpointUri(string apiBase, string endpointPath)
    {
        return new Uri($"{apiBase.TrimEnd('/')}{endpointPath}", UriKind.Absolute);
    }

    private sealed class ScopeState
    {
        public ScopeState(int generation, CancellationToken cacheToken)
        {
            Generation = generation;
            CacheToken = cacheToken;
        }

        public int Generation { get; }

        public CancellationToken CacheToken { get; }

        public int BuildVersion;

        public object SyncRoot { get; } = new();

        public SemaphoreSlim InitializationGate { get; } = new(1, 1);

        public bool LoadAttempted { get; set; }

        public bool RefreshStarted { get; set; }

        public Task? RefreshTask { get; set; }

        public IndexDocument? Document { get; set; }

        public TimeSpan? LastBuildDuration { get; set; }

        public TimeSpan? LastLoadDuration { get; set; }
    }

    private sealed class IndexDocument
    {
        public int Version { get; set; }

        public string ApiBase { get; set; } = string.Empty;

        public DateTimeOffset BuiltAtUtc { get; set; }

        public double BuildDurationMilliseconds { get; set; }

        public List<LocalMediaSearchItem> Items { get; set; } = new();
    }

    private sealed class IndexPageResponse
    {
        public List<IndexItemResponse>? Items { get; set; }

        public int? TotalRecordCount { get; set; }
    }

    private sealed class IndexItemResponse
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? OriginalTitle { get; set; }

        public string? SortName { get; set; }

        public string? SeriesName { get; set; }

        public string? Type { get; set; }

        public int? ProductionYear { get; set; }

        public int? IndexNumber { get; set; }

        public int? ParentIndexNumber { get; set; }

        public IndexImageTags? ImageTags { get; set; }
    }

    private sealed class IndexImageTags
    {
        public string? Primary { get; set; }
    }

    private sealed class IndexEndpointResult
    {
        private IndexEndpointResult(
            bool isSuccess,
            bool canFallback,
            string? content,
            string? apiBase,
            HttpStatusCode? statusCode = null)
        {
            IsSuccess = isSuccess;
            CanFallback = canFallback;
            Content = content;
            ApiBase = apiBase;
            StatusCode = statusCode;
        }

        public bool IsSuccess { get; }

        public bool CanFallback { get; }

        public string? Content { get; }

        public string? ApiBase { get; }

        public HttpStatusCode? StatusCode { get; }

        public IndexEndpointResult WithApiBase(string apiBase)
        {
            return new IndexEndpointResult(IsSuccess, CanFallback, Content, apiBase, StatusCode);
        }

        public static IndexEndpointResult Success(string content)
        {
            return new IndexEndpointResult(true, false, content, null);
        }

        public static IndexEndpointResult FallbackAllowed()
        {
            return new IndexEndpointResult(false, true, null, null);
        }

        public static IndexEndpointResult Failure(HttpStatusCode? statusCode = null)
        {
            return new IndexEndpointResult(false, false, null, null, statusCode);
        }
    }
}
