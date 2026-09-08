using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using EmbyPlayer.App.Security;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Devices;
using EmbyPlayer.Core.Library;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.Emby;

internal static partial class Program
{
    private const int BrowseLogicalLimit = 25;
    private const int BrowseHttpLimit = 50;

    private static string BrowseWorkspace(string path)
    {
        for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "EmbyPlayer.sln"))) return parent.FullName;
        throw new ArgumentException("Workspace unavailable.");
    }

    private static void ValidateLibraryPaths(string artifactRoot, string outputRoot)
    {
        var temp = Path.Combine(BrowseWorkspace(artifactRoot), ".tmp") + Path.DirectorySeparatorChar;
        if (!Directory.Exists(artifactRoot) || !artifactRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !outputRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || artifactRoot.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("P1 build and evidence must be separate workspace temporary directories.");
        foreach (var path in new[] { artifactRoot, outputRoot, AppContext.BaseDirectory })
            for (var parent = new DirectoryInfo(path); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Reparse paths are unsupported.");
    }

    private static object VerifyLibraryBuild(string root)
    {
        var assemblies = new List<object>();
        foreach (var name in ProductAssemblies)
        {
            var file = name + ".dll";
            var sourcePath = Path.Combine(root, file);
            var copyPath = Path.Combine(AppContext.BaseDirectory, file);
            Require((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) == 0
                && (File.GetAttributes(copyPath) & FileAttributes.ReparsePoint) == 0, "p1-assembly-reparse");
            var sourceHash = HashFile(sourcePath);
            var copyHash = HashFile(copyPath);
            Require(sourceHash == copyHash, "p1-copy-does-not-match-supplied-build");
            var loaded = Assembly.LoadFrom(copyPath);
            var loadedHash = HashFile(loaded.Location);
            Require(loaded.GetName().Name == name && sourceHash == loadedHash, "p1-loaded-assembly-mismatch");
            var info = new FileInfo(sourcePath);
            assemblies.Add(new { fileName = file, sourceSha256 = sourceHash, copiedSha256 = copyHash,
                loadedSha256 = loadedHash, sizeBytes = info.Length, sourceLastWriteUtc = info.LastWriteTimeUtc });
        }
        var workspace = BrowseWorkspace(root);
        string Git(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
                { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            Require(process.WaitForExit(5000) && process.ExitCode == 0, "p1-checkout-identity-unavailable");
            return output.TrimEnd('\r', '\n');
        }
        var commit = Git("rev-parse", "HEAD");
        var changes = Git("status", "--porcelain").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return new { kind = "P1-source-build", assemblies, buildCommit = (string?)null,
            provenance = "Pre-existing supplied DLLs; checkout identity below is observed at check time, not build attestation.",
            checkoutAtCheck = new { commit, dirty = changes.Length > 0,
                indexDirty = changes.Any(line => line[0] is not (' ' or '?')),
                worktreeDirty = changes.Any(line => line.Length > 1 && line[1] != ' ') } };
    }

    private static async Task RunLibraryBrowseAsync(string outputRoot, string sourceRoot, CheckResult result, CancellationToken token)
    {
        var snapshotRoot = Path.Combine(outputRoot, "library-settings");
        var sourceFiles = new Dictionary<string, string?>();
        var sourceBaselineCaptured = false;
        var gaps = new List<string>();
        BrowseObserver? observer = null;
        result.Details["scope"] = "GET-only library browsing; no player, GUI, settings preference changes, or credential writes/deletes";
        result.Details["nameCollation"] = "Server query and response order only; no local string-order assertion";
        result.Details["budgets"] = new { logicalCalls = BrowseLogicalLimit, httpRequests = BrowseHttpLimit, overallSeconds = 120 };
        try
        {
            Directory.CreateDirectory(snapshotRoot);
            foreach (var name in new[] { "settings.json", "settings.json.bak" })
            {
                var source = Path.Combine(sourceRoot, name);
                sourceFiles[name] = File.Exists(source) ? HashFile(source) : null;
                if (!File.Exists(source)) continue;
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                await using var copy = new FileStream(Path.Combine(snapshotRoot, name), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await input.CopyToAsync(copy, token);
            }
            sourceBaselineCaptured = true;
            var settings = new FileAppSettingsService(Path.Combine(snapshotRoot, "settings.json"));
            var metadata = await settings.GetAuthSessionMetadataAsync(token);
            if (metadata is null) throw new CheckNotRun("library-session-metadata-unavailable");
            var deviceId = await settings.GetDeviceIdAsync(token);
            if (string.IsNullOrWhiteSpace(deviceId)) throw new CheckNotRun("library-saved-device-id-unavailable");
            // Only the read operation is used. Never invoke AuthSessionStore cleanup or credential Save/Delete.
            string? accessToken;
            try
            {
                accessToken = await new WindowsCredentialAuthSessionStore().LoadTokenAsync(
                    AuthSessionCredentialTarget.Create(metadata.ServerBase, metadata.UserId), token);
            }
            catch (System.ComponentModel.Win32Exception) { throw new CheckNotRun("library-secure-session-unavailable"); }
            if (string.IsNullOrWhiteSpace(accessToken)) throw new CheckNotRun("library-secure-session-unavailable");
            var session = new AuthSession(metadata.ServerBase, accessToken, metadata.UserId, metadata.UserName, metadata.ServerId);
            result.Assert("library-existing-session-loaded-read-only", true);
            observer = new BrowseObserver(session);
            using var http = new HttpClient(observer) { Timeout = TimeSpan.FromSeconds(15) };
            var service = new EmbyLibraryService(http, new SettingsDeviceIdService(settings));
            observer.Prepare(null, new LibraryQuery(), 0, 0);
            var libraries = await service.LoadLibrariesAsync(session, token);
            Require(libraries.IsSuccess, "library-views-" + libraries.Error);
            result.Assert("library-views-get", true);
            foreach (var type in new[] { "movies", "tvshows" })
            {
                var library = libraries.Libraries.FirstOrDefault(item => item.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
                if (library is null)
                {
                    BrowseNotRun(result, gaps, type + "-library", "matching-library-unavailable");
                    continue;
                }
                await CheckLibraryAsync(service, session, library, observer, result, gaps, token);
            }
            if (gaps.Count > 0) throw new CheckNotRun("library-browse-partial-coverage");
        }
        finally
        {
            result.Details["logicalCalls"] = observer?.LogicalCalls ?? 0;
            result.Details["httpRequests"] = observer?.SentCount ?? 0;
            result.Details["notRunChecks"] = gaps;
            result.Details["credentialEvidence"] = "LoadTokenAsync only; token equality or external credential-store changes were not measured";
            try
            {
                bool? unchanged = sourceBaselineCaptured ? sourceFiles.All(pair => pair.Value == (File.Exists(Path.Combine(sourceRoot, pair.Key))
                    ? HashFile(Path.Combine(sourceRoot, pair.Key)) : null)) : null;
                result.Details["sourceSettingsFilesUnchanged"] = unchanged;
                Require(unchanged != false, "library-source-settings-changed-during-check");
            }
            finally
            {
                if (Directory.Exists(snapshotRoot))
                {
                    foreach (var name in new[] { "settings.json", "settings.json.bak" })
                    {
                        var path = Path.Combine(snapshotRoot, name);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    Directory.Delete(snapshotRoot);
                }
                result.Details["settingsSnapshot"] = "owned-copy-removed";
            }
        }
    }

    private static async Task CheckLibraryAsync(EmbyLibraryService service, AuthSession session, LibraryItem library,
        BrowseObserver observer, CheckResult result, List<string> gaps, CancellationToken token)
    {
        var prefix = library.Type.ToLowerInvariant();
        async Task<LibraryItemsLoadResult> Page(string name, LibraryQuery query, int start, int limit)
        {
            observer.Prepare(library, query, start, limit);
            var page = await service.LoadLibraryItemsAsync(session, library, start, limit, query, token);
            Require(page.IsSuccess, name + "-" + page.Error);
            result.Assert(name + "-request", true);
            var raw = observer.LastItems ?? throw new CheckFailure(name + "-response-unavailable");
            Require(observer.LastRawCount <= limit, name + "-response-limit");
            Require(page.TotalRecordCount == observer.LastTotal, name + "-total-mapping");
            Require(page.Items.Select(item => item.Id).SequenceEqual(raw.Select(item => item.Id)), name + "-response-order-mapping");
            Require(page.Items.Zip(raw).All(pair => pair.First.Year == pair.Second.Year
                && pair.First.IsPlayed == (pair.Second.Played ?? false)
                && pair.First.IsFavorite == (pair.Second.Favorite ?? false)), name + "-user-data-mapping");
            result.Assert(name + "-response-mapping", true);
            if (raw.Count == 0) BrowseNotRun(result, gaps, name + "-sample", "empty-result");
            else
            {
                if (query.WatchedFilter != LibraryWatchedFilter.All || query.FavoritesOnly)
                {
                    if (raw.Any(item => query.WatchedFilter != LibraryWatchedFilter.All && item.Played is null
                        || query.FavoritesOnly && item.Favorite is null))
                        BrowseNotRun(result, gaps, name + "-membership", "explicit-user-data-unavailable");
                    else result.Assert(name + "-membership", raw.All(item =>
                        (query.WatchedFilter == LibraryWatchedFilter.All || item.Played == (query.WatchedFilter == LibraryWatchedFilter.Watched))
                        && (!query.FavoritesOnly || item.Favorite == true)));
                }
                CheckBrowseOrder(name, query, raw, result, gaps);
            }
            return page;
        }

        var first = await Page(prefix + "-Title-Ascending", new LibraryQuery(), 0, 100);
        if (first.Items.Count == 100 && (first.TotalRecordCount is null || first.TotalRecordCount > 100))
        {
            var second = await Page(prefix + "-next-page", new LibraryQuery(), 100, 100);
            if (first.TotalRecordCount != second.TotalRecordCount)
                BrowseNotRun(result, gaps, prefix + "-pagination", "total-changed-during-check");
            else if (second.Items.Count == 0)
                BrowseNotRun(result, gaps, prefix + "-pagination", "second-page-empty");
            else result.Assert(prefix + "-pagination", !first.Items.Select(item => item.Id)
                .Intersect(second.Items.Select(item => item.Id), StringComparer.OrdinalIgnoreCase).Any()
                && first.Items.Concat(second.Items).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    == first.Items.Count + second.Items.Count);
        }
        else BrowseNotRun(result, gaps, prefix + "-pagination", "fewer-than-two-pages");

        foreach (var field in Enum.GetValues<LibrarySortField>())
            foreach (var direction in Enum.GetValues<LibrarySortDirection>())
            {
                if (field == LibrarySortField.Title && direction == LibrarySortDirection.Ascending) continue;
                await Page($"{prefix}-{field}-{direction}", new LibraryQuery(field, direction), 0, 20);
            }
        foreach (var watched in Enum.GetValues<LibraryWatchedFilter>())
            foreach (var favorites in new[] { false, true })
            {
                if (watched == LibraryWatchedFilter.All && !favorites) continue;
                await Page($"{prefix}-{watched}-favorites-{favorites}",
                    new LibraryQuery(WatchedFilter: watched, FavoritesOnly: favorites), 0, 20);
            }
    }

    private static void CheckBrowseOrder(string name, LibraryQuery query, IReadOnlyList<BrowseItem> items,
        CheckResult result, List<string> gaps)
    {
        if (query.SortField == LibrarySortField.Title) return;
        var values = items.Select(item => query.SortField == LibrarySortField.Year ? (long?)item.Year : item.DateAdded?.UtcTicks).ToArray();
        if (values.Any(value => value is null) || values.Distinct().Count() < 2)
        {
            BrowseNotRun(result, gaps, name + "-value-order", "missing-or-insufficient-distinct-sort-values");
            return;
        }
        result.Assert(name + "-value-order", values.Zip(values.Skip(1)).All(pair =>
            query.SortDirection == LibrarySortDirection.Ascending ? pair.First <= pair.Second : pair.First >= pair.Second));
    }

    private static void BrowseNotRun(CheckResult result, List<string> gaps, string name, string reason)
    {
        gaps.Add(name);
        result.Checks.Add(new { name, status = "NotRun", reason });
    }

    private sealed record BrowseItem(string Id, int? Year, bool? Played, bool? Favorite, DateTimeOffset? DateAdded);

    // Inspect only bounded GET responses in memory; no request URI, identity, or response is exported.
    private sealed class BrowseObserver(AuthSession session, HttpMessageHandler? inner = null)
        : DelegatingHandler(inner ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        private readonly Uri origin = new(session.ServerBase);
        private LibraryItem? library;
        private LibraryQuery query = new();
        private int startIndex, limit;
        public int LogicalCalls { get; private set; }
        public int SentCount { get; private set; }
        public List<BrowseItem>? LastItems { get; private set; }
        public int? LastTotal { get; private set; }
        public int LastRawCount { get; private set; }
        public bool RedirectsDisabled => InnerHandler is HttpClientHandler { AllowAutoRedirect: false };

        public void Prepare(LibraryItem? target, LibraryQuery options, int start, int count)
        {
            Require(LogicalCalls < BrowseLogicalLimit, "library-logical-request-budget");
            LogicalCalls++;
            library = target; query = options; startIndex = start; limit = count;
            LastItems = null; LastTotal = null; LastRawCount = 0;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri;
            var suffix = "/Users/" + Uri.EscapeDataString(session.UserId) + (library is null ? "/Views" : "/Items");
            var basePath = origin.AbsolutePath.TrimEnd('/');
            Require(request.Method == HttpMethod.Get && request.Content is null && uri is not null
                && uri.Scheme == origin.Scheme && uri.Host == origin.Host && uri.Port == origin.Port
                && (uri.AbsolutePath == basePath + suffix || uri.AbsolutePath == basePath + "/emby" + suffix),
                "library-request-outside-get-scope");
            Require(request.Headers.TryGetValues("X-Emby-Token", out var tokens) && tokens.Single() == session.AccessToken,
                "library-request-authentication-header");
            if (library is not null)
            {
                var parameters = uri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]),
                        part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : "", StringComparer.OrdinalIgnoreCase);
                string? Value(string key) => parameters.GetValueOrDefault(key);
                Require(Value("ParentId") == library.Id && Value("StartIndex") == startIndex.ToString()
                    && Value("Limit") == limit.ToString() && limit is > 0 and <= 100
                    && Value("SortBy") == (query.SortField switch { LibrarySortField.DateAdded => "DateCreated,SortName",
                        LibrarySortField.Year => "ProductionYear,SortName", _ => "SortName" })
                    && Value("SortOrder") == query.SortDirection.ToString()
                    && Value("IsPlayed") == (query.WatchedFilter switch { LibraryWatchedFilter.Watched => "true",
                        LibraryWatchedFilter.Unwatched => "false", _ => null })
                    && Value("IsFavorite") == (query.FavoritesOnly ? "true" : null)
                    && Value("EnableUserData") == "true" && Value("Recursive") == "true"
                    && Value("IncludeItemTypes") == (library.Type.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "Movie" : "Series"),
                    "library-query-parameters");
            }
            Require(SentCount < BrowseHttpLimit, "library-http-request-budget");
            SentCount++;
            var response = await base.SendAsync(request, token);
            if (library is null || !response.IsSuccessStatusCode) return response;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root = document.RootElement;
                LastTotal = Number(root, "TotalRecordCount");
                LastItems = [];
                if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    LastRawCount = items.GetArrayLength();
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("Id", out var id) || string.IsNullOrWhiteSpace(id.GetString())
                            || !item.TryGetProperty("Name", out var title) || string.IsNullOrWhiteSpace(title.GetString())) continue;
                        item.TryGetProperty("UserData", out var userData);
                        var date = item.TryGetProperty("DateCreated", out var created) && created.ValueKind == JsonValueKind.String
                            && created.TryGetDateTimeOffset(out var timestamp) ? timestamp : (DateTimeOffset?)null;
                        LastItems.Add(new(id.GetString()!, Number(item, "ProductionYear"), Boolean(userData, "Played"),
                            Boolean(userData, "IsFavorite"), date));
                    }
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        private static int? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number
            && field.TryGetInt32(out var value) ? value : null;
        private static bool? Boolean(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : null;
    }

    private static async Task RunLibraryOfflineAsync(CheckResult result, CancellationToken token)
    {
        result.Details["network"] = "not-used-fake-http-only";
        result.Details["credentials"] = "not-accessed";
        var session = new AuthSession("https://fixture.invalid", "fixture-token", "fixture-user", "Fixture", "fixture-server");
        using var productionGuard = new BrowseObserver(session);
        result.Assert("offline-production-redirects-disabled", productionGuard.RedirectsDisabled);
        var fake = new BrowseFakeHandler();
        using var observer = new BrowseObserver(session, fake);
        using var http = new HttpClient(observer);
        HttpRequestMessage Request(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Emby-Token", session.AccessToken);
            return request;
        }
        async Task Reject(string name, HttpMethod method, string path, string code)
        {
            var sent = fake.Sent;
            try
            {
                using var request = Request(method, path);
                using var response = await http.SendAsync(request, token);
                throw new CheckFailure(name + "-not-rejected");
            }
            catch (CheckFailure failure) when (failure.Code == code) { }
            result.Assert(name, fake.Sent == sent);
        }
        observer.Prepare(null, new LibraryQuery(), 0, 0);
        const string views = "https://fixture.invalid/Users/fixture-user/Views";
        await Reject("offline-block-post", HttpMethod.Post, views, "library-request-outside-get-scope");
        await Reject("offline-block-cross-origin", HttpMethod.Get, "https://other.invalid/Users/fixture-user/Views", "library-request-outside-get-scope");
        await Reject("offline-block-other-user", HttpMethod.Get, "https://fixture.invalid/Users/other/Views", "library-request-outside-get-scope");
        await Reject("offline-block-admin-path", HttpMethod.Get, "https://fixture.invalid/Library/Refresh", "library-request-outside-get-scope");
        await Reject("offline-block-detail-path", HttpMethod.Get, "https://fixture.invalid/Users/fixture-user/Items/item", "library-request-outside-get-scope");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var sent = fake.Sent;
            try
            {
                using var request = Request(HttpMethod.Get, views);
                using var response = await http.SendAsync(request, cancelled.Token);
                throw new CheckFailure("offline-cancellation-not-rejected");
            }
            catch (OperationCanceledException) { result.Assert("offline-cancel-before-send", fake.Sent == sent); }
        }
        for (var i = 0; i < BrowseHttpLimit; i++)
        {
            using var request = Request(HttpMethod.Get, views);
            using var response = await http.SendAsync(request, token);
        }
        await Reject("offline-http-budget-before-send", HttpMethod.Get, views, "library-http-request-budget");
        for (var i = 1; i < BrowseLogicalLimit; i++) observer.Prepare(null, new LibraryQuery(), 0, 0);
        try { observer.Prepare(null, new LibraryQuery(), 0, 0); throw new CheckFailure("offline-logical-budget-not-rejected"); }
        catch (CheckFailure failure) when (failure.Code == "library-logical-request-budget") { result.Assert("offline-logical-budget", true); }
        using var queryObserver = new BrowseObserver(session, new BrowseFakeHandler());
        using var queryHttp = new HttpClient(queryObserver);
        var service = new EmbyLibraryService(queryHttp, new BrowseFixtureDevice());
        var library = new LibraryItem("fixture-library", "Fixture", "movies");
        var options = new LibraryQuery(LibrarySortField.Year, LibrarySortDirection.Descending, LibraryWatchedFilter.Watched, true);
        queryObserver.Prepare(library, options, 100, 20);
        var page = await service.LoadLibraryItemsAsync(session, library, 100, 20, options, token);
        result.Assert("offline-production-query-forwarding", page.IsSuccess && queryObserver.SentCount == 1 && page.Items.Count == 0);
        var fixtureResult = new CheckResult("offline-fixture");
        var gaps = new List<string>();
        using var sparseObserver = new BrowseObserver(session, new BrowseFakeHandler(
            "{\"Items\":[{\"Id\":\"fixture\",\"Name\":\"Fixture\",\"UserData\":{}}],\"TotalRecordCount\":1}"));
        using var sparseHttp = new HttpClient(sparseObserver);
        await CheckLibraryAsync(new EmbyLibraryService(sparseHttp, new BrowseFixtureDevice()), session, library,
            sparseObserver, fixtureResult, gaps, token);
        result.Assert("offline-missing-values-produce-not-run", gaps.Any(name => name.EndsWith("-value-order")));
        result.Assert("offline-missing-bools-never-confirm-filter", gaps.Count(name => name.EndsWith("-membership")) == 5);
        result.Assert("offline-insufficient-pagination-not-run", gaps.Contains("movies-pagination"));
    }

    private sealed class BrowseFakeHandler(string content = "{\"Items\":[],\"TotalRecordCount\":0}") : HttpMessageHandler
    {
        public int Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
    private sealed class BrowseFixtureDevice : IDeviceIdService
    {
        public Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken) => Task.FromResult("fixture-device");
    }
}
