using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Type-checking contract for switch exhaustiveness (STK3044) and definite return
/// (STK3045). Every `switch` must cover its scrutinee's whole domain — all enum
/// variants, both bools, every value of a ranged integer — or carry a `default` arm;
/// and a non-void function must return on every control-flow path. Together these make
/// the previously silent failure modes (runtime trap on an unmatched switch value,
/// undefined behavior when control falls off the end of a value-returning body)
/// unrepresentable at compile time.
/// </summary>
public sealed class SwitchExhaustivenessDiagnosticsTests
{
    private const string Prelude =
        """
        module Demo

        enum Color { Red, Green, Blue }

        """;

    [Fact]
    public void ExhaustiveEnumSwitchWithAllVariantsReturningTypeChecks()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(Color c)
            {
                switch (c)
                {
                    case Color.Red: return 1;
                    case Color.Green: return 2;
                    case Color.Blue: return 3;
                }
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void EnumSwitchWithDefaultArmTypeChecks()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(Color c)
            {
                switch (c)
                {
                    case Color.Red: return 1;
                    default: return 0;
                }
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void ExhaustiveBoolSwitchTypeChecks()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(bool flag)
            {
                switch (flag)
                {
                    case true: return 1;
                    case false: return 0;
                }
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void ExhaustiveRangedIntegerSwitchTypeChecks()
    {
        // u8[0 3] has exactly four values; covering 0..3 is exhaustive with no default.
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(u8[0 3] tag)
            {
                switch (tag)
                {
                    case 0: return 10;
                    case 1: return 20;
                    case 2: return 30;
                    case 3: return 40;
                }
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void WideIntegerSwitchWithDefaultArmTypeChecks()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(i32[min max] value)
            {
                switch (value)
                {
                    case 0: return 10;
                    case 1: return 20;
                    default: return 0;
                }
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void IfElseWhereBothBranchesReturnTypeChecks()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(bool flag)
            {
                if (flag)
                {
                    return 1;
                }
                else
                {
                    return 0;
                }
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void InfiniteLoopWithoutBreakAsFinalStatementTypeChecks()
    {
        // An `infinite` loop may not contain any structural exit (STK4111), so control
        // can never fall past it: the function body needs no return after the loop.
        var result = Compile(Prelude +
            """
            fn i32[min max] Spin(i32[min max] seed)
            {
                stack mut i32[min max] state = seed;
                while infinite (true)
                {
                    state = state + 1;
                }
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void EnumSwitchMissingVariantIsRejected()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(Color c)
            {
                switch (c)
                {
                    case Color.Red: return 1;
                    case Color.Green: return 2;
                }
            }
            """);

        AssertDiagnostic(result, "STK3044", "Blue");
    }

    [Fact]
    public void BoolSwitchMissingFalseIsRejected()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(bool flag)
            {
                switch (flag)
                {
                    case true: return 1;
                }
            }
            """);

        AssertDiagnostic(result, "STK3044", "'false'");
    }

    [Fact]
    public void RangedIntegerSwitchMissingValueIsRejected()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(u8[0 3] tag)
            {
                switch (tag)
                {
                    case 0: return 10;
                    case 1: return 20;
                    case 2: return 30;
                }
            }
            """);

        AssertDiagnostic(result, "STK3044", "3 of 4");
    }

    [Fact]
    public void StatementPositionEnumSwitchMissingVariantIsRejected()
    {
        // Previously an unmatched value silently fell through the switch; now the gap in
        // coverage is a compile error even when no value is being returned.
        var result = Compile(Prelude +
            """
            static mut i32[min max] Marker = 0;

            fn void Note(Color c)
            {
                switch (c)
                {
                    case Color.Red: Marker = 1;
                    case Color.Green: Marker = 2;
                }
                return;
            }
            """);

        AssertDiagnostic(result, "STK3044", "Blue");
    }

    [Fact]
    public void WhenGuardedLabelsDoNotCountTowardCoverage()
    {
        // The guarded arm may not run even when its pattern matches, so it cannot prove
        // coverage; a default (or unguarded match-all) is still required.
        var result = Compile(Prelude +
            """
            fn i32[min max] Pick(Color c, bool flag)
            {
                switch (c)
                {
                    case Color.Red: return 1;
                    case Color.Green: return 2;
                    case Color.Blue when flag: return 3;
                }
            }
            """);

        AssertDiagnostic(result, "STK3044", "Blue");
    }

    [Fact]
    public void FunctionWithoutReturnIsRejected()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Broken(i32[min max] x)
            {
                stack i32[min max] y = x + 1;
            }
            """);

        AssertDiagnostic(result, "STK3045", "Broken");
    }

    [Fact]
    public void IfWithoutElseReturningIsRejected()
    {
        var result = Compile(Prelude +
            """
            fn i32[min max] Broken(bool flag)
            {
                if (flag)
                {
                    return 1;
                }
            }
            """);

        AssertDiagnostic(result, "STK3045", "Broken");
    }

    [Fact]
    public void InfiniteLoopWithBreakAsFinalStatementIsRejected()
    {
        // A break makes the "infinite" loop exit, so control falls past it with no value.
        var result = Compile(Prelude +
            """
            fn i32[min max] Broken(i32[min max] seed)
            {
                stack mut i32[min max] state = seed;
                while infinite (true)
                {
                    state = state + 1;
                    if (state > 100)
                    {
                        break;
                    }
                }
            }
            """);

        AssertDiagnostic(result, "STK3045", "Broken");
    }

    [Fact]
    public void VoidFunctionWithoutReturnTypeChecks()
    {
        // Definite return only applies to value-returning functions.
        var result = Compile(Prelude +
            """
            static mut i32[min max] Marker = 0;

            fn void Note(i32[min max] x)
            {
                Marker = x;
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

    private static void AssertDiagnostic(CompilationResult result, string code, string messageFragment)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code && diagnostic.Message.Contains(messageFragment, StringComparison.Ordinal));
    }
}
