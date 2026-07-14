using System.Security.Cryptography;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class PackageImageSdkParityIntegrationTests
{
    [Fact]
    public async Task RealSystemBitOperationsPreservesSelectedTypedAndLlvmLoweringFacts()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var stdlibRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var systemSourcePath = Path.Combine(stdlibRoot, "System", "BitOperations.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-system-parity-");
        var packagePath = Path.Combine(tempDirectory.FullName, "SystemBitOperations.starkpkg");
        var libraryPath = Path.Combine(tempDirectory.FullName, NativeLibraryFileName("SystemBitOperations"));
        var consumerPath = Path.Combine(tempDirectory.FullName, "Consumer.stark");
        const string consumerSource = """
            import System.BitOperations
            module Consumer

            export fn i32[min max] main()
            {
                return PopCount(42);
            }
            """;

        try
        {
            await AssertCompilerSucceedsAsync(
                [
                    systemSourcePath,
                    "--emit-lib",
                    "--package-image-output", packagePath,
                    "--package-profile", "release",
                    "--target", targetInfo.Triple,
                    "--no-stark-path",
                    "-o", libraryPath
                ]);

            var pipeline = DefaultCompilerPipeline.Create();
            var sourceResult = pipeline.Run(
                new CompilationInput(consumerSource, consumerPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(stdlibRoot),
                    TargetInfo: targetInfo));
            var packageResult = pipeline.Run(
                new CompilationInput(consumerSource, consumerPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    TargetInfo: targetInfo));

            Assert.True(sourceResult.Succeeded, FormatDiagnostics(sourceResult));
            Assert.True(packageResult.Succeeded, FormatDiagnostics(packageResult));
            var sourceCall = GetPopCountCall(sourceResult);
            var packageCall = GetPopCountCall(packageResult);
            Assert.Equal(RenderTypedSignature(sourceCall.Signature), RenderTypedSignature(packageCall.Signature));
            Assert.Equal(
                sourceCall.Arguments.Select(RenderCallArgument),
                packageCall.Arguments.Select(RenderCallArgument));

            var sourceLlvm = sourceResult.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            var packageLlvm = packageResult.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            // A package image publishes the callee's closed-world semantic summary,
            // while a raw source import is not compiled as an independent root. That
            // lets the package path strengthen conservative source-import attributes.
            // The selected implementation bodies must remain byte-identical, and the
            // published memory/no-recurse facts must reach LLVM rather than being lost.
            Assert.Equal(
                ExtractDefinitionBody(sourceLlvm, "@main("),
                ExtractDefinitionBody(packageLlvm, "@main("));
            Assert.Equal(
                ExtractDefinitionBody(sourceLlvm, "@System_BitOperations_PopCount"),
                ExtractDefinitionBody(packageLlvm, "@System_BitOperations_PopCount"));
            Assert.Contains(
                "memory(none)",
                ExtractDefinitionHeader(packageLlvm, "@System_BitOperations_PopCount"),
                StringComparison.Ordinal);
            Assert.Contains(
                "norecurse",
                ExtractDefinitionHeader(packageLlvm, "@main("),
                StringComparison.Ordinal);
            Assert.Contains("@llvm.ctpop.i32", packageLlvm, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            Assert.Empty(PackageImageLoader.ValidateManifest(manifest, packagePath));
            Assert.Equal("System.BitOperations", manifest.RootModule);
            Assert.IsType<StarkPackageIdentityManifest>(manifest.Identity);
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    [Fact]
    public async Task NativeBackedVendorPackagePreservesTypedAndLlvmFactsFromSourceExactly()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-vendor-parity-");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "source"));
        var packageDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "package"));
        var vendorSourcePath = Path.Combine(sourceDirectory.FullName, "Probe.stark");
        var consumerSourcePath = Path.Combine(tempDirectory.FullName, "Consumer.stark");
        var packagePath = Path.Combine(packageDirectory.FullName, "libVendorProbe.starkpkg");
        var sourceLlvmPath = Path.Combine(tempDirectory.FullName, "source.ll");
        var packageLlvmPath = Path.Combine(tempDirectory.FullName, "package.ll");

        try
        {
            await File.WriteAllTextAsync(
                vendorSourcePath,
                """
                module Vendor.Probe

                [LinkName("probe_add")]
                public unsafe ffi fn i32[min max] NativeAdd(
                    i32[min max] left,
                    i32[min max] right);
                """);
            await File.WriteAllTextAsync(
                consumerSourcePath,
                """
                import Vendor.Probe
                module Consumer

                export unsafe fn i32[min max] main()
                {
                    unsafe
                    {
                        return NativeAdd(20, 22);
                    }
                }
                """);

            await AssertCompilerSucceedsAsync(
                [
                    vendorSourcePath,
                    "--emit-pkg",
                    "--package-library-file", "libVendorProbe.a",
                    "--native-library", "probe_native",
                    "--native-link-arg=-pthread",
                    "-o", packagePath
                ]);
            await AssertCompilerSucceedsAsync(
                [consumerSourcePath, "--emit-llvm", "-I", sourceDirectory.FullName, "-o", sourceLlvmPath]);
            await AssertCompilerSucceedsAsync(
                [consumerSourcePath, "--emit-llvm", "-I", packageDirectory.FullName, "-o", packageLlvmPath]);

            Assert.Equal(
                await File.ReadAllTextAsync(sourceLlvmPath),
                await File.ReadAllTextAsync(packageLlvmPath));
            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            Assert.Empty(PackageImageLoader.ValidateManifest(manifest, packagePath));
            var identity = Assert.IsType<StarkPackageIdentityManifest>(manifest.Identity);
            Assert.Equal("Vendor.Probe", identity.PackageId);
            Assert.True(PackageImageIdentity.IsSha256(identity.ApiHash));
            Assert.True(PackageImageIdentity.IsSha256(identity.ContentHash));
            Assert.Empty(identity.Dependencies);
            Assert.Equal("probe_native", Assert.Single(manifest.NativeDependencies!.Libraries!));
            Assert.Equal("-pthread", Assert.Single(manifest.NativeDependencies.LinkArguments!));
            var module = Assert.Single(manifest.Modules);
            var function = Assert.Single(module.EffectiveTypedInterface!.Functions);
            Assert.True(function.IsFfi);
            Assert.Equal("probe_add", function.LinkName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task SelectedSdkGraphPreservesArchiveAndNativeOrderWithoutTouchingUnusedVendorOrOptimizations()
    {
        if (OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-sdk-selected-link-");
        var sdkRoot = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "sdk"));
        var packageDirectory = Directory.CreateDirectory(Path.Combine(sdkRoot.FullName, "packages"));
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var executablePath = Path.Combine(tempDirectory.FullName, "app");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        var systemSourcePath = Path.Combine(tempDirectory.FullName, "SystemBase.stark");
        var usedSourcePath = Path.Combine(tempDirectory.FullName, "Used.stark");
        var systemImagePath = Path.Combine(packageDirectory.FullName, "SystemBase.starkpkg");
        var usedImagePath = Path.Combine(packageDirectory.FullName, "VendorUsed.starkpkg");
        var unusedImagePath = Path.Combine(packageDirectory.FullName, "VendorUnused.missing.starkpkg");
        var systemLibraryPath = Path.Combine(packageDirectory.FullName, NativeLibraryFileName("SystemBase"));
        var usedLibraryPath = Path.Combine(packageDirectory.FullName, NativeLibraryFileName("VendorUsed"));
        var unusedLibraryPath = Path.Combine(packageDirectory.FullName, NativeLibraryFileName("VendorUnused"));
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            await File.WriteAllTextAsync(
                systemSourcePath,
                """
                module System.Base

                public finite law i32[min max] BaseValue();
                """);
            await File.WriteAllTextAsync(
                usedSourcePath,
                """
                import System.Base
                module Vendor.Used

                public finite law i32[min max] UsedValue();
                """);
            await File.WriteAllTextAsync(
                appPath,
                """
                import Vendor.Used
                module App

                export fn i32[min max] main()
                {
                    return UsedValue();
                }
                """);

            await AssertCompilerSucceedsAsync(
                [
                    systemSourcePath,
                    "--emit-pkg",
                    "--package-library-file", Path.GetFileName(systemLibraryPath),
                    "--package-profile", "release",
                    "--native-library", "system_native",
                    "--native-link-arg=-Wl,system-last",
                    "--target", targetInfo.Triple,
                    "--no-stark-path",
                    "-o", systemImagePath
                ]);
            await File.WriteAllBytesAsync(systemLibraryPath, []);
            await AssertCompilerSucceedsAsync(
                [
                    usedSourcePath,
                    "--emit-pkg",
                    "--package-library-file", Path.GetFileName(usedLibraryPath),
                    "--package-profile", "release",
                    "--native-library", "used_native",
                    "--native-link-arg=-Wl,used-first",
                    "--target", targetInfo.Triple,
                    "-I", packageDirectory.FullName,
                    "--no-stark-path",
                    "-o", usedImagePath
                ]);
            await File.WriteAllBytesAsync(usedLibraryPath, []);

            Assert.True(PackageImageLoader.TryLoadManifest(systemImagePath, out var systemManifest));
            Assert.True(PackageImageLoader.TryLoadManifest(usedImagePath, out var usedManifest));
            var systemIdentity = Assert.IsType<StarkPackageIdentityManifest>(systemManifest.Identity);
            var usedIdentity = Assert.IsType<StarkPackageIdentityManifest>(usedManifest.Identity);
            var usedDependency = Assert.Single(usedIdentity.Dependencies);
            Assert.Equal(systemIdentity.PackageId, usedDependency.PackageId);
            Assert.Equal(systemIdentity.ApiHash, usedDependency.ApiHash);
            Assert.Equal(systemIdentity.ContentHash, usedDependency.ContentHash);

            await File.WriteAllTextAsync(
                Path.Combine(sdkRoot.FullName, SdkRootResolver.ManifestFileName),
                CreateSelectedGraphSdkJson(
                    sdkRoot.FullName,
                    targetInfo,
                    systemImagePath,
                    systemLibraryPath,
                    systemIdentity,
                    usedImagePath,
                    usedLibraryPath,
                    usedIdentity,
                    unusedImagePath,
                    unusedLibraryPath));

            await CreateAppendCaptureClangAsync(tempDirectory.FullName, clangLogPath);
            var lldPath = Path.Combine(tempDirectory.FullName, "ld.lld");
            await File.WriteAllTextAsync(lldPath, "#!/usr/bin/env bash\nexit 0\n");
            MakeExecutable(lldPath);
            Environment.SetEnvironmentVariable(
                "PATH",
                $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");

            var deadStripArgument = OperatingSystem.IsMacOS()
                ? "-Wl,-dead_strip"
                : "-Wl,--gc-sections";
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    appPath,
                    "--emit-exe",
                    "--sdk-root", sdkRoot.FullName,
                    "--no-stark-path",
                    $"--link-arg={deadStripArgument}",
                    "-o", executablePath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.True(File.Exists(executablePath));
            Assert.False(File.Exists(unusedImagePath));
            Assert.False(File.Exists(unusedLibraryPath));

            var clangLines = await File.ReadAllLinesAsync(clangLogPath);
            var linkLine = Assert.Single(
                clangLines,
                line => line.Contains(Path.GetFullPath(executablePath), StringComparison.Ordinal));
            AssertOrder(linkLine, Path.GetFullPath(usedLibraryPath), Path.GetFullPath(systemLibraryPath));
            AssertOrder(linkLine, "-lused_native", "-Wl,used-first", "-lsystem_native", "-Wl,system-last");
            Assert.DoesNotContain("VendorUnused", linkLine, StringComparison.Ordinal);
            Assert.DoesNotContain("unused_native", linkLine, StringComparison.Ordinal);
            Assert.DoesNotContain("unused-never", linkLine, StringComparison.Ordinal);
            Assert.Contains("-flto=thin", linkLine, StringComparison.Ordinal);
            Assert.Contains("-O3", linkLine, StringComparison.Ordinal);
            Assert.Contains("-fuse-ld=lld", linkLine, StringComparison.Ordinal);
            Assert.Contains(deadStripArgument, linkLine, StringComparison.Ordinal);
            Assert.Contains(
                clangLines,
                line => line.Contains("-c", StringComparison.Ordinal)
                    && line.Contains("-ffunction-sections", StringComparison.Ordinal)
                    && line.Contains("-fdata-sections", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            TryDelete(tempDirectory);
        }
    }

    private static async Task AssertCompilerSucceedsAsync(string[] arguments)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            arguments,
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    private static DirectCallTypingRecord GetPopCountCall(CompilationResult result)
    {
        var model = result.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
        return Assert.Single(
            model.DirectCalls,
            call => call.Signature.DisplaySourceName.EndsWith("PopCount", StringComparison.Ordinal));
    }

    private static string ExtractDefinitionHeader(string llvm, string symbolMarker)
    {
        var (definitionStart, bodyStart, _) = FindDefinition(llvm, symbolMarker);
        return llvm[definitionStart..bodyStart].TrimEnd();
    }

    private static string ExtractDefinitionBody(string llvm, string symbolMarker)
    {
        var (_, bodyStart, bodyEnd) = FindDefinition(llvm, symbolMarker);
        return llvm[(bodyStart + 1)..bodyEnd];
    }

    private static (int DefinitionStart, int BodyStart, int BodyEnd) FindDefinition(
        string llvm,
        string symbolMarker)
    {
        var searchStart = 0;
        while (true)
        {
            var definitionStart = llvm.IndexOf("define ", searchStart, StringComparison.Ordinal);
            if (definitionStart < 0)
            {
                Assert.Fail($"Expected LLVM definition containing '{symbolMarker}'.");
            }

            var bodyStart = llvm.IndexOf('{', definitionStart);
            Assert.True(bodyStart >= 0, $"Expected definition body after offset {definitionStart}.");
            if (llvm.AsSpan(definitionStart, bodyStart - definitionStart)
                .Contains(symbolMarker, StringComparison.Ordinal))
            {
                var bodyEnd = llvm.IndexOf("\n}", bodyStart, StringComparison.Ordinal);
                Assert.True(bodyEnd >= 0, $"Expected definition terminator for '{symbolMarker}'.");
                return (definitionStart, bodyStart, bodyEnd);
            }

            searchStart = bodyStart + 1;
        }
    }

    private static string RenderTypedSignature(TypedFunctionSignature signature) =>
        string.Join(
            '|',
            signature.Name,
            signature.DisplaySourceName,
            signature.ReturnType.DisplayName,
            string.Join(",", signature.Parameters.Select(parameter =>
                $"{parameter.Name}:{parameter.Type.DisplayName}:disjoint={parameter.IsDisjoint}:const={parameter.IsConst}:count={parameter.RawPointerElementCountExpression}")),
            signature.Kind,
            signature.IsUnsafe,
            signature.IsVarargs,
            signature.FfiAbi,
            signature.BackendOptimizationMode,
            signature.Visibility,
            signature.ExternalLinkName);

    private static string RenderCallArgument(CallArgumentTypingRecord argument) =>
        string.Join(
            '|',
            argument.ParameterIndex,
            argument.SourceArgumentIndex,
            argument.ParameterType.DisplayName,
            argument.ArgumentType.DisplayName,
            argument.IsReceiver,
            argument.RequiresAddressable,
            argument.RequiresMutable,
            argument.RequiresConstProvenance,
            argument.ArgumentIsAddressable,
            argument.ArgumentIsMutable,
            argument.ArgumentHasConstProvenance);

    private static string CreateSelectedGraphSdkJson(
        string sdkRoot,
        LlvmTargetInfo targetInfo,
        string systemImagePath,
        string systemLibraryPath,
        StarkPackageIdentityManifest systemIdentity,
        string usedImagePath,
        string usedLibraryPath,
        StarkPackageIdentityManifest usedIdentity,
        string unusedImagePath,
        string unusedLibraryPath)
    {
        static object Native(IReadOnlyList<string> libraries, IReadOnlyList<string> linkArguments) => new
        {
            artifacts = Array.Empty<string>(),
            includeDirectories = Array.Empty<string>(),
            libraryDirectories = Array.Empty<string>(),
            runtimeFiles = Array.Empty<string>(),
            licenseFiles = Array.Empty<string>(),
            fileChecksums = Array.Empty<object>(),
            libraries,
            linkArguments
        };

        object Package(
            string id,
            string imagePath,
            string libraryPath,
            StarkPackageIdentityManifest identity,
            object native) => new
        {
            id,
            version = "test",
            profile = "release",
            apiHash = identity.ApiHash,
            contentHash = identity.ContentHash,
            image = RelativePath(sdkRoot, imagePath),
            library = RelativePath(sdkRoot, libraryPath),
            imageSha256 = Sha256(imagePath),
            librarySha256 = Sha256(libraryPath),
            dependencies = identity.Dependencies.Select(dependency => new
            {
                id = dependency.PackageId,
                apiHash = dependency.ApiHash,
                contentHash = dependency.ContentHash
            }),
            native
        };

        var unusedHash = new string('e', 64);
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                kind = "release",
                sdkVersion = "test",
                compilerCompatibility = SdkCompilerCompatibility.SupportedLine,
                packageFormatVersion = (int)PackageImageBinaryFormat.CurrentFormatVersion,
                target = new
                {
                    id = "test-host",
                    llvmTriple = targetInfo.Triple,
                    architecture = TargetArchitecture(targetInfo.Triple),
                    operatingSystem = TargetOperatingSystem(targetInfo.Triple),
                    abi = TargetAbi(targetInfo.Triple),
                    pointerBitWidth = IntPtr.Size * 8,
                    endianness = BitConverter.IsLittleEndian ? "little" : "big",
                    dataLayout = targetInfo.DataLayout,
                    baselineCpu = targetInfo.Cpu,
                    baselineFeatures = targetInfo.Features ?? Array.Empty<string>(),
                    relocationModel = targetInfo.RelocationModel.ToString().ToLowerInvariant(),
                    codeModel = targetInfo.CodeModel?.ToString().ToLowerInvariant()
                },
                modules = new object[]
                {
                    new { name = "System.Base", package = "System.Base" },
                    new { name = "Vendor.Used", package = "Vendor.Used" },
                    new { name = "Vendor.Unused", package = "Vendor.Unused" }
                },
                packages = new object[]
                {
                    Package(
                        "System.Base",
                        systemImagePath,
                        systemLibraryPath,
                        systemIdentity,
                        Native(["system_native"], ["-Wl,system-last"])),
                    Package(
                        "Vendor.Used",
                        usedImagePath,
                        usedLibraryPath,
                        usedIdentity,
                        Native(["used_native"], ["-Wl,used-first"])),
                    new
                    {
                        id = "Vendor.Unused",
                        version = "test",
                        profile = "release",
                        apiHash = unusedHash,
                        contentHash = unusedHash,
                        image = RelativePath(sdkRoot, unusedImagePath),
                        library = RelativePath(sdkRoot, unusedLibraryPath),
                        imageSha256 = unusedHash,
                        librarySha256 = unusedHash,
                        dependencies = Array.Empty<object>(),
                        native = Native(["unused_native"], ["-Wl,unused-never"])
                    }
                },
                developmentSourceRoots = Array.Empty<string>()
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task CreateAppendCaptureClangAsync(string directory, string logPath)
    {
        var path = Path.Combine(directory, "clang");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> "{{logPath}}"
            out=""
            prev=""
            for arg in "$@"; do
              if [ "$prev" = "-o" ]; then
                out="$arg"
                break
              fi
              prev="$arg"
            done
            if [ -n "$out" ]; then
              : > "$out"
            fi
            """);
        MakeExecutable(path);
    }

    private static void MakeExecutable(string path)
    {
        using var process = System.Diagnostics.Process.Start("chmod", $"+x {path}");
        process!.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void AssertOrder(string text, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = text.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{value}' after offset {previous} in:{Environment.NewLine}{text}");
            previous = current;
        }
    }

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string NativeLibraryFileName(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.lib" : $"lib{name}.a";

    private static string TargetArchitecture(string triple)
    {
        var architecture = triple.Split('-', 2)[0].ToLowerInvariant();
        return architecture is "arm64" or "aarch64"
            ? "arm64"
            : architecture is "x86_64" or "amd64"
                ? "x86_64"
                : architecture;
    }

    private static string TargetOperatingSystem(string triple) =>
        triple.Contains("darwin", StringComparison.OrdinalIgnoreCase)
        || triple.Contains("macos", StringComparison.OrdinalIgnoreCase)
            ? "macos"
            : triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
              || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase)
                ? "windows"
                : "linux";

    private static string TargetAbi(string triple)
    {
        if (triple.Contains("darwin", StringComparison.OrdinalIgnoreCase)
            || triple.Contains("macos", StringComparison.OrdinalIgnoreCase))
        {
            return "darwin";
        }

        if (triple.Contains("msvc", StringComparison.OrdinalIgnoreCase))
        {
            return "msvc";
        }

        return triple.Contains("musl", StringComparison.OrdinalIgnoreCase) ? "musl" : "gnu";
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()));

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

        throw new DirectoryNotFoundException("Could not find the Stark repository root.");
    }

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
