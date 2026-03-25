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

            fn i32 Main() {
                heap Box box = new Box() { Value = 1 };
                return 1;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Main"].ImplicitDrops);
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

            fn void Consume(Box value) {
                return;
            }

            fn i32 Main() {
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
    public void CopyValuesRemainUsableAfterAssignment()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                stack i32 x = 1;
                stack i32 y = x;
                return x + y;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Main"].OwnershipValid);
        Assert.DoesNotContain("x", ownership.Functions["Main"].Moves);
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

            fn i32 Main() {
                stack mut Box box = new Box() { Value = 1 };
                box = new Box() { Value = 2 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("box", ownership.Functions["Main"].ImplicitDrops);
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

            fn void Consume(Box value) {
                return;
            }

            fn i32 Main() {
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

            fn Box Make() {
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

            fn ascii Main() {
                stack Container value = new Container() { Name = "hi", Label = "there" };
                return value.Name;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.Contains("value.Name", ownership.Functions["Main"].Moves);
        Assert.Contains("value", ownership.Functions["Main"].ImplicitDrops);
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

            fn ascii Main() {
                stack Container value = new Container() { Name = new NameBox() { Value = "hi" } };
                return value.Name.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4203", "Cannot move out of field or indexed place");
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
