using Stark.Compiler;

namespace compiler.Tests;

public sealed class LlvmTextOrderedComparisonEmissionTests
{
    [Fact]
    public void TextOrderedComparisonsEmitLexicographicHelperCalls()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool LessAscii(ascii left, ascii right) {
                return left < right;
            }

            unsafe fn bool GreaterOrEqualUnicode(unicode left, unicode right) {
                return left >= right;
            }

            unsafe fn bool BoolLess(bool left, bool right) {
                return left < right;
            }

            unsafe fn bool PointerLess(rawptr<i32[-2147483648 2147483647]> left, rawptr<i32[-2147483648 2147483647]> right) {
                return left < right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define internal dso_local i32 @__stark_ascii_compare(%stark_ascii %left, %stark_ascii %right)", llvm);
        Assert.Contains("define internal dso_local i32 @__stark_unicode_compare(%stark_unicode %left, %stark_unicode %right)", llvm);
        Assert.Contains("call i32 @__stark_ascii_compare(%stark_ascii %arg_left, %stark_ascii %arg_right)", llvm);
        Assert.Contains("call i32 @__stark_unicode_compare(%stark_unicode %arg_left, %stark_unicode %arg_right)", llvm);
        Assert.Contains("icmp slt i32", llvm);
        Assert.Contains("icmp sge i32", llvm);
        Assert.Contains("icmp ult i1", llvm);
        Assert.Contains("icmp ult ptr", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for LessAscii", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for GreaterOrEqualUnicode", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for BoolLess", llvm);
        Assert.DoesNotContain("; LLVM body emission fallback for PointerLess", llvm);
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
