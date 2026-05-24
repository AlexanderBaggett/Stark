using Stark.Compiler;

namespace compiler.Tests;

public sealed class MidLevelIrDynamicFixedArrayIndexingTests
{
    [Fact]
    public void DynamicIndexingOnFixedArrayTemporaryLowersWithoutUnsupportedFallback()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] index)
            {
                return (new i32[min max][3])[index];
            }
            """);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Logs,
            log => log.Category == "lowering"
                && log.EventId == "unsupported-lowering"
                && log.Stage == "lower-mir"
                && log.Operation == "LowerIndexAccess");

        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrAddressOfLocalRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    private static CompilationResult Compile(string source)
    {
        var pipeline = DefaultCompilerPipeline.Create();
        return pipeline.Run(new CompilationInput(source), new CompilerOptions(StopAfterPassId: "lower-mir"));
    }

    private static MidLevelIrModule GetMir(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        return mir;
    }
}
