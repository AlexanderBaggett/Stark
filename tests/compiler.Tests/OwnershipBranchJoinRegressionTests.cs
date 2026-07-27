using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Branch-join regressions for ownership validation: a branch that returns
/// from the function on every path never reaches the code below the branch
/// statement, so its end-state (values consumed by an early `return value`)
/// must not leak into the join. The old merge included those dead states,
/// poisoning the fall-through path with bogus STK4205 "not fully
/// initialized" errors — the long-standing "early return-move" sharp edge
/// that forced flag-and-single-tail-return rewrites in ported code.
/// break/continue paths are different: their states still reach the
/// enclosing loop's join and must keep merging.
/// </summary>
public sealed class OwnershipBranchJoinRegressionTests
{
    private const string MoveOnlyPrelude =
        """
        module Demo

        struct Box
        {
            i32[min max] Value;

            drop
            {
            }
        }

        struct Holder
        {
            bool Flag;
            Box Data;

            Holder()
            {
                self.Flag = false;
                self.Data = new Box()
                {
                    Value = 0
                };
            }
        }

        fn void Consume(Box value)
        {
            return;
        }

        """;

    [Fact]
    public void EarlyReturnMoveInThenBranchDoesNotPoisonTheFallThroughJoin()
    {
        var result = Compile(
            MoveOnlyPrelude +
            """
            fn Holder Build(bool flag)
            {
                stack mut Holder holder = new();
                if (flag)
                {
                    return holder;
                }

                return holder;
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void EarlyReturnMoveThenFieldReassignmentAndAggregateReturnValidates()
    {
        var result = Compile(
            MoveOnlyPrelude +
            """
            fn Holder Build(bool flag)
            {
                stack mut Holder holder = new();
                if (flag)
                {
                    return holder;
                }

                holder.Data = new Box()
                {
                    Value = 7
                };
                return holder;
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void SwitchArmEarlyReturnMoveDoesNotPoisonTheFallThroughJoin()
    {
        var result = Compile(
            MoveOnlyPrelude +
            """
            fn Holder Build(i64[min max] mode)
            {
                stack mut Holder holder = new();
                switch (mode)
                {
                    case 0:
                    {
                        return holder;
                    }
                    case _:
                    {
                    }
                }

                holder.Data = new Box()
                {
                    Value = 7
                };
                return holder;
            }
            """);

        AssertSucceeded(result);
    }

    [Fact]
    public void ConditionalMoveWithoutReturnStillErrorsOnLaterUse()
    {
        var result = Compile(
            MoveOnlyPrelude +
            """
            fn i32[min max] Build(bool flag)
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                if (flag)
                {
                    Consume(box);
                }

                stack Box other = box;
                return other.Value;
            }
            """);

        AssertMoveOrInitializationError(result);
    }

    [Fact]
    public void MoveBeforeBreakStaysVisibleAfterTheLoop()
    {
        var result = Compile(
            MoveOnlyPrelude +
            """
            fn i32[min max] Build(bool flag)
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                stack mut i64[min max] index = 0;
                while willexit (index < 3)
                {
                    if (flag)
                    {
                        Consume(box);
                        break;
                    }

                    index = index + 1;
                }

                stack Box other = box;
                return other.Value;
            }
            """);

        AssertMoveOrInitializationError(result);
    }

    [Fact]
    public void ElseBranchMoveSurvivesWhenThenBranchReturns()
    {
        var result = Compile(
            MoveOnlyPrelude +
            """
            fn i32[min max] Build(bool flag)
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                if (flag)
                {
                    return 7;
                }
                else
                {
                    Consume(box);
                }

                stack Box other = box;
                return other.Value;
            }
            """);

        Assert.False(result.Succeeded);
        // The surviving path moved the value unconditionally, so the join
        // reports the precise definite-move error, not a vague maybe-move.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK4200");
    }

    [Fact]
    public void SwitchFallThroughArmMoveStillErrors()
    {
        var result = Compile(
            MoveOnlyPrelude +
            """
            fn i32[min max] Build(i64[min max] mode)
            {
                stack mut Box box = new Box()
                {
                    Value = 1
                };
                switch (mode)
                {
                    case 0:
                    {
                        Consume(box);
                    }
                    case _:
                    {
                    }
                }

                stack Box other = box;
                return other.Value;
            }
            """);

        AssertMoveOrInitializationError(result);
    }

    private static void AssertSucceeded(CompilationResult result)
    {
        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static void AssertMoveOrInitializationError(CompilationResult result)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "STK4200" or "STK4205");
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }
}
