using System.Numerics;
using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineIntegerCallFactsTests
{
    [Theory]
    [InlineData("value-facts")]
    [InlineData("arithmetic-fold-ssa")]
    [InlineData("emit-llvm")]
    public void ContextualReturnFactsProveAnInductionWithoutInlining(string pass)
    {
        var result = Compile(Program(), pass);
        var module = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
        var function = module.Functions.Single(f => f.Name == "Reduce");
        var calls = function.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>()
            .Where(i => i.Value is SsaCallRValue { FunctionName: "Step" }).ToArray();
        Assert.NotEmpty(calls);
        var facts = result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts).Functions["Reduce"].Values;
        foreach (var call in calls)
        {
            Assert.Equal(BigInteger.Zero, facts[call.ResultName].IntegerRange!.Min);
            Assert.True(facts[call.ResultName].IntegerRange!.Max <= 65536);
        }
        Assert.Equal(BigInteger.Zero, ReturnFacts(result, "Reduce").IntegerRange!.Min);
        // A call context must not strengthen the general callee's contract.
        Assert.True(ReturnFacts(result, "Step").IntegerRange!.Min < 0);
        if (pass == "emit-llvm")
        {
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            Assert.Contains("urem i64", llvm);
            Assert.Contains("@Step(", llvm);
        }
    }

    [Theory]
    [InlineData("negative-entry")]
    [InlineData("negative-return")]
    [InlineData("wrapping")]
    [InlineData("overflow")]
    [InlineData("truncation")]
    [InlineData("shift")]
    [InlineData("opaque")]
    [InlineData("recursive")]
    public void UnsupportedOrSignedCallContextsRemainConservative(string scenario)
    {
        var body = scenario switch
        {
            "negative-return" => "return -1;",
            "wrapping" => "return (left *% 2305843009213693951 +% right) % 65537;",
            "overflow" => "return (left * 2305843009213693951 + right) % 65537;",
            "truncation" => "return (i64[min max])(i8[min max])(left * 17);",
            "shift" => "return (left << 60) % 65537;",
            "recursive" => "return Step(left, right);",
            _ => "return (left * 7 + right) % 65537;"
        };
        var result = Compile(Program(body, scenario));
        Assert.True(ReturnFacts(result, "Reduce").IntegerRange!.Min < 0);
    }

    [Theory]
    [InlineData("i8[min max]", "i64[min max]", -128, 127)]
    [InlineData("u8[0 max]", "i64[min max]", 0, 255)]
    public void NarrowingFactsIncludeWrappedValues(string narrow, string returned, int min, int max)
    {
        var result = Compile($$"""
            module CastProof
            unsafe finite law {{returned}} Cast(u16[0 1088] value)
            {
                return ({{returned}})({{narrow}})value;
            }
            """, "value-facts");
        var facts = ReturnFacts(result, "Cast");
        Assert.Equal(new BigInteger(min), facts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(max), facts.IntegerRange.Max);
    }

    [Fact]
    public void SignedToUnsignedWideningRetainsBothEndsOfTheBitDomain()
    {
        var result = Compile("""
            module CastProof
            unsafe finite law u64[0 max] Cast(i8[min max] value)
            {
                return (u64[0 max])value;
            }
            """, "value-facts");
        var facts = ReturnFacts(result, "Cast");
        Assert.Equal(BigInteger.Zero, facts.IntegerRange!.Min);
        Assert.Equal((BigInteger.One << 64) - 1, facts.IntegerRange.Max);
    }

    private static string Program(string body = "return (left * 7 + right) % 65537;", string scenario = "")
    {
        var recursive = scenario == "recursive";
        var unsafeCall = scenario is "opaque" or "truncation";
        var helper = scenario == "opaque"
            ? "unsafe ffi finite law i64[min max] Step(i64[min max] left, i64[min max] right);"
            : $"{(unsafeCall ? "unsafe " : "")}noinline {(recursive ? "law" : "finite law")} i64[min max] Step(i64[min max] left, i64[min max] right) {{ {body} }}";
        return $$"""
            module CallProof
            {{helper}}
            {{(unsafeCall ? "unsafe " : "")}}{{(recursive ? "law" : "finite law")}} i64[min max] Reduce({{(scenario == "negative-entry" ? "i32[min max]" : "u32[0 max]")}} seed, u8[0 64] count)
            {
                stack mut i64[min max] total = (i64[min max])seed % 89;
                for willexit (stack mut u8[0 64] i = 0; i < count; i += 1)
                {
                    total = (total + Step((i64[min max])i, total % 23)) % 1000003;
                }
                return total;
            }
            """;
    }

    private static CompilationResult Compile(string source, string pass = "arithmetic-fold-ssa")
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source), new CompilerOptions(StopAfterPassId: pass));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }

    private static SsaValueFacts ReturnFacts(CompilationResult result, string name)
    {
        var function = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions.Single(f => f.Name == name);
        var block = Assert.Single(function.Blocks, b => b.Terminator.Kind == SsaTerminatorKind.Return);
        var value = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        return result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts).Functions[name].Values[value.Name];
    }
}
