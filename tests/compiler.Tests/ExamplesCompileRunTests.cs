using Stark.Compiler;

namespace compiler.Tests;

public sealed class ExamplesCompileRunTests
{
    [Fact]
    public async Task HelloExampleCompilesAndRunsWithStdlibPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-hello-");
        var stdlibPackageDirectory = Path.Combine(tempDirectory.FullName, "stdlib");
        Directory.CreateDirectory(stdlibPackageDirectory);

        var stdlibSource = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var stdlibLibrary = Path.Combine(stdlibPackageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var helloSource = Path.Combine(repositoryRoot, "examples", "hello.stark");
        var helloOutput = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "hello.exe" : "hello");

        try
        {
            await BuildStdlibPackageAsync(stdlibSource, stdlibLibrary);
            var compileResult = await CompileExecutableAsync(helloSource, helloOutput, stdlibPackageDirectory);

            Assert.True(File.Exists(helloOutput));
            Assert.Contains("Emitted executable:", compileResult.Stdout);

            var processResult = await RunNativeExecutableAsync(helloOutput);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("Hello, world!\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task MultiModuleExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-multi-");
        var appOutput = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "multi-module", "App.stark"),
                appOutput);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(appOutput);

            Assert.Equal(7, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task DataModelExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-data-model-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "data-model.exe" : "data-model");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "data-model", "DataModel.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(15, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task StaticLibraryExampleBuildsAndRunsFromPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-static-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var facadeSource = Path.Combine(repositoryRoot, "examples", "static-library", "Facade.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var appSource = Path.Combine(appDirectory, "App.stark");
        var appOutput = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await CompileLibraryAsync(facadeSource, libraryPath);

            await File.WriteAllTextAsync(
                appSource,
                """
                import Facade
                module App

                export ffi fn i32 main() {
                    return Facade.Quadruple(5);
                }
                """);

            var compileResult = await CompileExecutableAsync(appSource, appOutput, packageDirectory);
            Assert.Contains("Emitted executable:", compileResult.Stdout);

            var processResult = await RunNativeExecutableAsync(appOutput);

            Assert.Equal(20, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    private static async Task CompileLibraryAsync(string sourcePath, string libraryPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [sourcePath, "--emit-lib", "-o", libraryPath],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(libraryPath));
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task<(string Stdout, string Stderr)> CompileExecutableAsync(
        string sourcePath,
        string outputPath,
        string? libraryDirectory = null)
    {
        var args = new List<string> { sourcePath, "--emit-exe", "-o", outputPath };
        if (libraryDirectory is not null)
        {
            args.Add("-I");
            args.Add(libraryDirectory);
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            args.ToArray(),
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        AssertCompilerLogsEmitted(stderr.ToString());
        return (stdout.ToString(), stderr.ToString());
    }

    private static async Task BuildStdlibPackageAsync(string sourcePath, string libraryPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [sourcePath, "--emit-lib", "-o", libraryPath],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(libraryPath));
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunNativeExecutableAsync(string executablePath)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, standardOutput, standardError);
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for example tests.");
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

    private static void AssertCompilerLogsEmitted(string text)
    {
        Assert.Equal(string.Empty, text);
    }
}
