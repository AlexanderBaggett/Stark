using System.Diagnostics;

namespace compiler.Tests;

public sealed class BenchmarkRegressionScriptTests
{
    [Fact]
    public async Task CheckBenchmarkRegressionsPassesWithinConfiguredBaselineThreshold()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var files = new TemporaryBenchmarkFiles();
        var baseline = files.WriteCsv(
            "baseline.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,900,1000,1100");
        var current = files.WriteCsv(
            "current.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,900,1075,1150");

        var result = await RunRegressionCheckerAsync(
            repositoryRoot,
            current,
            baseline,
            new Dictionary<string, string?>
            {
                ["STARK_BENCH_MAX_REGRESSION_PCT"] = "10",
                ["STARK_BENCH_MIN_REGRESSION_DELTA"] = "50"
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Baseline regression check passed", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckBenchmarkRegressionsFailsWhenBaselineRuntimeRegressesPastThreshold()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var files = new TemporaryBenchmarkFiles();
        var baseline = files.WriteCsv(
            "baseline.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,900,1000,1100");
        var current = files.WriteCsv(
            "current.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,1100,1250,1300");

        var result = await RunRegressionCheckerAsync(
            repositoryRoot,
            current,
            baseline,
            new Dictionary<string, string?>
            {
                ["STARK_BENCH_MAX_REGRESSION_PCT"] = "10",
                ["STARK_BENCH_MIN_REGRESSION_DELTA"] = "50"
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("REGRESSION", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckBenchmarkRegressionsFailsWhenStarkToRustRatioExceedsThreshold()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var files = new TemporaryBenchmarkFiles();
        var current = files.WriteCsv(
            "current.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,1800,2100,2300",
            "benchmarks/micro/StackScalarLoadForwarding,rust,50,1000,0,0,0,10000,900,1000,1100");

        var result = await RunRegressionCheckerAsync(
            repositoryRoot,
            current,
            baselinePath: null,
            new Dictionary<string, string?>
            {
                ["STARK_BENCH_MAX_STARK_TO_RUST_RATIO"] = "1.5",
                ["STARK_BENCH_MIN_REGRESSION_DELTA"] = "50"
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("RATIO", result.StandardError, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunRegressionCheckerAsync(
        string repositoryRoot,
        string currentPath,
        string? baselinePath,
        IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "check-benchmark-regressions.sh"));
        startInfo.ArgumentList.Add(currentPath);
        if (baselinePath is not null)
        {
            startInfo.ArgumentList.Add(baselinePath);
        }

        foreach (var item in environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start benchmark regression checker.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for benchmark regression script tests.");
    }

    private sealed class TemporaryBenchmarkFiles : IDisposable
    {
        private const string Header =
            "benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,avg_us,max_us";

        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"stark-benchmark-regression-tests-{Guid.NewGuid():N}");

        public TemporaryBenchmarkFiles()
        {
            Directory.CreateDirectory(_directory);
        }

        public string WriteCsv(string fileName, params string[] rows)
        {
            var path = Path.Combine(_directory, fileName);
            File.WriteAllLines(path, rows.Prepend(Header));
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
