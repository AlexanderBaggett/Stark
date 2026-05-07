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
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg.json");
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

            var manifest = StarkPackageManifest.FromJson(await File.ReadAllTextAsync(packagePath));
            Assert.NotNull(manifest);
            Assert.Equal("Demo", manifest!.RootModule);
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
    public async Task EmitPackageModeWritesNativeDependencyMetadata()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-emit-pkg-native-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Demo.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg.json");
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

            var manifest = StarkPackageManifest.FromJson(await File.ReadAllTextAsync(packagePath));
            Assert.NotNull(manifest);
            Assert.NotNull(manifest!.NativeDependencies);
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
        var packagePath = Path.Combine(packageDirectory, "Demo.starkpkg.json");
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

            var manifest = StarkPackageManifest.FromJson(await File.ReadAllTextAsync(packagePath));
            Assert.NotNull(manifest);
            Assert.NotNull(manifest!.NativeDependencies);

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

            public fn i32[min max] GetValue() {
                unsafe {
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

            export unsafe ffi fn i32[min max] main() {
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

                public fn i32[min max] GetValue() {
                    return stark_native_value();
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

                export unsafe ffi fn i32[min max] main() {
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
        var packagePath = Path.Combine(tempDirectory.FullName, "Demo.starkpkg.json");
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

    private const string DemoSource =
        """
        module Demo

        public fn i32[min max] Run() {
            return 7;
        }
        """;

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
