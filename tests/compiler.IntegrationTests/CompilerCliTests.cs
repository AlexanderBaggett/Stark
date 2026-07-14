using System.Text.Json;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
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

                fn i32[min max] Run()
                {
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
    public async Task CheckModeValidatesImportedSourceModuleBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-check-imported-");
        try
        {
            var depPath = Path.Combine(tempDirectory.FullName, "Dep.stark");
            var rootPath = Path.Combine(tempDirectory.FullName, "Root.stark");
            await File.WriteAllTextAsync(
                depPath,
                """
                module Dep

                public struct Table
                {
                    dynamic i64[min max] Items;

                    Table()
                    {
                        self.Items = new();
                    }

                    public law retborrow i64[min max] Get(borrow Table self, u64[0 2 ** 63 - 1] index)
                        where index < self.Items.Length
                    {
                        return self.Items[index];
                    }
                }

                public fn i64[min max] Consume(i64[min max] value)
                {
                    return value + 1;
                }

                public fn i64[min max] UseViolation(borrow Table table)
                {
                    if (table.Items.Length == 0)
                    {
                        return 0;
                    }

                    return Consume(table.Get(0));
                }
                """);
            await File.WriteAllTextAsync(
                rootPath,
                """
                import Dep
                module Root

                fn i64[min max] Run(borrow Table table)
                {
                    return UseViolation(table);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--check", "-I", tempDirectory.FullName, "--no-stark-path"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            // The violation lives in the imported module's body: check-mode parity
            // with binary builds requires the dependency sweep to surface it.
            Assert.Equal(1, exitCode);
            Assert.Contains("STK4003", stderr.ToString());
            Assert.Contains("Dep.stark", stderr.ToString());
            Assert.DoesNotContain("Check succeeded.", stdout.ToString());
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task ImportedModuleTypeCheckDiagnosticsReportTheImportedFilePath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-imported-path-");
        try
        {
            var depPath = Path.Combine(tempDirectory.FullName, "Dep.stark");
            var rootPath = Path.Combine(tempDirectory.FullName, "Root.stark");
            await File.WriteAllTextAsync(
                depPath,
                """
                module Dep

                public fn i64[min max] BrokenBody()
                {
                    return missingName;
                }
                """);
            await File.WriteAllTextAsync(
                rootPath,
                """
                import Dep
                module Root

                fn i64[min max] Run()
                {
                    return BrokenBody();
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--check", "-I", tempDirectory.FullName, "--no-stark-path"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            // The unknown symbol lives in the imported module's body, so the
            // diagnostic must carry Dep.stark, not the root compilation path.
            Assert.Equal(1, exitCode);
            Assert.Contains("STK3003", stderr.ToString());
            Assert.Contains("Dep.stark", stderr.ToString());
            Assert.DoesNotContain("Root.stark:", stderr.ToString());
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task CheckModeAcceptsCleanImportedSourceModules()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-check-imported-clean-");
        try
        {
            var depPath = Path.Combine(tempDirectory.FullName, "Dep.stark");
            var rootPath = Path.Combine(tempDirectory.FullName, "Root.stark");
            await File.WriteAllTextAsync(
                depPath,
                """
                module Dep

                public struct Table
                {
                    dynamic i64[min max] Items;

                    Table()
                    {
                        self.Items = new();
                    }

                    public law retborrow i64[min max] Get(borrow Table self, u64[0 2 ** 63 - 1] index)
                        where index < self.Items.Length
                    {
                        return self.Items[index];
                    }
                }

                public fn i64[min max] Consume(i64[min max] value)
                {
                    return value + 1;
                }

                public fn i64[min max] UseValue(borrow Table table)
                {
                    if (table.Items.Length == 0)
                    {
                        return 0;
                    }

                    stack i64[min max] value = table.Get(0);
                    return Consume(value);
                }
                """);
            await File.WriteAllTextAsync(
                rootPath,
                """
                import Dep
                module Root

                fn i64[min max] Run(borrow Table table)
                {
                    return UseValue(table);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--check", "-I", tempDirectory.FullName, "--no-stark-path"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString());
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task HelpOutputGroupsOptionsByWorkflow()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(["--help"], new StringReader(string.Empty), stdout, stderr);

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();
        Assert.Contains("Workflows:", text);
        Assert.Contains("Inputs and Outputs:", text);
        Assert.Contains("Targeting and Native Toolchain:", text);
        Assert.Contains("Compiler Logs:", text);
        Assert.Contains("doctor         Inspect compiler, runtime, target, toolchain, SDK, stdlib, and vendor setup", text);
        Assert.Contains("--target-cpu <cpu>", text);
        Assert.Contains("--target-feature <feature>", text);
        Assert.Contains("--relocation-model <default|static|pic|pie>", text);
        Assert.Contains("--code-model <tiny|small|kernel|medium|large>", text);
        Assert.Contains("--strict-integer-ranges", text);
        Assert.Contains("--toolchain-dir <dir>", text);
        Assert.Contains("--llvm-lib <path>", text);
        Assert.Contains("--link-arg <arg>", text);
        Assert.Contains("--native-source <path>", text);
        Assert.Contains("--native-library <name>", text);
        Assert.Contains("--native-pkg-config <name>", text);
        Assert.Contains("--save-temps <dir>", text);
        Assert.Contains("--toolchain-metrics <path>", text);
        Assert.Contains("--package-image-output <path>", text);
        Assert.Contains("--package-profile <dev|release>", text);
        Assert.Contains("--format <text|json>   Select doctor/inspect-pkg output format", text);
        Assert.Contains("--strict               Make doctor exit nonzero", text);
        Assert.Contains("--no-stark-path", text);
        Assert.Contains("--diagnostic-format <text|json>", text);
        Assert.Contains("--log-level <info|warning|error>     Set the minimum compiler log severity printed to stderr (default: warning)", text);
        Assert.Contains("--log-verbosity <normal|verbose>", text);
        Assert.Contains("--log-category <name>", text);
        Assert.Contains("--log-stage <pass-id>", text);
        Assert.Contains("--log-kind <pipeline|symbol|decision|gap>", text);
        Assert.Contains("(default)      Build an executable when the root source exports main; otherwise build a library", text);
        Assert.Contains("With no workflow flag, the compiler infers executable vs library from the root source.", text);
        Assert.Contains("Examples:", text);
        Assert.Contains("compiler app.stark", text);
        Assert.Contains("compiler app.stark --emit-llvm -o app.ll", text);
        Assert.Contains("compiler doctor", text);
        Assert.Contains("compiler doctor --strict --format json", text);
        Assert.Contains("compiler app.stark --diagnostic-format json", text);
        Assert.Contains("--compile-only", text);
        Assert.Contains("--link-only", text);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task DoctorCommandReportsRuntimeTargetToolchainAndLibraries()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [
                "doctor",
                "--target",
                "x86_64-unknown-linux-gnu",
                "--target-data-layout",
                "e-m:e-p270:32:32-p271:32:32-p272:64:64-p:64:64-i64:64-f80:128-n8:16:32:64-S128"
            ],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        var text = stdout.ToString();
        Assert.Contains("Stark doctor", text, StringComparison.Ordinal);
        Assert.Contains("compiler:", text, StringComparison.Ordinal);
        Assert.Contains("runtime:", text, StringComparison.Ordinal);
        Assert.Contains("runtime id:", text, StringComparison.Ordinal);
        Assert.Contains("target:", text, StringComparison.Ordinal);
        Assert.Contains("triple: x86_64-unknown-linux-gnu", text, StringComparison.Ordinal);
        Assert.Contains("c data model: LP64", text, StringComparison.Ordinal);
        Assert.Contains("toolchain:", text, StringComparison.Ordinal);
        Assert.Contains("libLLVM:", text, StringComparison.Ordinal);
        Assert.Contains("clang:", text, StringComparison.Ordinal);
        Assert.Contains("sdk:", text, StringComparison.Ordinal);
        Assert.Contains("libraries:", text, StringComparison.Ordinal);
        Assert.Contains("stdlib:", text, StringComparison.Ordinal);
        Assert.Contains("vendor:", text, StringComparison.Ordinal);
        Assert.Contains("diagnostics:", text, StringComparison.Ordinal);
        Assert.Contains("linux libc:", text, StringComparison.Ordinal);
        Assert.Contains("native/vendor dependencies:", text, StringComparison.Ordinal);
        Assert.Contains("status:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorDoesNotRequireLibLlvmForTheStage0TextualBackend()
    {
        var originalLlvmLibrary = Environment.GetEnvironmentVariable("STARK_LLVM_LIB");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-doctor-missing-libllvm-");
        try
        {
            Environment.SetEnvironmentVariable(
                "STARK_LLVM_LIB",
                Path.Combine(tempDirectory.FullName, "missing-libLLVM.dylib"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    "doctor",
                    "--target",
                    "x86_64-unknown-linux-gnu",
                    "--target-data-layout",
                    "e-m:e-p270:32:32-p271:32:32-p272:64:64-p:64:64-i64:64-f80:128-n8:16:32:64-S128"
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains(
                "note: libLLVM: libLLVM is not bundled, but the active Stage0 textual LLVM backend does not require it",
                stdout.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain("warning: libLLVM:", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_LLVM_LIB", originalLlvmLibrary);
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public void NativeToolchainResolverPrefersCliToolchainDirectoryBeforeEnvironment()
    {
        var originalToolchainDirectory = Environment.GetEnvironmentVariable("STARK_TOOLCHAIN_DIR");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-toolchain-resolver-cli-");

        try
        {
            var cliToolchain = CreateFakeToolchain(tempDirectory.FullName, "cli");
            var environmentToolchain = CreateFakeToolchain(tempDirectory.FullName, "environment");
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", environmentToolchain);

            var resolved = NativeToolchain.Resolve(new NativeToolchainResolutionOptions(
                CliToolchainDirectory: cliToolchain));

            Assert.Equal(NativeToolchainResolutionSource.CliOverride, resolved.Clang.Source);
            Assert.Equal(Path.Combine(cliToolchain, "bin", ToolFileName("clang")), resolved.Clang.Path);
            Assert.Equal(NativeToolchainResolutionSource.CliOverride, resolved.LlvmLibrary.Source);
            Assert.Equal(Path.Combine(cliToolchain, "lib", LlvmLibraryFileName()), resolved.LlvmLibrary.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", originalToolchainDirectory);
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public void NativeToolchainResolverPrefersEnvironmentBeforeUserConfig()
    {
        var originalToolchainDirectory = Environment.GetEnvironmentVariable("STARK_TOOLCHAIN_DIR");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-toolchain-resolver-env-");

        try
        {
            var userConfigToolchain = CreateFakeToolchain(tempDirectory.FullName, "user");
            var environmentToolchain = CreateFakeToolchain(tempDirectory.FullName, "environment");
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", environmentToolchain);

            var resolved = NativeToolchain.Resolve(new NativeToolchainResolutionOptions(
                UserConfigToolchainDirectory: userConfigToolchain));

            Assert.Equal(NativeToolchainResolutionSource.EnvironmentOverride, resolved.Clang.Source);
            Assert.Equal(Path.Combine(environmentToolchain, "bin", ToolFileName("clang")), resolved.Clang.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", originalToolchainDirectory);
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public void NativeToolchainResolverFindsBundledToolsFromSelectedSdkRoot()
    {
        var originalToolchainDirectory = Environment.GetEnvironmentVariable("STARK_TOOLCHAIN_DIR");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-toolchain-resolver-sdk-");

        try
        {
            var sdkRoot = Path.Combine(tempDirectory.FullName, "sdk");
            var bundledToolchain = CreateFakeToolchain(
                Path.Combine(sdkRoot, "toolchain"),
                "llvm-22.1.8");
            Directory.CreateDirectory(Path.Combine(sdkRoot, "bin"));
            File.WriteAllText(Path.Combine(sdkRoot, "sdk.json"), "{}");
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", null);

            var resolved = NativeToolchain.Resolve(new NativeToolchainResolutionOptions(
                SdkRootDirectory: sdkRoot));

            var canonicalToolchain = SdkRootResolver.CanonicalizeRootPath(bundledToolchain);
            Assert.Equal(NativeToolchainResolutionSource.Bundled, resolved.Clang.Source);
            Assert.Equal(Path.Combine(canonicalToolchain, "bin", ToolFileName("clang")), resolved.Clang.Path);
            Assert.Equal(NativeToolchainResolutionSource.Bundled, resolved.LlvmLibrary.Source);
            Assert.Equal(Path.Combine(canonicalToolchain, "lib", LlvmLibraryFileName()), resolved.LlvmLibrary.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", originalToolchainDirectory);
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public void NativeToolchainResolverKeepsMacOsSdkLocatorAvailableDuringPathIsolation()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/xcrun"))
        {
            return;
        }

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalToolchainDirectory = Environment.GetEnvironmentVariable("STARK_TOOLCHAIN_DIR");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-toolchain-isolated-path-");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tempDirectory.FullName);
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", null);

            var resolved = NativeToolchain.Resolve();

            Assert.Equal("/usr/bin/xcrun", resolved.Xcrun.Path);
            Assert.Equal(NativeToolchainResolutionSource.Path, resolved.Xcrun.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("STARK_TOOLCHAIN_DIR", originalToolchainDirectory);
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task DoctorCommandReportsExplicitToolchainDirectory()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-doctor-toolchain-dir-");

        try
        {
            var toolchainDirectory = CreateFakeToolchain(tempDirectory.FullName, "explicit");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    "doctor",
                    "--toolchain-dir",
                    toolchainDirectory,
                    "--target",
                    "x86_64-unknown-linux-gnu"
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            var text = stdout.ToString();
            Assert.Contains("search roots:", text, StringComparison.Ordinal);
            Assert.Contains(toolchainDirectory, text, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(toolchainDirectory, "bin", ToolFileName("clang")), text, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(toolchainDirectory, "lib", LlvmLibraryFileName()), text, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task DoctorCommandReportsWindowsCrtAndNativeDependencyRequirements()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [
                "doctor",
                "--target",
                "x86_64-pc-windows-msvc",
                "--native-pkg-config",
                "sqlite3",
                "--native-library",
                "sqlite3"
            ],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        var text = stdout.ToString();
        Assert.Contains("diagnostics:", text, StringComparison.Ordinal);
        Assert.Contains("windows crt: Windows executable links use the current linker-driver path", text, StringComparison.Ordinal);
        Assert.Contains("pkg-config packages: sqlite3", text, StringComparison.Ordinal);
        Assert.Contains("native dependencies: this invocation declares native dependency metadata", text, StringComparison.Ordinal);
        Assert.Contains("native/vendor dependencies:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorCommandReportsMissingPkgConfigGuidance()
    {
        var originalPkgConfig = Environment.GetEnvironmentVariable("STARK_PKG_CONFIG");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-doctor-missing-pkg-config-");

        try
        {
            Environment.SetEnvironmentVariable(
                "STARK_PKG_CONFIG",
                Path.Combine(tempDirectory.FullName, "missing-pkg-config"));

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    "doctor",
                    "--target",
                    "x86_64-unknown-linux-gnu",
                    "--native-pkg-config",
                    "raylib"
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            var text = stdout.ToString();
            Assert.Contains("warning: pkg-config: pkg-config is unavailable", text, StringComparison.Ordinal);
            Assert.Contains("this invocation declares pkg-config packages: raylib", text, StringComparison.Ordinal);
            Assert.Contains("status: warnings", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_PKG_CONFIG", originalPkgConfig);
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task DoctorCommandRejectsSourceInput()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(["doctor", "App.stark"], new StringReader(string.Empty), stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("doctor does not take a source input path.", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitLlvmIrRejectsContradictoryTargetDataLayout()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [
                "--emit-llvm",
                "--target",
                "x86_64-unknown-linux-gnu",
                "--target-data-layout",
                "e-p:32:32"
            ],
            new StringReader(
                """
                module Demo

                export fn i32[min max] main()
                {
                    return 0;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("STK7307", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("requires 64-bit pointers", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitLlvmIrDerivesDetectedDataLayoutForExplicitLocalTarget()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || string.IsNullOrWhiteSpace(targetInfo.DataLayout))
        {
            return;
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [
                "--emit-llvm",
                "--target",
                targetInfo.Triple
            ],
            new StringReader(
                """
                module Demo

                export fn i32[min max] main()
                {
                    return 0;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains($"target datalayout = \"{targetInfo.DataLayout}\"", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultExecutableOutputPathMatchesInputNameAndAddsExeForWindowsTargets()
    {
        var appPath = Path.Combine(Path.GetTempPath(), "hello.stark");

        var linuxOutput = CompilerCli.DeriveExecutableOutputPath(
            inputPath: appPath,
            moduleName: null,
            targetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null));
        var windowsOutput = CompilerCli.DeriveExecutableOutputPath(
            inputPath: appPath,
            moduleName: null,
            targetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null));

        Assert.Equal(Path.Combine(Path.GetTempPath(), "hello"), linuxOutput);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "hello.exe"), windowsOutput);
    }

    [Fact]
    public void DefaultExecutableOutputPathUsesModuleNameForStandardInput()
    {
        var output = CompilerCli.DeriveExecutableOutputPath(
            inputPath: null,
            moduleName: "Demo.App",
            targetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null));

        Assert.Equal(Path.GetFullPath("Demo.App.exe"), output);
    }

    [Fact]
    public async Task DefaultModeInfersExecutableFromExportedMain()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-default-exe-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "App");

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export fn i32[min max] main()
                {
                    return 7;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [rootPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString(), StringComparison.Ordinal);
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
    public async Task DefaultModeInfersLibraryWhenRootHasNoExportedMain()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var archiverPath = FindFirstAvailableTool("llvm-ar", "ar");
        if (archiverPath is null)
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-default-lib-");
        var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var extension = OperatingSystem.IsWindows() ? ".lib" : ".a";
        var outputPath = Path.Combine(tempDirectory.FullName, $"libFacade{extension}");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg");

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module Facade

                public finite law i32[min max] Double(i32[min max] value)
                {
                    return value + value;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--archiver", archiverPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Emitted package image:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.True(File.Exists(manifestPath));
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
    public async Task EmitLibraryStagesPackageLibraryBesideRoutedPackageImage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var archiverPath = FindFirstAvailableTool("llvm-ar", "ar");
        if (archiverPath is null)
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-lib-package-route-");
        var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var binDirectory = Path.Combine(tempDirectory.FullName, "bin");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "pkg");
        Directory.CreateDirectory(binDirectory);
        Directory.CreateDirectory(packageDirectory);

        var extension = OperatingSystem.IsWindows() ? ".lib" : ".a";
        var outputPath = Path.Combine(binDirectory, $"libFacade{extension}");
        var manifestPath = Path.Combine(packageDirectory, "libFacade.starkpkg");
        var packagedLibraryPath = Path.Combine(packageDirectory, $"libFacade{extension}");
        var relocatedDirectory = Path.Combine(tempDirectory.FullName, "relocated-pkg");
        var relocatedManifestPath = Path.Combine(relocatedDirectory, "libFacade.starkpkg");
        var relocatedLibraryPath = Path.Combine(relocatedDirectory, $"libFacade{extension}");

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module Facade

                public finite law i32[min max] Double(i32[min max] value)
                {
                    return value + value;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--emit-lib", "-o", outputPath, "--package-image-output", manifestPath, "--archiver", archiverPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Emitted package image:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.True(File.Exists(manifestPath));
            Assert.True(File.Exists(packagedLibraryPath));
            Assert.False(File.Exists(Path.Combine(binDirectory, "libFacade.starkpkg")));

            Assert.True(PackageImageLoader.TryLoadManifest(manifestPath, out var manifest));
            Assert.Equal($"libFacade{extension}", manifest.LibraryFileName);

            Directory.CreateDirectory(relocatedDirectory);
            File.Copy(manifestPath, relocatedManifestPath);
            File.Copy(packagedLibraryPath, relocatedLibraryPath);
            Directory.Delete(packageDirectory, recursive: true);
            Directory.Delete(binDirectory, recursive: true);

            var resolver = new FileSystemModuleResolver(relocatedDirectory);
            Assert.True(resolver.TryResolveModule("Facade", out var resolvedModule));
            Assert.Equal(
                Path.GetFullPath(relocatedLibraryPath),
                Path.GetFullPath(resolvedModule.LibraryPath!));
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
    public async Task PackageImageOutputRejectsExecutableEmission()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-package-output-exe-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var packagePath = Path.Combine(tempDirectory.FullName, "App.starkpkg.json");

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--check", "--package-image-output", packagePath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("--package-image-output is only valid for library emission.", stderr.ToString(), StringComparison.Ordinal);
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
    public async Task CheckModeRejectsPositiveSignedRangesByDefault()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check"],
            new StringReader(
                """
                module Demo

                fn i32[0 10] Run()
                {
                    return 0;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("error STK3014 [semantic-validate]", text, StringComparison.Ordinal);
        Assert.Contains("u8[0 10]", text, StringComparison.Ordinal);
        Assert.Contains("Use `u8[0 10]`", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrictIntegerRangeFlagRejectsPositiveSignedRanges()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check", "--strict-integer-ranges"],
            new StringReader(
                """
                module Demo

                fn i32[0 10] Run()
                {
                    return 0;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("error STK3014 [semantic-validate]", text, StringComparison.Ordinal);
        Assert.Contains("u8[0 10]", text, StringComparison.Ordinal);
        Assert.Contains("Use `u8[0 10]`", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonDiagnosticFormatEmitsStableMachineReadableDocument()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check", "--diagnostic-format", "json"],
            new StringReader(
                """
                module Demo

                fn bool Run()
                {
                    return 1;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());

        using var document = JsonDocument.Parse(stderr.ToString());
        var root = document.RootElement;

        Assert.False(root.GetProperty("succeeded").GetBoolean());
        var summary = root.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("warningCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("infoCount").GetInt32());

        var diagnostic = Assert.Single(root.GetProperty("diagnostics").EnumerateArray().ToArray());
        Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.GetProperty("message").GetString()));
        Assert.True(diagnostic.TryGetProperty("stage", out _));
        var location = diagnostic.GetProperty("location");
        Assert.True(location.GetProperty("line").GetInt32() > 0);
        Assert.True(location.GetProperty("column").GetInt32() > 0);
    }

    [Fact]
    public async Task TextDiagnosticsRenderSingleLineSourceSnippets()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check"],
            new StringReader(
                """
                module Demo

                fn bool Run()
                {
                    return 1;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("error", text, StringComparison.Ordinal);
        Assert.Contains("5 |     return 1;", text, StringComparison.Ordinal);
        Assert.Contains("^", text, StringComparison.Ordinal);
        Assert.Contains("Failure summary: 1 error, 0 warnings, 0 infos.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDiagnosticsExpandTabsBeforeRenderingCarets()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check", "--log-level", "error"],
            new StringReader("module Demo\n\nfn bool Run() {\n\treturn 1;\n}\n"),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("4:9: error", text, StringComparison.Ordinal);
        Assert.Contains("4 |     return 1;", text, StringComparison.Ordinal);
        Assert.Contains("    |            ^", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDiagnosticsRenderMultilineSpansAcrossSourceLines()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check", "--log-level", "error"],
            new StringReader(
                """
                module Demo

                fn ascii Run(ascii text, i32[min max] first, i32[min max] second, i32[min max] third)
                {
                    return text[
                        first,
                        second,
                        third
                    ];
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("STK3008", text, StringComparison.Ordinal);
        Assert.Contains("5 |     return text[", text, StringComparison.Ordinal);
        Assert.Contains("6 |         first,", text, StringComparison.Ordinal);
        Assert.Contains("7 |         second,", text, StringComparison.Ordinal);
        Assert.Contains("8 |         third", text, StringComparison.Ordinal);
        Assert.Contains("9 |     ];", text, StringComparison.Ordinal);
        Assert.Contains("^^^^^^^^^^^^^^", text, StringComparison.Ordinal);
        Assert.Contains("Failure summary: 1 error, 0 warnings, 0 infos.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDiagnosticsGroupCrossCodeNotesUnderTheirPrimaryDiagnostic()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check"],
            new StringReader(
                """
                module Demo

                fn i32[min max] Run(bool value)
                {
                    switch (value)
                    {
                        case true:
                            return 1;
                        case false:
                            return 0;
                        default:
                            return 2;
                    }
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("STK3019", text, StringComparison.Ordinal);
        Assert.Contains("note [type-check]", text, StringComparison.Ordinal);
        Assert.Contains("Switch coverage becomes exhaustive here for 'bool'.", text, StringComparison.Ordinal);
        Assert.Contains("9 |         case false:", text, StringComparison.Ordinal);
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

                fn i32[min max] Run(bool flag)
                {
                    return flag ? 1 : 2;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();
        Assert.Contains("mir module Demo", text);
        Assert.Contains("fn i32 Run(bool flag)", text);
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

                fn i32[min max] Run(bool left, bool right)
                {
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
    public async Task VerboseLogVerbosityRequiresExplicitInfoLogLevelForPipelineLifecycleEvents()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check", "--log-verbosity", "verbose", "--log-kind", "pipeline"],
            new StringReader(
                """
                module Demo

                fn i32[min max] Run()
                {
                    return 7;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Check succeeded.", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task ExplicitInfoLogLevelPrintsPipelineLifecycleEvents()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check", "--log-level", "info", "--log-verbosity", "verbose", "--log-kind", "pipeline"],
            new StringReader(
                """
                module Demo

                fn i32[min max] Run()
                {
                    return 7;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Check succeeded.", stdout.ToString());

        var text = stderr.ToString();
        Assert.Contains("Starting pass 'parse'. [info pipeline stage=parse", text, StringComparison.Ordinal);
        Assert.Contains("Completed pass 'ownership-validate'. [info pipeline stage=ownership-validate", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogLevelFilterCanSuppressInformationalPassLogs()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check", "--log-level", "warning"],
            new StringReader(
                """
                module Demo

                fn i32[min max] Run()
                {
                    return 7;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Check succeeded.", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task SuccessfulChecksPrintWarningsAndTextSummary()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check"],
            new StringReader(
                """
                module Demo

                struct Buffer
                {
                    i32[min max] Value;

                    mut drop
                    {
                        ;
                    }
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Check succeeded.", stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("warning STK4010", text, StringComparison.Ordinal);
        Assert.Contains("Summary: 0 errors, 1 warning, 0 infos.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDiagnosticsRenderSourceSnippetsForInfoNotesToo()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check"],
            new StringReader(
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;

                    drop
                    {
                        ;
                    }
                }

                fn void Consume(Box value)
                {
                    return;
                }

                fn i32[min max] Run()
                {
                    stack Box box = new Box()
                    {
                        Value = 1
                    };
                    Consume(box);
                    return box.Value;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("Move error", text, StringComparison.Ordinal);
        Assert.Contains("24 |     Consume(box);", text, StringComparison.Ordinal);
        Assert.Contains("25 |     return box.Value;", text, StringComparison.Ordinal);
        Assert.Contains("note [ownership-validate]", text, StringComparison.Ordinal);
        Assert.Contains("was moved here", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDiagnosticsDoNotRepeatTheSameOwnershipMoveError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--check"],
            new StringReader(
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;

                    drop
                    {
                        ;
                    }
                }

                fn void Consume(Box value)
                {
                    return;
                }

                fn i32[min max] Run()
                {
                    stack Box box = new Box()
                    {
                        Value = 1
                    };
                    Consume(box);
                    return box.Value;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Equal(
            1,
            text.Split(
                "error STK4200 [ownership-validate]: Move error: value 'box' was moved and must be reinitialized before it can be read.",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            text.Split(
                "note [ownership-validate] at 24:13: Value 'box' was moved here.",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("Failure summary: 1 error, 0 warnings, 1 info.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitMirModeReportsTypeErrorsInsteadOfLoweringWarningsForVoidCallsUsedAsValues()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--emit-mir", "--log-level", "warning", "--log-kind", "gap", "--log-category", "lowering", "--log-stage", "lower-mir"],
            new StringReader(
                """
                module Demo

                fn void A()
                {
                    return;
                }

                fn bool Run(bool flag)
                {
                    return (flag ? A() : A()) == (flag ? A() : A());
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());

        var text = stderr.ToString();
        Assert.Contains("error STK3002 [type-check]", text, StringComparison.Ordinal);
        Assert.Contains("Operator '==' cannot compare 'void' and 'void'.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[warn gap lowering stage=lower-mir", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Starting pass", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitLlvmModeFailsWithTypeDiagnosticForVoidCallsUsedAsValues()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--emit-llvm"],
            new StringReader(
                """
                module Demo

                fn void A()
                {
                    return;
                }

                fn bool Run(bool flag)
                {
                    return (flag ? A() : A()) == (flag ? A() : A());
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("error STK3002 [type-check]", text, StringComparison.Ordinal);
        Assert.Contains("Operator '==' cannot compare 'void' and 'void'.", text, StringComparison.Ordinal);
        Assert.Contains("Failure summary: 1 error, 0 warnings, 0 infos.", text, StringComparison.Ordinal);
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

                    fn i32[min max] Run()
                    {
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

                    fn i32[min max] Run()
                    {
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
    public async Task CompileOnlyAliasWritesObjectFile()
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
                ["--compile-only", "-o", outputPath],
                new StringReader(
                    """
                    module Demo

                    fn i32[min max] Run()
                    {
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
    public async Task EmitObjectModeForwardsTargetCpuAndFeaturesToClang()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-target-cpu-feature-");
        var outputPath = Path.Combine(tempDirectory.FullName, "app.o");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        _ = await CreateUnixCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    "--emit-obj",
                    "-o", outputPath,
                    "--target", "x86_64-unknown-linux-gnu",
                    "--target-cpu", "znver4",
                    "--target-feature", "+sse4.1",
                    "--target-feature=-avx"
                ],
                new StringReader(
                    """
                    module Demo

                    fn i32[min max] Run()
                    {
                        return 7;
                    }
                    """),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted object file:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLog = await File.ReadAllTextAsync(clangLogPath);
            Assert.Contains("-target", clangLog, StringComparison.Ordinal);
            Assert.Contains("x86_64-unknown-linux-gnu", clangLog, StringComparison.Ordinal);
            Assert.Contains("-mcpu=znver4", clangLog, StringComparison.Ordinal);
            Assert.Contains("-target-feature", clangLog, StringComparison.Ordinal);
            Assert.Contains("+sse4.1", clangLog, StringComparison.Ordinal);
            Assert.Contains("-avx", clangLog, StringComparison.Ordinal);
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
    public async Task EmitObjectModeForwardsRelocationModelAndCodeModelToClang()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-target-models-");
        var outputPath = Path.Combine(tempDirectory.FullName, "app.o");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        _ = await CreateUnixCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    "--emit-obj",
                    "-o", outputPath,
                    "--target", "x86_64-unknown-linux-gnu",
                    "--relocation-model", "pie",
                    "--code-model", "large"
                ],
                new StringReader(
                    """
                    module Demo

                    fn i32[min max] Run()
                    {
                        return 7;
                    }
                    """),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted object file:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLog = await File.ReadAllTextAsync(clangLogPath);
            Assert.Contains("-fPIE", clangLog, StringComparison.Ordinal);
            Assert.Contains("-mcmodel=large", clangLog, StringComparison.Ordinal);
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
    public async Task EmitObjectModeKeepsLlvmPassesForImportedInlineBodyClones()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-imported-inline-llvm-passes-");
        var libPath = Path.Combine(tempDirectory.FullName, "Lib.stark");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app.o");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        _ = await CreateUnixCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            await File.WriteAllTextAsync(
                libPath,
                """
                module Lib

                public inline finite u8[0 120] SelectOrAdd(u8[0 100] value, bool add)
                {
                    stack u8[0 101] v1 = value + 1;
                    stack u8[0 102] v2 = v1 + 1;
                    stack u8[0 103] v3 = v2 + 1;
                    stack u8[0 104] v4 = v3 + 1;
                    stack u8[0 105] v5 = v4 + 1;
                    stack u8[0 106] v6 = v5 + 1;
                    stack u8[0 107] v7 = v6 + 1;
                    stack u8[0 108] v8 = v7 + 1;
                    stack u8[0 109] v9 = v8 + 1;
                    stack u8[0 110] v10 = v9 + 1;
                    stack u8[0 111] v11 = v10 + 1;
                    stack u8[0 112] v12 = v11 + 1;
                    stack u8[0 113] v13 = v12 + 1;

                    if (add)
                    {
                        return v13;
                    }

                    return v13 + 1;
                }
                """);
            await File.WriteAllTextAsync(
                appPath,
                """
                import Lib
                module App

                fn u8[0 120] Run(u8[0 100] value, bool add)
                {
                    return Lib.SelectOrAdd(value, add);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    appPath,
                    "--emit-obj",
                    "-I", tempDirectory.FullName,
                    "-o", outputPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted object file:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLog = await File.ReadAllTextAsync(clangLogPath);
            Assert.Contains("-O3", clangLog, StringComparison.Ordinal);
            Assert.DoesNotContain("-disable-llvm-passes", clangLog, StringComparison.Ordinal);
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

                public finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Math
                module App

                fn i32[min max] Run()
                {
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
    public async Task CheckModeCanUseStarkPathAndCanDisableIt()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-stark-path-");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(packageDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var supportPath = Path.Combine(packageDirectory, "EnvSupport.stark");
        var originalStarkPath = Environment.GetEnvironmentVariable("STARK_PATH");

        try
        {
            await File.WriteAllTextAsync(
                supportPath,
                """
                module EnvSupport

                public finite law i32[min max] Value()
                {
                    return 9;
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                import EnvSupport
                module App

                fn i32[min max] Run()
                {
                    return EnvSupport.Value();
                }
                """);

            Environment.SetEnvironmentVariable("STARK_PATH", packageDirectory);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--check"],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Check succeeded.", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());

            var disabledStdout = new StringWriter();
            var disabledStderr = new StringWriter();
            var disabledExitCode = await CompilerCli.RunAsync(
                [appPath, "--check", "--no-stark-path"],
                new StringReader(string.Empty),
                disabledStdout,
                disabledStderr);

            Assert.Equal(1, disabledExitCode);
            Assert.Equal(string.Empty, disabledStdout.ToString());
            Assert.Contains("Unable to resolve imported module 'EnvSupport'.", disabledStderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARK_PATH", originalStarkPath);

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
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg");

        try
        {
            await File.WriteAllTextAsync(
                dependencyPath,
                """
                module Math

                public finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                rootPath,
                """
                export import Math
                module Facade

                public finite law i32[min max] Double(i32[min max] value)
                {
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
            Assert.Contains("Emitted package image:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
            Assert.True(File.Exists(manifestPath));

            Assert.True(PackageImageLoader.TryLoadManifest(manifestPath, out var manifest));
            Assert.Equal("Facade", manifest.RootModule);
            Assert.Contains(
                manifest.Modules,
                module =>
                {
                    if (module.ModuleName != "Facade")
                    {
                        return false;
                    }

                    var sourceSurface = module.SourceSurface;
                    return sourceSurface?.ReExports?.Any(reExport => reExport.ModuleName == "Math") == true
                           && sourceSurface.Functions?.Any(function => function.SymbolName == "Facade.Double") == true;
                });
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
    public async Task EmitLibraryModeRebuildReplacesStaleArchiveMembers()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var archiverPath = FindFirstAvailableTool("llvm-ar", "ar");
        if (archiverPath is null)
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-lib-rebuild-");
        var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var dependencyPath = Path.Combine(tempDirectory.FullName, "Math.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "libFacade.a");

        try
        {
            await File.WriteAllTextAsync(
                dependencyPath,
                """
                module Math

                public finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                rootPath,
                """
                export import Math
                module Facade

                public finite law i32[min max] Double(i32[min max] value)
                {
                    return Math.Add(value, value);
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var firstExitCode = await CompilerCli.RunAsync(
                [rootPath, "--emit-lib", "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, firstExitCode);
            Assert.True(File.Exists(outputPath));

            await File.WriteAllTextAsync(
                rootPath,
                """
                module Facade

                public finite law i32[min max] Double(i32[min max] value)
                {
                    return value + value;
                }
                """);

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();

            var secondExitCode = await CompilerCli.RunAsync(
                [rootPath, "--emit-lib", "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, secondExitCode);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = archiverPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("t");
            startInfo.ArgumentList.Add(outputPath);

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);

            var members = await process!.StandardOutput.ReadToEndAsync();
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, errors);
            Assert.DoesNotContain("Math.o", members, StringComparison.Ordinal);
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
    public async Task EmitLibraryModeCompilesArchiveObjectsWithSectionGranularity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-lib-sections-");
        var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "libFacade.a");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        var archiverLogPath = Path.Combine(tempDirectory.FullName, "archiver.log");
        _ = await CreateUnixCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var archiverPath = await CreateUnixCaptureArchiverAsync(tempDirectory.FullName, archiverLogPath);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            await File.WriteAllTextAsync(
                rootPath,
                """
                module Facade

                public finite law i32[min max] Used()
                {
                    return 1;
                }

                public finite law i32[min max] Unused()
                {
                    return 2;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-lib",
                    "-o", outputPath,
                    "--archiver", archiverPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLog = await File.ReadAllTextAsync(clangLogPath);
            Assert.Contains("-ffunction-sections", clangLog);
            Assert.Contains("-fdata-sections", clangLog);
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
    public async Task EmitLibraryModeSuppressesRanlibEmptyObjectWarningsFromSuccessfulArchive()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-lib-ranlib-warning-");
        var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "libFacade.a");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        _ = await CreateUnixCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var archiverPath = await CreateUnixRanlibWarningArchiverAsync(tempDirectory.FullName);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            await File.WriteAllTextAsync(
                rootPath,
                """
                module Facade

                public finite law i32[min max] Used()
                {
                    return 1;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-lib",
                    "-o", outputPath,
                    "--archiver", archiverPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
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

                public struct Box
                {
                    i32[min max] Value;
                }

                public fn Box Make()
                {
                    return new Box()
                    {
                        Value = 7
                    };
                }

                public fn i32[min max] Read(Box box)
                {
                    return box.Value;
                }
                """);

            await File.WriteAllTextAsync(
                rootPath,
                """
                import Geometry
                module App

                export fn i32[min max] main()
                {
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
    public async Task EmitExecutableModeEnablesThinLtoForOptimizedBuildsWhenLldIsAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-exe-lto-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        _ = await CreateUnixCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var lldPath = Path.Combine(tempDirectory.FullName, "ld.lld");
        await File.WriteAllTextAsync(lldPath, "#!/usr/bin/env bash\nexit 0\n");
        System.Diagnostics.Process.Start("chmod", $"+x {lldPath}")!.WaitForExit();
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-o", outputPath,
                    "--target", "x86_64-unknown-linux-gnu"
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLog = await File.ReadAllTextAsync(clangLogPath);
            Assert.Contains("-flto=thin", clangLog, StringComparison.Ordinal);
            Assert.Contains("-O3", clangLog, StringComparison.Ordinal);
            Assert.Contains("-fuse-ld=lld", clangLog, StringComparison.Ordinal);
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
    public async Task EmitExecutableModeForwardsMacOSSdkRootToClangForDarwinTargets()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-exe-macos-sdk-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        var sdkRoot = Path.Combine(tempDirectory.FullName, "MacOSX.sdk");
        Directory.CreateDirectory(sdkRoot);
        _ = await CreateUnixCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalSdkRoot = Environment.GetEnvironmentVariable("SDKROOT");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            Environment.SetEnvironmentVariable("SDKROOT", sdkRoot);
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-o", outputPath,
                    "--target", "arm64-apple-darwin"
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLog = await File.ReadAllTextAsync(clangLogPath);
            Assert.Contains("-isysroot", clangLog, StringComparison.Ordinal);
            Assert.Contains(sdkRoot, clangLog, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("SDKROOT", originalSdkRoot);

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
    public async Task EmitExecutableModeAllowsSystemTextDependencyThinLto()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-exe-lto-system-text-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var saveTempsPath = Path.Combine(tempDirectory.FullName, "temps");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        var metricsPath = Path.Combine(tempDirectory.FullName, "toolchain.metrics");
        _ = await CreateUnixAppendCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var lldPath = Path.Combine(tempDirectory.FullName, "ld.lld");
        await File.WriteAllTextAsync(lldPath, "#!/usr/bin/env bash\nexit 0\n");
        System.Diagnostics.Process.Start("chmod", $"+x {lldPath}")!.WaitForExit();
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            await File.WriteAllTextAsync(
                rootPath,
                """
                import System
                import System.Text
                module App

                export fn i32[min max] main()
                {
                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> text = 0.ToAscii();
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-I", Path.Combine(repositoryRoot, "stdlib", "src"),
                    "-o", outputPath,
                    "--target", "x86_64-unknown-linux-gnu",
                    "--save-temps", saveTempsPath,
                    "--toolchain-metrics", metricsPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLogLines = await File.ReadAllLinesAsync(clangLogPath);
            Assert.Contains(
                clangLogLines,
                static line => line.Contains("root.ll", StringComparison.Ordinal)
                    && line.Contains("-flto=thin", StringComparison.Ordinal)
                    && !line.Contains("-disable-llvm-passes", StringComparison.Ordinal));
            Assert.Contains(
                clangLogLines,
                static line => line.Contains("System_Text.ll", StringComparison.Ordinal)
                    && line.Contains("-flto=thin", StringComparison.Ordinal)
                    && !line.Contains("-disable-llvm-passes", StringComparison.Ordinal));
            Assert.DoesNotContain(
                clangLogLines,
                static line => line.Contains("System_Memory.ll", StringComparison.Ordinal));

            var metrics = await File.ReadAllTextAsync(metricsPath);
            Assert.Contains("optimization_decision_count=", metrics, StringComparison.Ordinal);
            Assert.Contains("module=System.Text", metrics, StringComparison.Ordinal);
            Assert.Contains("thinlto=true", metrics, StringComparison.Ordinal);
            Assert.Contains("reason=thinlto-enabled-hot-inline-candidates", metrics, StringComparison.Ordinal);
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
    public async Task EmitExecutableModeAllowsSystemCollectionsDependencyThinLto()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-exe-lto-system-collections-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var saveTempsPath = Path.Combine(tempDirectory.FullName, "temps");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        _ = await CreateUnixAppendCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var lldPath = Path.Combine(tempDirectory.FullName, "ld.lld");
        await File.WriteAllTextAsync(lldPath, "#!/usr/bin/env bash\nexit 0\n");
        System.Diagnostics.Process.Start("chmod", $"+x {lldPath}")!.WaitForExit();
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            await File.WriteAllTextAsync(
                rootPath,
                """
                import System
                import System.Collections
                module App

                export fn i32[min max] main()
                {
                    stack mut List<u32[0 2 ** 31 - 1]> values = new();
                    values.Push(1);
                    return (i32[min max])values.Count();
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-I", Path.Combine(repositoryRoot, "stdlib", "src"),
                    "-o", outputPath,
                    "--target", "x86_64-unknown-linux-gnu",
                    "--save-temps", saveTempsPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLogLines = await File.ReadAllLinesAsync(clangLogPath);
            Assert.Contains(
                clangLogLines,
                static line => line.Contains("root.ll", StringComparison.Ordinal)
                    && line.Contains("-flto=thin", StringComparison.Ordinal)
                    && !line.Contains("-disable-llvm-passes", StringComparison.Ordinal));
            Assert.Contains(
                clangLogLines,
                static line => line.Contains("System_Collections.ll", StringComparison.Ordinal)
                    && line.Contains("-flto=thin", StringComparison.Ordinal)
                    && !line.Contains("-disable-llvm-passes", StringComparison.Ordinal));
            Assert.DoesNotContain(
                clangLogLines,
                static line => line.Contains("System_Memory.ll", StringComparison.Ordinal));
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
    public async Task EmitExecutableModeReportsMixedThinLtoDependencyPolicy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-exe-lto-mixed-policy-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var saveTempsPath = Path.Combine(tempDirectory.FullName, "temps");
        var metricsPath = Path.Combine(tempDirectory.FullName, "toolchain.metrics");
        var clangLogPath = Path.Combine(tempDirectory.FullName, "clang.log");
        _ = await CreateUnixAppendCaptureClangAsync(tempDirectory.FullName, clangLogPath);
        var lldPath = Path.Combine(tempDirectory.FullName, "ld.lld");
        await File.WriteAllTextAsync(lldPath, "#!/usr/bin/env bash\nexit 0\n");
        System.Diagnostics.Process.Start("chmod", $"+x {lldPath}")!.WaitForExit();
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{originalPath}");
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Opaque.stark"),
                """
                [Backend(Opaque)]
                module Opaque

                public fn i32[min max] Value()
                {
                    return 1;
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory.FullName, "Inlineable.stark"),
                """
                module Inlineable

                public finite law i32[min max] Value()
                {
                    return 2;
                }
                """);
            await File.WriteAllTextAsync(
                rootPath,
                """
                import Opaque
                import Inlineable
                module App

                export fn i32[min max] main()
                {
                    return Opaque.Value() + Inlineable.Value();
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-I", tempDirectory.FullName,
                    "-o", outputPath,
                    "--target", "x86_64-unknown-linux-gnu",
                    "--save-temps", saveTempsPath,
                    "--toolchain-metrics", metricsPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());

            var clangLogLines = await File.ReadAllLinesAsync(clangLogPath);
            Assert.Contains(
                clangLogLines,
                static line => line.Contains("root.ll", StringComparison.Ordinal)
                    && line.Contains("-flto=thin", StringComparison.Ordinal));
            Assert.Contains(
                clangLogLines,
                static line => line.Contains("Inlineable.ll", StringComparison.Ordinal)
                    && line.Contains("-flto=thin", StringComparison.Ordinal));
            Assert.Contains(
                clangLogLines,
                static line => line.Contains("Opaque.ll", StringComparison.Ordinal)
                    && !line.Contains("-flto=thin", StringComparison.Ordinal));

            var metrics = await File.ReadAllTextAsync(metricsPath);
            Assert.Contains("module=Inlineable,thinlto=true", metrics, StringComparison.Ordinal);
            Assert.Contains("reason=thinlto-enabled-hot-inline-candidates", metrics, StringComparison.Ordinal);
            Assert.Contains("module=Opaque,thinlto=false", metrics, StringComparison.Ordinal);
            Assert.Contains("reason=backend-opaque", metrics, StringComparison.Ordinal);
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
        var buildTempsPath = Path.Combine(packageDirectory, "temps");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public finite law i32[min max] Double(i32[min max] value)
                {
                    return Math.Add(value, value);
                }
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [facadePath, "--emit-lib", "-o", libraryPath, "--save-temps", buildTempsPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Equal(string.Empty, buildStderr.ToString());
            if (NativeToolchain.SupportsExecutableThinLto())
            {
                var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";
                Assert.True(await IsLlvmBitcodeFileAsync(Path.Combine(buildTempsPath, $"root{objectExtension}")));
                Assert.True(await IsLlvmBitcodeFileAsync(Path.Combine(buildTempsPath, $"Math{objectExtension}")));
            }

            File.Delete(facadePath);
            File.Delete(mathPath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                export fn i32[min max] main()
                {
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

    [Fact]
    public async Task EmitExecutableModeLinksManifestBackedOverloadedLibrariesWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-overload-manifest-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var facadePath = Path.Combine(packageDirectory, "Facade.stark");
        var mathPath = Path.Combine(packageDirectory, "Math.stark");
        var textPath = Path.Combine(packageDirectory, "Text.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                textPath,
                """
                module Text

                public enum Encoding
                {
                    Binary,
                    UTF8,
                }
                """);

            await File.WriteAllTextAsync(
                mathPath,
                """
                import Text
                module Math

                public enum FileMode
                {
                    Read,
                    Write,
                }

                public finite law i32[min max] Open(ascii path, FileMode mode)
                {
                    return 4;
                }

                public finite law i32[min max] Open(ascii path, FileMode mode, Text.Encoding encoding)
                {
                    return 11;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Text
                export import Math
                module Facade

                public finite law i32[min max] Run()
                {
                    return Math.Open("demo.txt", Math.FileMode.Write)
                        + Math.Open("demo.txt", Math.FileMode.Write, Text.Encoding.UTF8);
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
            File.Delete(textPath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Facade
                module App

                export fn i32[min max] main()
                {
                    return Math.Open("demo.txt", Math.FileMode.Write)
                        + Math.Open("demo.txt", Math.FileMode.Write, Text.Encoding.UTF8);
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
            Assert.Equal(15, process.ExitCode);
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
    public async Task EmitExecutableModeLinksManifestBackedAsmLibrariesWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-asm-manifest-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var syscallPath = Path.Combine(packageDirectory, "Syscall.stark");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var libraryPath = Path.Combine(packageDirectory, "libSyscall.a");
        var manifestPath = Path.Combine(packageDirectory, "libSyscall.starkpkg");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            await File.WriteAllTextAsync(
                syscallPath,
                """
                module Syscall

                public unsafe ffi asm(x86_64) fn i64[min max] Syscall0(i64[min max] number)
                    in("rax") number,
                    out("rax") return,
                    clobber("rcx", "r11")
                {
                    "syscall"
                }
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [syscallPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Equal(string.Empty, buildStderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            {
                Assert.True(PackageImageLoader.TryLoadManifest(manifestPath, out var manifest));
                var syscallModule = Assert.Single(
                    manifest.Modules,
                    static module => module.ModuleName == "Syscall");
                var syscallFunctions = syscallModule.Functions.Count != 0
                    ? syscallModule.Functions
                    : syscallModule.SourceSurface?.Functions ?? [];
                var syscallFunction = syscallFunctions
                    .Single(static function => function.Name == "Syscall0");
                Assert.NotNull(syscallFunction.Asm);
                Assert.Equal("x86_64", syscallFunction.Asm!.ArchitectureText);
                Assert.Equal("syscall", syscallFunction.Asm.TemplateText);
            }

            File.Delete(syscallPath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Syscall
                module App

                export unsafe fn i32[min max] main()
                {
                    if (Syscall.Syscall0(39) <= 0)
                    {
                        return 1;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
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
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
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
    public async Task EmitExecutableModeSupportsCustomLinkerLinkArgsAndSavedTemps()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-linker-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var librarySearchPath = Path.Combine(tempDirectory.FullName, "native-libs");
        var tempsPath = Path.Combine(tempDirectory.FullName, "temps");
        var metricsPath = Path.Combine(tempDirectory.FullName, "toolchain.metrics");
        Directory.CreateDirectory(librarySearchPath);

        var linkerLogPath = Path.Combine(tempDirectory.FullName, "linker.log");
        var linkerPath = await CreateUnixCaptureLinkerAsync(tempDirectory.FullName, linkerLogPath);

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export fn i32[min max] main()
                {
                    return 7;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-o", outputPath,
                    "--linker", linkerPath,
                    "-L", librarySearchPath,
                    "--link-arg=-Wl,--gc-sections",
                    "--save-temps", tempsPath,
                    "--toolchain-metrics", metricsPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.True(File.Exists(Path.Combine(tempsPath, "root.ll")));
            Assert.True(File.Exists(Path.Combine(tempsPath, OperatingSystem.IsWindows() ? "root.obj" : "root.o")));
            Assert.True(File.Exists(metricsPath));

            var linkerLog = await File.ReadAllTextAsync(linkerLogPath);
            Assert.Contains("-L", linkerLog);
            Assert.Contains(Path.GetFullPath(librarySearchPath), linkerLog);
            Assert.Contains("-Wl,--gc-sections", linkerLog);

            var metrics = await File.ReadAllTextAsync(metricsPath);
            Assert.Contains("timing_unit=microseconds", metrics);
            Assert.Contains("llvm_object_us=", metrics);
            Assert.Contains("link_us=", metrics);
            Assert.Contains("toolchain_us=", metrics);
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
    public async Task EmitExecutableModeReportsFriendlyMissingNativeLibraryDiagnostic()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-missing-native-lib-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var linkerPath = await CreateUnixMissingLibraryLinkerAsync(tempDirectory.FullName, "MissingNative");

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export fn i32[min max] main()
                {
                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-o", outputPath,
                    "--linker", linkerPath,
                    "--native-library", "MissingNative"
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            var errorText = stderr.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("error STK7204 [native-link]", errorText, StringComparison.Ordinal);
            Assert.Contains("Native library 'MissingNative' could not be found while linking.", errorText, StringComparison.Ordinal);
            Assert.Contains("--native-library-dir", errorText, StringComparison.Ordinal);
            Assert.Contains("cannot find -lMissingNative", errorText, StringComparison.Ordinal);
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
    public async Task EmitExecutableModeForwardsRelocationModelToLinker()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-linker-reloc-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "App");
        var linkerLogPath = Path.Combine(tempDirectory.FullName, "linker.log");
        var linkerPath = await CreateUnixCaptureLinkerAsync(tempDirectory.FullName, linkerLogPath);

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                fn i32[min max] Run()
                {
                    return 7;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--emit-exe",
                    "-o", outputPath,
                    "--target", "x86_64-unknown-linux-gnu",
                    "--relocation-model", "pie",
                    "--linker", linkerPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var linkerLog = await File.ReadAllTextAsync(linkerLogPath);
            Assert.Contains("-target", linkerLog, StringComparison.Ordinal);
            Assert.Contains("x86_64-unknown-linux-gnu", linkerLog, StringComparison.Ordinal);
            Assert.Contains("-pie", linkerLog, StringComparison.Ordinal);
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
    public async Task LinkOnlyAliasSupportsCustomLinkerLinkArgsAndSavedTemps()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-linkonly-");
        var rootPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");
        var librarySearchPath = Path.Combine(tempDirectory.FullName, "native-libs");
        var tempsPath = Path.Combine(tempDirectory.FullName, "temps");
        Directory.CreateDirectory(librarySearchPath);

        var linkerLogPath = Path.Combine(tempDirectory.FullName, "linker.log");
        var linkerPath = await CreateUnixCaptureLinkerAsync(tempDirectory.FullName, linkerLogPath);

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export fn i32[min max] main()
                {
                    return 7;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [
                    rootPath,
                    "--link-only",
                    "-o", outputPath,
                    "--linker", linkerPath,
                    "-L", librarySearchPath,
                    "--link-arg=-Wl,--gc-sections",
                    "--save-temps", tempsPath
                ],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.True(File.Exists(Path.Combine(tempsPath, "root.ll")));
            Assert.True(File.Exists(Path.Combine(tempsPath, OperatingSystem.IsWindows() ? "root.obj" : "root.o")));

            var linkerLog = await File.ReadAllTextAsync(linkerLogPath);
            Assert.Contains("-L", linkerLog);
            Assert.Contains(Path.GetFullPath(librarySearchPath), linkerLog);
            Assert.Contains("-Wl,--gc-sections", linkerLog);
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
    public async Task CreateStaticLibraryReplacesExistingArchiveOnlyAfterArchiverSucceeds()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-atomic-archive-");
        var objectPath = Path.Combine(tempDirectory.FullName, "Facade.o");
        var outputPath = Path.Combine(tempDirectory.FullName, "libFacade.a");
        var failingArchiverPath = Path.Combine(tempDirectory.FullName, "failing-archiver.sh");
        var successfulArchiverPath = Path.Combine(tempDirectory.FullName, "successful-archiver.sh");

        try
        {
            await File.WriteAllTextAsync(objectPath, "object");
            await File.WriteAllTextAsync(outputPath, "known-good archive");
            await File.WriteAllTextAsync(
                failingArchiverPath,
                """
                #!/usr/bin/env bash
                set -euo pipefail
                out="${2:-}"
                printf '%s' 'partial replacement' > "$out"
                printf '%s\n' 'archiver failed after writing staging output' >&2
                exit 1
                """);
            await File.WriteAllTextAsync(
                successfulArchiverPath,
                """
                #!/usr/bin/env bash
                set -euo pipefail
                out="${2:-}"
                printf '%s' 'complete replacement' > "$out"
                """);
            System.Diagnostics.Process.Start("chmod", $"+x {failingArchiverPath}")!.WaitForExit();
            System.Diagnostics.Process.Start("chmod", $"+x {successfulArchiverPath}")!.WaitForExit();

            var failed = NativeToolchain.CreateStaticLibrary(
                [objectPath],
                outputPath,
                failingArchiverPath);

            Assert.False(failed.Succeeded);
            Assert.Equal(Path.GetFullPath(outputPath), failed.OutputPath);
            Assert.Contains("archiver failed", failed.StandardError, StringComparison.Ordinal);
            Assert.Equal("known-good archive", await File.ReadAllTextAsync(outputPath));
            Assert.Empty(Directory.EnumerateFiles(
                tempDirectory.FullName,
                $".*.{Path.GetFileName(outputPath)}"));

            var succeeded = NativeToolchain.CreateStaticLibrary(
                [objectPath],
                outputPath,
                successfulArchiverPath);

            Assert.True(succeeded.Succeeded);
            Assert.Equal(Path.GetFullPath(outputPath), succeeded.OutputPath);
            Assert.Equal("complete replacement", await File.ReadAllTextAsync(outputPath));
            Assert.Empty(Directory.EnumerateFiles(
                tempDirectory.FullName,
                $".*.{Path.GetFileName(outputPath)}"));
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
    public async Task EmitLibraryModeSupportsCustomArchiverTool()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-archiver-");
        var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "libFacade.a");
        var archiverLogPath = Path.Combine(tempDirectory.FullName, "archiver.log");
        var archiverPath = await CreateUnixCaptureArchiverAsync(tempDirectory.FullName, archiverLogPath);

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module Facade

                public finite law i32[min max] Double(i32[min max] value)
                {
                    return value + value;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [rootPath, "--emit-lib", "-o", outputPath, "--archiver", archiverPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var archiverLog = await File.ReadAllTextAsync(archiverLogPath);
            var archiverArguments = archiverLog.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.True(archiverArguments.Length >= 3);
            Assert.Equal("rcs", archiverArguments[0]);

            // The archiver must populate a same-directory staging archive so a
            // successful invocation can replace the requested output atomically.
            var stagingArchivePath = archiverArguments[1];
            Assert.NotEqual(Path.GetFullPath(outputPath), stagingArchivePath);
            Assert.Equal(tempDirectory.FullName, Path.GetDirectoryName(stagingArchivePath));
            Assert.Matches(
                $@"^\.[0-9a-f]{{32}}\.{System.Text.RegularExpressions.Regex.Escape(Path.GetFileName(outputPath))}$",
                Path.GetFileName(stagingArchivePath));
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

    private static async Task<string> CreateUnixMissingLibraryLinkerAsync(string directory, string libraryName)
    {
        var path = Path.Combine(directory, "missing-library-linker.sh");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf 'ld: error: cannot find -l{{libraryName}}\n' >&2
            exit 1
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
    }

    private static async Task<string> CreateUnixCaptureClangAsync(string directory, string logPath)
    {
        var path = Path.Combine(directory, "clang");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$@" > "{{logPath}}"
            out=""
            emit_llvm=false
            prev=""
            for arg in "$@"; do
              if [ "$arg" = "-emit-llvm" ]; then
                emit_llvm=true
              fi
              if [ "$prev" = "-o" ]; then
                out="$arg"
                break
              fi
              prev="$arg"
            done
            if [ "$emit_llvm" = true ] && [ "$out" = "-" ]; then
              printf '%s\n' 'target datalayout = "e-p:64:64"' 'target triple = "x86_64-unknown-linux-gnu"'
              exit 0
            fi
            if [ -n "$out" ] && [ "$out" != "-" ]; then
              : > "$out"
            fi
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
    }

    private static async Task<string> CreateUnixAppendCaptureClangAsync(string directory, string logPath)
    {
        var path = Path.Combine(directory, "clang");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> "{{logPath}}"
            out=""
            emit_llvm=false
            prev=""
            for arg in "$@"; do
              if [ "$arg" = "-emit-llvm" ]; then
                emit_llvm=true
              fi
              if [ "$prev" = "-o" ]; then
                out="$arg"
                break
              fi
              prev="$arg"
            done
            if [ "$emit_llvm" = true ] && [ "$out" = "-" ]; then
              printf '%s\n' 'target datalayout = "e-p:64:64"' 'target triple = "x86_64-unknown-linux-gnu"'
              exit 0
            fi
            if [ -n "$out" ] && [ "$out" != "-" ]; then
              : > "$out"
            fi
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
    }

    private static async Task<string> CreateUnixCaptureArchiverAsync(string directory, string logPath)
    {
        var path = Path.Combine(directory, "capture-archiver.sh");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$@" > "{{logPath}}"
            out="${2:-}"
            : > "$out"
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
    }

    private static async Task<string> CreateUnixRanlibWarningArchiverAsync(string directory)
    {
        var path = Path.Combine(directory, "ranlib-warning-archiver.sh");
        await File.WriteAllTextAsync(
            path,
            """
            #!/usr/bin/env bash
            set -euo pipefail
            out="${2:-}"
            printf "ranlib: warning: '%s(Facade.o)' has no symbols\n" "$out" >&2
            : > "$out"
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
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

        throw new InvalidOperationException("Unable to locate the Stark repository root.");
    }

    private static string CreateFakeToolchain(string parentDirectory, string name)
    {
        var root = Path.Combine(parentDirectory, name);
        var bin = Path.Combine(root, "bin");
        var lib = Path.Combine(root, "lib");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(lib);

        foreach (var toolName in new[]
                 {
                     "clang",
                     OperatingSystem.IsWindows() ? "lld-link" : "ld.lld",
                     OperatingSystem.IsWindows() ? "llvm-lib" : "llvm-ar",
                     "pkg-config",
                     "xcrun"
                 })
        {
            File.WriteAllText(Path.Combine(bin, ToolFileName(toolName)), string.Empty);
        }

        File.WriteAllText(Path.Combine(lib, LlvmLibraryFileName()), string.Empty);
        return root;
    }

    private static string ToolFileName(string name)
        => OperatingSystem.IsWindows() && !Path.HasExtension(name) ? $"{name}.exe" : name;

    private static string LlvmLibraryFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "LLVM-C.lib";
        }

        return OperatingSystem.IsMacOS() ? "libLLVM.dylib" : "libLLVM.so";
    }

    private static void Cleanup(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static async Task<bool> IsLlvmBitcodeFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length < 4)
        {
            return false;
        }

        // Raw bitcode ('BC' 0xC0DE) on ELF/COFF targets; Darwin emits the bitcode
        // wrapper header (0x0B17C0DE little-endian) around the same payload.
        return (bytes[0] == (byte)'B'
                && bytes[1] == (byte)'C'
                && bytes[2] == 0xC0
                && bytes[3] == 0xDE)
            || (bytes[0] == 0xDE
                && bytes[1] == 0xC0
                && bytes[2] == 0x17
                && bytes[3] == 0x0B);
    }

    private static string? FindFirstAvailableTool(params string[] toolNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var toolName in toolNames)
            {
                var candidate = Path.Combine(directory, toolName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
