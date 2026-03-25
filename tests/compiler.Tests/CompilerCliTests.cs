using Stark.Compiler;

namespace compiler.Tests;

public sealed class CompilerCliTests
{
    [Fact]
    public async Task CheckModeReportsSuccess()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check"],
            new StringReader(
                """
                module Demo

                fn i32 Main() {
                    return 1;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Check succeeded.", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task EmitMirModePrintsMirModule()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--emit-mir"],
            new StringReader(
                """
                module Demo

                fn i32 Main(bool flag) {
                    return flag ? 1 : 2;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();
        Assert.Contains("mir module Demo", text);
        Assert.Contains("fn i32 Main(bool flag)", text);
        Assert.Contains("blocks:", text);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task EmitSsaModePrintsSsaModule()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--emit-ssa"],
            new StringReader(
                """
                module Demo

                fn i32 Main(bool left, bool right) {
                    return left && right ? 1 : 2;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();
        Assert.Contains("ssa module Demo", text);
        Assert.Contains("phi", text);
        Assert.Contains("branch", text);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task EmitMirModeSupportsOutputPath()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"stark-mir-{Guid.NewGuid():N}.txt");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["--emit-mir", "-o", outputPath],
                new StringReader(
                    """
                    module Demo

                    fn i32 Main() {
                        return 7;
                    }
                    """),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.Contains("mir module Demo", await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task EmitObjectModeWritesObjectFile()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var extension = OperatingSystem.IsWindows() ? ".obj" : ".o";
        var outputPath = Path.Combine(Path.GetTempPath(), $"stark-obj-{Guid.NewGuid():N}{extension}");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["--emit-obj", "-o", outputPath],
                new StringReader(
                    """
                    module Demo

                    fn i32 Main() {
                        return 7;
                    }
                    """),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted object file:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task CheckModeResolvesSourceImportsFromConfiguredSearchPath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-search-source-");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(packageDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var mathPath = Path.Combine(packageDirectory, "Math.stark");

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

                fn i32 Main() {
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
    public async Task EmitLibraryModeBuildsStaticLibraryAndManifest()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-lib-");
        var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var dependencyPath = Path.Combine(tempDirectory.FullName, "Math.stark");
        var extension = OperatingSystem.IsWindows() ? ".lib" : ".a";
        var outputPath = Path.Combine(tempDirectory.FullName, $"libFacade{extension}");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            await File.WriteAllTextAsync(
                dependencyPath,
                """
                module Math

                public finite law i32 Add(i32 left, i32 right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                rootPath,
                """
                export import Math
                module Facade

                public finite law i32 Double(i32 value) {
                    return Math.Add(value, value);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--emit-lib", "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            Assert.Contains("Emitted package manifest:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
            Assert.True(File.Exists(manifestPath));

            using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var root = manifest.RootElement;
            Assert.Equal("Facade", root.GetProperty("RootModule").GetString());
            Assert.Contains(
                root.GetProperty("Modules").EnumerateArray(),
                module => module.GetProperty("ModuleName").GetString() == "Facade"
                          && module.GetProperty("ReExports").EnumerateArray().Any(reExport => reExport.GetProperty("ModuleName").GetString() == "Math")
                          && module.GetProperty("Functions").EnumerateArray().Any(function => function.GetProperty("SymbolName").GetString() == "Facade.Double"));
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
    public async Task EmitExecutableModeBuildsImportedAggregateDependencies()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-import-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var dependencyPath = Path.Combine(tempDirectory.FullName, "Geometry.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                dependencyPath,
                """
                module Geometry

                public struct Box {
                    i32 Value;
                }

                public fn Box Make() {
                    return new Box() { Value = 7 };
                }

                public fn i32 Read(Box box) {
                    return box.Value;
                }
                """);

            await File.WriteAllTextAsync(
                rootPath,
                """
                import Geometry
                module App

                export ffi fn i32 main() {
                    return Geometry.Read(Geometry.Make());
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--emit-exe", "-o", outputPath],
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
            process!.WaitForExit();
            Assert.Equal(7, process.ExitCode);
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
    public async Task EmitExecutableModeLinksManifestBackedLibrariesWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-manifest-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
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
            Assert.Equal(string.Empty, buildStderr.ToString());

            File.Delete(facadePath);
            File.Delete(mathPath);

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
            process!.WaitForExit();
            Assert.Equal(7, process.ExitCode);
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
}
