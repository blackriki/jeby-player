using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly string[] ProductAssemblies =
        ["EmbyPlayer.App", "EmbyPlayer.Core", "EmbyPlayer.Emby", "EmbyPlayer.Player", "EmbyPlayer.UI"];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is not (6 or 8) || args.Where((_, index) => index % 2 == 0).Distinct().Count() != args.Length / 2)
            return InvalidArguments();
        var options = Enumerable.Range(0, args.Length / 2).ToDictionary(index => args[index * 2], index => args[index * 2 + 1]);
        options.TryGetValue("--session-data-directory", out var sessionDataDirectory);
        if (!options.TryGetValue("--artifact-directory", out var artifactDirectory)
            || !options.TryGetValue("--output-directory", out var outputDirectory)
            || !options.TryGetValue("--check", out var check)
            || !IsSupportedCheck(check)
            || string.IsNullOrWhiteSpace(artifactDirectory) || string.IsNullOrWhiteSpace(outputDirectory)
            || args.Length == 8 && (string.IsNullOrWhiteSpace(sessionDataDirectory)
                || !UsesSavedSession(check)))
            return InvalidArguments();
#if LIBRARY_BROWSE
        if (check == "library-browse" && sessionDataDirectory is null) return InvalidArguments();
#endif

        string artifactRoot;
        string outputRoot;
        string? sessionDataRoot = null;
        try
        {
            artifactRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDirectory));
            outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
            if (outputRoot.Equals(artifactRoot, StringComparison.OrdinalIgnoreCase)
                || outputRoot.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
                return InvalidArguments();
            if (UsesSavedSession(check))
            {
                if (sessionDataDirectory is not null && (!Path.IsPathFullyQualified(sessionDataDirectory)
                    || !Directory.Exists(sessionDataDirectory) || sessionDataDirectory.StartsWith(@"\\?\")
                    || sessionDataDirectory.StartsWith(@"\\.\"))) return InvalidArguments();
                sessionDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDataDirectory
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbyPlayer")));
                var sourcePrefix = Path.EndsInDirectorySeparator(sessionDataRoot) ? sessionDataRoot : sessionDataRoot + Path.DirectorySeparatorChar;
                var outputPrefix = Path.EndsInDirectorySeparator(outputRoot) ? outputRoot : outputRoot + Path.DirectorySeparatorChar;
                if (outputRoot.Equals(sessionDataRoot, StringComparison.OrdinalIgnoreCase)
                    || outputRoot.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
                    || sessionDataRoot.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase)) return InvalidArguments();
                foreach (var directory in new[] { sessionDataRoot, outputRoot })
                    for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                        if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return InvalidArguments();
                foreach (var name in new[] { "settings.json", "settings.json.bak" })
                {
                    var path = Path.Combine(sessionDataRoot, name);
                    if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return InvalidArguments();
                }
            }
#if LIBRARY_BROWSE
            ValidateLibraryPaths(artifactRoot, outputRoot);
#endif
            Directory.CreateDirectory(outputRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return InvalidArguments();
        }

        var result = new CheckResult(check);
        var watch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(check == "library-browse" ? 120 : check == "server-playback" ? 200 : check == "audio-switch" ? 110 : 30));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        // Product diagnostics use this isolated root; the owned UI gets its own fresh directory.
        Environment.SetEnvironmentVariable("EMBYPLAYER_TEST_DATA_ROOT", Path.Combine(outputRoot, "app-data"));
        try
        {
#if LIBRARY_BROWSE
            result.Details["sourceBuild"] = VerifyLibraryBuild(artifactRoot);
            result.Assert("p1-source-build-binding", true);
            if (check == "library-browse-offline")
                await RunLibraryOfflineAsync(result, cancellation.Token);
            else
                await RunLibraryBrowseAsync(outputRoot, sessionDataRoot!, result, cancellation.Token);
#else
            result.Artifact = VerifyArtifact(artifactRoot);
            result.Assert("artifact-binding", true);
            if (check == "settings-export")
                await RunSettingsExportAsync(outputRoot, result, cancellation.Token);
            else if (check == "ui-smoke")
                await RunUiSmokeAsync(artifactRoot, outputRoot, result, cancellation.Token);
            else
            {
                result.Details["sessionSourceIsExplicit"] = sessionDataDirectory is not null;
                await RunServerPlaybackAsync(artifactRoot, outputRoot, result, cancellation.Token,
                    audioOnly: check == "audio-switch", sessionDataDirectory: sessionDataRoot);
            }
#endif
            result.Status = "Passed";
        }
        catch (Exception exception)
        {
            if (exception is CheckNotRun) result.Status = "NotRun";
            result.ErrorCode = exception is CheckFailure failure ? failure.Code
                : exception is CheckNotRun notRun ? notRun.Code
                : exception is OperationCanceledException ? "check-cancelled-or-timed-out" : "check-exception";
            result.ExceptionType = exception.GetType().Name;
        }
        result.DurationMilliseconds = watch.ElapsedMilliseconds;
        var json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "result.json"), json, Utf8);
        Console.WriteLine(json);
        return result.Status == "Passed" ? 0 : result.Status == "NotRun" ? 3 : 1;
    }

    private static int InvalidArguments()
    {
#if LIBRARY_BROWSE
        Console.Error.WriteLine("Usage: --artifact-directory <P1-source-build> --check library-browse|library-browse-offline --output-directory <new-workspace-temp-directory> [--session-data-directory <required-existing-profile-for-library-browse>]");
#else
        Console.Error.WriteLine("Usage: --artifact-directory <release-directory> --check settings-export|ui-smoke|server-playback|audio-switch --output-directory <new-or-empty-directory-outside-artifact> [--session-data-directory <existing-absolute-directory-for-real-checks>]");
#endif
        return 2;
    }

    private static bool IsSupportedCheck(string check) =>
