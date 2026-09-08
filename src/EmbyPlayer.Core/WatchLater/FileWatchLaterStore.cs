using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Servers;

namespace EmbyPlayer.Core.WatchLater;

public sealed class FileWatchLaterStore : IWatchLaterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> ImageQueryKeys = new(StringComparer.OrdinalIgnoreCase)
        { "tag", "maxWidth", "maxHeight", "width", "height", "quality", "format", "fillWidth", "fillHeight" };
    private readonly string directoryPath;
    // MainWindow shares one instance; serialize read-modify-write operations across its callers.
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <param name="directoryPath">Dedicated persistent directory, outside image/search cache directories.</param>
    public FileWatchLaterStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        this.directoryPath = Path.GetFullPath(directoryPath);
    }

    public async Task<IReadOnlyList<WatchLaterItem>> LoadAsync(AuthSession session, CancellationToken cancellationToken)
    {
        var scope = GetScope(session);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(scope.Path, session, scope.ServerBase, cancellationToken).ConfigureAwait(false);
            return NormalizeItems(session, scope.ServerBase, document.Items!);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> IsSavedAsync(AuthSession session, string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var items = await LoadAsync(session, cancellationToken).ConfigureAwait(false);
        return items.Any(item => string.Equals(item.ItemId, itemId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public Task AddAsync(AuthSession session, WatchLaterItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ItemId);
        if (!IsSupportedType(item.MediaType)) throw new ArgumentException("Unsupported watch-later media type.", nameof(item));
        return UpdateAsync(session, items => new[] { item }.Concat(items)
            .DistinctBy(value => value.ItemId.Trim(), StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
    }

    public Task RemoveAsync(AuthSession session, string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return UpdateAsync(session, items => items.Where(item =>
            !string.Equals(item.ItemId, itemId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray(), cancellationToken);
    }

    private async Task UpdateAsync(AuthSession session, Func<IReadOnlyList<WatchLaterItem>, IReadOnlyList<WatchLaterItem>> update,
        CancellationToken cancellationToken)
    {
        var scope = GetScope(session);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadAsync(scope.Path, session, scope.ServerBase, cancellationToken).ConfigureAwait(false);
            document.Items = NormalizeItems(session, scope.ServerBase,
                update(NormalizeItems(session, scope.ServerBase, document.Items!))).ToList();
            await WriteAsync(scope.Path, document, preserveBackup: true, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task ResetCorruptedAsync(AuthSession session, CancellationToken cancellationToken)
    {
        var scope = GetScope(session);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primary = await TryReadAsync(scope.Path, cancellationToken).ConfigureAwait(false);
            if (primary.Document is not null) return;
            var backup = await TryReadAsync(scope.Path + ".bak", cancellationToken).ConfigureAwait(false);
            if (backup.Document is not null)
            {
                backup.Document.Items = NormalizeItems(session, scope.ServerBase, backup.Document.Items!).ToList();
                await WriteAsync(scope.Path, backup.Document, preserveBackup: false, cancellationToken).ConfigureAwait(false);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (primary.IsCorrupted) File.Move(scope.Path, scope.Path + $".corrupt-{Guid.NewGuid():N}");
            if (backup.IsCorrupted) File.Move(scope.Path + ".bak", scope.Path + $".bak.corrupt-{Guid.NewGuid():N}");
            await WriteAsync(scope.Path, new Document(), preserveBackup: false, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<Document> ReadAsync(string path, AuthSession session, string serverBase, CancellationToken cancellationToken)
    {
        var primary = await TryReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (primary.Document is not null) return primary.Document;
        var backup = await TryReadAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
        if (backup.Document is not null)
        {
            backup.Document.Items = NormalizeItems(session, serverBase, backup.Document.Items!).ToList();
            await WriteAsync(path, backup.Document, preserveBackup: false, cancellationToken).ConfigureAwait(false);
            Trace.TraceWarning("Recovered watch-later data from its backup.");
            return backup.Document;
        }
        if (primary.IsCorrupted || backup.IsCorrupted)
            throw new InvalidDataException("The watch-later list and its backup cannot be read.");
        return new Document();
    }

    private static async Task<(Document? Document, bool IsCorrupted)> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return document?.Version == 1 && document.Items is not null
                && document.Items.All(item => item is not null && !string.IsNullOrWhiteSpace(item.ItemId)
                    && IsSupportedType(item.MediaType))
                ? (document, false) : (null, true);
        }
        catch (FileNotFoundException) { return (null, false); }
        catch (DirectoryNotFoundException) { return (null, false); }
        catch (JsonException) { return (null, true); }
    }

    private static async Task WriteAsync(string path, Document document, bool preserveBackup, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporaryPath, path, preserveBackup ? path + ".bak" : null, ignoreMetadataErrors: true);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private (string Path, string ServerBase) GetScope(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalized = ServerUrlNormalizer.Normalize(session.ServerBase);
        if (!normalized.IsSuccess) throw new ArgumentException("A valid server base is required.", nameof(session));
        var serverBase = normalized.Server!.ServerBase;
        var identity = JsonSerializer.Serialize(new[] { serverBase, session.UserId });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return (Path.Combine(directoryPath, hash + ".json"), serverBase);
    }

    private static IReadOnlyList<WatchLaterItem> NormalizeItems(AuthSession session, string serverBase, IEnumerable<WatchLaterItem> items)
        => items.Select(item => item with
        {
            ItemId = item.ItemId.Trim(),
            Title = string.IsNullOrWhiteSpace(item.Title) ? "未命名媒体" : item.Title,
            ImageUrl = SafeImageUrl(item.ImageUrl, serverBase, session.AccessToken)
        }).DistinctBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsSupportedType(string? type) => type is not null
        && (type.Equals("Movie", StringComparison.OrdinalIgnoreCase) || type.Equals("Series", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Episode", StringComparison.OrdinalIgnoreCase));

    private static string? SafeImageUrl(string? imageUrl, string serverBase, string accessToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        var server = new Uri(serverBase);
        if (uri.Scheme != server.Scheme || !uri.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != server.Port
            || !uri.AbsolutePath.StartsWith(server.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal)) return null;
        if (Uri.UnescapeDataString(uri.AbsoluteUri).Contains(accessToken, StringComparison.Ordinal)) return null;
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = parameter.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? parameter : parameter[..separator]);
            if (!ImageQueryKeys.Contains(key)) return null;
        }
        return uri.AbsoluteUri;
    }

    private sealed class Document
    {
        [JsonRequired]
        public int Version { get; set; } = 1;
        [JsonRequired]
        public List<WatchLaterItem>? Items { get; set; } = new();
    }
}
