using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    [Fact]
    public void ArenaDynamicStorageEmitsExplicitMirFrameScopeOnEveryReturn()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn u64[0 max] Run(bool flag)
            {
                stack mut dynamic u32[0 max] values = new(arena, 4);
                if (flag)
                {
                    return values.Capacity;
                }

                values.Reserve(8);
                return values.Capacity;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var mir = GetMir(result);
        AssertMirHasNoNullLoweringArtifacts(mir);

        var function = Assert.Single(mir.Functions);
        var entryBlock = Assert.Single(function.Blocks, block => block.Id == function.EntryBlockId);
        Assert.NotEmpty(entryBlock.Statements);
        Assert.Equal(MidLevelIrStatementKind.ArenaFrameEnter, entryBlock.Statements[0].Kind);

        var returnBlocks = function.Blocks
            .Where(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Return)
            .ToArray();
        Assert.Equal(2, returnBlocks.Length);
        Assert.All(returnBlocks, static block =>
        {
            Assert.NotEmpty(block.Statements);
            Assert.Equal(MidLevelIrStatementKind.ArenaFrameLeave, block.Statements[^1].Kind);
        });

        Assert.Equal(
            returnBlocks.Length,
            function.Blocks
                .SelectMany(static block => block.Statements)
                .Count(static statement => statement.Kind == MidLevelIrStatementKind.ArenaFrameLeave));
    }
}
