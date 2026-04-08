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

                fn i32 Run() {
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
        Assert.Contains("--target-cpu <cpu>", text);
        Assert.Contains("--target-feature <feature>", text);
        Assert.Contains("--relocation-model <default|static|pic|pie>", text);
        Assert.Contains("--code-model <tiny|small|kernel|medium|large>", text);
        Assert.Contains("-O0|-O1|-O2|-O3", text);
        Assert.Contains("--optimize <0|1|2|3>", text);
        Assert.Contains("--link-arg <arg>", text);
        Assert.Contains("--save-temps <dir>", text);
        Assert.Contains("--diagnostic-format <text|json>", text);
        Assert.Contains("--log-level <info|warning|error>     Set the minimum compiler log severity printed to stderr (default: warning)", text);
        Assert.Contains("--log-verbosity <normal|verbose>", text);
        Assert.Contains("--log-category <name>", text);
        Assert.Contains("--log-stage <pass-id>", text);
        Assert.Contains("--log-kind <pipeline|symbol|decision|gap>", text);
        Assert.Contains("(default)      Run the full compilation pipeline and print a pass summary", text);
        Assert.Contains("With no workflow flag, the compiler runs the full pipeline and prints a success summary.", text);
        Assert.Contains("Examples:", text);
        Assert.Contains("compiler app.stark --emit-llvm -o app.ll", text);
        Assert.Contains("compiler app.stark --diagnostic-format json", text);
        Assert.Contains("--compile-only", text);
        Assert.Contains("--link-only", text);
        Assert.Equal(string.Empty, stderr.ToString());
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

                fn bool Run() {
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

                fn bool Run() {
                    return 1;
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("error", text, StringComparison.Ordinal);
        Assert.Contains("4 |     return 1;", text, StringComparison.Ordinal);
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

                fn ascii Run(ascii text, i32 first, i32 second, i32 third) {
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
        Assert.Contains("4 |     return text[", text, StringComparison.Ordinal);
        Assert.Contains("5 |         first,", text, StringComparison.Ordinal);
        Assert.Contains("6 |         second,", text, StringComparison.Ordinal);
        Assert.Contains("7 |         third", text, StringComparison.Ordinal);
        Assert.Contains("8 |     ];", text, StringComparison.Ordinal);
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

                fn i32 Run(bool value) {
                    switch (value) {
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
        Assert.Contains("7 |         case false:", text, StringComparison.Ordinal);
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

                fn i32 Run(bool flag) {
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

                fn i32 Run(bool left, bool right) {
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

                fn i32 Run() {
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

                fn i32 Run() {
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

                fn i32 Run() {
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

                struct Buffer {
                    i32 Value;

                    mut drop {
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

                struct Box {
                    i32 Value;
                }

                fn void Consume(Box value) {
                    return;
                }

                fn i32 Run() {
                    stack Box box = new Box() { Value = 1 };
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
        Assert.Contains("13 |     Consume(box);", text, StringComparison.Ordinal);
        Assert.Contains("14 |     return box.Value;", text, StringComparison.Ordinal);
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

                struct Box {
                    i32 Value;
                }

                fn void Consume(Box value) {
                    return;
                }

                fn i32 Run() {
                    stack Box box = new Box() { Value = 1 };
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
                "note [ownership-validate] at 13:13: Value 'box' was moved here.",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("Failure summary: 1 error, 0 warnings, 1 info.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogFiltersRenderHumanReadableStructuredWarnings()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--emit-mir", "--log-level", "warning", "--log-kind", "gap", "--log-category", "lowering", "--log-stage", "lower-mir"],
            new StringReader(
                """
                module Demo

                fn void A() {
                    return;
                }

                fn bool Run(bool flag) {
                    return (flag ? A() : A()) == (flag ? A() : A());
                }
                """),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("mir module Demo", stdout.ToString());

        var text = stderr.ToString();
        Assert.Contains("Direct MIR lowering stopped in 'LowerPostfixExpression'.", text, StringComparison.Ordinal);
        Assert.Contains("[warn gap lowering stage=lower-mir symbol=Run", text, StringComparison.Ordinal);
        Assert.Contains("op=LowerPostfixExpression", text, StringComparison.Ordinal);
        Assert.Contains("outcome=unsupported", text, StringComparison.Ordinal);
        Assert.Contains("feature=lower-postfix-expression", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Starting pass", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitLlvmModeFailsWithStableUnsupportedLoweringDiagnostic()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--emit-llvm"],
            new StringReader(
                """
                module Demo

                fn void A() {
                    return;
                }

                fn bool Run(bool flag) {
                    return (flag ? A() : A()) == (flag ? A() : A());
                }
                """),
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        var text = stderr.ToString();
        Assert.Contains("error STK5000 [lower-mir]", text, StringComparison.Ordinal);
        Assert.Contains("Code generation does not yet support this construct (lower-postfix-expression).", text, StringComparison.Ordinal);
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

                    fn i32 Run() {
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

                    fn i32 Run() {
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

                    fn i32 Run() {
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

                    fn i32 Run() {
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

                    fn i32 Run() {
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
    public async Task EmitObjectModeForwardsOptimizationLevelToClang()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-cli-opt-level-");
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
                    "-O0"
                ],
                new StringReader(
                    """
                    module Demo

                    fn i32 Run(bool flag) {
                        stack mut i32 value = 0;
                        if (flag) {
                            value = 1;
                        } else {
                            value = 2;
                        }

                        return value;
                    }
                    """),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted object file:", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var clangLog = await File.ReadAllTextAsync(clangLogPath);
            Assert.Contains("-O0", clangLog, StringComparison.Ordinal);
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

                public finite law i32 Double(i32 value) {
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

                public enum Encoding {
                    Binary,
                    UTF8,
                }
                """);

            await File.WriteAllTextAsync(
                mathPath,
                """
                import Text
                module Math

                public enum FileMode {
                    Read,
                    Write,
                }

                public finite law i32 Open(ascii path, FileMode mode) {
                    return 4;
                }

                public finite law i32 Open(ascii path, FileMode mode, Text.Encoding encoding) {
                    return 11;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Text
                export import Math
                module Facade

                public finite law i32 Run() {
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

                export ffi fn i32 main() {
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
        var manifestPath = Path.Combine(packageDirectory, "libSyscall.starkpkg.json");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            await File.WriteAllTextAsync(
                syscallPath,
                """
                module Syscall

                public ffi asm(x86_64) fn i64 Syscall0(i64 number)
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

            using (var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
            {
                var syscallModule = manifest.RootElement.GetProperty("Modules")
                    .EnumerateArray()
                    .Single(module => module.GetProperty("ModuleName").GetString() == "Syscall");
                var syscallFunction = syscallModule.GetProperty("Functions")
                    .EnumerateArray()
                    .Single(function => function.GetProperty("Name").GetString() == "Syscall0");
                Assert.True(syscallFunction.TryGetProperty("Asm", out var asm));
                Assert.Equal("x86_64", asm.GetProperty("ArchitectureText").GetString());
                Assert.Equal("syscall", asm.GetProperty("TemplateText").GetString());
            }

            File.Delete(syscallPath);

            await File.WriteAllTextAsync(
                appPath,
                """
                import Syscall
                module App

                export ffi fn i32 main() {
                    if (Syscall.Syscall0(39) <= 0) {
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
        Directory.CreateDirectory(librarySearchPath);

        var linkerLogPath = Path.Combine(tempDirectory.FullName, "linker.log");
        var linkerPath = await CreateUnixCaptureLinkerAsync(tempDirectory.FullName, linkerLogPath);

        try
        {
            await File.WriteAllTextAsync(
                rootPath,
                """
                module App

                export ffi fn i32 main() {
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

                fn i32 Run() {
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

                export ffi fn i32 main() {
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

                public finite law i32 Double(i32 value) {
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
            Assert.Contains("rcs", archiverLog);
            Assert.Contains(Path.GetFullPath(outputPath), archiverLog);
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
            prev=""
            for arg in "$@"; do
              if [ "$prev" = "-o" ]; then
                out="$arg"
                break
              fi
              prev="$arg"
            done
            if [ -n "$out" ]; then
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
