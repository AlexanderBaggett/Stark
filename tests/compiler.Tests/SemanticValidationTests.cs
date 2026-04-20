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

            fn borrow i32[-2147483648 2147483647] Echo(borrow i32[-2147483648 2147483647] value) {
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

            fn storeborrow i32[-2147483648 2147483647] Source();
            static borrow i32[-2147483648 2147483647] Current = Source();
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

            fn void Use(rawptr<rawptr<i8[-128 127]>> value);
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

            ffi fn void Use(rawptr<rawptr<i8[-128 127]>> value);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void PublicSafeRawAllocationApisAreRejected()
    {
        var result = Compile(
            """
            module Demo

            public fn rawmutptr<i8[-128 127]> AllocateBytes(i64[0 9223372036854775807] byteCount);
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4118");
    }

    [Fact]
    public void PublicSafeRawFreeApisAreRejected()
    {
        var result = Compile(
            """
            module Demo

            public fn void FreeBytes(rawmutptr<i8[-128 127]> pointer);
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4118");
    }

    [Fact]
    public void InternalRawAllocationApisRemainAvailableForLowLevelImplementation()
    {
        var result = Compile(
            """
            module Demo

            internal fn rawmutptr<i8[-128 127]> AllocateBytes(i64[0 9223372036854775807] byteCount);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FfiRawAllocationBoundariesRemainAvailable()
    {
        var result = Compile(
            """
            module Demo

            public ffi fn rawmutptr<i8[-128 127]> AllocateBytes(i64[0 9223372036854775807] byteCount);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void PublicSafeNonAllocationRawPointerViewsRemainAvailable()
    {
        var result = Compile(
            """
            module Demo

            public finite law rawptr<i8[-128 127]> Data(ascii source);
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
                rawptr<i8[-128 127]> Ptr;
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

            fn i32[-2147483648 2147483647][] Source();

            const i32[-2147483648 2147483647][] View = Source();
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4008");
    }

    [Fact]
    public void ConstGlobalsAllowPureArithmeticInitializers()
    {
        var result = Compile(
            """
            module Demo

            const i32[-2147483648 2147483647] Answer = 1 + 2;
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ConstGlobalsRejectNonEvaluableFunctionCallInitializers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Read();

            const i32[-2147483648 2147483647] Answer = Read();
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

            const i32[-2147483648 2147483647] Answer = 42;

            law i32[-2147483648 2147483647] Read() {
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

            static mut i32[-2147483648 2147483647] Counter = 0;

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

            static i32[-2147483648 2147483647] Counter = 1;

            fn i32[-2147483648 2147483647] Impure() {
                return Counter;
            }

            law i32[-2147483648 2147483647] PureWrapper() {
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

            fn i32[-2147483648 2147483647] PureAdd(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            law i32[-2147483648 2147483647] Use() {
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
                i32[-2147483648 2147483647] Value;
            }

            law i32[-2147483648 2147483647] Bump() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value = 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void LawsCanForwardRetborrowThroughLawWrappers()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[0 max] Value;

                law retborrow i32[0 max] Get(borrow Box self) {
                    return self.Value;
                }
            }

            struct Holder {
                Box Inner;

                law retborrow i32[0 max] Get(borrow Holder self) {
                    return self.Inner.Get();
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void LawsCannotWriteThroughBorrowedMemory()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
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
    public void FiniteFunctionsRejectNonDeterministicForLoops()
    {
        var result = Compile(
            """
            module Demo

            finite void Loop(bool flag) {
                for non-deterministic (; flag; ) {
                    ;
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4103");
    }

    [Fact]
    public void InfiniteLoopsMustUseStaticallyUnconditionalConditions()
    {
        var result = Compile(
            """
            module Demo

            fn void Loop(bool flag) {
                while infinite (flag) {
                    ;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4111");
    }

    [Fact]
    public void InfiniteLoopsRejectStructuralExit()
    {
        var result = Compile(
            """
            module Demo

            fn void Loop() {
                while infinite (true) {
                    break;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4111");
    }

    [Fact]
    public void WillexitWhileLoopsWithUnconditionalConditionsRequireStructuralExit()
    {
        var result = Compile(
            """
            module Demo

            fn void Loop() {
                while willexit (true) {
                    ;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4112");
    }

    [Fact]
    public void WillexitForLoopsWithOmittedConditionsRequireStructuralExit()
    {
        var result = Compile(
            """
            module Demo

            fn void Loop() {
                for willexit (;; ) {
                    ;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4112");
    }

    [Fact]
    public void WillexitLoopsAcceptStructuralExitForUnconditionalConditions()
    {
        var result = Compile(
            """
            module Demo

            fn void Loop() {
                for willexit (;; ) {
                    break;
                }

                return;
            }
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BreakOutsideLoopOrSwitchIsRejected()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                break;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4113");
    }

    [Fact]
    public void ContinueOutsideLoopIsRejectedEvenInsideSwitch()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] value) {
                switch (value) {
                    default:
                        continue;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4114");
    }

    [Fact]
    public void BreakInsideSwitchIsAllowed()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] value) {
                switch (value) {
                    case 0:
                        break;
                    default:
                        break;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void WillexitLoopDoesNotTreatSwitchBreakAsALoopExit()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] value) {
                while willexit (true) {
                    switch (value) {
                        default:
                            break;
                    }
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4112");
    }

    [Fact]
    public void InfiniteLoopAllowsSwitchBreakBecauseItOnlyExitsTheSwitch()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[-2147483648 2147483647] value) {
                while infinite (true) {
                    switch (value) {
                        default:
                            break;
                    }
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FunctionModifiersRejectConflictingInlinePreferences()
    {
        var result = Compile(
            """
            module Demo

            inline noinline fn void Run() {
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4109");
    }

    [Fact]
    public void FunctionModifiersRejectHotAndColdTogether()
    {
        var result = Compile(
            """
            module Demo

            hot cold fn void Run() {
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4110");
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

            fn i32[-2147483648 2147483647] Step(i32[-2147483648 2147483647] value) {
                return value + 1;
            }

            finite i32[-2147483648 2147483647] Outer(i32[-2147483648 2147483647] value) {
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

            fn retborrow i32[-2147483648 2147483647] Bounce(retborrow i32[-2147483648 2147483647] value);

            fn retborrow i32[-2147483648 2147483647] Forward(retborrow i32[-2147483648 2147483647] value) {
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

            ffi fn void Accept(borrow i32[-2147483648 2147483647] value);

            fn void Use(borrow i32[-2147483648 2147483647] value) {
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

            fn void Inspect(borrow i32[-2147483648 2147483647] value) {
                return;
            }

            fn retborrow i32[-2147483648 2147483647] Echo(retborrow i32[-2147483648 2147483647] value) {
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

            const i32[-2147483648 2147483647] Answer = 42;

            doctrine Numbers {
                law i32[-2147483648 2147483647] Read() {
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
                i32[-2147483648 2147483647] Value;

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
                i32[-2147483648 2147483647] Value;

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
    public void MutDropCallingSelfMethodDoesNotProduceFalseWarning()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i32[-2147483648 2147483647] Value;

                fn void Reset(mut borrow Buffer self) {
                    self.Value = 0;
                    return;
                }

                mut drop {
                    self.Reset();
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4010");
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
