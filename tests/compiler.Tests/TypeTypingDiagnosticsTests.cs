using Stark.Compiler;

namespace compiler.Tests;

public sealed class TypeTypingDiagnosticsTests
{
    [Fact]
    public void ConstructorArgumentsAreCheckedAgainstRecordPrimaryShape()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32 Left, i32 Right) { }

            fn void Run() {
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

            record Pair(i32 Left, i32 Right) { }

            fn void Run() {
                stack Pair pair = new Pair(1);
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3009", "does not declare a constructor that accepts 1 argument", "Available constructor arities: 2");
    }

    [Fact]
    public void TupleLikeEnumConstructorsRejectArityMismatchDuringTypeChecking()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Pair(i32, i32),
            }

            fn void Run() {
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

            struct Box {
                i32 Value;
            }

            fn void Run() {
                stack Box box = new Box() { Value = 1, Value = 2 };
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

            record Pair(i32 Left, i32 Right) { }

            fn void Run() {
                stack Pair pair = new Pair(1, 2) { Left = 3 };
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3006", "Object initializer member 'Left'", "already supplied by the constructor");
    }

    [Fact]
    public void FunctionNamesAreRejectedAsRuntimeValuesDuringTypeChecking()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Add(i32 left, i32 right) {
                return left + right;
            }

            fn void Run() {
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

            enum Token {
                End,
                Integer(i32),
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

            enum Token {
                End,
            }

            ffi fn void Use(Token token);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Type 'Token'", "depends on enum 'Token'", "cannot cross FFI/export boundaries", "parameter 'token'");
    }

    [Fact]
    public void AsmFunctionsRejectUnsupportedParameterAndReturnTypes()
    {
        var result = Compile(
            """
            module Demo

            public ffi asm(x86_64) fn bool Broken(ascii text, i32 count)
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

            public ffi asm(x86_64) fn f64 Identity(f64 value)
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

            public ffi asm(aarch64) fn f32 Identity(f32 value)
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

            public ffi asm(x86_64) fn f32 Broken(f32 scale, i32 count)
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

            enum Token {
                End,
                Integer(i32),
            }

            struct Inner {
                Token Value;
            }

            struct Outer {
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

            alias Score = i32;

            fn i32 Parse(i32 value) {
                return value;
            }

            fn i32 Parse(Score value) {
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

            fn void Use(A value) {
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

            enum Token {
                End,
            }

            struct State {
                Token Current;
            }

            ffi fn void Use(State value);
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

            enum Token {
                End,
            }

            struct Inner {
                Token Value;
            }

            struct Outer {
                Inner Current;
            }

            static Outer Shared = new Outer() { Current = new Inner() { Value = Token.End } };
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Type 'Outer'", "depends on enum 'Token'", "not yet supported in a global variable type");
    }

    [Fact]
    public void AggregateSwitchPatternsRejectMoveOnlyFieldCaptures()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                ascii Text;
            }

            fn i32 Run(Box value) {
                switch (value) {
                    case Box(var text):
                        return 1;
                    default:
                        return 0;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Field 'Text'", "cannot currently be captured", "scalar, non-owning field types");
    }

    [Fact]
    public void ExhaustiveBoolSwitchRejectsLaterDefaultLabel()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(bool value) {
                switch (value) {
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

            record Pair(i32 Left, i32 Right) { }

            fn i32 Run(Pair value) {
                switch (value) {
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

            record Pair(i32 Left, i32 Right) { }

            fn i32 Run(Pair value) {
                switch (value) {
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

            record Pair(i32 Left, i32 Right) { }
            record Outer(Pair Values, i32 Tail) { }

            fn i32 Run(Outer value) {
                switch (value) {
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

            enum Token {
                End,
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            fn i32 Run(Token token) {
                switch (token) {
                    case Token.End:
                        return 0;
                    case Token.Integer(_):
                        return 1;
                    case Token.Move { X: _, Y: _ }:
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

            enum Token {
                End,
                Integer(i32),
            }

            fn i32 Run(Token token) {
                switch (token) {
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

            fn void Run() {
                stack i32[2] values = { 1, false };
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

            fn void Run() {
                stack i32[] view = { 1, 2, 3 };
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

            struct Buffer {
                i32[] Values;
            }

            fn void Run() {
                stack Buffer buffer = { Values = { 5, 8 } };
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

            fn i32 Run() {
                return 1.5;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Return statement expects 'i32'", "found 'f32'", "explicit conversion is required");
    }

    [Fact]
    public void ConstArrayDerivedReadonlyPointersCannotBeUpgradedToMutableRawPointers()
    {
        var result = Compile(
            """
            module Demo

            const i32[3] Values = { 1, 2, 3 };

            fn void Run() {
                stack rawmutptr<i32> ptr = (rawmutptr<i32>)(&Values[0]);
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

            const i32[3] Values = { 1, 2, 3 };

            fn void Run() {
                stack i64 bits = (i64)(&Values[0]);
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

            struct Box {
                i32 Value;
            }

            const Box Current = new Box() { Value = 1 };

            fn void Run() {
                stack rawmutptr<i32> ptr = (rawmutptr<i32>)(&Current.Value);
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

            struct Box {
                i32 Value;
            }

            const Box Current = new Box() { Value = 1 };

            fn void Run() {
                stack i64 bits = (i64)(&Current.Value);
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

            struct Box {
                i32 Value;
            }

            fn void Run(frozen Box box) {
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

            fn void Run(frozen i32[] view) {
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

            struct Box {
                i32 Value;
            }

            fn void Run(frozen Box box) {
                stack rawmutptr<i32> ptr = (rawmutptr<i32>)(&box.Value);
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

            struct Box {
                i32 Value;
            }

            fn void Run(frozen Box box) {
                stack i64 bits = (i64)(&box.Value);
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

            struct PtrBox {
                rawmutptr<i32> Ptr;
            }

            fn void Run(frozen PtrBox box) {
                stack rawmutptr<i32> leaked = box.Ptr;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "rawmutptr<i32>", "rawptr<frozen i32>");
    }

    [Fact]
    public void MemberAssignmentsReportExpectedAndActualTypes()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };
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

            fn i32 Echo(i32 value) {
                return value;
            }

            fn i32 Run() {
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

            fn f32 Run() {
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

            fn unicode Widen(ascii text) {
                return (unicode)text;
            }

            fn ascii Narrow(unicode text) {
                return (ascii)text;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Explicit conversion from 'ascii' to 'unicode' is not supported", "compile-time text constant");
        AssertDiagnostic(result, "STK3002", "Explicit conversion from 'unicode' to 'ascii' is not supported", "compile-time text constant");
    }

    [Fact]
    public void RuntimeUnicodeToAsciiCastsStillRequireCompileTimeTextConstants()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Run() {
                stack unicode text = (unicode)"\u03B1";
                return (ascii)text;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3002", "Explicit conversion from 'unicode' to 'ascii' is not supported", "compile-time text constant");
    }

    [Fact]
    public void VoidReturningCallsCannotBeComparedAsValues()
    {
        var result = Compile(
            """
            module Demo

            fn void A() {
                return;
            }

            fn bool Run(bool flag) {
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

            fn ascii Run(ascii text) {
                return text[1, 2, 3];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3008", "Text indexing currently supports exactly one integer index or two integer expressions: start and length");
    }

    [Fact]
    public void EmptyIndexAccessProducesATypeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack i32[2] values = { 1, 2 };
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

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack Box box = new Box() { Value = 1 };
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

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack Box box = new Box() { Value = 1 };
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

            fn rawptr<i32> Run() {
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

            fn i32 Run() {
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

            public doctrine Numbers {
                law i32 Zero();
            }

            struct Holder {
                Numbers Laws;
            }

            static Numbers Current;

            fn Numbers Echo(Numbers value) {
                return value;
            }

            fn void Run() {
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

            public trait Comparable {
                law i32 Compare(i32 other);
            }

            struct Holder {
                Comparable Rules;
            }

            static Comparable Current;

            fn Comparable Echo(Comparable value) {
                return value;
            }

            fn void Run() {
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

            public trait Comparable {
                law i32 Compare(i32 other);
            }

            fn i32 Run() {
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

            fn i32 Convert(i32 value) {
                return value;
            }

            fn i32 Convert(ascii value) {
                return 0;
            }

            fn i32 Run() {
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

            fn i32 Run() {
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

            fn i32 Measure(f32 value) {
                return 1;
            }

            fn i32 Measure(f64 value) {
                return 2;
            }

            fn i32 Run() {
                stack i32 value = 1;
                return Measure(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK3022", "Call to overloaded function 'Measure' is ambiguous for argument types (i32)");
        AssertDiagnostic(result, "STK3022", "Measure(f32)");
        AssertDiagnostic(result, "STK3022", "Measure(f64)");
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
