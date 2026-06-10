using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemProcessStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceProcessHelpersTypeCheckAndLower()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibProcess.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Process
                module Demo

                fn i32[min max] ReadCurrentProcess()
                {
                    return System.Process.CurrentId();
                }

                fn void Stop(i32[min max] code)
                {
                    System.Process.Exit(code);
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("define fastcc noundef i32 @ReadCurrentProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc void @Stop(", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Process_CurrentId()", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Process_Exit(", llvm, StringComparison.Ordinal);

        var stopBody = ExtractDefinedFunctionText(llvm, "define fastcc void @Stop(", "Expected Stop to lower as a defined function.");
        Assert.Contains("call fastcc void @System_Process_Exit(", stopBody, StringComparison.Ordinal);
        Assert.Contains("call coldcc void @__stark_unreachable_trap()", stopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ret void", stopBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceProcessSpawnHelpersTypeCheckAndLower()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibProcessSpawn.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Process
                module Demo

                unsafe fn i32[min max] Probe()
                {
                    switch (System.Process.SetEnvironment("STARK_PROCESS_TEST", "one"))
                    {
                        case System.Process.ProcessStatus.Err(var error):
                            return 1;
                        case System.Process.ProcessStatus.Ok:
                    }

                    stack System.Process.ProcessResult<System.Process.ProcessOption<System.Text.OwnedAscii>> env =
                        System.Process.GetEnvironment("STARK_PROCESS_TEST");
                    switch (env)
                    {
                        case System.Process.ProcessResult<System.Process.ProcessOption<System.Text.OwnedAscii>>.Err(var error):
                            return 2;
                        case System.Process.ProcessResult<System.Process.ProcessOption<System.Text.OwnedAscii>>.Ok(var value):
                    }

                    stack System.Process.ProcessResult<System.Process.ProcessCommand> commandResult =
                        System.Process.Command("/bin/sh");
                    switch (commandResult)
                    {
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                            return 3;
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var command):
                            stack mut System.Process.ProcessCommand mutableCommand = command;
                            mutableCommand.AddArgument("-c");
                            mutableCommand.AddArgument("printf ok; printf err 1>&2; exit 7");
                            if (System.Process.CurrentId() < 0)
                            {
                                switch (System.Process.RunCaptureWithInput(mutableCommand, "ignored"))
                                {
                                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var runError):
                                        return 5;
                                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var inputOutput):
                                        if (inputOutput.ExitCode < 0)
                                        {
                                            return 6;
                                        }
                                }

                                switch (System.Process.RunCaptureWithTimeout(mutableCommand, 1))
                                {
                                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var runError):
                                        return 7;
                                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var timeoutOutput):
                                        if (timeoutOutput.ExitCode < -1000)
                                        {
                                            return 8;
                                        }
                                }

                                switch (System.Process.RunCaptureWithInputTimeout(mutableCommand, "ignored", 1))
                                {
                                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var runError):
                                        return 9;
                                    case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var timeoutInputOutput):
                                        return timeoutInputOutput.ExitCode;
                                }
                            }

                            switch (System.Process.RunCapture(mutableCommand))
                            {
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var runError):
                                    return 4;
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var output):
                                    return output.ExitCode;
                            }
                    }
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("@System_Process_Command(", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Process_RunCapture", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Process_RunCaptureWithInput", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Process_RunCaptureWithTimeout", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Process_RunCaptureWithInputTimeout", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Process_GetEnvironment(", llvm, StringComparison.Ordinal);
        Assert.Contains("@System_Process_SetEnvironment(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceProcessExitTerminatesWithRequestedExitCode()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-public-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Process
                module App

                export fn i32[min max] main()
                {
                    if (System.Process.CurrentId() <= 0)
                    {
                        return 3;
                    }

                    System.Process.Exit(23);
                    return 4;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(23, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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
    public async Task SourceProcessRunCaptureCapturesStdoutStderrExitCodeAndEnvironment()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-capture-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Process
                import System.Text
                module App

                unsafe fn bool SliceEqualsAscii(borrow i8[min max][] actual, ascii expected)
                {
                    stack i64[min max] expectedLength = System.Text.AsciiLength(expected);
                    if (expectedLength < 0 || actual.Length != (u64[0 2 ** 63 - 1])expectedLength)
                    {
                        return false;
                    }

                    stack rawptr<i8[min max]> expectedData = System.Text.AsciiData(expected);
                    for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < actual.Length; index += 1)
                    {
                        if (actual[index] != *(&expectedData[index]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                export unsafe fn i32[min max] main()
                {
                    switch (System.Process.GetEnvironment("STARK_PROCESS_INITIAL_VALUE"))
                    {
                        case System.Process.ProcessResult<System.Process.ProcessOption<System.Text.OwnedAscii>>.Err(var error):
                            switch (error)
                            {
                                case System.Process.ProcessError.CString(var cStringError):
                                    switch (cStringError)
                                    {
                                        case System.C.CStringError.NullPointer:
                                            return 110;
                                        case System.C.CStringError.MissingTerminator:
                                            return 111;
                                        case System.C.CStringError.InteriorNull:
                                            return 112;
                                        case System.C.CStringError.InvalidUtf8:
                                            return 113;
                                        case System.C.CStringError.TooLarge:
                                            return 114;
                                        case System.C.CStringError.Memory(var memoryError):
                                            return 115;
                                        case System.C.CStringError.Text(var textError):
                                            return 116;
                                    }
                                case System.Process.ProcessError.InvalidArgument:
                                    return 117;
                                case System.Process.ProcessError.TooLarge:
                                    return 118;
                                case System.Process.ProcessError.IO(var ioError):
                                    return 119;
                                case System.Process.ProcessError.Memory(var memoryError):
                                    return 120;
                                case System.Process.ProcessError.Platform(var code):
                                    return 121;
                            }
                        case System.Process.ProcessResult<System.Process.ProcessOption<System.Text.OwnedAscii>>.Ok(var value):
                            switch (value)
                            {
                                case System.Process.ProcessOption<System.Text.OwnedAscii>.None:
                                    return 12;
                                case System.Process.ProcessOption<System.Text.OwnedAscii>.Some(var text):
                                    if (!System.Text.OwnedAsciiEqualsAscii(text, "initial-ok"))
                                    {
                                        return 13;
                                    }
                            }
                    }

                    switch (System.Process.SetEnvironment("STARK_PROCESS_CAPTURE_VALUE", "capture-ok"))
                    {
                        case System.Process.ProcessStatus.Err(var error):
                            return 10;
                        case System.Process.ProcessStatus.Ok:
                    }

                    switch (System.Process.ArgumentCount())
                    {
                        case System.Process.ProcessResult<u64[0 2 ** 63 - 1]>.Err(var error):
                            return 14;
                        case System.Process.ProcessResult<u64[0 2 ** 63 - 1]>.Ok(var count):
                            if (count == 0)
                            {
                                return 15;
                            }
                    }

                    switch (System.Process.CurrentDirectory())
                    {
                        case System.Process.ProcessResult<System.Text.OwnedAscii>.Err(var error):
                            return 16;
                        case System.Process.ProcessResult<System.Text.OwnedAscii>.Ok(var cwd):
                            if (cwd.IsEmpty())
                            {
                                return 17;
                            }
                    }

                    stack System.Process.ProcessResult<System.Process.ProcessCommand> commandResult =
                        System.Process.Command("/bin/sh");
                    switch (commandResult)
                    {
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                            return 18;
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var command):
                            stack mut System.Process.ProcessCommand child = command;
                            switch (child.AddArgument("-c"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 19;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (child.AddArgument("printf \"$STARK_PROCESS_CAPTURE_VALUE\"; printf err 1>&2; exit 7"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 20;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (child.SetWorkingDirectory("."))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 21;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (System.Process.RunCapture(child))
                            {
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var error):
                                    return 22;
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var output):
                                    if (output.ExitCode != 7)
                                    {
                                        return 23;
                                    }

                                    if (!SliceEqualsAscii(output.StdoutSlice(), "capture-ok"))
                                    {
                                        return 24;
                                    }

                                    if (!SliceEqualsAscii(output.StderrSlice(), "err"))
                                    {
                                        return 25;
                                    }

                                    return 0;
                            }
                    }
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = tempDirectory.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment =
                {
                    ["STARK_PROCESS_INITIAL_VALUE"] = "initial-ok"
                }
            });

            Assert.NotNull(process);
            process!.StandardInput.Close();
            var appStdout = await process.StandardOutput.ReadToEndAsync();
            var appStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var execution = (ExitCode: process.ExitCode, Stdout: appStdout, Stderr: appStderr);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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
    public async Task SourceProcessRunCaptureWithInputFeedsStdinAndClosesIt()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-capture-input-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Process
                import System.Text
                module App

                unsafe fn bool SliceEqualsAscii(borrow i8[min max][] actual, ascii expected)
                {
                    stack i64[min max] expectedLength = System.Text.AsciiLength(expected);
                    if (expectedLength < 0 || actual.Length != (u64[0 2 ** 63 - 1])expectedLength)
                    {
                        return false;
                    }

                    stack rawptr<i8[min max]> expectedData = System.Text.AsciiData(expected);
                    for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < actual.Length; index += 1)
                    {
                        if (actual[index] != *(&expectedData[index]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                export unsafe fn i32[min max] main()
                {
                    stack System.Process.ProcessResult<System.Process.ProcessCommand> commandResult =
                        System.Process.Command("/bin/sh");
                    switch (commandResult)
                    {
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                            return 10;
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var command):
                            stack mut System.Process.ProcessCommand child = command;
                            switch (child.AddArgument("-c"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 11;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (child.AddArgument("cat; printf err 1>&2; exit 9"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 12;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (System.Process.RunCaptureWithInput(child, "input-ok\n"))
                            {
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var error):
                                    return 13;
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var output):
                                    if (output.ExitCode != 9)
                                    {
                                        return 14;
                                    }

                                    if (!SliceEqualsAscii(output.StdoutSlice(), "input-ok\n"))
                                    {
                                        return 15;
                                    }

                                    if (!SliceEqualsAscii(output.StderrSlice(), "err"))
                                    {
                                        return 16;
                                    }

                                    return 0;
                            }
                    }
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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
    public async Task SourceProcessRunCaptureTimeoutKillsChildAndKeepsPartialOutput()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-timeout-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Process
                import System.Text
                module App

                unsafe fn bool SliceEqualsAscii(borrow i8[min max][] actual, ascii expected)
                {
                    stack i64[min max] expectedLength = System.Text.AsciiLength(expected);
                    if (expectedLength < 0 || actual.Length != (u64[0 2 ** 63 - 1])expectedLength)
                    {
                        return false;
                    }

                    stack rawptr<i8[min max]> expectedData = System.Text.AsciiData(expected);
                    for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < actual.Length; index += 1)
                    {
                        if (actual[index] != *(&expectedData[index]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                unsafe fn i32[min max] CheckTimeoutOutput()
                {
                    stack System.Process.ProcessResult<System.Process.ProcessCommand> commandResult =
                        System.Process.Command("/bin/sh");
                    switch (commandResult)
                    {
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                            return 10;
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var command):
                            stack mut System.Process.ProcessCommand child = command;
                            switch (child.AddArgument("-c"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 11;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (child.AddArgument("printf timed-out; printf err 1>&2; exec sleep 5"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 12;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (System.Process.RunCaptureWithTimeout(child, 500))
                            {
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var error):
                                    return 13;
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var output):
                                    if (!output.WasTimedOut())
                                    {
                                        return 14;
                                    }

                                    if (output.ExitCode != 137)
                                    {
                                        return 15;
                                    }

                                    if (!SliceEqualsAscii(output.StdoutSlice(), "timed-out"))
                                    {
                                        return 16;
                                    }

                                    if (!SliceEqualsAscii(output.StderrSlice(), "err"))
                                    {
                                        return 17;
                                    }

                                    return 0;
                            }
                    }
                }

                unsafe fn i32[min max] CheckTimeoutInputOutput()
                {
                    stack System.Process.ProcessResult<System.Process.ProcessCommand> commandResult =
                        System.Process.Command("/bin/sh");
                    switch (commandResult)
                    {
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                            return 20;
                        case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var command):
                            stack mut System.Process.ProcessCommand child = command;
                            switch (child.AddArgument("-c"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 21;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (child.AddArgument("cat; printf err-in 1>&2; exec sleep 5"))
                            {
                                case System.Process.ProcessStatus.Err(var error):
                                    return 22;
                                case System.Process.ProcessStatus.Ok:
                            }

                            switch (System.Process.RunCaptureWithInputTimeout(child, "input-ok\n", 500))
                            {
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var error):
                                    return 23;
                                case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var output):
                                    if (!output.WasTimedOut())
                                    {
                                        return 24;
                                    }

                                    if (output.ExitCode != 137)
                                    {
                                        return 25;
                                    }

                                    if (!SliceEqualsAscii(output.StdoutSlice(), "input-ok\n"))
                                    {
                                        return 26;
                                    }

                                    if (!SliceEqualsAscii(output.StderrSlice(), "err-in"))
                                    {
                                        return 27;
                                    }

                                    return 0;
                            }
                    }
                }

                export unsafe fn i32[min max] main()
                {
                    stack i32[min max] first = CheckTimeoutOutput();
                    if (first != 0)
                    {
                        return first;
                    }

                    return CheckTimeoutInputOutput();
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
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
    public async Task PackagedStdLibProcessHelpersWorkWithoutSource()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-process-package-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg.json");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");

        try
        {
            Assert.True(File.Exists(manifestPath));

            var appSource =
                """
                import System
                module App

                fn i32[min max] Run()
                {
                    if (System.Process.CurrentId() < 0)
                    {
                        return 1;
                    }

                    return 0;
                }

                fn void Stop()
                {
                    System.Process.Exit(5);
                    return;
                }
                """;
            await File.WriteAllTextAsync(appPath, appSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(packageDirectory),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            var stopBody = ExtractDefinedFunctionText(llvm, "define fastcc void @Stop(", "Expected packaged System.Process.Exit caller to lower as a defined function.");
            Assert.Contains("call fastcc void @System_Process_Exit(", stopBody, StringComparison.Ordinal);
            Assert.Contains("call coldcc void @__stark_unreachable_trap()", stopBody, StringComparison.Ordinal);
            Assert.DoesNotContain("ret void", stopBody, StringComparison.Ordinal);
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
}
