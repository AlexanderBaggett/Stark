using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaOptimizationTests
{
    [Fact]
    public void CleanupRemovesTrivialCopyInstructions()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [new TypedParameterSymbol("value", valueType)],
                    HasBody: true,
                    SupportsDirectCodeGeneration: true,
                    EntryBlockId: 0,
                    Blocks:
                    [
                        new SsaBasicBlock(
                            0,
                            "bb0_entry",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaUseRValue(new SsaValueReference("arg_value", valueType)))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Instructions);
        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void CleanupRemovesIdentityPhiNodes()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [new TypedParameterSymbol("flag", boolType)],
                    HasBody: true,
                    SupportsDirectCodeGeneration: true,
                    EntryBlockId: 0,
                    Blocks:
                    [
                        new SsaBasicBlock(
                            0,
                            "bb0_entry",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Branch,
                                [1, 2],
                                Condition: new SsaValueReference("arg_flag", boolType))),
                        new SsaBasicBlock(
                            1,
                            "bb1_then",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_else",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [
                                new SsaPhi(
                                    "v0_phi",
                                    "value",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(1, new SsaIntegerConstant(5, valueType)),
                                        new SsaPhiIncoming(2, new SsaIntegerConstant(5, valueType))
                                    ])
                            ],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0_phi", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var joinBlock = Assert.Single(function.Blocks, static block => block.Id == 3);

        Assert.Empty(joinBlock.Phis);
        var returnValue = Assert.IsType<SsaIntegerConstant>(joinBlock.Terminator.Value);
        Assert.Equal(new BigInteger(5), returnValue.Value);
    }

    [Fact]
    public void CleanupReusesIdenticalMaterializedConstantConversions()
    {
        var sourceType = StarkTypeSymbols.Integer(8);
        var targetType = StarkTypeSymbols.Integer(32);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    targetType,
                    [],
                    HasBody: true,
                    SupportsDirectCodeGeneration: true,
                    EntryBlockId: 0,
                    Blocks:
                    [
                        new SsaBasicBlock(
                            0,
                            "bb0_entry",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaConvertRValue(
                                        new SsaIntegerConstant(1, sourceType),
                                        targetType,
                                        "cast(1)")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaConvertRValue(
                                        new SsaIntegerConstant(1, sourceType),
                                        targetType,
                                        "cast(1)")),
                                new SsaValueInstruction(
                                    "v2",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("v0", targetType),
                                        new SsaValueReference("v1", targetType),
                                        targetType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v2", targetType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var instructions = block.Instructions.OfType<SsaValueInstruction>().ToArray();

        Assert.Single(instructions, static instruction => instruction.Value is SsaConvertRValue);
        var add = Assert.Single(instructions, static instruction => instruction.Value is SsaBinaryRValue);
        var addValue = Assert.IsType<SsaBinaryRValue>(add.Value);
        var left = Assert.IsType<SsaValueReference>(addValue.Left);
        var right = Assert.IsType<SsaValueReference>(addValue.Right);
        Assert.Equal(left.Name, right.Name);
    }

    [Fact]
    public void OptimizedSsaCanonicalizesBooleanCompareBranches()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(bool flag) {
                if (flag == true) {
                    return 1;
                }

                return 2;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);
        var instructions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToArray();

        Assert.DoesNotContain(
            instructions,
            static instruction => instruction.Value is SsaBinaryRValue
            {
                Operator: SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual
            });

        var branchCondition = Assert.IsType<SsaValueReference>(entry.Terminator.Condition);
        Assert.Equal("arg_flag", branchCondition.Name);
    }

    [Fact]
    public void OptimizedSsaFoldsConstantArithmetic()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                return (1 + 2) * 3;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Instructions);
        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new BigInteger(9), returnValue.Value);
    }

    [Fact]
    public void OptimizedSsaReusesRepeatedSliceLoadsWithinABlock()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32[] view, i32 index) {
                return view[index] + view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        var instructions = function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToArray();

        Assert.Single(instructions, static instruction => instruction.Value is SsaSliceElementAddressRValue);
        Assert.Single(instructions, static instruction => instruction.Value is SsaLoadIndirectRValue);
        var add = Assert.Single(instructions, static instruction => instruction.Value is SsaBinaryRValue);
        var addValue = Assert.IsType<SsaBinaryRValue>(add.Value);
        var left = Assert.IsType<SsaValueReference>(addValue.Left);
        var right = Assert.IsType<SsaValueReference>(addValue.Right);
        Assert.Equal(left.Name, right.Name);
    }

    [Fact]
    public void OptimizedSsaDoesNotReuseLoadsAcrossStores()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var pointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [],
                    HasBody: true,
                    SupportsDirectCodeGeneration: true,
                    EntryBlockId: 0,
                    Blocks:
                    [
                        new SsaBasicBlock(
                            0,
                            "bb0_entry",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("ptr", pointerType),
                                        valueType,
                                        "*ptr")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("ptr", pointerType),
                                    valueType,
                                    new SsaIntegerConstant(9, valueType)),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("ptr", pointerType),
                                        valueType,
                                        "*ptr")),
                                new SsaValueInstruction(
                                    "v2",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("v0", valueType),
                                        new SsaValueReference("v1", valueType),
                                        valueType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v2", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var loadCount = block.Instructions
            .OfType<SsaValueInstruction>()
            .Count(static instruction => instruction.Value is SsaLoadIndirectRValue);

        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void ConstantPropagationFoldsConstantBranchesInLlvm()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack i32 x = 1;
                if (x == 1) {
                    return 7;
                } else {
                    return 9;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("ret i32 7", llvm);
        Assert.DoesNotContain("icmp eq", llvm);
        Assert.DoesNotContain("br i1", llvm);
    }

    [Fact]
    public void ConstantPropagationFoldsConstantSwitchesInLlvm()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack i32 value = 2;
                switch (value) {
                    case 1:
                        return 1;
                    case 2:
                        return 2;
                    default:
                        return 3;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("ret i32 2", llvm);
        Assert.DoesNotContain("switch i32", llvm);
        Assert.DoesNotContain("icmp eq", llvm);
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }

    private static SsaIrModule GetOptimizedSsa(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        return ssa;
    }

    private static string GetLlvm(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        return llvm.Text;
    }
}
