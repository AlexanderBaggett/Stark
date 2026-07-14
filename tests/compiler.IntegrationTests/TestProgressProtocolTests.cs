using System.Diagnostics;
using System.Text.RegularExpressions;
using Stark.Compiler;

namespace compiler.IntegrationTests;

// Conformance harness for the test-progress streaming protocol
// (docs/Self-host-Prep/30-test-progress-streaming.md §3). The fixture projects
// and golden transcripts under tests/fixtures/test-progress are the normative
// artifacts for the stage0/stage1 contract: the runner-level goldens must stay
// byte-stable, and the stage1 port is expected to reproduce them verbatim.
[Collection("SerialToolchain")]
public sealed class TestProgressProtocolTests
{
    private const string ConformanceProjectKey = "progress-conformance";
    private const string HangProjectKey = "progress-hang";

    private static readonly Regex ElapsedPrefixPattern = new(
        @"^\[\d+\.\d+s\] ",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public async Task RunnerTranscriptsMatchGoldens()
    {
        if (OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            // The goldens pin the [Platform(windows)] fact as skipped, which
            // only holds on non-Windows hosts.
            return;
        }

        var fixtureDirectory = FixtureDirectory("conformance");
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = fixtureDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                WithRepositorySdk("build"),
                new StringReader(string.Empty),
                stdout,
                stderr);
            Assert.True(buildExitCode == 0, $"fixture build failed:\n{stdout}\n{stderr}");

            var runnerPath = RunnerExecutablePath(fixtureDirectory, targetInfo.Triple, ConformanceProjectKey);
            Assert.True(File.Exists(runnerPath), $"generated runner missing at {runnerPath}");

            var legacy = RunProcess(runnerPath, arguments: []);
            Assert.Equal(1, legacy.ExitCode);
            Assert.Equal(Golden(fixtureDirectory, "legacy.stdout.txt"), Normalize(legacy.StandardOutput));
            Assert.Equal(Golden(fixtureDirectory, "legacy.stderr.txt"), Normalize(legacy.StandardError));

            var progress = RunProcess(runnerPath, arguments: ["--progress"]);
            Assert.Equal(1, progress.ExitCode);
            Assert.Equal(Golden(fixtureDirectory, "progress.stdout.txt"), Normalize(progress.StandardOutput));
            Assert.Equal(Golden(fixtureDirectory, "progress.stderr.txt"), Normalize(progress.StandardError));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task DriverStreamsElapsedPrefixedProgressLines()
    {
        if (OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var fixtureDirectory = FixtureDirectory("conformance");
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = fixtureDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                WithRepositorySdk("test", "--test-progress"),
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            var normalizedOut = NormalizeElapsedPrefixes(stdout.ToString());
            var normalizedErr = NormalizeElapsedPrefixes(stderr.ToString());

            // Every forwarded runner line carries a driver-stamped elapsed
            // prefix; ordinals stay sparse under the skip (see §3.3).
            AssertContainsInOrder(
                normalizedOut,
                [
                    "[T] run FirstPasses",
                    "[T] ok FirstPasses (1/10)",
                    "[T] run SecondFailsByDesign",
                    "[T] skipped SkippedOffPlatform: target does not match [Platform]",
                    "[T] run Adds[AddCases:0]",
                    "[T] ok Adds[AddCases:2] (6/10)",
                    "[T] ok TenthPasses (10/10)",
                ]);
            Assert.Contains("[T] FAILED SecondFailsByDesign (2/10)", normalizedErr, StringComparison.Ordinal);
            Assert.Contains("Failed test project 'progress-conformance' with exit code 1.", normalizedErr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task LegacyDriverOutputStaysByteIdenticalWithoutProgressFlag()
    {
        if (OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var fixtureDirectory = FixtureDirectory("conformance");
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = fixtureDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                WithRepositorySdk("test"),
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);

            // Without --test-progress the forwarded lines are byte-identical
            // to the runner-level legacy golden: no prefixes, no markers, no
            // counters.
            var stdoutText = Normalize(stdout.ToString());
            var stderrText = Normalize(stderr.ToString());
            Assert.Contains(Golden(fixtureDirectory, "legacy.stdout.txt"), stdoutText, StringComparison.Ordinal);
            Assert.Contains(Golden(fixtureDirectory, "legacy.stderr.txt"), stderrText, StringComparison.Ordinal);
            Assert.DoesNotMatch(ElapsedPrefixPattern, stdoutText);
            Assert.DoesNotContain("run FirstPasses", stdoutText, StringComparison.Ordinal);
            Assert.DoesNotContain("(1/10)", stdoutText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task TestTimeoutKillsHangingRunnerAndNamesInFlightFact()
    {
        if (OperatingSystem.IsWindows()
            || !NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var fixtureDirectory = FixtureDirectory("hang");
        var originalDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = fixtureDirectory;

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                WithRepositorySdk("test", "--test-progress", "--test-timeout", "5"),
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            var normalizedOut = NormalizeElapsedPrefixes(stdout.ToString());
            AssertContainsInOrder(
                normalizedOut,
                [
                    "[T] run CompletesQuickly",
                    "[T] ok CompletesQuickly (1/2)",
                    "[T] run HangsForever",
                ]);

            // The hanging fact is the last start marker: nothing runs after it.
            var lastRunMarker = normalizedOut.LastIndexOf("[T] run ", StringComparison.Ordinal);
            Assert.True(
                normalizedOut[lastRunMarker..].StartsWith("[T] run HangsForever", StringComparison.Ordinal),
                $"expected the last run marker to name HangsForever:\n{normalizedOut}");

            Assert.Contains(
                "Test run timed out after 5s; the last `run <name>` line above names the fact that was in flight.",
                stderr.ToString(),
                StringComparison.Ordinal);

            AssertNoOrphanedRunnerProcess();
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    private static void AssertNoOrphanedRunnerProcess()
    {
        // The driver kills the entire process tree at the deadline; a
        // lingering runner means the kill regressed. pgrep exits 1 when no
        // process matches.
        var startInfo = new ProcessStartInfo
        {
            FileName = "pgrep",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(HangProjectKey);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var matches = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return;
            }

            Thread.Sleep(200);
            if (attempt == 9)
            {
                Assert.Fail($"runner process tree survived the timeout kill:\n{matches}");
            }
        }
    }

    private static void AssertContainsInOrder(string text, IReadOnlyList<string> fragments)
    {
        var position = 0;
        foreach (var fragment in fragments)
        {
            var index = text.IndexOf(fragment, position, StringComparison.Ordinal);
            Assert.True(index >= 0, $"missing (or out of order) fragment '{fragment}' in:\n{text}");
            position = index + fragment.Length;
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunProcess(
        string fileName,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static string Golden(string fixtureDirectory, string name)
    {
        return Normalize(File.ReadAllText(Path.Combine(fixtureDirectory, "goldens", name)));
    }

    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string NormalizeElapsedPrefixes(string text)
    {
        return ElapsedPrefixPattern.Replace(Normalize(text), "[T] ");
    }

    private static string RunnerExecutablePath(string fixtureDirectory, string targetTriple, string projectKey)
    {
        return Path.Combine(
            fixtureDirectory,
            "build",
            "dev",
            targetTriple,
            "stage0",
            "tests",
            projectKey,
            projectKey);
    }

    private static string[] WithRepositorySdk(params string[] arguments)
    {
        return [.. arguments, "--sdk-root", RepositoryRoot()];
    }

    private static string FixtureDirectory(string name)
    {
        return Path.Combine(RepositoryRoot(), "tests", "fixtures", "test-progress", name);
    }

    private static string RepositoryRoot()
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for test-progress fixtures.");
    }
}