#if LIBRARY_BROWSE
        check is "library-browse" or "library-browse-offline";
#else
        check is "settings-export" or "ui-smoke" or "server-playback" or "audio-switch";
#endif

    private static bool UsesSavedSession(string check) =>
#if LIBRARY_BROWSE
        check == "library-browse";
#else
        check is "server-playback" or "audio-switch";
#endif

    private static ArtifactEvidence VerifyArtifact(string root)
    {
        var manifestBytes = File.ReadAllBytes(Path.Combine(root, "release-manifest.json"));
        using var manifest = JsonDocument.Parse(manifestBytes);
        var data = manifest.RootElement;
        Require(data.GetProperty("schemaVersion").GetInt32() == 1
            && !string.IsNullOrWhiteSpace(data.GetProperty("version").GetString())
            && !string.IsNullOrWhiteSpace(data.GetProperty("git").GetProperty("commit").GetString()), "artifact-manifest-identity");
        var evidence = new ArtifactEvidence(
            data.GetProperty("version").GetString()!,
            data.GetProperty("git").GetProperty("commit").GetString()!,
            Convert.ToHexString(SHA256.HashData(manifestBytes)), []);
        foreach (var name in ProductAssemblies)
        {
            var fileName = name + ".dll";
            var entry = data.GetProperty("files").EnumerateArray().Single(
                item => item.GetProperty("relativePath").GetString() == fileName);
            var expectedHash = entry.GetProperty("sha256").GetString()!;
            var size = entry.GetProperty("sizeBytes").GetInt64();
            var artifactPath = Path.Combine(root, fileName);
            var copyPath = Path.Combine(AppContext.BaseDirectory, fileName);
            var artifactHash = HashFile(artifactPath);
            // Check the copy before loading, then check the assembly actually returned by the loader.
            Require(HashFile(copyPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
                && artifactHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(artifactPath).Length == size, "artifact-payload-mismatch");
            var loaded = Assembly.LoadFrom(copyPath);
            var loadedHash = HashFile(loaded.Location);
            Require(loaded.GetName().Name == name
                && loadedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(loaded.Location).Length == size, "loaded-assembly-mismatch");
            evidence.Assemblies.Add(new(fileName, expectedHash, artifactHash, loadedHash));
        }
        return evidence;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new CheckFailure(code);
    }

    private sealed class CheckFailure(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    private sealed class CheckNotRun(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    private sealed class CheckResult(string check)
    {
        public int SchemaVersion => 1;
        public string Check { get; } = check;
        public string Status { get; set; } = "Failed";
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public long DurationMilliseconds { get; set; }
        public string? ErrorCode { get; set; }
        public string? ExceptionType { get; set; }
        public ArtifactEvidence? Artifact { get; set; }
        public List<object> Checks { get; } = [];
        public Dictionary<string, object?> Details { get; } = [];
        public void Assert(string name, bool condition)
        {
            Checks.Add(new { name, status = condition ? "Passed" : "Failed" });
            Require(condition, name);
        }
    }

    private sealed record ArtifactEvidence(string Version, string Commit, string ManifestSha256, List<AssemblyEvidence> Assemblies);
    private sealed record AssemblyEvidence(string FileName, string ManifestSha256, string ArtifactSha256, string LoadedSha256);
}
