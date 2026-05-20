using System.Diagnostics;
using System.Runtime.InteropServices;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class IntegerArithmeticFoldNativeCodegenTests
{
    [Fact]
    public async Task RepeatedAddFoldLetsX64BackendSelectLea()
    {
        if (OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || !NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || FindFirstAvailableTool("objdump", "llvm-objdump") is not { } objdumpPath)
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-arithmetic-fold-native-");
        var outputPath = Path.Combine(tempDirectory.FullName, "demo.o");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                ["--emit-obj", "-O3", "-o", outputPath],
                new StringReader(
                    """
                    module Demo

                    public fn i32[min max] Run(i32[min max] value) {
                        return value + value + value;
                    }
                    """),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted object file:", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var disassembly = await DisassembleAsync(objdumpPath, outputPath);
            Assert.Contains("lea", disassembly, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("call", disassembly, StringComparison.OrdinalIgnoreCase);
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

    private static async Task<string> DisassembleAsync(string objdumpPath, string objectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = objdumpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add("--no-show-raw-insn");
        startInfo.ArgumentList.Add(objectPath);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        return stdout;
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
