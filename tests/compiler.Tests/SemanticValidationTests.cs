using Stark.Compiler;

namespace compiler.Tests;

public sealed class SemanticValidationTests
{
    [Fact]
    public void StrictIntegerRangesRejectNonCanonicalStorageOutsideFfiBoundaries()
    {
        var result = Compile(
            """
            module Demo

            alias SmallAlias = i32[0 255];

            struct Box
            {
                i32[0 255] Value;
                i32[0 127][4] Fixed;
            }

            struct Holder<T>
            {
                T Value;
            }

            fn i64[0 max] Run(i32[1 10] parameter, Holder<i32[0 63]> holder)
            {
                stack dynamic i32[0 7] values = new();
                stack i32[0 128] local = parameter;
                stack i32[0 9] converted = (i32[0 9])local;
                return local;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                EnforceIntegerRangeStorageRules: true));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[0 255]", "u8"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[0 127]", "u8[0 127]"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i64[0 9223372036854775807]", "u64[0 9223372036854775807]"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[1 10]", "u8[1 10]"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[0 63]", "u8[0 63]"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[0 7]", "u8[0 7]"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[0 128]", "u8[0 128]"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[0 9]", "u8[0 9]"));
    }

    [Fact]
    public void StrictIntegerRangesAllowFfiAbiSignatureStorage()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi fn i32[0 255] Read(i32[0 255] value);
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                EnforceIntegerRangeStorageRules: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StrictIntegerRangesAllowPlatformAnnotatedAbiSignatureStorage()
    {
        var result = Compile(
            """
            module Demo

            [Platform]
            unsafe fn i32[0 255] ReadPlatformStatus(i32[0 255] status)
            {
                return status;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                EnforceIntegerRangeStorageRules: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StrictIntegerRangesAllowPlatformAnnotatedAggregateFieldStorage()
    {
        var result = Compile(
            """
            module Demo

            [Platform]
            struct HostStatus
            {
                i32[0 255] Code;
            }

            fn i32[min max] Read(HostStatus status)
            {
                return (i32[min max])status.Code;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                EnforceIntegerRangeStorageRules: true));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StrictIntegerRangesStillRejectNonCanonicalLocalsInsidePlatformAnnotatedFunctions()
    {
        var result = Compile(
            """
            module Demo

            [Platform]
            unsafe fn i32[min max] Run()
            {
                stack i32[0 255] status = 0;
                return status;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                EnforceIntegerRangeStorageRules: true));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[0 255]", "u8"));
    }

    [Fact]
    public void StrictIntegerRangesRejectUnnecessarilyWideUnsignedAndSignedRanges()
    {
        var result = Compile(
            """
            module Demo

            fn u32[0 255] Run(u16[0 15] tag)
            {
                stack i32[-1 1] delta = -1;
                return tag;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                EnforceIntegerRangeStorageRules: true));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "u32[0 255]", "u8"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "u16[0 15]", "u8[0 15]"));
        Assert.Contains(result.Diagnostics, static diagnostic => IsStrictRangeDiagnostic(diagnostic, "i32[-1 1]", "i8[-1 1]"));
    }

    [Fact]
    public void BorrowReturnTypesAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn borrow i32[min max] Echo(borrow i32[min max] value)
            {
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

            struct Box
            {
                i32[min max] Value;
            }

            fn storeborrow Box Source();
            static borrow Box Current = Source();
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002");
    }

    [Fact]
    public void AggregateFieldsRejectNonEscapingBorrowClasses()
    {
        var result = Compile(
            """
            module Demo

            struct BorrowField
            {
                borrow i32[min max] Value;
            }

            struct RetBorrowField
            {
                retborrow i32[min max] Value;
            }

            struct BorrowClosureField
            {
                borrow closure<fn i32[min max]()> Callback;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4005"
                && diagnostic.Message.Contains("Field declarations may not store 'borrow i32'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4005"
                && diagnostic.Message.Contains("Field declarations may not store 'retborrow i32'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4005"
                && diagnostic.Message.Contains("borrow closure", StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateFieldsAllowExplicitStoredClosureBorrows()
    {
        var result = Compile(
            """
            module Demo

            struct Holder
            {
                storeborrow closure<fn void()> Callback;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void TopLevelRegisterStorageIsRejected()
    {
        var result = Compile(
            """
            module Demo

            register i32[min max] Value = 1;
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4015"
                && diagnostic.Message.Contains("Top-level global variables must use 'static' storage", StringComparison.Ordinal)
                && diagnostic.Message.Contains("'register'", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterLocalsCannotBeAddressed()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run()
            {
                register mut i32[min max] value = 1;
                stack rawmutptr<i32[min max]> pointer = &value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4016"
                && diagnostic.Message.Contains("Register local 'value' cannot be addressed", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Use 'stack' storage", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterLocalsCannotBePassedToBorrowParameters()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Read(borrow Box box)
            {
                return box.Value;
            }

            fn i32[min max] Run()
            {
                register Box box = new Box()
                {
                    Value = 1
                };
                return Read(box);
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4016"
                && diagnostic.Message.Contains("Register local 'box' cannot be used where stable storage is required", StringComparison.Ordinal)
                && diagnostic.Message.Contains("borrow", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterFixedArraysCannotFormSliceViews()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                register i32[min max][2] values =
                {
                    1, 2
                };
                stack i32[min max][] view = values;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4016"
                && diagnostic.Message.Contains("Register local 'values' cannot be used where stable storage is required", StringComparison.Ordinal)
                && diagnostic.Message.Contains("slice view", StringComparison.Ordinal));
    }

    [Fact]
    public void ArenaLocalStorageIsRejectedUntilArenaLoweringExists()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                arena i32[min max] value = 1;
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4017"
                && diagnostic.Message.Contains("Local 'arena' storage", StringComparison.Ordinal)
                && diagnostic.Message.Contains("not a valid executable local storage class", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionLocalStaticStorageIsRejectedUntilStaticLocalSemanticsExist()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                static mut i32[min max] value = 1;
                value = value + 1;
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4017"
                && diagnostic.Message.Contains("Function-local 'static' storage", StringComparison.Ordinal)
                && diagnostic.Message.Contains("not a valid local storage class", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedRawPointersAreRejectedOutsideFfiBoundaries()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Use(rawptr<rawptr<i8[min max]>> value);
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

            unsafe ffi fn void Use(rawptr<rawptr<i8[min max]>> value);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NestedRawPointersAreAllowedInPlatformAggregateFields()
    {
        var result = Compile(
            """
            module Demo

            [Platform]
            struct NativeList
            {
                rawmutptr<rawptr<i8[min max]>> Items;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void PublicSafeRawAllocationApisAreRejected()
    {
        var result = Compile(
            """
            module Demo

            public unsafe fn rawmutptr<i8[min max]> AllocateBytes(u64[0 9223372036854775807] byteCount);
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

            public unsafe fn void FreeBytes(rawmutptr<i8[min max]> pointer);
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

            internal unsafe fn rawmutptr<i8[min max]> AllocateBytes(u64[0 9223372036854775807] byteCount);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FfiRawAllocationBoundariesRemainAvailable()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi fn rawmutptr<i8[min max]> AllocateBytes(i64[0 max] byteCount);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void PublicSafeNonAllocationRawPointerViewsRemainAvailable()
    {
        var result = Compile(
            """
            module Demo

            public unsafe finite law rawptr<i8[min max]> Data(ascii source);
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ConstGlobalsRejectReachableRawPointers()
    {
        var result = Compile(
            """
            module Demo

            struct Holder
            {
                rawptr<i8[min max]> Ptr;
            }

            const Holder Current = new Holder()
            {
                Ptr = null
            };
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

            fn i32[min max][] Source();

            const i32[min max][] View = Source();
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

            const u8 Answer = 1 + 2;
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ConstGlobalsRejectNonEvaluableFunctionCallInitializers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Read();

            const i32 Answer = Read();
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

            const Answer = 42;

            law u8[0 max] Read()
            {
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

            static mut i32[min max] Counter = 0;

            law void Touch()
            {
                Counter = 1;
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void LawsCannotAllocateDynamicStorage()
    {
        var result = Compile(
            """
            module Demo

            law dynamic i32[0 max] Make()
            {
                return new(4);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void LawsCannotReserveDynamicStorage()
    {
        var result = Compile(
            """
            module Demo

            law void Grow()
            {
                stack mut dynamic i32[0 max] values = new();
                values.Reserve(4);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void LawsCannotTryReserveDynamicStorage()
    {
        var result = Compile(
            """
            module Demo

            law bool Grow()
            {
                stack mut dynamic i32[0 max] values = new();
                return values.TryReserve(4);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void LawsCannotMoveLastFromDynamicStorage()
    {
        var result = Compile(
            """
            module Demo

            law i32[0 max] Pop(mut borrow dynamic i32[0 max] values)
            {
                return values.MoveLast();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void LawsCannotMoveAtFromDynamicStorage()
    {
        var result = Compile(
            """
            module Demo

            law i32[0 max] Remove(mut borrow dynamic i32[0 max] values)
            {
                return values.MoveAt(0);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4104");
    }

    [Fact]
    public void DynamicStorageAllowsConcreteDestructibleElementTypes()
    {
        var result = Compile(
            """
            module Demo

            struct Token
            {
                i32[min max] Value;

                drop
                {
                    ;
                }
            }

            fn void Run()
            {
                stack mut dynamic Token values = new(4);
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void LawsCannotCallNonLawFunctions()
    {
        var result = Compile(
            """
            module Demo

            static i32[min max] Counter = 1;

            fn i32[min max] Impure()
            {
                return Counter;
            }

            law i32[min max] PureWrapper()
            {
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

            fn i32[min max] PureAdd(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            law i32[min max] Use()
            {
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

            struct Box
            {
                i32[min max] Value;
            }

            law i32[min max] Bump()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
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

            struct Box
            {
                u32[0 2147483647] Value;

                law retborrow u32[0 2147483647] Get(borrow Box self)
                {
                    return self.Value;
                }
            }

            struct Holder
            {
                Box Inner;

                law retborrow u32[0 2147483647] Get(borrow Holder self)
                {
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

            struct Box
            {
                i32[min max] Value;
            }

            law void Touch(mut borrow Box box)
            {
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

            finite void Loop()
            {
                while infinite (true)
                {
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

            finite void Loop(bool flag)
            {
                for non-deterministic (; flag; )
                {
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

            fn void Loop(bool flag)
            {
                while infinite (flag)
                {
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

            fn void Loop()
            {
                while infinite (true)
                {
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

            fn void Loop()
            {
                while willexit (true)
                {
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

            fn void Loop()
            {
                for willexit (;; )
                {
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

            fn void Loop()
            {
                for willexit (;; )
                {
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

            fn void Run()
            {
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

            fn void Run(i32[min max] value)
            {
                switch (value)
                {
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

            fn void Run(i32[min max] value)
            {
                switch (value)
                {
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

            fn void Run(i32[min max] value)
            {
                while willexit (true)
                {
                    switch (value)
                    {
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

            fn void Run(i32[min max] value)
            {
                while infinite (true)
                {
                    switch (value)
                    {
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

            inline noinline fn void Run()
            {
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

            hot cold fn void Run()
            {
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

            fn void Maybe()
            {
                while infinite (true)
                {
                    break;
                }

                return;
            }

            finite void Outer()
            {
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

            fn i32[min max] Step(i32[min max] value)
            {
                return value + 1;
            }

            finite i32[min max] Outer(i32[min max] value)
            {
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

            finite void Recur()
            {
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

            fn retborrow i32[min max] Bounce(retborrow i32[min max] value);

            fn retborrow i32[min max] Forward(retborrow i32[min max] value)
            {
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

            unsafe ffi fn void Accept(borrow i32[min max] value);

            unsafe fn void Use(borrow i32[min max] value)
            {
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

            fn void Inspect(borrow i32[min max] value)
            {
                return;
            }

            fn retborrow i32[min max] Echo(retborrow i32[min max] value)
            {
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

            const Answer = 42;

            doctrine Numbers
            {
                law u8[0 max] Read()
                {
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

            struct Buffer
            {
                i32[min max] Value;

                drop
                {
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

            struct Buffer
            {
                i32[min max] Value;

                mut drop
                {
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

            struct Buffer
            {
                i32[min max] Value;

                fn void Reset(mut borrow Buffer self)
                {
                    self.Value = 0;
                    return;
                }

                mut drop
                {
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

            struct Buffer
            {
                drop
                {
                    return;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4014");
    }

    [Fact]
    public void BaseListRejectsClassStyleInheritanceFromStruct()
    {
        var result = Compile(
            """
            module Demo

            struct Base
            {
                i32[min max] Value;
            }

            struct Widget : Base
            {
                i32[min max] Other;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3026"
                && diagnostic.Message.Contains("Base", StringComparison.Ordinal)
                && diagnostic.Message.Contains("class-style inheritance", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseListAcceptsConformingTraitImplementation()
    {
        var result = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Width(borrow Self self);
            }

            struct Widget : Drawable
            {
                i32[min max] W;

                finite law i32[min max] Width(borrow Widget self)
                {
                    return self.W;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3026");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3032");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3033");
    }

    [Fact]
    public void BaseListRejectsTraitMethodKindMismatch()
    {
        var result = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Width(borrow Self self);
            }

            struct Widget : Drawable
            {
                i32[min max] W;

                fn i32[min max] Width(borrow Widget self)
                {
                    return self.W;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3033"
                && diagnostic.Message.Contains("finite law", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Width", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseListRejectsTraitMethodParameterTypeMismatch()
    {
        var result = Compile(
            """
            module Demo

            trait Scaler
            {
                finite law i32[min max] Scale(borrow Self self, i32[min max] factor);
            }

            struct Widget : Scaler
            {
                i32[min max] W;

                finite law i32[min max] Scale(borrow Widget self, u8[0 max] factor)
                {
                    return self.W;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3033"
                && diagnostic.Message.Contains("Scale", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseListRejectsMissingTraitMethod()
    {
        var result = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Width(borrow Self self);
            }

            struct Widget : Drawable
            {
                i32[min max] W;
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3032"
                && diagnostic.Message.Contains("Width", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Widget", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseListRejectsMissingTraitMethodFromImportedTrait()
    {
        var result = Compile(
            """
            import Contracts
            module Demo

            struct Widget : Contracts.Drawable
            {
                i32[min max] W;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Contracts", "/virtual/Contracts.stark", IsExternal: false),
                        """
                        module Contracts

                        public trait Drawable
                        {
                            finite law i32[min max] Width(borrow Self self);
                        }
                        """,
                        "/virtual/Contracts.stark"
                    )
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3032"
                && diagnostic.Message.Contains("Contracts.Drawable.Width", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Widget", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseListRejectsImportedTraitMethodSignatureMismatch()
    {
        var result = Compile(
            """
            import Contracts
            module Demo

            struct Widget : Contracts.Drawable
            {
                i32[min max] W;

                finite law u8[0 max] Width(borrow Widget self)
                {
                    return 1;
                }
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "semantic-validate",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Contracts", "/virtual/Contracts.stark", IsExternal: false),
                        """
                        module Contracts

                        public trait Drawable
                        {
                            finite law i32[min max] Width(borrow Self self);
                        }
                        """,
                        "/virtual/Contracts.stark"
                    )
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3033"
                && diagnostic.Message.Contains("Contracts.Drawable.Width", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Widget.Width", StringComparison.Ordinal));
    }

    [Fact]
    public void DynTraitObjectRejectsStaticOnlyTrait()
    {
        var result = Compile(
            """
            module Demo

            trait Speaker
            {
                finite law i32[min max] Speak(borrow Self self);
            }

            finite law i32[min max] AreaOf(borrow dyn Speaker speaker)
            {
                return speaker.Speak();
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3035"
                && diagnostic.Message.Contains("Speaker", StringComparison.Ordinal)
                && diagnostic.Message.Contains("dyn trait", StringComparison.Ordinal));
    }

    [Fact]
    public void DynTraitRejectsNonObjectSafeGenericMethod()
    {
        var result = Compile(
            """
            module Demo

            dyn trait Speaker
            {
                finite law i32[min max] Speak<T>(borrow Self self, borrow T item);
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3036"
                && diagnostic.Message.Contains("Speak", StringComparison.Ordinal)
                && diagnostic.Message.Contains("object-safe", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericConstraintAcceptsConformingTypeArgument()
    {
        var result = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Width(borrow Self self);
            }

            struct Widget : Drawable
            {
                i32[min max] W;

                finite law i32[min max] Width(borrow Widget self)
                {
                    return self.W;
                }
            }

            finite law i32[min max] Use<T>(borrow T value) where T: Drawable
            {
                return 0;
            }

            export fn i32[min max] main()
            {
                stack Widget w = new Widget() { W = 5 };
                return Use(w);
            }
            """,
            new CompilerOptions(StopAfterPassId: "instantiation-ownership"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3034");
    }

    [Fact]
    public void GenericConstraintRejectsNonConformingTypeArgument()
    {
        var result = Compile(
            """
            module Demo

            trait Drawable
            {
                finite law i32[min max] Width(borrow Self self);
            }

            struct Plain
            {
                i32[min max] V;
            }

            finite law i32[min max] Use<T>(borrow T value) where T: Drawable
            {
                return 0;
            }

            export fn i32[min max] main()
            {
                stack Plain p = new Plain() { V = 1 };
                return Use(p);
            }
            """,
            new CompilerOptions(StopAfterPassId: "instantiation-ownership"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3034"
                && diagnostic.Message.Contains("Plain", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Drawable", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseListDoesNotRequireDefaultTraitMethods()
    {
        var result = Compile(
            """
            module Demo

            trait Greeter
            {
                finite law i32[min max] Base(borrow Self self);

                finite law i32[min max] Doubled(borrow Self self)
                {
                    return self.Base() * 2;
                }
            }

            struct Widget : Greeter
            {
                i32[min max] V;

                finite law i32[min max] Base(borrow Widget self)
                {
                    return self.V;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "semantic-validate"));

        // `Doubled` has a default body, so `Widget` need not override it to conform.
        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3032");
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static void AssertDiagnostic(CompilationResult result, string code)
    {
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static bool IsStrictRangeDiagnostic(CompilerDiagnostic diagnostic, string sourceType, string suggestedType)
    {
        return diagnostic.Code == "STK3014"
            && diagnostic.Message.Contains(sourceType, StringComparison.Ordinal)
            && diagnostic.Message.Contains(suggestedType, StringComparison.Ordinal);
    }
}
