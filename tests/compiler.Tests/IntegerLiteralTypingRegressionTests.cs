using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Regression for integer-literal typing in ranged-integer arithmetic. A bare
/// literal is typed as the smallest signed singleton range that holds its value
/// (e.g. <c>1</c> is <c>i8[1 1]</c>); reconciling that against a runtime ranged
/// operand in <c>FindCommonType</c> missed the same-type fast path and merged to
/// a full-width, opposite-signed default (<c>u64[...] + 1</c> became <c>i64</c>),
/// which then demanded a narrowing cast that var-with-var arithmetic never needed.
/// A fitting literal now adopts the peer ranged type; a non-fitting (or wrong-sign)
/// literal still requires an explicit cast.
/// </summary>
public sealed class IntegerLiteralTypingRegressionTests
{
    [Fact]
    public void AdditiveLiteralAdoptsRangedUnsignedOperandType()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(u64[0 2 ** 63 - 1] position, u64[0 2 ** 63 - 1] length)
            {
                stack u64[0 2 ** 63 - 1] a = position + 1;
                stack u64[0 2 ** 63 - 1] b = length - 1;
                stack u64[0 2 ** 63 - 1] c = 1 + position;
            }
            """);

        AssertNoNarrowingCastRequired(result);
    }

    [Fact]
    public void MultiplicativeLiteralAdoptsRangedUnsignedOperandType()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(u64[0 2 ** 63 - 1] position)
            {
                stack u64[0 2 ** 63 - 1] a = position * 2;
                stack u64[0 2 ** 63 - 1] b = position % 8;
            }
            """);

        AssertNoNarrowingCastRequired(result);
    }

    [Fact]
    public void BitwiseLiteralAdoptsRangedUnsignedOperandType()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(u64[0 2 ** 63 - 1] flags)
            {
                stack u64[0 2 ** 63 - 1] a = flags | 1;
                stack u64[0 2 ** 63 - 1] b = flags & 255;
            }
            """);

        AssertNoNarrowingCastRequired(result);
    }

    [Fact]
    public void LiteralAdoptsSignedRangedOperandType()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i8[-100 100] count)
            {
                stack i8[-100 100] a = count + 1;
                stack i8[-100 100] b = count - 1;
            }
            """);

        AssertNoNarrowingCastRequired(result);
    }

    [Fact]
    public void LiteralAdoptionStripsQualifiersFromTheAnchorOperand()
    {
        // Regression: the literal must adopt only the anchor's numeric shape, not
        // any out/borrow/frozen qualifier the anchor operand carries. `capacity * 2`
        // (capacity is `out u64`) must type as a plain `u64`, so the explicit
        // conversion is a legal identity — a leaked `out` qualifier made the operand
        // "out u64" and rejected the surrounding conversion (the stdlib's
        // System.Collections capacity-growth site that this fix first broke).
        var result = Compile(
            """
            module Demo

            fn void Grow(out u64[0 2 ** 63 - 1] capacity)
            {
                capacity = 8;
                capacity = (u64[0 2 ** 63 - 1])(capacity * 2);
            }
            """);

        AssertNoNarrowingCastRequired(result);
    }

    [Fact]
    public void NonFittingLiteralStillRequiresExplicitCast()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(u8[0 max] small)
            {
                stack u8[0 max] a = small + 1000;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "narrowing conversion is required");
    }

    [Fact]
    public void NegativeLiteralIntoUnsignedOperandStillRequiresExplicitCast()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(u64[0 2 ** 63 - 1] position)
            {
                stack u64[0 2 ** 63 - 1] a = position + -1;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "narrowing conversion is required");
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "type-check"));
    }

    private static void AssertNoNarrowingCastRequired(CompilationResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3002");
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
