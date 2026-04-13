using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineOptimizeSsaTests
{
    [Fact]
    public void OptimizeSsaPreservesMaterializedGenericCallSymbols()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "optimize-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var call = run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>()
            .Single();

        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", call.FunctionName);
    }
}
