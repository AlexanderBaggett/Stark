using Stark.Compiler;

namespace compiler.Tests;

public sealed class OwnershipRoadmapRegressionTests
{
    [Fact]
    public void MovedOwnedLocalCanBeReinitializedBeforeLaterRead()
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

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };
                Consume(box);
                box = new Box() { Value = 2 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
        Assert.Contains("box", ownership.Functions["Run"].Moves);
        Assert.Contains("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void BranchReinitializationKeepsOwnedLocalAvailable()
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

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };

                if (true) {
                    Consume(box);
                    box = new Box() { Value = 2 };
                }

                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void LoopReinitializationKeepsOwnedLocalAvailable()
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

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };
                stack i32 count = 0;

                while willexit (count < 1) {
                    Consume(box);
                    box = new Box() { Value = 2 };
                    count = count + 1;
                }

                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchMergeRequiresReinitializationOnEveryPath()
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

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };

                if (true) {
                    Consume(box);
                } else {
                    box = new Box() { Value = 2 };
                }

                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Control-flow error", "not available on every path");
    }

    [Fact]
    public void UninitializedOwnedLocalIsNotDroppedAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack Box box;
                return 1;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.DoesNotContain("box", ownership.Functions["Run"].ImplicitDrops);
    }

    [Fact]
    public void ReadingUninitializedOwnedLocalProducesInitializationDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack Box box;
                return box.Value;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "Initialization error", "field 'Value' of 'box' is not initialized yet");
    }

    [Fact]
    public void FieldAssignmentsCanFullyInitializeAnAggregateLocal()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run() {
                stack mut Pair pair;
                pair.Left = 1;
                pair.Right = 2;
                return pair.Left + pair.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void PartiallyInitializedAggregateCannotBeConsumedAsAWholeValue()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn void Consume(Pair value) {
                return;
            }

            fn i32 Run() {
                stack mut Pair pair;
                pair.Left = 1;
                Consume(pair);
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "value 'pair' is not fully initialized", "Missing fields: Right");
    }

    [Fact]
    public void PartiallyInitializedAggregateIsRejectedAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run() {
                stack mut Pair pair;
                pair.Left = 1;
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4205", "Drop error", "cannot drop 'pair'", "Missing fields: Right");
    }

    [Fact]
    public void WholeAggregateUseAfterFieldMoveReportsPartialMove()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                ascii Left;
                ascii Right;
            }

            fn void Consume(Pair value) {
                return;
            }

            fn i32 Run() {
                stack mut Pair pair = new Pair() { Left = "a", Right = "b" };
                stack ascii left = pair.Left;
                Consume(pair);
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Move error", "value 'pair' is partially moved", "Left (moved)");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4200"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("Field 'Left' of 'pair' was moved here.", StringComparison.Ordinal));
    }

    [Fact]
    public void ReinitializingMovedFieldRestoresWholeAggregateAvailability()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                ascii Left;
                ascii Right;
            }

            fn void Consume(Pair value) {
                return;
            }

            fn i32 Run() {
                stack mut Pair pair = new Pair() { Left = "a", Right = "b" };
                stack ascii left = pair.Left;
                pair.Left = "c";
                Consume(pair);
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchMergesPartialFieldMoveStateAcrossPaths()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                ascii Left;
                ascii Right;
            }

            fn i32 Run(bool choose) {
                stack mut Pair pair = new Pair() { Left = "a", Right = "b" };

                if (choose) {
                    stack ascii left = pair.Left;
                }

                stack ascii current = pair.Left;
                return 0;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Control-flow error", "field 'Left' of 'pair' is not available on every path");
    }

    [Fact]
    public void BranchReinitializationAfterFieldMoveKeepsFieldAvailable()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                ascii Left;
                ascii Right;
            }

            fn i32 Run(bool choose) {
                stack mut Pair pair = new Pair() { Left = "a", Right = "b" };

                if (choose) {
                    stack ascii left = pair.Left;
                    pair.Left = "c";
                }

                stack ascii current = pair.Left;
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchesMergeDefiniteAggregateFieldInitialization()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run(bool choose) {
                stack mut Pair pair;

                if (choose) {
                    pair.Left = 1;
                } else {
                    pair.Left = 2;
                }

                pair.Right = 3;
                return pair.Left + pair.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var ownership = GetOwnership(result);
        Assert.True(ownership.Functions["Run"].OwnershipValid);
    }

    [Fact]
    public void BranchesReportFieldAvailabilityWhenOnlySomePathsInitializeIt()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run(bool choose) {
                stack mut Pair pair;

                if (choose) {
                    pair.Left = 1;
                } else {
                    pair.Right = 2;
                }

                return pair.Left;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4200", "Control-flow error", "field 'Left' of 'pair' is not available on every path");
    }

    [Fact]
    public void ReturningBorrowFromUnknownSourceReportsLifetimeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32 Source();

            fn retborrow i32 Leak() {
                return Source();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "unknown source lifetime", "could not be proven");
    }

    [Fact]
    public void ReturningBorrowFromBranchSpecificCallsStillReportsLifetimeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32 Source();
            fn retborrow i32 Other();

            fn retborrow i32 Leak(bool choose) {
                if (choose) {
                    return Source();
                }

                return Other();
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "could not be proven");
    }

    [Fact]
    public void AssigningBorrowFromUnknownSourceReportsDestinationLifetime()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32 Source();

            fn void Run() {
                stack retborrow i32 alias = Source();
                alias = Source();

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "cannot assign a borrow with an unknown source lifetime to 'alias'", "destination scope");
    }

    [Fact]
    public void AssigningBorrowFromInnerScopeToOuterScopeReportsEscapeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32 Source();

            fn void Run() {
                stack retborrow i32 outer;

                {
                    stack retborrow i32 innerAlias = Source();
                    outer = innerAlias;
                }

                return;
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "source lifetime could not be proven", "destination scope");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4202"
                && diagnostic.Severity == DiagnosticSeverity.Info
                && diagnostic.Message.Contains("borrow source for call 'Source' is here.", StringComparison.Ordinal));
    }

    [Fact]
    public void ReturningBorrowFromInnerScopeReportsEscapeDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            fn retborrow i32 Source();

            fn retborrow i32 Run() {
                {
                    stack retborrow i32 alias = Source();
                    return alias;
                }
            }
            """);

        Assert.False(result.Succeeded);
        AssertDiagnostic(result, "STK4202", "Lifetime error", "source lifetime could not be proven", "return path");
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
