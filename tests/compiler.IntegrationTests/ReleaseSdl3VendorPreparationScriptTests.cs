using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseSdl3VendorPreparationScriptTests
{
    private const string Recipe = "scripts/prepare-sdl3-vendor-release-input.ps1";
    private const string SourceSha256 = "12b34280415ec8418c864408b93d008a20a6530687ee613d60bfbd20411f2785";

    [Fact]
    public void CatalogPinsOfficialSourceToolsFeaturesAndNativeLinkFacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "vendor-packages.json")));
        var package = document.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Single(static item => item.GetProperty("id").GetString() == "Vendor.SDL3");

        Assert.Equal("3.4.10", package.GetProperty("version").GetString());
        Assert.Equal("archive:SDL3-3.4.10.tar.gz", package.GetProperty("sourceIdentity").GetString());
        Assert.Equal("https://github.com/libsdl-org/SDL/releases/download/release-3.4.10/SDL3-3.4.10.tar.gz", package.GetProperty("sourceUrl").GetString());
        Assert.Equal(SourceSha256, package.GetProperty("sourceSha256").GetString());
        Assert.Equal(15_606_216, package.GetProperty("sourceSize").GetInt64());
        Assert.Equal("SDL3-3.4.10", package.GetProperty("sourcePayloadRoot").GetString());
        Assert.Equal(1_780_249_121, package.GetProperty("sourceDateEpoch").GetInt64());
        Assert.Equal("LICENSE.txt", package.GetProperty("sourceLicensePath").GetString());
        Assert.Equal(Recipe, package.GetProperty("buildRecipe").GetString());

        var files = package.GetProperty("sourceArchiveFiles")
            .EnumerateArray()
            .ToDictionary(static item => item.GetProperty("path").GetString()!, StringComparer.Ordinal);
        AssertSourceFile(files, "CMakeLists.txt", 147_423, "dfee8b830d8b80456a71bd5e91f69c2efac391024d8f65adb6e8714ae694298c");
        AssertSourceFile(files, "LICENSE.txt", 884, "1c040b8271b37e5076359f8fd54240e371114112924d2df81ef87c7d6a1dfdfd");
        AssertSourceFile(files, "include/SDL3/SDL.h", 2_986, "99cfa6d497d1f2bbf973ad0581333e9895f28737b50d4d0cc2d22cdd40c4d11d");

        var tools = package.GetProperty("buildTools");
        Assert.Equal("3.31.6", tools.GetProperty("cmake").GetProperty("version").GetString());
        Assert.Equal("1.12.1", tools.GetProperty("ninja").GetProperty("version").GetString());
        Assert.Equal(
            "workflow-must-provide-reviewed-pinned-binary",
            tools.GetProperty("cmake").GetProperty("acquisition").GetString());
        Assert.Equal(
            "workflow-must-provide-reviewed-pinned-binary",
            tools.GetProperty("ninja").GetProperty("acquisition").GetString());

        var build = package.GetProperty("sourceBuildOptions");
        Assert.Equal("Release", build.GetProperty("configuration").GetString());
        Assert.Equal("O3", build.GetProperty("optimization").GetString());
        Assert.Equal("thin", build.GetProperty("lto").GetString());
        Assert.True(build.GetProperty("deterministicArchive").GetBoolean());
        Assert.False(build.GetProperty("pkgConfigDiscovery").GetBoolean());
        Assert.True(build.GetProperty("adapterCompiledIntoNativeArchive").GetBoolean());
        var commonOptions = Strings(build.GetProperty("commonCmakeOptions")).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SDL_STATIC=ON", commonOptions);
        Assert.Contains("SDL_SHARED=OFF", commonOptions);
        Assert.Contains("SDL_ASSERTIONS=disabled", commonOptions);
        Assert.Contains("SDL_GPU=ON", commonOptions);
        Assert.Contains("SDL_RENDER_GPU=ON", commonOptions);
        Assert.Contains("SDL_VULKAN=ON", commonOptions);

        foreach (var target in new[] { "linux-x64", "windows-x64", "macos-arm64" })
        {
            Assert.Equal("required-source-build", package.GetProperty("targetSupport").GetProperty(target).GetString());
            Assert.NotEmpty(build.GetProperty("targetCmakeOptions").GetProperty(target).EnumerateArray());
            Assert.NotEmpty(package.GetProperty("requiredBuildDefines").GetProperty(target).EnumerateArray());
            Assert.Equal("SDL3", package.GetProperty("nativeLinkFacts").GetProperty(target)
                .GetProperty("libraries")[0].GetString());
        }

        var macLinks = package.GetProperty("nativeLinkFacts").GetProperty("macos-arm64");
        Assert.Equal(["SDL3", "m"], Strings(macLinks.GetProperty("libraries")));
        var macArguments = Strings(macLinks.GetProperty("linkArguments"));
        Assert.Contains("-lpthread", macArguments);
        Assert.Contains("-weak_framework", macArguments);
        Assert.Contains("UniformTypeIdentifiers", macArguments);
        Assert.Contains("ForceFeedback", macArguments);
        Assert.Contains("Carbon", macArguments);
        Assert.Contains("CoreHaptics", macArguments);
        var macInterface = package.GetProperty("cmakeStaticInterface").GetProperty("macos-arm64");
        var macInterfaceLibraries = Strings(macInterface.GetProperty("libraries"));
        Assert.Contains("framework:ForceFeedback", macInterfaceLibraries);
        Assert.Contains("weak-framework:CoreHaptics", macInterfaceLibraries);
        Assert.Equal(["-lpthread"], Strings(macInterface.GetProperty("linkOptions")));

        var evidence = Assert.Single(package.GetProperty("licenseEvidencePaths").EnumerateArray()).GetString()!;
        var evidenceText = File.ReadAllText(Path.Combine(repositoryRoot, evidence));
        Assert.Contains("This software is provided 'as-is'", evidenceText, StringComparison.Ordinal);
        Assert.Equal(
            files["LICENSE.txt"].GetProperty("sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repositoryRoot, evidence)))));
    }

    [Fact]
    public void ContributorIsHermeticOptimizedAndPrecompilesTheAdapter()
    {
        var script = ReadScript();

        foreach (var parameter in new[]
        {
            "$AssetSuffix", "$TargetTriple", "$OutputVendorRoot", "$StdlibPackageDir",
            "$ToolchainDir", "$CMakePath", "$NinjaPath", "$ContributionManifestPath", "$CacheDir"
        })
        {
            Assert.Contains(parameter, script, StringComparison.Ordinal);
        }

        Assert.Contains("schemaVersion\") -ne 2", script, StringComparison.Ordinal);
        Assert.Contains("payloadKind\") -cne \"stark-compiler-private-backend", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ExternalBuildToolVersion", script, StringComparison.Ordinal);
        Assert.Contains("build requires reviewed $Kind $ExpectedVersion exactly", script, StringComparison.Ordinal);
        Assert.Contains("Expand-CheckedTarGzip", script, StringComparison.Ordinal);
        Assert.Contains("TarEntryType]::RegularFile", script, StringComparison.Ordinal);
        Assert.Contains("duplicate or case-colliding entry", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-WebRequest -Uri $sourceUrl", script, StringComparison.Ordinal);
        Assert.Contains("-DCMAKE_DISABLE_FIND_PACKAGE_PkgConfig=TRUE", script, StringComparison.Ordinal);
        Assert.Contains("\"-O3\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-flto=thin\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-ffunction-sections\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-fdata-sections\"", script, StringComparison.Ordinal);
        Assert.Contains("target_sources(SDL3-static PRIVATE", script, StringComparison.Ordinal);
        Assert.Contains("Sdl3Binding\\.c\\.(?:o|obj)", script, StringComparison.Ordinal);
        Assert.Contains("adapterCompiledIntoNativeArchive = $true", script, StringComparison.Ordinal);
        Assert.Contains("perApplicationNativeSourceCompilation = $false", script, StringComparison.Ordinal);
        Assert.Contains("requiredBuildDefines", script, StringComparison.Ordinal);
        Assert.Contains("silently lost required backend define", script, StringComparison.Ordinal);
        Assert.Contains("cmakeStaticInterfaceLibraries", script, StringComparison.Ordinal);
        Assert.Contains("SDL3 CMake static interface libraries", script, StringComparison.Ordinal);
        Assert.Contains("SDL3 CMake static interface options", script, StringComparison.Ordinal);
        Assert.Contains("optimizationRationale", script, StringComparison.Ordinal);
        Assert.Contains("O3 and ThinLTO preserve whole-program optimization opportunities", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Get-Command cmake", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Command ninja", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Command clang", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--native-pkg-config", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--native-source\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor/build-sdl3-package.sh", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorPreservesRelocatablePackageSystemAndContributionFacts()
    {
        var script = ReadScript();

        Assert.Contains("\"--emit-lib\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--no-stark-path\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--package-profile\", \"release\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--toolchain-dir\", $toolchainRoot", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-include-dir\", $nativeRoot", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-library-dir\", $nativeRoot", script, StringComparison.Ordinal);
        Assert.Contains("native/sdl3", script, StringComparison.Ordinal);
        Assert.Contains("SDL3 package native sources", script, StringComparison.Ordinal);
        Assert.Contains("SDL3 package pkg-config dependencies", script, StringComparison.Ordinal);
        Assert.Contains("does not preserve the staged System API/content identity", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 1", script, StringComparison.Ordinal);
        Assert.Contains("packages = [object[]]@($packageEntry)", script, StringComparison.Ordinal);
        Assert.Contains("nativePayload = [ordered]@{", script, StringComparison.Ordinal);
        Assert.Contains("licenseFiles = [object[]]@($licenseDescriptor)", script, StringComparison.Ordinal);
        Assert.Contains("provenance = $provenanceDescriptor", script, StringComparison.Ordinal);
        Assert.Contains("Sort-ObjectsOrdinalByProperty -Values $artifactDescriptors", script, StringComparison.Ordinal);
        Assert.Contains("[StringComparer]::Ordinal.Compare", script, StringComparison.Ordinal);
        foreach (var kind in new[] { "header", "license", "static-library", "documentation", "provenance" })
        {
            Assert.Contains($"Kind = \"{kind}\"", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RuntimeSmokeUsesOnlyThePublicBundledPackageSurface()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "tests", "fixtures", "release", "SDL3BundledSmoke.stark"));

        Assert.Contains("import Vendor.SDL3", source, StringComparison.Ordinal);
        Assert.Contains("version.Major != 3", source, StringComparison.Ordinal);
        Assert.Contains("Initialize(InitEvents)", source, StringComparison.Ordinal);
        Assert.Contains("WasInitialized(InitEvents)", source, StringComparison.Ordinal);
        Assert.Contains("PushQuitEvent()", source, StringComparison.Ordinal);
        Assert.Contains("PollEvent()", source, StringComparison.Ordinal);
        Assert.Contains("event.Kind != SdlEventKind.Quit", source, StringComparison.Ordinal);
        Assert.Contains("return 0;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorConstrainsOwnedPathsAndCleansItsStableWorkRoot()
    {
        var script = ReadScript();

        Assert.Contains("Assert-SafeOutputRoot -Path $outputRoot", script, StringComparison.Ordinal);
        Assert.Contains("cannot be a filesystem root", script, StringComparison.Ordinal);
        Assert.Contains("must be outside shared OutputVendorRoot", script, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", script, StringComparison.Ordinal);
        Assert.Contains("artifacts/sdl3-work", script, StringComparison.Ordinal);
        Assert.Contains("Enter-Sdl3WorkRootLock", script, StringComparison.Ordinal);
        Assert.Contains("-TimeoutSeconds $WorkLockTimeoutSeconds", script, StringComparison.Ordinal);
        Assert.Contains("Exit-Sdl3WorkRootLock -Lock $workLock", script, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedPath -Root $workParent -Path $workRoot", script, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean unexpected SDL3 work root", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $workRoot -Recurse -Force", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $outputRoot -Recurse", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableWorkRootLockSerializesProcessesReportsOwnerAndReleases()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var testRoot = Path.Combine(Path.GetTempPath(), "stark-sdl3-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var lockPath = Path.Combine(testRoot, ".build.lock");
        var markerPath = Path.Combine(testRoot, "first-acquired");
        var releasePath = Path.Combine(testRoot, "release-first");
        var helperPath = Path.Combine(repositoryRoot, "scripts", "sdl3-work-root-lock.ps1");
        const string command = """
            & {
                param($HelperPath, $LockPath, $OwnerLabel, $MarkerPath, $ReleasePath, [int] $TimeoutSeconds)
                function Assert-NoReparsePointPath { param([string] $Path) }
                . $HelperPath
                $lock = Enter-Sdl3WorkRootLock `
                    -LockPath $LockPath `
                    -OwnerLabel $OwnerLabel `
                    -TargetId "macos-arm64" `
                    -TargetTriple "arm64-apple-macosx11.0.0" `
                    -OutputRoot $LockPath `
                    -TimeoutSeconds $TimeoutSeconds `
                    -PollMilliseconds 50
                try {
                    if ($MarkerPath -cne "-") {
                        [IO.File]::WriteAllText($MarkerPath, "acquired")
                    }
                    if ($ReleasePath -cne "-") {
                        $releaseDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
                        while (-not (Test-Path -LiteralPath $ReleasePath -PathType Leaf)) {
                            if ([DateTimeOffset]::UtcNow -ge $releaseDeadline) {
                                throw "Timed out waiting for the lock-test release marker."
                            }
                            Start-Sleep -Milliseconds 50
                        }
                    }
                } finally {
                    Exit-Sdl3WorkRootLock -Lock $lock
                }
            }
            """;

        Process? first = null;
        try
        {
            first = StartProcess(
                powershell,
                ["-NoProfile", "-NonInteractive", "-Command", command, helperPath, lockPath, "first-owner", markerPath, releasePath, "5"],
                repositoryRoot);
            var firstStdout = first.StandardOutput.ReadToEndAsync();
            var firstStderr = first.StandardError.ReadToEndAsync();
            for (var attempt = 0; attempt < 100 && !File.Exists(markerPath); attempt++)
            {
                await Task.Delay(50);
            }
            Assert.True(File.Exists(markerPath), "The first lock holder did not acquire the lock in time.");

            var second = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-Command", command, helperPath, lockPath, "second-owner", "-", "-", "1"],
                repositoryRoot);
            Assert.NotEqual(0, second.ExitCode);
            Assert.Contains("Timed out after 1 seconds", second.Stderr, StringComparison.Ordinal);
            Assert.Contains("first-owner", second.Stderr, StringComparison.Ordinal);
            Assert.Contains("\"pid\"", second.Stderr, StringComparison.Ordinal);

            await File.WriteAllTextAsync(releasePath, "release");
            await first.WaitForExitAsync();
            Assert.True(first.ExitCode == 0, (await firstStdout) + (await firstStderr));
            first.Dispose();
            first = null;

            var third = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-Command", command, helperPath, lockPath, "third-owner", "-", "-", "1"],
                repositoryRoot);
            Assert.True(third.ExitCode == 0, third.Stderr);
        }
        finally
        {
            if (first is not null)
            {
                if (!first.HasExited)
                {
                    first.Kill(entireProcessTree: true);
                    await first.WaitForExitAsync();
                }
                first.Dispose();
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContributorParsesWhenPowerShellIsAvailable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        const string parserCommand = """
            & {
                param([string] $ScriptPath)
                $tokens = $null
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) {
                    $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }
                    exit 1
                }
            }
            """;
        var result = await RunProcessAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-Command", parserCommand, Path.Combine(repositoryRoot, Recipe)],
            repositoryRoot);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private static void AssertSourceFile(
        IReadOnlyDictionary<string, JsonElement> files,
        string path,
        long bytes,
        string sha256)
    {
        Assert.Equal(bytes, files[path].GetProperty("bytes").GetInt64());
        Assert.Equal(sha256, files[path].GetProperty("sha256").GetString());
    }

    private static string[] Strings(JsonElement value)
        => value.EnumerateArray().Select(static item => item.GetString()!).ToArray();

    private static string ReadScript()
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Recipe));

    private static async Task<string?> FindPowerShellAsync(string workingDirectory)
    {
        var explicitPowerShell = Environment.GetEnvironmentVariable("POWERSHELL_EXE");
        var candidates = OperatingSystem.IsWindows()
            ? new[] { explicitPowerShell, "pwsh.exe", "powershell.exe" }
            : new[] { explicitPowerShell, "pwsh" };
        foreach (var candidateValue in candidates.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = candidateValue!;
            try
            {
                var result = await RunProcessAsync(
                    candidate,
                    ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"],
                    workingDirectory);
                if (result.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Win32Exception)
            {
            }
        }
        return null;
    }

    private static Process StartProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        return process;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        using var process = StartProcess(fileName, arguments, workingDirectory);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
