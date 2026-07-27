using Stark.Compiler;

namespace compiler.Tests;

public sealed class TypeTypingDiagnosticsTests
{
    [Fact]
    public void RangePatternRequiresIntegerTarget()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(bool flag)
            {
                switch (flag)
                {
                    case 0..1:
                        return 1;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Range pattern '0..1' requires an integer target", "bool");
    }

    [Fact]
    public void RangePatternRejectsLowerBoundGreaterThanUpperBound()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(u8[0 10] value)
            {
                switch (value)
                {
                    case 10..0:
                        return 1;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Range pattern '10..0' has lower bound 10 greater than upper bound 0");
    }

    [Fact]
    public void RangePatternRejectsNonOverlappingIntegerDomain()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(u8[0 3] value)
            {
                switch (value)
                {
                    case 10..20:
                        return 1;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Range pattern '10..20' cannot match 'u8[0 3]'", "[0, 3]");
    }

    [Fact]
    public void VarargsModifierRequiresFfiDeclaration()
    {
        var result = Compile(
            """
            module Demo

            public varargs fn void Log(ascii format);
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4119", "uses 'varargs'", "only available for 'ffi' functions", "ffi varargs fn");
    }

    [Fact]
    public void FfiVarargsRejectsArgumentsThatNeedHiddenCPromotion()
    {
        var result = Compile(
            """
            module Demo

            public ffi varargs fn i32[min max] printf(ascii format);

            fn i32[min max] Run(f32 value)
            {
                return printf("%f", value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3009", "Extra argument 2", "f32", "Cast f32 to f64");
    }

    [Fact]
    public void FfiVarargsRejectsStarkTextForPercentSFormatArguments()
    {
        var result = Compile(
            """
            module Demo

            public ffi varargs fn i32[min max] printf(ascii format);

            fn i32[min max] Run(ascii name)
            {
                return printf("name=%s", name);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(
            result,
            "STK3009",
            "C varargs '%s' argument 2",
            "must be rawptr<System.C.c_char> or rawmutptr<System.C.c_char>",
            "System.C.FromAscii");
    }

    [Fact]
    public void FfiVarargsRejectsStarkTextForPercentSConstFormatArguments()
    {
        var result = Compile(
            """
            module Demo

            const ascii Format = "name=%s";

            public ffi varargs fn i32[min max] printf(ascii format);

            fn i32[min max] Run(ascii name)
            {
                return printf(Format, name);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(
            result,
            "STK3009",
            "C varargs '%s' argument 2",
            "must be rawptr<System.C.c_char> or rawmutptr<System.C.c_char>",
            "System.C.FromAscii");
    }

    [Fact]
    public void ConstructorArgumentsAreCheckedAgainstRecordPrimaryShape()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            fn void Run()
            {
                stack Pair pair = new Pair(1, false);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Constructor argument 2", "expects 'i32'", "found 'bool'");
    }

    [Fact]
    public void ConstructorArityMismatchReportsAvailableShapes()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            fn void Run()
            {
                stack Pair pair = new Pair(1);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3009", "does not declare a constructor that accepts 1 argument", "Available constructor arities: 2");
    }

    [Fact]
    public void ExplicitConstructorsSuppressImplicitDefaultConstructor()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                Box(i32[min max] value)
                {
                    self.Value = value;
                }
            }

            fn void Run()
            {
                stack Box box = new();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3009", "does not declare a constructor that accepts 0 arguments", "Available constructor arities: 1");
    }

    [Fact]
    public void TupleLikeEnumConstructorsRejectArityMismatchDuringTypeChecking()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
                Pair(i32[min max], i32[min max]),
            }

            fn void Run()
            {
                stack Token token = Token.Pair(1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3009", "Enum constructor 'Token.Pair' expects 2 arguments but received 1");
    }

    [Fact]
    public void ObjectInitializersRejectDuplicateMembers()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Run()
            {
                stack Box box = new Box()
                {
                    Value = 1, Value = 2
                };
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3006", "Object initializer member 'Value'", "assigned more than once");
    }

    [Fact]
    public void ObjectInitializersRejectMembersAlreadySuppliedByPrimaryConstructor()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            fn void Run()
            {
                stack Pair pair = new Pair(1, 2)
                {
                    Left = 3
                };
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3006", "Object initializer member 'Left'", "already supplied by the constructor");
    }

    [Fact]
    public void ObjectCreationRequiresExplicitFunctionPointerFields()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Worker()
            {
                return 1;
            }

            struct Callback
            {
                i32[min max] Value;
                fnptr<fn i32[min max]()> Entry;
            }

            fn void Run()
            {
                stack Callback missing = new Callback();
                stack Callback partial = new Callback()
                {
                    Value = 2
                };
                stack Callback ok = new Callback()
                {
                    Value = 3, Entry = Worker
                };
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3009", "Field 'Entry'", "must be explicitly initialized", "function pointers cannot be null");
    }

    [Fact]
    public void ArrayInitializersRequireEveryFunctionPointerElement()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Worker()
            {
                return 1;
            }

            fn void Run()
            {
                stack fnptr<fn i32[min max]()>[2] callbacks =
                {
                    Worker
                };
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3009", "Array initializer", "must provide all 2 elements", "function-pointer elements");
    }

    [Fact]
    public void FunctionNamesAreRejectedAsRuntimeValuesDuringTypeChecking()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            fn void Run()
            {
                Add;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3012", "Function 'Add' must be called before its value can be used");
    }

    [Fact]
    public void ExportedFunctionsRejectEnumTypesAtAbiBoundaries()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
                Integer(i32[min max]),
            }

            export fn Token Use(Token token);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Type 'Token'", "depends on enum 'Token'", "cannot cross FFI/export boundaries", "parameter 'token'");
        AssertDiagnostic(result, "STK3008", "Type 'Token'", "depends on enum 'Token'", "cannot cross FFI/export boundaries", "return type of function 'Use'");
    }

    [Fact]
    public void FfiFunctionsRejectEnumTypesAtAbiBoundaries()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
            }

            unsafe ffi fn void Use(Token token);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Type 'Token'", "depends on enum 'Token'", "cannot cross FFI/export boundaries", "parameter 'token'");
    }

    [Fact]
    public void FfiFunctionsRejectTextViewReturnTypes()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi fn ascii CurrentAscii();
            unsafe ffi fn unicode CurrentUnicode();
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "FFI function 'CurrentAscii'", "cannot return text view type 'ascii'", "raw pointer plus explicit length/status");
        AssertDiagnostic(result, "STK3008", "FFI function 'CurrentUnicode'", "cannot return text view type 'unicode'", "raw pointer plus explicit length/status");
    }

    [Fact]
    public void FfiAbiModifiersRejectUnsupportedTargetsDuringCompilation()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(stdcall) fn void Bad();
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => (diagnostic.Code == "STK2111" || diagnostic.Code == "STK3046")
                && diagnostic.Message.Contains("stdcall", StringComparison.Ordinal)
                && diagnostic.Message.Contains("is not supported for target", StringComparison.Ordinal));
    }

    [Fact]
    public void AsmFunctionsRejectUnsupportedParameterAndReturnTypes()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(x86_64) fn bool Broken(ascii text, i32[min max] count)
                in("rdi") text,
                in("rsi") count,
                out("rax") return
            {
                "syscall"
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Asm function 'Broken'", "return type 'bool'");
        AssertDiagnostic(result, "STK3008", "Asm function 'Broken'", "parameter 'text'", "type 'ascii'");
    }

    [Fact]
    public void AsmFunctionsAcceptFloatingPointParametersAndReturns()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(x86_64) fn f64 Identity(f64 value)
                in("xmm0") value,
                out("xmm0") return
            {
                "nop"
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AsmFunctionsAcceptAArch64FloatingPointRegisters()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(aarch64) fn f32 Identity(f32 value)
                in("s0") value,
                out("s0") return
            {
                "nop"
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo("aarch64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AsmFunctionsRejectRegisterClassesThatDoNotMatchValueKinds()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(x86_64) fn f32 Broken(f32 scale, i32[min max] count)
                in("rdi") scale,
                in("xmm1") count,
                out("rax") return
            {
                "nop"
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Asm function 'Broken'", "parameter 'scale'", "register 'rdi'", "general-purpose register", "Floating-point values must use a floating-point register on x86_64");
        AssertDiagnostic(result, "STK3008", "Asm function 'Broken'", "parameter 'count'", "register 'xmm1'", "floating-point register", "Integer and raw-pointer values must use a general-purpose register on x86_64");
        AssertDiagnostic(result, "STK3008", "Asm function 'Broken'", "return value", "register 'rax'", "general-purpose register", "Floating-point values must use a floating-point register on x86_64");
    }

    [Fact]
    public void ExportedFunctionsRejectAggregateTypesThatTransitivelyDependOnEnums()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
                Integer(i32[min max]),
            }

            struct Inner
            {
                Token Value;
            }

            struct Outer
            {
                Inner Current;
            }

            export fn Outer Use(Outer value);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Type 'Outer'", "depends on enum 'Token'", "cannot cross FFI/export boundaries", "parameter 'value'");
        AssertDiagnostic(result, "STK3008", "Type 'Outer'", "depends on enum 'Token'", "cannot cross FFI/export boundaries", "return type of function 'Use'");
    }

    [Fact]
    public void TypeAliasesShareTheUnderlyingOverloadIdentity()
    {
        var result = Compile(
            """
            module Demo

            alias Score = i32[min max];

            fn i32[min max] Parse(i32[min max] value)
            {
                return value;
            }

            fn i32[min max] Parse(Score value)
            {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3006", "Function 'Parse' declares overload 'Parse(i32)' more than once");
    }

    [Fact]
    public void TypeAliasCyclesAreRejected()
    {
        var result = Compile(
            """
            module Demo

            alias A = B;
            alias B = A;

            fn void Use(A value)
            {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3023", "participates in a cycle");
    }

    [Fact]
    public void FfiFunctionsRejectAggregateTypesThatTransitivelyDependOnEnums()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
            }

            struct State
            {
                Token Current;
            }

            unsafe ffi fn void Use(State value);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Type 'State'", "depends on enum 'Token'", "cannot cross FFI/export boundaries", "parameter 'value'");
    }

    [Fact]
    public void GlobalTypesRejectAggregateTypesThatTransitivelyDependOnEnums()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
            }

            struct Inner
            {
                Token Value;
            }

            struct Outer
            {
                Inner Current;
            }

            static Outer Shared = new Outer()
            {
                Current = new Inner()
                {
                    Value = Token.End
                }
            };
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Type 'Outer'", "depends on enum 'Token'", "enum-dependent runtime layout is not valid in a global variable type");
    }

    [Fact]
    public void AggregateSwitchPatternsRejectMoveOnlyFieldCaptures()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                Ascii Text;
            }

            fn i32[min max] Run(Box value)
            {
                switch (value)
                {
                    case Box(var text):
                        return 1;
                    default:
                        return 0;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Field 'Text'", "cannot currently be captured", "scalar and text-view field types");
    }

    [Fact]
    public void FixedArrayListSwitchPatternsRejectWrongLength()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(i32[min max][2] values)
            {
                switch (values)
                {
                    case [1]:
                        return 1;
                    default:
                        return 0;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "expects exactly 2 element subpatterns", "found 1");
    }

    [Fact]
    public void SwitchOrPatternsRejectInconsistentSharedCaptures()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Number(i32[min max]),
                Flag(bool),
                End,
            }

            fn i32[min max] Run(Token token)
            {
                switch (token)
                {
                    case Token.Number(var value) | Token.End:
                        return 1;
                    case Token.Flag(_):
                        return 0;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(
            result,
            "STK3008",
            "Switch labels that share a body must bind the same capture names with the same types",
            "Earlier label binds 'value: i32'",
            "this label binds no captures");
    }

    [Fact]
    public void GuardlessEmptySwitchLabelsPermitInconsistentUnusedCaptures()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Number(i32[min max]),
                End,
            }

            fn i32[min max] Run(Token token)
            {
                switch (token)
                {
                    case Token.Number(var value):
                    case Token.End:
                }

                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Switch labels that share a body", StringComparison.Ordinal));
    }

    [Fact]
    public void SwitchPatternsRejectDuplicateCapturesInOneAlternative()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            fn i32[min max] Run(Pair pair)
            {
                switch (pair)
                {
                    case Pair(var value, var value):
                        return value;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Switch pattern capture 'value' is bound more than once in the same pattern");
    }

    [Fact]
    public void ExhaustiveBoolSwitchRejectsLaterDefaultLabel()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(bool value)
            {
                switch (value)
                {
                    case true:
                        return 1;
                    case false:
                        return 0;
                    default:
                        return 2;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'default' is unreachable", "already exhaustive", "'false'");
        AssertDiagnostic(result, "STK3020", "Switch coverage becomes exhaustive here for 'bool'.");
    }

    [Fact]
    public void AggregateWildcardPatternRejectsLaterSpecificArm()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            fn i32[min max] Run(Pair value)
            {
                switch (value)
                {
                    case Pair(_, _):
                        return 0;
                    case Pair(1, 2):
                        return 1;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'Pair(1,2)' is unreachable", "already exhaustive", "'Pair(_,_)'");
        AssertDiagnostic(result, "STK3020", "Switch coverage becomes exhaustive here for 'Pair'.");
    }

    [Fact]
    public void BroaderAggregatePatternRejectsLaterSpecificArm()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            fn i32[min max] Run(Pair value)
            {
                switch (value)
                {
                    case Pair(1, _):
                        return 0;
                    case Pair(1, 2):
                        return 1;
                    default:
                        return 2;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'Pair(1,2)' is unreachable", "'Pair(1,_)' already covers it");
        AssertDiagnostic(result, "STK3020", "already covers the later label 'Pair(1,2)'");
    }

    [Fact]
    public void NestedAggregatePatternRejectsLaterSpecificArm()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }
            record Outer(Pair Values, i32[min max] Tail)
            {
            }

            fn i32[min max] Run(Outer value)
            {
                switch (value)
                {
                    case Outer(Pair(1, _), _):
                        return 0;
                    case Outer(Pair(1, 2), 3):
                        return 1;
                    default:
                        return 2;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'Outer(Pair(1,2),3)' is unreachable", "'Outer(Pair(1,_),_)' already covers it");
        AssertDiagnostic(result, "STK3020", "already covers the later label 'Outer(Pair(1,2),3)'");
    }

    [Fact]
    public void ExhaustiveEnumCasePatternsRejectLaterDefaultLabel()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
                Integer(i32[min max]),
                Move
                {
                    X: i32[min max], Y: i32[min max]
                },
            }

            fn i32[min max] Run(Token token)
            {
                switch (token)
                {
                    case Token.End:
                        return 0;
                    case Token.Integer(_):
                        return 1;
                    case Token.Move
                    {
                        X: _, Y: _
                    }:
                        return 2;
                    default:
                        return 3;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'default' is unreachable", "already exhaustive", "'Token.Move{X:_,Y:_}'");
        AssertDiagnostic(result, "STK3020", "Switch coverage becomes exhaustive here for 'Token'.");
    }

    [Fact]
    public void BroaderEnumTuplePatternRejectsLaterSpecificArm()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                End,
                Integer(i32[min max]),
            }

            fn i32[min max] Run(Token token)
            {
                switch (token)
                {
                    case Token.Integer(_):
                        return 0;
                    case Token.Integer(1):
                        return 1;
                    default:
                        return 2;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3019", "Switch label 'Token.Integer(1)' is unreachable", "'Token.Integer(_)' already covers it");
        AssertDiagnostic(result, "STK3020", "already covers the later label 'Token.Integer(1)'");
    }

    [Fact]
    public void ArrayInitializerMismatchesUseExpectedActualWording()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max][2] values =
                {
                    1, false
                };
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Array initializer element expects 'i32'", "found 'bool'");
    }

    [Fact]
    public void SliceVariablesCannotUseArrayInitializerSyntax()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max][] view =
                {
                    1, 2, 3
                };
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "fixed-size array target", "i32[]", "Form a slice explicitly from backing storage instead");
    }

    [Fact]
    public void SliceMembersCannotUseArrayInitializerSyntax()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer
            {
                i32[min max][] Values;
            }

            fn void Run()
            {
                stack Buffer buffer =
                {
                    Values =
                    {
                        5, 8
                    }
                };
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "fixed-size array target", "i32[]", "Form a slice explicitly from backing storage instead");
    }

    [Fact]
    public void ReturnMismatchesExplainWhenExplicitConversionIsRequired()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                return 1.5;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Return statement expects 'i32'", "found 'f32'", "explicit conversion is required");
    }

    [Fact]
    public void ImmutableLocalAddressCannotInitializeMutableRawPointer()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] value = 1;
                stack rawmutptr<i32[min max]> ptr = &value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Assignment expects 'rawmutptr<i32>'", "found 'rawptr<i32>'");
    }

    [Fact]
    public void ImmutableLocalCannotBeReassigned()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] value = 1;
                value = 2;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3007", "Cannot assign to immutable local 'value'");
    }

    [Fact]
    public void ImmutableLocalFieldsCannotBeMutated()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                box.Value = 2;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3007", "Cannot assign to immutable local 'box'");
    }

    [Fact]
    public void ImmutableLocalReadonlyPointerCannotBeUpgradedToMutableRawPointer()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] value = 1;
                stack rawmutptr<i32[min max]> ptr = (rawmutptr<i32[min max]>)(&value);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawptr<i32>", "rawmutptr<i32>", "strengthen pointer mutability");
    }

    [Fact]
    public void ConstArrayDerivedReadonlyPointersCannotBeUpgradedToMutableRawPointers()
    {
        var result = Compile(
            """
            module Demo

            const i32[min max][3] Values =
            {
                1, 2, 3
            };

            fn void Run()
            {
                stack rawmutptr<i32[min max]> ptr = (rawmutptr<i32[min max]>)(&Values[0]);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawptr<frozen i32>", "rawmutptr<i32>", "strengthen pointer mutability");
    }

    [Fact]
    public void ConstArrayDerivedReadonlyPointersCannotBeLaunderedThroughIntegers()
    {
        var result = Compile(
            """
            module Demo

            const i32[min max][3] Values =
            {
                1, 2, 3
            };

            fn void Run()
            {
                stack i64[min max] bits = (i64[min max])(&Values[0]);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawptr<frozen i32>", "i64", "erase readonly pointer provenance");
    }

    [Fact]
    public void ConstFieldDerivedReadonlyPointersCannotBeUpgradedToMutableRawPointers()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            const Box Current = new Box()
            {
                Value = 1
            };

            fn void Run()
            {
                stack rawmutptr<i32[min max]> ptr = (rawmutptr<i32[min max]>)(&Current.Value);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawptr<frozen i32>", "rawmutptr<i32>", "strengthen pointer mutability");
    }

    [Fact]
    public void ConstFieldDerivedReadonlyPointersCannotBeLaunderedThroughIntegers()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            const Box Current = new Box()
            {
                Value = 1
            };

            fn void Run()
            {
                stack i64[min max] bits = (i64[min max])(&Current.Value);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawptr<frozen i32>", "i64", "erase readonly pointer provenance");
    }

    [Fact]
    public void FrozenMemberProjectionsCannotBeMutated()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Run(frozen Box box)
            {
                box.Value = 2;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3007", "Cannot mutate member 'Value' through a frozen value");
    }

    [Fact]
    public void FrozenSliceProjectionsRemainReadonly()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(frozen i32[min max][] view)
            {
                view[0] = 4;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3007", "Cannot mutate indexed element through a frozen value");
    }

    [Fact]
    public void FrozenDerivedReadonlyPointersCannotBeUpgradedToMutableRawPointers()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Run(frozen Box box)
            {
                stack rawmutptr<i32[min max]> ptr = (rawmutptr<i32[min max]>)(&box.Value);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawptr<frozen i32>", "rawmutptr<i32>", "strengthen pointer mutability");
    }

    [Fact]
    public void FrozenDerivedReadonlyPointersCannotBeLaunderedThroughIntegers()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Run(frozen Box box)
            {
                stack i64[min max] bits = (i64[min max])(&box.Value);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawptr<frozen i32>", "i64", "erase readonly pointer provenance");
    }

    [Fact]
    public void FrozenReachableRawPointerFieldsCannotLeakMutableAliases()
    {
        var result = Compile(
            """
            module Demo

            struct PtrBox
            {
                rawmutptr<i32[min max]> Ptr;
            }

            fn void Run(frozen PtrBox box)
            {
                stack rawmutptr<i32[min max]> leaked = box.Ptr;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawmutptr<i32>", "rawptr<frozen i32>");
    }

    [Fact]
    public void ConstRawPointerParametersCannotMutatePointees()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Run(const rawmutptr<i32[min max]> ptr)
            {
                *ptr = 1;
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3007", "Cannot mutate dereferenced value through a frozen value");
    }

    [Fact]
    public void ConstParameterReachableRawPointerFieldsCannotLeakMutableAliases()
    {
        var result = Compile(
            """
            module Demo

            struct PtrBox
            {
                rawmutptr<i32[min max]> Ptr;
            }

            fn void Run(const PtrBox box)
            {
                stack rawmutptr<i32[min max]> leaked = box.Ptr;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawmutptr<i32>", "rawptr<frozen i32>");
    }

    [Fact]
    public void ConstParameterCallsRequireConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Inspect(const Box box)
            {
                return;
            }

            fn void Run(Box box)
            {
                Inspect(box);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3031", "must have const provenance", "box");
    }

    [Fact]
    public void FrozenParameterCallsDoNotSatisfyConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Inspect(const Box box)
            {
                return;
            }

            fn void Run(frozen Box box)
            {
                Inspect(box);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3031", "must have const provenance", "box");
    }

    [Fact]
    public void FrozenParameterProjectionsDoNotSatisfyConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            struct Inner
            {
                i32[min max] Value;
            }

            struct Outer
            {
                Inner Child;
            }

            fn void Inspect(const Inner inner)
            {
                return;
            }

            fn void Run(frozen Outer outer)
            {
                Inspect(outer.Child);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3031", "must have const provenance");
    }

    [Fact]
    public void FrozenRawPointerFieldsDoNotSatisfyConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            struct PtrBox
            {
                rawmutptr<i32[min max]> Ptr;
            }

            unsafe fn void Inspect(const rawmutptr<i32[min max]> ptr)
            {
                return;
            }

            fn void Run(frozen PtrBox box)
            {
                Inspect(box.Ptr);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3031", "must have const provenance");
    }

    [Fact]
    public void RawSlicesFromFrozenPointersDoNotSatisfyConstProvenance()
    {
        var result = Compile(
            """
            module Demo

            fn void Inspect(const i32[min max][] view)
            {
                return;
            }

            unsafe fn void Run(
                frozen rawmutptr<i32[min max]>[count] pointer,
                u8[1 10] count)
                {
                    unsafe
                {
                    stack frozen i32[min max][] view = slice(pointer, count);
                    Inspect(view);
                }
                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3031", "must have const provenance", "view");
    }

    [Fact]
    public void MemberAssignmentsReportExpectedAndActualTypes()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Run()
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                box.Value = false;
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Assignment to member 'Value'", "expects 'i32'", "found 'bool'");
    }

    [Fact]
    public void CallMismatchesReportArgumentPositions()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Echo(i32[min max] value)
            {
                return value;
            }

            fn i32[min max] Run()
            {
                return Echo(false);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Argument 1 for 'Echo'", "expects 'i32'", "found 'bool'");
    }

    [Fact]
    public void ExplicitArithmeticOperatorsRequireIntegerOperands()
    {
        var result = Compile(
            """
            module Demo

            fn f32 Run()
            {
                return 1.0 +% 2.0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Operator '+%'", "requires integer operands");
    }

    [Fact]
    public void RuntimeAsciiAndUnicodeCastsStillRequireOwningTextStorage()
    {
        var result = Compile(
            """
            module Demo

            fn unicode Widen(ascii text)
            {
                return (unicode)text;
            }

            fn ascii Narrow(unicode text)
            {
                return (ascii)text;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Explicit conversion from 'ascii' to 'unicode' requires a compile-time text constant", "System.Text APIs");
        AssertDiagnostic(result, "STK3002", "Explicit conversion from 'unicode' to 'ascii' requires a compile-time text constant", "System.Text APIs");
    }

    [Fact]
    public void RuntimeUnicodeToAsciiCastsStillRequireCompileTimeTextConstants()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Run()
            {
                stack unicode text = (unicode)"\u03B1";
                return (ascii)text;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Explicit conversion from 'unicode' to 'ascii' requires a compile-time text constant", "System.Text APIs");
    }

    [Fact]
    public void RuntimeTextConcatenationExplainsStorageRequirement()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Join(ascii left)
            {
                return left + "!";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Only compile-time text constants can use '+'", "System.Text.TryConcatAscii");
    }

    [Fact]
    public void RuntimeTextBufferConcatenationExplainsFixedCapacityRequirement()
    {
        var result = Compile(
            """
            module Demo

            fn void Join(Ascii left, Ascii right)
            {
                stack Ascii combined = left + right;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "need a destination capacity", "stack Ascii combined[4096]");
    }

    [Fact]
    public void FixedTextStorageCapacityIsOnlyForStackTextBuffers()
    {
        var result = Compile(
            """
            module Demo

            fn void Bad(u64[0 2 ** 63 - 1] size, Ascii left, Ascii right)
            {
                stack i32[min max] number[4] = 0;
                heap Ascii heapText[16] = left + right;
                stack Ascii dynamicText[size] = left + right;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "only for stack Ascii or Unicode", "number");
        AssertDiagnostic(result, "STK3002", "must use stack storage", "heapText");
        AssertDiagnostic(result, "STK3002", "capacity Stark can know at compile time", "dynamicText");
    }

    [Fact]
    public void RuntimeInterpolatedTextExplainsStorageRequirement()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Label(i32[min max] score)
            {
                return $"Score: {score}";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Interpolated text with runtime value 'score' needs caller-owned storage", "fixed-capacity buffer");
    }

    [Fact]
    public void FixedCapacityInterpolatedTextRequiresKnownFormatter()
    {
        var result = Compile(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);

            unsafe fn Ascii Label(rawptr<i8[min max]> pointer)
            {
                stack Ascii label[64] = $"Pointer: {pointer}";
                return label;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "does not know how to format 'rawptr<i8>'", "Convert the value to text first");
    }

    [Fact]
    public void VoidReturningCallsCannotBeComparedAsValues()
    {
        var result = Compile(
            """
            module Demo

            fn void A()
            {
                return;
            }

            fn bool Run(bool flag)
            {
                return (flag ? A() : A()) == (flag ? A() : A());
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Operator '==' cannot compare 'void' and 'void'");
    }

    [Fact]
    public void TextAccessRejectsMoreThanTwoIndices()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Run(ascii text)
            {
                return text[1, 2, 3];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Text indexing currently supports exactly one integer index or two integer expressions: start and length");
    }

    [Fact]
    public void DynamicStorageAccessRejectsMoreThanTwoIndices()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                stack mut dynamic i32[min max] values = new();
                return values[0, 1, 2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Dynamic storage indexing supports either one integer index or two integer expressions: start and count");
    }

    [Fact]
    public void EmptyIndexAccessProducesATypeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                stack i32[min max][2] values =
                {
                    1, 2
                };
                values[];
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Index access requires at least one index expression");
    }

    [Fact]
    public void CallingANonFunctionMemberIncludesMemberContext()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                return box.Value();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "member 'Value' of type 'i32'", "not callable");
    }

    [Fact]
    public void IndexingANonIndexableMemberIncludesMemberContext()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                return box.Value[0];
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3010", "member 'Value' of type 'i32'", "not indexable");
    }

    [Fact]
    public void AddressOfRequiresAnAddressableOperand()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn rawptr<i32[min max]> Run()
            {
                return &(1 + 2);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Operator '&' requires an addressable value");
    }

    [Fact]
    public void DereferenceRequiresARawPointerOperand()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                return *1;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Operator '*'", "requires a raw pointer operand");
    }

    [Fact]
    public void DoctrinesCannotBeUsedAsRuntimeValueTypes()
    {
        var result = Compile(
            """
            module Demo

            public doctrine Numbers
            {
                law i32[min max] Zero();
            }

            struct Holder
            {
                Numbers Laws;
            }

            static Numbers Current;

            fn Numbers Echo(Numbers value)
            {
                return value;
            }

            fn void Run()
            {
                stack Numbers local = new Numbers();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3013", "field 'Laws' in type 'Holder'");
        AssertDiagnostic(result, "STK3013", "a global variable type");
        AssertDiagnostic(result, "STK3013", "parameter 'value'");
        AssertDiagnostic(result, "STK3013", "the return type of function 'Echo'");
        AssertDiagnostic(result, "STK3013", "a local variable type");
        AssertDiagnostic(result, "STK3013", "Cannot create an instance of compile-time-only doctrine 'Numbers'");
        AssertDiagnostic(result, "STK3013", "no runtime dispatch values for traits or doctrines");
    }

    [Fact]
    public void TraitsCannotBeUsedAsRuntimeValueTypes()
    {
        var result = Compile(
            """
            module Demo

            public trait Comparable
            {
                law i32[min max] Compare(i32[min max] other);
            }

            struct Holder
            {
                Comparable Rules;
            }

            static Comparable Current;

            fn Comparable Echo(Comparable value)
            {
                return value;
            }

            fn void Run()
            {
                stack Comparable local = new Comparable();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3013", "field 'Rules' in type 'Holder'");
        AssertDiagnostic(result, "STK3013", "a global variable type");
        AssertDiagnostic(result, "STK3013", "parameter 'value'");
        AssertDiagnostic(result, "STK3013", "the return type of function 'Echo'");
        AssertDiagnostic(result, "STK3013", "a local variable type");
        AssertDiagnostic(result, "STK3013", "Cannot create an instance of compile-time-only trait 'Comparable'");
        AssertDiagnostic(result, "STK3013", "no runtime dispatch values for traits or doctrines");
    }

    [Fact]
    public void TraitMethodsCannotBeCalledDirectly()
    {
        var result = Compile(
            """
            module Demo

            public trait Comparable
            {
                law i32[min max] Compare(i32[min max] other);
            }

            fn i32[min max] Run()
            {
                return Comparable.Compare(1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3013", "Trait method 'Comparable.Compare'", "cannot be called directly");
    }

    [Fact]
    public void OverloadResolutionReportsNoMatchingCandidates()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Convert(i32[min max] value)
            {
                return value;
            }

            fn i32[min max] Convert(ascii value)
            {
                return 0;
            }

            fn i32[min max] Run()
            {
                return Convert(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3021", "No overload of 'Convert' matches argument types (bool)");
        AssertDiagnostic(result, "STK3021", "Convert(i32)");
        AssertDiagnostic(result, "STK3021", "Convert(ascii)");
    }

    [Fact]
    public void GenericCallsWithoutInferableTypeArgumentsReportNoMatchingCandidates()
    {
        var result = Compile(
            """
            module Demo

            fn T Make<T>();

            fn i32[min max] Run()
            {
                return Make();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3021", "No overload of 'Make' matches argument types ()");
        AssertDiagnostic(result, "STK3021", "Make<T>()");
    }

    [Fact]
    public void OverloadResolutionReportsAmbiguousCalls()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Measure(f32 value)
            {
                return 1;
            }

            fn i32[min max] Measure(f64 value)
            {
                return 2;
            }

            fn i32[min max] Run()
            {
                stack i32[min max] value = 1;
                return Measure(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3022", "Call to overloaded function 'Measure' is ambiguous for argument types (i32)");
        AssertDiagnostic(result, "STK3022", "Measure(f32)");
        AssertDiagnostic(result, "STK3022", "Measure(f64)");
    }

    [Fact]
    public void MemberOverloadResolutionReportsNoMatchingCandidates()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                finite law i32[min max] Add(borrow Box self, i32[min max] amount)
                {
                    return self.Value + amount;
                }

                finite law i32[min max] Add(borrow Box self, ascii label)
                {
                    return self.Value;
                }
            }

            fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                return box.Add(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3021", "No overload of 'Box.Add' matches argument types (bool)");
        AssertDiagnostic(result, "STK3021", "Box.Add(borrow Box, i32)");
        AssertDiagnostic(result, "STK3021", "Box.Add(borrow Box, ascii)");
    }

    [Fact]
    public void MemberOverloadResolutionReportsAmbiguousCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                finite law i32[min max] Measure(borrow Box self, f32 value)
                {
                    return self.Value;
                }

                finite law i32[min max] Measure(borrow Box self, f64 value)
                {
                    return self.Value;
                }
            }

            fn i32[min max] Run()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                stack i32[min max] value = 1;
                return box.Measure(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3022", "Call to overloaded function 'Box.Measure' is ambiguous for argument types (i32)");
        AssertDiagnostic(result, "STK3022", "Box.Measure(borrow Box, f32)");
        AssertDiagnostic(result, "STK3022", "Box.Measure(borrow Box, f64)");
    }

    [Fact]
    public void VoidCallsUsedAsValuesFailBeforeMirLowering()
    {
        var result = Compile(
            """
            module Demo

            fn void Touch()
            {
                return;
            }

            struct Box
            {
                i32[min max] Value;

                fn void Clear(borrow Box self)
                {
                    return;
                }
            }

            fn i32[min max] Direct()
            {
                return Touch();
            }

            fn i32[min max] Member()
            {
                stack Box box = new Box()
                {
                    Value = 1
                };
                return box.Clear();
            }

            fn i32[min max] Indirect()
            {
                stack fnptr<fn void()> callback = Touch;
                return callback();
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            3,
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Return statement expects 'i32'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("found 'void'", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Logs, IsLoweringOrBackendFallbackLog);
    }

    [Fact]
    public void SemanticErrorShapedLoweringFallbackCasesFailBeforeMirLowering()
    {
        var cases = new (string Name, string Source, string DiagnosticCode, string[] MessageFragments)[]
        {
            (
                "constructor return value",
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;

                    Box(i32[min max] value)
                    {
                        return value;
                    }
                }
                """,
                "STK3002",
                ["Constructor bodies cannot return a value"]),
            (
                "direct call arity",
                """
                module Demo

                fn i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }

                fn i32[min max] Run()
                {
                    return Add(1);
                }
                """,
                "STK3009",
                ["Function 'Add'", "expects 2 arguments", "received 1"]),
            (
                "member call arity",
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;

                    finite law i32[min max] Add(borrow Box self, i32[min max] amount)
                    {
                        return self.Value + amount;
                    }
                }

                fn i32[min max] Run()
                {
                    stack Box box = new Box()
                    {
                        Value = 1
                    };
                    return box.Add();
                }
                """,
                "STK3009",
                ["Function 'Box.Add'", "expects 1 arguments", "received 0"]),
            (
                "void direct call as value",
                """
                module Demo

                fn void Touch()
                {
                    return;
                }

                fn i32[min max] Run()
                {
                    return Touch();
                }
                """,
                "STK3002",
                ["Return statement expects 'i32'", "found 'void'"]),
            (
                "void member call as value",
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;

                    fn void Clear(borrow Box self)
                    {
                        return;
                    }
                }

                fn i32[min max] Run()
                {
                    stack Box box = new Box()
                    {
                        Value = 1
                    };
                    return box.Clear();
                }
                """,
                "STK3002",
                ["Return statement expects 'i32'", "found 'void'"]),
            (
                "void indirect call as value",
                """
                module Demo

                fn void Touch()
                {
                    return;
                }

                fn i32[min max] Run()
                {
                    stack fnptr<fn void()> callback = Touch;
                    return callback();
                }
                """,
                "STK3002",
                ["Return statement expects 'i32'", "found 'void'"]),
            (
                "member call binding",
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;

                    finite law i32[min max] Add(borrow Box self, i32[min max] amount)
                    {
                        return self.Value + amount;
                    }

                    finite law i32[min max] Add(borrow Box self, ascii label)
                    {
                        return self.Value;
                    }
                }

                fn i32[min max] Run()
                {
                    stack Box box = new Box()
                    {
                        Value = 1
                    };
                    return box.Add(true);
                }
                """,
                "STK3021",
                ["No overload of 'Box.Add' matches argument types (bool)"]),
            (
                "raw slice arity",
                """
                module Demo

                unsafe fn void Run(rawptr<i32[min max]> pointer)
                {
                    unsafe
                    {
                        stack i32[min max][] view = slice(pointer);
                    }
                }
                """,
                "STK3009",
                ["Raw slice construction expects 2 arguments", "received 1"]),
            (
                "index non-integer operand",
                """
                module Demo

                fn i32[min max] Run(bool flag)
                {
                    stack i32[min max][2] values =
                    {
                        1, 2
                    };
                    return values[flag];
                }
                """,
                "STK3002",
                ["Index access", "expects an integer index", "bool"]),
            (
                "empty index arity",
                """
                module Demo

                fn i32[min max] Run()
                {
                    stack i32[min max][2] values =
                    {
                        1, 2
                    };
                    values[];
                    return 0;
                }
                """,
                "STK3002",
                ["Index access requires at least one index expression"]),
            (
                "dynamic storage index arity",
                """
                module Demo

                fn i32[min max] Run()
                {
                    stack mut dynamic i32[min max] values = new();
                    return values[0, 1, 2];
                }
                """,
                "STK3008",
                ["Dynamic storage indexing supports either one integer index or two integer expressions"]),
            (
                "text index arity",
                """
                module Demo

                fn ascii Run(ascii text)
                {
                    return text[1, 2, 3];
                }
                """,
                "STK3008",
                ["Text indexing currently supports exactly one integer index or two integer expressions"]),
            (
                "dynamic storage operation shape",
                """
                module Demo

                fn void Run()
                {
                    stack mut dynamic i32[min max] values = new();
                    values.Reserve(true);
                }
                """,
                "STK3002",
                ["Dynamic storage Reserve", "additional capacity", "must be an integer", "bool"]),
            (
                "dynamic storage MoveAt arity",
                """
                module Demo

                fn void Run()
                {
                    stack mut dynamic i32[min max] values = new();
                    values.MoveAt();
                }
                """,
                "STK3009",
                ["Dynamic storage MoveAt", "expects one index argument", "received 0"]),
            (
                "dynamic storage non-addressable receiver",
                """
                module Demo

                fn dynamic i32[min max] Make()
                {
                    return new();
                }

                fn void Run()
                {
                    Make().MoveLast();
                }
                """,
                "STK3007",
                ["Dynamic storage MoveLast", "requires a mutable addressable dynamic owner"]),
            (
                "dynamic storage creation capacity type",
                """
                module Demo

                fn void Run()
                {
                    stack mut dynamic i32[min max] values = new(true);
                }
                """,
                "STK3002",
                ["Dynamic storage capacity", "must be an integer", "bool"]),
            (
                "dynamic storage creation object initializer",
                """
                module Demo

                fn void Run()
                {
                    stack mut dynamic i32[min max] values = new()
                    {
                        Length = 1
                    };
                }
                """,
                "STK3008",
                ["Dynamic storage creation", "does not support object initializers", "new(capacity)"]),
            (
                "fixed text buffer initializer shape",
                """
                module Demo

                fn void Run(Ascii left)
                {
                    stack Ascii text[16] =
                    {
                        left
                    };
                }
                """,
                "STK3002",
                ["Fixed text buffer 'text'", "needs a text-building initializer"]),
            (
                "function as value",
                """
                module Demo

                fn i32[min max] Add(i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }

                fn void Run()
                {
                    Add;
                }
                """,
                "STK3012",
                ["Function 'Add' must be called before its value can be used"]),
            (
                "lambda without function pointer target",
                """
                module Demo

                fn void Run()
                {
                    (i32[min max] value) => value + 1;
                }
                """,
                "STK3008",
                ["Lambda expressions require an explicit function-pointer or closure target type"]),
            (
                "capturing lambda cannot convert to function pointer",
                """
                module Demo

                fn i32[min max] Apply(fnptr<fn i32[min max](i32[min max])> op)
                {
                    return op(41);
                }

                fn i32[min max] Run()
                {
                    stack i32[min max] offset = 1;
                    return Apply(capture(copy offset) (i32[min max] value) => value + offset);
                }
                """,
                "STK3008",
                ["lambda converted to 'fnptr<...>' cannot capture local state", "function pointers do not carry closure storage"]),
            (
                "runtime interpolated text without caller-owned storage",
                """
                module Demo

                fn ascii Run(i32[min max] score)
                {
                    return $"Score: {score}";
                }
                """,
                "STK3002",
                ["Interpolated text with runtime value 'score' needs caller-owned storage", "fixed-capacity buffer"]),
            (
                "invalid switch domain",
                """
                module Demo

                fn void Run()
                {
                    stack fnptr<fn void()> values = null;
                    switch (values)
                    {
                        default:
                            return;
                    }
                }
                """,
                "STK3008",
                ["Switch expression type 'fnptr<fn void()>'", "not a valid switch domain"]),
            (
                "non-concrete type layout expression",
                """
                module Demo

                trait Marker
                {
                }

                fn i64[0 max] Run()
                {
                    return sizeof(Marker);
                }
                """,
                "STK3008",
                ["sizeof requires a concrete runtime layout", "Marker"]),
            (
                "ffi text view return",
                """
                module Demo

                unsafe ffi fn ascii CurrentName();
                """,
                "STK3008",
                ["FFI function 'CurrentName'", "cannot return text view type 'ascii'", "raw pointer plus explicit length/status"]),
            (
                "unresolved named operand",
                """
                module Demo

                fn i32[min max] Run()
                {
                    return Missing;
                }
                """,
                "STK3003",
                ["Unknown symbol 'Missing'"])
        };

        foreach (var (name, source, diagnosticCode, messageFragments) in cases)
        {
            var result = Compile(source);

            Assert.False(result.Succeeded, name);
            AssertDiagnostic(result, diagnosticCode, messageFragments);
            Assert.DoesNotContain(result.Logs, IsLoweringOrBackendFallbackLog);
        }
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static bool IsLoweringOrBackendFallbackLog(CompilerLogEntry log)
    {
        return log.EventId is
            "unsupported-lowering" or
            "llvm-body-fallback" or
            "llvm-asm-fallback" or
            "llvm-body-pending" or
            "missing-function-body";
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
