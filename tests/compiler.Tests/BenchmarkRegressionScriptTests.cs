using System.Diagnostics;

namespace compiler.Tests;

public sealed class BenchmarkRegressionScriptTests
{
    [Fact]
    public async Task AddBenchmarkCRatiosUsesAverageRuntimeByBenchmark()
    {
        if (!IsCommandAvailable("bash"))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        using var files = new TemporaryBenchmarkFiles();
        var current = files.WriteCsv(
            "current.csv",
            "benchmarks/micro/Calls,c,50,1000,0,0,0,10000,900,1000,1100,20.000000,2048",
            "benchmarks/micro/Calls,stark,50,1000,200,100,300,10000,700,800,900,25.000000,4096",
            "benchmarks/micro/Calls,zig,50,1000,200,100,300,10000,1200,1250,1300,8.000000,4096",
            "benchmarks/micro/Calls,rust,50,1000,0,0,0,10000,1100,1200,1300,16.666667,3072",
            "benchmarks/micro/NoC,stark,50,1000,200,100,300,10000,400,500,600,40.000000,4096");

        var result = await RunScriptAsync(
            repositoryRoot,
            Path.Combine(repositoryRoot, "scripts", "add-benchmark-c-ratios.sh"),
            [current],
            new Dictionary<string, string?>());

        Assert.Equal(0, result.ExitCode);

        var lines = File.ReadAllLines(current);
        Assert.Equal(
            "benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib,c_avg_ratio",
            lines[0]);
        Assert.EndsWith(",1.000000", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",0.800000", lines[2], StringComparison.Ordinal);
        Assert.EndsWith(",1.250000", lines[3], StringComparison.Ordinal);
        Assert.EndsWith(",1.200000", lines[4], StringComparison.Ordinal);
        Assert.EndsWith(",", lines[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckBenchmarkRegressionsPassesWithinConfiguredBaselineThreshold()
    {
        if (!IsCommandAvailable("bash"))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        using var files = new TemporaryBenchmarkFiles();
        var baseline = files.WriteCsv(
            "baseline.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,900,1000,1100,20.000000,4096");
        var current = files.WriteCsv(
            "current.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,900,1075,1150,23.255814,4096");

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
        if (!IsCommandAvailable("bash"))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        using var files = new TemporaryBenchmarkFiles();
        var baseline = files.WriteCsv(
            "baseline.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,900,1000,1100,20.000000,4096");
        var current = files.WriteCsv(
            "current.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,1100,1250,1300,16.000000,4096");

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
        if (!IsCommandAvailable("bash"))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        using var files = new TemporaryBenchmarkFiles();
        var current = files.WriteCsv(
            "current.csv",
            "benchmarks/micro/StackScalarLoadForwarding,stark,50,1000,200,100,300,10000,1800,2100,2300,23.809524,4096",
            "benchmarks/micro/StackScalarLoadForwarding,rust,50,1000,0,0,0,10000,900,1000,1100,20.000000,4096");

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
        var arguments = new List<string> { currentPath };
        if (baselinePath is not null)
        {
            arguments.Add(baselinePath);
        }

        return await RunScriptAsync(
            repositoryRoot,
            Path.Combine(repositoryRoot, "scripts", "check-benchmark-regressions.sh"),
            arguments,
            environment);
    }

    private static async Task<ProcessResult> RunScriptAsync(
        string repositoryRoot,
        string scriptPath,
        IReadOnlyList<string> arguments,
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

        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
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

    private static bool IsCommandAvailable(string name)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed class TemporaryBenchmarkFiles : IDisposable
    {
        private const string Header =
            "benchmark,language,runs,compile_us,llvm_object_us,link_us,toolchain_us,binary_bytes,min_us,avg_us,max_us,runtime_spread_pct,peak_rss_kib";

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
