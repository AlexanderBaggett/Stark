using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class ProjectCliTests
{
    [Fact]
    public async Task BuildHelpUsesProjectCommandDriver()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-help-");

        try
        {
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["build", "--help"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Usage: stark build", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildBuildsCurrentProjectFromManifest()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-project-build-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "demo"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "demo-app"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                module App

                export ffi fn i32[min max] main() {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["build"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, ".stark", "build", "dev", "demo", ExecutableFileName("demo-app"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildBuildsSolutionDefaultTargetAndPathDependencies()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-solution-build-");

        try
        {
            await CreateSolutionFixtureAsync(tempDirectory.FullName);
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["build"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, ".stark", "build", "dev", "math", LibraryFileName("Math"))));
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, ".stark", "build", "dev", "app", ExecutableFileName("demo-app"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task RunUsesSolutionDefaultRunTarget()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-solution-run-");

        try
        {
            await CreateSolutionFixtureAsync(tempDirectory.FullName);
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["run"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(7, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    private static async Task CreateSolutionFixtureAsync(string rootDirectory)
    {
        var mathDirectory = Path.Combine(rootDirectory, "math");
        var appDirectory = Path.Combine(rootDirectory, "app");
        Directory.CreateDirectory(mathDirectory);
        Directory.CreateDirectory(appDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "Stark.solution.toml"),
            """
            [solution]
            name = "DemoSolution"
            members = ["math", "app"]

            [defaults]
            build = ["app"]
            run = "app"

            [aliases]
            app = "app"
            math = "math"

            [profiles.dev]
            opt = 0
            """);

        await File.WriteAllTextAsync(
            Path.Combine(mathDirectory, "Stark.toml"),
            """
            [project]
            name = "math"
            version = "0.1.0"
            kind = "library"

            [library]
            root = "Math.stark"
            output = "Math"

            [profiles.dev]
            opt = 0
            """);

        await File.WriteAllTextAsync(
            Path.Combine(mathDirectory, "Math.stark"),
            """
            module Math

            public finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                return left + right;
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(appDirectory, "Stark.toml"),
            """
            [project]
            name = "app"
            version = "0.1.0"
            kind = "executable"

            [executable]
            root = "App.stark"
            output = "demo-app"

            [dependencies]
            math = { path = "../math" }

            [profiles.dev]
            opt = 0
            """);

        await File.WriteAllTextAsync(
            Path.Combine(appDirectory, "App.stark"),
            """
            import Math
            module App

            export ffi fn i32[min max] main() {
                return Math.Add(3, 4);
            }
            """);
    }

    private static string ExecutableFileName(string name)
    {
        return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
    }

    private static string LibraryFileName(string outputName)
    {
        return OperatingSystem.IsWindows() ? $"{outputName}.lib" : $"lib{outputName}.a";
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
