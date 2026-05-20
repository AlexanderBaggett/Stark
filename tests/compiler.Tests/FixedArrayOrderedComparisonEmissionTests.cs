using Stark.Compiler;

namespace compiler.Tests;

public sealed class FixedArrayOrderedComparisonEmissionTests
{
    [Fact]
    public void FixedArrayOrderedComparisonsEmitLexicographicHelperCalls()
    {
        var result = Compile(
            """
            module Demo

            fn bool Less(i32[min max][3] left, i32[min max][3] right) {
                return left < right;
            }

            fn bool LessOrEqual(i32[min max][3] left, i32[min max][3] right) {
                return left <= right;
            }

            fn bool Greater(i32[min max][3] left, i32[min max][3] right) {
                return left > right;
            }

            fn bool GreaterOrEqual(i32[min max][3] left, i32[min max][3] right) {
                return left >= right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local i32 @__stark_fixed_array_compare_", llvm);
        Assert.Contains("call i32 @__stark_fixed_array_compare_", llvm);
        Assert.Contains("icmp slt i32", llvm);
        Assert.Contains("icmp sgt i32", llvm);
        Assert.Contains("icmp sle i32", llvm);
        Assert.Contains("icmp sge i32", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Less", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for LessOrEqual", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for Greater", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for GreaterOrEqual", llvm);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static string GetLlvm(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        return llvmModule.Text;
    }
}
