using Stark.Compiler;
using System.Diagnostics;

namespace compiler.Tests;

public sealed class LargeOwnedAggregateRuntimeTests
{
    [Fact]
    public async Task LargeEnumPayloadMovesPreserveSingleOwnerDropSemantics()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"stark-large-owned-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var appPath = Path.Combine(tempDirectory, "LargeOwnedAggregateRuntime.stark");
            var witnessPath = Path.Combine(tempDirectory, "LargeOwnedAggregateWitness.c");
            var outputPath = Path.Combine(tempDirectory, OperatingSystem.IsWindows() ? "LargeOwnedAggregateRuntime.exe" : "LargeOwnedAggregateRuntime");
            await File.WriteAllTextAsync(
                witnessPath,
                """
                static volatile int counter;

                void reset_external_counter(void)
                {
                    counter = 0;
                }

                void bump_external_counter(int value)
                {
                    counter += value;
                }

                int read_external_counter(void)
                {
                    return counter;
                }
                """);

            await File.WriteAllTextAsync(
                appPath,
                """
                module Demo

                unsafe ffi fn void reset_external_counter();
                unsafe ffi fn void bump_external_counter(i32[min max] value);
                unsafe ffi fn i32[min max] read_external_counter();

                struct LargeResource
                {
                    i8[min max][512] Data;
                    i32[min max] Value;

                    drop
                    {
                        bump_external_counter(self.Value);
                    }
                }

                enum Result
                {
                    Ok(LargeResource),
                    Err(i32[min max])
                }

                unsafe fn LargeResource MakeResource(i32[min max] value)
                {
                    stack mut LargeResource resource = new LargeResource();
                    resource.Value = value;
                    return resource;
                }

                unsafe fn Result MakeOk(i32[min max] value)
                {
                    stack LargeResource resource = MakeResource(value);
                    return Result.Ok(resource);
                }

                unsafe fn Result MakeErr()
                {
                    return Result.Err(3);
                }

                unsafe fn void RunSuccessPayloadMove()
                {
                    stack Result result = MakeOk(7);
                    switch (result)
                    {
                        case Result.Ok(var payload):
                            stack LargeResource value = payload;
                        case Result.Err(var code):
                    }

                    return;
                }

                unsafe fn void RunErrorPayloadPath()
                {
                    stack Result result = MakeErr();
                    switch (result)
                    {
                        case Result.Ok(var payload):
                            stack LargeResource value = payload;
                        case Result.Err(var code):
                    }

                    return;
                }

                unsafe fn void RunEarlyReturnPayloadMove()
                {
                    stack Result result = MakeOk(7);
                    switch (result)
                    {
                        case Result.Ok(var payload):
                            stack LargeResource value = payload;
                            return;
                        case Result.Err(var code):
                            return;
                    }
                }

                unsafe fn void RunReassignmentPayloadMove()
                {
                    stack Result result = MakeOk(7);
                    switch (result)
                    {
                        case Result.Ok(var payload):
                            stack mut LargeResource value = payload;
                            value = MakeResource(11);
                        case Result.Err(var code):
                    }

                    return;
                }

                export unsafe fn i32[min max] main()
                {
                    reset_external_counter();
                    RunSuccessPayloadMove();
                    if (read_external_counter() != 7)
                    {
                        return 1;
                    }

                    reset_external_counter();
                    RunErrorPayloadPath();
                    if (read_external_counter() != 0)
                    {
                        return 2;
                    }

                    reset_external_counter();
                    RunEarlyReturnPayloadMove();
                    if (read_external_counter() != 7)
                    {
                        return 3;
                    }

                    reset_external_counter();
                    RunReassignmentPayloadMove();
                    if (read_external_counter() != 18)
                    {
                        return 4;
                    }

                    return 0;
                }
                """);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "--native-source", witnessPath, "-o", outputPath, "--target", targetInfo.Triple],
                TextReader.Null,
                stdout,
                stderr);
            Assert.True(compileExitCode == 0, stdout + Environment.NewLine + stderr);

            var execution = await RunProcessAsync(outputPath, tempDirectory);
            Assert.True(
                execution.ExitCode == 0,
                $"Exit code: {execution.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{execution.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{execution.Stderr}");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task MutableGlobalReloadsAfterDestructorBackedCall()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"stark-mutable-global-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var appPath = Path.Combine(tempDirectory, "MutableGlobalDestructorCall.stark");
            var outputPath = Path.Combine(tempDirectory, OperatingSystem.IsWindows() ? "MutableGlobalDestructorCall.exe" : "MutableGlobalDestructorCall");
            await File.WriteAllTextAsync(
                appPath,
                """
                module Demo

                static mut i32[min max] Counter = 0;

                struct LargeResource
                {
                    i8[min max][512] Data;
                    i32[min max] Value;

                    drop
                    {
                        Counter = Counter + self.Value;
                    }
                }

                enum Result
                {
                    Ok(LargeResource),
                    Err(i32[min max])
                }

                unsafe fn LargeResource MakeResource(i32[min max] value)
                {
                    stack mut LargeResource resource = new LargeResource();
                    resource.Value = value;
                    return resource;
                }

                unsafe fn Result MakeOk(i32[min max] value)
                {
                    stack LargeResource resource = MakeResource(value);
                    return Result.Ok(resource);
                }

                unsafe fn void RunSuccessPayloadMove()
                {
                    stack Result result = MakeOk(7);
                    switch (result)
                    {
                        case Result.Ok(var payload):
                            stack LargeResource value = payload;
                        case Result.Err(var code):
                    }

                    return;
                }

                export unsafe fn i32[min max] main()
                {
                    Counter = 0;
                    RunSuccessPayloadMove();
                    if (Counter != 7)
                    {
                        return 1;
                    }

                    return 0;
                }
                """);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-o", outputPath, "--target", targetInfo.Triple],
                TextReader.Null,
                stdout,
                stderr);
            Assert.True(compileExitCode == 0, stdout + Environment.NewLine + stderr);

            var execution = await RunProcessAsync(outputPath, tempDirectory);
            Assert.True(
                execution.ExitCode == 0,
                $"Exit code: {execution.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{execution.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{execution.Stderr}");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string fileName, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
