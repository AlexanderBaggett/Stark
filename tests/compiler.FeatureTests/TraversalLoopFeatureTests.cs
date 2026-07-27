using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class TraversalLoopFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void ForInTraversesFixedArraysWithBorrowElements()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            finite law i32[min max] Sum()
            {
                stack Box[3] boxes =
                {
                    new Box() { Value = 1 },
                    new Box() { Value = 2 },
                    new Box() { Value = 3 }
                };
                stack mut i32[min max] sum = 0;
                for willexit (borrow Box box in boxes)
                {
                    sum += box.Value;
                }
                return sum;
            }
            """,
            new CompilerOptions());

        Assert.Contains("define fastcc noundef i32 @Sum()", llvm);
        Assert.Contains("getelementptr", llvm);
        Assert.DoesNotContain("iterator", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForInCanExposeCheckedIndexAndMutableElementBorrow()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] MutateAndSum()
            {
                stack mut Box[3] boxes =
                {
                    new Box() { Value = 10 },
                    new Box() { Value = 20 },
                    new Box() { Value = 30 }
                };
                stack mut i32[min max] sum = 0;
                for willexit (stack u8[0 2] index, borrow mut Box box in boxes)
                {
                    box.Value += 1;
                    sum += box.Value;
                }
                return sum;
            }
            """,
            new CompilerOptions());

        Assert.Contains("define fastcc noundef i32 @MutateAndSum()", llvm);
        Assert.Contains("store i32", llvm);
    }

    [Fact]
    public void ForInTraversesSlicesWithoutIteratorAllocation()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe finite law i32[min max] SumSlice()
            {
                stack mut Box[2] boxes =
                {
                    new Box() { Value = 4 },
                    new Box() { Value = 5 }
                };
                stack mut Box[] view = boxes;
                stack mut i32[min max] sum = 0;
                for willexit (borrow Box box in view)
                {
                    sum += box.Value;
                }
                return sum;
            }
            """,
            new CompilerOptions());

        Assert.Contains("define fastcc noundef i32 @SumSlice()", llvm);
        Assert.DoesNotContain("iterator", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForInTraversesDynamicStorage()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            unsafe fn i32[min max] SumDynamic()
            {
                stack mut dynamic Box boxes = new(2);
                init boxes[0] = new Box() { Value = 6 };
                init boxes[1] = new Box() { Value = 7 };
                stack mut i32[min max] sum = 0;
                for willexit (borrow Box box in boxes)
                {
                    sum += box.Value;
                }
                return sum;
            }
            """,
            new CompilerOptions());

        Assert.Contains("define fastcc noundef i32 @SumDynamic()", llvm);
        Assert.Contains("extractvalue { ptr, i64, i64 }", llvm);
        Assert.DoesNotContain("iterator", llvm, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForInRejectsNonBorrowElementBindings()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Bad()
            {
                stack i32[min max][1] values = { 1 };
                for willexit (i32[min max] value in values)
                {
                    return value;
                }
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("must be declared as 'borrow T' or 'borrow mut T'", StringComparison.Ordinal));
    }

    [Fact]
    public void ForInRejectsMutableBorrowFromImmutableStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            finite law i32[min max] Bad()
            {
                stack Box[1] boxes =
                {
                    new Box() { Value = 1 }
                };
                for willexit (borrow mut Box box in boxes)
                {
                    box.Value = 2;
                }
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("does not provide mutable element storage", StringComparison.Ordinal));
    }
}
