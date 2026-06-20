using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Value contracts: `where &lt;comparison&gt;` clauses on functions are entry
/// facts inside the callee (a strict `index &lt; storage.Length` contract
/// proves the body's reads) and obligations at every call site, discharged
/// by a dominating comparison, the caller's own matching contract, or
/// constant arguments — otherwise STK4206 names the unproven obligation.
/// </summary>
public sealed class ValueContractTests
{
    private const string TablePrelude = """
        module Demo

        struct Table
        {
            dynamic i64[min max] Items;

            finite law i64[min max] At(borrow Table self, u64[0 2 ** 63 - 1] index)
                where index < self.Items.Length
            {
                return self.Items[index];
            }
        }

        """;

    [Fact]
    public void ContractsSeedEntryFactsAndDischargeByGuardOrForwardedContract()
    {
        var result = Compile(TablePrelude + """
            finite law i64[min max] Forwarded(borrow Table table, u64[0 2 ** 63 - 1] index)
                where index < table.Items.Length
            {
                return table.At(index);
            }

            finite law i64[min max] Guarded(borrow Table table, u64[0 2 ** 63 - 1] index)
            {
                if (index >= table.Items.Length)
                {
                    return 0;
                }

                return table.At(index);
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnprovenCallSitesAreDiagnosedWithTheSubstitutedObligation()
    {
        var result = Compile(TablePrelude + """
            finite law i64[min max] Unproven(borrow Table table, u64[0 2 ** 63 - 1] index)
            {
                return table.At(index);
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4206"
                && diagnostic.Message.Contains("index < self.Items.Length", StringComparison.Ordinal)
                && diagnostic.Message.Contains("index < table.Items.Length", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArgumentsDischargePureValueComparisons()
    {
        var result = Compile("""
            module Demo

            finite law u32[0 max] Pick(u32[0 max] count)
                where count < 64
            {
                return count;
            }

            finite law u32[0 max] InRange()
            {
                return Pick(16);
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var outOfRange = Compile("""
            module Demo

            finite law u32[0 max] Pick(u32[0 max] count)
                where count < 64
            {
                return count;
            }

            finite law u32[0 max] OutOfRange()
            {
                return Pick(99);
            }
            """);

        Assert.False(outOfRange.Succeeded);
        Assert.Contains(
            outOfRange.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4206"
                && diagnostic.Message.Contains("99 < 64", StringComparison.Ordinal));
    }

    [Fact]
    public void MirroredComparisonsNormalizeBetweenContractAndCaller()
    {
        var result = Compile(TablePrelude + """
            finite law i64[min max] Mirrored(borrow Table table, u64[0 2 ** 63 - 1] index)
                where table.Items.Length > index
            {
                return table.At(index);
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ContractFactsDoNotLeakToUnrelatedIndexes()
    {
        var result = Compile(TablePrelude + """
            finite law i64[min max] WrongIndex(borrow Table table, u64[0 2 ** 63 - 1] index, u64[0 2 ** 63 - 1] other)
                where index < table.Items.Length
            {
                return table.At(other);
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4206"
                && diagnostic.Message.Contains("other < table.Items.Length", StringComparison.Ordinal));
    }

    [Fact]
    public void ArithmeticPropagationProvesComputedOffsetReads()
    {
        var result = Compile("""
            module Demo

            struct Doc
            {
                dynamic i8[min max] Bytes;
            }

            fn bool RangeIsZero(borrow Doc self, u64[0 2 ** 63 - 1] start, u64[0 2 ** 63 - 1] length)
                where start + length <= self.Bytes.Length
            {
                stack mut u64[0 2 ** 63 - 1] index = 0;
                while willexit (index < length)
                {
                    if (self.Bytes[start + index] != 0)
                    {
                        return false;
                    }

                    index = (u64[0 2 ** 63 - 1])(index + 1);
                }

                return true;
            }

            fn bool GuardSpelledInline(borrow Doc self, u64[0 2 ** 63 - 1] start, u64[0 2 ** 63 - 1] length)
            {
                if (start + length <= self.Bytes.Length)
                {
                    return RangeIsZero(self, start, length);
                }

                return false;
            }
            """);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ComputedOffsetReadsWithoutTheContractStayRejected()
    {
        var result = Compile("""
            module Demo

            struct Doc
            {
                dynamic i8[min max] Bytes;
            }

            fn bool NoContract(borrow Doc self, u64[0 2 ** 63 - 1] start, u64[0 2 ** 63 - 1] length)
            {
                stack mut u64[0 2 ** 63 - 1] index = 0;
                while willexit (index < length)
                {
                    if (self.Bytes[start + index] != 0)
                    {
                        return false;
                    }

                    index = (u64[0 2 ** 63 - 1])(index + 1);
                }

                return true;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK4205"
                && diagnostic.Message.Contains("self.Bytes[start+index]", StringComparison.Ordinal));
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true));
    }
}
