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
    public async Task TestHelpUsesProjectCommandDriver()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-test-help-");

        try
        {
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test", "--help"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Usage: stark test", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Build and run Stark test projects.", stdout.ToString(), StringComparison.Ordinal);
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

                export fn i32[min max] main()
                {
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

    [Fact]
    public async Task TestRunsCurrentTestProjectFromManifest()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-project-test-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "demo-tests"
                version = "0.1.0"
                kind = "test"

                [test]
                root = "Tests.stark"
                output = "demo-tests"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Tests.stark"),
                """
                module Tests

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Running test project 'demo-tests'...", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Passed test project 'demo-tests'.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, ".stark", "build", "dev", "demo-tests", ExecutableFileName("demo-tests"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestReturnsFailureWhenTestExecutableFails()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-project-test-fail-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "failing-tests"
                version = "0.1.0"
                kind = "test"

                [test]
                root = "Tests.stark"
                output = "failing-tests"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Tests.stark"),
                """
                module Tests

                export fn i32[min max] main()
                {
                    return 7;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(1, exitCode);
            Assert.Contains("Running test project 'failing-tests'...", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Failed test project 'failing-tests' with exit code 7.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestRunsSolutionDefaultTestTargetAndPathDependencies()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-solution-test-");

        try
        {
            await CreateTestSolutionFixtureAsync(tempDirectory.FullName);
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Running test project 'math-tests'...", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Passed test project 'math-tests'.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, ".stark", "build", "dev", "math", LibraryFileName("Math"))));
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, ".stark", "build", "dev", "math-tests", ExecutableFileName("math-tests"))));
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

            public finite law i32[min max] Add(i32[min max] left, i32[min max] right)
            {
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

            export fn i32[min max] main()
            {
                return Math.Add(3, 4);
            }
            """);
    }

    private static async Task CreateTestSolutionFixtureAsync(string rootDirectory)
    {
        var mathDirectory = Path.Combine(rootDirectory, "math");
        var testsDirectory = Path.Combine(rootDirectory, "math-tests");
        Directory.CreateDirectory(mathDirectory);
        Directory.CreateDirectory(testsDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "Stark.solution.toml"),
            """
            [solution]
            name = "DemoTestSolution"
            members = ["math", "math-tests"]

            [defaults]
            build = ["math"]
            test = ["math-tests"]

            [aliases]
            math = "math"
            tests = "math-tests"

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

            public finite law i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(testsDirectory, "Stark.toml"),
            """
            [project]
            name = "math-tests"
            version = "0.1.0"
            kind = "test"

            [test]
            root = "MathTests.stark"
            output = "math-tests"

            [dependencies]
            math = { path = "../math" }

            [profiles.dev]
            opt = 0
            """);

        await File.WriteAllTextAsync(
            Path.Combine(testsDirectory, "MathTests.stark"),
            """
            import Math
            module MathTests

            export fn i32[min max] main()
            {
                if (Math.Add(3, 4) == 7)
                {
                    return 0;
                }

                return 1;
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
