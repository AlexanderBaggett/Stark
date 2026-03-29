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
        AssertDiagnostic(result, "STK3002", "rawptr<i32>", "rawmutptr<i32>", "strengthen pointer mutability");
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
        AssertDiagnostic(result, "STK3002", "rawptr<i32>", "i64", "erase readonly pointer provenance");
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
