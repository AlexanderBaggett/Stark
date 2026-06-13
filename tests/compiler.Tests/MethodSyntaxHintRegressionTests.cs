using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Regression for the STK3005 method-syntax hint. Stark has no UFCS, so calling a
/// free function with method syntax (<c>value.Fn(...)</c>) fails. When a free
/// function named <c>Fn</c> exists whose first parameter accepts the receiver
/// type, the "does not contain a field named" diagnostic now suggests the
/// free-call form instead of leaving the mistake unexplained.
/// </summary>
public sealed class MethodSyntaxHintRegressionTests
{
    [Fact]
    public void FreeFunctionCalledWithMethodSyntaxSuggestsTheFreeCallForm()
    {
        var result = Compile(
            """
            module Demo

            struct Point
            {
                i32[min max] X;
            }

            fn i32[min max] GetX(borrow Point self)
            {
                return self.X;
            }

            fn i32[min max] Run(borrow Point p)
            {
                return p.GetX();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(
            result,
            "STK3005",
            "does not contain a field named 'GetX'",
            "'GetX' is a free function",
            "Methods are declared inside the type body");
    }

    [Fact]
    public void GenuinelyMissingMemberHasNoFreeFunctionHint()
    {
        var result = Compile(
            """
            module Demo

            struct Point
            {
                i32[min max] X;
            }

            fn i32[min max] Run(borrow Point p)
            {
                return p.Nonexistent;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3005"
                && diagnostic.Message.Contains("does not contain a field named 'Nonexistent'", StringComparison.Ordinal)
                && !diagnostic.Message.Contains("free function", StringComparison.Ordinal));
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "type-check"));
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
