using System.Security.Cryptography;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class CompilerCliSdkTests
{
    [Fact]
    public async Task CompilerReportsStableSdkCompatibilityLineWithoutSdkDiscovery()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [SdkCompilerCompatibility.PrintOption],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(SdkCompilerCompatibility.SupportedLine + Environment.NewLine, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task MissingOfficialImportWithoutAnActiveSdkReportsInstallationDiagnostic()
    {
        var originalSdkRoot = Environment.GetEnvironmentVariable(SdkRootResolver.EnvironmentVariableName);
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-missing-sdk-import-");
        try
        {
            Environment.SetEnvironmentVariable(SdkRootResolver.EnvironmentVariableName, null);
            var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
            await File.WriteAllTextAsync(
                sourcePath,
                """
                import Vendor.NotInstalled
                module MissingSdkApp
                """);
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--check", "--no-stark-path"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Contains("STK7496", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("no active Stark SDK manifest is available", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("Run 'stark doctor'", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("STARK_PATH to locate official modules", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SdkRootResolver.EnvironmentVariableName, originalSdkRoot);
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task DevelopmentSdkOfficialRootIsAllowedOnlyInsideDeclaredSourceRootAndDoesNotSelectSdkArchive()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-root-namespace-");
        var sdkRoot = Path.Combine(tempDirectory.FullName, "sdk");
        var developmentSourceRoot = Path.Combine(sdkRoot, "development-src");
        var packageDirectory = Path.Combine(sdkRoot, "packages");
        var outsideDirectory = Path.Combine(tempDirectory.FullName, "outside");
        Directory.CreateDirectory(developmentSourceRoot);
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(outsideDirectory);

        var sourcePath = Path.Combine(developmentSourceRoot, "Raylib.stark");
        var outsideSourcePath = Path.Combine(outsideDirectory, "Raylib.stark");
        var packageImagePath = Path.Combine(packageDirectory, "Raylib.starkpkg");
        var missingPackageLibraryPath = Path.Combine(packageDirectory, NativeLibraryFileName("Raylib"));
        var executablePath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "raylib-root.exe" : "raylib-root");

        try
        {
            const string source = """
                module Vendor.Raylib

                export fn i32[min max] main()
                {
                    return 0;
                }
                """;
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(outsideSourcePath, source);

            var packageStdout = new StringWriter();
            var packageStderr = new StringWriter();
            var packageExitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-pkg",
                    "-o", packageImagePath,
                    "--package-profile", "release",
                    "--package-library-file", Path.GetFileName(missingPackageLibraryPath),
                    "--no-stark-path"
                ],
                new StringReader(string.Empty),
                packageStdout,
                packageStderr);
            Assert.Equal(0, packageExitCode);
            Assert.Equal(string.Empty, packageStderr.ToString());

            await File.WriteAllTextAsync(
                Path.Combine(sdkRoot, SdkRootResolver.ManifestFileName),
                CreateSdkManifestJson(
                    sdkRoot,
                    "Vendor.Raylib",
                    Path.GetRelativePath(sdkRoot, packageImagePath).Replace(Path.DirectorySeparatorChar, '/'),
                    Path.GetRelativePath(sdkRoot, missingPackageLibraryPath).Replace(Path.DirectorySeparatorChar, '/'),
                    targetInfo,
                    kind: "development",
                    developmentSourceRoots: ["development-src"]));

            var allowedStdout = new StringWriter();
            var allowedStderr = new StringWriter();
            var allowedExitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-exe",
                    "-o", executablePath,
                    "--sdk-root", sdkRoot,
                    "--no-stark-path"
                ],
                new StringReader(string.Empty),
                allowedStdout,
                allowedStderr);

            Assert.True(allowedExitCode == 0, allowedStderr.ToString());
            Assert.True(File.Exists(executablePath));
            Assert.DoesNotContain("STK7466", allowedStderr.ToString(), StringComparison.Ordinal);

            var rejectedStdout = new StringWriter();
            var rejectedStderr = new StringWriter();
            var rejectedExitCode = await CompilerCli.RunAsync(
                [outsideSourcePath, "--check", "--sdk-root", sdkRoot, "--no-stark-path"],
                new StringReader(string.Empty),
                rejectedStdout,
                rejectedStderr);

            Assert.Equal(1, rejectedExitCode);
            Assert.Contains("STK7494", rejectedStderr.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                "Source root module 'Vendor.Raylib' uses the official namespace reserved by the active Stark SDK",
                rejectedStderr.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain("Check succeeded.", rejectedStdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task DirectCheckResolvesIndexedSdkPackageAfterSdkIsRelocated()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-");
        var sdkRoot = Path.Combine(tempDirectory.FullName, "sdk-a");
        var relocatedSdkRoot = Path.Combine(tempDirectory.FullName, "sdk-b");
        var packageDirectory = Path.Combine(sdkRoot, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var packageSourcePath = Path.Combine(tempDirectory.FullName, "SdkMath.stark");
        var packageImagePath = Path.Combine(packageDirectory, "SdkMath.starkpkg");
        var packageLibraryPath = Path.Combine(packageDirectory, NativeLibraryFileName("SdkMath"));
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                packageSourcePath,
                """
                module Vendor.SdkMath

                public finite law i32[min max] FortyTwo()
                {
                    return 42;
                }
                """);

            var packageStdout = new StringWriter();
            var packageStderr = new StringWriter();
            var packageExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-pkg",
                    "-o", packageImagePath,
                    "--package-profile", "release",
                    "--package-library-file", Path.GetFileName(packageLibraryPath),
                    "--no-stark-path"
                ],
                new StringReader(string.Empty),
                packageStdout,
                packageStderr);

            Assert.Equal(0, packageExitCode);
            Assert.Equal(string.Empty, packageStderr.ToString());
            Assert.True(File.Exists(packageImagePath));
            // The SDK resolver preserves this archive path for downstream native
            // linking. Check mode does not consume it, so a zero-byte fixture is
            // sufficient here and keeps the test independent of host archivers.
            await File.WriteAllBytesAsync(packageLibraryPath, []);

            await File.WriteAllTextAsync(
                Path.Combine(sdkRoot, SdkRootResolver.ManifestFileName),
                CreateSdkManifestJson(
                    sdkRoot,
                    "Vendor.SdkMath",
                    Path.GetRelativePath(sdkRoot, packageImagePath).Replace(Path.DirectorySeparatorChar, '/'),
                    Path.GetRelativePath(sdkRoot, packageLibraryPath).Replace(Path.DirectorySeparatorChar, '/'),
                    targetInfo));

            await File.WriteAllTextAsync(
                appPath,
                """
                import Vendor.SdkMath
                module App

                fn i32[min max] Answer()
                {
                    return FortyTwo();
                }
                """);

            // Exact SDK-owned modules are reserved: an incidental source file
            // under the application root must not shadow the package ABI.
            var shadowDirectory = Path.Combine(appDirectory, "Vendor");
            Directory.CreateDirectory(shadowDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(shadowDirectory, "SdkMath.stark"),
                "this is intentionally not valid Stark source");

            await AssertSdkCheckSucceedsAsync(appPath, sdkRoot);
            await AssertDoctorReportsSdkAsync(sdkRoot);

            var unadvertisedModulePath = Path.Combine(shadowDirectory, "NotBundled.stark");
            await File.WriteAllTextAsync(
                unadvertisedModulePath,
                """
                module Vendor.NotBundled

                public fn i32[min max] HiddenValue()
                {
                    return 7;
                }
                """);
            var unadvertisedAppPath = Path.Combine(appDirectory, "Unadvertised.stark");
            await File.WriteAllTextAsync(
                unadvertisedAppPath,
                """
                import Vendor.NotBundled
                module Unadvertised

                fn i32[min max] Run()
                {
                    return HiddenValue();
                }
                """);
            await AssertReservedSdkImportFailsAsync(unadvertisedAppPath, sdkRoot, "Vendor.NotBundled");

            Directory.Move(sdkRoot, relocatedSdkRoot);
            await AssertSdkCheckSucceedsAsync(appPath, relocatedSdkRoot);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task RelocatedSdkPackagePreservesNativeSourceAndLinkFacts()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-native-");
        var initialSdkRoot = Path.Combine(tempDirectory.FullName, "sdk-a");
        var relocatedSdkRoot = Path.Combine(tempDirectory.FullName, "sdk-b");
        var packageDirectory = Path.Combine(initialSdkRoot, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var packageSourcePath = Path.Combine(packageDirectory, "NativeSdk.stark");
        var nativeSourcePath = Path.Combine(packageDirectory, "NativeSdk.c");
        var runtimeFilePath = Path.Combine(packageDirectory, "NativeSdk.runtime");
        var packageLibraryPath = Path.Combine(packageDirectory, NativeLibraryFileName("NativeSdk"));
        var packageImagePath = Path.Combine(packageDirectory, "libNativeSdk.starkpkg");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var executablePath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                packageSourcePath,
                """
                module Vendor.NativeSdk

                unsafe ffi fn i32[min max] stark_sdk_native_value();

                public fn i32[min max] NativeValue()
                {
                    unsafe
                    {
                        return stark_sdk_native_value();
                    }
                }
                """);
            await File.WriteAllTextAsync(
                nativeSourcePath,
                """
                int stark_sdk_native_value(void) {
                    return 42;
                }
                """);
            await File.WriteAllBytesAsync(runtimeFilePath, [0x53, 0x44, 0x4b]);

            var packageStdout = new StringWriter();
            var packageStderr = new StringWriter();
            var packageExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-lib",
                    "-o", packageLibraryPath,
                    "--package-profile", "release",
                    "--native-source", nativeSourcePath,
                    "--no-stark-path"
                ],
                new StringReader(string.Empty),
                packageStdout,
                packageStderr);

            Assert.Equal(0, packageExitCode);
            Assert.Equal(string.Empty, packageStderr.ToString());
            Assert.True(File.Exists(packageImagePath));
            await File.WriteAllTextAsync(
                Path.Combine(initialSdkRoot, SdkRootResolver.ManifestFileName),
                CreateSdkManifestJson(
                    initialSdkRoot,
                    "Vendor.NativeSdk",
                    Path.GetRelativePath(initialSdkRoot, packageImagePath).Replace(Path.DirectorySeparatorChar, '/'),
                    Path.GetRelativePath(initialSdkRoot, packageLibraryPath).Replace(Path.DirectorySeparatorChar, '/'),
                    targetInfo,
                    runtimeFiles:
                    [
                        Path.GetRelativePath(initialSdkRoot, runtimeFilePath).Replace(Path.DirectorySeparatorChar, '/')
                    ]));

            await File.WriteAllTextAsync(
                appPath,
                """
                import Vendor.NativeSdk
                module App

                export fn i32[min max] main()
                {
                    return NativeValue();
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "Stark.toml"),
                """
                [project]
                name = "sdk-native-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "sdk-native-app"
                """);

            File.Delete(packageSourcePath);
            Directory.Move(initialSdkRoot, relocatedSdkRoot);

            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = appDirectory;
                var projectStdout = new StringWriter();
                var projectStderr = new StringWriter();
                var projectExitCode = await CompilerCli.RunAsync(
                    ["build", "--target", targetInfo.Triple, "--sdk-root", relocatedSdkRoot],
                    new StringReader(string.Empty),
                    projectStdout,
                    projectStderr);

                Assert.True(projectExitCode == 0, projectStderr.ToString());
                Assert.Contains("Emitted executable:", projectStdout.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
            }

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    appPath,
                    "--emit-exe",
                    "-o", executablePath,
                    "--sdk-root", relocatedSdkRoot,
                    "--no-stark-path"
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(executablePath));
            var stagedRuntimePath = Path.Combine(appDirectory, Path.GetFileName(runtimeFilePath));
            Assert.True(File.Exists(stagedRuntimePath));
            Assert.Equal(new byte[] { 0x53, 0x44, 0x4b }, await File.ReadAllBytesAsync(stagedRuntimePath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(process);
            await process!.WaitForExitAsync();
            Assert.Equal(42, process.ExitCode);

            var relocatedRuntimePath = Path.Combine(
                relocatedSdkRoot,
                Path.GetRelativePath(initialSdkRoot, runtimeFilePath));
            await File.AppendAllTextAsync(relocatedRuntimePath, "tampered");

            var tamperedCompileStderr = new StringWriter();
            var tamperedCompileExitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "--sdk-root", relocatedSdkRoot, "--no-stark-path"],
                new StringReader(string.Empty),
                new StringWriter(),
                tamperedCompileStderr);
            Assert.Equal(1, tamperedCompileExitCode);
            Assert.Contains("STK7475", tamperedCompileStderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("checksum mismatch", tamperedCompileStderr.ToString(), StringComparison.Ordinal);

            var doctorStdout = new StringWriter();
            var doctorExitCode = await CompilerCli.RunAsync(
                ["doctor", "--sdk-root", relocatedSdkRoot],
                new StringReader(string.Empty),
                doctorStdout,
                new StringWriter());
            Assert.Equal(0, doctorExitCode);
            Assert.Contains("package integrity: invalid", doctorStdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("STK7475", doctorStdout.ToString(), StringComparison.Ordinal);

            var firstStrictJson = await RunStrictJsonDoctorAsync(relocatedSdkRoot);
            var secondStrictJson = await RunStrictJsonDoctorAsync(relocatedSdkRoot);
            Assert.Equal(1, firstStrictJson.ExitCode);
            Assert.Equal(string.Empty, firstStrictJson.Stderr);
            Assert.Equal(firstStrictJson.Stdout, secondStrictJson.Stdout);

            using (var document = JsonDocument.Parse(firstStrictJson.Stdout))
            {
                var root = document.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("warnings", root.GetProperty("status").GetString());
                Assert.True(root.GetProperty("strict").GetBoolean());
                var sdk = root.GetProperty("sdk");
                Assert.Equal("invalid", sdk.GetProperty("status").GetString());
                Assert.Equal("invalid", sdk.GetProperty("packageIntegrityStatus").GetString());
                Assert.Equal("ok", sdk.GetProperty("targetCompatibilityStatus").GetString());
                var package = Assert.Single(sdk.GetProperty("packages").EnumerateArray());
                Assert.Equal("Vendor.NativeSdk", package.GetProperty("id").GetString());
                Assert.Equal("invalid", package.GetProperty("runtimeFiles").GetProperty("status").GetString());
                Assert.Equal("invalid", package.GetProperty("checksums").GetProperty("status").GetString());
                var diagnostic = Assert.Single(
                    package.GetProperty("diagnostics").EnumerateArray(),
                    static diagnostic => diagnostic.GetProperty("code").GetString() == "STK7475");
                Assert.Equal("native-file-checksum", diagnostic.GetProperty("category").GetString());
                Assert.Equal(
                    CanonicalizeFilePath(relocatedRuntimePath),
                    diagnostic.GetProperty("path").GetString());
            }

            await File.WriteAllBytesAsync(relocatedRuntimePath, [0x53, 0x44, 0x4b]);
            File.Delete(relocatedRuntimePath);

            var missingCompileStderr = new StringWriter();
            var missingCompileExitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "--sdk-root", relocatedSdkRoot, "--no-stark-path"],
                new StringReader(string.Empty),
                new StringWriter(),
                missingCompileStderr);
            Assert.Equal(1, missingCompileExitCode);
            Assert.Contains("STK7473", missingCompileStderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("native runtime file is missing", missingCompileStderr.ToString(), StringComparison.Ordinal);

            var missingStrictJson = await RunStrictJsonDoctorAsync(relocatedSdkRoot);
            Assert.Equal(1, missingStrictJson.ExitCode);
            Assert.Equal(string.Empty, missingStrictJson.Stderr);
            using var missingDocument = JsonDocument.Parse(missingStrictJson.Stdout);
            var missingPackage = Assert.Single(
                missingDocument.RootElement.GetProperty("sdk").GetProperty("packages").EnumerateArray());
            Assert.Equal("invalid", missingPackage.GetProperty("runtimeFiles").GetProperty("status").GetString());
            Assert.Equal("not-checked", missingPackage.GetProperty("checksums").GetProperty("status").GetString());
            var missingDiagnostic = Assert.Single(
                missingPackage.GetProperty("diagnostics").EnumerateArray(),
                static diagnostic => diagnostic.GetProperty("code").GetString() == "STK7473");
            Assert.Equal("native-runtime-file", missingDiagnostic.GetProperty("category").GetString());
            Assert.Equal(
                CanonicalizeFilePath(relocatedRuntimePath),
                missingDiagnostic.GetProperty("path").GetString());
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task ActiveSdkPlatformModuleCannotBeShadowedByOrdinaryStdlibTemplate()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-platform-shadow-");
        var sdkRoot = Path.Combine(tempDirectory.FullName, "sdk");
        var packageDirectory = Path.Combine(sdkRoot, "packages");
        var fakeStdlibRoot = Path.Combine(tempDirectory.FullName, "ordinary", "stdlib");
        var appDirectory = Path.Combine(fakeStdlibRoot, "src");
        var templateDirectory = Path.Combine(fakeStdlibRoot, "templates");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(templateDirectory);

        var packageSourcePath = Path.Combine(tempDirectory.FullName, "Platform.stark");
        var packageImagePath = Path.Combine(packageDirectory, "Platform.starkpkg");
        var packageLibraryPath = Path.Combine(packageDirectory, NativeLibraryFileName("Platform"));
        var appPath = Path.Combine(appDirectory, "App.stark");
        var templateFileName = OperatingSystem.IsMacOS()
            ? "System.Runtime.Platform.MacOSDispatch.stark"
            : OperatingSystem.IsWindows()
                ? "System.Runtime.Platform.WindowsDispatch.stark"
                : "System.Runtime.Platform.LinuxDispatch.stark";

        try
        {
            await File.WriteAllTextAsync(
                packageSourcePath,
                """
                module System.Runtime.Platform

                public finite law i32[min max] ProcessId()
                {
                    return 42;
                }
                """);

            var packageStdout = new StringWriter();
            var packageStderr = new StringWriter();
            var packageExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-pkg",
                    "-o", packageImagePath,
                    "--package-profile", "release",
                    "--package-library-file", Path.GetFileName(packageLibraryPath),
                    "--no-stark-path"
                ],
                new StringReader(string.Empty),
                packageStdout,
                packageStderr);

            Assert.Equal(0, packageExitCode);
            Assert.Equal(string.Empty, packageStderr.ToString());
            await File.WriteAllBytesAsync(packageLibraryPath, []);
            await File.WriteAllTextAsync(
                Path.Combine(sdkRoot, SdkRootResolver.ManifestFileName),
                CreateSdkManifestJson(
                    sdkRoot,
                    "System.Runtime.Platform",
                    Path.GetRelativePath(sdkRoot, packageImagePath).Replace(Path.DirectorySeparatorChar, '/'),
                    Path.GetRelativePath(sdkRoot, packageLibraryPath).Replace(Path.DirectorySeparatorChar, '/'),
                    targetInfo));

            await File.WriteAllTextAsync(
                Path.Combine(templateDirectory, templateFileName),
                "this ordinary platform template must never be parsed");
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Platform
                module App

                fn i32[min max] ReadProcessId()
                {
                    return ProcessId();
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-llvm", "--sdk-root", sdkRoot, "--no-stark-path"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.DoesNotContain("ordinary platform template", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task CompileLoadsOnlySelectedSdkPackageWhileExactBrokenImportReportsIntegrityDiagnostic()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-sdk-lazy-");
        var sdkRoot = Path.Combine(tempDirectory.FullName, "sdk");
        var packageDirectory = Path.Combine(sdkRoot, "packages");
        Directory.CreateDirectory(packageDirectory);
        var packageSourcePath = Path.Combine(tempDirectory.FullName, "Healthy.stark");
        var packageImagePath = Path.Combine(packageDirectory, "Healthy.starkpkg");
        var packageLibraryPath = Path.Combine(packageDirectory, NativeLibraryFileName("Healthy"));
        var healthyAppPath = Path.Combine(tempDirectory.FullName, "HealthyApp.stark");
        var brokenAppPath = Path.Combine(tempDirectory.FullName, "BrokenApp.stark");

        try
        {
            await File.WriteAllTextAsync(
                packageSourcePath,
                """
                module System.Healthy

                public fn i32[min max] Value()
                {
                    return 42;
                }
                """);
            var packageStderr = new StringWriter();
            var packageExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-pkg",
                    "-o", packageImagePath,
                    "--package-profile", "release",
                    "--package-library-file", Path.GetFileName(packageLibraryPath),
                    "--no-stark-path"
                ],
                new StringReader(string.Empty),
                new StringWriter(),
                packageStderr);
            Assert.True(packageExitCode == 0, packageStderr.ToString());
            await File.WriteAllBytesAsync(packageLibraryPath, []);
            await File.WriteAllTextAsync(
                Path.Combine(sdkRoot, SdkRootResolver.ManifestFileName),
                CreateSdkManifestJson(
                    sdkRoot,
                    "System.Healthy",
                    "packages/Healthy.starkpkg",
                    $"packages/{Path.GetFileName(packageLibraryPath)}",
                    targetInfo,
                    missingVendorModule: "Vendor.Broken"));
            await File.WriteAllTextAsync(
                healthyAppPath,
                """
                import System.Healthy
                module HealthyApp

                fn i32[min max] Run()
                {
                    return Value();
                }
                """);
            await File.WriteAllTextAsync(
                brokenAppPath,
                """
                import Vendor.Broken
                module BrokenApp
                """);

            await AssertSdkCheckSucceedsAsync(healthyAppPath, sdkRoot);

            var brokenStderr = new StringWriter();
            var brokenExitCode = await CompilerCli.RunAsync(
                [brokenAppPath, "--check", "--sdk-root", sdkRoot, "--no-stark-path"],
                new StringReader(string.Empty),
                new StringWriter(),
                brokenStderr);
            Assert.Equal(1, brokenExitCode);
            Assert.Contains("STK7460", brokenStderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("STK7495", brokenStderr.ToString(), StringComparison.Ordinal);

            var doctorStdout = new StringWriter();
            await CompilerCli.RunAsync(
                ["doctor", "--sdk-root", sdkRoot],
                new StringReader(string.Empty),
                doctorStdout,
                new StringWriter());
            Assert.Contains("package integrity: invalid", doctorStdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("STK7460", doctorStdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static async Task AssertSdkCheckSucceedsAsync(string appPath, string sdkRoot)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            [appPath, "--check", "--sdk-root", sdkRoot, "--no-stark-path"],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunStrictJsonDoctorAsync(
        string sdkRoot)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            ["doctor", "--strict", "--format", "json", "--sdk-root", sdkRoot],
            new StringReader(string.Empty),
            stdout,
            stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task AssertDoctorReportsSdkAsync(string sdkRoot)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            ["doctor", "--sdk-root", sdkRoot],
            new StringReader(string.Empty),
            stdout,
            stderr);

        var text = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains($"root: {SdkRootResolver.CanonicalizeRootPath(sdkRoot)}", text, StringComparison.Ordinal);
        Assert.Contains("origin: explicit", text, StringComparison.Ordinal);
        Assert.Contains("kind: release", text, StringComparison.Ordinal);
        Assert.Contains($"compiler compatibility: {SdkCompilerCompatibility.SupportedLine}", text, StringComparison.Ordinal);
        Assert.Contains("packages: Vendor.SdkMath", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    private static async Task AssertReservedSdkImportFailsAsync(
        string appPath,
        string sdkRoot,
        string moduleName)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            [appPath, "--check", "--sdk-root", sdkRoot, "--no-stark-path"],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("STK7495", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Official module '{moduleName}' is not included", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
    }

    private static string CanonicalizeFilePath(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(parent)
            ? Path.GetFullPath(path)
            : Path.Combine(SdkRootResolver.CanonicalizeRootPath(parent), Path.GetFileName(path));
    }

    private static string CreateSdkManifestJson(
        string sdkRoot,
        string moduleName,
        string packageImagePath,
        string packageLibraryPath,
        LlvmTargetInfo targetInfo,
        string? missingVendorModule = null,
        IReadOnlyList<string>? runtimeFiles = null,
        string kind = "release",
        IReadOnlyList<string>? developmentSourceRoots = null)
    {
        var modules = new List<object>
        {
            new { name = moduleName, package = moduleName }
        };
        var packages = new List<object>
        {
            new
            {
                id = moduleName,
                version = "test",
                profile = "release",
                image = packageImagePath,
                library = packageLibraryPath,
                imageSha256 = CalculateSdkFileSha256(sdkRoot, packageImagePath),
                librarySha256 = File.Exists(Path.Combine(
                        sdkRoot,
                        packageLibraryPath.Replace('/', Path.DirectorySeparatorChar)))
                    ? CalculateSdkFileSha256(sdkRoot, packageLibraryPath)
                    : null,
                dependencies = Array.Empty<object>(),
                native = NativeDescriptor(sdkRoot, runtimeFiles)
            }
        };
        if (!string.IsNullOrWhiteSpace(missingVendorModule))
        {
            modules.Add(new { name = missingVendorModule, package = missingVendorModule });
            packages.Add(new
            {
                id = missingVendorModule,
                version = "test",
                profile = "release",
                image = $"packages/{missingVendorModule}.missing.starkpkg",
                library = $"packages/{NativeLibraryFileName(missingVendorModule)}",
                imageSha256 = new string('0', 64),
                librarySha256 = new string('0', 64),
                dependencies = Array.Empty<object>(),
                native = NativeDescriptor(sdkRoot)
            });
        }

        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                kind,
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
                    pointerBitWidth = IntPtr.Size * 8,
                    endianness = BitConverter.IsLittleEndian ? "little" : "big",
                    dataLayout = targetInfo.DataLayout,
                    baselineCpu = targetInfo.Cpu,
                    baselineFeatures = targetInfo.Features ?? Array.Empty<string>(),
                    relocationModel = targetInfo.RelocationModel.ToString().ToLowerInvariant(),
                    codeModel = targetInfo.CodeModel?.ToString().ToLowerInvariant()
                },
                modules,
                packages,
                developmentSourceRoots = developmentSourceRoots ?? Array.Empty<string>()
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static object NativeDescriptor(
        string sdkRoot,
        IReadOnlyList<string>? runtimeFiles = null) => new
    {
        artifacts = Array.Empty<string>(),
        includeDirectories = Array.Empty<string>(),
        libraryDirectories = Array.Empty<string>(),
        runtimeFiles = runtimeFiles ?? Array.Empty<string>(),
        licenseFiles = Array.Empty<string>(),
        fileChecksums = (runtimeFiles ?? Array.Empty<string>())
            .Order(StringComparer.Ordinal)
            .Select(path => new
            {
                path,
                sha256 = CalculateSdkFileSha256(sdkRoot, path)
            }),
        libraries = Array.Empty<string>(),
        linkArguments = Array.Empty<string>()
    };

    private static string CalculateSdkFileSha256(string sdkRoot, string relativePath)
    {
        var path = Path.Combine(sdkRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static string NativeLibraryFileName(string packageName) =>
        OperatingSystem.IsWindows() ? $"{packageName}.lib" : $"lib{packageName}.a";

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
}
