using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Type-checking contract for pattern-binding conditions `if (expr is pattern)` and
/// `while behavior (expr is pattern)`. The pattern reuses the `switch case` grammar and
/// binder, so this focuses on the new condition surface: captures bind into the
/// consequent, the scrutinee is not required to be `bool`, mismatched patterns are
/// rejected (via the shared switch-pattern diagnostics), and the plain boolean condition
/// still requires `bool`.
/// </summary>
public sealed class IfWhilePatternDiagnosticsTests
{
    private const string Prelude =
        """
        module Demo

        enum Option<T> { Some(T), None }
        enum Result<T, E> { Ok(T), Err(E) }
        enum Other { A, B }

        fn Option<i32[min max]> lookup(i32[min max] x) { return Option<i32[min max]>.Some(x); }
        """;

    [Fact]
    public void IfPatternBindsCaptureIntoThenBranch()
    {
        var result = Compile(Prelude +
            """

            fn i32[min max] UseIf(i32[min max] n)
            {
                if (lookup(n) is Option<i32[min max]>.Some(var value))
                {
                    return value;
                }
                return 0;
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void WhilePatternBindsCaptureIntoLoopBody()
    {
        var result = Compile(Prelude +
            """

            fn i32[min max] UseWhile(i32[min max] n)
            {
                stack mut i32[min max] total = 0;
                while willexit (lookup(n) is Option<i32[min max]>.Some(var value))
                {
                    total = total + value;
                    return total;
                }
                return total;
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void IfPatternCaptureIsNotVisibleInElseBranch()
    {
        // `value` is bound only on the matching (then) path; referencing it in `else` is unknown.
        var result = Compile(Prelude +
            """

            fn i32[min max] LeakCapture(i32[min max] n)
            {
                if (lookup(n) is Option<i32[min max]>.Some(var value))
                {
                    return value;
                }
                else
                {
                    return value;
                }
            }
            """);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void IfPatternWithMismatchedEnumTypeIsRejected()
    {
        // The scrutinee is an `Option<_>`, but the pattern names `Result` — a type mismatch.
        var result = Compile(Prelude +
            """

            fn i32[min max] Mismatch(i32[min max] n)
            {
                if (lookup(n) is Result<i32[min max], Other>.Ok(var value))
                {
                    return value;
                }
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void IfPatternOnNonEnumScrutineeIsRejected()
    {
        // An enum-case pattern against a plain integer scrutinee is a type mismatch.
        var result = Compile(Prelude +
            """

            fn i32[min max] NonEnum(i32[min max] n)
            {
                if (n is Option<i32[min max]>.Some(var value))
                {
                    return value;
                }
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void PlainBooleanIfAndWhileStillRequireBool()
    {
        // No `is pattern`: the condition must still be boolean.
        var result = Compile(Prelude +
            """

            fn i32[min max] BadBoolean(i32[min max] n)
            {
                if (n)
                {
                    return 1;
                }
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void PlainBooleanIfAndWhileAcceptBoolConditions()
    {
        var result = Compile(Prelude +
            """

            fn i32[min max] GoodBoolean(i32[min max] n)
            {
                stack mut i32[min max] total = 0;
                if (n > 0)
                {
                    total = total + 1;
                }
                while willexit (total < 0)
                {
                    total = total + 1;
                }
                return total;
            }
            """);

        AssertSucceeded(result);
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "semantic-validate"));
    }

    private static void AssertSucceeded(CompilationResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
