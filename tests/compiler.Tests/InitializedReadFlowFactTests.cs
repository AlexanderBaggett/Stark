using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Branch-dominance length facts for dynamic storage reads: a comparison
/// proving `index &lt; root.Length` on a path lets reads of `root[index]`
/// (including field projections through the slot) pass the dense-prefix
/// gate, in any function — not just inside the owning type. Field
/// projections through a dynamic slot are themselves checked now (they were
/// previously unvalidated), and facts die on writes to anything they
/// mention.
/// </summary>
public sealed class InitializedReadFlowFactTests
{
    private const string TablePrelude = """
        module Demo

        enum Kind
        {
            Alpha,
            Beta,
        }

        struct Item
        {
            Kind Tag;
            u64[0 2 ** 63 - 1] Start;
        }

        struct Table
        {
            dynamic Item Items;
        }

        """;

    [Fact]
    public void GuardThenReadProvesDirectAndProjectedReadsInFreeFunctions()
    {
        var result = Compile(TablePrelude + """
            finite law bool TagAtIs(borrow Table self, u64[0 2 ** 63 - 1] index, Kind expected)
            {
                if (index >= self.Items.Length)
                {
                    return false;
                }

                return self.Items[index].Tag == expected;
            }

            finite law Item ItemAt(borrow Table self, u64[0 2 ** 63 - 1] index)
            {
                if (index >= self.Items.Length)
                {
                    return new();
                }

                return self.Items[index];
            }

            finite law u64[0 2 ** 63 - 1] StartInsideThen(borrow Table self, u64[0 2 ** 63 - 1] index)
            {
                if (index < self.Items.Length)
                {
                    return self.Items[index].Start;
                }

                return 0;
            }

            finite law bool BothBelow(borrow Table self, u64[0 2 ** 63 - 1] left, u64[0 2 ** 63 - 1] right)
            {
                if (left < self.Items.Length && right < self.Items.Length)
                {
                    return self.Items[left].Tag == self.Items[right].Tag;
                }

                return false;
            }

            finite law u64[0 2 ** 63 - 1] CountViaWhile(borrow Table self)
            {
                stack mut u64[0 2 ** 63 - 1] index = 0;
                stack mut u64[0 2 ** 63 - 1] count = 0;
                while willexit (index < self.Items.Length)
                {
                    if (self.Items[index].Tag == Kind.Alpha)
                    {
                        count = (u64[0 2 ** 63 - 1])(count + 1);
                    }

                    index = (u64[0 2 ** 63 - 1])(index + 1);
                }

                return count;
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnguardedFieldProjectionsThroughDynamicSlotsAreNowChecked()
    {
        var result = Compile(TablePrelude + """
            finite law bool UnguardedField(borrow Table self, u64[0 2 ** 63 - 1] index)
            {
                return self.Items[index].Tag == Kind.Alpha;
            }
            """);

        Assert.False(result.Succeeded);
        AssertSlotProofError(result, "self.Items");
    }

    [Fact]
    public void WritingToTheIndexInvalidatesTheFact()
    {
        var result = Compile(TablePrelude + """
            finite law bool IncrementAfterGuard(borrow Table self, u64[0 2 ** 63 - 1] index)
            {
                stack mut u64[0 2 ** 63 - 1] probe = index;
                if (probe >= self.Items.Length)
                {
                    return false;
                }

                probe += 1;
                return self.Items[probe].Tag == Kind.Alpha;
            }
            """);

        Assert.False(result.Succeeded);
        AssertSlotProofError(result, "self.Items");
    }

    [Fact]
    public void NonStrictComparisonsAndNonTerminatingGuardsProveNothing()
    {
        var result = Compile(TablePrelude + """
            finite law bool NonStrict(borrow Table self, u64[0 2 ** 63 - 1] index)
            {
                if (index <= self.Items.Length)
                {
                    return self.Items[index].Tag == Kind.Alpha;
                }

                return false;
            }

            finite law bool GuardWithoutReturn(borrow Table self, u64[0 2 ** 63 - 1] index)
            {
                if (index >= self.Items.Length)
                {
                    stack u64[0 2 ** 63 - 1] ignored = 0;
                }

                return self.Items[index].Tag == Kind.Alpha;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            2,
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == "STK4205"
                && diagnostic.Message.Contains("without a proof that the slot is initialized", StringComparison.Ordinal)));
    }

    [Fact]
    public void MutBorrowingTheStorageOwnerInvalidatesTheFact()
    {
        var result = Compile(TablePrelude + """
            fn void Touch(mut borrow Table table)
            {
                return;
            }

            fn bool ReadAfterMutCall(mut borrow Table self, u64[0 2 ** 63 - 1] index)
            {
                if (index >= self.Items.Length)
                {
                    return false;
                }

                Touch(self);
                return self.Items[index].Tag == Kind.Alpha;
            }
            """);

        Assert.False(result.Succeeded);
        AssertSlotProofError(result, "self.Items");
    }

    private static void AssertSlotProofError(CompilationResult result, string root)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK4205"
                && diagnostic.Message.Contains("without a proof that the slot is initialized", StringComparison.Ordinal)
                && diagnostic.Message.Contains(root, StringComparison.Ordinal));
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true));
    }
}
