using System.Text.Json;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class MultiFileIntegrationTests
{
    [Fact]
    public async Task SiblingModulesResolveThroughTheSourceSearchPath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-source-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32 Add(i32 left, i32 right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Math
                module App

                fn i32 Run() {
                    return Math.Add(3, 4);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportedReExportsMakeTransitiveModulesAvailableToConsumingApps()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-reexport-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32 Add(i32 left, i32 right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public fn i32 Double(i32 value) {
                    return Math.Add(value, value);
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                fn i32 Run() {
                    return Math.Add(Facade.Double(2), 3);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ModuleQualifiedEnumCasesResolveThroughImportedEnumTypes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-enum-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var textDirectory = Path.Combine(packageDirectory, "System");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(textDirectory);
        Directory.CreateDirectory(appDirectory);

        var systemPath = Path.Combine(packageDirectory, "System.stark");
        var textPath = Path.Combine(textDirectory, "Text.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                systemPath,
                """
                export import System.Text
                module System
                """);

            await File.WriteAllTextAsync(
                textPath,
                """
                module System.Text

                public enum Encoding {
                    Binary,
                    UTF8,
                    UTF16,
                    UTF32,
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                fn i32 Run() {
                    stack System.Text.Encoding encoding = System.Text.Encoding.UTF8;
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ModulePrivateDeclarationsStayHiddenAcrossModuleBoundaries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-visibility-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                fn i32 HiddenAdd(i32 left, i32 right) {
                    return left + right;
                }

                public fn i32 Add(i32 left, i32 right) {
                    return HiddenAdd(left, right);
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public fn i32 Double(i32 value) {
                    return Math.Add(value, value);
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                fn i32 Run() {
                    return Math.HiddenAdd(Facade.Double(2), 3);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "-I", packageDirectory],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("HiddenAdd", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ManifestBackedLibrariesCanBeConsumedWithoutSourceFiles()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-manifest-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var manifestPath = Path.Combine(packageDirectory, "libFacade.starkpkg.json");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32 Add(i32 left, i32 right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public finite law i32 Double(i32 value) {
                    return Math.Add(value, value);
                }
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [facadePath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Contains("Emitted static library:", buildStdout.ToString());
            Assert.Contains("Emitted package manifest:", buildStdout.ToString());
            Assert.Equal(string.Empty, buildStderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
            {
                Assert.Equal("Facade", manifest.RootElement.GetProperty("RootModule").GetString());
                Assert.Contains(
                    manifest.RootElement.GetProperty("Modules").EnumerateArray(),
                    module => module.GetProperty("ModuleName").GetString() == "Facade"
                              && module.GetProperty("ReExports").EnumerateArray().Any(reExport => reExport.GetProperty("ModuleName").GetString() == "Math"));
            }

            File.Delete(mathPath);
            File.Delete(facadePath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                export ffi fn i32 main() {
                    return Math.Add(3, 4);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(7, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ManifestBackedPublicGlobalsLinkAcrossPackageBoundaries()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-multifile-global-manifest-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var globalsPath = Path.Combine(packageDirectory, "Globals.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Globals.lib" : "libGlobals.a");
        var manifestPath = Path.Combine(packageDirectory, "libGlobals.starkpkg.json");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                globalsPath,
                """
                module Globals

                public const i32 Answer = 7;
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [globalsPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Contains("Emitted static library:", buildStdout.ToString());
            Assert.Contains("Emitted package manifest:", buildStdout.ToString());
            Assert.Equal(string.Empty, buildStderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            File.Delete(globalsPath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Globals
                module App

                export ffi fn i32 main() {
                    return Globals.Answer;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(7, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    private static void Cleanup(DirectoryInfo tempDirectory)
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
