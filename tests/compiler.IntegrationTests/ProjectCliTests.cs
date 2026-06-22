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
            Assert.Contains("--target <triple>", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("--stage stage0", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task CleanHelpUsesProjectCommandDriver()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-clean-help-");

        try
        {
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["clean", "--help"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Usage: stark clean", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Default scope is `stage`.", stdout.ToString(), StringComparison.Ordinal);
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
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
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
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--stage", "stage0", "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "demo", ExecutableFileName("demo-app"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildUsesStageLocalStdlibSearchDirectory()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-stage-stdlib-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "stage-stdlib-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "stage-stdlib-app"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import System.StageProbe
                module App

                export fn i32[min max] main()
                {
                    return System.StageProbe.Value();
                }
                """);

            var stageStdlibModuleDirectory = Path.Combine(
                BuildStagePath(tempDirectory.FullName, targetInfo.Triple),
                "stdlib",
                "System");
            Directory.CreateDirectory(stageStdlibModuleDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(stageStdlibModuleDirectory, "StageProbe.stark"),
                """
                module System.StageProbe

                public finite law i32[min max] Value()
                {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "stage-stdlib-app", ExecutableFileName("stage-stdlib-app"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildUsesRepoStdlibSourceTreeDiscovery()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-repo-stdlib-src-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "repo-stdlib-source-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "repo-stdlib-source-app"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import System.RepoSourceProbe
                module App

                export fn i32[min max] main()
                {
                    return System.RepoSourceProbe.Value();
                }
                """);

            var stdlibSourceDirectory = Path.Combine(tempDirectory.FullName, "stdlib", "src", "System");
            Directory.CreateDirectory(stdlibSourceDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "stdlib", "Stark.toml"),
                """
                [project]
                name = "stdlib"
                version = "0.1.0"
                kind = "library"

                [library]
                root = "src/System.stark"
                output = "System"
                """);
            await File.WriteAllTextAsync(
                Path.Combine(stdlibSourceDirectory, "RepoSourceProbe.stark"),
                """
                module System.RepoSourceProbe

                public finite law i32[min max] Value()
                {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "repo-stdlib-source-app", ExecutableFileName("repo-stdlib-source-app"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildUsesRepoVendorSourceTreeDiscovery()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-repo-vendor-src-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "repo-vendor-source-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "repo-vendor-source-app"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import Vendor.RepoSourceProbe
                module App

                export fn i32[min max] main()
                {
                    return Vendor.RepoSourceProbe.Value();
                }
                """);

            var vendorSourceDirectory = Path.Combine(tempDirectory.FullName, "vendor", "src", "Vendor");
            Directory.CreateDirectory(vendorSourceDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "vendor", "Stark.toml"),
                """
                [project]
                name = "vendor"
                version = "0.1.0"
                kind = "library"

                [library]
                root = "src/Vendor/RepoSourceProbe.stark"
                output = "Vendor"
                """);
            await File.WriteAllTextAsync(
                Path.Combine(vendorSourceDirectory, "RepoSourceProbe.stark"),
                """
                module Vendor.RepoSourceProbe

                public finite law i32[min max] Value()
                {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "repo-vendor-source-app", ExecutableFileName("repo-vendor-source-app"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task RunPrefersRepoStdlibDistPackageBeforeRepoSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-repo-stdlib-dist-");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "repo-stdlib-dist-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "repo-stdlib-dist-app"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import System.RepoDistProbe
                module App

                export fn i32[min max] main()
                {
                    return System.RepoDistProbe.Value();
                }
                """);

            await CreateRepoStdlibDistProbePackageAsync(tempDirectory.FullName, targetInfo.Triple);

            var stdlibSourceDirectory = Path.Combine(tempDirectory.FullName, "stdlib", "src", "System");
            Directory.CreateDirectory(stdlibSourceDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(stdlibSourceDirectory, "RepoDistProbe.stark"),
                """
                module System.RepoDistProbe

                public finite law i32[min max] Value()
                {
                    return 7;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["run", "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                $"Expected repo stdlib dist package to run successfully. Exit: {exitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
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
    public async Task BuildUsesInstalledBundledStdlibPackageWhenNoRepoStdlibExists()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-installed-stdlib-");
        var installedPackage = await CreateInstalledStdlibProbePackageAsync(targetInfo.Triple);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "installed-stdlib-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "installed-stdlib-app"

                [profiles.dev]
                opt = 0
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import System.InstalledProbe
                module App

                export fn i32[min max] main()
                {
                    return System.InstalledProbe.Value();
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                $"Expected installed bundled stdlib package to build successfully. Exit: {exitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "installed-stdlib-app", ExecutableFileName("installed-stdlib-app"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            CleanupInstalledStdlibProbePackage(installedPackage);
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildReportsStdlibDiscoveryPathsForMissingSystemImport()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-stdlib-diagnostics-");
        const string targetTriple = "test-triple";

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "missing-stdlib-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "missing-stdlib-app"
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import System.DoesNotExist
                module App

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetTriple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            var stderrText = stderr.ToString();
            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("Unable to resolve imported module 'System.DoesNotExist'.", stderrText, StringComparison.Ordinal);
            Assert.Contains("Stark stdlib discovery failed while resolving a System.* import.", stderrText, StringComparison.Ordinal);
            Assert.Contains("Active stdlib context: profile=dev, target=test-triple, stage=stage0", stderrText, StringComparison.Ordinal);
            Assert.Contains(BuildStagePath(tempDirectory.FullName, targetTriple), stderrText, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(tempDirectory.FullName, "stdlib"), stderrText, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(AppContext.BaseDirectory, "stdlib"), stderrText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildReportsVendorDiscoveryPathsForMissingVendorImport()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-vendor-diagnostics-");
        const string targetTriple = "test-triple";

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "missing-vendor-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "missing-vendor-app"
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import Vendor.DoesNotExist
                module App

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetTriple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            var stderrText = stderr.ToString();
            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("Unable to resolve imported module 'Vendor.DoesNotExist'.", stderrText, StringComparison.Ordinal);
            Assert.Contains("Stark vendor library discovery failed while resolving a Vendor.* import.", stderrText, StringComparison.Ordinal);
            Assert.Contains("Active vendor library context: profile=dev, target=test-triple, stage=stage0", stderrText, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(tempDirectory.FullName, "vendor"), stderrText, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(AppContext.BaseDirectory, "vendor"), stderrText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ProjectBuildDoesNotUseStarkPathAsHiddenStdlibDiscovery()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var originalStarkPath = Environment.GetEnvironmentVariable("STARK_PATH");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-no-stark-path-");
        var globalSearchDirectory = Path.Combine(tempDirectory.FullName, "global-search");
        var globalSystemDirectory = Path.Combine(globalSearchDirectory, "System");
        const string targetTriple = "test-triple";

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Stark.toml"),
                """
                [project]
                name = "hidden-env-app"
                version = "0.1.0"
                kind = "executable"

                [executable]
                root = "App.stark"
                output = "hidden-env-app"
                """);

            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "App.stark"),
                """
                import System.GlobalProbe
                module App

                export fn i32[min max] main()
                {
                    return System.GlobalProbe.Value();
                }
                """);

            Directory.CreateDirectory(globalSystemDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(globalSystemDirectory, "GlobalProbe.stark"),
                """
                module System.GlobalProbe

                public finite law i32[min max] Value()
                {
                    return 0;
                }
                """);

            Environment.SetEnvironmentVariable("STARK_PATH", globalSearchDirectory);
            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--target", targetTriple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            var stderrText = stderr.ToString();
            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("Unable to resolve imported module 'System.GlobalProbe'.", stderrText, StringComparison.Ordinal);
            Assert.Contains("Stark stdlib discovery failed while resolving a System.* import.", stderrText, StringComparison.Ordinal);
            Assert.DoesNotContain(globalSearchDirectory, stderrText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_PATH", originalStarkPath);
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildRejectsUnavailableCompilerStage()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-unavailable-stage-");

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
            var exitCode = await CompilerCli.RunAsync(
                ["build", "--stage=stage1", "--target=test-triple"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("Compiler stage 'stage1' is not available yet.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task CleanDeletesSelectedStageByDefault()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-clean-stage-");
        const string targetTriple = "test-triple";

        try
        {
            await CreateMinimalExecutableProjectAsync(tempDirectory.FullName, "clean-demo");
            var stage0Path = BuildStagePath(tempDirectory.FullName, targetTriple, "stage0");
            var stage1Path = BuildStagePath(tempDirectory.FullName, targetTriple, "stage1");
            Directory.CreateDirectory(Path.Combine(stage0Path, "bin"));
            Directory.CreateDirectory(Path.Combine(stage1Path, "bin"));
            await File.WriteAllTextAsync(Path.Combine(stage0Path, "bin", "old"), "delete");
            await File.WriteAllTextAsync(Path.Combine(stage1Path, "bin", "keep"), "keep");

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["clean", "--target", targetTriple], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Deleted", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.False(Directory.Exists(stage0Path));
            Assert.True(Directory.Exists(stage1Path));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task CleanDeletesTargetAndProfileScopes()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-clean-profile-target-");
        const string targetTriple = "test-triple";
        const string otherTargetTriple = "other-triple";

        try
        {
            await CreateMinimalExecutableProjectAsync(tempDirectory.FullName, "clean-demo");
            var targetPath = Path.Combine(tempDirectory.FullName, "build", "dev", targetTriple);
            var otherTargetPath = Path.Combine(tempDirectory.FullName, "build", "dev", otherTargetTriple);
            var releaseProfilePath = Path.Combine(tempDirectory.FullName, "build", "release");
            Directory.CreateDirectory(Path.Combine(targetPath, "stage0"));
            Directory.CreateDirectory(Path.Combine(otherTargetPath, "stage0"));
            Directory.CreateDirectory(Path.Combine(releaseProfilePath, targetTriple, "stage0"));

            Environment.CurrentDirectory = tempDirectory.FullName;

            var targetStdout = new StringWriter();
            var targetStderr = new StringWriter();
            var targetExitCode = await CompilerCli.RunAsync(["clean", "target", "--target", targetTriple], new StringReader(string.Empty), targetStdout, targetStderr);

            Assert.Equal(0, targetExitCode);
            Assert.Equal(string.Empty, targetStderr.ToString());
            Assert.False(Directory.Exists(targetPath));
            Assert.True(Directory.Exists(otherTargetPath));
            Assert.True(Directory.Exists(releaseProfilePath));

            var profileStdout = new StringWriter();
            var profileStderr = new StringWriter();
            var profileExitCode = await CompilerCli.RunAsync(["clean", "profile"], new StringReader(string.Empty), profileStdout, profileStderr);

            Assert.Equal(0, profileExitCode);
            Assert.Equal(string.Empty, profileStderr.ToString());
            Assert.False(Directory.Exists(Path.Combine(tempDirectory.FullName, "build", "dev")));
            Assert.True(Directory.Exists(releaseProfilePath));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task CleanDeletesDiagnosticsAndArtifactsScopes()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-clean-artifacts-");
        const string targetTriple = "test-triple";

        try
        {
            await CreateMinimalExecutableProjectAsync(tempDirectory.FullName, "clean-demo");
            var stagePath = BuildStagePath(tempDirectory.FullName, targetTriple);
            var diagnosticsPath = Path.Combine(stagePath, "diagnostics");
            var artifactsPath = Path.Combine(stagePath, "artifacts");
            var binPath = Path.Combine(stagePath, "bin");
            Directory.CreateDirectory(diagnosticsPath);
            Directory.CreateDirectory(artifactsPath);
            Directory.CreateDirectory(binPath);
            await File.WriteAllTextAsync(Path.Combine(diagnosticsPath, "diagnostic.txt"), "delete");
            await File.WriteAllTextAsync(Path.Combine(artifactsPath, "artifact.txt"), "delete");
            await File.WriteAllTextAsync(Path.Combine(binPath, "program"), "keep");

            Environment.CurrentDirectory = tempDirectory.FullName;

            var diagnosticsStdout = new StringWriter();
            var diagnosticsStderr = new StringWriter();
            var diagnosticsExitCode = await CompilerCli.RunAsync(["clean", "diagnostics", "--target", targetTriple], new StringReader(string.Empty), diagnosticsStdout, diagnosticsStderr);

            Assert.Equal(0, diagnosticsExitCode);
            Assert.Equal(string.Empty, diagnosticsStderr.ToString());
            Assert.False(Directory.Exists(diagnosticsPath));
            Assert.True(Directory.Exists(artifactsPath));
            Assert.True(Directory.Exists(binPath));

            var artifactsStdout = new StringWriter();
            var artifactsStderr = new StringWriter();
            var artifactsExitCode = await CompilerCli.RunAsync(["clean", "artifacts", "--target", targetTriple], new StringReader(string.Empty), artifactsStdout, artifactsStderr);

            Assert.Equal(0, artifactsExitCode);
            Assert.Equal(string.Empty, artifactsStderr.ToString());
            Assert.False(Directory.Exists(artifactsPath));
            Assert.True(Directory.Exists(binPath));
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
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
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
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "math", LibraryFileName("Math"))));
            Assert.True(File.Exists(BuildPackageImagePath(tempDirectory.FullName, targetInfo.Triple, "math", "Math")));
            Assert.False(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "math", "libMath.starkpkg")));
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "app", ExecutableFileName("demo-app"))));
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
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
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
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "tests", "demo-tests", ExecutableFileName("demo-tests"))));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestGeneratesFactRunnerFromMetadataAndAppliesFilter()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-generated-test-filter-");

        try
        {
            var testDirectory = await CreateGeneratedRunnerFixtureAsync(tempDirectory.FullName);
            Environment.CurrentDirectory = testDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test", "--filter=Adds"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Passed test project 'generated-tests'.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());

            var generatedRunnerPath = GeneratedRunnerPath(testDirectory, targetInfo.Triple);
            Assert.True(File.Exists(generatedRunnerPath));

            var generatedRunner = await File.ReadAllTextAsync(generatedRunnerPath);
            Assert.Contains("import System.Testing", generatedRunner, StringComparison.Ordinal);
            Assert.Contains("System.Testing.RunFact(\"AddsNumbers\", AddsNumbers())", generatedRunner, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Testing.RunFact(\"FailsByDesign\", FailsByDesign())", generatedRunner, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ConcurrentFilteredGeneratedTestRunsSerializeSharedBuildOutput()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-generated-test-filter-race-");

        try
        {
            var testDirectory = await CreateGeneratedRunnerFixtureAsync(tempDirectory.FullName);
            Environment.CurrentDirectory = testDirectory;

            var passingStdout = new StringWriter();
            var passingStderr = new StringWriter();
            var failingStdout = new StringWriter();
            var failingStderr = new StringWriter();

            var passingRun = CompilerCli.RunAsync(
                ["test", "--filter=AddsNumbers"],
                new StringReader(string.Empty),
                passingStdout,
                passingStderr);
            var failingRun = CompilerCli.RunAsync(
                ["test", "--filter=FailsByDesign"],
                new StringReader(string.Empty),
                failingStdout,
                failingStderr);

            var exitCodes = await Task.WhenAll(passingRun, failingRun);

            Assert.Equal(0, exitCodes[0]);
            Assert.Equal(1, exitCodes[1]);
            Assert.Contains("Passed test project 'generated-tests'.", passingStdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, passingStderr.ToString());
            Assert.Contains("Running test project 'generated-tests'...", failingStdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Failed test project 'generated-tests' with exit code 1.", failingStderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestGeneratedFactRunnerAppliesPlatformGatesFromTargetTriple()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !TryGetPlatformSelectorForTarget(targetInfo.Triple, out var platformSelector))
        {
            return;
        }

        var otherPlatformSelector = platformSelector == "linux" ? "windows" : "linux";
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-generated-test-platform-");

        try
        {
            var testDirectory = await CreateGeneratedRunnerFixtureAsync(tempDirectory.FullName);
            await File.WriteAllTextAsync(
                Path.Combine(testDirectory, "GeneratedTests.stark"),
                $$"""
                  module GeneratedTests

                  [Fact]
                  [Platform({{platformSelector}})]
                  fn bool RunsHere()
                  {
                      return true;
                  }

                  [Fact]
                  [Platform({{otherPlatformSelector}})]
                  fn bool SkipsElsewhere()
                  {
                      return false;
                  }

                  [Fact]
                  [SkipPlatform({{platformSelector}})]
                  fn bool SkipsCurrent()
                  {
                      return false;
                  }

                  [Platform({{platformSelector}})]
                  struct CurrentPlatformGroup
                  {
                      [Fact]
                      static fn bool GroupRunsHere()
                      {
                          return true;
                      }
                  }

                  [Platform({{otherPlatformSelector}})]
                  struct OtherPlatformGroup
                  {
                      [Fact]
                      static fn bool GroupSkipsElsewhere()
                      {
                          return false;
                      }
                  }
                  """);
            Environment.CurrentDirectory = testDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Passed test project 'generated-tests'.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());

            var generatedRunnerPath = GeneratedRunnerPath(testDirectory, targetInfo.Triple);
            Assert.True(File.Exists(generatedRunnerPath));

            var generatedRunner = await File.ReadAllTextAsync(generatedRunnerPath);
            Assert.Contains("System.Testing.RunFact(\"RunsHere\", RunsHere())", generatedRunner, StringComparison.Ordinal);
            Assert.Contains(
                "System.Testing.RunFact(\"CurrentPlatformGroup.GroupRunsHere\", CurrentPlatformGroup.GroupRunsHere())",
                generatedRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "System.Testing.SkipFact(\"SkipsElsewhere\", \"target does not match [Platform]\")",
                generatedRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "System.Testing.SkipFact(\"SkipsCurrent\", \"excluded by [SkipPlatform]\")",
                generatedRunner,
                StringComparison.Ordinal);
            Assert.Contains(
                "System.Testing.SkipFact(\"OtherPlatformGroup.GroupSkipsElsewhere\", \"target does not match [Platform]\")",
                generatedRunner,
                StringComparison.Ordinal);
            Assert.DoesNotContain("System.Testing.RunFact(\"SkipsElsewhere\", SkipsElsewhere())", generatedRunner, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Testing.RunFact(\"SkipsCurrent\", SkipsCurrent())", generatedRunner, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "System.Testing.RunFact(\"OtherPlatformGroup.GroupSkipsElsewhere\", OtherPlatformGroup.GroupSkipsElsewhere())",
                generatedRunner,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestGeneratedFactRunnerAppliesSerialCollections()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-generated-test-collections-");

        try
        {
            var testDirectory = await CreateGeneratedRunnerFixtureAsync(tempDirectory.FullName);
            await File.WriteAllTextAsync(
                Path.Combine(testDirectory, "GeneratedTests.stark"),
                """
                module GeneratedTests

                [Fact]
                fn bool Before()
                {
                    return true;
                }

                [Fact]
                [Collection(Toolchain)]
                fn bool ToolchainA()
                {
                    return true;
                }

                [Fact]
                fn bool Between()
                {
                    return true;
                }

                [Fact]
                [Collection("Toolchain")]
                fn bool ToolchainB()
                {
                    return true;
                }

                [Serial]
                struct SerialGroup
                {
                    [Fact]
                    static fn bool First()
                    {
                        return true;
                    }

                    [Fact]
                    static fn bool Second()
                    {
                        return true;
                    }
                }
                """);
            Environment.CurrentDirectory = testDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Passed test project 'generated-tests'.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());

            var generatedRunnerPath = GeneratedRunnerPath(testDirectory, targetInfo.Triple);
            Assert.True(File.Exists(generatedRunnerPath));

            var generatedRunner = await File.ReadAllTextAsync(generatedRunnerPath);
            AssertInOrder(
                generatedRunner,
                "System.Testing.RunFact(\"Before\", Before())",
                "    // Test collection: Toolchain",
                "System.Testing.RunFact(\"ToolchainA\", ToolchainA())",
                "System.Testing.RunFact(\"ToolchainB\", ToolchainB())",
                "System.Testing.RunFact(\"Between\", Between())",
                "    // Test collection: Serial",
                "System.Testing.RunFact(\"SerialGroup.First\", SerialGroup.First())",
                "System.Testing.RunFact(\"SerialGroup.Second\", SerialGroup.Second())");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestGeneratedFactRunnerExpandsInlineDataTheories()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-generated-test-theories-");

        try
        {
            var testDirectory = await CreateGeneratedRunnerFixtureAsync(tempDirectory.FullName);
            await File.WriteAllTextAsync(
                Path.Combine(testDirectory, "GeneratedTests.stark"),
                """
                module GeneratedTests

                [Theory]
                [InlineData(1, 2, 3)]
                [InlineData(-2, 5, 3)]
                fn bool Adds(i32[min max] left, i32[min max] right, i32[min max] expected)
                {
                    return left + right == expected;
                }

                [Collection(TextCases)]
                [Theory]
                [InlineData("stark", true)]
                fn bool TextCase(ascii name, bool expected)
                {
                    return expected;
                }
                """);
            Environment.CurrentDirectory = testDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Passed test project 'generated-tests'.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());

            var generatedRunnerPath = GeneratedRunnerPath(testDirectory, targetInfo.Triple);
            Assert.True(File.Exists(generatedRunnerPath));

            var generatedRunner = await File.ReadAllTextAsync(generatedRunnerPath);
            Assert.Contains("System.Testing.RunFact(\"Adds(1, 2, 3)\", Adds(1, 2, 3))", generatedRunner, StringComparison.Ordinal);
            Assert.Contains("System.Testing.RunFact(\"Adds(-2, 5, 3)\", Adds(-2, 5, 3))", generatedRunner, StringComparison.Ordinal);
            Assert.Contains("    // Test collection: TextCases", generatedRunner, StringComparison.Ordinal);
            Assert.Contains(
                "System.Testing.RunFact(\"TextCase(\\\"stark\\\", true)\", TextCase(\"stark\", true))",
                generatedRunner,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestGeneratedFactRunnerExpandsMemberDataTheories()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-generated-test-member-data-");

        try
        {
            var testDirectory = await CreateGeneratedRunnerFixtureAsync(tempDirectory.FullName);
            await File.WriteAllTextAsync(
                Path.Combine(testDirectory, "GeneratedTests.stark"),
                """
                module GeneratedTests

                record AddRow(i32[min max] Left, i32[min max] Right, i32[min max] Expected) { }

                finite law AddRow AddRows(u64[0 2 ** 63 - 1] index)
                {
                    switch (index)
                    {
                        case 0:
                            return new AddRow(1, 2, 3);
                        default:
                            return new AddRow(-2, 5, 3);
                    }
                }

                [Theory]
                [MemberData(AddRows, AddRow, 2, Left, Right, Expected)]
                fn bool Adds(i32[min max] left, i32[min max] right, i32[min max] expected)
                {
                    return left + right == expected;
                }
                """);
            Environment.CurrentDirectory = testDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Passed test project 'generated-tests'.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());

            var generatedRunnerPath = GeneratedRunnerPath(testDirectory, targetInfo.Triple);
            Assert.True(File.Exists(generatedRunnerPath));

            var generatedRunner = await File.ReadAllTextAsync(generatedRunnerPath);
            Assert.Contains("stack AddRow __stark_member_data_0 = AddRows(0);", generatedRunner, StringComparison.Ordinal);
            Assert.Contains(
                "System.Testing.RunFact(\"Adds[AddRows:0]\", Adds(__stark_member_data_0.Left, __stark_member_data_0.Right, __stark_member_data_0.Expected))",
                generatedRunner,
                StringComparison.Ordinal);
            Assert.Contains("stack AddRow __stark_member_data_1 = AddRows(1);", generatedRunner, StringComparison.Ordinal);
            Assert.Contains(
                "System.Testing.RunFact(\"Adds[AddRows:1]\", Adds(__stark_member_data_1.Left, __stark_member_data_1.Right, __stark_member_data_1.Expected))",
                generatedRunner,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestGeneratedFactRunnerReportsFailingFactThroughExitCode()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-generated-test-fail-");

        try
        {
            var testDirectory = await CreateGeneratedRunnerFixtureAsync(tempDirectory.FullName);
            Environment.CurrentDirectory = testDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(1, exitCode);
            Assert.Contains("Running test project 'generated-tests'...", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Failed test project 'generated-tests' with exit code 1.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestRejectsFilterWhenNoGeneratedFactsExist()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-filter-no-facts-");

        try
        {
            await CreateSimpleTestProjectAsync(
                tempDirectory.FullName,
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
            var exitCode = await CompilerCli.RunAsync(
                ["test", "--filter", "Anything", "--target=test-triple"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("No [Fact] or [Theory] tests were found, so --filter cannot be applied.", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TestRejectsExplicitMainWhenFactRunnerIsGenerated()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("stark-project-cli-fact-main-conflict-");

        try
        {
            await CreateSimpleTestProjectAsync(
                tempDirectory.FullName,
                """
                module Tests

                [Fact]
                fn bool AddsNumbers()
                {
                    return 2 + 2 == 4;
                }

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            Environment.CurrentDirectory = tempDirectory.FullName;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(["test", "--target=test-triple"], new StringReader(string.Empty), stdout, stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("Remove the explicit 'main' function from the test root.", stderr.ToString(), StringComparison.Ordinal);
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
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
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
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "math", LibraryFileName("Math"))));
            Assert.True(File.Exists(BuildPackageImagePath(tempDirectory.FullName, targetInfo.Triple, "math", "Math")));
            Assert.False(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "bin", "math", "libMath.starkpkg")));
            Assert.True(File.Exists(BuildArtifactPath(tempDirectory.FullName, targetInfo.Triple, "tests", "math-tests", ExecutableFileName("math-tests"))));
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

    private static async Task<string> CreateGeneratedRunnerFixtureAsync(string rootDirectory)
    {
        var testingDirectory = Path.Combine(rootDirectory, "testing");
        var testDirectory = Path.Combine(rootDirectory, "generated-tests");
        Directory.CreateDirectory(testingDirectory);
        Directory.CreateDirectory(testDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(testingDirectory, "Stark.toml"),
            """
            [project]
            name = "testing"
            version = "0.1.0"
            kind = "library"

            [library]
            root = "Testing.stark"
            output = "TestSupport"

            [profiles.dev]
            opt = 0
            """);

        await File.WriteAllTextAsync(
            Path.Combine(testingDirectory, "Testing.stark"),
            """
            module System.Testing

            public fn u8[0 1] RunFact(ascii name, bool assertion)
            {
                if (assertion)
                {
                    return 0;
                }

                return 1;
            }

            public fn u8[0 1] SkipFact(ascii name, ascii reason)
            {
                return 0;
            }

            public fn u64[0 2 ** 63 - 1] CollectionArgumentCount()
            {
                return 0;
            }

            public fn bool CollectionArgumentEquals(u64[0 2 ** 63 - 1] index, ascii expected)
            {
                return false;
            }

            public fn void ReportUnknownCollection()
            {
            }

            public fn void ReportKnownCollection(ascii name)
            {
            }

            public fn void ReportNoFactsSelected()
            {
            }

            public fn i32[min max] ExitCode(u32[0 2 ** 31 - 1] failureCount)
            {
                if (failureCount == 0)
                {
                    return 0;
                }

                return 1;
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(testDirectory, "Stark.toml"),
            """
            [project]
            name = "generated-tests"
            version = "0.1.0"
            kind = "test"

            [test]
            root = "GeneratedTests.stark"
            output = "generated-tests"

            [dependencies]
            testing = { path = "../testing" }

            [profiles.dev]
            opt = 0
            """);

        await File.WriteAllTextAsync(
            Path.Combine(testDirectory, "GeneratedTests.stark"),
            """
            module GeneratedTests

            [Fact]
            fn bool AddsNumbers()
            {
                return 2 + 2 == 4;
            }

            [Fact]
            fn bool FailsByDesign()
            {
                return false;
            }
            """);

        return testDirectory;
    }

    private static async Task CreateSimpleTestProjectAsync(string rootDirectory, string sourceText)
    {
        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "Stark.toml"),
            """
            [project]
            name = "simple-tests"
            version = "0.1.0"
            kind = "test"

            [test]
            root = "Tests.stark"
            output = "simple-tests"

            [profiles.dev]
            opt = 0
            """);

        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "Tests.stark"),
            sourceText);
    }

    private static void AssertInOrder(string text, params string[] needles)
    {
        var currentIndex = -1;
        foreach (var needle in needles)
        {
            var nextIndex = text.IndexOf(needle, currentIndex + 1, StringComparison.Ordinal);
            Assert.True(nextIndex >= 0, $"Expected to find '{needle}' after index {currentIndex}.{Environment.NewLine}{text}");
            currentIndex = nextIndex;
        }
    }

    private static async Task CreateRepoStdlibDistProbePackageAsync(string rootDirectory, string targetTriple)
    {
        var stdlibRootDirectory = Path.Combine(rootDirectory, "stdlib");
        var distDirectory = Path.Combine(stdlibRootDirectory, "dist");
        var packageSourceDirectory = Path.Combine(rootDirectory, "stdlib-package-src");
        Directory.CreateDirectory(distDirectory);
        Directory.CreateDirectory(packageSourceDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(stdlibRootDirectory, "Stark.toml"),
            """
            [project]
            name = "stdlib"
            version = "0.1.0"
            kind = "library"

            [library]
            root = "src/System.stark"
            output = "System"
            """);

        var packageSourcePath = Path.Combine(packageSourceDirectory, "RepoDistProbe.stark");
        await File.WriteAllTextAsync(
            packageSourcePath,
            """
            module System.RepoDistProbe

            public finite law i32[min max] Value()
            {
                return 0;
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            [
                packageSourcePath,
                "--emit-lib",
                "-o",
                Path.Combine(distDirectory, LibraryFileName("SystemRepoDistProbe")),
                "--package-image-output",
                Path.Combine(distDirectory, "libSystemRepoDistProbe.starkpkg"),
                "--target",
                targetTriple
            ],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Emitted package image:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    private static async Task<InstalledStdlibProbePackage> CreateInstalledStdlibProbePackageAsync(string targetTriple)
    {
        var stdlibRootDirectory = Path.Combine(AppContext.BaseDirectory, "stdlib");
        var distDirectory = Path.Combine(stdlibRootDirectory, "dist");
        var packageSourceDirectory = Path.Combine(AppContext.BaseDirectory, "installed-stdlib-probe-src");
        Directory.CreateDirectory(distDirectory);
        Directory.CreateDirectory(packageSourceDirectory);

        var packageSourcePath = Path.Combine(packageSourceDirectory, "InstalledProbe.stark");
        await File.WriteAllTextAsync(
            packageSourcePath,
            """
            module System.InstalledProbe

            public finite law i32[min max] Value()
            {
                return 0;
            }
            """);

        var libraryPath = Path.Combine(distDirectory, LibraryFileName("SystemInstalledProbe"));
        var packageImagePath = Path.Combine(distDirectory, "libSystemInstalledProbe.starkpkg");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(
            [
                packageSourcePath,
                "--emit-lib",
                "-o",
                libraryPath,
                "--package-image-output",
                packageImagePath,
                "--target",
                targetTriple
            ],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Emitted package image:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());

        return new InstalledStdlibProbePackage(
            stdlibRootDirectory,
            distDirectory,
            packageSourceDirectory,
            libraryPath,
            packageImagePath);
    }

    private static void CleanupInstalledStdlibProbePackage(InstalledStdlibProbePackage installedPackage)
    {
        TryDeleteFile(installedPackage.PackageImagePath);
        TryDeleteFile(installedPackage.LibraryPath);
        TryDeleteDirectory(installedPackage.PackageSourceDirectory, recursive: true);
        TryDeleteDirectory(installedPackage.DistDirectory);
        TryDeleteDirectory(installedPackage.StdlibRootDirectory);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static void TryDeleteDirectory(string path, bool recursive = false)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string ExecutableFileName(string name)
    {
        return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
    }

    private static string LibraryFileName(string outputName)
    {
        return OperatingSystem.IsWindows() ? $"{outputName}.lib" : $"lib{outputName}.a";
    }

    private static string BuildArtifactPath(string rootDirectory, string targetTriple, string artifactKind, string projectKey, string fileName)
    {
        return Path.Combine(
            BuildStagePath(rootDirectory, targetTriple),
            artifactKind,
            projectKey,
            fileName);
    }

    private static string BuildPackageImagePath(string rootDirectory, string targetTriple, string projectKey, string outputName)
    {
        return BuildArtifactPath(rootDirectory, targetTriple, "pkg", projectKey, $"lib{outputName}.starkpkg");
    }

    private static string GeneratedRunnerPath(string testDirectory, string targetTriple)
    {
        return Path.Combine(
            BuildStagePath(testDirectory, targetTriple),
            "tests",
            "generated-tests",
            "generated",
            "generated-tests.generated.stark");
    }

    private static string BuildStagePath(string rootDirectory, string targetTriple, string stageName = "stage0")
    {
        return Path.Combine(
            rootDirectory,
            "build",
            "dev",
            NormalizeBuildPathSegment(targetTriple),
            stageName);
    }

    private static string NormalizeBuildPathSegment(string value)
    {
        var chars = value.Trim().Select(static ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '+'
                ? ch
                : '_');
        var normalized = new string(chars.ToArray());
        return normalized.Length == 0 ? "_" : normalized;
    }

    private static bool TryGetPlatformSelectorForTarget(string targetTriple, out string selector)
    {
        var normalized = targetTriple.ToLowerInvariant();
        if (normalized.Contains("android", StringComparison.Ordinal))
        {
            selector = "android";
            return true;
        }

        if (normalized.Contains("ios", StringComparison.Ordinal))
        {
            selector = "ios";
            return true;
        }

        if (normalized.Contains("windows", StringComparison.Ordinal)
            || normalized.Contains("win32", StringComparison.Ordinal)
            || normalized.Contains("mingw", StringComparison.Ordinal))
        {
            selector = "windows";
            return true;
        }

        if (normalized.Contains("darwin", StringComparison.Ordinal)
            || normalized.Contains("macos", StringComparison.Ordinal))
        {
            selector = "macos";
            return true;
        }

        if (normalized.Contains("linux", StringComparison.Ordinal))
        {
            selector = "linux";
            return true;
        }

        selector = string.Empty;
        return false;
    }

    private static async Task CreateMinimalExecutableProjectAsync(string rootDirectory, string projectName)
    {
        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "Stark.toml"),
            $$"""
            [project]
            name = "{{projectName}}"
            version = "0.1.0"
            kind = "executable"

            [executable]
            root = "App.stark"
            output = "{{projectName}}"
            """);

        await File.WriteAllTextAsync(
            Path.Combine(rootDirectory, "App.stark"),
            """
            module App

            export fn i32[min max] main()
            {
                return 0;
            }
            """);
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

    private sealed record InstalledStdlibProbePackage(
        string StdlibRootDirectory,
        string DistDirectory,
        string PackageSourceDirectory,
        string LibraryPath,
        string PackageImagePath);
}
