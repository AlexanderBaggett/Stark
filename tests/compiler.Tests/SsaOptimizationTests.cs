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
    public void CleanupPrunesPhiIncomingsForRemovedCfgEdges()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var boolType = StarkTypeSymbols.Bool;
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
                                [1, 3],
                                Condition: new SsaValueReference("arg_flag", boolType))),
                        new SsaBasicBlock(
                            1,
                            "bb1_live_predecessor",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [2])),
                        new SsaBasicBlock(
                            2,
                            "bb2_join",
                            [
                                new SsaPhi(
                                    "v0",
                                    "value",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(0, new SsaIntegerConstant(10, valueType)),
                                        new SsaPhiIncoming(1, new SsaIntegerConstant(20, valueType))
                                    ])
                            ],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType))),
                        new SsaBasicBlock(
                            3,
                            "bb3_exit",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Phis.SelectMany(static phi => phi.Incomings)),
            static incoming => incoming.PredecessorBlockId == 0);
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
    public void CleanupRemovesIntegerAlgebraicIdentities()
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
                                        new SsaIntegerConstant(BigInteger.Zero, valueType),
                                        valueType,
                                        "+")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Multiply,
                                        new SsaIntegerConstant(BigInteger.One, valueType),
                                        new SsaValueReference("v0", valueType),
                                        valueType,
                                        "*")),
                                new SsaValueInstruction(
                                    "v2",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.BitwiseAnd,
                                        new SsaValueReference("v1", valueType),
                                        new SsaIntegerConstant(-BigInteger.One, valueType),
                                        valueType,
                                        "&")),
                                new SsaValueInstruction(
                                    "v3",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.ShiftLeft,
                                        new SsaValueReference("v2", valueType),
                                        new SsaIntegerConstant(BigInteger.Zero, valueType),
                                        valueType,
                                        "<<")),
                                new SsaValueInstruction(
                                    "v4",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.BitwiseXor,
                                        new SsaValueReference("v3", valueType),
                                        new SsaIntegerConstant(BigInteger.Zero, valueType),
                                        valueType,
                                        "^"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v4", valueType)))
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
    public void CleanupRemovesIntegerModuloByOne()
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
                                        SsaBinaryOperator.Modulo,
                                        new SsaValueReference("arg_value", valueType),
                                        new SsaIntegerConstant(BigInteger.One, valueType),
                                        valueType,
                                        "%"))
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
        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(BigInteger.Zero, returnValue.Value);
    }

    [Fact]
    public void CleanupRemovesSameOperandDivisionAndModuloWhenRangeExcludesZero()
    {
        var resultType = StarkTypeSymbols.Integer(32);
        var nonZeroType = StarkTypeSymbols.Integer(32, BigInteger.One, new BigInteger(100));
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    resultType,
                    [new TypedParameterSymbol("value", nonZeroType)],
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
                                        SsaBinaryOperator.Divide,
                                        new SsaValueReference("arg_value", nonZeroType),
                                        new SsaValueReference("arg_value", nonZeroType),
                                        resultType,
                                        "/")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Modulo,
                                        new SsaValueReference("arg_value", nonZeroType),
                                        new SsaValueReference("arg_value", nonZeroType),
                                        resultType,
                                        "%")),
                                new SsaValueInstruction(
                                    "v2",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("v0", resultType),
                                        new SsaValueReference("v1", resultType),
                                        resultType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v2", resultType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.DoesNotContain(
            function.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaBinaryRValue>(),
            static binary => binary.Operator is SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo);
    }

    [Fact]
    public void CleanupRemovesSameOperandIntegerComparisons()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    boolType,
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
                                CreateComparison("v0", SsaBinaryOperator.Equal),
                                CreateComparison("v1", SsaBinaryOperator.NotEqual),
                                CreateComparison("v2", SsaBinaryOperator.LessThan),
                                CreateComparison("v3", SsaBinaryOperator.LessThanOrEqual),
                                CreateComparison("v4", SsaBinaryOperator.GreaterThan),
                                CreateComparison("v5", SsaBinaryOperator.GreaterThanOrEqual)
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", boolType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.DoesNotContain(
            function.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaBinaryRValue>(),
            static binary => binary.Operator is SsaBinaryOperator.Equal
                or SsaBinaryOperator.NotEqual
                or SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual);

        var block = Assert.Single(function.Blocks);
        var returnValue = Assert.IsType<SsaBoolConstant>(block.Terminator.Value);
        Assert.True(returnValue.Value);

        SsaValueInstruction CreateComparison(string resultName, SsaBinaryOperator op)
        {
            return new SsaValueInstruction(
                resultName,
                new SsaBinaryRValue(
                    op,
                    new SsaValueReference("arg_value", valueType),
                    new SsaValueReference("arg_value", valueType),
                    boolType,
                    op.ToString()));
        }
    }

    [Fact]
    public void CleanupRemovesModuloAndDivisionWhenStaticRangeIsBelowPositiveDivisor()
    {
        var resultType = StarkTypeSymbols.Integer(32);
        var slotType = StarkTypeSymbols.Integer(32, BigInteger.Zero, new BigInteger(7));
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    resultType,
                    [new TypedParameterSymbol("slot", slotType)],
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
                                        SsaBinaryOperator.Modulo,
                                        new SsaValueReference("arg_slot", slotType),
                                        new SsaIntegerConstant(new BigInteger(8), resultType),
                                        resultType,
                                        "%")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Divide,
                                        new SsaValueReference("arg_slot", slotType),
                                        new SsaIntegerConstant(new BigInteger(8), resultType),
                                        resultType,
                                        "/")),
                                new SsaValueInstruction(
                                    "v2",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("v0", resultType),
                                        new SsaValueReference("v1", resultType),
                                        resultType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v2", resultType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.DoesNotContain(
            function.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaBinaryRValue>(),
            static binary => binary.Operator is SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo);
    }

    [Fact]
    public void CleanupForwardsAggregateFieldThroughPhiWhenIncomingFieldsMatch()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("flag", boolType),
                        new TypedParameterSymbol("value", valueType)
                    ],
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
                            "bb1_left",
                            [],
                            [
                                new SsaValueInstruction(
                                    "left_value",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaValueReference("arg_value", valueType),
                                        aggregateType,
                                        "left.Value")),
                                new SsaValueInstruction(
                                    "left_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaValueReference("left_value", aggregateType),
                                        "Count",
                                        1,
                                        new SsaIntegerConstant(BigInteger.One, valueType),
                                        aggregateType,
                                        "left.Count"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_right",
                            [],
                            [
                                new SsaValueInstruction(
                                    "right_value",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaValueReference("arg_value", valueType),
                                        aggregateType,
                                        "right.Value")),
                                new SsaValueInstruction(
                                    "right_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaValueReference("right_value", aggregateType),
                                        "Count",
                                        1,
                                        new SsaIntegerConstant(new BigInteger(2), valueType),
                                        aggregateType,
                                        "right.Count"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [
                                new SsaPhi(
                                    "pair_phi",
                                    "pair",
                                    aggregateType,
                                    [
                                        new SsaPhiIncoming(1, new SsaValueReference("left_pair", aggregateType)),
                                        new SsaPhiIncoming(2, new SsaValueReference("right_pair", aggregateType))
                                    ])
                            ],
                            [
                                new SsaValueInstruction(
                                    "result",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("pair_phi", aggregateType),
                                        "Value",
                                        0,
                                        valueType,
                                        "pair.Value"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("result", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var returnBlock = Assert.Single(optimized.Functions)
            .Blocks.Single(static block => block.Terminator.Kind == SsaTerminatorKind.Return);

        Assert.DoesNotContain(
            optimized.Functions.SelectMany(static function => function.Blocks)
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaExtractFieldRValue);
        var returnValue = Assert.IsType<SsaValueReference>(returnBlock.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void CleanupKeepsAggregateFieldExtractThroughPhiWhenIncomingFieldsDiffer()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
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
                            "bb1_left",
                            [],
                            [
                                new SsaValueInstruction(
                                    "left_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaIntegerConstant(BigInteger.One, valueType),
                                        aggregateType,
                                        "left.Value"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_right",
                            [],
                            [
                                new SsaValueInstruction(
                                    "right_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaIntegerConstant(new BigInteger(2), valueType),
                                        aggregateType,
                                        "right.Value"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [
                                new SsaPhi(
                                    "pair_phi",
                                    "pair",
                                    aggregateType,
                                    [
                                        new SsaPhiIncoming(1, new SsaValueReference("left_pair", aggregateType)),
                                        new SsaPhiIncoming(2, new SsaValueReference("right_pair", aggregateType))
                                    ])
                            ],
                            [
                                new SsaValueInstruction(
                                    "result",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("pair_phi", aggregateType),
                                        "Value",
                                        0,
                                        valueType,
                                        "pair.Value"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("result", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);

        Assert.Contains(
            optimized.Functions.SelectMany(static function => function.Blocks)
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaExtractFieldRValue);
    }

    [Fact]
    public void CleanupForwardsAggregateIndexThroughPhiWhenIncomingElementsMatch()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var arrayType = StarkTypeSymbols.FixedArray(valueType, 2);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("flag", boolType),
                        new TypedParameterSymbol("value", valueType)
                    ],
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
                            "bb1_left",
                            [],
                            [
                                new SsaValueInstruction(
                                    "left_array",
                                    new SsaInsertIndexRValue(
                                        new SsaZeroInitializerValue(arrayType),
                                        0,
                                        IndexedElementOperationFamily.FixedArrayElement,
                                        new SsaValueReference("arg_value", valueType),
                                        arrayType,
                                        "left[0]"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_right",
                            [],
                            [
                                new SsaValueInstruction(
                                    "right_array",
                                    new SsaInsertIndexRValue(
                                        new SsaZeroInitializerValue(arrayType),
                                        0,
                                        IndexedElementOperationFamily.FixedArrayElement,
                                        new SsaValueReference("arg_value", valueType),
                                        arrayType,
                                        "right[0]"))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [
                                new SsaPhi(
                                    "array_phi",
                                    "values",
                                    arrayType,
                                    [
                                        new SsaPhiIncoming(1, new SsaValueReference("left_array", arrayType)),
                                        new SsaPhiIncoming(2, new SsaValueReference("right_array", arrayType))
                                    ])
                            ],
                            [
                                new SsaValueInstruction(
                                    "result",
                                    new SsaExtractIndexRValue(
                                        new SsaValueReference("array_phi", arrayType),
                                        0,
                                        IndexedElementOperationFamily.FixedArrayElement,
                                        valueType,
                                        "values[0]"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("result", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var returnBlock = Assert.Single(optimized.Functions)
            .Blocks.Single(static block => block.Terminator.Kind == SsaTerminatorKind.Return);

        Assert.DoesNotContain(
            optimized.Functions.SelectMany(static function => function.Blocks)
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaExtractIndexRValue);
        var returnValue = Assert.IsType<SsaValueReference>(returnBlock.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void CleanupForwardsAggregateFieldThroughSelectWhenSelectedFieldsMatch()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("flag", boolType),
                        new TypedParameterSymbol("value", valueType)
                    ],
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
                                    "left_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaValueReference("arg_value", valueType),
                                        aggregateType,
                                        "left.Value")),
                                new SsaValueInstruction(
                                    "right_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaValueReference("arg_value", valueType),
                                        aggregateType,
                                        "right.Value")),
                                new SsaValueInstruction(
                                    "selected_pair",
                                    new SsaSelectRValue(
                                        new SsaValueReference("arg_flag", boolType),
                                        new SsaValueReference("left_pair", aggregateType),
                                        new SsaValueReference("right_pair", aggregateType),
                                        aggregateType,
                                        "flag ? left : right")),
                                new SsaValueInstruction(
                                    "result",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("selected_pair", aggregateType),
                                        "Value",
                                        0,
                                        valueType,
                                        "pair.Value"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("result", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var block = Assert.Single(Assert.Single(optimized.Functions).Blocks);

        Assert.Empty(block.Instructions);
        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void CleanupKeepsAggregateFieldExtractThroughSelectWhenSelectedFieldsDiffer()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
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
                            [
                                new SsaValueInstruction(
                                    "left_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaIntegerConstant(BigInteger.One, valueType),
                                        aggregateType,
                                        "left.Value")),
                                new SsaValueInstruction(
                                    "right_pair",
                                    new SsaInsertFieldRValue(
                                        new SsaZeroInitializerValue(aggregateType),
                                        "Value",
                                        0,
                                        new SsaIntegerConstant(new BigInteger(2), valueType),
                                        aggregateType,
                                        "right.Value")),
                                new SsaValueInstruction(
                                    "selected_pair",
                                    new SsaSelectRValue(
                                        new SsaValueReference("arg_flag", boolType),
                                        new SsaValueReference("left_pair", aggregateType),
                                        new SsaValueReference("right_pair", aggregateType),
                                        aggregateType,
                                        "flag ? left : right")),
                                new SsaValueInstruction(
                                    "result",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("selected_pair", aggregateType),
                                        "Value",
                                        0,
                                        valueType,
                                        "pair.Value"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("result", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);

        Assert.Contains(
            optimized.Functions.SelectMany(static function => function.Blocks)
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaExtractFieldRValue);
    }

    [Fact]
    public void CleanupForwardsAggregateIndexThroughSelectWhenSelectedElementsMatch()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var arrayType = StarkTypeSymbols.FixedArray(valueType, 2);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("flag", boolType),
                        new TypedParameterSymbol("value", valueType)
                    ],
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
                                    "left_array",
                                    new SsaInsertIndexRValue(
                                        new SsaZeroInitializerValue(arrayType),
                                        0,
                                        IndexedElementOperationFamily.FixedArrayElement,
                                        new SsaValueReference("arg_value", valueType),
                                        arrayType,
                                        "left[0]")),
                                new SsaValueInstruction(
                                    "right_array",
                                    new SsaInsertIndexRValue(
                                        new SsaZeroInitializerValue(arrayType),
                                        0,
                                        IndexedElementOperationFamily.FixedArrayElement,
                                        new SsaValueReference("arg_value", valueType),
                                        arrayType,
                                        "right[0]")),
                                new SsaValueInstruction(
                                    "selected_array",
                                    new SsaSelectRValue(
                                        new SsaValueReference("arg_flag", boolType),
                                        new SsaValueReference("left_array", arrayType),
                                        new SsaValueReference("right_array", arrayType),
                                        arrayType,
                                        "flag ? left : right")),
                                new SsaValueInstruction(
                                    "result",
                                    new SsaExtractIndexRValue(
                                        new SsaValueReference("selected_array", arrayType),
                                        0,
                                        IndexedElementOperationFamily.FixedArrayElement,
                                        valueType,
                                        "values[0]"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("result", valueType)))
                    ])
            ]);

        var optimized = new SsaCleanupOptimizer().Optimize(module);
        var block = Assert.Single(Assert.Single(optimized.Functions).Blocks);

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

        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaValueReference>(entry.Terminator.Value);
        var instructions = entry.Instructions.OfType<SsaValueInstruction>().ToArray();
        var compare = Assert.Single(instructions, static instruction => instruction.Value is SsaBinaryRValue);
        var compareValue = Assert.IsType<SsaBinaryRValue>(compare.Value);
        Assert.Equal(SsaBinaryOperator.Equal, compareValue.Operator);
        var selectInstruction = Assert.Single(instructions, static instruction => instruction.Value is SsaSelectRValue);
        Assert.Equal(selectInstruction.ResultName, returnValue.Name);
        var select = Assert.IsType<SsaSelectRValue>(selectInstruction.Value);
        var selectCondition = Assert.IsType<SsaValueReference>(select.Condition);
        Assert.Equal(compare.ResultName, selectCondition.Name);
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

        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaValueReference>(entry.Terminator.Value);
        var selectInstruction = Assert.Single(entry.Instructions.OfType<SsaValueInstruction>());
        Assert.Equal(selectInstruction.ResultName, returnValue.Name);
        Assert.IsType<SsaSelectRValue>(selectInstruction.Value);
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

        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaValueReference>(entry.Terminator.Value);
        var selectInstruction = Assert.Single(entry.Instructions.OfType<SsaValueInstruction>());
        Assert.Equal(selectInstruction.ResultName, returnValue.Name);
        Assert.IsType<SsaSelectRValue>(selectInstruction.Value);
    }

    [Fact]
    public void OptimizedSsaSimplifiesSingleCaseSwitchBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value) {
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
        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaValueReference>(entry.Terminator.Value);
        var instructions = entry.Instructions.OfType<SsaValueInstruction>().ToArray();
        Assert.Contains(instructions, static instruction => instruction.Value is SsaBinaryRValue);
        var selectInstruction = Assert.Single(instructions, static instruction => instruction.Value is SsaSelectRValue);
        Assert.Equal(selectInstruction.ResultName, returnValue.Name);

        var llvm = GetLlvm(result);
        Assert.Contains("icmp eq i32 %arg_value, 1", llvm);
        Assert.Contains("select i1", llvm);
        Assert.DoesNotContain("br i1", llvm);
        Assert.DoesNotContain("switch i32", llvm);
    }

    [Fact]
    public void OptimizedSsaNormalizesSmallSparseSwitchToCompareChainBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value) {
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

            fn i32[min max] Run(i32[min max] value) {
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

        var entry = Assert.Single(function.Blocks);
        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaValueReference>(entry.Terminator.Value);
        var selectInstruction = Assert.Single(entry.Instructions.OfType<SsaValueInstruction>());
        Assert.Equal(selectInstruction.ResultName, returnValue.Name);
        var select = Assert.IsType<SsaSelectRValue>(selectInstruction.Value);
        var whenTrue = Assert.IsType<SsaIntegerConstant>(select.WhenTrue);
        var whenFalse = Assert.IsType<SsaIntegerConstant>(select.WhenFalse);
        Assert.Equal(new BigInteger(1), whenTrue.Value);
        Assert.Equal(new BigInteger(2), whenFalse.Value);
    }

    [Fact]
    public void OptimizedSsaCanonicalizesReturnPhiJoinsBeforeLlvmEmission()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(bool flag, i32[min max] value) {
                stack mut i32[min max] result = value;
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

            fn i32[min max] Run(i32[min max] n) {
                stack mut i32[min max] sum = 0;
                stack mut i32[min max] i = 0;
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

            fn i32[min max] Run(bool flag) {
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

        Assert.Equal(SsaTerminatorKind.Return, entry.Terminator.Kind);
        var returnValue = Assert.IsType<SsaValueReference>(entry.Terminator.Value);
        var selectInstruction = Assert.Single(
            instructions,
            static instruction => instruction.Value is SsaSelectRValue);
        Assert.Equal(selectInstruction.ResultName, returnValue.Name);

        var select = Assert.IsType<SsaSelectRValue>(selectInstruction.Value);
        var selectCondition = Assert.IsType<SsaValueReference>(select.Condition);
        Assert.Equal("arg_flag", selectCondition.Name);
    }

    [Fact]
    public void OptimizedSsaFoldsConstantArithmetic()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run() {
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
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run() {
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

            fn i32[min max] Run() {
                stack i32[min max][3] values = { 1, 2, 3 };
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

            fn i32[min max] Run(i32[min max][] view, i32[min max] index) {
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
    public void FactDrivenBranchPruningThreadsProvenEdgesThroughBranchOnlyBlocks()
    {
        var boolType = StarkTypeSymbols.Bool;
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
                                        SsaBinaryOperator.LessThan,
                                        new SsaValueReference("arg_value", valueType),
                                        new SsaIntegerConstant(10, valueType),
                                        boolType,
                                        "<"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Branch,
                                [1, 4],
                                Condition: new SsaValueReference("v0", boolType))),
                        new SsaBasicBlock(
                            1,
                            "bb1_check",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.LessThan,
                                        new SsaValueReference("arg_value", valueType),
                                        new SsaIntegerConstant(20, valueType),
                                        boolType,
                                        "<"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Branch,
                                [2, 3],
                                Condition: new SsaValueReference("v1", boolType))),
                        new SsaBasicBlock(
                            2,
                            "bb2_fast",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(1, valueType))),
                        new SsaBasicBlock(
                            3,
                            "bb3_slow",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(2, valueType))),
                        new SsaBasicBlock(
                            4,
                            "bb4_default",
                            [],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(3, valueType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var optimized = new SsaFactDrivenBranchPruner().Optimize(module, facts);
        var function = Assert.Single(optimized.Functions);
        var entry = Assert.Single(function.Blocks, static block => block.Id == 0);

        Assert.Equal(SsaTerminatorKind.Branch, entry.Terminator.Kind);
        Assert.Equal([2, 4], entry.Terminator.Targets);
    }

    [Fact]
    public void ValueFactsJoinLoopPhiInputsToAStableRange()
    {
        var valueType = StarkTypeSymbols.Integer(32, BigInteger.Zero, new BigInteger(10));
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
                            "bb1_loop",
                            [
                                new SsaPhi(
                                    "loop_phi",
                                    "loop",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(0, new SsaIntegerConstant(0, valueType)),
                                        new SsaPhiIncoming(2, new SsaValueReference("loop_backedge", valueType))
                                    ])
                            ],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [2])),
                        new SsaBasicBlock(
                            2,
                            "bb2_backedge",
                            [],
                            [
                                new SsaValueInstruction(
                                    "loop_backedge",
                                    new SsaUseRValue(new SsaIntegerConstant(10, valueType)))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [1]))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var phiFacts = functionFacts.Values["loop_phi"];

        Assert.Equal(SsaFactLatticeKind.Known, phiFacts.IntegerRangeKind);
        Assert.NotNull(phiFacts.IntegerRange);
        Assert.Equal(BigInteger.Zero, phiFacts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(10), phiFacts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsIgnoreUnreachablePhiInputs()
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
                            new SsaTerminator(SsaTerminatorKind.Goto, [2])),
                        new SsaBasicBlock(
                            1,
                            "bb1_unreachable",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [2])),
                        new SsaBasicBlock(
                            2,
                            "bb2_join",
                            [
                                new SsaPhi(
                                    "result_phi",
                                    "result",
                                    valueType,
                                    [
                                        new SsaPhiIncoming(0, new SsaIntegerConstant(2, valueType)),
                                        new SsaPhiIncoming(1, new SsaIntegerConstant(100, valueType))
                                    ])
                            ],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("result_phi", valueType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var phiFacts = functionFacts.Values["result_phi"];

        Assert.Equal(SsaFactLatticeKind.Known, phiFacts.IntegerRangeKind);
        Assert.NotNull(phiFacts.IntegerRange);
        Assert.Equal(new BigInteger(2), phiFacts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(2), phiFacts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsCaptureTextLiteralPayloadFacts()
    {
        var asciiType = StarkTypeSymbols.Ascii;
        var unicodeType = StarkTypeSymbols.Unicode;
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    unicodeType,
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
                                    "ascii_literal",
                                    new SsaUseRValue(new SsaStringConstant("\"Stark\"", asciiType))),
                                new SsaValueInstruction(
                                    "unicode_literal",
                                    new SsaUseRValue(new SsaStringConstant("\"λ\"", unicodeType)))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("unicode_literal", unicodeType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        var asciiPayload = functionFacts.Values["ascii_literal"].TextLiteralPayload;
        Assert.NotNull(asciiPayload);
        Assert.Equal(SsaFactLatticeKind.Known, functionFacts.Values["ascii_literal"].TextLiteralPayloadKind);
        Assert.Equal("Stark", asciiPayload!.DecodedText);
        Assert.Equal("537461726B", asciiPayload.Utf8PayloadHex);
        Assert.Equal("00000053,00000074,00000061,00000072,0000006B", asciiPayload.Utf32PayloadHex);
        Assert.True(asciiPayload.IsAsciiOnly);
        Assert.Equal(5, asciiPayload.Utf8Length);
        Assert.Equal(5, asciiPayload.Utf32Length);

        var unicodePayload = functionFacts.Values["unicode_literal"].TextLiteralPayload;
        Assert.NotNull(unicodePayload);
        Assert.Equal(SsaFactLatticeKind.Known, functionFacts.Values["unicode_literal"].TextLiteralPayloadKind);
        Assert.Equal("λ", unicodePayload!.DecodedText);
        Assert.Equal("CEBB", unicodePayload.Utf8PayloadHex);
        Assert.Equal("000003BB", unicodePayload.Utf32PayloadHex);
        Assert.False(unicodePayload.IsAsciiOnly);
        Assert.Equal(2, unicodePayload.Utf8Length);
        Assert.Equal(1, unicodePayload.Utf32Length);
    }

    [Fact]
    public void ValueFactsPreserveOnlyIdenticalTextLiteralPayloadsThroughPhi()
    {
        var textType = StarkTypeSymbols.Ascii;
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    textType,
                    [new TypedParameterSymbol("choose", StarkTypeSymbols.Bool)],
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
                                Condition: new SsaValueReference("arg_choose", StarkTypeSymbols.Bool))),
                        new SsaBasicBlock(
                            1,
                            "bb1_left",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_right",
                            [],
                            [],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [
                                new SsaPhi(
                                    "same_phi",
                                    "same",
                                    textType,
                                    [
                                        new SsaPhiIncoming(1, new SsaStringConstant("\"same\"", textType)),
                                        new SsaPhiIncoming(2, new SsaStringConstant("\"same\"", textType))
                                    ]),
                                new SsaPhi(
                                    "mixed_phi",
                                    "mixed",
                                    textType,
                                    [
                                        new SsaPhiIncoming(1, new SsaStringConstant("\"left\"", textType)),
                                        new SsaPhiIncoming(2, new SsaStringConstant("\"right\"", textType))
                                    ])
                            ],
                            [],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("same_phi", textType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        var samePayload = functionFacts.Values["same_phi"].TextLiteralPayload;
        Assert.NotNull(samePayload);
        Assert.Equal(SsaFactLatticeKind.Known, functionFacts.Values["same_phi"].TextLiteralPayloadKind);
        Assert.Equal("same", samePayload!.DecodedText);
        Assert.Equal(SsaFactLatticeKind.Known, functionFacts.Values["same_phi"].LengthKind);
        var sameLength = functionFacts.Values["same_phi"].LengthRange;
        Assert.NotNull(sameLength);
        Assert.Equal(new BigInteger(4), sameLength!.Min);
        Assert.Equal(new BigInteger(4), sameLength.Max);

        Assert.Equal(SsaFactLatticeKind.Unknown, functionFacts.Values["mixed_phi"].TextLiteralPayloadKind);
        Assert.Null(functionFacts.Values["mixed_phi"].TextLiteralPayload);
        Assert.Equal(SsaFactLatticeKind.Known, functionFacts.Values["mixed_phi"].LengthKind);
        var mixedLength = functionFacts.Values["mixed_phi"].LengthRange;
        Assert.NotNull(mixedLength);
        Assert.Equal(new BigInteger(4), mixedLength!.Min);
        Assert.Equal(new BigInteger(5), mixedLength.Max);
    }

    [Fact]
    public void ValueFactsDeriveTextSliceLiteralPayloads()
    {
        var textType = StarkTypeSymbols.Ascii;
        var indexType = StarkTypeSymbols.Integer(64);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    textType,
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
                                    "slice",
                                    new SsaTextSliceRValue(
                                        new SsaStringConstant("\"abcdef\"", textType),
                                        new SsaIntegerConstant(2, indexType),
                                        new SsaIntegerConstant(3, indexType),
                                        textType,
                                        "\"abcdef\"[2, 3]")),
                                new SsaStoreLocalInstruction(
                                    "local",
                                    textType,
                                    new SsaValueReference("slice", textType)),
                                new SsaValueInstruction(
                                    "loaded",
                                    new SsaLoadLocalRValue("local", textType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("loaded", textType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        AssertSlicedPayload(functionFacts.Values["slice"]);
        AssertSlicedPayload(functionFacts.Values["loaded"]);

        static void AssertSlicedPayload(SsaValueFacts facts)
        {
            Assert.Equal(SsaFactLatticeKind.Known, facts.LengthKind);
            Assert.Equal(new BigInteger(3), facts.LengthRange!.Min);
            Assert.Equal(new BigInteger(3), facts.LengthRange.Max);
            Assert.Equal(SsaFactLatticeKind.Known, facts.TextLiteralPayloadKind);
            Assert.NotNull(facts.TextLiteralPayload);
            Assert.Equal("cde", facts.TextLiteralPayload!.DecodedText);
            Assert.Equal("636465", facts.TextLiteralPayload.Utf8PayloadHex);
            Assert.Equal("00000063,00000064,00000065", facts.TextLiteralPayload.Utf32PayloadHex);
        }
    }

    [Fact]
    public void ValueFactsKeepDynamicTextSlicePayloadUnknown()
    {
        var textType = StarkTypeSymbols.Ascii;
        var indexType = StarkTypeSymbols.Integer(64);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    textType,
                    [new TypedParameterSymbol("start", indexType)],
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
                                    "slice",
                                    new SsaTextSliceRValue(
                                        new SsaStringConstant("\"abcdef\"", textType),
                                        new SsaValueReference("arg_start", indexType),
                                        new SsaIntegerConstant(3, indexType),
                                        textType,
                                        "\"abcdef\"[start, 3]"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("slice", textType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var sliceFacts = Assert.Single(facts.Functions.Values).Values["slice"];

        Assert.Equal(SsaFactLatticeKind.Known, sliceFacts.LengthKind);
        Assert.Equal(new BigInteger(3), sliceFacts.LengthRange!.Min);
        Assert.Equal(new BigInteger(3), sliceFacts.LengthRange.Max);
        Assert.Equal(SsaFactLatticeKind.Unknown, sliceFacts.TextLiteralPayloadKind);
        Assert.Null(sliceFacts.TextLiteralPayload);
    }

    [Fact]
    public void ValueFactsPublishBoundedRawPointerParameterRegions()
    {
        var countType = StarkTypeSymbols.Integer(8, BigInteger.One, new BigInteger(10), isUnsigned: true);
        var elementType = StarkTypeSymbols.Integer(32);
        var pointerType = StarkTypeSymbols.RawPointer(elementType, isMutable: false);
        var sliceType = StarkTypeSymbols.Slice(elementType);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    sliceType,
                    [
                        new TypedParameterSymbol("input", pointerType, RawPointerElementCountExpression: "count"),
                        new TypedParameterSymbol("count", countType)
                    ],
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
                                    "alias",
                                    new SsaUseRValue(new SsaValueReference("arg_input", pointerType))),
                                new SsaValueInstruction(
                                    "slice",
                                    new SsaMakeSliceFromPointerRValue(
                                        new SsaValueReference("alias", pointerType),
                                        new SsaValueReference("arg_count", countType),
                                        sliceType,
                                        "slice(input, count)"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("slice", sliceType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var inputFacts = functionFacts.Values["arg_input"];
        var aliasFacts = functionFacts.Values["alias"];
        var sliceFacts = functionFacts.Values["slice"];

        Assert.Equal(SsaNullabilityFactKind.NonNull, inputFacts.Nullability);
        Assert.Equal(SsaFactLatticeKind.Known, inputFacts.PointerAlignmentKind);
        Assert.Equal(4, inputFacts.PointerAlignmentBytes);
        AssertBoundedRegion(inputFacts, "arg_count", countType, BigInteger.One, new BigInteger(10), 4);
        AssertBoundedRegion(aliasFacts, "arg_count", countType, BigInteger.One, new BigInteger(10), 4);

        Assert.Equal(SsaFactLatticeKind.Known, sliceFacts.LengthKind);
        Assert.Equal(BigInteger.One, sliceFacts.LengthRange!.Min);
        Assert.Equal(new BigInteger(10), sliceFacts.LengthRange.Max);
        AssertBoundedRegion(sliceFacts, "arg_count", countType, BigInteger.One, new BigInteger(10), 4);
    }

    [Fact]
    public void ValueFactsKeepZeroAllowedBoundedRawPointersNullable()
    {
        var countType = StarkTypeSymbols.Integer(8, BigInteger.Zero, new BigInteger(10), isUnsigned: true);
        var elementType = StarkTypeSymbols.Integer(32);
        var pointerType = StarkTypeSymbols.RawPointer(elementType, isMutable: false);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    StarkTypeSymbols.Void,
                    [
                        new TypedParameterSymbol("input", pointerType, RawPointerElementCountExpression: "count"),
                        new TypedParameterSymbol("count", countType)
                    ],
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
                            new SsaTerminator(SsaTerminatorKind.Return, []))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var inputFacts = Assert.Single(facts.Functions.Values).Values["arg_input"];

        Assert.Equal(SsaNullabilityFactKind.Unknown, inputFacts.Nullability);
        Assert.Equal(SsaFactLatticeKind.Unknown, inputFacts.PointerAlignmentKind);
        AssertBoundedRegion(inputFacts, "arg_count", countType, BigInteger.Zero, new BigInteger(10), 4);
    }

    [Fact]
    public void ValueFactsTrackDynamicStorageAllocationFieldsAndDataPointer()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var countType = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - BigInteger.One, isUnsigned: true);
        var storageType = StarkTypeSymbols.Dynamic(elementType);
        var dataPointerType = StarkTypeSymbols.RawPointer(elementType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    countType,
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
                                    "storage",
                                    new SsaDynamicStorageAllocationRValue(
                                        new SsaIntegerConstant(4, countType),
                                        storageType,
                                        "new(4)")),
                                new SsaValueInstruction(
                                    "length",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("storage", storageType),
                                        "Length",
                                        1,
                                        countType,
                                        "storage.Length")),
                                new SsaValueInstruction(
                                    "capacity",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("storage", storageType),
                                        "Capacity",
                                        2,
                                        countType,
                                        "storage.Capacity")),
                                new SsaValueInstruction(
                                    "data",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("storage", storageType),
                                        "Data",
                                        0,
                                        dataPointerType,
                                        "storage.Data"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("capacity", countType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        AssertDynamicRange(functionFacts.Values["storage"], lengthMin: 0, lengthMax: 0, capacityMin: 4, capacityMax: 4);
        var storageRegion = AssertDynamicRegion(functionFacts.Values["storage"], elementType, lengthMin: 0, lengthMax: 0, capacityMin: 4, capacityMax: 4, spareMin: 4, spareMax: 4);
        Assert.Null(storageRegion.OwnerRootName);
        Assert.Equal(SsaDynamicStorageBackingAllocationKind.RuntimeAllocation, storageRegion.BackingAllocationKind);
        Assert.Equal("storage", storageRegion.BackingAllocationId);
        Assert.Equal(SsaDynamicStorageAllocatorProvenanceKind.RuntimeDefault, storageRegion.AllocatorProvenance);
        Assert.Equal(4, storageRegion.ElementAlignmentBytes);
        AssertIntegerRange(functionFacts.Values["length"], 0, 0);
        AssertIntegerRange(functionFacts.Values["capacity"], 4, 4);
        Assert.Equal(SsaNullabilityFactKind.NonNull, functionFacts.Values["data"].Nullability);
        Assert.Equal(SsaFactLatticeKind.Known, functionFacts.Values["data"].PointerAlignmentKind);
        Assert.Equal(4, functionFacts.Values["data"].PointerAlignmentBytes);
    }

    [Fact]
    public void ValueFactsTrackDynamicStorageTryReserveSuccessAndFailureEdges()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var countType = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - BigInteger.One, isUnsigned: true);
        var storageType = StarkTypeSymbols.Dynamic(elementType);
        var storagePointerType = StarkTypeSymbols.RawPointer(storageType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    countType,
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
                                new SsaAllocateLocalInstruction("items", storageType),
                                new SsaValueInstruction(
                                    "allocated",
                                    new SsaDynamicStorageAllocationRValue(
                                        new SsaIntegerConstant(4, countType),
                                        storageType,
                                        "new(4)")),
                                new SsaStoreLocalInstruction(
                                    "items",
                                    storageType,
                                    new SsaValueReference("allocated", storageType)),
                                new SsaValueInstruction(
                                    "items_address",
                                    new SsaAddressOfLocalRValue(
                                        "items",
                                        storageType,
                                        storagePointerType,
                                        "&items")),
                                new SsaValueInstruction(
                                    "reserved",
                                    new SsaDynamicStorageTryReserveRValue(
                                        new SsaValueReference("items_address", storagePointerType),
                                        storageType,
                                        new SsaIntegerConstant(8, countType),
                                        "items.TryReserve(8)"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Branch,
                                [1, 2],
                                Condition: new SsaValueReference("reserved", StarkTypeSymbols.Bool))),
                        new SsaBasicBlock(
                            1,
                            "bb1_success",
                            [],
                            [
                                new SsaValueInstruction(
                                    "success_storage",
                                    new SsaLoadLocalRValue("items", storageType)),
                                new SsaValueInstruction(
                                    "success_capacity",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("success_storage", storageType),
                                        "Capacity",
                                        2,
                                        countType,
                                        "items.Capacity"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("success_capacity", countType))),
                        new SsaBasicBlock(
                            2,
                            "bb2_failure",
                            [],
                            [
                                new SsaValueInstruction(
                                    "failure_storage",
                                    new SsaLoadLocalRValue("items", storageType)),
                                new SsaValueInstruction(
                                    "failure_capacity",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("failure_storage", storageType),
                                        "Capacity",
                                        2,
                                        countType,
                                        "items.Capacity"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("failure_capacity", countType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        AssertDynamicRange(functionFacts.Values["success_storage"], lengthMin: 0, lengthMax: 0, capacityMin: 8, capacityMax: (BigInteger.One << 63) - BigInteger.One);
        var successRegion = AssertDynamicRegion(functionFacts.Values["success_storage"], elementType, lengthMin: 0, lengthMax: 0, capacityMin: 8, capacityMax: (BigInteger.One << 63) - BigInteger.One, spareMin: 8, spareMax: (BigInteger.One << 63) - BigInteger.One);
        Assert.Equal("items", successRegion.OwnerRootName);
        Assert.Equal(SsaDynamicStorageBackingAllocationKind.RuntimeAllocation, successRegion.BackingAllocationKind);
        Assert.Null(successRegion.BackingAllocationId);
        Assert.Equal(SsaDynamicStorageAllocatorProvenanceKind.RuntimeDefault, successRegion.AllocatorProvenance);
        AssertIntegerRange(functionFacts.Values["success_capacity"], 8, (BigInteger.One << 63) - BigInteger.One);
        AssertDynamicRange(functionFacts.Values["failure_storage"], lengthMin: 0, lengthMax: 0, capacityMin: 4, capacityMax: 4);
        var failureRegion = AssertDynamicRegion(functionFacts.Values["failure_storage"], elementType, lengthMin: 0, lengthMax: 0, capacityMin: 4, capacityMax: 4, spareMin: 4, spareMax: 4);
        Assert.Equal("items", failureRegion.OwnerRootName);
        Assert.Equal(SsaDynamicStorageBackingAllocationKind.RuntimeAllocation, failureRegion.BackingAllocationKind);
        Assert.Equal("allocated", failureRegion.BackingAllocationId);
        AssertIntegerRange(functionFacts.Values["failure_capacity"], 4, 4);
    }

    [Fact]
    public void ValueFactsTrackDynamicStorageMoveLastLengthCommit()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var countType = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - BigInteger.One, isUnsigned: true);
        var storageType = StarkTypeSymbols.Dynamic(elementType);
        var storagePointerType = StarkTypeSymbols.RawPointer(storageType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    countType,
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
                                new SsaAllocateLocalInstruction("items", storageType),
                                new SsaValueInstruction(
                                    "allocated",
                                    new SsaDynamicStorageAllocationRValue(
                                        new SsaIntegerConstant(4, countType),
                                        storageType,
                                        "new(4)")),
                                new SsaValueInstruction(
                                    "initialized",
                                    new SsaInsertFieldRValue(
                                        new SsaValueReference("allocated", storageType),
                                        "Length",
                                        1,
                                        new SsaIntegerConstant(2, countType),
                                        storageType,
                                        "items.Length = 2")),
                                new SsaStoreLocalInstruction(
                                    "items",
                                    storageType,
                                    new SsaValueReference("initialized", storageType)),
                                new SsaValueInstruction(
                                    "items_address",
                                    new SsaAddressOfLocalRValue(
                                        "items",
                                        storageType,
                                        storagePointerType,
                                        "&items")),
                                new SsaValueInstruction(
                                    "moved",
                                    new SsaDynamicStorageMoveLastRValue(
                                        new SsaValueReference("items_address", storagePointerType),
                                        storageType,
                                        elementType,
                                        "items.MoveLast()")),
                                new SsaValueInstruction(
                                    "after_move",
                                    new SsaLoadLocalRValue("items", storageType)),
                                new SsaValueInstruction(
                                    "after_length",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("after_move", storageType),
                                        "Length",
                                        1,
                                        countType,
                                        "items.Length"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("after_length", countType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        AssertDynamicRange(functionFacts.Values["initialized"], lengthMin: 2, lengthMax: 2, capacityMin: 4, capacityMax: 4);
        AssertDynamicRange(functionFacts.Values["after_move"], lengthMin: 1, lengthMax: 1, capacityMin: 4, capacityMax: 4);
        var afterMoveRegion = AssertDynamicRegion(functionFacts.Values["after_move"], elementType, lengthMin: 1, lengthMax: 1, capacityMin: 4, capacityMax: 4, spareMin: 3, spareMax: 3);
        Assert.Equal("items", afterMoveRegion.OwnerRootName);
        Assert.Equal(SsaDynamicStorageBackingAllocationKind.RuntimeAllocation, afterMoveRegion.BackingAllocationKind);
        Assert.Equal("allocated", afterMoveRegion.BackingAllocationId);
        AssertIntegerRange(functionFacts.Values["after_length"], 1, 1);
    }

    [Fact]
    public void ValueFactsTrackDynamicStorageLengthFieldStoreAsPrefixCommit()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var countType = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - BigInteger.One, isUnsigned: true);
        var storageType = StarkTypeSymbols.Dynamic(elementType);
        var storagePointerType = StarkTypeSymbols.RawPointer(storageType, isMutable: true);
        var lengthPointerType = StarkTypeSymbols.RawPointer(countType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    countType,
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
                                new SsaAllocateLocalInstruction("items", storageType),
                                new SsaValueInstruction(
                                    "allocated",
                                    new SsaDynamicStorageAllocationRValue(
                                        new SsaIntegerConstant(4, countType),
                                        storageType,
                                        "new(4)")),
                                new SsaStoreLocalInstruction(
                                    "items",
                                    storageType,
                                    new SsaValueReference("allocated", storageType)),
                                new SsaValueInstruction(
                                    "items_address",
                                    new SsaAddressOfLocalRValue(
                                        "items",
                                        storageType,
                                        storagePointerType,
                                        "&items")),
                                new SsaValueInstruction(
                                    "length_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("items_address", storagePointerType),
                                        storageType,
                                        "Length",
                                        1,
                                        lengthPointerType,
                                        "&items.Length")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("length_address", lengthPointerType),
                                    countType,
                                    new SsaIntegerConstant(2, countType)),
                                new SsaValueInstruction(
                                    "after_commit",
                                    new SsaLoadLocalRValue("items", storageType)),
                                new SsaValueInstruction(
                                    "after_length",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("after_commit", storageType),
                                        "Length",
                                        1,
                                        countType,
                                        "items.Length"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("after_length", countType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        AssertDynamicRange(functionFacts.Values["after_commit"], lengthMin: 2, lengthMax: 2, capacityMin: 4, capacityMax: 4);
        var afterCommitRegion = AssertDynamicRegion(functionFacts.Values["after_commit"], elementType, lengthMin: 2, lengthMax: 2, capacityMin: 4, capacityMax: 4, spareMin: 2, spareMax: 2);
        Assert.Equal("items", afterCommitRegion.OwnerRootName);
        Assert.Equal(SsaDynamicStorageBackingAllocationKind.RuntimeAllocation, afterCommitRegion.BackingAllocationKind);
        Assert.Equal("allocated", afterCommitRegion.BackingAllocationId);
        Assert.Equal(SsaDynamicStorageAllocatorProvenanceKind.RuntimeDefault, afterCommitRegion.AllocatorProvenance);
        AssertIntegerRange(functionFacts.Values["after_length"], 2, 2);
    }

    [Fact]
    public void ValueFactsTrackDynamicLengthReadDerivedCommitThroughOwnerAddress()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var countType = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - BigInteger.One, isUnsigned: true);
        var storageType = StarkTypeSymbols.Dynamic(elementType);
        var storagePointerType = StarkTypeSymbols.RawPointer(storageType, isMutable: true);
        var lengthPointerType = StarkTypeSymbols.RawPointer(countType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    countType,
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
                                new SsaAllocateLocalInstruction("items", storageType),
                                new SsaValueInstruction(
                                    "allocated",
                                    new SsaDynamicStorageAllocationRValue(
                                        new SsaIntegerConstant(4, countType),
                                        storageType,
                                        "new(4)")),
                                new SsaStoreLocalInstruction(
                                    "items",
                                    storageType,
                                    new SsaValueReference("allocated", storageType)),
                                new SsaValueInstruction(
                                    "items_address",
                                    new SsaAddressOfLocalRValue(
                                        "items",
                                        storageType,
                                        storagePointerType,
                                        "&items")),
                                new SsaValueInstruction(
                                    "loaded_items",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("items_address", storagePointerType),
                                        storageType,
                                        "items:load")),
                                new SsaValueInstruction(
                                    "old_length",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("loaded_items", storageType),
                                        "Length",
                                        1,
                                        countType,
                                        "items.Length")),
                                new SsaValueInstruction(
                                    "next_length",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("old_length", countType),
                                        new SsaIntegerConstant(1, countType),
                                        countType,
                                        "items.Length + 1")),
                                new SsaValueInstruction(
                                    "length_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("items_address", storagePointerType),
                                        storageType,
                                        "Length",
                                        1,
                                        lengthPointerType,
                                        "&items.Length")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("length_address", lengthPointerType),
                                    countType,
                                    new SsaValueReference("next_length", countType)),
                                new SsaValueInstruction(
                                    "after_commit",
                                    new SsaLoadLocalRValue("items", storageType)),
                                new SsaValueInstruction(
                                    "after_length",
                                    new SsaExtractFieldRValue(
                                        new SsaValueReference("after_commit", storageType),
                                        "Length",
                                        1,
                                        countType,
                                        "items.Length"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("after_length", countType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        AssertIntegerRange(functionFacts.Values["old_length"], 0, 0);
        AssertIntegerRange(functionFacts.Values["next_length"], 1, 1);
        AssertDynamicRange(functionFacts.Values["after_commit"], lengthMin: 1, lengthMax: 1, capacityMin: 4, capacityMax: 4);
        AssertIntegerRange(functionFacts.Values["after_length"], 1, 1);
    }

    [Fact]
    public void ValueFactsDemoteDynamicStorageAfterEscapedDataPointerCall()
    {
        var module = CreateDynamicStorageDataPointerEscapeModule();

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);

        Assert.Equal(SsaFactLatticeKind.Unknown, functionFacts.Values["after_escape"].CapacityKind);
        Assert.Equal(SsaFactLatticeKind.Unknown, functionFacts.Values["after_escape"].InitializedPrefixKind);
        Assert.Equal(SsaFactLatticeKind.Known, functionFacts.Values["after_capacity"].IntegerRangeKind);
        Assert.NotEqual(
            new SsaIntegerRangeFact(new BigInteger(4), new BigInteger(4)),
            functionFacts.Values["after_capacity"].IntegerRange);
    }

    [Fact]
    public void DynamicStorageOptimizerKeepsReserveAfterEscapedDataPointerCall()
    {
        var module = CreateDynamicStorageDataPointerEscapeModule();
        var facts = new SsaValueFactAnalyzer().Analyze(module);

        var optimized = new SsaDynamicStorageOptimizer().Optimize(module, facts);
        var function = Assert.Single(optimized.Functions);

        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
    }

    [Fact]
    public void DynamicStorageOptimizerKeepsReserveAfterEscapedDataSliceCall()
    {
        var module = CreateDynamicStorageDataPointerEscapeModule(escapeSlice: true);
        var facts = new SsaValueFactAnalyzer().Analyze(module);

        var optimized = new SsaDynamicStorageOptimizer().Optimize(module, facts);
        var function = Assert.Single(optimized.Functions);

        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
    }

    [Fact]
    public void ValueFactsTrackDynamicStorageZeroInitializerAsNoBackingAllocation()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var storageType = StarkTypeSymbols.Dynamic(elementType);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    storageType,
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
                                    "storage",
                                    new SsaUseRValue(new SsaZeroInitializerValue(storageType)))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("storage", storageType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var storageFacts = Assert.Single(facts.Functions.Values).Values["storage"];

        AssertDynamicRange(storageFacts, lengthMin: 0, lengthMax: 0, capacityMin: 0, capacityMax: 0);
        var region = AssertDynamicRegion(storageFacts, elementType, lengthMin: 0, lengthMax: 0, capacityMin: 0, capacityMax: 0, spareMin: 0, spareMax: 0);
        Assert.Equal(SsaDynamicStorageBackingAllocationKind.None, region.BackingAllocationKind);
        Assert.Null(region.BackingAllocationId);
        Assert.Equal(SsaDynamicStorageAllocatorProvenanceKind.RuntimeDefault, region.AllocatorProvenance);
    }

    private static SsaIrModule CreateDynamicStorageDataPointerEscapeModule(bool escapeSlice = false)
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var countType = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - BigInteger.One, isUnsigned: true);
        var storageType = StarkTypeSymbols.Dynamic(elementType);
        var storagePointerType = StarkTypeSymbols.RawPointer(storageType, isMutable: true);
        var dataPointerType = StarkTypeSymbols.RawPointer(elementType, isMutable: true);
        var sliceType = StarkTypeSymbols.ApplyQualifiers(StarkTypeSymbols.Slice(elementType), isMutableView: true);
        var instructions = new List<SsaInstruction>
        {
            new SsaAllocateLocalInstruction("items", storageType),
            new SsaValueInstruction(
                "allocated",
                new SsaDynamicStorageAllocationRValue(
                    new SsaIntegerConstant(4, countType),
                    storageType,
                    "new(4)")),
            new SsaStoreLocalInstruction(
                "items",
                storageType,
                new SsaValueReference("allocated", storageType)),
            new SsaValueInstruction(
                "loaded",
                new SsaLoadLocalRValue("items", storageType)),
            new SsaValueInstruction(
                "data",
                new SsaExtractFieldRValue(
                    new SsaValueReference("loaded", storageType),
                    "Data",
                    0,
                    dataPointerType,
                    "items.Data"))
        };

        if (escapeSlice)
        {
            instructions.Add(
                new SsaValueInstruction(
                    "slice",
                    new SsaMakeSliceFromPointerRValue(
                        new SsaValueReference("data", dataPointerType),
                        new SsaIntegerConstant(4, countType),
                        sliceType,
                        "items.Data[0..4]")));
            instructions.Add(
                new SsaCallInstruction(
                    "ObserveSlice",
                    [new SsaValueReference("slice", sliceType)],
                    StarkTypeSymbols.Void,
                    "ObserveSlice(slice)"));
        }
        else
        {
            instructions.Add(
                new SsaCallInstruction(
                    "ObserveRaw",
                    [new SsaValueReference("data", dataPointerType)],
                    StarkTypeSymbols.Void,
                    "ObserveRaw(data)"));
        }

        instructions.AddRange(
            [
                new SsaValueInstruction(
                    "after_escape",
                    new SsaLoadLocalRValue("items", storageType)),
                new SsaValueInstruction(
                    "after_capacity",
                    new SsaExtractFieldRValue(
                        new SsaValueReference("after_escape", storageType),
                        "Capacity",
                        2,
                        countType,
                        "items.Capacity")),
                new SsaValueInstruction(
                    "items_address",
                    new SsaAddressOfLocalRValue(
                        "items",
                        storageType,
                        storagePointerType,
                        "&items")),
                new SsaValueInstruction(
                    "reserve",
                    new SsaDynamicStorageReserveRValue(
                        new SsaValueReference("items_address", storagePointerType),
                        storageType,
                        new SsaIntegerConstant(4, countType),
                        "items.Reserve(4)")),
                new SsaValueInstruction(
                    "after_reserve",
                    new SsaLoadLocalRValue("items", storageType)),
                new SsaValueInstruction(
                    "capacity",
                    new SsaExtractFieldRValue(
                        new SsaValueReference("after_reserve", storageType),
                        "Capacity",
                        2,
                        countType,
                        "items.Capacity"))
            ]);

        return new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    countType,
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
                            instructions,
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("capacity", countType)))
                    ])
            ]);
    }

    private static void AssertBoundedRegion(
        SsaValueFacts facts,
        string countName,
        StarkTypeSymbol countType,
        BigInteger min,
        BigInteger max,
        int alignmentBytes)
    {
        Assert.Equal(SsaFactLatticeKind.Known, facts.BoundedRawPointerRegionKind);
        Assert.NotNull(facts.BoundedRawPointerRegion);
        var region = facts.BoundedRawPointerRegion!;
        var countReference = Assert.IsType<SsaValueReference>(region.ElementCount);
        Assert.Equal(countName, countReference.Name);
        Assert.Equal(countType, countReference.Type);
        Assert.Equal(min, region.ElementCountRange!.Min);
        Assert.Equal(max, region.ElementCountRange.Max);
        Assert.Equal(alignmentBytes, region.ElementAlignmentBytes);
    }

    private static void AssertDynamicRange(
        SsaValueFacts facts,
        BigInteger lengthMin,
        BigInteger lengthMax,
        BigInteger capacityMin,
        BigInteger capacityMax)
    {
        Assert.Equal(SsaFactLatticeKind.Known, facts.LengthKind);
        Assert.NotNull(facts.LengthRange);
        Assert.Equal(lengthMin, facts.LengthRange!.Min);
        Assert.Equal(lengthMax, facts.LengthRange.Max);
        Assert.Equal(SsaFactLatticeKind.Known, facts.InitializedPrefixKind);
        Assert.NotNull(facts.InitializedPrefixRange);
        Assert.Equal(lengthMin, facts.InitializedPrefixRange!.Min);
        Assert.Equal(lengthMax, facts.InitializedPrefixRange.Max);
        Assert.Equal(SsaFactLatticeKind.Known, facts.CapacityKind);
        Assert.NotNull(facts.CapacityRange);
        Assert.Equal(capacityMin, facts.CapacityRange!.Min);
        Assert.Equal(capacityMax, facts.CapacityRange.Max);
    }

    private static SsaDynamicStorageRegionFact AssertDynamicRegion(
        SsaValueFacts facts,
        StarkTypeSymbol elementType,
        BigInteger lengthMin,
        BigInteger lengthMax,
        BigInteger capacityMin,
        BigInteger capacityMax,
        BigInteger spareMin,
        BigInteger spareMax)
    {
        Assert.Equal(SsaFactLatticeKind.Known, facts.DynamicStorageRegionKind);
        Assert.NotNull(facts.DynamicStorageRegion);
        var region = facts.DynamicStorageRegion!;
        Assert.Equal(elementType, region.ElementType);
        Assert.NotNull(region.InitializedLengthRange);
        Assert.Equal(lengthMin, region.InitializedLengthRange!.Min);
        Assert.Equal(lengthMax, region.InitializedLengthRange.Max);
        Assert.NotNull(region.InitializedPrefixRange);
        Assert.Equal(lengthMin, region.InitializedPrefixRange!.Min);
        Assert.Equal(lengthMax, region.InitializedPrefixRange.Max);
        Assert.NotNull(region.CapacityRange);
        Assert.Equal(capacityMin, region.CapacityRange!.Min);
        Assert.Equal(capacityMax, region.CapacityRange.Max);
        Assert.NotNull(region.SpareCapacityRange);
        Assert.Equal(spareMin, region.SpareCapacityRange!.Min);
        Assert.Equal(spareMax, region.SpareCapacityRange.Max);
        return region;
    }

    private static void AssertIntegerRange(SsaValueFacts facts, BigInteger min, BigInteger max)
    {
        Assert.Equal(SsaFactLatticeKind.Known, facts.IntegerRangeKind);
        Assert.NotNull(facts.IntegerRange);
        Assert.Equal(min, facts.IntegerRange!.Min);
        Assert.Equal(max, facts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsKeepTextLocalPayloadUnknownWhenAddressTaken()
    {
        var textType = StarkTypeSymbols.Ascii;
        var pointerType = StarkTypeSymbols.RawPointer(textType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    textType,
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
                                new SsaStoreLocalInstruction(
                                    "local",
                                    textType,
                                    new SsaStringConstant("\"abc\"", textType)),
                                new SsaValueInstruction(
                                    "local_address",
                                    new SsaAddressOfLocalRValue("local", textType, pointerType, "&local")),
                                new SsaValueInstruction(
                                    "loaded",
                                    new SsaLoadLocalRValue("local", textType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("loaded", textType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var loadedFacts = Assert.Single(facts.Functions.Values).Values["loaded"];

        Assert.Equal(SsaFactLatticeKind.Unknown, loadedFacts.TextLiteralPayloadKind);
        Assert.Null(loadedFacts.TextLiteralPayload);
    }

    [Fact]
    public void ValueFactsClampNonWrappingArithmeticToContinuingTypeRange()
    {
        var resultType = StarkTypeSymbols.Integer(8, BigInteger.Zero, new BigInteger(255), isUnsigned: true);
        var leftType = StarkTypeSymbols.Integer(16, new BigInteger(240), new BigInteger(250));
        var rightType = StarkTypeSymbols.Integer(16, new BigInteger(10), new BigInteger(20));
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    resultType,
                    [
                        new TypedParameterSymbol("left", leftType),
                        new TypedParameterSymbol("right", rightType)
                    ],
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
                                    "sum",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("arg_left", leftType),
                                        new SsaValueReference("arg_right", rightType),
                                        resultType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("sum", resultType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var sumFacts = functionFacts.Values["sum"];

        Assert.Equal(SsaFactLatticeKind.Known, sumFacts.IntegerRangeKind);
        Assert.NotNull(sumFacts.IntegerRange);
        Assert.Equal(new BigInteger(250), sumFacts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(255), sumFacts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsDoNotCreateInvalidRangesWhenNonWrappingArithmeticAlwaysOverflows()
    {
        var resultType = StarkTypeSymbols.Integer(8, BigInteger.Zero, new BigInteger(255), isUnsigned: true);
        var leftType = StarkTypeSymbols.Integer(16, new BigInteger(250), new BigInteger(255));
        var rightType = StarkTypeSymbols.Integer(16, new BigInteger(10), new BigInteger(20));
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    resultType,
                    [
                        new TypedParameterSymbol("left", leftType),
                        new TypedParameterSymbol("right", rightType)
                    ],
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
                                    "sum",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.Add,
                                        new SsaValueReference("arg_left", leftType),
                                        new SsaValueReference("arg_right", rightType),
                                        resultType,
                                        "+"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("sum", resultType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var sumFacts = functionFacts.Values["sum"];

        Assert.Equal(SsaFactLatticeKind.Known, sumFacts.IntegerRangeKind);
        Assert.NotNull(sumFacts.IntegerRange);
        Assert.Equal(BigInteger.Zero, sumFacts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(255), sumFacts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsKeepWrappingArithmeticConservativeWhenRangeMayWrap()
    {
        var resultType = StarkTypeSymbols.Integer(8, BigInteger.Zero, new BigInteger(255), isUnsigned: true);
        var leftType = StarkTypeSymbols.Integer(16, new BigInteger(250), new BigInteger(255));
        var rightType = StarkTypeSymbols.Integer(16, new BigInteger(10), new BigInteger(20));
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    resultType,
                    [
                        new TypedParameterSymbol("left", leftType),
                        new TypedParameterSymbol("right", rightType)
                    ],
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
                                    "sum",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.WrappingAdd,
                                        new SsaValueReference("arg_left", leftType),
                                        new SsaValueReference("arg_right", rightType),
                                        resultType,
                                        "+%"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("sum", resultType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var sumFacts = functionFacts.Values["sum"];

        Assert.Equal(SsaFactLatticeKind.Known, sumFacts.IntegerRangeKind);
        Assert.NotNull(sumFacts.IntegerRange);
        Assert.Equal(BigInteger.Zero, sumFacts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(255), sumFacts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsClampSaturatingArithmeticInsteadOfUsingWrappingSemantics()
    {
        var resultType = StarkTypeSymbols.Integer(8, BigInteger.Zero, new BigInteger(255), isUnsigned: true);
        var leftType = StarkTypeSymbols.Integer(16, new BigInteger(250), new BigInteger(255));
        var rightType = StarkTypeSymbols.Integer(16, new BigInteger(10), new BigInteger(20));
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    resultType,
                    [
                        new TypedParameterSymbol("left", leftType),
                        new TypedParameterSymbol("right", rightType)
                    ],
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
                                    "sum",
                                    new SsaBinaryRValue(
                                        SsaBinaryOperator.SaturatingAdd,
                                        new SsaValueReference("arg_left", leftType),
                                        new SsaValueReference("arg_right", rightType),
                                        resultType,
                                        "+|"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("sum", resultType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var sumFacts = functionFacts.Values["sum"];

        Assert.Equal(SsaFactLatticeKind.Known, sumFacts.IntegerRangeKind);
        Assert.NotNull(sumFacts.IntegerRange);
        Assert.Equal(new BigInteger(255), sumFacts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(255), sumFacts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsClampIntegerConversionsToTargetRange()
    {
        var sourceType = StarkTypeSymbols.Integer(16, new BigInteger(240), new BigInteger(260));
        var targetType = StarkTypeSymbols.Integer(8, BigInteger.Zero, new BigInteger(255), isUnsigned: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    targetType,
                    [new TypedParameterSymbol("value", sourceType)],
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
                                    "converted",
                                    new SsaConvertRValue(
                                        new SsaValueReference("arg_value", sourceType),
                                        targetType,
                                        "cast"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("converted", targetType)))
                    ])
            ]);

        var facts = new SsaValueFactAnalyzer().Analyze(module);
        var functionFacts = Assert.Single(facts.Functions.Values);
        var convertedFacts = functionFacts.Values["converted"];

        Assert.Equal(SsaFactLatticeKind.Known, convertedFacts.IntegerRangeKind);
        Assert.NotNull(convertedFacts.IntegerRange);
        Assert.Equal(new BigInteger(240), convertedFacts.IntegerRange!.Min);
        Assert.Equal(new BigInteger(255), convertedFacts.IntegerRange.Max);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStoredStackScalarLoadsWithinBlock()
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
                                new SsaAllocateLocalInstruction("local", valueType),
                                new SsaStoreLocalInstruction(
                                    "local",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadLocalRValue("local", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadLocalRValue);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsAddressEscapedLocalLoads()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var pointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("local", valueType),
                                new SsaStoreLocalInstruction(
                                    "local",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "addr",
                                    new SsaAddressOfLocalRValue("local", valueType, pointerType, "&local")),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadLocalRValue("local", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Contains(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadLocalRValue { LocalName: "local" });

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v0", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStoredStackFieldLoadsWithinBlock()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);
        Assert.Contains(
            block.Instructions.OfType<SsaStoreIndirectInstruction>(),
            static instruction => instruction.Value is SsaValueReference { Name: "arg_value" });

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStoredStackFixedArrayElementLoadsWithinBlock()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var arrayType = StarkTypeSymbols.FixedArray(valueType, 4);
        var arrayPointerType = StarkTypeSymbols.RawPointer(arrayType, isMutable: true);
        var elementPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("value", valueType),
                        new TypedParameterSymbol("other", valueType)
                    ],
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
                                new SsaAllocateLocalInstruction("items", arrayType),
                                new SsaValueInstruction(
                                    "items_addr",
                                    new SsaAddressOfLocalRValue("items", arrayType, arrayPointerType, "&items")),
                                new SsaValueInstruction(
                                    "first_addr",
                                    new SsaElementAddressRValue(
                                        new SsaValueReference("items_addr", arrayPointerType),
                                        arrayType,
                                        null,
                                        0,
                                        elementPointerType,
                                        "&items[0]")),
                                new SsaValueInstruction(
                                    "second_addr",
                                    new SsaElementAddressRValue(
                                        new SsaValueReference("items_addr", arrayPointerType),
                                        arrayType,
                                        null,
                                        1,
                                        elementPointerType,
                                        "&items[1]")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("first_addr", elementPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("second_addr", elementPointerType),
                                    valueType,
                                    new SsaValueReference("arg_other", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("first_addr", elementPointerType),
                                        valueType,
                                        "items[0]:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsStackFixedArrayElementLoadsAcrossDynamicElementStores()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var indexType = StarkTypeSymbols.Integer(64, rangeMin: 0, rangeMax: 3);
        var arrayType = StarkTypeSymbols.FixedArray(valueType, 4);
        var arrayPointerType = StarkTypeSymbols.RawPointer(arrayType, isMutable: true);
        var elementPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("value", valueType),
                        new TypedParameterSymbol("index", indexType)
                    ],
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
                                new SsaAllocateLocalInstruction("items", arrayType),
                                new SsaValueInstruction(
                                    "items_addr",
                                    new SsaAddressOfLocalRValue("items", arrayType, arrayPointerType, "&items")),
                                new SsaValueInstruction(
                                    "first_addr",
                                    new SsaElementAddressRValue(
                                        new SsaValueReference("items_addr", arrayPointerType),
                                        arrayType,
                                        null,
                                        0,
                                        elementPointerType,
                                        "&items[0]")),
                                new SsaValueInstruction(
                                    "dynamic_addr",
                                    new SsaElementAddressRValue(
                                        new SsaValueReference("items_addr", arrayPointerType),
                                        arrayType,
                                        new SsaValueReference("arg_index", indexType),
                                        null,
                                        elementPointerType,
                                        "&items[index]")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("first_addr", elementPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("dynamic_addr", elementPointerType),
                                    valueType,
                                    new SsaIntegerConstant(9, valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("first_addr", elementPointerType),
                                        valueType,
                                        "items[0]:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Contains(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v0", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsStackFieldLoadsAcrossUnknownIndirectStores()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("value", valueType),
                        new TypedParameterSymbol("other", fieldPointerType)
                    ],
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("arg_other", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(9, valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Contains(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v0", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsNestedStackFieldLoadsWithinBlock()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var innerType = StarkTypeSymbols.Named("Inner");
        var outerType = StarkTypeSymbols.Named("Outer");
        var outerPointerType = StarkTypeSymbols.RawPointer(outerType, isMutable: true);
        var innerPointerType = StarkTypeSymbols.RawPointer(innerType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("outer", outerType),
                                new SsaValueInstruction(
                                    "outer_addr",
                                    new SsaAddressOfLocalRValue("outer", outerType, outerPointerType, "&outer")),
                                new SsaValueInstruction(
                                    "inner_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("outer_addr", outerPointerType),
                                        outerType,
                                        "Inner",
                                        0,
                                        innerPointerType,
                                        "&outer.Inner")),
                                new SsaValueInstruction(
                                    "value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("inner_addr", innerPointerType),
                                        innerType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&outer.Inner.Value")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("value_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("value_addr", fieldPointerType),
                                        valueType,
                                        "outer.Inner.Value:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsSiblingNestedStackFieldPathsSeparate()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var innerType = StarkTypeSymbols.Named("Inner");
        var outerType = StarkTypeSymbols.Named("Outer");
        var outerPointerType = StarkTypeSymbols.RawPointer(outerType, isMutable: true);
        var innerPointerType = StarkTypeSymbols.RawPointer(innerType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("outer", outerType),
                                new SsaValueInstruction(
                                    "outer_addr",
                                    new SsaAddressOfLocalRValue("outer", outerType, outerPointerType, "&outer")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("outer_addr", outerPointerType),
                                        outerType,
                                        "Left",
                                        0,
                                        innerPointerType,
                                        "&outer.Left")),
                                new SsaValueInstruction(
                                    "right_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("outer_addr", outerPointerType),
                                        outerType,
                                        "Right",
                                        1,
                                        innerPointerType,
                                        "&outer.Right")),
                                new SsaValueInstruction(
                                    "left_value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("left_addr", innerPointerType),
                                        innerType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&outer.Left.Value")),
                                new SsaValueInstruction(
                                    "right_value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_addr", innerPointerType),
                                        innerType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&outer.Right.Value")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_value_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(1, valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("right_value_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_value_addr", fieldPointerType),
                                        valueType,
                                        "outer.Left.Value:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(1, returnValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRemovesOverwrittenStackFieldStoresWithinBlock()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(1, valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var store = Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());

        Assert.IsType<SsaValueReference>(store.Address);
        Assert.Equal("arg_value", Assert.IsType<SsaValueReference>(store.Value).Name);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsStackFieldStoresAcrossUnknownIndirectStores()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("value", valueType),
                        new TypedParameterSymbol("other", fieldPointerType)
                    ],
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("arg_other", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(9, valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(11, valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var stores = block.Instructions.OfType<SsaStoreIndirectInstruction>().ToArray();

        Assert.Equal(3, stores.Length);
        Assert.Equal(
            2,
            stores.Count(static store => store.Address is SsaValueReference { Name: "left_addr" }));

        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(11, returnValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStackFieldLoadsAcrossPureScalarCalls()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Touch",
                                        [new SsaValueReference("arg_value", valueType)],
                                        valueType,
                                        "Touch(value)")),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureEffect("Touch"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Contains(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaCallRValue { FunctionName: "Touch" });
        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsStackFieldLoadsAcrossImpureCalls()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue("Touch", [], valueType, "Touch()")),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Contains(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v0", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRemovesOverwrittenStackFieldStoresAcrossReadonlyScalarCalls()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(1, valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Touch",
                                        [new SsaValueReference("arg_value", valueType)],
                                        valueType,
                                        "Touch(value)")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureReadArgumentEffect("Touch"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var store = Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());

        Assert.Equal("arg_value", Assert.IsType<SsaValueReference>(store.Value).Name);
        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsStackFieldStoresAcrossReadonlyLocalMemoryCalls()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(1, valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Touch",
                                        [new SsaValueReference("left_addr", fieldPointerType)],
                                        valueType,
                                        "Touch(&pair.Left)")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(11, valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureReadArgumentEffect("Touch"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var stores = block.Instructions.OfType<SsaStoreIndirectInstruction>().ToArray();

        Assert.Equal(2, stores.Length);
        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(11, returnValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStackFieldLoadsAcrossSinglePredecessorBlocks()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [1])),
                        new SsaBasicBlock(
                            1,
                            "bb1_return",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var returnBlock = Assert.Single(function.Blocks, static block => block.Id == 1);

        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);
        var returnValue = Assert.IsType<SsaValueReference>(returnBlock.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsStackFieldLoadsAtJoinBlocks()
    {
        var boolType = StarkTypeSymbols.Bool;
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                            [
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Branch,
                                [1, 2],
                                Condition: new SsaValueReference("arg_flag", boolType))),
                        new SsaBasicBlock(
                            1,
                            "bb1_then",
                            [],
                            [
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(1, valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_else",
                            [],
                            [
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(2, valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left:load"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRemovesOverwrittenStackScalarStoresWithinBlock()
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
                                new SsaAllocateLocalInstruction("local", valueType),
                                new SsaStoreLocalInstruction(
                                    "local",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaStoreLocalInstruction(
                                    "local",
                                    valueType,
                                    new SsaIntegerConstant(7, valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadLocalRValue("local", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        Assert.DoesNotContain(
            block.Instructions.OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "local");
        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadLocalRValue);
        var returnValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new BigInteger(7), returnValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStackScalarsAcrossSinglePredecessorBlocks()
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
                                new SsaAllocateLocalInstruction("local", valueType),
                                new SsaStoreLocalInstruction(
                                    "local",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [1])),
                        new SsaBasicBlock(
                            1,
                            "bb1_return",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadLocalRValue("local", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var returnBlock = Assert.Single(function.Blocks, static block => block.Id == 1);

        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadLocalRValue { LocalName: "local" });
        var returnValue = Assert.IsType<SsaValueReference>(returnBlock.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsStackScalarLoadsAtJoinBlocks()
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
                            [
                                new SsaAllocateLocalInstruction("local", valueType)
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Branch,
                                [1, 2],
                                Condition: new SsaValueReference("arg_flag", boolType))),
                        new SsaBasicBlock(
                            1,
                            "bb1_then",
                            [],
                            [
                                new SsaStoreLocalInstruction(
                                    "local",
                                    valueType,
                                    new SsaIntegerConstant(1, valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_else",
                            [],
                            [
                                new SsaStoreLocalInstruction(
                                    "local",
                                    valueType,
                                    new SsaIntegerConstant(2, valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadLocalRValue("local", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadLocalRValue { LocalName: "local" });
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsRepeatedGlobalScalarLoadsWithinBlock()
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
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType)),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v1", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadGlobalRValue { GlobalName: "Demo.Value" });

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v0", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStoredGlobalScalarLoadsWithinBlock()
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
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Contains(
            block.Instructions.OfType<SsaStoreGlobalInstruction>(),
            static instruction => instruction.GlobalName == "Demo.Value");
        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadGlobalRValue { GlobalName: "Demo.Value" });

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsGlobalScalarLoadsAcrossCallBarriers()
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
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue("Touch", [], valueType, "Touch()")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v1", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Equal(
            2,
            block.Instructions
                .OfType<SsaValueInstruction>()
                .Count(static instruction => instruction.Value is SsaLoadGlobalRValue { GlobalName: "Demo.Value" }));

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v1", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsGlobalScalarLoadsAcrossUnknownIndirectStores()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var pointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [new TypedParameterSymbol("other", pointerType)],
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
                                    new SsaLoadGlobalRValue("Demo.Value", valueType)),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("arg_other", pointerType),
                                    valueType,
                                    new SsaIntegerConstant(9, valueType)),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v1", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Equal(
            2,
            block.Instructions
                .OfType<SsaValueInstruction>()
                .Count(static instruction => instruction.Value is SsaLoadGlobalRValue { GlobalName: "Demo.Value" }));

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v1", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsGlobalScalarsAcrossSinglePredecessorBlocks()
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
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [1])),
                        new SsaBasicBlock(
                            1,
                            "bb1_return",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var returnBlock = Assert.Single(function.Blocks, static block => block.Id == 1);

        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadGlobalRValue { GlobalName: "Demo.Value" });

        var returnValue = Assert.IsType<SsaValueReference>(returnBlock.Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsGlobalScalarLoadsAtJoinBlocks()
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
                            [
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaIntegerConstant(1, valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            2,
                            "bb2_else",
                            [],
                            [
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaIntegerConstant(2, valueType))
                            ],
                            new SsaTerminator(SsaTerminatorKind.Goto, [3])),
                        new SsaBasicBlock(
                            3,
                            "bb3_join",
                            [],
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v0", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);

        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadGlobalRValue { GlobalName: "Demo.Value" });
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRemovesOverwrittenGlobalScalarStoresWithinBlock()
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
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var store = Assert.Single(block.Instructions.OfType<SsaStoreGlobalInstruction>());

        Assert.Equal("Demo.Value", store.GlobalName);
        var storeValue = Assert.IsType<SsaIntegerConstant>(store.Value);
        Assert.Equal(new BigInteger(7), storeValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsGlobalScalarStoresAcrossCallBarriers()
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
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue("Touch", [], valueType, "Touch()")),
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Equal(
            2,
            block.Instructions.OfType<SsaStoreGlobalInstruction>().Count(static store => store.GlobalName == "Demo.Value"));
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsGlobalScalarLoadsAcrossPureCalls()
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
                            [
                                new SsaValueInstruction(
                                    "v0",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue("Demo.Touch", [], valueType, "Touch()")),
                                new SsaValueInstruction(
                                    "v1",
                                    new SsaLoadGlobalRValue("Demo.Value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("v1", valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureEffect("Demo.Touch"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadGlobalRValue { GlobalName: "Demo.Value" });

        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("v0", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRemovesOverwrittenGlobalScalarStoresAcrossPureCalls()
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
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue("Demo.Touch", [], valueType, "Touch()")),
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureEffect("Demo.Touch"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var store = Assert.Single(block.Instructions.OfType<SsaStoreGlobalInstruction>());

        Assert.Equal("Demo.Value", store.GlobalName);
        var storeValue = Assert.IsType<SsaIntegerConstant>(store.Value);
        Assert.Equal(new BigInteger(7), storeValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsGlobalScalarStoresAcrossPureArgumentMemoryReadsWhenGlobalAddressIsPassed()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var pointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: false);
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
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Demo.Read",
                                        [new SsaGlobalAddressValue("Demo.Value", valueType, pointerType)],
                                        valueType,
                                        "Read(&Value)")),
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureReadArgumentEffect("Demo.Read"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Equal(
            2,
            block.Instructions.OfType<SsaStoreGlobalInstruction>().Count(static store => store.GlobalName == "Demo.Value"));
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRemovesOverwrittenGlobalScalarStoresAcrossPureArgumentMemoryReadsWithScalarArguments()
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
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Demo.Read",
                                        [new SsaValueReference("arg_value", valueType)],
                                        valueType,
                                        "Read(value)")),
                                new SsaStoreGlobalInstruction(
                                    "Demo.Value",
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureReadArgumentEffect("Demo.Read"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var store = Assert.Single(block.Instructions.OfType<SsaStoreGlobalInstruction>());

        Assert.Equal("Demo.Value", store.GlobalName);
        var storeValue = Assert.IsType<SsaIntegerConstant>(store.Value);
        Assert.Equal(new BigInteger(7), storeValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsPendingStackFieldStoresAcrossReadonlyReadsOfOtherLocals()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var boxType = StarkTypeSymbols.Named("Box");
        var boxPointerType = StarkTypeSymbols.RawPointer(boxType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                    "left_address",
                                    new SsaAddressOfLocalRValue(
                                        "left",
                                        boxType,
                                        boxPointerType,
                                        "&left")),
                                new SsaValueInstruction(
                                    "left_value_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("left_address", boxPointerType),
                                        boxType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&left.Value")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_value_address", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "right_address",
                                    new SsaAddressOfLocalRValue(
                                        "right",
                                        boxType,
                                        boxPointerType,
                                        "&right")),
                                new SsaValueInstruction(
                                    "right_value_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_address", boxPointerType),
                                        boxType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&right.Value")),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Demo.Read",
                                        [new SsaValueReference("right_value_address", fieldPointerType)],
                                        valueType,
                                        "Read(&right.Value)")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_value_address", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureReadArgumentEffect("Demo.Read"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var store = Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());

        var storeValue = Assert.IsType<SsaIntegerConstant>(store.Value);
        Assert.Equal(new BigInteger(7), storeValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsPendingSiblingFieldStoresAcrossReadonlyFieldReads()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var boxType = StarkTypeSymbols.Named("Box");
        var boxPointerType = StarkTypeSymbols.RawPointer(boxType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                    "right_address",
                                    new SsaAddressOfLocalRValue(
                                        "right",
                                        boxType,
                                        boxPointerType,
                                        "&right")),
                                new SsaValueInstruction(
                                    "right_value_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_address", boxPointerType),
                                        boxType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&right.Value")),
                                new SsaValueInstruction(
                                    "right_other_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_address", boxPointerType),
                                        boxType,
                                        "Other",
                                        1,
                                        fieldPointerType,
                                        "&right.Other")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("right_other_address", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Demo.Read",
                                        [new SsaValueReference("right_value_address", fieldPointerType)],
                                        valueType,
                                        "Read(&right.Value)")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("right_other_address", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer(Effects(PureReadArgumentEffect("Demo.Read"))).Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var store = Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());

        var address = Assert.IsType<SsaValueReference>(store.Address);
        Assert.Equal("right_other_address", address.Name);
        var storeValue = Assert.IsType<SsaIntegerConstant>(store.Value);
        Assert.Equal(new BigInteger(7), storeValue.Value);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsPendingSiblingFieldStoresAcrossExactFieldCopies()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var boxType = StarkTypeSymbols.Named("Box");
        var boxPointerType = StarkTypeSymbols.RawPointer(boxType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                    "left_address",
                                    new SsaAddressOfLocalRValue(
                                        "left",
                                        boxType,
                                        boxPointerType,
                                        "&left")),
                                new SsaValueInstruction(
                                    "left_value_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("left_address", boxPointerType),
                                        boxType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&left.Value")),
                                new SsaValueInstruction(
                                    "right_address",
                                    new SsaAddressOfLocalRValue(
                                        "right",
                                        boxType,
                                        boxPointerType,
                                        "&right")),
                                new SsaValueInstruction(
                                    "right_value_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_address", boxPointerType),
                                        boxType,
                                        "Value",
                                        0,
                                        fieldPointerType,
                                        "&right.Value")),
                                new SsaValueInstruction(
                                    "right_other_address",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_address", boxPointerType),
                                        boxType,
                                        "Other",
                                        1,
                                        fieldPointerType,
                                        "&right.Other")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("right_other_address", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaCopyMemoryInstruction(
                                    new SsaValueReference("left_value_address", fieldPointerType),
                                    new SsaValueReference("right_value_address", fieldPointerType),
                                    valueType),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("right_other_address", fieldPointerType),
                                    valueType,
                                    new SsaIntegerConstant(7, valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaAliasAwareMemoryOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);
        var stores = block.Instructions.OfType<SsaStoreIndirectInstruction>().ToArray();

        var store = Assert.Single(stores);
        var address = Assert.IsType<SsaValueReference>(store.Address);
        Assert.Equal("right_other_address", address.Name);
        var storeValue = Assert.IsType<SsaIntegerConstant>(store.Value);
        Assert.Equal(new BigInteger(7), storeValue.Value);
        Assert.Single(block.Instructions.OfType<SsaCopyMemoryInstruction>());
    }

    [Fact]
    public void ScalarReplacementRemovesDeadStackFieldStores()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Instructions.OfType<SsaStoreIndirectInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsStackFieldStoresObservedByLaterLoad()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "loaded",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("left_addr", fieldPointerType),
                                        valueType,
                                        "pair.Left"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("loaded", valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsStackFieldStoresAcrossUnknownIndirectLoads()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
        var module = new SsaIrModule(
            "Demo",
            [
                new SsaFunction(
                    "Run",
                    valueType,
                    [
                        new TypedParameterSymbol("value", valueType),
                        new TypedParameterSymbol("ptr", fieldPointerType)
                    ],
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "unknown",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("arg_ptr", fieldPointerType),
                                        valueType,
                                        "*ptr"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("unknown", valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsStackFieldStoresAcrossUnknownCalls()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType)),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Demo.Touch",
                                        [],
                                        valueType,
                                        "Touch()"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsStackFieldStoresAfterAggregateAddressEscapes()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("pair", aggregateType),
                                new SsaValueInstruction(
                                    "pair_addr",
                                    new SsaAddressOfLocalRValue("pair", aggregateType, aggregatePointerType, "&pair")),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Demo.Escape",
                                        [new SsaValueReference("pair_addr", aggregatePointerType)],
                                        valueType,
                                        "Escape(&pair)")),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("pair_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&pair.Left")),
                                new SsaStoreIndirectInstruction(
                                    new SsaValueReference("left_addr", fieldPointerType),
                                    valueType,
                                    new SsaValueReference("arg_value", valueType))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaStoreIndirectInstruction>());
    }

    [Fact]
    public void ScalarReplacementRemovesDeadExactScalarFieldCopies()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("left", aggregateType),
                                new SsaAllocateLocalInstruction("right", aggregateType),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaAddressOfLocalRValue("left", aggregateType, aggregatePointerType, "&left")),
                                new SsaValueInstruction(
                                    "right_addr",
                                    new SsaAddressOfLocalRValue("right", aggregateType, aggregatePointerType, "&right")),
                                new SsaValueInstruction(
                                    "left_value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("left_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&left.Left")),
                                new SsaValueInstruction(
                                    "right_value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&right.Left")),
                                new SsaCopyMemoryInstruction(
                                    new SsaValueReference("right_value_addr", fieldPointerType),
                                    new SsaValueReference("left_value_addr", fieldPointerType),
                                    valueType)
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Instructions.OfType<SsaCopyMemoryInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsExactScalarFieldCopiesObservedByLaterLoad()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("left", aggregateType),
                                new SsaAllocateLocalInstruction("right", aggregateType),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaAddressOfLocalRValue("left", aggregateType, aggregatePointerType, "&left")),
                                new SsaValueInstruction(
                                    "right_addr",
                                    new SsaAddressOfLocalRValue("right", aggregateType, aggregatePointerType, "&right")),
                                new SsaValueInstruction(
                                    "left_value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("left_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&left.Left")),
                                new SsaValueInstruction(
                                    "right_value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&right.Left")),
                                new SsaCopyMemoryInstruction(
                                    new SsaValueReference("right_value_addr", fieldPointerType),
                                    new SsaValueReference("left_value_addr", fieldPointerType),
                                    valueType),
                                new SsaValueInstruction(
                                    "loaded",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("right_value_addr", fieldPointerType),
                                        valueType,
                                        "right.Left"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("loaded", valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaCopyMemoryInstruction>());
    }

    [Fact]
    public void ScalarReplacementRemovesDeadAggregateCopiesToStackLocals()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("left", aggregateType),
                                new SsaAllocateLocalInstruction("right", aggregateType),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaAddressOfLocalRValue("left", aggregateType, aggregatePointerType, "&left")),
                                new SsaValueInstruction(
                                    "right_addr",
                                    new SsaAddressOfLocalRValue("right", aggregateType, aggregatePointerType, "&right")),
                                new SsaCopyMemoryInstruction(
                                    new SsaValueReference("right_addr", aggregatePointerType),
                                    new SsaValueReference("left_addr", aggregatePointerType),
                                    aggregateType)
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Instructions.OfType<SsaCopyMemoryInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsAggregateCopiesObservedByLaterFieldLoad()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
        var fieldPointerType = StarkTypeSymbols.RawPointer(valueType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("left", aggregateType),
                                new SsaAllocateLocalInstruction("right", aggregateType),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaAddressOfLocalRValue("left", aggregateType, aggregatePointerType, "&left")),
                                new SsaValueInstruction(
                                    "right_addr",
                                    new SsaAddressOfLocalRValue("right", aggregateType, aggregatePointerType, "&right")),
                                new SsaCopyMemoryInstruction(
                                    new SsaValueReference("right_addr", aggregatePointerType),
                                    new SsaValueReference("left_addr", aggregatePointerType),
                                    aggregateType),
                                new SsaValueInstruction(
                                    "right_value_addr",
                                    new SsaFieldAddressRValue(
                                        new SsaValueReference("right_addr", aggregatePointerType),
                                        aggregateType,
                                        "Left",
                                        0,
                                        fieldPointerType,
                                        "&right.Left")),
                                new SsaValueInstruction(
                                    "loaded",
                                    new SsaLoadIndirectRValue(
                                        new SsaValueReference("right_value_addr", fieldPointerType),
                                        valueType,
                                        "right.Left"))
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaValueReference("loaded", valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaCopyMemoryInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsAggregateCopiesAfterDestinationAddressEscapes()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("left", aggregateType),
                                new SsaAllocateLocalInstruction("right", aggregateType),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaAddressOfLocalRValue("left", aggregateType, aggregatePointerType, "&left")),
                                new SsaValueInstruction(
                                    "right_addr",
                                    new SsaAddressOfLocalRValue("right", aggregateType, aggregatePointerType, "&right")),
                                new SsaValueInstruction(
                                    "ignored",
                                    new SsaCallRValue(
                                        "Demo.Escape",
                                        [new SsaValueReference("right_addr", aggregatePointerType)],
                                        valueType,
                                        "Escape(&right)")),
                                new SsaCopyMemoryInstruction(
                                    new SsaValueReference("right_addr", aggregatePointerType),
                                    new SsaValueReference("left_addr", aggregatePointerType),
                                    aggregateType)
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaCopyMemoryInstruction>());
    }

    [Fact]
    public void ScalarReplacementKeepsDeadAggregateMoveCopiesConservative()
    {
        var valueType = StarkTypeSymbols.Integer(32);
        var aggregateType = StarkTypeSymbols.Named("Pair");
        var aggregatePointerType = StarkTypeSymbols.RawPointer(aggregateType, isMutable: true);
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
                                new SsaAllocateLocalInstruction("left", aggregateType),
                                new SsaAllocateLocalInstruction("right", aggregateType),
                                new SsaValueInstruction(
                                    "left_addr",
                                    new SsaAddressOfLocalRValue("left", aggregateType, aggregatePointerType, "&left")),
                                new SsaValueInstruction(
                                    "right_addr",
                                    new SsaAddressOfLocalRValue("right", aggregateType, aggregatePointerType, "&right")),
                                new SsaCopyMemoryInstruction(
                                    new SsaValueReference("right_addr", aggregatePointerType),
                                    new SsaValueReference("left_addr", aggregatePointerType),
                                    aggregateType,
                                    SsaMemoryTransferKind.Move)
                            ],
                            new SsaTerminator(
                                SsaTerminatorKind.Return,
                                [],
                                Value: new SsaIntegerConstant(0, valueType)))
                    ])
            ]);

        var optimized = new SsaScalarReplacementOptimizer().Optimize(module);
        var function = Assert.Single(optimized.Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Single(block.Instructions.OfType<SsaCopyMemoryInstruction>());
    }

    [Fact]
    public void ConstantPropagationFoldsConstantBranchesInLlvm()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run() {
                stack i32[min max] x = 1;
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

            fn i32[min max] Run() {
                stack i32[min max] value = 2;
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

    private static FunctionEffectModel Effects(params FunctionEffectProfile[] profiles)
    {
        return new FunctionEffectModel(
            "Demo",
            profiles.ToDictionary(static profile => profile.Name, StringComparer.Ordinal));
    }

    private static FunctionEffectProfile PureEffect(string name)
    {
        return new FunctionEffectProfile(
            name,
            StarkFunctionKind.FiniteLaw,
            ReadsArgumentMemory: false,
            IsPure: true,
            NoSync: true,
            NoFree: true,
            NoUnwind: true,
            WillReturn: true,
            MustProgress: true,
            UseFastCallingConvention: true,
            IsFfi: false,
            IsVarargs: false,
            IsHot: false,
            IsCold: false,
            InlinePreference: InlinePreference.InlineHint,
            IsStrictFp: false);
    }

    private static FunctionEffectProfile PureReadArgumentEffect(string name)
    {
        return PureEffect(name) with { ReadsArgumentMemory = true };
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
