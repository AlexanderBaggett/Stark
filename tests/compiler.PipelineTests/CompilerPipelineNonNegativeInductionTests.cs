using System.Numerics;
using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineNonNegativeInductionTests
{
    [Theory]
    [InlineData("value-facts")]
    [InlineData("arithmetic-fold-ssa")]
    [InlineData("emit-llvm")]
    public void PositiveRecurrenceRetainsAnInductiveRangeThroughReanalysis(string pass)
    {
        var result = Compile(Program(), pass);
        var returned = ReturnFacts(result);
        Assert.Equal(BigInteger.Zero, returned.IntegerRange!.Min);
        Assert.True(returned.IntegerRange.Max <= 123456);
        var function = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions.Single(f => f.Name == "Reduce");
        var facts = result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts).Functions["Reduce"];
        foreach (var divide in function.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>()
                     .Where(i => i.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo }))
        {
            Assert.True(facts.Values[divide.ResultName].IntegerRange!.Min >= 0);
            var operation = (SsaBinaryRValue)divide.Value;
            if (operation.Left is SsaValueReference input)
            {
                Assert.True(facts.Values[input.Name].IntegerRange!.Min >= 0);
            }
        }
        if (pass == "emit-llvm")
        {
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            Assert.Contains("udiv i64", llvm);
            Assert.Contains("urem i64", llvm);
            Assert.DoesNotContain("sdiv i64", llvm);
            Assert.DoesNotContain("srem i64", llvm);
        }
    }

    [Theory]
    [InlineData("(total + value * 5 - value / 7 + value % 13) % 123457")]
    [InlineData("(value * 5 + total - value / 7) % 123457")]
    [InlineData("(total + 5 * value - value / 7) % 123457")]
    [InlineData("(value - value / 7) % 123457")]
    public void RelationalSubtractionWorksForArbitraryEligibleExpressions(string step)
    {
        Assert.Equal(BigInteger.Zero, ReturnFacts(Compile(Program(step: step))).IntegerRange!.Min);
    }

    [Theory]
    [InlineData("negative-entry")]
    [InlineData("negative-edge")]
    [InlineData("negative-subtraction")]
    [InlineData("negative-divisor")]
    [InlineData("zero-divisor")]
    [InlineData("wrapping")]
    [InlineData("overflow")]
    [InlineData("truncation")]
    [InlineData("opaque")]
    public void UnprovedOrNegativeTransitionsKeepTheirSignedRange(string scenario)
    {
        var source = scenario switch
        {
            "negative-entry" => Program(seedType: "i32[min max]"),
            "negative-edge" => Program(edge: "if (choose) { total = -1; continue; }"),
            "negative-subtraction" => Program(step: "(total - value * 5) % 123457"),
            "negative-divisor" => Program(step: "(value / -7) % 123457"),
            "zero-divisor" => Program(step: "(value / (i64[min max])divisor) % 123457", extra: ", u8[0 7] divisor"),
            "wrapping" => Program(step: "(total *% 2305843009213693951 +% 1) % 123457"),
            "overflow" => Program(step: "(total * 2305843009213693951) % 123457"),
            "truncation" => Program(step: "((i64[min max])(i32[min max])(total * 1000000)) % 123457"),
            "opaque" => Program(step: "Opaque() % 123457", declaration: "unsafe ffi finite law i64[min max] Opaque();"),
            _ => throw new ArgumentException(scenario)
        };
        Assert.True(ReturnFacts(Compile(source)).IntegerRange!.Min < 0);
    }

    [Fact]
    public void AcyclicRelationalSignsAreRecoveredWithoutANewPhiProof()
    {
        var result = Compile("""
            module Proof
            finite law i64[min max] Reduce(u16[0 10000] input)
            {
                stack i64[min max] value = (i64[min max])input;
                return (value * 5 - value / 7) % 123457;
            }
            """, "emit-llvm");
        Assert.Equal(BigInteger.Zero, ReturnFacts(result).IntegerRange!.Min);
        Assert.DoesNotContain("srem i64", result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text);
    }

    [Fact]
    public void EveryBranchCanIndependentlyPreserveTheInvariant()
    {
        var result = Compile(Program(edge: "if (choose) { total = (value * 3 - value / 2) % 123457; continue; }"));
        Assert.Equal(BigInteger.Zero, ReturnFacts(result).IntegerRange!.Min);
    }

    [Fact]
    public void NegativeEntryInAMutualCycleIsNotHiddenByAnotherPhi()
    {
        var result = Compile("""
            module Proof
            finite law i64[min max] Reduce(u8[0 64] count)
            {
                stack mut i64[min max] left = 1;
                stack mut i64[min max] right = -1;
                for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
                {
                    stack i64[min max] previous = left;
                    left = right;
                    right = -previous % 97;
                }
                return left;
            }
            """);
        Assert.True(ReturnFacts(result).IntegerRange!.Min < 0);
    }

    private static string Program(string seedType = "u32[0 max]",
        string step = "(total + value * 5 - value / 7 + value % 13) % 123457",
        string edge = "", string extra = "", string declaration = "") => $$"""
        module Proof
        {{declaration}}
        unsafe finite law i64[min max] Reduce({{seedType}} seed, u8[0 64] count, bool choose{{extra}})
        {
            stack mut i64[min max] total = (i64[min max])seed % 89;
            for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
            {
                stack i64[min max] value = total + (i64[min max])i;
                {{edge}}
                total = {{step}};
            }
            return total;
        }
        """;

    private static CompilationResult Compile(string source, string pass = "arithmetic-fold-ssa")
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source), new CompilerOptions(StopAfterPassId: pass));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }

    private static SsaValueFacts ReturnFacts(CompilationResult result)
    {
        var function = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions.Single(f => f.Name == "Reduce");
        var returned = Assert.Single(function.Blocks, b => b.Terminator.Kind == SsaTerminatorKind.Return);
        var value = Assert.IsType<SsaValueReference>(returned.Terminator.Value);
        var facts = result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts).Functions[function.Name].Values[value.Name];
        Assert.Equal(SsaFactLatticeKind.Known, facts.IntegerRangeKind);
        return facts;
    }
}
