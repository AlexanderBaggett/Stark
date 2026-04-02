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
    public void ConstGlobalsRejectReachableRawPointers()
    {
        var result = Compile(
            """
            module Demo

            struct Holder {
                rawptr<i8> Ptr;
            }

            const Holder Current = new Holder() { Ptr = null };
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4007");
    }

    [Fact]
    public void ConstSliceGlobalsRejectNonMaterializableExpressionInitializers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[] Source();

            const i32[] View = Source();
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4008");
    }

    [Fact]
    public void ConstGlobalsRejectInitializersThatCannotBeMaterializedAsStaticData()
    {
        var result = Compile(
            """
            module Demo

            const i32 Answer = 1 + 2;
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4008");
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

            static i32 Counter = 1;

            fn i32 Impure() {
                return Counter;
            }

            law i32 PureWrapper() {
                return Impure();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4106");
    }

    [Fact]
    public void LawsCanCallPlainFnsWhenTheCompilerCanProveTheyArePure()
    {
        var result = Compile(
            """
            module Demo

            fn i32 PureAdd(i32 left, i32 right) {
                return left + right;
            }

            law i32 Use() {
                return PureAdd(1, 2);
            }
            """);

        Assert.True(result.Succeeded);
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
                while infinite (true) {
                    break;
                }

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
    public void FiniteFunctionsCanCallPlainFnsWhenTheCompilerCanProveTheyAreFinite()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Step(i32 value) {
                return value + 1;
            }

            finite i32 Outer(i32 value) {
                return Step(value);
            }
            """);

        Assert.True(result.Succeeded);
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

    [Fact]
    public void DoctrineMembersParticipateInLawValidation()
    {
        var result = Compile(
            """
            module Demo

            const i32 Answer = 42;

            doctrine Numbers {
                law i32 Read() {
                    return Answer;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4105");
    }

    [Fact]
    public void ReadOnlyDropBlocksCannotMutateSelf()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i32 Value;

                drop {
                    self.Value = 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4011");
    }

    [Fact]
    public void MutDropWithoutSelfMutationProducesWarning()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i32 Value;

                mut drop {
                    ;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4010"
                && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void DestructorBlocksCannotReturn()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                drop {
                    return;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4014");
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static void AssertDiagnostic(CompilationResult result, string code)
    {
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }
}
