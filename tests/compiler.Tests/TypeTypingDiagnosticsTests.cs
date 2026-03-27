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
