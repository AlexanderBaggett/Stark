using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaLoweringTests
{
    [Fact]
    public void BranchJoinProducesPhiForMergedLocal()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(bool flag) {
                stack mut i32 value = 0;
                if (flag) {
                    value = 1;
                } else {
                    value = 2;
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);

        var joinBlock = Assert.Single(function.Blocks, static block => block.Phis.Count != 0);
        var phi = Assert.Single(joinBlock.Phis);
        Assert.Equal("value", phi.VariableName);
        Assert.Equal(2, phi.Incomings.Count);
    }

    [Fact]
    public void CommutativeRepeatedExpressionsShareAValueNumber()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 a, i32 b) {
                stack i32 first = a + b;
                stack i32 second = b + a;
                return first + second;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var addCount = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Count(static instruction => instruction.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Add });

        Assert.Equal(2, addCount);
    }

    [Fact]
    public void TrivialPhiNodesAreRemovedAndRewritten()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(bool flag) {
                stack mut i32 value = 7;
                if (flag) {
                    value = 7;
                } else {
                    value = 7;
                }

                return value;
            }
        """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);

        Assert.All(function.Blocks, static block => Assert.Empty(block.Phis));
        Assert.Contains(
            function.Blocks,
            static block => block.Label.Contains("if_join", StringComparison.Ordinal)
                && block.Terminator.Value is SsaValueReference);
    }

    [Fact]
    public void EmptyTrampolineBlocksAreCollapsed()
    {
        var mir = new MidLevelIrModule(
            "Demo",
            [
                new MidLevelIrFunction(
                    "Main",
                    "Main() -> i32",
                    StarkTypeSymbols.Integer(32),
                    [],
                    HasBody: true,
                    SupportsDirectCodeGeneration: true,
                    EntryBlockId: 0,
                    Locals: [],
                    Blocks:
                    [
                        new MidLevelIrBasicBlock(
                            0,
                            "bb0_entry",
                            [],
                            new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [1])),
                        new MidLevelIrBasicBlock(
                            1,
                            "bb1_trampoline",
                            [],
                            new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [2])),
                        new MidLevelIrBasicBlock(
                            2,
                            "bb2_exit",
                            [],
                            new MidLevelIrTerminator(
                                MidLevelIrTerminatorKind.Return,
                                [],
                                Value: new MidLevelIrIntegerConstantOperand(0, StarkTypeSymbols.Integer(32))))
                    ])
            ]);

        var lowered = new SsaLowerer().Lower(mir);
        var function = Assert.Single(lowered.Functions);

        Assert.Equal(2, function.Blocks.Count);
        Assert.DoesNotContain(function.Blocks, static block => block.Label.Contains("trampoline", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, static block => block.Id == 0);
        Assert.Contains(function.Blocks, static block => block.Id == 2);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);
        Assert.Equal(2, entry.Terminator.Targets.Single());
    }

    [Fact]
    public void LoopHeaderProducesPhiForBackedgeValue()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                stack mut i32 i = 0;
                while willexit (i < 4) {
                    i = i + 1;
                }

                return i;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);

        var header = Assert.Single(function.Blocks, static block => block.Phis.Count == 1);
        var phi = Assert.Single(header.Phis);
        Assert.Equal("i", phi.VariableName);
        Assert.Equal(2, phi.Incomings.Count);
    }

    [Fact]
    public void UnreachableJoinBlocksArePrunedFromSsa()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                if (true) {
                    return 1;
                } else {
                    return 2;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);

        Assert.Equal(3, function.Blocks.Count);
        Assert.DoesNotContain(function.Blocks, static block => block.Label.Contains("if_join", StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateFieldOperationsLowerToSsaExtractAndInsert()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Main() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value = 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaInsertFieldRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaExtractFieldRValue });
    }

    [Fact]
    public void FixedArrayIndexOperationsLowerToSsaExtractAndInsert()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
                stack mut i32[3] values = { 1, 2, 3 };
                values[1] = 9;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaInsertIndexRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaExtractIndexRValue });
    }

    [Fact]
    public void SliceLoweringUsesLocalSlotsAndSliceLoads()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 index) {
                stack i32[3] values = { 4, 7, 9 };
                stack i32[] view = values;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaMakeSliceFromLocalRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaSliceElementAddressRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaLoadIndirectRValue });
    }

    [Fact]
    public void DynamicFixedArrayIndexMutationUsesIndirectAddressOps()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 index) {
                stack mut i32[3] values = { 1, 2, 3 };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaAddressOfLocalRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaElementAddressRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreIndirectInstruction);
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaLoadIndirectRValue });
    }

    [Fact]
    public void SliceMutationUsesIndirectAddressOps()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32[] view, i32 index) {
                view[index] = 9;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaSliceElementAddressRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreIndirectInstruction);
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaLoadIndirectRValue });
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }

    private static SsaIrModule GetSsa(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        return ssa;
    }
}
