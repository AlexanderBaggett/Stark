using System.Buffers.Binary;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class PackageImageCliToolingTests
{
    [Fact]
    public async Task EmitPackageModeWritesPackageImageWithRequestedLibraryFileName()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-pkg", "--package-library-file", "libDemoCustom.a", "-o", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains("Emitted package image:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Package library file: libDemoCustom.a", stdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(packagePath));

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            Assert.Equal(BinaryFormatVersion, BinaryPrimitives.ReadUInt32LittleEndian(packageBytes.AsSpan(8)));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(packageBytes.AsSpan(12)));
            Assert.Equal((ulong)(3 * BinarySectionEntryLength), BinaryPrimitives.ReadUInt64LittleEndian(packageBytes.AsSpan(16)));
            Assert.Equal(
                [BinaryStringTableSectionId, BinaryPackageFactsSectionId, BinaryManifestSectionId],
                ReadBinarySectionIds(packageBytes));

            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            Assert.Equal("Demo", manifest.RootModule);
            Assert.Equal("libDemoCustom.a", manifest.LibraryFileName);
            Assert.Single(manifest.Modules);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitPackageModeCanWriteTypedOnlyPackageImage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-typed-only-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Facade.starkpkg");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            module Facade

            public fn i32[min max] Observe<T>(i32[min max] value, T tag)
            {
                return value;
            }
            """);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-pkg",
                    "--package-library-file",
                    "libFacade.a",
                    "--package-typed-only",
                    "-o",
                    packagePath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(packagePath));
            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));

            var module = Assert.Single(manifest.Modules);
            Assert.Equal("Facade", module.ModuleName);
            Assert.Empty(module.Functions);
            Assert.Empty(module.Types);
            Assert.Empty(module.Globals);
            Assert.NotNull(module.TypeAliases);
            Assert.Empty(module.TypeAliases);
            Assert.NotNull(module.SourceSurface);
            Assert.Empty(module.SourceSurface!.Functions!);
            Assert.Empty(module.SourceSurface.Types!);
            Assert.Empty(module.SourceSurface.Globals!);
            Assert.Empty(module.SourceSurface.TypeAliases!);
            Assert.NotNull(module.TypedInterface);
            Assert.NotNull(module.CompilerFacts);
            Assert.NotNull(module.CompilerSections);
            Assert.NotNull(module.CompilerSections!.TypedInterface);
            Assert.NotNull(module.CompilerSections.CompilerFacts);
            Assert.Equal(module.TypedInterface!.Functions.Count, module.CompilerSections.TypedInterface!.Functions.Count);
            Assert.Equal(module.CompilerFacts!.FunctionEffects.Count, module.CompilerSections.CompilerFacts!.FunctionEffects.Count);

            var typedFunction = Assert.Single(module.TypedInterface!.Functions);
            Assert.Equal("Facade.Observe", typedFunction.QualifiedName);
            Assert.NotNull(module.GenericTemplates);
            var template = Assert.Single(module.GenericTemplates!.Functions);
            Assert.Equal("Facade.Observe", template.QualifiedResolvedName);
            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitPackageModeWritesNativeDependencyMetadata()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-native-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg");
        var nativeSourcePath = Path.Combine(tempDirectory.FullName, "DemoNative.c");
        var includeDirectory = Path.Combine(tempDirectory.FullName, "include");
        var libraryDirectory = Path.Combine(tempDirectory.FullName, "native");
        Directory.CreateDirectory(includeDirectory);
        Directory.CreateDirectory(libraryDirectory);
        await File.WriteAllTextAsync(sourcePath, DemoSource);
        await File.WriteAllTextAsync(nativeSourcePath, "int demo_native(void) { return 0; }\n");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-pkg",
                    "--native-source",
                    nativeSourcePath,
                    "--native-include-dir",
                    includeDirectory,
                    "--native-library-dir",
                    libraryDirectory,
                    "--native-library",
                    "demo",
                    "--native-pkg-config",
                    "demo-pkg",
                    "--native-link-arg",
                    "-pthread",
                    "-o",
                    packagePath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            Assert.NotNull(manifest.NativeDependencies);
            Assert.Equal("DemoNative.c", Assert.Single(manifest.NativeDependencies!.Sources!));
            Assert.Equal("include", Assert.Single(manifest.NativeDependencies.IncludeDirectories!));
            Assert.Equal("native", Assert.Single(manifest.NativeDependencies.LibraryDirectories!));
            Assert.Equal("demo", Assert.Single(manifest.NativeDependencies.Libraries!));
            Assert.Equal("demo-pkg", Assert.Single(manifest.NativeDependencies.PkgConfigPackages!));
            Assert.Equal("-pthread", Assert.Single(manifest.NativeDependencies.LinkArguments!));

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [packagePath, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.Equal(0, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr.ToString());
            Assert.Contains("native dependencies: sources=1, includes=1, library-dirs=1, libraries=1, pkg-config=1, link-args=1", inspectStdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitPackageModeStoresAbsoluteNativeSourcePathRelativeToPackageImage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-native-reloc-");
        var sourceDirectory = Path.Combine(tempDirectory.FullName, "src");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "dist");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(packageDirectory);

        var sourcePath = Path.Combine(sourceDirectory, "Demo.stark");
        var packagePath = Path.Combine(packageDirectory, "Demo.starkpkg");
        var nativeSourcePath = Path.Combine(sourceDirectory, "DemoNative.c");
        await File.WriteAllTextAsync(sourcePath, DemoSource);
        await File.WriteAllTextAsync(nativeSourcePath, "int demo_native(void) { return 0; }\n");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-pkg",
                    "--native-source",
                    nativeSourcePath,
                    "-o",
                    packagePath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            Assert.NotNull(manifest.NativeDependencies);

            var storedSource = Assert.Single(manifest.NativeDependencies!.Sources!);
            Assert.False(Path.IsPathRooted(storedSource));
            Assert.Equal(
                Path.GetRelativePath(packageDirectory, nativeSourcePath),
                storedSource);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitExecutableLinksImportedPackageNativeSources()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-pkg-native-link-");
        var packageSourcePath = Path.Combine(tempDirectory.FullName, "NativeDemo.stark");
        var nativeSourcePath = Path.Combine(tempDirectory.FullName, "NativeDemo.c");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "NativeDemo.lib" : "libNativeDemo.a");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var executablePath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        await File.WriteAllTextAsync(
            packageSourcePath,
            """
            module NativeDemo

            unsafe ffi fn i32[min max] stark_native_value();

            public fn i32[min max] GetValue()
            {
                unsafe
                {
                    return stark_native_value();
                }
            }
            """);
        await File.WriteAllTextAsync(
            nativeSourcePath,
            """
            int stark_native_value(void) {
                return 42;
            }
            """);
        await File.WriteAllTextAsync(
            appPath,
            """
            import NativeDemo
            module App

            export fn i32[min max] main()
            {
                return GetValue();
            }
            """);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [packageSourcePath, "--emit-lib", "-o", libraryPath, "--native-source", nativeSourcePath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());
            File.Delete(packageSourcePath);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", tempDirectory.FullName, "-o", executablePath],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());

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
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitExecutableLinksImportedPackageNativeSourceThroughFfiLinkName()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-pkg-native-link-name-");
        var packageSourcePath = Path.Combine(tempDirectory.FullName, "NativeAlias.stark");
        var nativeSourcePath = Path.Combine(tempDirectory.FullName, "NativeAlias.c");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "NativeAlias.lib" : "libNativeAlias.a");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var executablePath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        await File.WriteAllTextAsync(
            packageSourcePath,
            """
            module NativeAlias

            [LinkName("native_real_value")]
            internal unsafe ffi(c) fn i32[min max] StarkNativeValue();

            public fn i32[min max] GetValue()
            {
                unsafe
                {
                    return StarkNativeValue();
                }
            }
            """);
        await File.WriteAllTextAsync(
            nativeSourcePath,
            """
            int native_real_value(void) {
                return 43;
            }
            """);
        await File.WriteAllTextAsync(
            appPath,
            """
            import NativeAlias
            module App

            export fn i32[min max] main()
            {
                return GetValue();
            }
            """);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [packageSourcePath, "--emit-lib", "-o", libraryPath, "--native-source", nativeSourcePath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.Contains("Emitted static library:", emitStdout.ToString(), StringComparison.Ordinal);
            File.Delete(packageSourcePath);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", tempDirectory.FullName, "-o", executablePath],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);

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

            Assert.Equal(43, process.ExitCode);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitExecutableCopiesWindowsPackageRuntimeDllsBesideOutput()
    {
        if (!OperatingSystem.IsWindows() || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-pkg-native-runtime-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var nativeDirectory = Path.Combine(tempDirectory.FullName, "native");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(nativeDirectory);
        Directory.CreateDirectory(appDirectory);

        var packageSourcePath = Path.Combine(packageDirectory, "NativeRuntimeDemo.stark");
        var nativeSourcePath = Path.Combine(packageDirectory, "NativeRuntimeDemo.c");
        var dllSourcePath = Path.Combine(nativeDirectory, "DemoRuntime.c");
        var runtimeDllPath = Path.Combine(nativeDirectory, "demo.dll");
        var importLibraryPath = Path.Combine(nativeDirectory, "demo.lib");
        var packageLibraryPath = Path.Combine(packageDirectory, "NativeRuntimeDemo.lib");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var executablePath = Path.Combine(appDirectory, "app.exe");

        try
        {
            await File.WriteAllTextAsync(
                dllSourcePath,
                """
                __declspec(dllexport) int demo_value(void) {
                    return 42;
                }
                """);

            var dllBuildResult = await RunProcessAsync(
                "clang",
                [
                    "-target", targetInfo.Triple,
                    "-shared",
                    dllSourcePath,
                    "-o", runtimeDllPath,
                    $"-Wl,/implib:{importLibraryPath}"
                ],
                nativeDirectory);
            Assert.True(
                dllBuildResult.ExitCode == 0,
                dllBuildResult.Stdout + Environment.NewLine + dllBuildResult.Stderr);
            Assert.True(File.Exists(runtimeDllPath));
            Assert.True(File.Exists(importLibraryPath));

            await File.WriteAllTextAsync(
                packageSourcePath,
                """
                module NativeRuntimeDemo

                unsafe ffi fn i32[min max] stark_native_value();

                public fn i32[min max] GetValue()
                {
                    unsafe
                    {
                        return stark_native_value();
                    }
                }
                """);
            await File.WriteAllTextAsync(
                nativeSourcePath,
                """
                __declspec(dllimport) int demo_value(void);

                int stark_native_value(void) {
                    return demo_value();
                }
                """);
            await File.WriteAllTextAsync(
                appPath,
                """
                import NativeRuntimeDemo
                module App

                export fn i32[min max] main()
                {
                    return GetValue();
                }
                """);

            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-lib",
                    "-o", packageLibraryPath,
                    "--native-source", nativeSourcePath,
                    "--native-library-dir", nativeDirectory,
                    "--native-library", "demo"
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());
            File.Delete(packageSourcePath);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", executablePath],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(executablePath));

            var stagedRuntimeDllPath = Path.Combine(appDirectory, "demo.dll");
            Assert.True(File.Exists(stagedRuntimeDllPath));
            Assert.False(string.Equals(Path.GetFullPath(runtimeDllPath), Path.GetFullPath(stagedRuntimeDllPath), StringComparison.OrdinalIgnoreCase));

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
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitExecutableUsesPackageNativePkgConfigMetadata()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-pkg-native-pkg-config-");
        var packageSourcePath = Path.Combine(tempDirectory.FullName, "NativePkgDemo.stark");
        var nativeSourcePath = Path.Combine(tempDirectory.FullName, "NativePkgDemo.c");
        var includeDirectory = Path.Combine(tempDirectory.FullName, "include");
        var libraryDirectory = Path.Combine(tempDirectory.FullName, "native-libs");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var libraryPath = Path.Combine(packageDirectory, "libNativePkgDemo.a");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var executablePath = Path.Combine(tempDirectory.FullName, "app");
        var pkgConfigLogPath = Path.Combine(tempDirectory.FullName, "pkg-config.log");
        var linkerLogPath = Path.Combine(tempDirectory.FullName, "linker.log");
        Directory.CreateDirectory(includeDirectory);
        Directory.CreateDirectory(libraryDirectory);
        Directory.CreateDirectory(packageDirectory);

        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            await File.WriteAllTextAsync(
                packageSourcePath,
                """
                module NativePkgDemo

            unsafe ffi fn i32[min max] stark_native_value();

            public fn i32[min max] GetValue()
            {
                unsafe
                {
                    return stark_native_value();
                }
            }
            """);
            await File.WriteAllTextAsync(
                nativeSourcePath,
                """
                #include "native_demo.h"

                int stark_native_value(void) {
                    return NATIVE_DEMO_VALUE;
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(includeDirectory, "native_demo.h"),
                "#define NATIVE_DEMO_VALUE 42\n");
            await File.WriteAllTextAsync(
                appPath,
                """
                import NativePkgDemo
                module App

                export fn i32[min max] main()
                {
                    return GetValue();
                }
                """);

            var pkgConfigPath = await CreateUnixPkgConfigAsync(
                tempDirectory.FullName,
                pkgConfigLogPath,
                includeDirectory,
                libraryDirectory);
            var linkerPath = await CreateUnixCaptureLinkerAsync(tempDirectory.FullName, linkerLogPath);
            Environment.SetEnvironmentVariable(
                "PATH",
                $"{Path.GetDirectoryName(pkgConfigPath)}{Path.PathSeparator}{originalPath}");

            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-lib",
                    "-o", libraryPath,
                    "--native-source", nativeSourcePath,
                    "--native-pkg-config", "native-demo"
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());
            File.Delete(packageSourcePath);

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    appPath,
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-o", executablePath,
                    "--linker", linkerPath
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(executablePath));

            var pkgConfigLog = await File.ReadAllTextAsync(pkgConfigLogPath);
            Assert.Contains("--cflags", pkgConfigLog, StringComparison.Ordinal);
            Assert.Contains("--libs", pkgConfigLog, StringComparison.Ordinal);
            Assert.Contains("native-demo", pkgConfigLog, StringComparison.Ordinal);

            var linkerLog = await File.ReadAllTextAsync(linkerLogPath);
            Assert.Contains(Path.GetFullPath(libraryDirectory), linkerLog, StringComparison.Ordinal);
            Assert.Contains("-lNativeDemoSystem", linkerLog, StringComparison.Ordinal);
            Assert.Contains("-pthread", linkerLog, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageModePrintsReadableSummaryForValidPackageImage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-pkg", "-o", packagePath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());
            Assert.True(File.Exists(packagePath));

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                [packagePath, "--inspect-pkg"],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.Equal(0, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr.ToString());

            var inspection = inspectStdout.ToString();
            Assert.Contains("package image:", inspection, StringComparison.Ordinal);
            Assert.Contains("root module: Demo", inspection, StringComparison.Ordinal);
            Assert.Contains("module count: 1", inspection, StringComparison.Ordinal);
            Assert.Contains("module Demo:", inspection, StringComparison.Ordinal);
            Assert.Contains("source-surface", inspection, StringComparison.Ordinal);
            Assert.Contains("typed-interface", inspection, StringComparison.Ordinal);
            Assert.Contains("compiler-facts", inspection, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageCommandSupportsTextAndJsonFormats()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-command-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-pkg", "-o", packagePath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());
            Assert.True(File.Exists(packagePath));

            var textStdout = new StringWriter();
            var textStderr = new StringWriter();
            var textExitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                textStdout,
                textStderr);

            Assert.Equal(0, textExitCode);
            Assert.Equal(string.Empty, textStderr.ToString());
            Assert.Contains("root module: Demo", textStdout.ToString(), StringComparison.Ordinal);

            var jsonStdout = new StringWriter();
            var jsonStderr = new StringWriter();
            var jsonExitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath, "--format", "json"],
                new StringReader(string.Empty),
                jsonStdout,
                jsonStderr);

            Assert.Equal(0, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr.ToString());

            var manifest = StarkPackageManifest.FromJson(jsonStdout.ToString());
            Assert.NotNull(manifest);
            Assert.Equal("Demo", manifest!.RootModule);
            Assert.Single(manifest.Modules);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitPackageModeRecordsExplicitTargetFactsAndInspectionPrintsThem()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-target-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    sourcePath,
                    "--emit-pkg",
                    "--target",
                    "x86_64-unknown-linux-gnu",
                    "--target-data-layout",
                    "e-m:e-p270:32:32-p271:32:32-p272:64:64-p:64:64-i64:64-f80:128-n8:16:32:64-S128",
                    "--package-profile",
                    "release",
                    "-o",
                    packagePath
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());
            Assert.True(PackageImageLoader.TryLoadManifest(packagePath, out var manifest));
            Assert.NotNull(manifest.BuildProfile);
            Assert.Equal("release", manifest.BuildProfile!.Name);
            Assert.NotNull(manifest.Target);
            Assert.Equal("x86_64-unknown-linux-gnu", manifest.Target!.Triple);
            Assert.Equal("LP64", manifest.Target.CDataModel?.Kind);
            Assert.Equal(8, manifest.Target.AggregateLayout?.PointerSizeBytes);
            Assert.Equal(8, manifest.Target.AggregateLayout?.PointerAlignmentBytes);

            var inspectStdout = new StringWriter();
            var inspectStderr = new StringWriter();
            var inspectExitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                inspectStdout,
                inspectStderr);

            Assert.Equal(0, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr.ToString());
            var inspection = inspectStdout.ToString();
            Assert.Contains("build profile: release", inspection, StringComparison.Ordinal);
            Assert.Contains("target: x86_64-unknown-linux-gnu", inspection, StringComparison.Ordinal);
            Assert.Contains("target c data model: LP64", inspection, StringComparison.Ordinal);
            Assert.Contains("target aggregate layout: pointer-size=8, pointer-align=8", inspection, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task EmitLlvmRejectsDevPackageImageInReleaseBuild()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-pkg-profile-mismatch-");
        var packageSourceDirectory = Path.Combine(tempDirectory.FullName, "src");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageSourceDirectory);
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var packageSourcePath = Path.Combine(packageSourceDirectory, "Demo.stark");
        var packagePath = Path.Combine(packageDirectory, "Demo.starkpkg");
        var appPath = Path.Combine(appDirectory, "App.stark");
        await File.WriteAllTextAsync(packageSourcePath, DemoSource);
        await File.WriteAllTextAsync(
            appPath,
            """
            import Demo
            module App

            public fn i32[min max] Run()
            {
                return Demo.Run();
            }
            """);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-pkg",
                    "--target",
                    "x86_64-unknown-linux-gnu",
                    "--package-profile",
                    "dev",
                    "-o",
                    packagePath
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());

            var llvmStdout = new StringWriter();
            var llvmStderr = new StringWriter();
            var llvmExitCode = await CompilerCli.RunAsync(
                [
                    appPath,
                    "--emit-llvm",
                    "-I",
                    packageDirectory,
                    "--target",
                    "x86_64-unknown-linux-gnu",
                    "--package-profile",
                    "release"
                ],
                new StringReader(string.Empty),
                llvmStdout,
                llvmStderr);

            Assert.Equal(1, llvmExitCode);
            Assert.Equal(string.Empty, llvmStdout.ToString());
            var diagnostics = llvmStderr.ToString();
            Assert.Contains("STK7325", diagnostics, StringComparison.Ordinal);
            Assert.Contains("was built for profile 'dev'", diagnostics, StringComparison.Ordinal);
            Assert.Contains("active build profile is 'release'", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task CheckModeRejectsPackageImageBuiltForDifferentTarget()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-pkg-target-mismatch-");
        var packageSourceDirectory = Path.Combine(tempDirectory.FullName, "src");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageSourceDirectory);
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var packageSourcePath = Path.Combine(packageSourceDirectory, "Demo.stark");
        var packagePath = Path.Combine(packageDirectory, "Demo.starkpkg");
        var appPath = Path.Combine(appDirectory, "App.stark");
        await File.WriteAllTextAsync(packageSourcePath, DemoSource);
        await File.WriteAllTextAsync(
            appPath,
            """
            import Demo
            module App

            public fn i32[min max] Run()
            {
                return Demo.Run();
            }
            """);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    packageSourcePath,
                    "--emit-pkg",
                    "--target",
                    "x86_64-unknown-linux-gnu",
                    "-o",
                    packagePath
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());

            var checkStdout = new StringWriter();
            var checkStderr = new StringWriter();
            var checkExitCode = await CompilerCli.RunAsync(
                [
                    appPath,
                    "--check",
                    "-I",
                    packageDirectory,
                    "--target",
                    "aarch64-unknown-linux-gnu"
                ],
                new StringReader(string.Empty),
                checkStdout,
                checkStderr);

            Assert.Equal(1, checkExitCode);
            Assert.Equal(string.Empty, checkStdout.ToString());
            var diagnostics = checkStderr.ToString();
            Assert.Contains("STK7311", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Package image module 'Demo' was built for target triple 'x86_64-unknown-linux-gnu'", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageModeReportsValidationDiagnosticsForMalformedContent()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-invalid-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg.json");
        await File.WriteAllTextAsync(
            packagePath,
            """
            {
                "RootModule": "Demo",
              "LibraryFileName": "libDemo.a",
              "Modules": [
                {
                    "ModuleName": "Demo",
                  "ReExports": [],
                  "Functions": [],
                  "Types": [],
                  "Globals": []
                },
                {
                    "ModuleName": "Demo",
                  "ReExports": [],
                  "Functions": [],
                  "Types": [],
                  "Globals": []
                }
              ]
            }
            """);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [packagePath, "--inspect-pkg"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7106", diagnostics, StringComparison.Ordinal);
            Assert.Contains("package-image", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageModeReportsValidationDiagnosticsForUnsupportedBuildProfile()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-profile-invalid-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg.json");
        await File.WriteAllTextAsync(
            packagePath,
            """
            {
              "RootModule": "Demo",
              "LibraryFileName": "libDemo.a",
              "BuildProfile": { "Name": "debug" },
              "Modules": [
                {
                  "ModuleName": "Demo",
                  "ReExports": [],
                  "Functions": [],
                  "Types": [],
                  "Globals": []
                }
              ]
            }
            """);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [packagePath, "--inspect-pkg"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7129", diagnostics, StringComparison.Ordinal);
            Assert.Contains("build profile 'debug' is not supported", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageCommandReportsBinaryMagicDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-bad-magic-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        await File.WriteAllTextAsync(packagePath, "NOTSTARK package payload");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7120", diagnostics, StringComparison.Ordinal);
            Assert.Contains("package-image-binary", diagnostics, StringComparison.Ordinal);
            Assert.Contains("does not start with the STARKPKG magic", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageCommandReportsBinaryVersionDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-bad-version-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        var bytes = CreatePackageImageHeader(version: 999, encoding: 1, payloadLength: 0);
        await File.WriteAllBytesAsync(packagePath, bytes);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7121", diagnostics, StringComparison.Ordinal);
            Assert.Contains("package-image-binary", diagnostics, StringComparison.Ordinal);
            Assert.Contains("format version 999 is not supported", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageCommandReportsBinaryPayloadLengthDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-bad-length-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        var bytes = CreatePackageImageHeader(version: 1, encoding: 1, payloadLength: 99);
        await File.WriteAllBytesAsync(packagePath, bytes);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7123", diagnostics, StringComparison.Ordinal);
            Assert.Contains("package-image-binary", diagnostics, StringComparison.Ordinal);
            Assert.Contains("payload length is 99", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageCommandReportsUnknownRequiredSectionDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-unknown-section-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        var bytes = CreateSectionedPackageImage(
            sectionId: FourCc("NOPE"),
            flags: BinaryRequiredSectionFlag,
            offset: BinaryHeaderLength + BinarySectionEntryLength,
            length: 0,
            encoding: 0);
        await File.WriteAllBytesAsync(packagePath, bytes);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7131", diagnostics, StringComparison.Ordinal);
            Assert.Contains("unknown required section 'NOPE'", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageCommandReportsBinarySectionOffsetDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-bad-section-offset-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        var bytes = CreateSectionedPackageImage(
            sectionId: BinaryManifestSectionId,
            flags: BinaryRequiredSectionFlag,
            offset: 1,
            length: 4,
            encoding: BinaryBrotliJsonEncoding,
            sectionData: [0, 1, 2, 3]);
        await File.WriteAllBytesAsync(packagePath, bytes);

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7132", diagnostics, StringComparison.Ordinal);
            Assert.Contains("section 'MANF' has invalid offset/length", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Theory]
    [InlineData("STRS", "required STRS string-table section")]
    [InlineData("PINF", "required PINF package-facts section")]
    public async Task InspectPackageCommandReportsMissingRequiredBinarySectionDiagnostics(string sectionName, string expectedMessage)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-missing-section-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-pkg", "-o", packagePath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());

            var bytes = await File.ReadAllBytesAsync(packagePath);
            ReplaceSectionId(bytes, FourCc(sectionName), FourCc("SKIP"), flags: 0);
            await File.WriteAllBytesAsync(packagePath, bytes);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7133", diagnostics, StringComparison.Ordinal);
            Assert.Contains(expectedMessage, diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task InspectPackageCommandReportsBinaryFactMismatchDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-inspect-pkg-fact-mismatch-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        await File.WriteAllTextAsync(sourcePath, DemoSource);

        try
        {
            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-pkg", "-o", packagePath],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.Equal(0, emitExitCode);
            Assert.Equal(string.Empty, emitStderr.ToString());

            var bytes = await File.ReadAllBytesAsync(packagePath);
            ReplaceStringTableEntry(bytes, existing: "Demo", replacement: "Demu");
            await File.WriteAllBytesAsync(packagePath, bytes);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["inspect-pkg", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            var diagnostics = stderr.ToString();
            Assert.Contains("STK7135", diagnostics, StringComparison.Ordinal);
            Assert.Contains("binary root module fact 'Demu' does not match manifest root module 'Demo'", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Failure summary:", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task PackageImageLoaderReportsBinaryDecodeDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-loader-pkg-bad-binary-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg");
        await File.WriteAllTextAsync(packagePath, "NOTSTARK package payload");

        try
        {
            Assert.False(PackageImageLoader.TryLoadManifest(packagePath, out _, out var diagnostics));
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("STK7120", diagnostic.Code);
            Assert.Equal("package-image-binary", diagnostic.Stage);
            Assert.Contains("STARKPKG magic", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task PackageImageLoaderReportsLegacyJsonDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-loader-pkg-bad-json-");
        var packagePath = Path.Combine(tempDirectory.FullName, "Broken.starkpkg.json");
        await File.WriteAllTextAsync(packagePath, "{");

        try
        {
            Assert.False(PackageImageLoader.TryLoadManifest(packagePath, out _, out var diagnostics));
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("STK7101", diagnostic.Code);
            Assert.Equal("package-image", diagnostic.Stage);
            Assert.Contains("Package image JSON is malformed", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private const uint BinaryFormatVersion = 2;
    private const uint BinaryRequiredSectionFlag = 1;
    private const uint BinaryBrotliJsonEncoding = 1;
    private const uint BinaryManifestSectionId = (byte)'M'
        | ((uint)(byte)'A' << 8)
        | ((uint)(byte)'N' << 16)
        | ((uint)(byte)'F' << 24);
    private const uint BinaryStringTableSectionId = (byte)'S'
        | ((uint)(byte)'T' << 8)
        | ((uint)(byte)'R' << 16)
        | ((uint)(byte)'S' << 24);
    private const uint BinaryPackageFactsSectionId = (byte)'P'
        | ((uint)(byte)'I' << 8)
        | ((uint)(byte)'N' << 16)
        | ((uint)(byte)'F' << 24);
    private const int BinaryHeaderLength = 24;
    private const int BinarySectionEntryLength = 32;

    private const string DemoSource =
        """
        module Demo

        public fn i32[min max] Run()
        {
            return 7;
        }
        """;

    private static byte[] CreatePackageImageHeader(uint version, uint encoding, ulong payloadLength)
    {
        var bytes = new byte[24];
        System.Text.Encoding.ASCII.GetBytes("STARKPKG").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), encoding);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), payloadLength);
        return bytes;
    }

    private static byte[] CreateSectionedPackageImage(
        uint sectionId,
        uint flags,
        ulong offset,
        ulong length,
        uint encoding,
        byte[]? sectionData = null)
    {
        sectionData ??= [];
        var bytes = new byte[BinaryHeaderLength + BinarySectionEntryLength + sectionData.Length];
        System.Text.Encoding.ASCII.GetBytes("STARKPKG").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), BinaryFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), BinarySectionEntryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(BinaryHeaderLength), sectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(BinaryHeaderLength + 4), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(BinaryHeaderLength + 8), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(BinaryHeaderLength + 16), length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(BinaryHeaderLength + 24), encoding);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(BinaryHeaderLength + 28), 0);
        sectionData.CopyTo(bytes.AsSpan(BinaryHeaderLength + BinarySectionEntryLength));
        return bytes;
    }

    private static uint[] ReadBinarySectionIds(byte[] bytes)
    {
        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var sectionIds = new uint[sectionCount];
        for (var index = 0; index < sectionIds.Length; index++)
        {
            var entryOffset = BinaryHeaderLength + (index * BinarySectionEntryLength);
            sectionIds[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset));
        }

        return sectionIds;
    }

    private static void ReplaceStringTableEntry(byte[] bytes, string existing, string replacement)
    {
        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(existing),
            System.Text.Encoding.UTF8.GetByteCount(replacement));
        Assert.True(TryFindBinarySection(bytes, BinaryStringTableSectionId, out var sectionOffset, out var sectionLength));

        var offset = checked((int)sectionOffset);
        var end = checked(offset + (int)sectionLength);
        var stringCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(uint);

        for (var index = 0u; index < stringCount; index++)
        {
            var byteLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            offset += sizeof(uint);
            var value = System.Text.Encoding.UTF8.GetString(bytes, offset, byteLength);
            if (string.Equals(value, existing, StringComparison.Ordinal))
            {
                System.Text.Encoding.UTF8.GetBytes(replacement).CopyTo(bytes.AsSpan(offset, byteLength));
                return;
            }

            offset += byteLength;
        }

        throw new InvalidOperationException($"String table entry '{existing}' was not found before offset {end}.");
    }

    private static bool TryFindBinarySection(byte[] bytes, uint sectionId, out ulong offset, out ulong length)
    {
        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        for (var index = 0u; index < sectionCount; index++)
        {
            var entryOffset = BinaryHeaderLength + checked((int)index * BinarySectionEntryLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset)) == sectionId)
            {
                offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entryOffset + 8));
                length = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entryOffset + 16));
                return true;
            }
        }

        offset = 0;
        length = 0;
        return false;
    }

    private static void ReplaceSectionId(byte[] bytes, uint existingSectionId, uint replacementSectionId, uint flags)
    {
        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        for (var index = 0u; index < sectionCount; index++)
        {
            var entryOffset = BinaryHeaderLength + checked((int)index * BinarySectionEntryLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset)) == existingSectionId)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset), replacementSectionId);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 4), flags);
                return;
            }
        }

        throw new InvalidOperationException($"Section '{existingSectionId:X8}' was not found.");
    }

    private static uint FourCc(string value)
    {
        Assert.Equal(4, value.Length);
        return (byte)value[0]
            | ((uint)(byte)value[1] << 8)
            | ((uint)(byte)value[2] << 16)
            | ((uint)(byte)value[3] << 24);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return (1, string.Empty, $"Could not start '{fileName}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<string> CreateUnixPkgConfigAsync(
        string directory,
        string logPath,
        string includeDirectory,
        string libraryDirectory)
    {
        var path = Path.Combine(directory, "pkg-config");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$@" > "{{logPath}}"
            printf '%s\n' "-I{{includeDirectory}} -L{{libraryDirectory}} -lNativeDemoSystem -pthread"
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
    }

    private static async Task<string> CreateUnixCaptureLinkerAsync(string directory, string logPath)
    {
        var path = Path.Combine(directory, "capture-linker.sh");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$@" > "{{logPath}}"
            out=""
            prev=""
            for arg in "$@"; do
              if [ "$prev" = "-o" ]; then
                out="$arg"
                break
              fi
              prev="$arg"
            done
            : > "$out"
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
    }
}
