using Stark.Compiler;

namespace compiler.IntegrationTests;

/// <summary>
/// End-to-end runtime behavior of pattern-binding conditions: `if (expr is pattern)` and
/// `while behavior (expr is pattern)`. Covers enum-case capture, the then/else split, the
/// `while let` drain loop, nested/struct/literal patterns, and drop-correctness for owned
/// captures (including the move-out of a named-local scrutinee, which must not double-drop).
/// </summary>
public sealed class IfWhilePatternRuntimeTests
{
    [Fact]
    public async Task IfPatternBindsOnMatchAndTakesElseOnMismatch()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // lookup(3) = Some(30) -> binds x=30 (+30). lookup(0) = None -> else (+5). Total 35.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo
            enum Option<T> { Some(T), None }
            fn Option<i32[min max]> lookup(i32[min max] x)
            {
                if (x == 0)
                {
                    return Option<i32[min max]>.None;
                }
                return Option<i32[min max]>.Some(x * 10);
            }
            export fn i32[min max] main()
            {
                stack mut i32[min max] total = 0;
                if (lookup(3) is Option<i32[min max]>.Some(var x))
                {
                    total = total + x;
                }
                else
                {
                    total = total + 1;
                }
                if (lookup(0) is Option<i32[min max]>.Some(var y))
                {
                    total = total + y;
                }
                else
                {
                    total = total + 5;
                }
                return total;
            }
            """);

        Assert.Equal(35, exitCode);
    }

    [Fact]
    public async Task WhilePatternDrainsUntilNoMatch()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // `while let`: drains 5,4,3,2,1 from a counter until None. Sum = 15.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo
            enum Option<T> { Some(T), None }
            static mut i32[min max] N = 5;
            fn Option<i32[min max]> next()
            {
                if (N <= 0)
                {
                    return Option<i32[min max]>.None;
                }
                stack i32[min max] v = N;
                N = N - 1;
                return Option<i32[min max]>.Some(v);
            }
            export fn i32[min max] main()
            {
                stack mut i32[min max] sum = 0;
                while willexit (next() is Option<i32[min max]>.Some(var x))
                {
                    sum = sum + x;
                }
                return sum;
            }
            """);

        Assert.Equal(15, exitCode);
    }

    [Fact]
    public async Task IfPatternSupportsResultNestedStructAndLiteralPatterns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Ok(Point(4,5)) -> x+y=9; Err -> +100 (=109); literal `109` -> +1 (=110).
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo
            enum Result<T, E> { Ok(T), Err(E) }
            enum Err1 { Bad }
            struct Point { i32[min max] X; i32[min max] Y; }
            fn Result<Point, Err1> compute(i32[min max] n)
            {
                if (n < 0)
                {
                    return Result<Point, Err1>.Err(Err1.Bad);
                }
                return Result<Point, Err1>.Ok(new Point() { X = n, Y = n + 1 });
            }
            export fn i32[min max] main()
            {
                stack mut i32[min max] acc = 0;
                if (compute(4) is Result<Point, Err1>.Ok(Point(var x, var y)))
                {
                    acc = acc + x + y;
                }
                if (compute(-1) is Result<Point, Err1>.Err(var e))
                {
                    acc = acc + 100;
                }
                if (acc is 109)
                {
                    acc = acc + 1;
                }
                return acc;
            }
            """);

        Assert.Equal(110, exitCode);
    }

    [Fact]
    public async Task IfAndWhilePatternsDropOwnedCapturesExactlyOnce()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        // Named-local scrutinee: the match moves Res{7} out of `opt`; it is dropped exactly once
        // (+7) and `opt`'s own scope-drop must not re-drop it. The while loop binds and drops an
        // owned Res{10} on each of 3 iterations (+30). Total 37 proves no double-drop and no leak.
        var exitCode = await CompileAndRunExitCodeAsync(
            """
            module Demo
            static mut i32[min max] C = 0;
            fn void Bump(i32[min max] v) { C = C + v; return; }
            struct Res { i32[min max] V; drop { Bump(self.V); } }
            enum Option<T> { Some(T), None }

            fn Option<Res> make(bool s, i32[min max] v)
            {
                if (s)
                {
                    return Option<Res>.Some(new Res() { V = v });
                }
                return Option<Res>.None;
            }

            static mut i32[min max] M = 3;
            fn Option<Res> nextRes()
            {
                if (M <= 0)
                {
                    return Option<Res>.None;
                }
                M = M - 1;
                return Option<Res>.Some(new Res() { V = 10 });
            }

            export fn i32[min max] main()
            {
                stack mut Option<Res> opt = make(true, 7);
                if (opt is Option<Res>.Some(var r))
                {
                    stack i32[min max] used = r.V;
                }
                while willexit (nextRes() is Option<Res>.Some(var r))
                {
                    stack i32[min max] usedLoop = r.V;
                }
                return C;
            }
            """);

        Assert.Equal(37, exitCode);
    }

    private static async Task<int> CompileAndRunExitCodeAsync(string source)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-if-while-pattern-runtime-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(sourcePath, source);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [sourcePath, "--emit-exe", "-o", outputPath],
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
}
