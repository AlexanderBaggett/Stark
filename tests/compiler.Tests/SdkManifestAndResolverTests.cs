using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stark.Compiler;
using Stark.Parsing;

namespace compiler.Tests;

public sealed class SdkManifestAndResolverTests
{
    [Fact]
    public void RootResolutionUsesExplicitThenEnvironmentThenExecutablePrecedence()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-root-");
        try
        {
            var explicitRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "explicit")).FullName;
            var environmentRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "environment")).FullName;
            var executableRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "executable")).FullName;
            var executablePath = Path.Combine(executableRoot, OperatingSystem.IsWindows() ? "stark.exe" : "stark");
            File.WriteAllBytes(executablePath, []);
            var environmentReadCount = 0;

            var explicitResolution = SdkRootResolver.Resolve(
                explicitRoot,
                executablePath,
                name =>
                {
                    environmentReadCount++;
                    return name == SdkRootResolver.EnvironmentVariableName ? environmentRoot : null;
                });

            Assert.Equal(SdkRootOrigin.Explicit, explicitResolution.Origin);
            Assert.Equal(SdkRootResolver.CanonicalizeRootPath(explicitRoot), explicitResolution.RootPath);
            Assert.Equal(0, environmentReadCount);

            var environmentResolution = SdkRootResolver.Resolve(
                explicitRoot: null,
                executablePath,
                name => name == SdkRootResolver.EnvironmentVariableName ? environmentRoot : null);
            Assert.Equal(SdkRootOrigin.Environment, environmentResolution.Origin);
            Assert.Equal(SdkRootResolver.CanonicalizeRootPath(environmentRoot), environmentResolution.RootPath);

            var executableResolution = SdkRootResolver.Resolve(
                explicitRoot: null,
                executablePath,
                environmentVariableReader: static _ => null);
            Assert.Equal(SdkRootOrigin.Executable, executableResolution.Origin);
            Assert.Equal(SdkRootResolver.CanonicalizeRootPath(executableRoot), executableResolution.RootPath);
            Assert.Equal(
                Path.Combine(SdkRootResolver.CanonicalizeRootPath(executableRoot), Path.GetFileName(executablePath)),
                executableResolution.ExecutablePath);
            Assert.Equal(
                Path.Combine(executableResolution.RootPath, "sdk.json"),
                executableResolution.ManifestPath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void RootResolutionCanonicalizesExecutableAndExplicitRootSymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symlinks can require an elevated token on Windows. The
            // implementation uses the same FileSystemInfo API there.
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-symlink-");
        try
        {
            var realRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "real-sdk"));
            var executablePath = Path.Combine(realRoot.FullName, "stark");
            File.WriteAllBytes(executablePath, []);
            var executableLink = Path.Combine(tempDirectory.FullName, "stark-link");
            File.CreateSymbolicLink(executableLink, executablePath);
            var rootLink = Path.Combine(tempDirectory.FullName, "sdk-link");
            Directory.CreateSymbolicLink(rootLink, realRoot.FullName);

            var executableResolution = SdkRootResolver.Resolve(
                executablePath: executableLink,
                environmentVariableReader: static _ => null);
            var explicitResolution = SdkRootResolver.Resolve(
                explicitRoot: rootLink,
                executablePath: executableLink,
                environmentVariableReader: static _ => null);

            var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(realRoot.FullName);
            Assert.Equal(canonicalRoot, executableResolution.RootPath);
            Assert.Equal(Path.Combine(canonicalRoot, Path.GetFileName(executablePath)), executableResolution.ExecutablePath);
            Assert.Equal(canonicalRoot, explicitResolution.RootPath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void RootResolutionCanonicalizesIntermediateDirectorySymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symlinks can require an elevated token on Windows. The
            // component-wise resolver uses the same FileSystemInfo API there.
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-parent-symlink-");
        try
        {
            var realParent = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "real-parent"));
            var realRoot = Directory.CreateDirectory(Path.Combine(realParent.FullName, "sdk"));
            var executablePath = Path.Combine(realRoot.FullName, "stark");
            File.WriteAllBytes(executablePath, []);
            var parentLink = Path.Combine(tempDirectory.FullName, "linked-parent");
            Directory.CreateSymbolicLink(parentLink, realParent.FullName);
            var aliasedRoot = Path.Combine(parentLink, "sdk");
            var aliasedExecutable = Path.Combine(aliasedRoot, "stark");

            var executableResolution = SdkRootResolver.Resolve(
                executablePath: aliasedExecutable,
                environmentVariableReader: static _ => null);
            var explicitResolution = SdkRootResolver.Resolve(
                explicitRoot: aliasedRoot,
                executablePath: aliasedExecutable,
                environmentVariableReader: static _ => null);

            var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(realRoot.FullName);
            Assert.Equal(canonicalRoot, executableResolution.RootPath);
            Assert.Equal(Path.Combine(canonicalRoot, Path.GetFileName(executablePath)), executableResolution.ExecutablePath);
            Assert.Equal(canonicalRoot, explicitResolution.RootPath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void RootResolutionDiscoversConventionalBinLayout()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-bin-layout-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
            var binDirectory = Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "bin"));
            var executablePath = Path.Combine(binDirectory.FullName, OperatingSystem.IsWindows() ? "stark.exe" : "stark");
            File.WriteAllBytes(executablePath, []);
            File.WriteAllText(Path.Combine(sdkRoot.FullName, SdkRootResolver.ManifestFileName), "{}");

            var resolution = SdkRootResolver.Resolve(
                executablePath: executablePath,
                environmentVariableReader: static _ => null);

            var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot.FullName);
            Assert.Equal(SdkRootOrigin.Executable, resolution.Origin);
            Assert.Equal(canonicalRoot, resolution.RootPath);
            Assert.Equal(Path.Combine(canonicalRoot, SdkRootResolver.ManifestFileName), resolution.ManifestPath);
            Assert.Equal(
                Path.Combine(canonicalRoot, "bin", Path.GetFileName(executablePath)),
                resolution.ExecutablePath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void RootResolutionPrefersManifestBesideExecutableOverBinParent()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-bin-adjacent-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
            var binDirectory = Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "bin"));
            var executablePath = Path.Combine(binDirectory.FullName, OperatingSystem.IsWindows() ? "stark.exe" : "stark");
            File.WriteAllBytes(executablePath, []);
            File.WriteAllText(Path.Combine(sdkRoot.FullName, SdkRootResolver.ManifestFileName), "{}");
            File.WriteAllText(Path.Combine(binDirectory.FullName, SdkRootResolver.ManifestFileName), "{}");

            var resolution = SdkRootResolver.Resolve(
                executablePath: executablePath,
                environmentVariableReader: static _ => null);

            Assert.Equal(
                SdkRootResolver.CanonicalizeRootPath(binDirectory.FullName),
                resolution.RootPath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void RootResolutionUsesReleaseMarkerForMissingBinLayoutSdkManifest()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-bin-release-marker-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
            var binDirectory = Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "bin"));
            var executablePath = Path.Combine(
                binDirectory.FullName,
                OperatingSystem.IsWindows() ? "stark.exe" : "stark");
            File.WriteAllText(executablePath, "compiler");
            File.WriteAllText(
                Path.Combine(sdkRoot.FullName, SdkRootResolver.ReleaseManifestFileName),
                "{}");

            var result = SdkRootResolver.Resolve(
                executablePath: executablePath,
                environmentVariableReader: static _ => null);

            Assert.Equal(SdkRootResolver.CanonicalizeRootPath(sdkRoot.FullName), result.RootPath);
            Assert.Equal(
                Path.Combine(result.RootPath, SdkRootResolver.ManifestFileName),
                result.ManifestPath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void RootResolutionDiscoversBinLayoutThroughExecutableSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symlinks can require an elevated token on Windows. The
            // implementation uses the same FileSystemInfo API there.
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-bin-symlink-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
            var binDirectory = Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "bin"));
            var executablePath = Path.Combine(binDirectory.FullName, "stark");
            File.WriteAllBytes(executablePath, []);
            File.WriteAllText(Path.Combine(sdkRoot.FullName, SdkRootResolver.ManifestFileName), "{}");
            var executableLink = Path.Combine(tempDirectory.FullName, "stark");
            File.CreateSymbolicLink(executableLink, executablePath);

            var resolution = SdkRootResolver.Resolve(
                executablePath: executableLink,
                environmentVariableReader: static _ => null);

            var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot.FullName);
            Assert.Equal(canonicalRoot, resolution.RootPath);
            Assert.Equal(Path.Combine(canonicalRoot, "bin", "stark"), resolution.ExecutablePath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void RootResolutionDoesNotSearchUnrelatedExecutableAncestors()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-no-ancestor-search-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
            var toolsDirectory = Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "tools"));
            var executablePath = Path.Combine(toolsDirectory.FullName, OperatingSystem.IsWindows() ? "stark.exe" : "stark");
            File.WriteAllBytes(executablePath, []);
            File.WriteAllText(Path.Combine(sdkRoot.FullName, SdkRootResolver.ManifestFileName), "{}");

            var resolution = SdkRootResolver.Resolve(
                executablePath: executablePath,
                environmentVariableReader: static _ => null);

            var canonicalToolsDirectory = SdkRootResolver.CanonicalizeRootPath(toolsDirectory.FullName);
            Assert.Equal(canonicalToolsDirectory, resolution.RootPath);
            Assert.Equal(
                Path.Combine(canonicalToolsDirectory, SdkRootResolver.ManifestFileName),
                resolution.ManifestPath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestLoaderRejectsSdkJsonSymlinkOutsideCanonicalRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symlinks can require an elevated token on Windows. The
            // same reparse-point inspection is used there.
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-manifest-link-");
        try
        {
            var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
            var externalManifestPath = Path.Combine(tempDirectory.FullName, "outside-sdk.json");
            File.WriteAllText(externalManifestPath, "{}");
            File.CreateSymbolicLink(
                Path.Combine(sdkRoot.FullName, SdkRootResolver.ManifestFileName),
                externalManifestPath);

            var result = SdkManifestLoader.Load(sdkRoot.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("STK7400", diagnostic.Code);
            Assert.Contains("could not be resolved safely", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("symbolic link", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void DevelopmentManifestWriterProducesDeterministicSourceOnlySdk()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-development-sdk-");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "stdlib", "src"));
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "stdlib", "templates"));
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "vendor", "src"));

            Assert.True(
                DevelopmentSdkManifestWriter.TryWrite(
                    tempDirectory.FullName,
                    out var manifestPath,
                    out var error),
                error);
            var firstText = File.ReadAllText(manifestPath);
            var firstTimestamp = File.GetLastWriteTimeUtc(manifestPath);

            Assert.True(
                DevelopmentSdkManifestWriter.TryWrite(
                    tempDirectory.FullName,
                    out var secondManifestPath,
                    out error),
                error);

            Assert.Equal(manifestPath, secondManifestPath);
            Assert.Equal(firstText, File.ReadAllText(secondManifestPath));
            Assert.Equal(firstTimestamp, File.GetLastWriteTimeUtc(secondManifestPath));

            var result = SdkManifestLoader.Load(tempDirectory.FullName);
            Assert.True(result.Succeeded, FormatDiagnostics(result.Diagnostics));
            Assert.Equal(SdkDistributionKind.Development, result.Manifest!.Kind);
            Assert.Empty(result.Manifest.Packages);
            Assert.Empty(result.Manifest.Modules);
            Assert.Equal(
                ["stdlib/src", "stdlib/templates", "vendor/src"],
                result.Manifest.DevelopmentSourceRoots);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestParsingBuildsOrdinalDeterministicPackageAndModuleIndexes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-index-");
        try
        {
            var json = CreateManifestJson(
                modules:
                [
                    new TestModule("Vendor.Probe.Core", "Vendor.Probe"),
                    new TestModule("System", "System"),
                    new TestModule("Vendor.Probe", "Vendor.Probe")
                ],
                packages:
                [
                    new TestPackage(
                        "Vendor.Probe",
                        "vendor/dist/target/Vendor.Probe.starkpkg",
                        "vendor/dist/target/libVendorProbe.a",
                        ["System"],
                        ImageSha256: new string('a', 64),
                        LibrarySha256: new string('b', 64)),
                    new TestPackage(
                        "System",
                        "stdlib/dist/target/System.starkpkg",
                        "stdlib/dist/target/libSystem.a",
                        [],
                        ImageSha256: new string('c', 64),
                        LibrarySha256: new string('d', 64))
                ],
                kind: "release");

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.True(result.Succeeded, FormatDiagnostics(result.Diagnostics));
            Assert.NotNull(result.Manifest);
            Assert.NotNull(result.PackageIndex);
            Assert.Equal(SdkDistributionKind.Release, result.Manifest.Kind);
            Assert.Equal(["System", "Vendor.Probe"], result.PackageIndex.Packages.Select(static package => package.Id));
            Assert.Equal(
                ["System", "Vendor.Probe", "Vendor.Probe.Core"],
                result.PackageIndex.Modules.Select(static module => module.ModuleName));
            Assert.True(result.PackageIndex.TryGetPackageForModule(
                "Vendor.Probe.Core",
                out var package,
                out var ownership));
            Assert.Equal("Vendor.Probe", package.Id);
            Assert.Equal("Vendor.Probe.Core", ownership.ModuleName);
            Assert.Equal(
                Path.Combine(
                    SdkRootResolver.CanonicalizeRootPath(tempDirectory.FullName),
                    "vendor",
                    "dist",
                    "target",
                    "Vendor.Probe.starkpkg"),
                result.PackageIndex.GetPackageImagePath(package));
            Assert.False(result.PackageIndex.TryGetPackageForModule(
                "Vendor.Unknown",
                out _,
                out _));
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData("stark-sdk-v2")]
    [InlineData("0.1.0")]
    [InlineData(" stark-sdk-v1")]
    public void ManifestRejectsUnsupportedCompilerCompatibilityLine(string compilerCompatibility)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-compiler-compatibility-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage("System", "stdlib/System.starkpkg", "stdlib/libSystem.a", [])],
                compilerCompatibility: compilerCompatibility);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "STK7405");
            Assert.Contains(compilerCompatibility, diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains(SdkCompilerCompatibility.SupportedLine, diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("exact compatibility line", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData("../outside/System.starkpkg", "STK7413")]
    [InlineData("/outside/System.starkpkg", "STK7412")]
    [InlineData("C:/outside/System.starkpkg", "STK7412")]
    [InlineData("stdlib\\dist\\System.starkpkg", "STK7411")]
    [InlineData("stdlib//System.starkpkg", "STK7413")]
    public void ManifestRejectsNonRelocatablePackagePaths(string imagePath, string expectedCode)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-path-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage("System", imagePath, "stdlib/dist/libSystem.a", [])]);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsWhitespaceThatChangesEveryStoredPathKindAfterNormalization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-path-whitespace-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "../ ",
                    " /absolute/library.a",
                    [],
                    Native: new TestNative(
                        Artifacts: ["../ "],
                        IncludeDirectories: [" include"],
                        LibraryDirectories: ["lib "],
                        RuntimeFiles: [" /absolute/runtime.so"],
                        LicenseFiles: ["licenses/../ "],
                        FileChecksums:
                        [
                            new TestFileChecksum(" checksum/path ", new string('a', 64))
                        ]))],
                developmentSourceRoots: [" ../sources"]);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var whitespaceDiagnostics = result.Diagnostics
                .Where(static diagnostic => diagnostic.Code == "STK7415")
                .ToArray();
            Assert.Equal(9, whitespaceDiagnostics.Length);
            Assert.All(
                whitespaceDiagnostics,
                diagnostic => Assert.Contains("leading or trailing whitespace", diagnostic.Message, StringComparison.Ordinal));
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsDuplicateModuleOwnership()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-duplicate-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System"), new TestModule("System", "Vendor.Probe")],
                [
                    new TestPackage("System", "stdlib/System.starkpkg", "stdlib/libSystem.a", []),
                    new TestPackage("Vendor.Probe", "vendor/Probe.starkpkg", "vendor/libProbe.a", ["System"])
                ]);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "STK7432");
            Assert.Contains("more than one owner", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsUnknownModuleOwnerAndPackageDependency()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-reference-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Missing.Owner")],
                [new TestPackage("Vendor.Probe", "vendor/Probe.starkpkg", "vendor/libProbe.a", ["Missing.Dependency"])]);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7450");
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7451");
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsDependencyHashesThatDoNotMatchReferencedPackageIdentity()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-dependency-identity-");
        try
        {
            var document = JsonNode.Parse(CreateManifestJson(
                [
                    new TestModule("System", "System"),
                    new TestModule("Vendor.Probe", "Vendor.Probe")
                ],
                [
                    new TestPackage("System", "packages/System.starkpkg", "packages/libSystem.a", []),
                    new TestPackage("Vendor.Probe", "packages/Probe.starkpkg", "packages/libProbe.a", ["System"])
                ]))!.AsObject();
            var packages = document["packages"]!.AsArray();
            packages[0]!["apiHash"] = new string('a', 64);
            packages[0]!["contentHash"] = new string('b', 64);
            packages[1]!["apiHash"] = new string('c', 64);
            packages[1]!["contentHash"] = new string('d', 64);
            var dependency = packages[1]!["dependencies"]![0]!;
            dependency["apiHash"] = new string('e', 64);
            dependency["contentHash"] = new string('f', 64);

            var result = SdkManifestLoader.Parse(document.ToJsonString(), tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "STK7458");
            Assert.Contains("does not match", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsUnknownSchemaAndDistributionKind()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-schema-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage("System", "stdlib/System.starkpkg", "stdlib/libSystem.a", [])],
                schemaVersion: 99,
                kind: "portable");

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7402");
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7403");
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsUnsupportedPackageFormatVersion()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-package-format-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage("System", "packages/System.starkpkg", "packages/libSystem.a", [])],
                packageFormatVersion: 99);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "STK7409");
            Assert.Contains("not supported", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("1 or 2", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData("debug", "development")]
    [InlineData("dev", "release")]
    public void ManifestRejectsInvalidOrDevelopmentProfileInReleaseSdk(
        string profile,
        string kind)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-package-profile-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage(
                    "System",
                    "packages/System.starkpkg",
                    "packages/libSystem.a",
                    [],
                    ImageSha256: new string('a', 64),
                    LibrarySha256: new string('b', 64),
                    Profile: profile)],
                kind: kind);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7429");
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverLoadsOnlyIndexedPackageImagesAndResolvesExactModules()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-resolver-");
        try
        {
            WritePackage(tempDirectory.FullName, "packages/Probe.starkpkg", "packages/libProbe.a", "Vendor.Probe");
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage("Vendor.Probe", "packages/Probe.starkpkg", "packages/libProbe.a", [])]);

            var manifestResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);
            var resolverResult = SdkPackageModuleResolver.Load(manifestResult);

            Assert.True(resolverResult.Succeeded, FormatDiagnostics(resolverResult.Diagnostics));
            Assert.NotNull(resolverResult.Resolver);
            Assert.True(resolverResult.Resolver.TryResolveModule("Vendor.Probe", out var module));
            Assert.True(module.IsSdkPackage);
            var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(tempDirectory.FullName);
            Assert.Equal(Path.Combine(canonicalRoot, "packages", "Probe.starkpkg"), module.ManifestPath);
            Assert.Equal(Path.Combine(canonicalRoot, "packages", "libProbe.a"), module.LibraryPath);
            Assert.False(resolverResult.Resolver.TryResolveModule("Vendor", out _));
            Assert.False(resolverResult.Resolver.TryResolveModule("Vendor.Probe.Extra", out _));
            Assert.True(resolverResult.Resolver.TryLoadModuleSource(module, out var sourceText, out var sourcePath));
            Assert.Contains("module Vendor.Probe", sourceText, StringComparison.Ordinal);
            Assert.Equal(module.ManifestPath, sourcePath);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverRejectsDescriptorIdThatRelabelsPackageImageRootIdentity()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-package-id-");
        try
        {
            WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Actual");
            var json = CreateManifestJson(
                [new TestModule("Vendor.Actual", "Vendor.Relabeled")],
                [new TestPackage(
                    "Vendor.Relabeled",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [])]);

            var resolverResult = SdkPackageModuleResolver.Load(
                SdkManifestLoader.Parse(json, tempDirectory.FullName));

            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7454");
            Assert.Contains("Vendor.Relabeled", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("Vendor.Actual", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dev")]
    public void PackageResolverRejectsMissingOrRelabeledPackageImageProfile(string? imageProfile)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-image-profile-");
        try
        {
            WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe",
                imageProfile);
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [],
                    Profile: "release")]);

            var resolverResult = SdkPackageModuleResolver.Load(
                SdkManifestLoader.Parse(json, tempDirectory.FullName));

            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7455");
            Assert.Contains("profile", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverRejectsImageFormatThatDisagreesWithSdkManifest()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-image-format-");
        try
        {
            WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [])],
                packageFormatVersion: (int)PackageImageBinaryFormat.LegacyFormatVersion);

            var resolverResult = SdkPackageModuleResolver.Load(
                SdkManifestLoader.Parse(json, tempDirectory.FullName));

            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7456");
            Assert.Contains("format version is 2", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("packageFormatVersion 1", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverReportsHeaderFormatMismatchBeforeGenericDecodeFailure()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-image-unsupported-format-");
        try
        {
            var written = WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            var bytes = File.ReadAllBytes(written.ImagePath);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 999);
            File.WriteAllBytes(written.ImagePath, bytes);
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [])]);

            var resolverResult = SdkPackageModuleResolver.Load(
                SdkManifestLoader.Parse(json, tempDirectory.FullName));

            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics);
            Assert.Equal("STK7456", diagnostic.Code);
            Assert.Contains("format version is 999", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("STK7461", FormatDiagnostics(resolverResult.Diagnostics), StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverAllowsRelocatedLibraryDirectoryButRejectsRelabeledLibraryFile()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-library-identity-");
        try
        {
            WritePackage(
                tempDirectory.FullName,
                "images/Probe.starkpkg",
                "build-output/libActual.a",
                "Vendor.Probe");
            WriteFile(tempDirectory.FullName, "relocated/libRelabeled.a", []);
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "images/Probe.starkpkg",
                    "relocated/libRelabeled.a",
                    [])]);

            var resolverResult = SdkPackageModuleResolver.Load(
                SdkManifestLoader.Parse(json, tempDirectory.FullName));

            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7457");
            Assert.Contains("libRelabeled.a", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("libActual.a", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("directory may be relocated", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void LazyPackageResolverAllowsValidModuleWhenUnusedVendorPackageIsMissing()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-lazy-unused-");
        try
        {
            WritePackage(tempDirectory.FullName, "packages/System.starkpkg", "packages/libSystem.a", "System");
            var json = CreateManifestJson(
                [
                    new TestModule("System", "System"),
                    new TestModule("Vendor.Broken", "Vendor.Broken")
                ],
                [
                    new TestPackage("System", "packages/System.starkpkg", "packages/libSystem.a", []),
                    new TestPackage("Vendor.Broken", "packages/Missing.starkpkg", "packages/libMissing.a", [])
                ]);

            var manifestResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);
            var resolverResult = SdkPackageModuleResolver.CreateLazy(manifestResult);

            Assert.True(manifestResult.Succeeded, FormatDiagnostics(manifestResult.Diagnostics));
            Assert.True(resolverResult.Succeeded, FormatDiagnostics(resolverResult.Diagnostics));
            var resolver = Assert.IsType<SdkPackageModuleResolver>(resolverResult.Resolver);
            Assert.True(resolver.TryResolveModule("System", out _));

            Assert.False(resolver.TryResolveModule("Vendor.Broken", out _));
            var diagnosticProvider = Assert.IsAssignableFrom<IModuleResolutionDiagnosticProvider>(resolver);
            Assert.True(diagnosticProvider.TryGetUnresolvedModuleDiagnostic(
                "Vendor.Broken",
                out var code,
                out var message));
            Assert.Equal("STK7460", code);
            Assert.Contains("Vendor.Broken", message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void LazyPackageResolverDoctorValidationEagerlyFindsEveryBrokenPackageDeterministically()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-lazy-doctor-");
        try
        {
            var json = CreateManifestJson(
                [
                    new TestModule("Vendor.Zed", "Vendor.Zed"),
                    new TestModule("Vendor.Alpha", "Vendor.Alpha")
                ],
                [
                    new TestPackage("Vendor.Zed", "packages/Zed.starkpkg", "packages/libZed.a", []),
                    new TestPackage("Vendor.Alpha", "packages/Alpha.starkpkg", "packages/libAlpha.a", [])
                ]);
            var resolver = Assert.IsType<SdkPackageModuleResolver>(
                SdkPackageModuleResolver.CreateLazy(
                    SdkManifestLoader.Parse(json, tempDirectory.FullName)).Resolver);

            var first = resolver.ValidateAllPackages();
            var second = resolver.ValidateAllPackages();

            Assert.Equal(["STK7460", "STK7460"], first.Select(static diagnostic => diagnostic.Code));
            Assert.Contains("Vendor.Alpha", first[0].Message, StringComparison.Ordinal);
            Assert.Contains("Vendor.Zed", first[1].Message, StringComparison.Ordinal);
            Assert.Equal(first, second);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LazyPackageResolverReportsImagePathReplacedBySymlinkWithoutThrowing(bool validateAsDoctor)
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symlinks can require an elevated token on Windows. The
            // same non-throwing resolution path is used for reparse points.
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-lazy-path-swap-");
        var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
        var packagesPath = Path.Combine(sdkRoot.FullName, "packages");
        try
        {
            WritePackage(sdkRoot.FullName, "packages/Probe.starkpkg", "packages/libProbe.a", "Vendor.Probe");
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage("Vendor.Probe", "packages/Probe.starkpkg", "packages/libProbe.a", [])]);
            var manifest = SdkManifestLoader.Parse(json, sdkRoot.FullName);
            var resolverResult = SdkPackageModuleResolver.CreateLazy(manifest);

            Assert.True(manifest.Succeeded, FormatDiagnostics(manifest.Diagnostics));
            Assert.True(resolverResult.Succeeded, FormatDiagnostics(resolverResult.Diagnostics));
            var resolver = Assert.IsType<SdkPackageModuleResolver>(resolverResult.Resolver);

            ReplaceDirectoryWithExternalSymlink(
                packagesPath,
                Path.Combine(tempDirectory.FullName, "external-packages"));

            if (validateAsDoctor)
            {
                var diagnostic = Assert.Single(
                    resolver.ValidateAllPackages(),
                    diagnostic => diagnostic.Code == "STK7469");
                Assert.Contains("could not be resolved safely", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("symbolic link", diagnostic.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.False(resolver.TryResolveModule("Vendor.Probe", out _));
                Assert.True(resolver.TryGetUnresolvedModuleDiagnostic(
                    "Vendor.Probe",
                    out var code,
                    out var message));
                Assert.Equal("STK7469", code);
                Assert.Contains("could not be resolved safely", message, StringComparison.Ordinal);
                Assert.Contains("symbolic link", message, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteDirectoryLink(packagesPath);
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void LazyPackageResolverReportsLibraryPathReplacedBySymlinkWithoutThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-lazy-library-swap-");
        var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
        var librariesPath = Path.Combine(sdkRoot.FullName, "libraries");
        try
        {
            WritePackage(sdkRoot.FullName, "images/Probe.starkpkg", "libraries/libProbe.a", "Vendor.Probe");
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage("Vendor.Probe", "images/Probe.starkpkg", "libraries/libProbe.a", [])]);
            var resolver = Assert.IsType<SdkPackageModuleResolver>(
                SdkPackageModuleResolver.CreateLazy(
                    SdkManifestLoader.Parse(json, sdkRoot.FullName)).Resolver);

            ReplaceDirectoryWithExternalSymlink(
                librariesPath,
                Path.Combine(tempDirectory.FullName, "external-libraries"));

            Assert.False(resolver.TryResolveModule("Vendor.Probe", out _));
            Assert.True(resolver.TryGetUnresolvedModuleDiagnostic(
                "Vendor.Probe",
                out var code,
                out var message));
            Assert.Equal("STK7464", code);
            Assert.Contains("library path", message, StringComparison.Ordinal);
            Assert.Contains("symbolic link", message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryLink(librariesPath);
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void DoctorReportsNativePathsReplacedBySymlinkWithKindSpecificDiagnostics()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-lazy-native-swap-");
        var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
        var payloadPath = Path.Combine(sdkRoot.FullName, "payload");
        try
        {
            WritePackage(sdkRoot.FullName, "packages/Probe.starkpkg", "packages/libProbe.a", "Vendor.Probe");
            WriteFile(sdkRoot.FullName, "payload/artifact.a", [1]);
            WriteFile(sdkRoot.FullName, "payload/runtime.so", [2]);
            WriteFile(sdkRoot.FullName, "payload/LICENSE", [3]);
            Directory.CreateDirectory(Path.Combine(payloadPath, "include"));
            Directory.CreateDirectory(Path.Combine(payloadPath, "lib"));
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [],
                    Native: new TestNative(
                        Artifacts: ["payload/artifact.a"],
                        IncludeDirectories: ["payload/include"],
                        LibraryDirectories: ["payload/lib"],
                        RuntimeFiles: ["payload/runtime.so"],
                        LicenseFiles: ["payload/LICENSE"]))]);
            var resolver = Assert.IsType<SdkPackageModuleResolver>(
                SdkPackageModuleResolver.CreateLazy(
                    SdkManifestLoader.Parse(json, sdkRoot.FullName)).Resolver);

            ReplaceDirectoryWithExternalSymlink(
                payloadPath,
                Path.Combine(tempDirectory.FullName, "external-payload"));

            var diagnostics = resolver.ValidateAllPackages();
            Assert.Equal(
                ["STK7470", "STK7471", "STK7472", "STK7473", "STK7474"],
                diagnostics.Select(static diagnostic => diagnostic.Code));
            Assert.All(
                diagnostics,
                diagnostic => Assert.Contains("could not be resolved safely", diagnostic.Message, StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectoryLink(payloadPath);
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void LazyPackageResolverPublishesOneDeterministicPackageLoadAcrossThreads()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-lazy-concurrent-");
        try
        {
            WritePackage(tempDirectory.FullName, "packages/Probe.starkpkg", "packages/libProbe.a", "Vendor.Probe");
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage("Vendor.Probe", "packages/Probe.starkpkg", "packages/libProbe.a", [])]);
            var resolver = Assert.IsType<SdkPackageModuleResolver>(
                SdkPackageModuleResolver.CreateLazy(
                    SdkManifestLoader.Parse(json, tempDirectory.FullName)).Resolver);
            var references = new ResolvedModuleReference?[64];

            Parallel.For(0, references.Length, index =>
            {
                if (resolver.TryResolveModule("Vendor.Probe", out var reference))
                {
                    references[index] = reference;
                }
            });

            Assert.All(references, reference => Assert.NotNull(reference));
            Assert.Single(references.Distinct());
            Assert.Empty(resolver.ValidateAllPackages());
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void SdkLinkPlannerOrdersDependentsBeforeSharedStaticDependency()
    {
        var packages = new[]
        {
            CreatePackageDescriptor("System"),
            CreatePackageDescriptor("Vendor.Raylib", "System"),
            CreatePackageDescriptor("Vendor.Tools", "System")
        };
        var index = new SdkPackageIndex(
            Environment.CurrentDirectory,
            packages,
            packages.Select(package => new SdkModuleOwnership(package.Id, package.Id)));

        var ordered = SdkLinkPlanner.BuildDependentBeforeDependencyOrder(
            index,
            ["System", "Vendor.Tools", "Vendor.Raylib"]);

        Assert.Equal(
            ["Vendor.Raylib", "Vendor.Tools", "System"],
            ordered.Select(static package => package.Id));
    }

    [Fact]
    public void SdkLinkPlannerDoesNotSelectArchiveForSourceBackedOfficialModule()
    {
        var package = CreatePackageDescriptor("Vendor.Raylib");
        var index = new SdkPackageIndex(
            Environment.CurrentDirectory,
            [package],
            [new SdkModuleOwnership("Vendor.Raylib", package.Id)]);
        var root = CreateSourceModuleDocument("App", "/source/App.stark", isRoot: true);
        var sourceBackedOfficialModule = CreateSourceModuleDocument(
            "Vendor.Raylib",
            "/sdk-development-src/Vendor/Raylib.stark",
            isRoot: false);
        var loadedModules = new LoadedModuleSet(
            "App",
            new Dictionary<string, LoadedModuleDocument>(StringComparer.Ordinal)
            {
                ["App"] = root,
                ["Vendor.Raylib"] = sourceBackedOfficialModule
            });

        var selectedFromDocuments = SdkLinkPlanner.SelectSdkPackageIds(
            index,
            loadedModules,
            Array.Empty<string>());
        var selectedFromExactLibrary = SdkLinkPlanner.SelectSdkPackageIds(
            index,
            loadedModules,
            [index.GetPackageLibraryPath(package)!]);

        Assert.Empty(selectedFromDocuments);
        Assert.Equal(["Vendor.Raylib"], selectedFromExactLibrary);
    }

    [Fact]
    public void SdkRuntimePlanRejectsDifferentFilesWithSameStagedBasename()
    {
        var first = CreatePackageDescriptor("Vendor.First") with
        {
            Native = CreateRuntimeDescriptor("native/first/shared.dylib", new string('a', 64))
        };
        var second = CreatePackageDescriptor("Vendor.Second") with
        {
            Native = CreateRuntimeDescriptor("native/second/shared.dylib", new string('b', 64))
        };
        var index = new SdkPackageIndex(
            Environment.CurrentDirectory,
            [first, second],
            [
                new SdkModuleOwnership(first.Id, first.Id),
                new SdkModuleOwnership(second.Id, second.Id)
            ]);

        var plan = SdkLinkPlanner.BuildRuntimeFilePlan(
            index,
            [first, second],
            CreateSdkTarget());

        Assert.False(plan.Succeeded);
        var diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal("STK7476", diagnostic.Code);
        Assert.Contains("both stage as 'shared.dylib'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("do not declare the same SHA-256", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Vendor.First", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Vendor.Second", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkRuntimePlanDeduplicatesDistinctSourcesWithIdenticalDeclaredHash()
    {
        var sharedHash = new string('c', 64);
        var first = CreatePackageDescriptor("Vendor.First") with
        {
            Native = CreateRuntimeDescriptor("native/first/shared.so", sharedHash)
        };
        var second = CreatePackageDescriptor("Vendor.Second") with
        {
            Native = CreateRuntimeDescriptor("native/second/shared.so", sharedHash.ToUpperInvariant())
        };
        var index = new SdkPackageIndex(
            Environment.CurrentDirectory,
            [first, second],
            [
                new SdkModuleOwnership(first.Id, first.Id),
                new SdkModuleOwnership(second.Id, second.Id)
            ]);

        var plan = SdkLinkPlanner.BuildRuntimeFilePlan(
            index,
            [first, second],
            CreateSdkTarget());

        Assert.True(plan.Succeeded);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(
            [index.ResolvePath("native/first/shared.so")],
            plan.RuntimeFiles);
    }

    [Fact]
    public void SdkRuntimePlanUsesTargetPlatformBasenameComparison()
    {
        var first = CreatePackageDescriptor("Vendor.First") with
        {
            Native = CreateRuntimeDescriptor("native/first/Shared.dll", new string('a', 64))
        };
        var second = CreatePackageDescriptor("Vendor.Second") with
        {
            Native = CreateRuntimeDescriptor("native/second/shared.dll", new string('b', 64))
        };
        var index = new SdkPackageIndex(
            Environment.CurrentDirectory,
            [first, second],
            [
                new SdkModuleOwnership(first.Id, first.Id),
                new SdkModuleOwnership(second.Id, second.Id)
            ]);

        var windowsPlan = SdkLinkPlanner.BuildRuntimeFilePlan(
            index,
            [first, second],
            CreateSdkTarget(operatingSystem: "windows"));
        var unixPlan = SdkLinkPlanner.BuildRuntimeFilePlan(
            index,
            [first, second],
            CreateSdkTarget(operatingSystem: "linux"));

        Assert.Equal("STK7476", Assert.Single(windowsPlan.Diagnostics).Code);
        Assert.True(unixPlan.Succeeded);
        Assert.Equal(2, unixPlan.RuntimeFiles.Count);
    }

    [Fact]
    public void SdkRuntimePlanReportsRuntimePathReplacedBySymlinkWithoutThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-runtime-plan-swap-");
        var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
        var runtimePath = Path.Combine(sdkRoot.FullName, "runtime");
        try
        {
            WriteFile(sdkRoot.FullName, "runtime/probe.so", [1, 2, 3]);
            var package = CreatePackageDescriptor("Vendor.Probe") with
            {
                Native = CreateRuntimeDescriptor("runtime/probe.so", new string('a', 64))
            };
            var index = new SdkPackageIndex(
                sdkRoot.FullName,
                [package],
                [new SdkModuleOwnership("Vendor.Probe", package.Id)]);

            ReplaceDirectoryWithExternalSymlink(
                runtimePath,
                Path.Combine(tempDirectory.FullName, "external-runtime"));

            var plan = SdkLinkPlanner.BuildRuntimeFilePlan(
                index,
                [package],
                CreateSdkTarget());

            Assert.False(plan.Succeeded);
            var diagnostic = Assert.Single(plan.Diagnostics);
            Assert.Equal("STK7477", diagnostic.Code);
            Assert.Contains("runtime file path 'runtime/probe.so'", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("symbolic link", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryLink(runtimePath);
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void CompositeResolverDoesNotFallBackAfterIndexedSdkPackageFailsValidation()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-lazy-no-fallback-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("Vendor.Broken", "Vendor.Broken")],
                [new TestPackage("Vendor.Broken", "packages/Missing.starkpkg", "packages/libMissing.a", [])]);
            var sdkResolver = Assert.IsType<SdkPackageModuleResolver>(
                SdkPackageModuleResolver.CreateLazy(
                    SdkManifestLoader.Parse(json, tempDirectory.FullName)).Resolver);
            var fallback = new InMemoryModuleResolver(
                [new ResolvedModuleReference("Vendor.Broken", "/fallback/Vendor/Broken.stark", IsExternal: false)]);
            var composite = new CompositeModuleResolver([sdkResolver, fallback]);

            Assert.False(composite.TryResolveModule("Vendor.Broken", out _));
            Assert.True(composite.TryGetUnresolvedModuleDiagnostic(
                "Vendor.Broken",
                out var code,
                out _));
            Assert.Equal("STK7460", code);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverRejectsSdkIndexAndPackageImageModuleDisagreement()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-disagreement-");
        try
        {
            WritePackage(tempDirectory.FullName, "packages/Probe.starkpkg", "packages/libProbe.a", "Vendor.Actual");
            var json = CreateManifestJson(
                [new TestModule("Vendor.Expected", "Vendor.Actual")],
                [new TestPackage("Vendor.Actual", "packages/Probe.starkpkg", "packages/libProbe.a", [])]);

            var manifestResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);
            var resolverResult = SdkPackageModuleResolver.Load(manifestResult);

            Assert.True(manifestResult.Succeeded, FormatDiagnostics(manifestResult.Diagnostics));
            Assert.False(resolverResult.Succeeded);
            Assert.Null(resolverResult.Resolver);
            Assert.Contains(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7462");
            Assert.Contains(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7463");
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverRejectsSdkDescriptorIdentityThatDiffersFromPackageImage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-image-identity-mismatch-");
        try
        {
            var written = WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            Assert.True(PackageImageLoader.TryLoadManifest(written.ImagePath, out var packageManifest));
            var imageIdentity = Assert.IsType<StarkPackageIdentityManifest>(packageManifest.Identity);
            var document = JsonNode.Parse(CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [],
                    ImageSha256: CalculateSha256(written.ImagePath),
                    LibrarySha256: CalculateSha256(written.LibraryPath))],
                kind: "release"))!.AsObject();
            var descriptor = document["packages"]![0]!;
            descriptor["apiHash"] = CorruptSha256(imageIdentity.ApiHash);
            descriptor["contentHash"] = imageIdentity.ContentHash;

            var manifestResult = SdkManifestLoader.Parse(document.ToJsonString(), tempDirectory.FullName);
            var resolverResult = SdkPackageModuleResolver.Load(manifestResult);

            Assert.True(manifestResult.Succeeded, FormatDiagnostics(manifestResult.Diagnostics));
            Assert.False(resolverResult.Succeeded);
            Assert.Contains(resolverResult.Diagnostics, static diagnostic => diagnostic.Code == "STK7458");
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsMalformedPackageChecksumsBeforeArtifactLoading()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-checksum-shape-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage(
                    "System",
                    "packages/System.starkpkg",
                    "packages/libSystem.a",
                    [],
                    ImageSha256: "not-a-sha256")]);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "STK7426");
            Assert.Contains("exactly 64 hexadecimal", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsReleasePackageWithoutCompleteChecksumCoverage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-release-checksum-required-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage(
                    "System",
                    "packages/System.starkpkg",
                    "packages/libSystem.a",
                    [],
                    Native: new TestNative(Artifacts: ["native/libSystemSupport.a"]))],
                kind: "release");

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostics = result.Diagnostics.Where(static diagnostic => diagnostic.Code == "STK7427").ToArray();
            Assert.Equal(3, diagnostics.Length);
            Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("package 'System' image", StringComparison.Ordinal));
            Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("package 'System' library", StringComparison.Ordinal));
            Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("native file 'native/libSystemSupport.a'", StringComparison.Ordinal));
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsMalformedNativeFileChecksumBeforeArtifactLoading()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-native-checksum-shape-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage(
                    "System",
                    "packages/System.starkpkg",
                    "packages/libSystem.a",
                    [],
                    Native: new TestNative(
                        Artifacts: ["native/libSystemSupport.a"],
                        FileChecksums:
                        [
                            new TestFileChecksum("native/libSystemSupport.a", "not-a-sha256")
                        ]))]);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "STK7426");
            Assert.Contains("native file 'native/libSystemSupport.a'", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData(true, "STK7465")]
    [InlineData(false, "STK7467")]
    public void PackageResolverRejectsPackageImageAndLibraryChecksumMismatches(
        bool corruptImageChecksum,
        string expectedCode)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-checksum-mismatch-");
        try
        {
            var written = WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            var imageSha256 = CalculateSha256(written.ImagePath);
            var librarySha256 = CalculateSha256(written.LibraryPath);
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [],
                    ImageSha256: corruptImageChecksum ? CorruptSha256(imageSha256) : imageSha256,
                    LibrarySha256: corruptImageChecksum ? librarySha256 : CorruptSha256(librarySha256))]);

            var manifestResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);
            var resolverResult = SdkPackageModuleResolver.Load(manifestResult);

            Assert.True(manifestResult.Succeeded, FormatDiagnostics(manifestResult.Diagnostics));
            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
            Assert.Contains("checksum mismatch", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverAcceptsMatchingChecksumsAndCompleteNativePayload()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-integrity-ok-");
        try
        {
            var written = WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            WriteFile(tempDirectory.FullName, "native/libprobe-native.a", [1, 2, 3]);
            WriteFile(tempDirectory.FullName, "native/runtime/probe.bin", [4, 5]);
            WriteFile(tempDirectory.FullName, "licenses/Probe.txt", "license"u8.ToArray());
            var nativeArchiveChecksum = CalculateSha256(Path.Combine(tempDirectory.FullName, "native/libprobe-native.a"));
            var runtimeChecksum = CalculateSha256(Path.Combine(tempDirectory.FullName, "native/runtime/probe.bin"));
            var licenseChecksum = CalculateSha256(Path.Combine(tempDirectory.FullName, "licenses/Probe.txt"));
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "native", "include"));
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "native", "lib"));
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [],
                    ImageSha256: CalculateSha256(written.ImagePath).ToUpperInvariant(),
                    LibrarySha256: CalculateSha256(written.LibraryPath).ToUpperInvariant(),
                    Native: new TestNative(
                        Artifacts: ["native/libprobe-native.a"],
                        IncludeDirectories: ["native/include"],
                        LibraryDirectories: ["native/lib"],
                        RuntimeFiles: ["native/runtime/probe.bin"],
                        LicenseFiles: ["licenses/Probe.txt"],
                        FileChecksums:
                        [
                            new TestFileChecksum("native/runtime/probe.bin", runtimeChecksum.ToUpperInvariant()),
                            new TestFileChecksum("licenses/Probe.txt", licenseChecksum),
                            new TestFileChecksum("native/libprobe-native.a", nativeArchiveChecksum)
                        ]))],
                kind: "release");

            var manifestResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);
            var resolverResult = SdkPackageModuleResolver.Load(manifestResult);

            Assert.True(manifestResult.Succeeded, FormatDiagnostics(manifestResult.Diagnostics));
            Assert.Equal(
                CalculateSha256(written.ImagePath),
                Assert.Single(manifestResult.Manifest!.Packages).ImageSha256);
            Assert.True(resolverResult.Succeeded, FormatDiagnostics(resolverResult.Diagnostics));
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData("native/libprobe-native.a", true)]
    [InlineData("native/runtime/probe.dylib", false)]
    public void PackageResolverRejectsTamperedNativeFiles(
        string relativePath,
        bool isArtifact)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-native-checksum-mismatch-");
        try
        {
            var written = WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            WriteFile(tempDirectory.FullName, relativePath, [1, 2, 3, 4]);
            var nativePath = Path.Combine(tempDirectory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var expectedNativeChecksum = CalculateSha256(nativePath);
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [],
                    ImageSha256: CalculateSha256(written.ImagePath),
                    LibrarySha256: CalculateSha256(written.LibraryPath),
                    Native: new TestNative(
                        Artifacts: isArtifact ? [relativePath] : [],
                        RuntimeFiles: isArtifact ? [] : [relativePath],
                        FileChecksums: [new TestFileChecksum(relativePath, expectedNativeChecksum)]))],
                kind: "release");
            File.AppendAllText(nativePath, "tampered");

            var manifestResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);
            var resolverResult = SdkPackageModuleResolver.Load(manifestResult);

            Assert.True(manifestResult.Succeeded, FormatDiagnostics(manifestResult.Diagnostics));
            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7475");
            Assert.Contains(relativePath, diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("checksum mismatch", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverRequiresDeclaredPackageLibraryFile()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-library-missing-");
        try
        {
            var written = WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            File.Delete(written.LibraryPath);
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage("Vendor.Probe", "packages/Probe.starkpkg", "packages/libProbe.a", [])]);

            var resolverResult = SdkPackageModuleResolver.Load(
                SdkManifestLoader.Parse(json, tempDirectory.FullName));

            Assert.False(resolverResult.Succeeded);
            var diagnostic = Assert.Single(resolverResult.Diagnostics, diagnostic => diagnostic.Code == "STK7466");
            Assert.Contains("missing or is not a file", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void PackageResolverValidatesNativePayloadFileAndDirectoryKinds()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-native-kind-");
        try
        {
            WritePackage(
                tempDirectory.FullName,
                "packages/Probe.starkpkg",
                "packages/libProbe.a",
                "Vendor.Probe");
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "native", "artifact-directory"));
            WriteFile(tempDirectory.FullName, "native/include-file", [1]);
            WriteFile(tempDirectory.FullName, "native/library-file", [2]);
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "native", "runtime-directory"));
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "native", "license-directory"));
            var json = CreateManifestJson(
                [new TestModule("Vendor.Probe", "Vendor.Probe")],
                [new TestPackage(
                    "Vendor.Probe",
                    "packages/Probe.starkpkg",
                    "packages/libProbe.a",
                    [],
                    Native: new TestNative(
                        Artifacts: ["native/artifact-directory"],
                        IncludeDirectories: ["native/include-file"],
                        LibraryDirectories: ["native/library-file"],
                        RuntimeFiles: ["native/runtime-directory"],
                        LicenseFiles: ["native/license-directory"]))]);

            var resolverResult = SdkPackageModuleResolver.Load(
                SdkManifestLoader.Parse(json, tempDirectory.FullName));

            Assert.False(resolverResult.Succeeded);
            Assert.Equal(
                ["STK7470", "STK7471", "STK7472", "STK7473", "STK7474"],
                resolverResult.Diagnostics
                    .Where(static diagnostic => diagnostic.Code.StartsWith("STK747", StringComparison.Ordinal))
                    .Select(static diagnostic => diagnostic.Code));
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestRejectsPackageDependencyCyclesWithCanonicalDeterministicPath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-cycle-");
        try
        {
            var json = CreateManifestJson(
                [
                    new TestModule("Cycle.C", "C"),
                    new TestModule("Cycle.A", "A"),
                    new TestModule("Cycle.B", "B")
                ],
                [
                    new TestPackage("C", "packages/C.starkpkg", "packages/libC.a", ["A"]),
                    new TestPackage("A", "packages/A.starkpkg", "packages/libA.a", ["B"]),
                    new TestPackage("B", "packages/B.starkpkg", "packages/libB.a", ["C"])
                ]);

            var firstResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);
            var secondResult = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(firstResult.Succeeded);
            var firstDiagnostic = Assert.Single(firstResult.Diagnostics, diagnostic => diagnostic.Code == "STK7453");
            var secondDiagnostic = Assert.Single(secondResult.Diagnostics, diagnostic => diagnostic.Code == "STK7453");
            Assert.Equal("SDK package dependency cycle detected: A -> B -> C -> A.", firstDiagnostic.Message);
            Assert.Equal(firstDiagnostic, secondDiagnostic);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void ManifestPreservesTargetFeatureSwitchOrder()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-feature-order-");
        try
        {
            string[] features = ["-avx", "+sse2", "+sse4.2"];
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage("System", "packages/System.starkpkg", "packages/libSystem.a", [])],
                baselineFeatures: features);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.True(result.Succeeded, FormatDiagnostics(result.Diagnostics));
            Assert.Equal(features, result.Manifest!.Target.BaselineFeatures);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Theory]
    [InlineData("+sse2", "+sse2", "declared more than once")]
    [InlineData("+sse2", "-sse2", "conflicting enable and disable")]
    [InlineData("sSe2", "+SSE2", "declared more than once")]
    public void ManifestRejectsRepeatedOrConflictingTargetFeatureSwitches(
        string first,
        string second,
        string expectedMessage)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-feature-conflict-");
        try
        {
            var json = CreateManifestJson(
                [new TestModule("System", "System")],
                [new TestPackage("System", "packages/System.starkpkg", "packages/libSystem.a", [])],
                baselineFeatures: [first, second]);

            var result = SdkManifestLoader.Parse(json, tempDirectory.FullName);

            Assert.False(result.Succeeded);
            var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "STK7408");
            Assert.Contains(expectedMessage, diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(tempDirectory);
        }
    }

    [Fact]
    public void TargetCompatibilityAcceptsSafeArchitectureAliasesVendorDifferencesAndFeatureSupersets()
    {
        var sdkTarget = CreateSdkTarget(
            baselineFeatures: ["+sse2"]);
        var activeTarget = new LlvmTargetInfo(
            "amd64-unknown-linux-gnu",
            "e-p:64:64",
            Cpu: "generic",
            Features: ["+avx", "+sse2"],
            RelocationModel: LlvmRelocationModel.Pic,
            CodeModel: LlvmCodeModel.Small);

        var result = SdkTargetCompatibility.ValidateActiveTarget(sdkTarget, activeTarget);

        Assert.True(result.IsCompatible, FormatDiagnostics(result.Diagnostics));
    }

    [Fact]
    public void TargetCompatibilityAcceptsArm64MacOSAliasesAndNewerApplicationDeploymentMinimum()
    {
        var sdkTarget = CreateSdkTarget(
            llvmTriple: "arm64-apple-macosx13.0",
            architecture: "arm64",
            operatingSystem: "macos",
            abi: "darwin",
            minimumOperatingSystemVersion: "13.0");
        var activeTarget = new LlvmTargetInfo(
            "aarch64-apple-macosx14.0",
            "e-p:64:64",
            Cpu: "generic",
            RelocationModel: LlvmRelocationModel.Pic,
            CodeModel: LlvmCodeModel.Small);

        var result = SdkTargetCompatibility.ValidateActiveTarget(sdkTarget, activeTarget);

        Assert.True(result.IsCompatible, FormatDiagnostics(result.Diagnostics));
    }

    [Theory]
    [InlineData("x86_64")]
    [InlineData("aarch64")]
    public void TargetCompatibilityAcceptsVersionedMsvcEnvironmentAsTheMsvcAbiFamily(string architecture)
    {
        var sdkTarget = CreateSdkTarget(
            llvmTriple: $"{architecture}-pc-windows-msvc",
            architecture: architecture,
            operatingSystem: "windows",
            abi: "msvc",
            cDataModel: "llp64");
        var activeTarget = new LlvmTargetInfo(
            $"{architecture}-pc-windows-msvc1.2.3",
            "e-p:64:64",
            Cpu: "generic",
            RelocationModel: LlvmRelocationModel.Pic,
            CodeModel: LlvmCodeModel.Small);

        var result = SdkTargetCompatibility.ValidateActiveTarget(sdkTarget, activeTarget);

        Assert.True(result.IsCompatible, FormatDiagnostics(result.Diagnostics));
    }

    [Theory]
    [InlineData("x86_64-pc-windows-msvc", "STK7483")]
    [InlineData("x86_64-unknown-linux-musl", "STK7484")]
    [InlineData("x86_64-unknown-linux", "STK7484")]
    public void TargetCompatibilityRejectsOperatingSystemAndAbiEnvironmentDifferences(
        string activeTriple,
        string expectedCode)
    {
        var activeTarget = new LlvmTargetInfo(
            activeTriple,
            "e-p:64:64",
            Cpu: "generic",
            RelocationModel: LlvmRelocationModel.Pic,
            CodeModel: LlvmCodeModel.Small);

        var result = SdkTargetCompatibility.ValidateActiveTarget(CreateSdkTarget(), activeTarget);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void TargetCompatibilityRejectsEndiannessAndExactDataLayoutDifferences()
    {
        var activeTarget = new LlvmTargetInfo(
            "x86_64-unknown-linux-gnu",
            "E-p:64:64",
            Cpu: "generic",
            RelocationModel: LlvmRelocationModel.Pic,
            CodeModel: LlvmCodeModel.Small);

        var result = SdkTargetCompatibility.ValidateActiveTarget(CreateSdkTarget(), activeTarget);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7486");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7487");
    }

    [Fact]
    public void TargetCompatibilityRejectsPointerWidthEncodedByActiveDataLayout()
    {
        var activeTarget = new LlvmTargetInfo(
            "x86_64-unknown-linux-gnu",
            "e-p:32:32",
            Cpu: "generic",
            RelocationModel: LlvmRelocationModel.Pic,
            CodeModel: LlvmCodeModel.Small);

        var result = SdkTargetCompatibility.ValidateActiveTarget(CreateSdkTarget(), activeTarget);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7485");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7487");
    }

    [Fact]
    public void TargetCompatibilityRejectsRelocationCodeModelAndBaselineCpuDifferences()
    {
        var sdkTarget = CreateSdkTarget(baselineCpu: "x86-64-v3");
        var activeTarget = new LlvmTargetInfo(
            "x86_64-unknown-linux-gnu",
            "e-p:64:64",
            Cpu: "generic",
            RelocationModel: LlvmRelocationModel.Static,
            CodeModel: LlvmCodeModel.Large);

        var result = SdkTargetCompatibility.ValidateActiveTarget(sdkTarget, activeTarget);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7490");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7491");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7493");
    }

    [Fact]
    public void TargetCompatibilityRejectsMissingOrExplicitlyDisabledRequiredFeatures()
    {
        var sdkTarget = CreateSdkTarget(baselineFeatures: ["+sse2", "+sse4.2"]);
        var activeTarget = new LlvmTargetInfo(
            "x86_64-unknown-linux-gnu",
            "e-p:64:64",
            Cpu: "generic",
            Features: ["+sse2", "-sse4.2"],
            RelocationModel: LlvmRelocationModel.Pic,
            CodeModel: LlvmCodeModel.Small);

        var result = SdkTargetCompatibility.ValidateActiveTarget(sdkTarget, activeTarget);

        Assert.False(result.IsCompatible);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "STK7492");
        Assert.Contains("sse4.2", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetCompatibilityRejectsPackagePointerWidthAndDetailedCModelDifferences()
    {
        var packageTarget = CreatePackageTarget(
            dataLayout: "e-p:32:32",
            cDataModel: new StarkPackageCDataModelManifest(
                "ILP32",
                CharIsSigned: true,
                PointerBitWidth: 32,
                LongBitWidth: 32,
                SizeTBitWidth: 32,
                PtrDiffTBitWidth: 32),
            aggregateLayout: new StarkPackageAggregateLayoutManifest(4, 4));

        var result = SdkTargetCompatibility.ValidatePackageTarget(
            CreateSdkTarget(),
            packageTarget,
            "Vendor.Probe");

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7485");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7488");
    }

    [Fact]
    public void TargetCompatibilityRejectsPackageFeaturesAndCpuOutsideSdkBaseline()
    {
        var sdkTarget = CreateSdkTarget(
            baselineCpu: "x86-64-v2",
            baselineFeatures: ["+sse2"]);
        var packageTarget = CreatePackageTarget(
            cpu: "x86-64-v3",
            features: ["+avx", "+sse2"]);

        var result = SdkTargetCompatibility.ValidatePackageTarget(
            sdkTarget,
            packageTarget,
            "Vendor.Probe");

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7492");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK7493");
    }

    [Fact]
    public void TargetCompatibilityAcceptsPackageAtOrBelowSdkFeatureCpuAndDeploymentBaseline()
    {
        var sdkTarget = CreateSdkTarget(
            llvmTriple: "aarch64-apple-macosx13.0",
            architecture: "aarch64",
            operatingSystem: "darwin",
            abi: "macos",
            baselineCpu: "apple-m1",
            baselineFeatures: ["+neon", "+fp-armv8"],
            minimumOperatingSystemVersion: "13.0");
        var packageTarget = CreatePackageTarget(
            triple: "arm64-apple-macosx12.0",
            cpu: "generic",
            features: ["+neon"],
            cDataModel: CreateLp64CDataModel(),
            aggregateLayout: new StarkPackageAggregateLayoutManifest(8, 8));

        var result = SdkTargetCompatibility.ValidatePackageTarget(
            sdkTarget,
            packageTarget,
            "Vendor.Probe");

        Assert.True(result.IsCompatible, FormatDiagnostics(result.Diagnostics));
    }

    [Theory]
    [InlineData("aarch64-apple-macosx12.0", false, "older")]
    [InlineData("aarch64-apple-darwin", false, "does not expose")]
    [InlineData("aarch64-apple-macosx14.0", true, "newer")]
    public void TargetCompatibilityEnforcesDeploymentMinimumInTheCorrectDirection(
        string triple,
        bool packageComparison,
        string expectedMessageFragment)
    {
        var sdkTarget = CreateSdkTarget(
            llvmTriple: "aarch64-apple-macosx13.0",
            architecture: "aarch64",
            operatingSystem: "macos",
            abi: "darwin",
            minimumOperatingSystemVersion: "13.0");

        var result = packageComparison
            ? SdkTargetCompatibility.ValidatePackageTarget(
                sdkTarget,
                CreatePackageTarget(
                    triple: triple,
                    cDataModel: CreateLp64CDataModel(),
                    aggregateLayout: new StarkPackageAggregateLayoutManifest(8, 8)),
                "Vendor.Probe")
            : SdkTargetCompatibility.ValidateActiveTarget(
                sdkTarget,
                new LlvmTargetInfo(
                    triple,
                    "e-p:64:64",
                    Cpu: "generic",
                    RelocationModel: LlvmRelocationModel.Pic,
                    CodeModel: LlvmCodeModel.Small));

        Assert.False(result.IsCompatible);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "STK7489");
        Assert.Contains(expectedMessageFragment, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetCompatibilityRejectsPackageDetailedCModelEvenWhenKindMatches()
    {
        var packageTarget = CreatePackageTarget(
            cDataModel: new StarkPackageCDataModelManifest(
                "LP64",
                CharIsSigned: false,
                PointerBitWidth: 64,
                LongBitWidth: 64,
                SizeTBitWidth: 64,
                PtrDiffTBitWidth: 64));

        var result = SdkTargetCompatibility.ValidatePackageTarget(
            CreateSdkTarget(),
            packageTarget,
            "Vendor.Probe");

        Assert.False(result.IsCompatible);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == "STK7488" && diagnostic.Message.Contains("detailed", StringComparison.Ordinal));
        Assert.Contains("do not match", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetCompatibilityDiagnosesSdkStructuredFactsThatDisagreeWithItsFullTriple()
    {
        var inconsistentSdkTarget = CreateSdkTarget(architecture: "arm64");

        var result = SdkTargetCompatibility.ValidateActiveTarget(
            inconsistentSdkTarget,
            new LlvmTargetInfo(
                "aarch64-unknown-linux-gnu",
                "e-p:64:64",
                Cpu: "generic",
                RelocationModel: LlvmRelocationModel.Pic,
                CodeModel: LlvmCodeModel.Small));

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "STK7481"
            && diagnostic.Message.Contains("disagrees with LLVM triple", StringComparison.Ordinal));
    }

    private static string CreateManifestJson(
        IReadOnlyList<TestModule> modules,
        IReadOnlyList<TestPackage> packages,
        int schemaVersion = 1,
        string kind = "development",
        IReadOnlyList<string>? developmentSourceRoots = null,
        int packageFormatVersion = (int)PackageImageBinaryFormat.CurrentFormatVersion,
        IReadOnlyList<string>? baselineFeatures = null,
        string compilerCompatibility = SdkCompilerCompatibility.SupportedLine)
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion,
            kind,
            sdkVersion = "0.1.0-test",
            compilerCompatibility,
            packageFormatVersion,
            target = new
            {
                id = "test-x64",
                llvmTriple = "x86_64-unknown-linux-gnu",
                architecture = "x86_64",
                operatingSystem = "linux",
                abi = "gnu",
                pointerBitWidth = 64,
                endianness = "little",
                dataLayout = "e-p:64:64",
                baselineCpu = "generic",
                baselineFeatures = baselineFeatures ?? Array.Empty<string>(),
                relocationModel = "pic",
                codeModel = "small",
                cDataModel = "lp64"
            },
            modules = modules.Select(static module => new
            {
                name = module.Name,
                package = module.PackageId
            }),
            packages = packages.Select(static package => new
            {
                id = package.Id,
                version = "1.0.0",
                profile = package.Profile,
                image = package.Image,
                library = package.Library,
                imageSha256 = package.ImageSha256,
                librarySha256 = package.LibrarySha256,
                dependencies = package.Dependencies.Select(static dependency => new { id = dependency }),
                native = new
                {
                    artifacts = package.Native?.Artifacts ?? Array.Empty<string>(),
                    includeDirectories = package.Native?.IncludeDirectories ?? Array.Empty<string>(),
                    libraryDirectories = package.Native?.LibraryDirectories ?? Array.Empty<string>(),
                    runtimeFiles = package.Native?.RuntimeFiles ?? Array.Empty<string>(),
                    licenseFiles = package.Native?.LicenseFiles ?? Array.Empty<string>(),
                    fileChecksums = package.Native?.FileChecksums?
                        .Select(static checksum => (object)new
                        {
                            path = checksum.Path,
                            sha256 = checksum.Sha256
                        })
                        ?? Array.Empty<object>(),
                    libraries = Array.Empty<string>(),
                    linkArguments = Array.Empty<string>()
                }
            }),
            developmentSourceRoots = developmentSourceRoots ?? Array.Empty<string>()
        });
    }

    private static SdkTargetDescriptor CreateSdkTarget(
        string llvmTriple = "x86_64-pc-linux-gnu",
        string architecture = "x86_64",
        string operatingSystem = "linux",
        string abi = "gnu",
        int pointerBitWidth = 64,
        SdkEndianness endianness = SdkEndianness.Little,
        string? dataLayout = "e-p:64:64",
        string? baselineCpu = "generic",
        IReadOnlyList<string>? baselineFeatures = null,
        string relocationModel = "pic",
        string? codeModel = "small",
        string? cDataModel = "lp64",
        string? minimumOperatingSystemVersion = null)
    {
        return new SdkTargetDescriptor(
            "test-target",
            llvmTriple,
            architecture,
            operatingSystem,
            abi,
            pointerBitWidth,
            endianness,
            dataLayout,
            baselineCpu,
            baselineFeatures ?? Array.Empty<string>(),
            relocationModel,
            codeModel,
            cDataModel,
            minimumOperatingSystemVersion);
    }

    private static SdkPackageDescriptor CreatePackageDescriptor(
        string id,
        params string[] dependencyIds)
    {
        return new SdkPackageDescriptor(
            id,
            Version: "test",
            Profile: "release",
            ImagePath: $"packages/{id}.starkpkg",
            LibraryPath: $"packages/lib{id}.a",
            ApiHash: null,
            ContentHash: null,
            ImageSha256: null,
            LibrarySha256: null,
            Dependencies: dependencyIds
                .Select(static dependencyId => new SdkPackageDependency(dependencyId, null, null))
                .ToArray(),
            Native: new SdkNativePackageDescriptor([], [], [], [], [], [], [], []));
    }

    private static LoadedModuleDocument CreateSourceModuleDocument(
        string moduleName,
        string sourcePath,
        bool isRoot)
    {
        var parseResult = StarkSyntax.ParseCompilationUnit($"module {moduleName}\n");
        return new LoadedModuleDocument(
            new ResolvedModuleReference(moduleName, sourcePath, IsExternal: false, IsRoot: isRoot),
            parseResult,
            SyntaxModelFactory.Create(parseResult));
    }

    private static SdkNativePackageDescriptor CreateRuntimeDescriptor(
        string relativePath,
        string checksum) =>
        new(
            ArtifactPaths: [],
            IncludeDirectories: [],
            LibraryDirectories: [],
            RuntimeFiles: [relativePath],
            LicenseFiles: [],
            FileChecksums: [new SdkNativeFileChecksum(relativePath, checksum)],
            Libraries: [],
            LinkArguments: []);

    private static StarkPackageTargetManifest CreatePackageTarget(
        string triple = "x86_64-unknown-linux-gnu",
        string? dataLayout = "e-p:64:64",
        string? cpu = "generic",
        IReadOnlyList<string>? features = null,
        string relocationModel = "pic",
        string? codeModel = "small",
        StarkPackageCDataModelManifest? cDataModel = null,
        StarkPackageAggregateLayoutManifest? aggregateLayout = null)
    {
        return new StarkPackageTargetManifest(
            triple,
            dataLayout,
            cpu,
            features ?? Array.Empty<string>(),
            relocationModel,
            codeModel,
            cDataModel ?? CreateLp64CDataModel(),
            aggregateLayout ?? new StarkPackageAggregateLayoutManifest(8, 8));
    }

    private static StarkPackageCDataModelManifest CreateLp64CDataModel() =>
        new(
            "LP64",
            CharIsSigned: true,
            PointerBitWidth: 64,
            LongBitWidth: 64,
            SizeTBitWidth: 64,
            PtrDiffTBitWidth: 64);

    private static WrittenPackage WritePackage(
        string sdkRoot,
        string imageRelativePath,
        string libraryRelativePath,
        string moduleName,
        string? buildProfile = "release")
    {
        var imagePath = Path.Combine(sdkRoot, imageRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var libraryPath = Path.Combine(sdkRoot, libraryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        File.WriteAllBytes(libraryPath, []);
        var module = new StarkPackageModuleManifest(moduleName, [], [], [], []);
        var manifest = PackageImageIdentity.Apply(new StarkPackageManifest(
            moduleName,
            Path.GetFileName(libraryPath),
            [module],
            BuildProfile: buildProfile is null
                ? null
                : new StarkPackageBuildProfileManifest(buildProfile)));
        File.WriteAllBytes(imagePath, PackageImageBinaryFormat.Encode(manifest));
        return new WrittenPackage(imagePath, libraryPath);
    }

    private static void WriteFile(string sdkRoot, string relativePath, byte[] contents)
    {
        var path = Path.Combine(sdkRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private static void ReplaceDirectoryWithExternalSymlink(string directoryPath, string externalPath)
    {
        Directory.Move(directoryPath, externalPath);
        Directory.CreateSymbolicLink(directoryPath, externalPath);
    }

    private static void DeleteDirectoryLink(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            directory.Refresh();
            if (directory.LinkTarget is not null)
            {
                directory.Delete();
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string CalculateSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string CorruptSha256(string checksum) =>
        (checksum[0] == '0' ? '1' : '0') + checksum[1..];

    private static string FormatDiagnostics(IEnumerable<SdkDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    private static void CleanUp(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed record TestModule(string Name, string PackageId);

    private sealed record TestPackage(
        string Id,
        string Image,
        string Library,
        IReadOnlyList<string> Dependencies,
        string? ImageSha256 = null,
        string? LibrarySha256 = null,
        TestNative? Native = null,
        string Profile = "release");

    private sealed record TestNative(
        IReadOnlyList<string>? Artifacts = null,
        IReadOnlyList<string>? IncludeDirectories = null,
        IReadOnlyList<string>? LibraryDirectories = null,
        IReadOnlyList<string>? RuntimeFiles = null,
        IReadOnlyList<string>? LicenseFiles = null,
        IReadOnlyList<TestFileChecksum>? FileChecksums = null);

    private sealed record TestFileChecksum(string Path, string Sha256);

    private sealed record WrittenPackage(string ImagePath, string LibraryPath);
}
