using Stark.Compiler;

namespace compiler.IntegrationTests;

/// <summary>
/// End-to-end runtime behavior of the `try` error-propagation expression and the `from`
/// enum funnel under the v2 role model (docs/Self-host-Prep/11-error-propagation.md):
/// propagatable types are any two-variant enums carrying the innate `[Ok]`/`[Err]` role
/// attributes — never recognized by type name and never by stdlib identity. Covers
/// success/failure paths, cross-family `from` conversion (including stdlib `IOResult`
/// into a user enum), unit failures, and drop correctness (no double-drop, no leak) for
/// owned payloads on both the success and propagated-error paths.
/// </summary>
public sealed class TryPropagationRuntimeTests
{
    [Fact]
    public async Task TrySameErrorTypePropagatesAndUnwrapsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // The enum uses deliberately non-conventional names (Got/Failed): the runtime
        // lowering must work purely from the [Ok]/[Err] roles, never from variant names.
        // ReadNumber(5)=6, ReadNumber(6)=7, Compute returns Got(7+10=17). main unwraps to 17.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            enum DiskError { Bad }

            enum FetchOutcome<T>
            {
                [Ok] Got(T),
                [Err] Failed(DiskError),
            }

            fn FetchOutcome<i32[min max]> ReadNumber(i32[min max] x)
            {
                if (x < 0)
                {
                    return FetchOutcome<i32[min max]>.Failed(DiskError.Bad);
                }
                return FetchOutcome<i32[min max]>.Got(x + 1);
            }

            fn FetchOutcome<i32[min max]> Compute(i32[min max] start)
            {
                stack i32[min max] n = try ReadNumber(start);
                stack i32[min max] m = try ReadNumber(n);
                return FetchOutcome<i32[min max]>.Got(m + 10);
            }

