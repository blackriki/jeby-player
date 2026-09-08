using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReleasePipelineScriptTests
{
    [TestMethod]
    public async Task MpvRuntimeManifest_RecordsConsistentBinaryAndDistributionEvidence()
    {
        var root = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            root,
            "src",
            "EmbyPlayer.App",
            "runtimes",
            "win-x64",
            "native",
            "mpv-runtime.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var manifest = document.RootElement;
        var binary = manifest.GetProperty("binary");
        var archive = manifest.GetProperty("localArchive");

        Assert.IsTrue(binary.GetProperty("sizeBytes").GetInt64() > 0);
        StringAssert.Matches(binary.GetProperty("sha256").GetString()!, new System.Text.RegularExpressions.Regex("^[A-Fa-f0-9]{64}$"));
        Assert.AreEqual("AMD64", binary.GetProperty("architecture").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(archive.GetProperty("fileName").GetString()));
        StringAssert.Matches(archive.GetProperty("sha256").GetString()!, new System.Text.RegularExpressions.Regex("^[A-Fa-f0-9]{64}$"));
        var helperPath = Path.Combine(root, "scripts", "release-validation.ps1");
        var runtimeDirectory = Path.GetDirectoryName(manifestPath)!;
        var command = $"Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'; . '{QuotePowerShell(helperPath)}'; "
            + $"$manifest=Get-Content -LiteralPath '{QuotePowerShell(manifestPath)}' -Raw | ConvertFrom-Json; "
            + $"$binary=Join-Path '{QuotePowerShell(runtimeDirectory)}' 'libmpv-2.dll'; "
            + "if(Test-Path -LiteralPath $binary){ Assert-MpvRuntime -BinaryPath $binary -Manifest $manifest | Out-Null; "
            + $"Assert-MpvRuntimeDependencies -RuntimeDirectory '{QuotePowerShell(runtimeDirectory)}' -Manifest $manifest -RequireExactFileSet | Out-Null }}; "
            + $"$evidence=@(Get-MpvLicenseEvidence -Manifest $manifest -ManifestPath '{QuotePowerShell(manifestPath)}' -WorkspaceRoot '{QuotePowerShell(root)}'); "
            + "if($manifest.provenanceStatus -eq 'verified' -and -not (Test-MpvPublicDistributionReady -Manifest $manifest -LicenseEvidence $evidence)){ throw 'Verified provenance lacks public distribution evidence' }";
        var result = await RunPowerShellCommandAsync(command);
        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
    }

    [TestMethod]
    public async Task PublishScript_UsesNewWorkspaceStagingAndNeverTouchesStandardReleaseOutput()
    {
        var script = await ReadPublishScriptAsync();

        StringAssert.Contains(script, ".tmp\\release-staging");
        StringAssert.Contains(script, "Test-IsInsideWorkspace");
        StringAssert.Contains(script, "Assert-NoReparsePointInWorkspacePath");
        StringAssert.Contains(script, "Release target already exists");
        StringAssert.Contains(script, ".working-$transactionId");
        StringAssert.Contains(script, "Remove-OwnedWorkingDirectory");
        StringAssert.Contains(script, "Remove-OwnedWorkingChildDirectory");
        StringAssert.Contains(script, "RELEASE-FAILED.txt");
        StringAssert.Contains(script, "--artifacts-path");
        StringAssert.Contains(script, "$releaseManifestPath = Join-Path $publishDirectory");
        StringAssert.Contains(script, "Move-Item -LiteralPath $publishDirectory -Destination $targetRoot");
        Assert.IsFalse(script.Contains(
            "Move-Item -LiteralPath $workingRoot -Destination $targetRoot",
            StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("bin\\Release", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("clean-build-artifacts", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("run-app.ps1", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PublishScript_RestoresLockedBuildsReleaseAndRequiresEveryProjectLock()
    {
        var script = await ReadPublishScriptAsync();

        StringAssert.Contains(script, "--locked-mode");
        StringAssert.Contains(script, "-Configuration Release -ArtifactsPath $artifactsPath");
        StringAssert.Contains(script, "src\\EmbyPlayer.App\\packages.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.Core\\packages.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.Emby\\packages.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.Player\\packages.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.UI\\packages.lock.json");
        StringAssert.Contains(script, "tests\\EmbyPlayer.Core.Tests\\packages.lock.json");
        StringAssert.Contains(script, "tests\\EmbyPlayer.Emby.Tests\\packages.lock.json");
        StringAssert.Contains(script, "tests\\EmbyPlayer.Player.Tests\\packages.lock.json");
        StringAssert.Contains(script, "tests\\EmbyPlayer.UI.Tests\\packages.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.App\\packages.win-x64.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.Core\\packages.win-x64.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.Emby\\packages.win-x64.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.Player\\packages.win-x64.lock.json");
        StringAssert.Contains(script, "src\\EmbyPlayer.UI\\packages.win-x64.lock.json");
        StringAssert.Contains(script, "-p:RuntimeIdentifier=win-x64");
        StringAssert.Contains(script, "public static class ReleaseWindowProbe");
        StringAssert.Contains(script, "$env:TEMP = $testTempPath");
        StringAssert.Contains(script, "$env:TMP = $testTempPath");
        StringAssert.Contains(script, "SetEnvironmentVariable(\"TEMP\", $originalTemp");
    }

    [TestMethod]
    public async Task PublishScript_PublicChannelHardFailsIncompleteMpvEvidence()
    {
        var script = await ReadPublishScriptAsync();

        StringAssert.Contains(script, "$Channel -eq \"Public\" -and -not $mpvPublicReady");
        StringAssert.Contains(script, "Public releases never allow -AllowDirty");
        StringAssert.Contains(script, "Public releases require a clean working tree");
        StringAssert.Contains(script, "MPV source provenance, build recipe revision, and license evidence are incomplete");
        StringAssert.Contains(script, "$Channel -eq \"Public\" -and $SkipLaunchSmoke");
        StringAssert.Contains(script, "distributionReady = ($Channel -eq \"Public\" -and $mpvPublicReady)");
        StringAssert.Contains(script, "provenanceStatus");
        StringAssert.Contains(script, "sourceUrl");
        StringAssert.Contains(script, "license.expression");
    }

    [TestMethod]
    public async Task PublishScript_ValidatesMpvAndCreatesSortedHashedZipManifest()
    {
        var script = await ReadPublishScriptAsync();
        var appProject = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "EmbyPlayer.App",
            "EmbyPlayer.App.csproj"));
        var validationScript = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "release-validation.ps1"));

        StringAssert.Contains(script, "Assert-MpvRuntime");
        StringAssert.Contains(validationScript, "Assert-WinX64MpvArchitecture");
        StringAssert.Contains(validationScript, "Get-PeArchitecture");
        StringAssert.Contains(validationScript, "third-party/mpv/");
        StringAssert.Contains(script, "Published payload must not contain PDB files");
        StringAssert.Contains(script, "-p:DebugType=None");
        StringAssert.Contains(validationScript, "Get-FileSha256");
        StringAssert.Contains(script, "EmbyPlayer.App.exe");
        StringAssert.Contains(script, "EmbyPlayer.Core.dll");
        StringAssert.Contains(script, "EmbyPlayer.Emby.dll");
        StringAssert.Contains(script, "EmbyPlayer.Player.dll");
        StringAssert.Contains(script, "EmbyPlayer.UI.dll");
        StringAssert.Contains(script, "Sort-ReleaseManifestEntriesOrdinal -Entries $payloadFiles");
        StringAssert.Contains(script, "release-manifest.json");
        StringAssert.Contains(script, "Compress-Archive -LiteralPath");
        StringAssert.Contains(appProject, "Content Include=\"runtimes\\win-x64\\native\\*.dll\"");
        StringAssert.Contains(appProject, "Content Include=\"runtimes\\win-x64\\native\\mpv-runtime.json\"");
        Assert.IsFalse(appProject.Contains("native\\**\\*.*", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PublishScript_SmokeUsesOnlyExactStagingProcess()
    {
        var script = await ReadPublishScriptAsync();
        var validationScript = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "release-validation.ps1"));

        StringAssert.Contains(script, "-FilePath $executablePath");
        StringAssert.Contains(script, "-ArgumentList \"--release-smoke\"");
        StringAssert.Contains(script, "FindVisibleTopLevelWindow($smokePid)");
        StringAssert.Contains(script, "$stabilityDeadline = [DateTime]::UtcNow.AddSeconds(8)");
        StringAssert.Contains(script, "lost its visible top-level window during startup");
        StringAssert.Contains(script, "Complete-ReleaseLaunchSmokeProcess");
        StringAssert.Contains(script, "-AllowForceTermination (-not $SafeLaunchSmoke)");
        StringAssert.Contains(script, "[ReleaseWindowProbe]::TryPostClose($smokePid, $smokeWindowHandle)");
        StringAssert.Contains(script, "GetWindowThreadProcessId(windowHandle, out ownerProcessId) == 0");
        StringAssert.Contains(validationScript, "$Process.CloseMainWindow()");
        StringAssert.Contains(validationScript, "& $CloseAppWindow");
        StringAssert.Contains(validationScript, "elseif (-not $Process.CloseMainWindow())");
        StringAssert.Contains(validationScript, "Stop-Process -Id $processId -Force");
        Assert.IsFalse(script.Contains("Get-Process", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("Stop-Process -Name", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task WindowLifecycleVerification_ClosesIsolatedOrdinaryAndReleaseSmokeInstances()
    {
        var root = FindRepositoryRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(
            root,
            "scripts",
            "verify-window-lifecycle.ps1"));

        StringAssert.Contains(script, "Name = \"ordinary\"; Arguments = \"\"");
        StringAssert.Contains(script, "Name = \"release-smoke\"; Arguments = \"--release-smoke\"");
        StringAssert.Contains(script, "EnvironmentVariables[\"EMBYPLAYER_TEST_DATA_ROOT\"]");
        StringAssert.Contains(script, "$process.WaitForExit(5000)");
        StringAssert.Contains(script, "$process.ExitCode -ne 0");
        StringAssert.Contains(script, "FindVisibleTopLevelWindow($process.Id)");
        StringAssert.Contains(script, "$stabilityDeadline = [DateTime]::UtcNow.AddSeconds(8)");
        StringAssert.Contains(script, "lost its visible top-level window during startup");
        StringAssert.Contains(script, "[LifecycleWindowProbe]::TryPostClose($process.Id, $windowHandle)");
        StringAssert.Contains(script, "GetWindowThreadProcessId(windowHandle, out ownerProcessId) == 0");
        StringAssert.Contains(script, "application.previous.log");
        StringAssert.Contains(script, "category=application event=closing cancelled=false");
        StringAssert.Contains(script, "category=application event=exit");
        StringAssert.Contains(script, "$verificationSucceeded");
        StringAssert.Contains(script, "LIFECYCLE-DIAGNOSTICS-RETAINED");
        Assert.IsFalse(script.Contains("Get-Process", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("Stop-Process", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("$process.CloseMainWindow()", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PublishScript_PublicAllowDirtyPreflightFailsWithoutCreatingOutput()
    {
        var root = FindRepositoryRoot();
        var outputRoot = Path.Combine(
            root,
            ".tmp",
            "release-pipeline-tests",
            $"public-allowdirty-{Guid.NewGuid():N}");
        var result = await RunPowerShellFileAsync(
            Path.Combine(root, "scripts", "publish-release.ps1"),
            "-Version", $"1.0.0-public-allowdirty-{Guid.NewGuid():N}",
            "-Channel", "Public",
            "-AllowDirty",
            "-PreflightOnly",
            "-OutputRoot", outputRoot);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.CombinedOutput, "Public releases never allow -AllowDirty");
        Assert.IsFalse(Directory.Exists(outputRoot));
    }

    [DataTestMethod]
    [DataRow("-SkipTests", "Public releases cannot skip tests")]
    [DataRow("-SkipLaunchSmoke", "Public releases cannot skip launch smoke verification")]
    [DataRow("-DirectoryOnly", "Public releases must produce an immutable ZIP archive")]
    [DataRow("-SafeLaunchSmoke", "Public releases cannot use the non-force launch smoke mode")]
    public async Task PublishScript_PublicSkipPreflightFailsWithoutCreatingOutput(
        string skipArgument,
        string expectedMessage)
    {
        var root = FindRepositoryRoot();
        var outputRoot = Path.Combine(
            root,
            ".tmp",
            "release-pipeline-tests",
            $"public-skip-{Guid.NewGuid():N}");
        var result = await RunPowerShellFileAsync(
            Path.Combine(root, "scripts", "publish-release.ps1"),
            "-Version", $"1.0.0-public-skip-{Guid.NewGuid():N}",
            "-Channel", "Public",
            skipArgument,
            "-PreflightOnly",
            "-OutputRoot", outputRoot);

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.CombinedOutput, expectedMessage);
        Assert.IsFalse(Directory.Exists(outputRoot));
    }

    [TestMethod]
    public async Task PublishScript_PublicDirtyPreflightFailsWithoutCreatingOutput()
    {
        var root = FindRepositoryRoot();
        var dirtyMarker = Path.Combine(root, $".release-pipeline-dirty-test-{Guid.NewGuid():N}.txt");
        var outputRoot = Path.Combine(
            root,
            ".tmp",
            "release-pipeline-tests",
            $"public-dirty-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(dirtyMarker, "intentional dirty-tree fixture");
        try
        {
            var result = await RunPowerShellFileAsync(
                Path.Combine(root, "scripts", "publish-release.ps1"),
                "-Version", $"1.0.0-public-dirty-{Guid.NewGuid():N}",
                "-Channel", "Public",
                "-PreflightOnly",
                "-OutputRoot", outputRoot);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "Public releases require a clean working tree");
            Assert.IsFalse(Directory.Exists(outputRoot));
        }
        finally
        {
            File.Delete(dirtyMarker);
        }
    }

    [TestMethod]
    public async Task PublishScript_CommandFailurePropagatesAndRemovesOwnedWorkingPaths()
    {
        var root = FindRepositoryRoot();
        var outputRoot = Path.Combine(
            root,
            ".tmp",
            "release-pipeline-tests",
            $"command-failure-{Guid.NewGuid():N}");
        var fakeTools = Path.Combine(outputRoot, "fake-tools");
        Directory.CreateDirectory(fakeTools);
        var fakeDotnet = Path.Combine(fakeTools, "dotnet.cmd");
        await File.WriteAllTextAsync(
            fakeDotnet,
            "@echo off\r\nif \"%~1\"==\"--version\" (echo 10.0.303& exit /b 0)\r\nexit /b 37\r\n");
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = fakeTools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        };

        try
        {
            var version = $"1.0.0-command-failure-{Guid.NewGuid():N}";
            var result = await RunPowerShellFileAsync(
                Path.Combine(root, "scripts", "publish-release.ps1"),
                environment,
                "-Version", version,
                "-Channel", "Internal",
                "-AllowDirty",
                "-SkipTests",
                "-SkipLaunchSmoke",
                "-OutputRoot", outputRoot);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "locked solution restore failed with exit code 37");
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(outputRoot, ".working-*").Count());
            Assert.IsFalse(Directory.Exists(Path.Combine(outputRoot, version + "-frameworkdependent-internal")));
            Assert.IsFalse(File.Exists(Path.Combine(outputRoot, version + "-frameworkdependent-internal.zip")));
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublishScript_PdbFailureAfterPublishRemovesOwnedWorkingPaths()
    {
        var root = FindRepositoryRoot();
        var outputRoot = Path.Combine(
            root,
            ".tmp",
            "release-pipeline-tests",
            $"pdb-failure-{Guid.NewGuid():N}");
        var fakeTools = Path.Combine(outputRoot, "fake-tools");
        Directory.CreateDirectory(fakeTools);
        var fakeDotnet = Path.Combine(fakeTools, "dotnet.cmd");
        await File.WriteAllTextAsync(
            fakeDotnet,
            "@echo off\r\n"
            + "if \"%~1\"==\"--version\" (echo 10.0.303& exit /b 0)\r\n"
            + "if not \"%~1\"==\"publish\" exit /b 0\r\n"
            + ":findOutput\r\n"
            + "if \"%~1\"==\"\" exit /b 42\r\n"
            + "if \"%~1\"==\"-o\" goto createOutput\r\n"
            + "shift\r\n"
            + "goto findOutput\r\n"
            + ":createOutput\r\n"
            + "mkdir \"%~2\"\r\n"
            + "type nul > \"%~2\\unexpected.pdb\"\r\n"
            + "exit /b 0\r\n");
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = fakeTools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        };

        try
        {
            var version = $"1.0.0-pdb-failure-{Guid.NewGuid():N}";
            var result = await RunPowerShellFileAsync(
                Path.Combine(root, "scripts", "publish-release.ps1"),
                environment,
                "-Version", version,
                "-Channel", "Internal",
                "-AllowDirty",
                "-SkipTests",
                "-SkipLaunchSmoke",
                "-OutputRoot", outputRoot);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "Published payload must not contain PDB files");
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(outputRoot, ".working-*").Count());
            Assert.IsFalse(Directory.Exists(Path.Combine(outputRoot, version + "-frameworkdependent-internal")));
            Assert.IsFalse(File.Exists(Path.Combine(outputRoot, version + "-frameworkdependent-internal.zip")));
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReleaseValidation_RejectsActualNonAmd64PeFixture()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixturePath = Path.Combine(temporaryRoot, "fake-mpv.dll");
            var peBytes = new byte[512];
            peBytes[0] = (byte)'M';
            peBytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(peBytes, 0x3c);
            peBytes[0x80] = (byte)'P';
            peBytes[0x81] = (byte)'E';
            BitConverter.GetBytes((ushort)0x014c).CopyTo(peBytes, 0x84);
            await File.WriteAllBytesAsync(fixturePath, peBytes);

            var helperPath = Path.Combine(root, "scripts", "release-validation.ps1");
            var command = $". '{QuotePowerShell(helperPath)}'; Assert-WinX64MpvArchitecture -Path '{QuotePowerShell(fixturePath)}' | Out-Null";
            var result = await RunPowerShellCommandAsync(command);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "must be an actual AMD64 PE image");
            StringAssert.Contains(result.CombinedOutput, "0x014C");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow("valid", true)]
    [DataRow("legacy", true)]
    [DataRow("missing", false)]
    [DataRow("hash", false)]
    [DataRow("published-hash", false)]
    [DataRow("size", false)]
    [DataRow("traversal", false)]
    [DataRow("duplicate", false)]
    [DataRow("main", false)]
    [DataRow("undeclared", false)]
    [DataRow("architecture", false)]
    [DataRow("directory", false)]
    public async Task ReleaseValidation_DependencyManifestValidatesSourceAndPublishedFiles(string scenario, bool accepted)
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var runtime = Path.Combine(temporaryRoot, "native");
            var published = Path.Combine(temporaryRoot, "published");
            Directory.CreateDirectory(runtime);
            Directory.CreateDirectory(published);
            var bytes = new byte[512];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
            bytes[0x80] = (byte)'P';
            bytes[0x81] = (byte)'E';
            BitConverter.GetBytes((ushort)(scenario == "architecture" ? 0x014c : 0x8664)).CopyTo(bytes, 0x84);
            var fileName = scenario == "traversal" ? "../outside.dll" : scenario == "main" ? "libmpv-2.dll" : "dependency.dll";
            var entry = new
            {
                fileName,
                sizeBytes = scenario == "size" ? bytes.Length + 1 : bytes.Length,
                sha256 = scenario == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes))
            };
            var manifest = scenario == "legacy" ? "{}" : JsonSerializer.Serialize(new { dependencies = scenario == "duplicate" ? new[] { entry, entry } : new[] { entry } });
            var manifestPath = Path.Combine(temporaryRoot, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, manifest);
            if (scenario != "legacy")
            {
                await File.WriteAllBytesAsync(Path.Combine(runtime, "dependency.dll"), bytes);
                if (scenario == "directory")
                    Directory.CreateDirectory(Path.Combine(published, "dependency.dll"));
                else if (scenario != "missing")
                {
                    var publishedBytes = (byte[])bytes.Clone();
                    if (scenario == "published-hash")
                        publishedBytes[^1] = 1;
                    await File.WriteAllBytesAsync(Path.Combine(published, "dependency.dll"), publishedBytes);
                }
            }
            if (scenario == "undeclared")
                await File.WriteAllBytesAsync(Path.Combine(runtime, "unexpected.dll"), bytes);
            var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "release-validation.ps1");
            var command = $"Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'; . '{QuotePowerShell(helperPath)}'; "
                + $"$manifest=Get-Content -LiteralPath '{QuotePowerShell(manifestPath)}' -Raw | ConvertFrom-Json; "
                + $"$source=@(Assert-MpvRuntimeDependencies -RuntimeDirectory '{QuotePowerShell(runtime)}' -Manifest $manifest -RequireExactFileSet); "
                + $"$published=@(Assert-MpvRuntimeDependencies -RuntimeDirectory '{QuotePowerShell(published)}' -Manifest $manifest); "
                + "if($source.Count -ne $published.Count){ throw 'Dependency count changed during publish' }";
            var result = await RunPowerShellCommandAsync(command);
            Assert.AreEqual(accepted, result.ExitCode == 0, result.CombinedOutput);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyPromotion_RunningPlayerGuardRejectsWithoutStoppingProcess()
    {
        var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
        var result = await RunPowerShellCommandAsync(
            $"$ErrorActionPreference='Stop'; . '{QuotePowerShell(helperPath)}'; "
            + "function Get-Process { param($Name,$ErrorAction) if($Name -ne 'EmbyPlayer.App'){ throw 'Wrong process query' }; [pscustomobject]@{Id=54321} }; "
            + "function Stop-Process { throw 'Guard must never stop a process' }; Assert-DailyPlayerNotRunning");
        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.CombinedOutput, "54321");
        Assert.IsFalse(result.CombinedOutput.Contains("Guard must never stop", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("valid", true)]
    [DataRow("missing-license", false)]
    [DataRow("missing-notices", false)]
    [DataRow("older-cache-only", false)]
    [DataRow("missing-framework", false)]
    [DataRow("unsafe-version", false)]
    public async Task ReleaseValidation_DotNetLicensesUseIncludedFrameworkVersionsAndPreserveBytes(string scenario, bool accepted)
    {
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var cache = Path.Combine(temporaryRoot, "configured-cache");
            var published = Path.Combine(temporaryRoot, "published");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(published);
            var frameworks = new[]
            {
                new { name = "Microsoft.NETCore.App", version = scenario == "unsafe-version" ? "../outside" : "8.0.11" },
                new { name = "Microsoft.WindowsDesktop.App", version = "8.0.23" }
            };
            var runtimeConfig = Path.Combine(published, "EmbyPlayer.App.runtimeconfig.json");
            await File.WriteAllTextAsync(runtimeConfig, JsonSerializer.Serialize(new
            {
                runtimeOptions = new { includedFrameworks = scenario == "missing-framework" ? frameworks.Take(1).ToArray() : frameworks }
            }));
            var inputs = new[]
            {
                (Framework: "Microsoft.NETCore.App", Version: "8.0.11", File: "LICENSE.TXT"),
                (Framework: "Microsoft.NETCore.App", Version: "8.0.11", File: "THIRD-PARTY-NOTICES.TXT"),
                (Framework: "Microsoft.WindowsDesktop.App", Version: "8.0.23", File: "LICENSE")
            };
            var expectedBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("License fixture\r\nCopyright fixture — 保留原文\n")).ToArray();
            foreach (var input in inputs)
            {
                var packageId = input.Framework.ToLowerInvariant() + ".runtime.win-x64";
                var exactDirectory = Path.Combine(cache, packageId, input.Version);
                var olderDirectory = Path.Combine(cache, packageId, "8.0.1");
                Directory.CreateDirectory(exactDirectory);
                Directory.CreateDirectory(olderDirectory);
                await File.WriteAllTextAsync(Path.Combine(olderDirectory, input.File), "Wrong version decoy");
                if (scenario == "older-cache-only" ||
                    (scenario == "missing-license" && input.File == "LICENSE") ||
                    (scenario == "missing-notices" && input.File == "THIRD-PARTY-NOTICES.TXT"))
                    continue;
                await File.WriteAllBytesAsync(Path.Combine(exactDirectory, input.File), expectedBytes);
            }
            var helper = Path.Combine(FindRepositoryRoot(), "scripts", "release-validation.ps1");
            var evidencePath = Path.Combine(temporaryRoot, "evidence.json");
            var result = await RunPowerShellCommandAsync(
                $"Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'; . '{QuotePowerShell(helper)}'; "
                + $"$evidence=@(Copy-DotNetRuntimeLicenseEvidence -RuntimeConfigPath '{QuotePowerShell(runtimeConfig)}' "
                + $"-GlobalPackagesRoot '{QuotePowerShell(cache + Path.DirectorySeparatorChar)}' -PublishDirectory '{QuotePowerShell(published)}'); "
                + $"ConvertTo-Json -InputObject $evidence -Depth 5 | Set-Content -LiteralPath '{QuotePowerShell(evidencePath)}' -Encoding UTF8");
            Assert.AreEqual(accepted, result.ExitCode == 0, result.CombinedOutput);
            if (accepted)
            {
                using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
                Assert.AreEqual(inputs.Length, evidence.RootElement.GetArrayLength());
                foreach (var entry in evidence.RootElement.EnumerateArray())
                {
                    var destination = Path.Combine(published, entry.GetProperty("packagePath").GetString()!);
                    CollectionAssert.AreEqual(expectedBytes, await File.ReadAllBytesAsync(destination));
                    Assert.AreEqual(expectedBytes.Length, entry.GetProperty("sizeBytes").GetInt32());
                    Assert.AreEqual(Convert.ToHexString(SHA256.HashData(expectedBytes)), entry.GetProperty("sha256").GetString());
                    StringAssert.StartsWith(entry.GetProperty("source").GetString()!, "nuget:");
                    Assert.IsFalse(entry.GetProperty("source").GetString()!.Contains(temporaryRoot, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReleaseValidation_SortsManifestEntriesUsingOrdinalComparison()
    {
        var root = FindRepositoryRoot();
        var helperPath = Path.Combine(root, "scripts", "release-validation.ps1");
        var command =
            $". '{QuotePowerShell(helperPath)}'; "
            + "$entries=@("
            + "[pscustomobject]@{relativePath='clretwrc.dll'},"
            + "[pscustomobject]@{relativePath='D3DCompiler_47_cor3.dll'},"
            + "[pscustomobject]@{relativePath='Accessibility.dll'}); "
            + "@(Sort-ReleaseManifestEntriesOrdinal -Entries $entries).relativePath -join '|'";
        var result = await RunPowerShellCommandAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        Assert.AreEqual(
            "Accessibility.dll|D3DCompiler_47_cor3.dll|clretwrc.dll",
            result.StandardOutput.Trim());
    }

    [TestMethod]
    public async Task ReleaseValidation_CopiesHashedLicensesWithoutFlatteningDuplicateNames()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var runtimeDirectory = Path.Combine(temporaryRoot, "runtime");
            var firstRelativePath = Path.Combine("licenses", "component-a", "LICENSE.txt");
            var secondRelativePath = Path.Combine("licenses", "component-b", "LICENSE.txt");
            var firstPath = Path.Combine(runtimeDirectory, firstRelativePath);
            var secondPath = Path.Combine(runtimeDirectory, secondRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
            const string firstContent = "component a license fixture";
            const string secondContent = "component b license fixture";
            await File.WriteAllTextAsync(firstPath, firstContent);
            await File.WriteAllTextAsync(secondPath, secondContent);
            var manifestPath = Path.Combine(runtimeDirectory, "mpv-runtime.json");
            var manifest = new
            {
                license = new
                {
                    expression = "Test-Only",
                    files = new[]
                    {
                        new
                        {
                            path = firstRelativePath.Replace('\\', '/'),
                            sizeBytes = new FileInfo(firstPath).Length,
                            sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(firstPath)))
                        },
                        new
                        {
                            path = secondRelativePath.Replace('\\', '/'),
                            sizeBytes = new FileInfo(secondPath).Length,
                            sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(secondPath)))
                        }
                    }
                }
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
            var publishDirectory = Path.Combine(temporaryRoot, "publish");
            Directory.CreateDirectory(publishDirectory);

            var helperPath = Path.Combine(root, "scripts", "release-validation.ps1");
            var command =
                $". '{QuotePowerShell(helperPath)}'; "
                + $"$manifest=Get-Content -LiteralPath '{QuotePowerShell(manifestPath)}' -Raw | ConvertFrom-Json; "
                + $"$evidence=@(Get-MpvLicenseEvidence -Manifest $manifest -ManifestPath '{QuotePowerShell(manifestPath)}' -WorkspaceRoot '{QuotePowerShell(temporaryRoot)}'); "
                + $"Copy-MpvLicenseEvidence -LicenseEvidence $evidence -PublishDirectory '{QuotePowerShell(publishDirectory)}'; "
                + "$evidence | Select-Object sourceRelativePath,packagePath,sizeBytes,sha256 | ConvertTo-Json -Depth 4 -Compress";
            var result = await RunPowerShellCommandAsync(command);

            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
            Assert.IsTrue(File.Exists(Path.Combine(publishDirectory, "third-party", "mpv", firstRelativePath)));
            Assert.IsTrue(File.Exists(Path.Combine(publishDirectory, "third-party", "mpv", secondRelativePath)));
            StringAssert.Contains(result.StandardOutput, "third-party/mpv/licenses/component-a/LICENSE.txt");
            StringAssert.Contains(result.StandardOutput, "third-party/mpv/licenses/component-b/LICENSE.txt");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SensitiveScan_FindsNonIgnoredUntrackedSecretOutsideGitDiff()
    {
        var root = FindRepositoryRoot();
        var fixturePath = Path.Combine(root, $".release-sensitive-test-{Guid.NewGuid():N}.json");
        var keyName = "api_" + "key";
        await File.WriteAllTextAsync(fixturePath, $"{{\"url\":\"https://example.invalid/?{keyName}=fixture-{new string('x', 24)}\"}}");
        try
        {
            var result = await RunPowerShellFileAsync(Path.Combine(root, "scripts", "check-sensitive-files.ps1"));

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, Path.GetFileName(fixturePath));
            StringAssert.Contains(result.CombinedOutput, "api_key query");
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [TestMethod]
    public async Task SensitiveScan_FindsSecretInPublishedTextPayload()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var keyName = "access_" + "token";
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "appsettings.json"),
                $"{{\"{keyName}\":\"fixture-{new string('y', 24)}\"}}");
            var result = await RunPowerShellFileAsync(
                Path.Combine(root, "scripts", "check-sensitive-files.ps1"),
                "-PayloadRoot", temporaryRoot);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "credential assignment");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SensitiveScan_RejectsNullByteInRepositoryTextCandidate()
    {
        var root = FindRepositoryRoot();
        var fixturePath = Path.Combine(root, $".release-sensitive-nul-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(fixturePath, new byte[] { (byte)'{', 0, (byte)'}' });
        try
        {
            var result = await RunPowerShellFileAsync(Path.Combine(root, "scripts", "check-sensitive-files.ps1"));

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, ".release-sensitive-nul-");
            StringAssert.Contains(result.CombinedOutput, "contains a NUL byte");
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [TestMethod]
    public async Task SensitiveScan_RejectsNullByteInPayloadTextCandidate()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = CreateTemporaryDirectory();
        try
        {
            var fixturePath = Path.Combine(temporaryRoot, "appsettings.json");
            await File.WriteAllBytesAsync(fixturePath, new byte[] { (byte)'{', 0, (byte)'}' });
            var result = await RunPowerShellFileAsync(
                Path.Combine(root, "scripts", "check-sensitive-files.ps1"),
                "-PayloadRoot", temporaryRoot);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "appsettings.json");
            StringAssert.Contains(result.CombinedOutput, "contains a NUL byte");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReleaseValidation_RejectsFileWhoseAncestorIsDirectoryReparsePoint()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = CreateTemporaryDirectory();
        var controlledRoot = Path.Combine(temporaryRoot, "controlled");
        var targetRoot = Path.Combine(temporaryRoot, "target");
        var junctionPath = Path.Combine(controlledRoot, "linked");
        var linkedFilePath = Path.Combine(junctionPath, "settings.json");
        Directory.CreateDirectory(controlledRoot);
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllTextAsync(Path.Combine(targetRoot, "settings.json"), "{}");
        try
        {
            var junctionResult = await RunPowerShellCommandAsync(
                $"New-Item -ItemType Junction -Path '{QuotePowerShell(junctionPath)}' -Target '{QuotePowerShell(targetRoot)}' | Out-Null");
            Assert.AreEqual(0, junctionResult.ExitCode, junctionResult.CombinedOutput);

            var helperPath = Path.Combine(root, "scripts", "release-validation.ps1");
            var command =
                $". '{QuotePowerShell(helperPath)}'; "
                + $"Assert-NoReparsePointInControlledPath -Root '{QuotePowerShell(controlledRoot)}' -Path '{QuotePowerShell(linkedFilePath)}' -Description 'Fixture path'";
            var result = await RunPowerShellCommandAsync(command);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "must not traverse a reparse point");
            StringAssert.Contains(result.CombinedOutput, "linked");
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SensitiveScan_RejectsPayloadAncestorDirectoryReparsePoint()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = CreateTemporaryDirectory();
        var payloadRoot = Path.Combine(temporaryRoot, "payload");
        var targetRoot = Path.Combine(temporaryRoot, "target");
        var junctionPath = Path.Combine(payloadRoot, "linked");
        Directory.CreateDirectory(payloadRoot);
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllTextAsync(Path.Combine(targetRoot, "settings.json"), "{}");
        try
        {
            var junctionResult = await RunPowerShellCommandAsync(
                $"New-Item -ItemType Junction -Path '{QuotePowerShell(junctionPath)}' -Target '{QuotePowerShell(targetRoot)}' | Out-Null");
            Assert.AreEqual(0, junctionResult.ExitCode, junctionResult.CombinedOutput);

            var result = await RunPowerShellFileAsync(
                Path.Combine(root, "scripts", "check-sensitive-files.ps1"),
                "-PayloadRoot", payloadRoot);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "must not contain reparse points");
            StringAssert.Contains(result.CombinedOutput, "linked");
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyDeploy_InvokesVerifiedInternalPublishWithSafeLaunchSmoke()
    {
        var root = FindRepositoryRoot();
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        var entryScript = await File.ReadAllTextAsync(Path.Combine(root, "scripts", "deploy-daily.ps1"));
        var supportScript = await File.ReadAllTextAsync(Path.Combine(root, "scripts", "daily-deploy-support.ps1"));
        try
        {
            var fakePublish = Path.Combine(temporaryRoot, "fake-publish.ps1");
            await File.WriteAllTextAsync(
                fakePublish,
                "param([string]$Version,[string]$Deployment,[string]$Channel,[string]$OutputRoot,"
                + "[switch]$AllowDirty,[switch]$DirectoryOnly,[switch]$SafeLaunchSmoke)\r\n"
                + "[pscustomobject]@{Version=$Version;Deployment=$Deployment;Channel=$Channel;OutputRoot=$OutputRoot;"
                + "AllowDirty=$AllowDirty.IsPresent;DirectoryOnly=$DirectoryOnly.IsPresent;"
                + "SafeLaunchSmoke=$SafeLaunchSmoke.IsPresent} | ConvertTo-Json -Compress\r\n");
            var helperPath = Path.Combine(root, "scripts", "daily-deploy-support.ps1");
            var outputRoot = Path.Combine(temporaryRoot, "output");
            var command =
                $". '{QuotePowerShell(helperPath)}'; Invoke-DailyReleasePublish "
                + $"-PublishScript '{QuotePowerShell(fakePublish)}' -Version 'daily-fixture' "
                + $"-OutputRoot '{QuotePowerShell(outputRoot)}'";
            var result = await RunPowerShellCommandAsync(command);

            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var invocation = document.RootElement;
            Assert.AreEqual("SelfContained", invocation.GetProperty("Deployment").GetString());
            Assert.AreEqual("Internal", invocation.GetProperty("Channel").GetString());
            Assert.IsTrue(invocation.GetProperty("AllowDirty").GetBoolean());
            Assert.IsTrue(invocation.GetProperty("DirectoryOnly").GetBoolean());
            Assert.IsTrue(invocation.GetProperty("SafeLaunchSmoke").GetBoolean());
            StringAssert.Contains(entryScript, "Open-DailyDeploymentLock");
            StringAssert.Contains(entryScript, "Invoke-DailyReleasePublish");
            StringAssert.Contains(entryScript, "Invoke-DailyBuildTransaction");
            StringAssert.Contains(supportScript, "Get-Process -Name \"EmbyPlayer.App\"");
            Assert.IsFalse(entryScript.Contains("D:\\", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task SafeLaunchSmoke_LeavesUnresponsiveProcessAliveAndReportsItsPid()
    {
        var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "release-validation.ps1");
        var command =
            $". '{QuotePowerShell(helperPath)}'; "
            + "$child=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe') "
            + "-ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') -WindowStyle Hidden -PassThru; "
            + "try { try { Complete-ReleaseLaunchSmokeProcess -Process $child -AllowForceTermination $false; "
            + "throw 'safe cleanup unexpectedly succeeded' } catch { $message=$_.Exception.Message; $child.Refresh(); "
            + "if($child.HasExited){ throw 'safe cleanup terminated the child process' }; "
            + "Write-Output ('SAFE-ALIVE:'+$child.Id+'|'+$message) } } finally { "
            + "$child.Refresh(); if(-not $child.HasExited){ $child.Kill(); $child.WaitForExit() }; $child.Dispose() }";
        var result = await RunPowerShellCommandAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.StandardOutput, "SAFE-ALIVE:");
        StringAssert.Contains(result.StandardOutput, "was not force-terminated");
    }

    [TestMethod]
    public async Task SafeLaunchSmoke_CallbackFailureForceCleansOwnedProcessAndReportsSafeError()
    {
        var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "release-validation.ps1");
        var command =
            $". '{QuotePowerShell(helperPath)}'; "
            + "$child=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe') "
            + "-ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') -WindowStyle Hidden -PassThru; "
            + "try { try { Complete-ReleaseLaunchSmokeProcess -Process $child -AllowForceTermination $true "
            + "-CloseAppWindow { throw 'DO-NOT-LEAK' }; throw 'cleanup unexpectedly succeeded' } "
            + "catch { $message=$_.Exception.Message; $child.Refresh(); if(-not $child.HasExited){ throw 'owned process remained alive' }; "
            + "Write-Output ('FORCE-CLEANED:'+$child.Id+'|'+$message) } } finally { $child.Dispose() }";
        var result = await RunPowerShellCommandAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.StandardOutput, "FORCE-CLEANED:");
        StringAssert.Contains(result.StandardOutput, "failed its normal window close request");
        Assert.IsFalse(result.CombinedOutput.Contains("DO-NOT-LEAK", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SafeLaunchSmoke_CallbackFalseDoesNotForceTerminateWhenDisallowed()
    {
        var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "release-validation.ps1");
        var command =
            $". '{QuotePowerShell(helperPath)}'; "
            + "$child=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe') "
            + "-ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') -WindowStyle Hidden -PassThru; "
            + "try { try { Complete-ReleaseLaunchSmokeProcess -Process $child -AllowForceTermination $false "
            + "-CloseAppWindow { return $false }; throw 'safe cleanup unexpectedly succeeded' } "
            + "catch { $message=$_.Exception.Message; $child.Refresh(); if($child.HasExited){ throw 'safe cleanup terminated the child process' }; "
            + "Write-Output ('SAFE-ALIVE:'+$child.Id+'|'+$message) } } finally { "
            + "$child.Refresh(); if(-not $child.HasExited){ $child.Kill(); $child.WaitForExit() }; $child.Dispose() }";
        var result = await RunPowerShellCommandAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.StandardOutput, "SAFE-ALIVE:");
        StringAssert.Contains(result.StandardOutput, "failed normal close request");
        StringAssert.Contains(result.StandardOutput, "was not force-terminated");
    }

    [TestMethod]
    public async Task SafeLaunchSmoke_SuccessfulCallbackKeepsNormalPathUnchanged()
    {
        var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "release-validation.ps1");
        var command =
            $". '{QuotePowerShell(helperPath)}'; "
            + "$child=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe') "
            + "-ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') -WindowStyle Hidden -PassThru; "
            + "try { Complete-ReleaseLaunchSmokeProcess -Process $child -AllowForceTermination $false "
            + "-CloseAppWindow { $child.Kill() }; $child.Refresh(); if(-not $child.HasExited){ throw 'normal cleanup left process alive' }; "
            + "Write-Output ('NORMAL-CLOSED:'+$child.Id) } finally { "
            + "$child.Refresh(); if(-not $child.HasExited){ $child.Kill(); $child.WaitForExit() }; $child.Dispose() }";
        var result = await RunPowerShellCommandAsync(command);

        Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        StringAssert.Contains(result.StandardOutput, "NORMAL-CLOSED:");
    }

    [TestMethod]
    public async Task DailyDeploymentLock_IsExclusiveAndRejectsReparseInstallRoot()
    {
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        var installRoot = Path.Combine(temporaryRoot, "install");
        var linkedParent = Path.Combine(temporaryRoot, "linked-parent");
        var linkedInstall = Path.Combine(linkedParent, "child-install");
        try
        {
            var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
            var holderStart = CreatePowerShellStartInfo();
            holderStart.ArgumentList.Add("-Command");
            holderStart.ArgumentList.Add(
                "$ErrorActionPreference='Stop'; "
                + $". '{QuotePowerShell(helperPath)}'; "
                + $"$lock=Open-DailyDeploymentLock -InstallRoot '{QuotePowerShell(installRoot)}'; "
                + "try { Write-Output 'LOCKED'; [Console]::Out.Flush(); Start-Sleep -Seconds 8 } "
                + "finally { $lock.Dispose() }");
            using var holder = Process.Start(holderStart)
                ?? throw new InvalidOperationException("Unable to start daily lock holder.");
            try
            {
                var readyLine = await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual("LOCKED", readyLine);
                var stopwatch = Stopwatch.StartNew();
                var contender = await RunPowerShellCommandAsync(
                    $". '{QuotePowerShell(helperPath)}'; "
                    + $"$lock=Open-DailyDeploymentLock -InstallRoot '{QuotePowerShell(installRoot)}'; $lock.Dispose()");
                stopwatch.Stop();

                Assert.AreNotEqual(0, contender.ExitCode);
                StringAssert.Contains(contender.CombinedOutput, "Another daily deployment is already using this InstallRoot");
                Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3), contender.CombinedOutput);
                await holder.WaitForExitAsync();
                Assert.AreEqual(0, holder.ExitCode, await holder.StandardError.ReadToEndAsync());
                Assert.IsTrue(File.Exists(Path.Combine(installRoot, ".daily-deploy.lock")));
            }
            finally
            {
                if (!holder.HasExited)
                {
                    holder.Kill(entireProcessTree: true);
                    await holder.WaitForExitAsync();
                }
            }

            var targetRoot = Path.Combine(temporaryRoot, "linked-target");
            Directory.CreateDirectory(targetRoot);
            var junctionResult = await RunPowerShellCommandAsync(
                $"New-Item -ItemType Junction -Path '{QuotePowerShell(linkedParent)}' -Target '{QuotePowerShell(targetRoot)}' | Out-Null");
            Assert.AreEqual(0, junctionResult.ExitCode, junctionResult.CombinedOutput);
            var reparseResult = await RunPowerShellCommandAsync(
                $". '{QuotePowerShell(helperPath)}'; "
                + $"$lock=Open-DailyDeploymentLock -InstallRoot '{QuotePowerShell(linkedInstall)}'; $lock.Dispose()");
            Assert.AreNotEqual(0, reparseResult.ExitCode);
            StringAssert.Contains(reparseResult.CombinedOutput, "must not traverse a reparse point");
        }
        finally
        {
            if (Directory.Exists(linkedParent))
            {
                Directory.Delete(linkedParent);
            }

            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyPromotion_FirstDeployAndSecondDeployKeepOnlyCurrentAndPrevious()
    {
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        try
        {
            const string commit = "fixture-commit";
            var payloadOne = Path.Combine(temporaryRoot, "payload-one");
            var payloadTwo = Path.Combine(temporaryRoot, "payload-two");
            var installRoot = Path.Combine(temporaryRoot, "install");
            await CreateDailyPayloadAsync(payloadOne, "one", commit);
            await CreateDailyPayloadAsync(payloadTwo, "two", commit);

            var firstResult = await PromoteDailyPayloadAsync(payloadOne, installRoot, commit);
            Assert.AreEqual(0, firstResult.ExitCode, firstResult.CombinedOutput);
            Assert.AreEqual("one", await File.ReadAllTextAsync(Path.Combine(installRoot, "current", "build-label.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(installRoot, "previous")));

            var secondResult = await PromoteDailyPayloadAsync(payloadTwo, installRoot, commit);
            Assert.AreEqual(0, secondResult.ExitCode, secondResult.CombinedOutput);
            Assert.AreEqual("two", await File.ReadAllTextAsync(Path.Combine(installRoot, "current", "build-label.txt")));
            Assert.AreEqual("one", await File.ReadAllTextAsync(Path.Combine(installRoot, "previous", "build-label.txt")));
            Assert.AreEqual(0, EnumerateDailyTransactionDirectories(installRoot).Count());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyPromotion_SecondMoveFailureRestoresOriginalCurrent()
    {
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        try
        {
            const string commit = "fixture-commit";
            var payloadOne = Path.Combine(temporaryRoot, "payload-one");
            var payloadTwo = Path.Combine(temporaryRoot, "payload-two");
            var installRoot = Path.Combine(temporaryRoot, "install");
            await CreateDailyPayloadAsync(payloadOne, "one", commit);
            await CreateDailyPayloadAsync(payloadTwo, "two", commit);
            var firstResult = await PromoteDailyPayloadAsync(payloadOne, installRoot, commit);
            Assert.AreEqual(0, firstResult.ExitCode, firstResult.CombinedOutput);

            var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
            var command =
                $". '{QuotePowerShell(helperPath)}'; "
                + IsolateDailyFixtureProcessProbe
                + "$script:moveCount=0; $move={ param($source,$destination) "
                + "$script:moveCount++; if($script:moveCount -eq 2){ throw 'simulated second move failure' }; "
                + "Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop }; "
                + $"Invoke-DailyPayloadPromotion -PayloadRoot '{QuotePowerShell(payloadTwo)}' "
                + $"-InstallRoot '{QuotePowerShell(installRoot)}' -ExpectedCommit '{commit}' -MoveDirectory $move";
            var failedResult = await RunPowerShellCommandAsync(command);

            Assert.AreNotEqual(0, failedResult.ExitCode);
            StringAssert.Contains(failedResult.CombinedOutput, "simulated second move failure");
            Assert.AreEqual("one", await File.ReadAllTextAsync(Path.Combine(installRoot, "current", "build-label.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(installRoot, "previous")));
            Assert.AreEqual(0, EnumerateDailyTransactionDirectories(installRoot).Count());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public async Task DailyPromotion_ThirdDeployLateMoveFailureRestoresHealthyPair(int failingMove)
    {
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        try
        {
            const string commit = "fixture-commit";
            var payloadOne = Path.Combine(temporaryRoot, "payload-one");
            var payloadTwo = Path.Combine(temporaryRoot, "payload-two");
            var payloadThree = Path.Combine(temporaryRoot, "payload-three");
            var installRoot = Path.Combine(temporaryRoot, "install");
            await CreateDailyPayloadAsync(payloadOne, "one", commit);
            await CreateDailyPayloadAsync(payloadTwo, "two", commit);
            await CreateDailyPayloadAsync(payloadThree, "three", commit);
            Assert.AreEqual(0, (await PromoteDailyPayloadAsync(payloadOne, installRoot, commit)).ExitCode);
            Assert.AreEqual(0, (await PromoteDailyPayloadAsync(payloadTwo, installRoot, commit)).ExitCode);

            var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
            var command =
                $". '{QuotePowerShell(helperPath)}'; "
                + IsolateDailyFixtureProcessProbe
                + "$script:moveCount=0; $move={ param($source,$destination) $script:moveCount++; "
                + $"if($script:moveCount -eq {failingMove}){{ throw 'simulated move {failingMove} failure' }}; "
                + "Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop }; "
                + $"Invoke-DailyPayloadPromotion -PayloadRoot '{QuotePowerShell(payloadThree)}' "
                + $"-InstallRoot '{QuotePowerShell(installRoot)}' -ExpectedCommit '{commit}' -MoveDirectory $move";
            var result = await RunPowerShellCommandAsync(command);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, $"simulated move {failingMove} failure");
            Assert.AreEqual("two", await File.ReadAllTextAsync(Path.Combine(installRoot, "current", "build-label.txt")));
            Assert.AreEqual("one", await File.ReadAllTextAsync(Path.Combine(installRoot, "previous", "build-label.txt")));
            Assert.AreEqual(0, EnumerateDailyTransactionDirectories(installRoot).Count());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyPromotion_OldPreviousCleanupFailureKeepsCommittedHealthyPrevious()
    {
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        try
        {
            const string commit = "fixture-commit";
            var payloadOne = Path.Combine(temporaryRoot, "payload-one");
            var payloadTwo = Path.Combine(temporaryRoot, "payload-two");
            var payloadThree = Path.Combine(temporaryRoot, "payload-three");
            var installRoot = Path.Combine(temporaryRoot, "install");
            await CreateDailyPayloadAsync(payloadOne, "one", commit);
            await CreateDailyPayloadAsync(payloadTwo, "two", commit);
            await CreateDailyPayloadAsync(payloadThree, "three", commit);
            Assert.AreEqual(0, (await PromoteDailyPayloadAsync(payloadOne, installRoot, commit)).ExitCode);
            Assert.AreEqual(0, (await PromoteDailyPayloadAsync(payloadTwo, installRoot, commit)).ExitCode);

            var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
            var command =
                $". '{QuotePowerShell(helperPath)}'; "
                + IsolateDailyFixtureProcessProbe
                + "$remove={ param($path,$controlledRoot,$allowedPrefixes) "
                + "if(([IO.Path]::GetFileName($path)).StartsWith('.previous-old-',[StringComparison]::Ordinal)){ "
                + "Remove-Item -LiteralPath (Join-Path $path 'build-label.txt') -Force; throw 'simulated partial oldest cleanup failure' }; "
                + "Remove-DailyOwnedDirectory -Path $path -ControlledRoot $controlledRoot -AllowedPrefixes $allowedPrefixes }; "
                + $"Invoke-DailyPayloadPromotion -PayloadRoot '{QuotePowerShell(payloadThree)}' "
                + $"-InstallRoot '{QuotePowerShell(installRoot)}' -ExpectedCommit '{commit}' -RemoveDirectory $remove";
            var result = await RunPowerShellCommandAsync(command);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.CombinedOutput, "committed a healthy current and previous");
            StringAssert.Contains(result.CombinedOutput, "simulated partial oldest cleanup failure");
            Assert.AreEqual("three", await File.ReadAllTextAsync(Path.Combine(installRoot, "current", "build-label.txt")));
            Assert.AreEqual("two", await File.ReadAllTextAsync(Path.Combine(installRoot, "previous", "build-label.txt")));
            var retainedOld = Directory.EnumerateDirectories(installRoot, ".previous-old-*").Single();
            Assert.IsFalse(File.Exists(Path.Combine(retainedOld, "build-label.txt")));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyBuildTransaction_CleansItsUniqueRootAfterSuccessAndFailure()
    {
        var temporaryWorkspace = CreateWorkspaceDailyTestDirectory();
        try
        {
            var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
            var dailyBase = Path.Combine(temporaryWorkspace, ".tmp", "daily-deploy");
            var successCommand =
                $". '{QuotePowerShell(helperPath)}'; "
                + $"$dailyResult = @(Invoke-DailyBuildTransaction -WorkspaceRoot '{QuotePowerShell(temporaryWorkspace)}' -Operation {{ param($transactionRoot) "
                + "Set-Content -LiteralPath (Join-Path $transactionRoot 'fixture.txt') -Value 'ok'; "
                + "'fixture build output'; [pscustomobject]@{CurrentPath='fixture-current'} }); "
                + "if ($dailyResult.Count -ne 2) { throw 'operation output changed' }; "
                + "($dailyResult | Where-Object { $null -ne $_.PSObject.Properties['CurrentPath'] }).CurrentPath";
            var successResult = await RunPowerShellCommandAsync(successCommand);
            Assert.AreEqual(0, successResult.ExitCode, successResult.CombinedOutput);
            StringAssert.Contains(successResult.StandardOutput, "fixture build output");
            StringAssert.Contains(successResult.StandardOutput, "fixture-current");
            Assert.AreEqual(0, EnumerateDailyTransactionDirectories(dailyBase, ".daily-").Count());

            var failureCommand =
                $". '{QuotePowerShell(helperPath)}'; "
                + $"$dailyResult = Invoke-DailyBuildTransaction -WorkspaceRoot '{QuotePowerShell(temporaryWorkspace)}' -Operation {{ param($transactionRoot) "
                + "Set-Content -LiteralPath (Join-Path $transactionRoot 'fixture.txt') -Value 'fail'; "
                + "'Failed Fixture.NamedTest'; 'Expected 2 but was 1'; throw 'fixture build failure' }";
            var failureResult = await RunPowerShellCommandAsync(failureCommand);
            Assert.AreNotEqual(0, failureResult.ExitCode);
            StringAssert.Contains(failureResult.StandardOutput, "Failed Fixture.NamedTest");
            StringAssert.Contains(failureResult.StandardOutput, "Expected 2 but was 1");
            StringAssert.Contains(failureResult.CombinedOutput, "fixture build failure");
            Assert.AreEqual(0, EnumerateDailyTransactionDirectories(dailyBase, ".daily-").Count());
        }
        finally
        {
            Directory.Delete(temporaryWorkspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyPromotion_RejectsPdbInvalidManifestReparseAndFilesystemRoot()
    {
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        var junctionPath = Path.Combine(temporaryRoot, "payload-reparse", "linked");
        try
        {
            const string commit = "fixture-commit";
            var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
            var installRoot = Path.Combine(temporaryRoot, "install");
            var payloadPdb = Path.Combine(temporaryRoot, "payload-pdb");
            await CreateDailyPayloadAsync(payloadPdb, "pdb", commit);
            await File.WriteAllTextAsync(Path.Combine(payloadPdb, "unexpected.pdb"), "fixture");
            var pdbResult = await PromoteDailyPayloadAsync(payloadPdb, installRoot, commit);
            Assert.AreNotEqual(0, pdbResult.ExitCode);
            StringAssert.Contains(pdbResult.CombinedOutput, "must not contain PDB files");

            var payloadInvalid = Path.Combine(temporaryRoot, "payload-invalid");
            await CreateDailyPayloadAsync(payloadInvalid, "invalid", commit);
            await File.WriteAllTextAsync(Path.Combine(payloadInvalid, "release-manifest.json"), "{");
            var invalidResult = await PromoteDailyPayloadAsync(payloadInvalid, installRoot, commit);
            Assert.AreNotEqual(0, invalidResult.ExitCode);
            StringAssert.Contains(invalidResult.CombinedOutput, "manifest is invalid JSON");

            var payloadReparse = Path.Combine(temporaryRoot, "payload-reparse");
            var reparseTarget = Path.Combine(temporaryRoot, "reparse-target");
            await CreateDailyPayloadAsync(payloadReparse, "reparse", commit);
            Directory.CreateDirectory(reparseTarget);
            var junctionResult = await RunPowerShellCommandAsync(
                $"New-Item -ItemType Junction -Path '{QuotePowerShell(junctionPath)}' -Target '{QuotePowerShell(reparseTarget)}' | Out-Null");
            Assert.AreEqual(0, junctionResult.ExitCode, junctionResult.CombinedOutput);
            var reparseResult = await PromoteDailyPayloadAsync(payloadReparse, installRoot, commit);
            Assert.AreNotEqual(0, reparseResult.ExitCode);
            StringAssert.Contains(reparseResult.CombinedOutput, "must not contain reparse points");

            var filesystemRoot = Path.GetPathRoot(temporaryRoot)!;
            var rootCommand =
                $". '{QuotePowerShell(helperPath)}'; "
                + $"Invoke-DailyPayloadPromotion -PayloadRoot '{QuotePowerShell(payloadPdb)}' "
                + $"-InstallRoot '{QuotePowerShell(filesystemRoot)}' -ExpectedCommit '{commit}'";
            var rootResult = await RunPowerShellCommandAsync(rootCommand);
            Assert.AreNotEqual(0, rootResult.ExitCode);
            StringAssert.Contains(rootResult.CombinedOutput, "must not be a filesystem root");
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task DailyPromotion_RejectsStableReparsePathsAndPayloadInstallContainment()
    {
        var temporaryRoot = CreateWorkspaceDailyTestDirectory();
        var installRoot = Path.Combine(temporaryRoot, "stable-install");
        var currentPath = Path.Combine(installRoot, "current");
        var previousPath = Path.Combine(installRoot, "previous");
        try
        {
            const string commit = "fixture-commit";
            var payload = Path.Combine(temporaryRoot, "payload");
            var junctionTarget = Path.Combine(temporaryRoot, "junction-target");
            await CreateDailyPayloadAsync(payload, "safe", commit);
            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(junctionTarget);

            var currentJunction = await RunPowerShellCommandAsync(
                $"New-Item -ItemType Junction -Path '{QuotePowerShell(currentPath)}' -Target '{QuotePowerShell(junctionTarget)}' | Out-Null");
            Assert.AreEqual(0, currentJunction.ExitCode, currentJunction.CombinedOutput);
            var currentResult = await PromoteDailyPayloadAsync(payload, installRoot, commit);
            Assert.AreNotEqual(0, currentResult.ExitCode);
            StringAssert.Contains(currentResult.CombinedOutput, "stable path must be a normal directory");
            Directory.Delete(currentPath);

            Directory.CreateDirectory(currentPath);
            var previousJunction = await RunPowerShellCommandAsync(
                $"New-Item -ItemType Junction -Path '{QuotePowerShell(previousPath)}' -Target '{QuotePowerShell(junctionTarget)}' | Out-Null");
            Assert.AreEqual(0, previousJunction.ExitCode, previousJunction.CombinedOutput);
            var previousResult = await PromoteDailyPayloadAsync(payload, installRoot, commit);
            Assert.AreNotEqual(0, previousResult.ExitCode);
            StringAssert.Contains(previousResult.CombinedOutput, "stable path must be a normal directory");
            Directory.Delete(previousPath);

            var childInstallResult = await PromoteDailyPayloadAsync(
                payload,
                Path.Combine(payload, "nested-install"),
                commit);
            Assert.AreNotEqual(0, childInstallResult.ExitCode);
            StringAssert.Contains(childInstallResult.CombinedOutput, "must not contain one another");

            var containingInstall = Path.Combine(temporaryRoot, "containing-install");
            var nestedPayload = Path.Combine(containingInstall, "payload");
            await CreateDailyPayloadAsync(nestedPayload, "nested", commit);
            var containingResult = await PromoteDailyPayloadAsync(nestedPayload, containingInstall, commit);
            Assert.AreNotEqual(0, containingResult.ExitCode);
            StringAssert.Contains(containingResult.CombinedOutput, "must not contain one another");
        }
        finally
        {
            if (Directory.Exists(currentPath) &&
                (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(currentPath);
            }

            if (Directory.Exists(previousPath) &&
                (File.GetAttributes(previousPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(previousPath);
            }

            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task BuildTestScript_SupportsReleaseIsolatedArtifactsAndNativeFailureChecks()
    {
        var root = FindRepositoryRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "scripts", "build-test.ps1"));

        StringAssert.Contains(script, "ValidateSet(\"Debug\", \"Release\")");
        StringAssert.Contains(script, "ArtifactsPath");
        StringAssert.Contains(script, "--artifacts-path");
        StringAssert.Contains(script, "Assert-NativeSuccess");
    }

    [TestMethod]
    public async Task RepositoryPinsSdkAndEnablesPerProjectLockFiles()
    {
        var root = FindRepositoryRoot();
        using var globalJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "global.json")));
        var props = await File.ReadAllTextAsync(Path.Combine(root, "Directory.Build.props"));

        Assert.AreEqual("10.0.303", globalJson.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        Assert.AreEqual("latestPatch", globalJson.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString());
        StringAssert.Contains(props, "<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>");
        StringAssert.Contains(props, "packages.$(RuntimeIdentifier).lock.json");
    }

    private static Task<CommandResult> RunPowerShellFileAsync(string scriptPath, params string[] arguments)
    {
        return RunPowerShellFileAsync(scriptPath, environment: null, arguments);
    }

    private static async Task<CommandResult> RunPowerShellFileAsync(
        string scriptPath,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var startInfo = CreatePowerShellStartInfo();
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return await RunProcessAsync(startInfo);
    }

    private static Task<CommandResult> RunPowerShellCommandAsync(string command)
    {
        var startInfo = CreatePowerShellStartInfo();
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$ErrorActionPreference='Stop'; " + command);
        return RunProcessAsync(startInfo);
    }

    private static ProcessStartInfo CreatePowerShellStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        return startInfo;
    }

    private static async Task<CommandResult> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start PowerShell test process.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "emby-release-pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateWorkspaceDailyTestDirectory()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            ".tmp",
            "daily-deploy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task CreateDailyPayloadAsync(string payloadRoot, string label, string commit)
    {
        Directory.CreateDirectory(payloadRoot);
        var requiredFiles = new[]
        {
            "EmbyPlayer.App.exe",
            "EmbyPlayer.App.dll",
            "EmbyPlayer.Core.dll",
            "EmbyPlayer.Emby.dll",
            "EmbyPlayer.Player.dll",
            "EmbyPlayer.UI.dll",
            "libmpv-2.dll",
            "mpv-runtime.json"
        };
        foreach (var fileName in requiredFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(payloadRoot, fileName), $"{fileName}:{label}");
        }

        await File.WriteAllTextAsync(Path.Combine(payloadRoot, "build-label.txt"), label);
        var entries = Directory
            .EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                relativePath = Path.GetRelativePath(payloadRoot, path).Replace('\\', '/'),
                sizeBytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            })
            .OrderBy(entry => entry.relativePath, StringComparer.Ordinal)
            .ToArray();
        var manifest = new
        {
            schemaVersion = 1,
            version = "daily-fixture",
            channel = "Internal",
            deployment = "SelfContained",
            selfContained = true,
            distributionReady = false,
            testsSkipped = false,
            git = new { commit, workingTreeDirty = false },
            launchSmoke = new { status = "passed", visibleTopLevelWindow = true },
            files = entries
        };
        await File.WriteAllTextAsync(
            Path.Combine(payloadRoot, "release-manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    // Transaction fixtures contain text files, never a running player. Keep user processes
    // outside these filesystem tests; the production process guard has its own test above.
    private const string IsolateDailyFixtureProcessProbe = "function Assert-DailyPlayerNotRunning { }; ";

    private static Task<CommandResult> PromoteDailyPayloadAsync(
        string payloadRoot,
        string installRoot,
        string commit)
    {
        var helperPath = Path.Combine(FindRepositoryRoot(), "scripts", "daily-deploy-support.ps1");
        var command =
            $". '{QuotePowerShell(helperPath)}'; "
            + IsolateDailyFixtureProcessProbe
            + $"Invoke-DailyPayloadPromotion -PayloadRoot '{QuotePowerShell(payloadRoot)}' "
            + $"-InstallRoot '{QuotePowerShell(installRoot)}' -ExpectedCommit '{QuotePowerShell(commit)}' | Out-Null";
        return RunPowerShellCommandAsync(command);
    }

    private static IEnumerable<string> EnumerateDailyTransactionDirectories(
        string root,
        string? exactPrefix = null)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var prefixes = exactPrefix is null
            ? new[] { ".next-", ".rollback-", ".previous-old-" }
            : new[] { exactPrefix };
        return Directory
            .EnumerateDirectories(root)
            .Where(path => prefixes.Any(prefix =>
                Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static string QuotePowerShell(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static Task<string> ReadPublishScriptAsync()
    {
        return File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "scripts", "publish-release.ps1"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbyPlayer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
    }
}
