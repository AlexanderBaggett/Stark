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
        AssertDiagnostic(result, "STK4200");
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
    public void MovingOutOfAFieldIsRejected()
    {
        var result = Compile(
            """
            module Demo

            struct Container {
                ascii Name;
            }

            fn ascii Main() {
                stack Container value = new Container() { Name = "hi" };
                return value.Name;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4203");
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

    private static void AssertDiagnostic(CompilationResult result, string code)
    {
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }
}