            export fn i32[min max] main()
            {
                switch (Compute(5))
                {
                    case FetchOutcome<i32[min max]>.Got(var v): return v;
                    case FetchOutcome<i32[min max]>.Failed(var e): return 99;
                }
            }
            """);

        Assert.Equal(17, exitCode);
    }

    [Fact]
    public async Task TryFromConversionAndUnitFailurePropagateOnBothPathsAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Result/Option-shaped enums work like any other role-annotated enum.
        // r1: ComputeHigh(5) Ok path with from-conversion never triggered = 5+1+100 = 106.
        // r2: ComputeHigh(-1) diverts; LowError funnels into HighError.Low = 7.
        // r3: ComputeOpt(3) Some path = 3*2+1 = 7.
        // r4: ComputeOpt(0) None unit-failure diversion = 3. Total 106+7+7+3 = 123.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            enum Result<T, E>
            {
                [Ok] Ok(T),
                [Err] Err(E),
            }

            enum Option<T>
            {
                [Ok] Some(T),
                [Err] None,
            }

            enum LowError { Disk }
            enum HighError { Low from LowError, Other }

            fn Result<i32[min max], LowError> ReadLow(i32[min max] x)
            {
                if (x < 0)
                {
                    return Result<i32[min max], LowError>.Err(LowError.Disk);
                }
                return Result<i32[min max], LowError>.Ok(x + 1);
            }

            fn Result<i32[min max], HighError> ComputeHigh(i32[min max] start)
            {
                stack i32[min max] n = try ReadLow(start);
                return Result<i32[min max], HighError>.Ok(n + 100);
            }

            fn Option<i32[min max]> Lookup(i32[min max] x)
            {
                if (x == 0)
                {
                    return Option<i32[min max]>.None;
                }
                return Option<i32[min max]>.Some(x * 2);
            }

            fn Option<i32[min max]> ComputeOpt(i32[min max] x)
            {
                stack i32[min max] a = try Lookup(x);
                return Option<i32[min max]>.Some(a + 1);
            }

            export fn i32[min max] main()
            {
                stack mut i32[min max] r1 = 0;
                switch (ComputeHigh(5))
                {
                    case Result<i32[min max], HighError>.Ok(var v): r1 = v;
                    case Result<i32[min max], HighError>.Err(var e): r1 = 0;
                }

                stack mut i32[min max] r2 = 0;
                switch (ComputeHigh(-1))
                {
                    case Result<i32[min max], HighError>.Ok(var v): r2 = 0;
                    case Result<i32[min max], HighError>.Err(var e):
                        switch (e)
                        {
                            case HighError.Low(var low): r2 = 7;
                            case HighError.Other: r2 = 1;
                        }
                }

                stack mut i32[min max] r3 = 0;
                switch (ComputeOpt(3))
                {
                    case Option<i32[min max]>.Some(var v): r3 = v;
                    case Option<i32[min max]>.None: r3 = 0;
                }

                stack mut i32[min max] r4 = 0;
                switch (ComputeOpt(0))
                {
                    case Option<i32[min max]>.Some(var v): r4 = 0;
                    case Option<i32[min max]>.None: r4 = 3;
                }

                return r1 + r2 + r3 + r4;
            }
            """);

        Assert.Equal(123, exitCode);
    }

    [Fact]
    public async Task TryStdlibIOResultPropagatesIntoUserEnumThroughFunnelAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Cross-family propagation against the real standard library: the operand is a
        // stdlib IOResult (annotated [Ok]/[Err] in System/IO.stark), the enclosing
        // function returns a user-defined AppResult, and the `Io from IOError` funnel
        // connects the two families.
        // Load(10): ReadValue Ok(12), returns Ok(62). Load(-1): Err(NotFound) funnels
        // into AppError.Io = 5. Total 62+5 = 67.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            import System.IO
            module Demo

            enum AppError
            {
                Io from IOError,
                BadInput,
            }

            enum AppResult<T>
            {
                [Ok] Ok(T),
                [Err] Err(AppError),
            }

            fn IOResult<i32[min max]> ReadValue(i32[min max] x)
            {
                if (x < 0)
                {
                    return IOResult<i32[min max]>.Err(IOError.NotFound);
                }
                return IOResult<i32[min max]>.Ok(x + 2);
            }

            fn AppResult<i32[min max]> Load(i32[min max] x)
            {
                stack i32[min max] n = try ReadValue(x);
                return AppResult<i32[min max]>.Ok(n + 50);
            }

            export fn i32[min max] main()
            {
                stack mut i32[min max] okPart = 0;
                switch (Load(10))
                {
                    case AppResult<i32[min max]>.Ok(var v): okPart = v;
                    case AppResult<i32[min max]>.Err(var e): okPart = 0;
                }

                stack mut i32[min max] errPart = 0;
                switch (Load(-1))
                {
                    case AppResult<i32[min max]>.Ok(var v): errPart = 0;
                    case AppResult<i32[min max]>.Err(var e):
                        switch (e)
                        {
                            case AppError.Io(var ioError): errPart = 5;
                            case AppError.BadInput: errPart = 1;
                        }
                }

                return okPart + errPart;
            }
            """,
            includeDirectory: Path.Combine(FindRepositoryRoot(), "stdlib", "src"));

        Assert.Equal(67, exitCode);
    }

    [Fact]
    public async Task TryDropsOwnedPayloadsExactlyOnceThroughFromConversionAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Ok path: the unwrapped Res{1} is dropped once at PipeHigh scope exit (+1).
        // Err path: the LowErr's owned Res{2} is funneled into HighErr.Low and propagated;
        // it is dropped once when the caller drops the error (+2). Total 3 proves no
        // double-drop (would be >3) and no leak (would be <3), even across the funnel.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo

            static mut i32[min max] Counter = 0;

            fn void Bump(i32[min max] v)
            {
                Counter = Counter + v;
                return;
            }

            struct Res
            {
                i32[min max] V;
                drop
                {
                    Bump(self.V);
                }
            }

            enum Result<T, E>
            {
                [Ok] Ok(T),
                [Err] Err(E),
            }

            enum LowErr { Disk(Res) }
            enum HighErr { Low from LowErr }

            fn Result<Res, LowErr> MakeLow(bool ok)
            {
                if (ok)
                {
                    return Result<Res, LowErr>.Ok(new Res() { V = 1 });
                }
                return Result<Res, LowErr>.Err(LowErr.Disk(new Res() { V = 2 }));
            }

            fn Result<i32[min max], HighErr> PipeHigh(bool ok)
            {
                stack Res r = try MakeLow(ok);
                return Result<i32[min max], HighErr>.Ok(r.V + 100);
            }

            export fn i32[min max] main()
            {
                switch (PipeHigh(true))
                {
                    case Result<i32[min max], HighErr>.Ok(var v): { }
                    case Result<i32[min max], HighErr>.Err(var e): { }
                }
                switch (PipeHigh(false))
                {
                    case Result<i32[min max], HighErr>.Ok(var v): { }
                    case Result<i32[min max], HighErr>.Err(var e): { }
                }
                return Counter;
            }
            """);

        Assert.Equal(3, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source, string? includeDirectory = null)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-try-propagation-runtime-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(sourcePath, source);

            var arguments = new List<string> { sourcePath, "--emit-exe" };
            if (includeDirectory is not null)
            {
                arguments.Add("-I");
                arguments.Add(includeDirectory);
            }

            arguments.Add("-o");
            arguments.Add(outputPath);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [.. arguments],
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
            await process.WaitForExitAsync();

            var processOutput = await process.StandardOutput.ReadToEndAsync();
            var processError = await process.StandardError.ReadToEndAsync();
            Assert.Equal(string.Empty, processOutput);
            Assert.Equal(string.Empty, processError);
            return process.ExitCode;
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for try-propagation tests.");
    }
}
