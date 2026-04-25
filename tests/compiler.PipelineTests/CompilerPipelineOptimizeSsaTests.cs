using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineOptimizeSsaTests
{
    [Fact]
    public void CallableAddressTakenFactsSurviveThroughLlvmEmission()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                finite law i32[-2147483648 2147483647] Target() {
                    return 1;
                }

                fn i32[-2147483648 2147483647] Run() {
                    stack fnptr<fn i32[-2147483648 2147483647]()> op = Target;
                    return op();
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));

        Assert.NotNull(typeModel);
        Assert.NotNull(hir);
        Assert.NotNull(mir);
        Assert.NotNull(ssa);
        Assert.NotNull(optimizedSsa);
        Assert.NotNull(llvm);

        Assert.Equal("Target", Assert.Single(typeModel.AddressTakenFunctions).Signature.Name);
        Assert.Equal("Target", Assert.Single(hir.AddressTakenFunctions));
        Assert.Equal("Target", Assert.Single(mir.AddressTakenFunctions));
        Assert.Equal("Target", Assert.Single(ssa.AddressTakenFunctions));
        Assert.Equal("Target", Assert.Single(optimizedSsa.AddressTakenFunctions));
        Assert.Equal("Target", Assert.Single(llvm.AddressTakenFunctions));
    }

    [Fact]
    public void NonCapturingLambdaAddressTakenFactsSurviveThroughLlvmEmission()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run() {
                    stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> increment =
                        (i32[-2147483648 2147483647] value) => value + 1;
                    return increment(41);
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));

        Assert.NotNull(typeModel);
        Assert.NotNull(hir);
        Assert.NotNull(mir);
        Assert.NotNull(ssa);
        Assert.NotNull(optimizedSsa);
        Assert.NotNull(llvm);

        var lambdaName = Assert.Single(typeModel.Lambdas).FunctionName;
        Assert.Empty(typeModel.AddressTakenFunctions);
        Assert.Equal(lambdaName, Assert.Single(hir.AddressTakenFunctions));
        Assert.Equal(lambdaName, Assert.Single(mir.AddressTakenFunctions));
        Assert.Equal(lambdaName, Assert.Single(ssa.AddressTakenFunctions));
        Assert.Equal(lambdaName, Assert.Single(optimizedSsa.AddressTakenFunctions));
        Assert.Equal(lambdaName, Assert.Single(llvm.AddressTakenFunctions));
    }

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
