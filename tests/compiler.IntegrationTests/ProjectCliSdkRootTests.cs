using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class ProjectCliSdkRootTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildForwardsExplicitSdkRootToNestedCompiler(bool useEqualsForm)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-sdk-root-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "sdk-root-forwarding"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "sdk-root-forwarding"
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

            var sdkRoot = Path.Combine(tempDirectory.FullName, "sdk");
            Directory.CreateDirectory(sdkRoot);
            var sdkManifestPath = Path.Combine(sdkRoot, "sdk.json");
            await File.WriteAllTextAsync(sdkManifestPath, "{");

            Environment.CurrentDirectory = tempDirectory.FullName;

            var arguments = new List<string>
            {
                "build",
                "--target",
                targetInfo.Triple
            };
            if (useEqualsForm)
            {
                arguments.Add($"--sdk-root={Path.Combine(sdkRoot, ".")}");
            }
            else
            {
                arguments.Add("--sdk-root");
                arguments.Add(Path.Combine(sdkRoot, "."));
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                arguments.ToArray(),
                new StringReader(string.Empty),
                stdout,
                stderr);

            var stderrText = stderr.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("STK7401", stderrText, StringComparison.Ordinal);
            Assert.Contains("SDK manifest JSON is malformed", stderrText, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(sdkManifestPath), stderrText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Theory]
    [InlineData("build")]
    [InlineData("run")]
    [InlineData("test")]
    [InlineData("clean")]
    public async Task ProjectCommandHelpAdvertisesSdkRoot(string command)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [command, "--help"],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("--sdk-root <dir>", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task DevelopmentSdkSourceRootsAreExplicitAndInvalidateProjectStamp()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-development-sdk-");
        try
        {
            var sdkRoot = Path.Combine(tempDirectory.FullName, "sdk");
            var vendorSourceDirectory = Path.Combine(sdkRoot, "vendor", "src", "Vendor");
            var appDirectory = Path.Combine(tempDirectory.FullName, "app");
            Directory.CreateDirectory(vendorSourceDirectory);
            Directory.CreateDirectory(Path.Combine(sdkRoot, "stdlib", "src"));
            Directory.CreateDirectory(Path.Combine(sdkRoot, "stdlib", "templates"));
            Directory.CreateDirectory(appDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(vendorSourceDirectory, "DevelopmentProbe.stark"),
                """
                module Vendor.DevelopmentProbe

                public finite law i32[min max] Value()
                {
                    return 0;
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "Stark.toml"),
                """
                [project]
                name = "development-sdk-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "development-sdk-app"
                """);
            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "App.stark"),
                """
                import Vendor.DevelopmentProbe
                module App

                export fn i32[min max] main()
                {
                    return Value();
                }
                """);
            Assert.True(
                DevelopmentSdkManifestWriter.TryWrite(sdkRoot, out var manifestPath, out var error),
                error);

            Environment.CurrentDirectory = appDirectory;
            var firstStdout = new StringWriter();
            var firstStderr = new StringWriter();
            var firstExitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple, "--sdk-root", sdkRoot],
                new StringReader(string.Empty),
                firstStdout,
                firstStderr);
            Assert.True(firstExitCode == 0, firstStderr.ToString());
            var stampPath = Assert.Single(Directory.GetFiles(
                Path.Combine(appDirectory, "build"),
                ".stark-build-stamp",
                SearchOption.AllDirectories));
            var firstStamp = await File.ReadAllTextAsync(stampPath);

            await File.AppendAllTextAsync(manifestPath, Environment.NewLine);
            var secondStdout = new StringWriter();
            var secondStderr = new StringWriter();
            var secondExitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple, "--sdk-root", sdkRoot],
                new StringReader(string.Empty),
                secondStdout,
                secondStderr);
            Assert.True(secondExitCode == 0, secondStderr.ToString());
            Assert.NotEqual(firstStamp, await File.ReadAllTextAsync(stampPath));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ProjectAncestorVendorDirectoryIsNotAnImplicitSdk()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var originalSdkRoot = Environment.GetEnvironmentVariable(SdkRootResolver.EnvironmentVariableName);
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-no-ancestor-sdk-");
        try
        {
            var appDirectory = Path.Combine(tempDirectory.FullName, "app");
            var vendorSourceDirectory = Path.Combine(tempDirectory.FullName, "vendor", "src", "Vendor");
            Directory.CreateDirectory(appDirectory);
            Directory.CreateDirectory(vendorSourceDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(vendorSourceDirectory, "AncestorProbe.stark"),
                """
                module Vendor.AncestorProbe

                public finite law i32[min max] Value()
                {
                    return 0;
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "Stark.toml"),
                """
                [project]
                name = "no-ancestor-sdk"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "no-ancestor-sdk"
                """);
            await File.WriteAllTextAsync(
                Path.Combine(appDirectory, "App.stark"),
                """
                import Vendor.AncestorProbe
                module App

                export fn i32[min max] main()
                {
                    return Value();
                }
                """);

            Environment.SetEnvironmentVariable(SdkRootResolver.EnvironmentVariableName, null);
            Environment.CurrentDirectory = appDirectory;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            var stderrText = stderr.ToString();
            Assert.Contains("STK7496", stderrText, StringComparison.Ordinal);
            Assert.Contains("Official module 'Vendor.AncestorProbe'", stderrText, StringComparison.Ordinal);
            Assert.Contains("no active Stark SDK manifest is available", stderrText, StringComparison.Ordinal);
            Assert.DoesNotContain(vendorSourceDirectory, stderrText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SdkRootResolver.EnvironmentVariableName, originalSdkRoot);
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    private static void Cleanup(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch
        {
            // Best effort cleanup on platforms where a tool briefly retains a file handle.
        }
    }
}
