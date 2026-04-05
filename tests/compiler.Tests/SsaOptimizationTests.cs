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
    public void CleanupRemovesUnusedPureTemporaries()
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
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("arg_value", valueType),
                                        new SsaIntegerConstant(1, valueType),
                                        valueType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(9, valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Instructions);
        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new BigInteger(9), returnValue.Value);
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
        var joinBlock = Assert.Single(function.Blocks);

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
    public void CleanupCanonicalizesEquivalentCommutativeExpressions()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [new TypedParameterSymbol("left", valueType), new TypedParameterSymbol("right", valueType)],
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
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("arg_left", valueType),
                                        new SsaValueReference("arg_right", valueType),
                                        valueType,
                                        "+")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("arg_right", valueType),
                                        new SsaValueReference("arg_left", valueType),
                                        valueType,
                                        "+")),
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
        var instructions = block.Instructions.OfType<SsaValueInstruction>().ToArray();

        Assert.Equal(2, instructions.Length);
        var finalAdd = Assert.IsType<SsaBinaryRValue>(instructions[^1].Value);
        var left = Assert.IsType<SsaValueReference>(finalAdd.Left);
        var right = Assert.IsType<SsaValueReference>(finalAdd.Right);
        Assert.Equal(left.Name, right.Name);
    }

    [Fact]
    public void CleanupRemovesRedundantSameTypeConversions()
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
                                    new SsaConvertRValue(
                                        new SsaValueReference("arg_value", valueType),
                                        valueType,
                                        "(i32)value")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("v0", valueType),
                                        new SsaIntegerConstant(1, valueType),
                                        valueType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v1", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var instructions = block.Instructions.OfType<SsaValueInstruction>().ToArray();

        Assert.Single(instructions);
        var add = Assert.IsType<SsaBinaryRValue>(instructions[0].Value);
        var left = Assert.IsType<SsaValueReference>(add.Left);
        Assert.Equal("arg_value", left.Name);
    }

    [Fact]
    public void CleanupCoalescesEquivalentPhiNodes()
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
                                    "left",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(1, new SsaIntegerConstant(5, valueType)),
                                        new SsaPhiIncoming(2, new SsaIntegerConstant(7, valueType))
                                    ]),
                                new SsaPhi(
                                    "v1_phi",
                                    "right",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(1, new SsaIntegerConstant(5, valueType)),
                                        new SsaPhiIncoming(2, new SsaIntegerConstant(7, valueType))
                                    ])
                            ],
                            [
                                new SsaValueInstruction(
                                    "v2",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("v0_phi", valueType),
                                        new SsaValueReference("v1_phi", valueType),
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
        var joinBlock = Assert.Single(function.Blocks, static block => block.Id == 3);

        Assert.Single(joinBlock.Phis);
        var add = Assert.Single(joinBlock.Instructions.OfType<SsaValueInstruction>());
        var addValue = Assert.IsType<SsaBinaryRValue>(add.Value);
        var left = Assert.IsType<SsaValueReference>(addValue.Left);
        var right = Assert.IsType<SsaValueReference>(addValue.Right);
        Assert.Equal(left.Name, right.Name);
    }

    [Fact]
    public void CleanupSimplifiesBranchWithIdenticalTargets()
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
                                [1, 1],
                                Condition: new SsaValueReference("arg_flag", boolType))),
                        new SsaBasicBlock(
                            1,
                            "bb1_body",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(7, valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var entry = Assert.Single(function.Blocks);

        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaIntegerConstant>(entry.Terminator.Value);
        Assert.Equal(new BigInteger(7), returnValue.Value);
    }

    [Fact]
    public void CleanupSimplifiesDefaultOnlySwitchToGoto()
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
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Switch,
                                [],
                                Condition: new SsaValueReference("arg_value", valueType),
                                SwitchCases: [],
                                DefaultTarget: 1)),
                        new SsaBasicBlock(
                            1,
                            "bb1_body",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(9, valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var entry = Assert.Single(function.Blocks);

        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaIntegerConstant>(entry.Terminator.Value);
        Assert.Equal(new BigInteger(9), returnValue.Value);
    }

    [Fact]
    public void CleanupRemovesUnusedLocalStorageScaffolding()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var localType = StarkTypeSymbols.FixedArray(elementType, 3);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    elementType,
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
                                new SsaAllocateLocalInstruction("values", localType),
                                new SsaLifetimeStartInstruction("values", localType),
                                new SsaStoreLocalInstruction("values", localType, new SsaZeroInitializerValue(localType)),
                                new SsaLifetimeEndInstruction("values", localType)
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(4, elementType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Instructions);
        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new BigInteger(4), returnValue.Value);
    }

    [Fact]
    public void CleanupRemovesEmptyJumpOnlyBlocks()
    {
        var valueType = StarkTypeSymbols.Integer(32);
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
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [1])),
                        new SsaBasicBlock(
                            1,
                            "bb1_trampoline",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [2])),
                        new SsaBasicBlock(
                            2,
                            "bb2_body",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(3, valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.DoesNotContain(function.Blocks, static block => block.Id == 1);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);
        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaIntegerConstant>(entry.Terminator.Value);
        Assert.Equal(new BigInteger(3), returnValue.Value);
    }

    [Fact]
    public void CleanupMergesLinearBlocksWithSinglePredecessor()
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
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [1])),
                        new SsaBasicBlock(
                            1,
                            "bb1_body",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("arg_value", valueType),
                                        new SsaIntegerConstant(1, valueType),
                                        valueType,
                                        "+"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [2])),
                        new SsaBasicBlock(
                            2,
                            "bb2_exit",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Equal(0, block.Id);
        Assert.Single(block.Instructions.OfType<SsaValueInstruction>());
        Assert.Equal(SsaTerminatorKind.Return, block.Terminator.Kind);
        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v0", returnValue.Name);
    }

    [Fact]
    public void CleanupSimplifiesSingleCaseSwitchToBranch()
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
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Switch,
                                [1],
                                Condition: new SsaValueReference("arg_value", valueType),
                                SwitchCases:
                                [
                                    new SsaSwitchCase("case_1", 1, new SsaIntegerConstant(1, valueType))
                                ],
                                DefaultTarget: 2)),
                        new SsaBasicBlock(
                            1,
                            "bb1_case",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(10, valueType))),
                        new SsaBasicBlock(
                            2,
                            "bb2_default",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(20, valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);

        Assert.Equal(SsaTerminatorKind.Branch, entry.Terminator.Kind);
        Assert.Equal(2, entry.Terminator.Targets.Count);
        Assert.Equal(1, entry.Terminator.Targets[0]);
        Assert.Equal(2, entry.Terminator.Targets[1]);
        var condition = Assert.IsType<SsaValueReference>(entry.Terminator.Condition);
        var compare = Assert.Single(entry.Instructions.OfType<SsaValueInstruction>());
        Assert.Equal(compare.ResultName, condition.Name);
        var compareValue = Assert.IsType<SsaBinaryRValue>(compare.Value);
        Assert.Equal(SsaBinaryOperator.Equal, compareValue.Operator);
    }

    [Fact]
    public void CleanupKeepsDuplicateEdgeBranchesWhenSuccessorPhiNeedsBothIncomingValues()
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
                                [1, 1],
                                Condition: new SsaValueReference("arg_flag", boolType))),
                        new SsaBasicBlock(
                            1,
                            "bb1_join",
                            [
                                new SsaPhi(
                                    "v0_phi",
                                    "value",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(0, new SsaIntegerConstant(1, valueType)),
                                        new SsaPhiIncoming(0, new SsaIntegerConstant(2, valueType))
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
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);

        Assert.Equal(SsaTerminatorKind.Branch, entry.Terminator.Kind);
        Assert.Equal([1, 1], entry.Terminator.Targets);
    }

    [Fact]
    public void CleanupNormalizesExhaustiveBoolSwitchToBranch()
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
                                SsaTerminatorKind.Switch,
                                [1, 2],
                                Condition: new SsaValueReference("arg_flag", boolType),
                                SwitchCases:
                                [
                                    new SsaSwitchCase("case_true", 1, new SsaBoolConstant(true)),
                                    new SsaSwitchCase("case_false", 2, new SsaBoolConstant(false))
                                ],
                                DefaultTarget: 3)),
                        new SsaBasicBlock(
                            1,
                            "bb1_true",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(1, valueType))),
                        new SsaBasicBlock(
                            2,
                            "bb2_false",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(2, valueType))),
                        new SsaBasicBlock(
                            3,
                            "bb3_unreachable",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Unreachable, []))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);

        Assert.Equal(SsaTerminatorKind.Branch, entry.Terminator.Kind);
        Assert.DoesNotContain(function.Blocks, static block => block.Id == 3);
    }

    [Fact]
    public void CleanupDropsSwitchCasesThatAlreadyMatchDefaultTarget()
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
                                SsaTerminatorKind.Switch,
                                [1, 2],
                                Condition: new SsaValueReference("arg_flag", boolType),
                                SwitchCases:
                                [
                                    new SsaSwitchCase("case_true", 1, new SsaBoolConstant(true)),
                                    new SsaSwitchCase("case_false", 2, new SsaBoolConstant(false))
                                ],
                                DefaultTarget: 2)),
                        new SsaBasicBlock(
                            1,
                            "bb1_true",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(1, valueType))),
                        new SsaBasicBlock(
                            2,
                            "bb2_default",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(2, valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);

        Assert.Equal(SsaTerminatorKind.Branch, entry.Terminator.Kind);
        Assert.Equal(1, entry.Terminator.Targets[0]);
        Assert.Equal(2, entry.Terminator.Targets[1]);
    }

    [Fact]
    public void OptimizedSsaSimplifiesSingleCaseSwitchBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value) {
                switch (value) {
                    case 1:
                        return 10;
                    default:
                        return 20;
                }
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);
        Assert.Equal(SsaTerminatorKind.Branch, entry.Terminator.Kind);
        Assert.Single(entry.Instructions.OfType<SsaValueInstruction>());

        var llvm = GetLlvm(result);
        Assert.Contains("icmp eq i32 %arg_value, 1", llvm);
        Assert.Contains("br i1", llvm);
        Assert.DoesNotContain("switch i32", llvm);
    }

    [Fact]
    public void OptimizedSsaNormalizesSmallSparseSwitchToCompareChainBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value) {
                switch (value) {
                    case 9:
                        return 90;
                    case 1:
                        return 10;
                    case 5:
                        return 50;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Switch);
        Assert.True(function.Blocks.Count >= 5);

        var llvm = GetLlvm(result);
        Assert.Contains("icmp eq i32 %arg_value, 1", llvm);
        Assert.Contains("icmp eq i32 %arg_value, 5", llvm);
        Assert.Contains("icmp eq i32 %arg_value, 9", llvm);
        Assert.Contains("br i1", llvm);
        Assert.DoesNotContain("switch i32", llvm);
    }

    [Fact]
    public void OptimizedSsaKeepsDenseFourWaySwitchForLlvmLowering()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value) {
                switch (value) {
                    case 1:
                        return 10;
                    case 2:
                        return 20;
                    case 3:
                        return 30;
                    case 4:
                        return 40;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Switch);

        var llvm = GetLlvm(result);
        Assert.Contains("switch i32 %arg_value", llvm);
    }

    [Fact]
    public void CleanupCanonicalizesEarlyReturnDiamonds()
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
                                        new SsaPhiIncoming(1, new SsaIntegerConstant(1, valueType)),
                                        new SsaPhiIncoming(2, new SsaIntegerConstant(2, valueType))
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

        Assert.DoesNotContain(function.Blocks, static block => block.Id == 3);
        var thenBlock = Assert.Single(function.Blocks, static block => block.Id == 1);
        var elseBlock = Assert.Single(function.Blocks, static block => block.Id == 2);

        var thenReturn = Assert.IsType<SsaIntegerConstant>(thenBlock.Terminator.Value);
        var elseReturn = Assert.IsType<SsaIntegerConstant>(elseBlock.Terminator.Value);
        Assert.Equal(SsaTerminatorKind.Return, thenBlock.Terminator.Kind);
        Assert.Equal(SsaTerminatorKind.Return, elseBlock.Terminator.Kind);
        Assert.Equal(new BigInteger(1), thenReturn.Value);
        Assert.Equal(new BigInteger(2), elseReturn.Value);
    }

    [Fact]
    public void OptimizedSsaCanonicalizesReturnPhiJoinsBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(bool flag, i32 value) {
                stack i32 result = value;
                if (flag) {
                    result = result + 1;
                } else {
                    result = result + 2;
                }

                return result;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        Assert.Equal(3, function.Blocks.Count);
        Assert.DoesNotContain(function.Blocks, static block => block.Phis.Count != 0);

        var thenBlock = Assert.Single(function.Blocks, static block => block.Label == "bb1_if_then");
        var elseBlock = Assert.Single(function.Blocks, static block => block.Label == "bb2_if_else");
        Assert.Equal(SsaTerminatorKind.Return, thenBlock.Terminator.Kind);
        Assert.Equal(SsaTerminatorKind.Return, elseBlock.Terminator.Kind);

        var llvm = GetLlvm(result);
        Assert.Contains("br i1", llvm);
        Assert.DoesNotContain(" phi ", llvm);
    }

    [Fact]
    public void CleanupRemovesLoopInvariantSelfReferentialPhiNodes()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [new TypedParameterSymbol("flag", boolType), new TypedParameterSymbol("limit", valueType)],
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
                            new SsaTerminator(SsaTerminatorKind.Goto, [1])),
                        new SsaBasicBlock(
                            1,
                            "bb1_loop_header",
                            [
                                new SsaPhi(
                                    "v0_phi",
                                    "limit",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(0, new SsaValueReference("arg_limit", valueType)),
                                        new SsaPhiIncoming(2, new SsaValueReference("v0_phi", valueType))
                                    ]),
                                new SsaPhi(
                                    "v1_phi",
                                    "index",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(0, new SsaIntegerConstant(0, valueType)),
                                        new SsaPhiIncoming(2, new SsaValueReference("v2", valueType))
                                    ])
                            ],
                            [
                                new SsaValueInstruction(
                                    "v3",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.LessThan,
                                        new SsaValueReference("v1_phi", valueType),
                                        new SsaValueReference("v0_phi", valueType),
                                        boolType,
                                        "<"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Branch,
                                [2, 3],
                                Condition: new SsaValueReference("v3", boolType))),
                        new SsaBasicBlock(
                            2,
                            "bb2_loop_body",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v2",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("v1_phi", valueType),
                                        new SsaIntegerConstant(1, valueType),
                                        valueType,
                                        "+"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [1])),
                        new SsaBasicBlock(
                            3,
                            "bb3_exit",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0_phi", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var header = Assert.Single(function.Blocks, static block => block.Id == 1);
        var exit = Assert.Single(function.Blocks, static block => block.Id == 3);

        Assert.Single(header.Phis);
        Assert.Equal("v1_phi", header.Phis[0].ResultName);

        var compare = Assert.Single(header.Instructions.OfType<SsaValueInstruction>());
        var compareValue = Assert.IsType<SsaBinaryRValue>(compare.Value);
        var right = Assert.IsType<SsaValueReference>(compareValue.Right);
        Assert.Equal("arg_limit", right.Name);

        var returnValue = Assert.IsType<SsaValueReference>(exit.Terminator.Value);
        Assert.Equal("arg_limit", returnValue.Name);
    }

    [Fact]
    public void OptimizedSsaRemovesLoopInvariantHeaderPhisBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 n) {
                stack i32 sum = 0;
                stack i32 i = 0;
                while willexit (i < n) {
                    sum = sum + i;
                    i = i + 1;
                }

                return sum;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        var header = Assert.Single(function.Blocks, static block => block.Label == "bb1_while_willexit_cond");

        Assert.Equal(2, header.Phis.Count);
        Assert.DoesNotContain(
            header.Phis,
            static phi => phi.Incomings.Any(incoming => incoming.Value is SsaValueReference reference
                                                       && reference.Name == phi.ResultName));

        var llvm = GetLlvm(result);
        Assert.Contains("icmp slt i32", llvm);
        Assert.DoesNotContain("= phi i32 [ 0, %bb0 ], [ %v0_phi, %bb2 ]", llvm);
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
    public void OptimizedSsaFoldsConstantAggregateInitializerAccesses()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32 Left;
                i32 Right;
            }

            fn i32 Run() {
                stack Pair pair = new Pair() { Left = 1 + 2, Right = 3 * 4 };
                return pair.Left + pair.Right;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        var block = Assert.Single(function.Blocks);
        Assert.Empty(block.Instructions);

        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new BigInteger(15), returnValue.Value);

        var llvm = GetLlvm(result);
        Assert.Contains("ret i32 15", llvm);
        Assert.DoesNotContain("insertvalue %Pair", llvm);
        Assert.DoesNotContain("extractvalue %Pair", llvm);
    }

    [Fact]
    public void OptimizedSsaRemovesDeadAddressableLocalStorageBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack i32[3] values = { 1, 2, 3 };
                return 4;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetOptimizedSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();
        Assert.DoesNotContain(
            instructions,
            static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "values" }
                or SsaLifetimeStartInstruction { LocalName: "values" }
                or SsaLifetimeEndInstruction { LocalName: "values" }
                or SsaStoreLocalInstruction { LocalName: "values" });

        var llvm = GetLlvm(result);
        Assert.DoesNotContain("%slot_values = alloca [3 x i32]", llvm);
        Assert.DoesNotContain("call void @llvm.lifetime.start.p0(i64 12, ptr %slot_values)", llvm);
        Assert.DoesNotContain("call void @llvm.lifetime.end.p0(i64 12, ptr %slot_values)", llvm);
        Assert.DoesNotContain("store [3 x i32]", llvm);
        Assert.Contains("ret i32 4", llvm);
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
