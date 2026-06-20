using System.Text.Json;
using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class HostCompilerTestRunnerTests
{
    [Fact]
    public async Task HostTestInspectReturnsStructuredArtifactsDiagnosticsLogsAndExecutions()
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            request = new
            {
                id = "law-llvm",
                sourceText =
                    """
                    module Demo

                    law i32[min max] Identity(i32[min max] value)
                    {
                        return value;
                    }
                    """,
                stopAfterPassId = "emit-llvm",
                optimizationLevel = "O0",
                artifacts = new[] { "llvm", "mir", "optimized-ssa" },
                includeLogs = true,
                includeExecutions = true
            }
        });

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--host-test-inspect"],
            new StringReader(requestJson),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var document = JsonDocument.Parse(stdout.ToString());
        var response = Assert.Single(document.RootElement.GetProperty("responses").EnumerateArray());
        Assert.Equal("law-llvm", response.GetProperty("id").GetString());
        Assert.True(response.GetProperty("succeeded").GetBoolean());
        Assert.Equal("Demo", response.GetProperty("rootModuleName").GetString());
        Assert.Equal(0, response.GetProperty("diagnosticSummary").GetProperty("errorCount").GetInt32());
        Assert.Contains(
            response.GetProperty("availableArtifacts").EnumerateArray(),
            artifact => artifact.GetString() == CompilerArtifactKeys.LlvmIrModule.Name);
        Assert.Contains(
            response.GetProperty("executions").EnumerateArray(),
            execution => execution.GetProperty("passId").GetString() == "emit-llvm"
                         && execution.GetProperty("status").GetString() == PassExecutionStatus.Executed.ToString());
        Assert.Contains(
            response.GetProperty("logs").EnumerateArray(),
            log => log.GetProperty("eventId").GetString() == "pass-completed"
                   && log.GetProperty("stage").GetString() == "emit-llvm");

        var artifactTexts = response.GetProperty("artifactTexts").EnumerateArray().ToArray();
        Assert.Contains(
            artifactTexts,
            artifact => artifact.GetProperty("name").GetString() == CompilerArtifactKeys.LlvmIrModule.Name
                        && artifact.GetProperty("text").GetString()!.Contains("ModuleID = 'Demo'", StringComparison.Ordinal));
        Assert.Contains(
            artifactTexts,
            artifact => artifact.GetProperty("name").GetString() == CompilerArtifactKeys.MidLevelIr.Name
                        && artifact.GetProperty("text").GetString()!.Contains("mir module Demo", StringComparison.Ordinal));
        Assert.Contains(
            artifactTexts,
            artifact => artifact.GetProperty("name").GetString() == CompilerArtifactKeys.OptimizedSsaIr.Name
                        && artifact.GetProperty("text").GetString()!.Contains("ssa module Demo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostTestInspectAcceptsBatchRequestsAndSourcePaths()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-host-test-batch-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "FromFile.stark");

        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                """
                module FromFile

                fn i32[min max] Run()
                {
                    return 7;
                }
                """);

            var requestJson = JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                requests = new object[]
                {
                    new
                    {
                        id = "from-file",
                        sourcePath,
                        stopAfterPassId = "type-check"
                    },
                    new
                    {
                        id = "invalid-source",
                        sourceText = "module",
                        stopAfterPassId = "parse"
                    }
                }
            });

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["--host-test-inspect"],
                new StringReader(requestJson),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            using var document = JsonDocument.Parse(stdout.ToString());
            var responses = document.RootElement.GetProperty("responses").EnumerateArray().ToArray();
            Assert.Equal(2, responses.Length);

            var fromFile = Assert.Single(responses, response => response.GetProperty("id").GetString() == "from-file");
            Assert.True(fromFile.GetProperty("succeeded").GetBoolean());
            Assert.Equal("FromFile", fromFile.GetProperty("rootModuleName").GetString());
            Assert.Contains(
                fromFile.GetProperty("availableArtifacts").EnumerateArray(),
                artifact => artifact.GetString() == CompilerArtifactKeys.TypeCheckModel.Name);

            var invalidSource = Assert.Single(responses, response => response.GetProperty("id").GetString() == "invalid-source");
            Assert.False(invalidSource.GetProperty("succeeded").GetBoolean());
            Assert.True(invalidSource.GetProperty("diagnosticSummary").GetProperty("errorCount").GetInt32() > 0);
            Assert.NotEmpty(invalidSource.GetProperty("diagnostics").EnumerateArray());
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
    public async Task HostTestInspectExportsArtifactsAndDiagnosticsWithoutInliningArtifactText()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-host-test-exports-");
        var artifactDirectory = Path.Combine(tempDirectory.FullName, "artifacts");
        var diagnosticsPath = Path.Combine(tempDirectory.FullName, "diagnostics", "result.json");
        var executionsPath = Path.Combine(tempDirectory.FullName, "diagnostics", "executions.json");

        try
        {
            var requestJson = JsonSerializer.Serialize(new
            {
                protocolVersion = 1,
                request = new
                {
                    id = "exported-artifacts",
                    sourceText =
                        """
                        module Exported

                        fn i32[min max] Run()
                        {
                            return 11;
                        }
                        """,
                    stopAfterPassId = "emit-llvm",
                    optimizationLevel = "O0",
                    artifacts = new[] { "llvm", "mir" },
                    includeArtifactTexts = false,
                    artifactOutputDirectory = artifactDirectory,
                    diagnosticsOutputPath = diagnosticsPath,
                    executionsOutputPath = executionsPath,
                    includeExecutions = false
                }
            });

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                ["--host-test-inspect"],
                new StringReader(requestJson),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            using var document = JsonDocument.Parse(stdout.ToString());
            var response = Assert.Single(document.RootElement.GetProperty("responses").EnumerateArray());
            Assert.True(response.GetProperty("succeeded").GetBoolean());
            Assert.Empty(response.GetProperty("artifactTexts").EnumerateArray());
            Assert.Empty(response.GetProperty("outputErrors").EnumerateArray());
            Assert.Equal(Path.GetFullPath(diagnosticsPath), response.GetProperty("diagnosticsOutputPath").GetString());
            Assert.Equal(Path.GetFullPath(executionsPath), response.GetProperty("executionsOutputPath").GetString());
            Assert.Empty(response.GetProperty("executions").EnumerateArray());

            var artifactFiles = response.GetProperty("artifactFiles").EnumerateArray().ToArray();
            Assert.Equal(2, artifactFiles.Length);
            Assert.Contains(
                artifactFiles,
                artifact => artifact.GetProperty("name").GetString() == CompilerArtifactKeys.LlvmIrModule.Name
                            && artifact.GetProperty("path").GetString() == Path.Combine(artifactDirectory, "llvm-ir-module.ll"));
            Assert.Contains(
                artifactFiles,
                artifact => artifact.GetProperty("name").GetString() == CompilerArtifactKeys.MidLevelIr.Name
                            && artifact.GetProperty("path").GetString() == Path.Combine(artifactDirectory, "mid-level-ir.mir"));

            Assert.Contains("ModuleID = 'Exported'", await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "llvm-ir-module.ll")), StringComparison.Ordinal);
            Assert.Contains("mir module Exported", await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "mid-level-ir.mir")), StringComparison.Ordinal);

            using var diagnostics = JsonDocument.Parse(await File.ReadAllTextAsync(diagnosticsPath));
            Assert.Equal(0, diagnostics.RootElement.GetProperty("summary").GetProperty("errorCount").GetInt32());

            using var executions = JsonDocument.Parse(await File.ReadAllTextAsync(executionsPath));
            Assert.Contains(
                executions.RootElement.EnumerateArray(),
                execution => execution.GetProperty("passId").GetString() == "emit-llvm");
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
    public async Task HostTestServerProcessesMultipleJsonLinesAndShutdown()
    {
        var firstRequest = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            request = new
            {
                id = "server-ok",
                sourceText = "module ServerOk",
                stopAfterPassId = "syntax-model"
            }
        });
        var secondRequest = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            request = new
            {
                id = "server-error"
            }
        });
        var shutdown = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            shutdown = true
        });

        var stdin = string.Join('\n', firstRequest, secondRequest, shutdown);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            ["--host-test-server"],
            new StringReader(stdin),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        var lines = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        var firstResponse = Assert.Single(first.RootElement.GetProperty("responses").EnumerateArray());
        Assert.Equal("server-ok", firstResponse.GetProperty("id").GetString());
        Assert.True(firstResponse.GetProperty("succeeded").GetBoolean());

        using var second = JsonDocument.Parse(lines[1]);
        var secondResponse = Assert.Single(second.RootElement.GetProperty("responses").EnumerateArray());
        Assert.Equal("server-error", secondResponse.GetProperty("id").GetString());
        Assert.False(secondResponse.GetProperty("succeeded").GetBoolean());
        Assert.Contains("sourceText or sourcePath", secondResponse.GetProperty("protocolError").GetString(), StringComparison.Ordinal);

        using var shutdownResponse = JsonDocument.Parse(lines[2]);
        Assert.True(shutdownResponse.RootElement.GetProperty("shutdown").GetBoolean());
    }
}
