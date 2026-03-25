using Stark.Compiler;

namespace compiler.Tests;

public sealed class SemanticValidationTests
{
    [Fact]
    public void BorrowReturnTypesAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn borrow i32 Echo(borrow i32 value) {
                return value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4000");
    }

    [Fact]
    public void GlobalNonEscapingBorrowsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn storeborrow i32 Source();
            static borrow i32 Current = Source();
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4005");
    }

    [Fact]
    public void NestedRawPointersAreRejectedOutsideFfiBoundaries()
    {
        var result = Compile(
            """
            module Demo

            fn void Use(rawptr<rawptr<i8>> value);
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4006");
    }

    [Fact]
    public void NestedRawPointersAreAllowedOnFfiBoundaries()
    {
        var result = Compile(
            """
            module Demo

            ffi fn void Use(rawptr<rawptr<i8>> value);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void LawsCannotReadGlobalState()
    {
        var result = Compile(
            """
            module Demo

            const i32 Answer = 42;

            law i32 Read() {
                return Answer;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4105");
    }

    [Fact]
    public void LawsCannotWriteGlobalState()
    {
        var result = Compile(
            """
            module Demo

            static mut i32 Counter = 0;

            law void Touch() {
                Counter = 1;
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void LawsCannotCallNonLawFunctions()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Impure() {
                return 1;
            }

            law i32 PureWrapper() {
                return Impure();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4106");
    }

    [Fact]
    public void LawsCanMutatePurelyLocalState()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            law i32 Bump() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value = 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void LawsCannotWriteThroughBorrowedMemory()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn storeborrow mut Box Source();

            law void Touch() {
                stack mut borrow mut Box box = Source();
                box.Value = 1;
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void FiniteFunctionsRejectNonWillexitLoops()
    {
        var result = Compile(
            """
            module Demo

            finite void Loop() {
                while infinite (true) {
                    break;
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4103");
    }

    [Fact]
    public void FiniteFunctionsCannotCallNonFiniteFunctions()
    {
        var result = Compile(
            """
            module Demo

            fn void Maybe() {
                return;
            }

            finite void Outer() {
                Maybe();
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4107");
    }

    [Fact]
    public void FiniteFunctionsRejectRecursiveCallCycles()
    {
        var result = Compile(
            """
            module Demo

            finite void Recur() {
                Recur();
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4108");
    }

    [Fact]
    public void RetborrowsCannotBeForwardedToRetborrowParameters()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32 Bounce(retborrow i32 value);

            fn retborrow i32 Forward(retborrow i32 value) {
                return Bounce(value);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4003");
    }

    [Fact]
    public void SafeBorrowsCannotCrossFfiBoundaries()
    {
        var result = Compile(
            """
            module Demo

            ffi fn void Accept(borrow i32 value);

            fn void Use(borrow i32 value) {
                Accept(value);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4001");
    }

    [Fact]
    public void RetborrowsCanBeUsedLocallyAndReturned()
    {
        var result = Compile(
            """
            module Demo

            fn void Inspect(borrow i32 value) {
                return;
            }

            fn retborrow i32 Echo(retborrow i32 value) {
                Inspect(value);
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);
        Assert.True(validation.Functions["Echo"].EffectsValid);
        Assert.True(validation.Functions["Echo"].BorrowingValid);
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }

    private static void AssertDiagnostic(CompilationResult result, string code)
    {
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }
}
