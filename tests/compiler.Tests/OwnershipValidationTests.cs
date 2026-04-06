using Stark.Compiler;

namespace compiler.Tests;

public sealed class OwnershipValidationTests
{
    [Fact]
    public void HeapOwnedValuesAreDroppedAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            finite i32 Run() {
                heap Box box = new Box() { Value = 1 };
                return 1;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void MovingOwnedLocalMakesLaterUseInvalid()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            finite law void Consume(Box value) {
                return;
            }

            finite law i32 Run() {
                stack Box box = new Box() { Value = 1 };
                Consume(box);
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("was moved here", StringComparison.Ordinal));
    }

    [Fact]
    public void MoveDiagnosticsAreNotDuplicatedForTheSameUse()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            finite law void Consume(Box value) {
                return;
            }

            finite law i32 Run() {
                stack Box box = new Box() { Value = 1 };
                Consume(box);
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            1,
            result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Message.Contains("Move error", StringComparison.Ordinal)));
        Assert.Equal(
            1,
            result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("was moved here", StringComparison.Ordinal)));
    }

    [Fact]
    public void ValueReceiverMethodCallsMoveTheReceiver()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;

                finite law void Consume(Box box) {
                    return;
                }
            }

            finite law i32 Run() {
                stack Box box = new Box() { Value = 1 };
                box.Consume();
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("was moved here", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyValuesRemainUsableAfterAssignment()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run() {
                stack i32 x = 1;
                stack i32 y = x;
                return x + y;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.DoesNotContain("x", ownership.Functions["Run"].Moves);
    }

    [Fact]
    public void ReassigningOwnedLocalDropsPreviousValue()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            finite law i32 Run() {
                stack mut Box box = new Box() { Value = 1 };
                box = new Box() { Value = 2 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void ConditionalMoveMakesLaterUseInvalid()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            finite law void Consume(Box value) {
                return;
            }

            finite law i32 Run() {
                stack Box box = new Box() { Value = 1 };
                if (true) {
                    Consume(box);
                }

                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200");
    }

    [Fact]
    public void ReturningOwnedLocalMovesItOutInsteadOfDroppingIt()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            finite law Box Make() {
                stack Box box = new Box() { Value = 1 };
                return box;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Make"].Moves);
        Assert.DoesNotContain("box", ownership.Functions["Make"].ImplicitDrops);
    }

    [Fact]
    public void MovingOutOfATopLevelFieldIsAllowed()
    {
        var result = Compile(
            """
            module Demo

            struct Container {
                ascii Name;
                ascii Label;
            }

            finite law ascii Run() {
                stack Container value = new Container() { Name = "hi", Label = "there" };
                return value.Name;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("value.Name", ownership.Functions["Run"].Moves);
        Assert.Contains("value", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void MovingOutOfANestedFieldIsRejected()
    {
        var result = Compile(
            """
            module Demo

            struct NameBox {
                ascii Value;
            }

            struct Container {
                NameBox Name;
            }

            finite law ascii Run() {
                stack Container value = new Container() { Name = new NameBox() { Value = "hi" } };
                return value.Name.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4203", "Cannot move out of field or indexed place");
    }

    [Fact]
    public void DoctrineCallsParticipateInOwnershipFlow()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            doctrine Sink {
                finite law void Consume(Box value) {
                    return;
                }
            }

            finite law i32 Run() {
                stack Box box = new Box() { Value = 1 };
                Sink.Consume(box);
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "was moved and must be reinitialized");
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }

    private static OwnershipValidationModel GetOwnership(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OwnershipValidation, out OwnershipValidationModel? ownership));
        Assert.NotNull(ownership);
        return ownership;
    }

    private static void AssertDiagnostic(CompilationResult result, string code, params string[] messageFragments)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code
                && messageFragments.All(fragment => diagnostic.Message.Contains(fragment, StringComparison.Ordinal)));
    }
}
