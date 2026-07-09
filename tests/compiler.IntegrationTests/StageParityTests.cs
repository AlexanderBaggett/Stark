using Stark.Compiler;

namespace compiler.IntegrationTests;

/// <summary>
/// Stage0/stage1 differential harness: every corpus file under
/// <c>tests-stark/corpus/</c> is compiled and RUN by the host compiler
/// (stage0) and lowered by the self-hosted compiler (stage1, via the
/// <c>selfhost/tools/DifferentialDriver</c> project) whose emitted module is
/// built with clang and run. The exit codes must match — execution parity is
/// the gate; the normalized per-function skeleton diff
/// (<see cref="LlvmTextNormalizer"/>) is attached on failure for
/// investigation. The corpus is the growth surface: every new stage1
/// lowering family should land with corpus files.
///
/// The stage1 driver binary must be built first (one-time selfhost package
/// build, ~20 min cold):
///   cd selfhost/tools/DifferentialDriver &amp;&amp; stark build
/// Without the binary these tests pass as skipped unless
/// STARK_STAGE_PARITY=1, which turns a missing driver into a failure (CI).
/// </summary>
public sealed class StageParityTests
{
    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        var corpusDirectory = FindRepositoryPath("tests-stark/corpus");
        if (corpusDirectory is not null)
        {
            foreach (var path in Directory.EnumerateFiles(corpusDirectory, "*.stark").OrderBy(static path => path, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }
        }

        if (data.Count == 0)
        {
            data.Add("<corpus-missing>");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public async Task CorpusFileHasStageParity(string corpusFileName)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        Assert.NotEqual("<corpus-missing>", corpusFileName);
        var corpusPath = Path.Combine(FindRepositoryPath("tests-stark/corpus")!, corpusFileName);

        var driverPath = FindDriverBinary();
        if (driverPath is null)
        {
            Assert.False(
                Environment.GetEnvironmentVariable("STARK_STAGE_PARITY") == "1",
                "STARK_STAGE_PARITY=1 but the stage1 driver is not built; run `stark build` in selfhost/tools/DifferentialDriver");
            return;
        }

        var corpusText = await File.ReadAllTextAsync(corpusPath);
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stage-parity-");

        try
        {
            // Stage0: host compiler end-to-end.
            var stage0Source = $"module Corpus\n\n{corpusText}";
            var stage0SourcePath = Path.Combine(tempDirectory.FullName, "Corpus.stark");
            await File.WriteAllTextAsync(stage0SourcePath, stage0Source);

            var stage0Llvm = await EmitStage0LlvmAsync(stage0SourcePath);
            var stage0ExePath = Path.Combine(tempDirectory.FullName, "stage0-app");
            await RunCliAsync([stage0SourcePath, "--emit-exe", "-o", stage0ExePath], expectSuccess: true);
            var stage0Exit = await RunProcessAsync(stage0ExePath, []);

            // Stage1: self-hosted lowering, then clang on its module text.
            var (driverExit, driverStdout, driverStderr) = await RunProcessWithOutputAsync(driverPath, [corpusPath]);
            Assert.True(
                driverExit == 0,
                $"stage1 rejected corpus file '{corpusFileName}' (exit {driverExit}):\n{driverStdout}\n{driverStderr}");

            var stage1LlvmPath = Path.Combine(tempDirectory.FullName, "stage1.ll");
            await File.WriteAllTextAsync(stage1LlvmPath, WrapStage1EntryPoint(driverStdout, corpusFileName));
            var stage1ExePath = Path.Combine(tempDirectory.FullName, "stage1-app");
            var (clangExit, _, clangStderr) = await RunProcessWithOutputAsync(
                "clang",
                OperatingSystem.IsMacOS() && await ResolveMacSdkPathAsync() is { } sdkPath
                    ? [stage1LlvmPath, "-isysroot", sdkPath, "-o", stage1ExePath]
                    : [stage1LlvmPath, "-o", stage1ExePath]);
            Assert.True(clangExit == 0, $"clang failed on stage1 module for '{corpusFileName}':\n{clangStderr}\n--- stage1 module\n{driverStdout}");
            var stage1Exit = await RunProcessAsync(stage1ExePath, []);

            Assert.True(
                stage0Exit == stage1Exit,
                $"stage parity failure for '{corpusFileName}': stage0 exited {stage0Exit}, stage1 exited {stage1Exit}\n"
                + $"--- normalized skeleton diff\n{LlvmTextNormalizer.DiffModules(stage0Llvm, driverStdout)}");
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

    /// <summary>
    /// Stage1 names functions ordinally (@f1, @f2, ...) with no @main; the
    /// corpus convention keeps `main` as the file's last function, so the
    /// last zero-parameter define is the entry point and gets a thin @main
    /// wrapper for the clang link.
    /// </summary>
    private static string WrapStage1EntryPoint(string stage1ModuleText, string corpusFileName)
    {
        var entry = System.Text.RegularExpressions.Regex
            .Matches(stage1ModuleText, @"define (?:internal )?(?:dso_local )?(i32|i64) @([A-Za-z0-9_]+)\(\)")
            .LastOrDefault();
        Assert.True(entry is not null, $"no zero-parameter entry define found in stage1 module for '{corpusFileName}':\n{stage1ModuleText}");

        var returnType = entry!.Groups[1].Value;
        var entryName = entry.Groups[2].Value;
        return stage1ModuleText
            + $"\ndefine {returnType} @main() {{\nentry:\n  %r = call {returnType} @{entryName}()\n  ret {returnType} %r\n}}\n";
    }

    private static string? _macSdkPath;

    private static async Task<string?> ResolveMacSdkPathAsync()
    {
        if (_macSdkPath is not null)
        {
            return _macSdkPath.Length == 0 ? null : _macSdkPath;
        }

        var (exitCode, stdout, _) = await RunProcessWithOutputAsync("xcrun", ["--show-sdk-path"]);
        _macSdkPath = exitCode == 0 ? stdout.Trim() : string.Empty;
        return _macSdkPath.Length == 0 ? null : _macSdkPath;
    }

    private static async Task<string> EmitStage0LlvmAsync(string sourcePath)
    {
        var llvmPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "stage0.ll");
        await RunCliAsync([sourcePath, "--emit-llvm", "-o", llvmPath], expectSuccess: true);
        return await File.ReadAllTextAsync(llvmPath);
    }

    private static async Task RunCliAsync(string[] args, bool expectSuccess)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CompilerCli.RunAsync(args, new StringReader(string.Empty), stdout, stderr);
        if (expectSuccess)
        {
            Assert.True(exitCode == 0, $"stage0 CLI failed ({string.Join(' ', args)}):\n{stderr}");
        }
    }

    private static async Task<int> RunProcessAsync(string fileName, string[] arguments)
    {
        var (exitCode, _, _) = await RunProcessWithOutputAsync(fileName, arguments);
        return exitCode;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessWithOutputAsync(string fileName, string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string? FindDriverBinary()
    {
        var projectDirectory = FindRepositoryPath("selfhost/tools/DifferentialDriver");
        if (projectDirectory is null)
        {
            return null;
        }

        var buildDirectory = Path.Combine(projectDirectory, "build");
        if (!Directory.Exists(buildDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(buildDirectory, "differential-driver*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path) is "" or ".exe")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? FindRepositoryPath(string relativePath)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
