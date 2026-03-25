using Stark.Compiler;

namespace compiler.Tests;

public sealed class StandardLibraryTests
{
    [Fact]
    public async Task StdLibPackageBuildsFromRepositorySources()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-build-");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var manifestPath = Path.Combine(tempDirectory.FullName, Path.GetFileNameWithoutExtension(libraryPath) + ".starkpkg.json");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var modules = manifest.RootElement.GetProperty("Modules").EnumerateArray().ToArray();

            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.Console");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.IO.Stdout");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.IO.Stderr");

            var rootModule = modules.Single(module => module.GetProperty("ModuleName").GetString() == "System");
            var reExports = rootModule.GetProperty("ReExports").EnumerateArray().Select(static item => item.GetProperty("ModuleName").GetString()).ToArray();
            Assert.Contains("System.Console", reExports);
            Assert.Contains("System.IO.Stdout", reExports);
            Assert.Contains("System.IO.Stderr", reExports);
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
    public async Task PackagedStdLibCanBeConsumedWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-app-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Equal(string.Empty, buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                export ffi fn i32 main() {
                    System.Console.Write("Hello");
                    System.Console.WriteLine(" World");
                    System.Console.WriteError("Err");
                    System.Console.WriteErrorLine("!");
                    return 0;
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

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("Hello World\n", processStdout);
            Assert.Equal("Err!\n", processStderr);
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for stdlib integration tests.");
    }
}
