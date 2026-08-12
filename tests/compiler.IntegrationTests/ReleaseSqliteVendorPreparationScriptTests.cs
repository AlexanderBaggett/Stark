using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseSqliteVendorPreparationScriptTests
{
    private const string Recipe = "scripts/prepare-sqlite-vendor-release-input.ps1";
    private const string SourceSha256 = "8a310d0a16c7a90cacd4c884e70faa51c902afed2a89f63aaa0126ab83558a32";

    [Fact]
    public void CatalogPinsTheOfficialAmalgamationLicenseBuildFeaturesAndEveryEnabledTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "vendor-packages.json")));
        var package = document.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Single(static item => item.GetProperty("id").GetString() == "Vendor.SQLite");

        Assert.Equal("3.53.2", package.GetProperty("version").GetString());
        Assert.Equal("archive:sqlite-amalgamation-3530200.zip", package.GetProperty("sourceIdentity").GetString());
        Assert.Equal("https://sqlite.org/2026/sqlite-amalgamation-3530200.zip", package.GetProperty("sourceUrl").GetString());
        Assert.Equal(SourceSha256, package.GetProperty("sourceSha256").GetString());
        Assert.Equal(2_943_292, package.GetProperty("sourceSize").GetInt64());
        Assert.Equal("sqlite-amalgamation-3530200", package.GetProperty("sourcePayloadRoot").GetString());
        Assert.Equal(1_780_496_820, package.GetProperty("sourceDateEpoch").GetInt64());
        Assert.Equal(Recipe, package.GetProperty("buildRecipe").GetString());

        var sourceFiles = package.GetProperty("sourceArchiveFiles")
            .EnumerateArray()
            .ToDictionary(static item => item.GetProperty("path").GetString()!, StringComparer.Ordinal);
        Assert.Equal(3, sourceFiles.Count);
        AssertSourceFile(sourceFiles, "sqlite3.c", 9_507_037, "0a409f1633283fa31a9126b11fbfd64a1991c5d30defad07e5745d4667f5e23d");
        AssertSourceFile(sourceFiles, "sqlite3.h", 690_725, "9e69a1353a4288450b0d5239ede11fc7f1f4c8e5eb07491fc8317eacb5b7de7e");
        AssertSourceFile(sourceFiles, "sqlite3ext.h", 39_175, "ac9645e5c9ff0cf176efdd6e75cb5e98f46295d38e02db5c4d208826a39ab4be");

        var definitions = package.GetProperty("compileDefinitions")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(definitions.SetEquals(
        [
            "SQLITE_THREADSAFE=1",
            "SQLITE_ENABLE_FTS5=1",
            "SQLITE_ENABLE_RTREE=1",
            "SQLITE_ENABLE_GEOPOLY=1",
            "SQLITE_ENABLE_COLUMN_METADATA=1",
            "SQLITE_ENABLE_DBSTAT_VTAB=1",
            "SQLITE_ENABLE_EXPLAIN_COMMENTS=1",
            "SQLITE_ENABLE_MATH_FUNCTIONS=1",
            "SQLITE_ENABLE_NORMALIZE=1",
            "SQLITE_ENABLE_STMT_SCANSTATUS=1",
            "SQLITE_ENABLE_UNLOCK_NOTIFY=1",
            "SQLITE_ENABLE_PREUPDATE_HOOK=1",
            "SQLITE_ENABLE_CARRAY=1",
            "SQLITE_ENABLE_SNAPSHOT=1"
        ]));
        Assert.Equal(
            ["STARK_SQLITE_BUNDLED_FEATURES=1"],
            Strings(package.GetProperty("adapterCompileDefinitions")));

        foreach (var targetId in new[] { "linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64" })
        {
            Assert.Equal("required-source-build", package.GetProperty("targetSupport").GetProperty(targetId).GetString());
        }
        Assert.Equal(["dl", "m", "pthread"], Strings(package.GetProperty("systemLinkFacts").GetProperty("linux")));
        Assert.Empty(package.GetProperty("systemLinkFacts").GetProperty("windows").EnumerateArray());
        Assert.Empty(package.GetProperty("systemLinkFacts").GetProperty("macos").EnumerateArray());

        var evidencePath = Assert.Single(package.GetProperty("licenseEvidencePaths").EnumerateArray()).GetString()!;
        var evidenceText = File.ReadAllText(Path.Combine(repositoryRoot, evidencePath));
        Assert.Contains("The author disclaims copyright", evidenceText, StringComparison.Ordinal);
        Assert.Contains("May you do good and not evil", evidenceText, StringComparison.Ordinal);
        Assert.Equal(
            sourceFiles["sqlite3.h"].GetProperty("sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repositoryRoot, evidencePath)))));
    }

    [Fact]
    public void ContributorUsesOnlyPinnedSourceAndTheExplicitPrivateToolchain()
    {
        var script = ReadScript();

        foreach (var parameter in new[]
        {
            "$AssetSuffix",
            "$TargetTriple",
            "$OutputVendorRoot",
            "$StdlibPackageDir",
            "$ToolchainDir",
            "$ContributionManifestPath",
            "$CompilerProject",
            "$CacheDir"
        })
        {
            Assert.Contains(parameter, script, StringComparison.Ordinal);
        }

        Assert.Contains("$archiveCacheRoot = Join-Path $cacheRoot $sourceSha256", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Sha256 -Path $archivePath", script, StringComparison.Ordinal);
        Assert.Contains("Expand-VerifiedZipArchive", script, StringComparison.Ordinal);
        Assert.Contains("sourceArchiveFiles", script, StringComparison.Ordinal);
        Assert.Contains("sourceSize", script, StringComparison.Ordinal);
        Assert.Contains("sourceDateEpoch", script, StringComparison.Ordinal);
        Assert.Contains("$clangPath = Join-Path $toolchainPath", script, StringComparison.Ordinal);
        Assert.Contains("$archiverPath = Join-Path $toolchainPath", script, StringComparison.Ordinal);
        Assert.Contains("\"--target=$TargetTriple\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-O3\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-DNDEBUG\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-ffunction-sections\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-fdata-sections\"", script, StringComparison.Ordinal);
        Assert.Contains("& $archiverPath rcsD", script, StringComparison.Ordinal);
        Assert.Contains("$sqliteObjectPath $adapterObjectPath", script, StringComparison.Ordinal);
        Assert.Contains("-ExpectedMembers @($sqliteObjectFileName, $adapterObjectFileName)", script, StringComparison.Ordinal);
        Assert.Contains("STARK_SQLITE_BUNDLED_FEATURES=1", script, StringComparison.Ordinal);
        Assert.Contains("adapterCompiledIntoNativeArchive = $true", script, StringComparison.Ordinal);
        Assert.Contains("perApplicationNativeSourceCompilation = $false", script, StringComparison.Ordinal);
        Assert.Contains("SQLiteBundledOptionalSmoke.stark", script, StringComparison.Ordinal);
        Assert.Contains("Bundled SQLite optional-feature runtime smoke failed", script, StringComparison.Ordinal);
        Assert.Contains("carray = \"available-and-query-verified\"", script, StringComparison.Ordinal);
        Assert.Contains("normalizedSql = \"available-and-result-verified\"", script, StringComparison.Ordinal);
        Assert.Contains("statementScanStatus = \"available-and-invoked\"", script, StringComparison.Ordinal);
        Assert.Contains("snapshot = \"available-and-invoked-with-non-wal-database\"", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NativeObjectTarget", script, StringComparison.Ordinal);
        Assert.Contains("Assert-StaticLibraryArchive", script, StringComparison.Ordinal);
        Assert.Contains("The SQLite amalgamation is already one translation unit", script, StringComparison.Ordinal);
        foreach (var targetId in new[] { "linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64" })
        {
            Assert.Contains($"\"{targetId}\" {{", script, StringComparison.Ordinal);
        }
        Assert.Contains("$bytes[18] -eq 0xb7", script, StringComparison.Ordinal);
        Assert.Contains("$bytes[1] -eq 0xaa", script, StringComparison.Ordinal);
        Assert.Contains("$bytes[4] -eq 0x07", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Get-Command clang", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--native-pkg-config", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PKG_CONFIG", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLITE_INCLUDE_DIR", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLITE_LIBRARY_DIR", script, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor/dist/arm64", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stdlib/src", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorPreservesPackageTargetSystemAndRelocatableNativeFacts()
    {
        var script = ReadScript();

        Assert.Contains("\"--emit-lib\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--no-stark-path\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--package-profile\", \"release\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--toolchain-dir\", $toolchainPath", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-include-dir\", $nativeSqliteRoot", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-library-dir\", $nativeSqliteRoot", script, StringComparison.Ordinal);
        Assert.Contains("\"--native-library\", \"sqlite3\"", script, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredProperty -Object $systemLinkFacts -Name $targetOperatingSystem", script, StringComparison.Ordinal);
        Assert.Contains("Generated SQLite release package must not depend on pkg-config", script, StringComparison.Ordinal);
        Assert.Contains("$adapterSourcePath = Join-Path $repositoryRoot \"vendor/SQLiteTextBinding.c\"", script, StringComparison.Ordinal);
        Assert.Contains("$legacyStagedBindingSource = Join-Path $targetDist \"SQLiteTextBinding.c\"", script, StringComparison.Ordinal);
        Assert.Contains("$nativeSources.Count -ne 0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"--native-source\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$stagedBindingSource", script, StringComparison.Ordinal);
        Assert.Contains("native/sqlite", script, StringComparison.Ordinal);
        Assert.Contains("must depend only on staged System", script, StringComparison.Ordinal);
        Assert.Contains("does not preserve the staged System API/content identity", script, StringComparison.Ordinal);
        Assert.Contains("modules '$($moduleNames -join ', ')' do not match catalog", script, StringComparison.Ordinal);

        Assert.Contains("schemaVersion = 1", script, StringComparison.Ordinal);
        Assert.Contains("targetId = $AssetSuffix", script, StringComparison.Ordinal);
        Assert.Contains("packages = [object[]]@($packageEntry)", script, StringComparison.Ordinal);
        Assert.Contains("nativePayload = [ordered]@{", script, StringComparison.Ordinal);
        Assert.Contains("licenseFiles = [object[]]@($licenseDescriptor)", script, StringComparison.Ordinal);
        Assert.Contains("provenance = $provenanceDescriptor", script, StringComparison.Ordinal);
        foreach (var kind in new[] { "header", "license", "static-library", "documentation", "provenance" })
        {
            Assert.Contains($"Kind = \"{kind}\"", script, StringComparison.Ordinal);
        }
        Assert.Contains("Sort-ObjectsOrdinalByProperty -Values $artifactDescriptors", script, StringComparison.Ordinal);
        Assert.Contains("[StringComparer]::Ordinal.Compare", script, StringComparison.Ordinal);
        Assert.Contains("function New-PlainFileDescriptor", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BundledAdapterUsesDirectOptionalSymbolsWithoutChangingDeveloperFallbacks()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "vendor", "SQLiteTextBinding.c"));

        Assert.Contains("#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_CARRAY)", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_carray_bind;", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_carray_bind_v2;", source, StringComparison.Ordinal);
        Assert.Contains("#if defined(STARK_SQLITE_BUNDLED_FEATURES) && defined(SQLITE_ENABLE_SNAPSHOT)", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_snapshot_get;", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_snapshot_open;", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_snapshot_free;", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_snapshot_cmp;", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_snapshot_recover;", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_normalized_sql;", source, StringComparison.Ordinal);
        Assert.Contains("return sqlite3_stmt_scanstatus_v2;", source, StringComparison.Ordinal);
        Assert.Contains("return &sqlite3_temp_directory;", source, StringComparison.Ordinal);
        Assert.Contains("return &sqlite3_data_directory;", source, StringComparison.Ordinal);

        // The non-bundled developer build still resolves optional system-library
        // features dynamically instead of introducing unconditional link edges.
        Assert.Contains("stark_sqlite_find_symbol(\"sqlite3_carray_bind\")", source, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_find_symbol(\"sqlite3_snapshot_get\")", source, StringComparison.Ordinal);
        Assert.Contains("stark_sqlite_find_symbol(\"sqlite3_normalized_sql\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSmokeExercisesBundledOptionalFeaturesThroughThePublicStarkPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(repositoryRoot, "tests", "fixtures", "release", "SQLiteBundledOptionalSmoke.stark"));

        Assert.Contains("import Vendor.SQLite", source, StringComparison.Ordinal);
        Assert.Contains("CArrayBindAvailable()", source, StringComparison.Ordinal);
        Assert.Contains("CArrayBindV2Available()", source, StringComparison.Ordinal);
        Assert.Contains("BindCArrayInt32V2(statement, 1, values)", source, StringComparison.Ordinal);
        Assert.Contains("SELECT sum(value) FROM carray(?)", source, StringComparison.Ordinal);
        Assert.Contains("ColumnInt(statement, 0) != 6", source, StringComparison.Ordinal);
        Assert.Contains("NormalizedSqlAvailable()", source, StringComparison.Ordinal);
        Assert.Contains("NormalizedSql(scanStatement, 128)", source, StringComparison.Ordinal);
        Assert.Contains("StatementScanStatusAvailable()", source, StringComparison.Ordinal);
        Assert.Contains("StatementScanStatusV2Available()", source, StringComparison.Ordinal);
        Assert.Contains("StatementScanStatusI64(", source, StringComparison.Ordinal);
        Assert.Contains("ResetStatementScanStatus(scanStatement)", source, StringComparison.Ordinal);
        Assert.Contains("SnapshotAvailable()", source, StringComparison.Ordinal);
        Assert.Contains("GetSnapshot(database, \"main\")", source, StringComparison.Ordinal);
        Assert.Contains("return 0;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorCannotReplaceSourceTreesOrWriteItsManifestInsideTheSharedRoot()
    {
        var script = ReadScript();

        Assert.Contains("Assert-SafeOutputRoot -Path $outputRoot", script, StringComparison.Ordinal);
        Assert.Contains("cannot be a filesystem root", script, StringComparison.Ordinal);
        Assert.Contains("must be a child of '$artifactsRoot'", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $repositoryRoot \"vendor/src\"", script, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", script, StringComparison.Ordinal);
        Assert.Contains("must be outside shared OutputVendorRoot", script, StringComparison.Ordinal);
        Assert.Contains("A contributor must never replace the shared output Vendor root", script, StringComparison.Ordinal);
        Assert.Contains("[Guid]::NewGuid().ToString(\"N\")", script, StringComparison.Ordinal);
        Assert.Contains("} finally {", script, StringComparison.Ordinal);
        Assert.Contains("artifacts/sqlite-work", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $workRoot -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean unexpected SQLite work root", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $outputRoot -Recurse", script, StringComparison.Ordinal);
        Assert.DoesNotContain("release-input.json", script, StringComparison.Ordinal);
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
        Assert.Matches("^[0-9a-f]{64}$", sha256);
    }

    private static string[] Strings(JsonElement value)
        => value.EnumerateArray().Select(static item => item.GetString()!).ToArray();

    private static string ReadScript()
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Recipe));

    private static async Task<string?> FindPowerShellAsync(string workingDirectory)
    {
        foreach (var candidate in OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : new[] { "pwsh" })
        {
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        using var process = new Process
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
