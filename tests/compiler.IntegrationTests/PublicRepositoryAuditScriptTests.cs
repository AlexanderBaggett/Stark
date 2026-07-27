using System.Diagnostics;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class PublicRepositoryAuditScriptTests
{
    [Fact]
    public void AuditScriptHasCompleteScopeRedactionAndCiFailureContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "audit-public-repository.ps1"));
        var allowlist = File.ReadAllText(
            Path.Combine(repositoryRoot, "scripts", "release-repository-audit-allowlist.json"));

        Assert.Contains("ls-files", script, StringComparison.Ordinal);
        Assert.Contains("--cached", script, StringComparison.Ordinal);
        Assert.Contains("--others", script, StringComparison.Ordinal);
        Assert.Contains("--exclude-standard", script, StringComparison.Ordinal);
        Assert.Contains("rev-list", script, StringComparison.Ordinal);
        Assert.Contains("--objects", script, StringComparison.Ordinal);
        Assert.Contains("--all", script, StringComparison.Ordinal);
        Assert.Contains("cat-file", script, StringComparison.Ordinal);
        Assert.Contains("--batch", script, StringComparison.Ordinal);

        Assert.Contains("private-key", script, StringComparison.Ordinal);
        Assert.Contains("github-token", script, StringComparison.Ordinal);
        Assert.Contains("assigned-secret", script, StringComparison.Ordinal);
        Assert.Contains("private-url", script, StringComparison.Ordinal);
        Assert.Contains("personal-absolute-path", script, StringComparison.Ordinal);
        Assert.Contains("credential-file", script, StringComparison.Ordinal);
        Assert.Contains("large-tracked-binary", script, StringComparison.Ordinal);
        Assert.Contains("large-history-binary", script, StringComparison.Ordinal);

        Assert.Contains("fingerprint", script, StringComparison.Ordinal);
        Assert.Contains("allowlistReason", script, StringComparison.Ordinal);
        Assert.Contains("scanner-error", script, StringComparison.Ordinal);
        Assert.Contains("exit 2", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json -Depth 8", script, StringComparison.Ordinal);
        Assert.DoesNotContain("excerpt =", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("repositoryRoot = $resolvedRoot", script, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(allowlist);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("entries").ValueKind);
    }

    [Fact]
    public void CurrentTreeAuditFindsUntrackedRiskWithoutEchoingMatchedValues()
    {
        var powerShell = FindPowerShell();
        if (powerShell is null)
        {
            // PowerShell is present on GitHub's release runners. Keep this test
            // portable to developer machines that intentionally have no pwsh.
            return;
        }

        using var repository = TemporaryGitRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "safe.txt"), "ordinary public text\n");
        repository.CommitAll("safe baseline");

        var token = string.Concat("gh", "p_", new string('Q', 40));
        var personalPath = string.Concat("/Us", "ers/release-engineer/private/cache");
        var privateUrl = string.Concat("https://build", ".internal/artifacts");
        File.WriteAllText(
            Path.Combine(repository.Path, "untracked-risk.txt"),
            string.Join('\n', token, personalPath, privateUrl));
        File.WriteAllBytes(Path.Combine(repository.Path, "untracked-large.bin"), new byte[2048]);

        var result = RunAudit(
            powerShell,
            repository.Path,
            "-LargeFileThresholdBytes", "1024",
            "-NoFail");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(token, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(personalPath, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(privateUrl, result.Stdout, StringComparison.Ordinal);
        using var report = JsonDocument.Parse(result.Stdout);
        Assert.Equal("findings", report.RootElement.GetProperty("status").GetString());
        Assert.Empty(report.RootElement.GetProperty("scannerErrors").EnumerateArray());
        var findings = report.RootElement.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Contains(findings, finding => IsFinding(finding, "github-token", "current-tree", "untracked-risk.txt"));
        Assert.Contains(findings, finding => IsFinding(finding, "personal-absolute-path", "current-tree", "untracked-risk.txt"));
        Assert.Contains(findings, finding => IsFinding(finding, "private-url", "current-tree", "untracked-risk.txt"));
        Assert.Contains(findings, finding => IsFinding(finding, "large-untracked-binary", "current-tree", "untracked-large.bin"));
    }

    [Fact]
    public void HistoryAuditFindsDeletedSecretAndLargeBlobWithoutEchoingSecret()
    {
        var powerShell = FindPowerShell();
        if (powerShell is null)
        {
            return;
        }

        using var repository = TemporaryGitRepository.Create();
        var token = string.Concat("github_", "pat_", new string('R', 80));
        File.WriteAllText(Path.Combine(repository.Path, "retired-risk.txt"), token);
        File.WriteAllBytes(Path.Combine(repository.Path, "retired-large.bin"), new byte[2048]);
        repository.CommitAll("introduce historical fixtures");
        File.Delete(Path.Combine(repository.Path, "retired-risk.txt"));
        File.Delete(Path.Combine(repository.Path, "retired-large.bin"));
        repository.CommitAll("remove historical fixtures");

        var result = RunAudit(
            powerShell,
            repository.Path,
            "-TrackedOnly",
            "-IncludeHistory",
            "-LargeFileThresholdBytes", "1024",
            "-NoFail");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(token, result.Stdout, StringComparison.Ordinal);
        using var report = JsonDocument.Parse(result.Stdout);
        Assert.Empty(report.RootElement.GetProperty("scannerErrors").EnumerateArray());
        var findings = report.RootElement.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Contains(findings, finding => IsFinding(finding, "github-token", "history", "retired-risk.txt"));
        Assert.Contains(findings, finding => IsFinding(finding, "large-history-binary", "history", "retired-large.bin"));
    }

    [Fact]
    public void ExactFingerprintAllowlistSuppressesOnlyTheReviewedFinding()
    {
        var powerShell = FindPowerShell();
        if (powerShell is null)
        {
            return;
        }

        using var repository = TemporaryGitRepository.Create();
        var token = string.Concat("gh", "o_", new string('S', 40));
        File.WriteAllText(Path.Combine(repository.Path, "reviewed.txt"), token);

        var first = RunAudit(powerShell, repository.Path, "-NoFail");
        Assert.Equal(0, first.ExitCode);
        using var firstReport = JsonDocument.Parse(first.Stdout);
        var finding = firstReport.RootElement.GetProperty("findings")
            .EnumerateArray()
            .Single(item => IsFinding(item, "github-token", "current-tree", "reviewed.txt"));
        var fingerprint = finding.GetProperty("fingerprint").GetString()!;

        var allowlistPath = Path.Combine(repository.Path, "audit-allowlist.json");
        File.WriteAllText(
            allowlistPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entries = new[]
                {
                    new
                    {
                        ruleId = "github-token",
                        path = "reviewed.txt",
                        fingerprint,
                        source = "current-tree",
                        reason = "Synthetic integration-test credential shape."
                    }
                }
            }));

        var second = RunAudit(
            powerShell,
            repository.Path,
            "-AllowlistPath", allowlistPath,
            "-NoFail");

        Assert.Equal(0, second.ExitCode);
        Assert.DoesNotContain(token, second.Stdout, StringComparison.Ordinal);
        using var secondReport = JsonDocument.Parse(second.Stdout);
        Assert.DoesNotContain(
            secondReport.RootElement.GetProperty("findings").EnumerateArray(),
            item => IsFinding(item, "github-token", "current-tree", "reviewed.txt"));
        Assert.Contains(
            secondReport.RootElement.GetProperty("suppressedFindings").EnumerateArray(),
            item => IsFinding(item, "github-token", "current-tree", "reviewed.txt"));
    }

    private static bool IsFinding(JsonElement finding, string ruleId, string source, string path)
        => finding.GetProperty("ruleId").GetString() == ruleId
           && finding.GetProperty("source").GetString() == source
           && finding.GetProperty("path").GetString() == path;

    private static CommandResult RunAudit(string powerShell, string repositoryPath, params string[] arguments)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "audit-public-repository.ps1");
        var startInfo = new ProcessStartInfo(powerShell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-RepositoryRoot");
        startInfo.ArgumentList.Add(repositoryPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new CommandResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }

    private static string? FindPowerShell()
    {
        var executableName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "scripts")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Stark repository root.");
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed class TemporaryGitRepository : IDisposable
    {
        private TemporaryGitRepository(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryGitRepository Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"stark-public-audit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            var repository = new TemporaryGitRepository(path);
            repository.Git("init");
            repository.Git("config", "user.name", "Audit Test");
            repository.Git("config", "user.email", "audit-test@example.invalid");
            return repository;
        }

        public void CommitAll(string message)
        {
            Git("add", "-A");
            Git("commit", "-m", message);
        }

        private void Git(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Git.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git command failed ({process.ExitCode}): {stderr.GetAwaiter().GetResult()}");
            }

            _ = stdout.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort fixture cleanup. The test result must describe the audit,
                // not a transient cleanup issue on a virus-scanned Windows workspace.
            }
        }
    }
}
