using System.Security.Cryptography;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class CompilerCliEmitPackageSdkTargetTests
{
    [Fact]
    public async Task ActiveSdkGenericPackageAcceptsTunedApplicationCpuAndFeatureSuperset()
    {
        if (!TryResolveHostSdkTarget(out var sdkTarget))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-tuned-target-");
        try
        {
            var sdkRoot = await CreateSdkAsync(tempDirectory.FullName, sdkTarget);
            var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
            await File.WriteAllTextAsync(
                sourcePath,
                """
                import Vendor.SdkTarget
                module App

                public finite law i32[min max] ReadSdkValue()
                {
                    return SdkValue();
                }
                """);

            var arguments = new List<string>
            {
                sourcePath,
                "--check",
                "--sdk-root", sdkRoot,
                "--no-stark-path",
                "--target", sdkTarget.Triple,
                "--target-data-layout", sdkTarget.DataLayout!,
                "--target-cpu", "stark-test-tuned-cpu"
            };
            foreach (var feature in sdkTarget.Features ?? [])
            {
                arguments.Add("--target-feature");
                arguments.Add(feature);
            }

            arguments.Add("--target-feature");
            arguments.Add("+stark-test-extra-feature");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                arguments.ToArray(),
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Contains("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ActiveSdkPackageAcceptsSafeArchitectureAliasEndToEnd()
    {
        if (!TryResolveHostSdkTarget(out var sdkTarget)
            || !TryCreateSafeArchitectureAlias(sdkTarget.Triple, out var activeTriple))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-target-alias-");
        try
        {
            var sdkRoot = await CreateSdkAsync(tempDirectory.FullName, sdkTarget);
            var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
            await File.WriteAllTextAsync(
                sourcePath,
                """
                import Vendor.SdkTarget
                module App

                public finite law i32[min max] ReadSdkValue()
                {
                    return SdkValue();
                }
                """);

            var arguments = new List<string>
            {
                sourcePath,
                "--check",
                "--sdk-root", sdkRoot,
                "--no-stark-path",
                "--target", activeTriple,
                "--target-data-layout", sdkTarget.DataLayout!,
                "--target-cpu", sdkTarget.Cpu!
            };
            foreach (var feature in sdkTarget.Features ?? [])
            {
                arguments.Add("--target-feature");
                arguments.Add(feature);
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                arguments.ToArray(),
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Contains("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("STK7311", stderr.ToString(), StringComparison.Ordinal);
            Assert.NotEqual(sdkTarget.Triple, activeTriple);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ActiveSdkPackageRejectsIncompatibleAbiEndToEnd()
    {
        if (!TryResolveAbiMismatchTargets(out var sdkTarget, out var packageTarget))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-target-abi-mismatch-");
        try
        {
            var sdkRoot = await CreateSdkAsync(tempDirectory.FullName, sdkTarget, packageTarget);
            var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
            await File.WriteAllTextAsync(
                sourcePath,
                """
                import Vendor.SdkTarget
                module App

                public finite law i32[min max] ReadSdkValue()
                {
                    return SdkValue();
                }
                """);

            var arguments = new List<string>
            {
                sourcePath,
                "--check",
                "--sdk-root", sdkRoot,
                "--no-stark-path",
                "--target", sdkTarget.Triple,
                "--target-data-layout", sdkTarget.DataLayout!,
                "--target-cpu", sdkTarget.Cpu!
            };
            foreach (var feature in sdkTarget.Features ?? [])
            {
                arguments.Add("--target-feature");
                arguments.Add(feature);
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                arguments.ToArray(),
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("STK7484", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("ABI", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(packageTarget.Triple, stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("STK7311", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task EmitPackageWithoutExplicitTargetInheritsCompleteActiveSdkTargetFacts()
    {
        if (!TryResolveHostSdkTarget(out var sdkTarget))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-sdk-target-");
        try
        {
            var sdkRoot = await CreateSdkAsync(tempDirectory.FullName, sdkTarget);
            var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
            var packagePath = Path.Combine(tempDirectory.FullName, "App.starkpkg");
            await File.WriteAllTextAsync(
                sourcePath,
                """
                import Vendor.SdkTarget
                module App

                public finite law i32[min max] ReadSdkValue()
                {
                    return SdkValue();
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-pkg",
                    "--sdk-root", sdkRoot,
                    "--no-stark-path",
                    "-o", packagePath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            var target = Assert.IsType<StarkPackageTargetManifest>(manifest.Target);
            Assert.Equal(sdkTarget.Triple, target.Triple);
            Assert.Equal(sdkTarget.DataLayout, target.DataLayout);
            Assert.Equal(sdkTarget.Cpu, target.Cpu);
            Assert.Equal(sdkTarget.Features, target.Features);
            Assert.Equal("pic", target.RelocationModel);
            Assert.Equal("small", target.CodeModel);

            var cDataModel = Assert.IsType<StarkPackageCDataModelManifest>(target.CDataModel);
            Assert.False(string.IsNullOrWhiteSpace(cDataModel.Kind));
            Assert.True(cDataModel.PointerBitWidth > 0);
            Assert.True(cDataModel.LongBitWidth > 0);
            Assert.True(cDataModel.SizeTBitWidth > 0);
            Assert.True(cDataModel.PtrDiffTBitWidth > 0);

            var aggregateLayout = Assert.IsType<StarkPackageAggregateLayoutManifest>(target.AggregateLayout);
            Assert.Equal(cDataModel.PointerBitWidth / 8, aggregateLayout.PointerSizeBytes);
            Assert.True(aggregateLayout.PointerAlignmentBytes > 0);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task EmitPackageWithActiveSdkRejectsImportedPackageForIncompatibleTarget()
    {
        if (!TryResolveHostSdkTarget(out var sdkTarget)
            || !TryResolveIncompatibleTarget(sdkTarget.Triple, out var incompatibleTarget))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-sdk-mismatch-");
        try
        {
            var sdkRoot = await CreateSdkAsync(tempDirectory.FullName, sdkTarget);
            var dependencyDirectory = Path.Combine(tempDirectory.FullName, "ordinary-packages");
            var dependencySourceDirectory = Path.Combine(tempDirectory.FullName, "ordinary-source");
            Directory.CreateDirectory(dependencyDirectory);
            Directory.CreateDirectory(dependencySourceDirectory);
            var dependencySourcePath = Path.Combine(dependencySourceDirectory, "Other.stark");
            var dependencyPackagePath = Path.Combine(dependencyDirectory, "Other.starkpkg");
            await File.WriteAllTextAsync(
                dependencySourcePath,
                """
                module Other

                public finite law i32[min max] OtherValue()
                {
                    return 7;
                }
                """);

            var dependencyArguments = new List<string>
            {
                dependencySourcePath,
                "--emit-pkg",
                "--target", incompatibleTarget.Triple,
                "--no-stark-path",
                "-o", dependencyPackagePath
            };
            if (!string.IsNullOrWhiteSpace(incompatibleTarget.DataLayout))
            {
                dependencyArguments.Add("--target-data-layout");
                dependencyArguments.Add(incompatibleTarget.DataLayout);
            }

            var dependencyStderr = new StringWriter();
            var dependencyExitCode = await CompilerCli.RunAsync(
                dependencyArguments.ToArray(),
                new StringReader(string.Empty),
                new StringWriter(),
                dependencyStderr);
            Assert.True(dependencyExitCode == 0, dependencyStderr.ToString());

            var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
            var packagePath = Path.Combine(tempDirectory.FullName, "App.starkpkg");
            await File.WriteAllTextAsync(
                sourcePath,
                """
                import Vendor.SdkTarget
                import Other
                module App

                public finite law i32[min max] ReadValues()
                {
                    return SdkValue() + OtherValue();
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-package",
                    "--sdk-root", sdkRoot,
                    "--no-stark-path",
                    "-I", dependencyDirectory,
                    "-o", packagePath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("STK7311", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("Package image module 'Other' was built for target triple", stderr.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(packagePath));
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static bool TryResolveHostSdkTarget(out LlvmTargetInfo targetInfo)
    {
        targetInfo = default!;
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTarget)
            || string.IsNullOrWhiteSpace(detectedTarget.DataLayout))
        {
            return false;
        }

        targetInfo = detectedTarget with
        {
            Cpu = "generic",
            Features = [TargetFeature(detectedTarget.Triple)],
            RelocationModel = LlvmRelocationModel.Pic,
            CodeModel = LlvmCodeModel.Small
        };
        return true;
    }

    private static bool TryResolveIncompatibleTarget(string activeTriple, out LlvmTargetInfo targetInfo)
    {
        var activeArchitecture = activeTriple.Split('-', 2)[0];
        var candidates = activeArchitecture.Equals("x86_64", StringComparison.OrdinalIgnoreCase)
            || activeArchitecture.Equals("amd64", StringComparison.OrdinalIgnoreCase)
                ? new[] { "aarch64-unknown-linux-gnu", "arm64-apple-macosx11.0.0" }
                : new[] { "x86_64-unknown-linux-gnu", "x86_64-apple-macosx11.0.0" };

        foreach (var candidate in candidates)
        {
            if (NativeToolchain.TryDetectTargetInfo(candidate, out targetInfo)
                && !string.IsNullOrWhiteSpace(targetInfo.DataLayout))
            {
                return true;
            }
        }

        targetInfo = default!;
        return false;
    }

    private static bool TryCreateSafeArchitectureAlias(string triple, out string alias)
    {
        var separator = triple.IndexOf('-');
        if (separator <= 0)
        {
            alias = string.Empty;
            return false;
        }

        var architecture = triple[..separator].ToLowerInvariant();
        var aliasArchitecture = architecture switch
        {
            "x86_64" => "amd64",
            "amd64" => "x86_64",
            "aarch64" => "arm64",
            "arm64" => "aarch64",
            _ => null
        };
        if (aliasArchitecture is null)
        {
            alias = string.Empty;
            return false;
        }

        alias = aliasArchitecture + triple[separator..];
        return !string.Equals(alias, triple, StringComparison.Ordinal);
    }

    private static bool TryResolveAbiMismatchTargets(
        out LlvmTargetInfo sdkTarget,
        out LlvmTargetInfo packageTarget)
    {
        foreach (var architecture in new[] { "x86_64", "aarch64" })
        {
            var sdkTriple = $"{architecture}-unknown-linux-gnu";
            var packageTriple = $"{architecture}-unknown-linux-musl";
            if (!NativeToolchain.TryDetectTargetInfo(sdkTriple, out var detectedSdkTarget)
                || !NativeToolchain.TryDetectTargetInfo(packageTriple, out var detectedPackageTarget)
                || string.IsNullOrWhiteSpace(detectedSdkTarget.DataLayout)
                || !string.Equals(
                    detectedSdkTarget.DataLayout,
                    detectedPackageTarget.DataLayout,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var features = new[] { TargetFeature(sdkTriple) };
            sdkTarget = detectedSdkTarget with
            {
                Cpu = "generic",
                Features = features,
                RelocationModel = LlvmRelocationModel.Pic,
                CodeModel = LlvmCodeModel.Small
            };
            packageTarget = detectedPackageTarget with
            {
                Cpu = "generic",
                Features = features,
                RelocationModel = LlvmRelocationModel.Pic,
                CodeModel = LlvmCodeModel.Small
            };
            return true;
        }

        sdkTarget = default!;
        packageTarget = default!;
        return false;
    }

    private static async Task<string> CreateSdkAsync(
        string rootDirectory,
        LlvmTargetInfo targetInfo,
        LlvmTargetInfo? packageTargetInfo = null)
    {
        packageTargetInfo ??= targetInfo;
        var sdkRoot = Path.Combine(rootDirectory, "sdk");
        var packageDirectory = Path.Combine(sdkRoot, "packages");
        Directory.CreateDirectory(packageDirectory);
        var sourcePath = Path.Combine(rootDirectory, "SdkTarget.stark");
        var packageImagePath = Path.Combine(packageDirectory, "SdkTarget.starkpkg");
        var packageLibraryPath = Path.Combine(packageDirectory, NativeLibraryFileName("SdkTarget"));
        await File.WriteAllTextAsync(
            sourcePath,
            """
            module Vendor.SdkTarget

            public finite law i32[min max] SdkValue()
            {
                return 35;
            }
            """);

        var arguments = new List<string>
        {
            sourcePath,
            "--emit-pkg",
            "--target", packageTargetInfo.Triple,
            "--target-data-layout", packageTargetInfo.DataLayout!,
            "--target-cpu", packageTargetInfo.Cpu!,
            "--relocation-model", "pic",
            "--code-model", "small",
            "--package-profile", "release",
            "--package-library-file", Path.GetFileName(packageLibraryPath),
            "--no-stark-path",
            "-o", packageImagePath
        };
        foreach (var feature in packageTargetInfo.Features ?? [])
        {
            arguments.Add("--target-feature");
            arguments.Add(feature);
        }

        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            arguments.ToArray(),
            new StringReader(string.Empty),
            new StringWriter(),
            stderr);
        Assert.True(exitCode == 0, stderr.ToString());
        await File.WriteAllBytesAsync(packageLibraryPath, []);

        var packageTarget = Assert.IsType<StarkPackageTargetManifest>(
            AssertPackageTarget(packageImagePath));
        await File.WriteAllTextAsync(
            Path.Combine(sdkRoot, SdkRootResolver.ManifestFileName),
            CreateSdkManifestJson(sdkRoot, packageImagePath, packageLibraryPath, targetInfo, packageTarget));
        return sdkRoot;
    }

    private static StarkPackageTargetManifest? AssertPackageTarget(string packageImagePath)
    {
        Assert.True(PackageImageLoader.TryLoadManifest(packageImagePath, out var manifest));
        return manifest.Target;
    }

    private static string CreateSdkManifestJson(
        string sdkRoot,
        string packageImagePath,
        string packageLibraryPath,
        LlvmTargetInfo targetInfo,
        StarkPackageTargetManifest packageTarget)
    {
        var relativeImagePath = Path.GetRelativePath(sdkRoot, packageImagePath).Replace(Path.DirectorySeparatorChar, '/');
        var relativeLibraryPath = Path.GetRelativePath(sdkRoot, packageLibraryPath).Replace(Path.DirectorySeparatorChar, '/');
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                kind = "release",
                sdkVersion = "test",
                compilerCompatibility = SdkCompilerCompatibility.SupportedLine,
                packageFormatVersion = 2,
                target = new
                {
                    id = "test-host",
                    llvmTriple = targetInfo.Triple,
                    architecture = TargetArchitecture(targetInfo.Triple),
                    operatingSystem = TargetOperatingSystem(targetInfo.Triple),
                    abi = TargetAbi(targetInfo.Triple),
                    pointerBitWidth = packageTarget.CDataModel!.PointerBitWidth,
                    endianness = BitConverter.IsLittleEndian ? "little" : "big",
                    dataLayout = targetInfo.DataLayout,
                    baselineCpu = targetInfo.Cpu,
                    baselineFeatures = targetInfo.Features,
                    relocationModel = "pic",
                    codeModel = "small",
                    cDataModel = packageTarget.CDataModel.Kind
                },
                modules = new[]
                {
                    new { name = "Vendor.SdkTarget", package = "Vendor.SdkTarget" }
                },
                packages = new[]
                {
                    new
                    {
                        id = "Vendor.SdkTarget",
                        version = "test",
                        profile = "release",
                        image = relativeImagePath,
                        library = relativeLibraryPath,
                        imageSha256 = Sha256(packageImagePath),
                        librarySha256 = Sha256(packageLibraryPath),
                        dependencies = Array.Empty<object>(),
                        native = new
                        {
                            artifacts = Array.Empty<string>(),
                            includeDirectories = Array.Empty<string>(),
                            libraryDirectories = Array.Empty<string>(),
                            runtimeFiles = Array.Empty<string>(),
                            licenseFiles = Array.Empty<string>(),
                            fileChecksums = Array.Empty<object>(),
                            libraries = Array.Empty<string>(),
                            linkArguments = Array.Empty<string>()
                        }
                    }
                },
                developmentSourceRoots = Array.Empty<string>()
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static string TargetFeature(string triple)
    {
        var architecture = triple.Split('-', 2)[0].ToLowerInvariant();
        return architecture switch
        {
            "x86" or "i386" or "i686" or "x86_64" or "amd64" => "+sse2",
            "arm" or "armv7" or "arm64" or "aarch64" => "+neon",
            "riscv64" => "+m",
            _ => "+baseline"
        };
    }

    private static string TargetArchitecture(string triple)
    {
        var architecture = triple.Split('-', 2)[0].ToLowerInvariant();
        return architecture is "arm64" or "aarch64"
            ? "arm64"
            : architecture is "x86_64" or "amd64"
                ? "x86_64"
                : architecture;
    }

    private static string TargetOperatingSystem(string triple)
    {
        return triple.Contains("darwin", StringComparison.OrdinalIgnoreCase)
               || triple.Contains("macos", StringComparison.OrdinalIgnoreCase)
            ? "macos"
            : triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
              || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase)
                ? "windows"
                : "linux";
    }

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

    private static string NativeLibraryFileName(string packageName) =>
        OperatingSystem.IsWindows() ? $"{packageName}.lib" : $"lib{packageName}.a";

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void TryDeleteDirectory(DirectoryInfo directory)
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
