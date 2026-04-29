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

            fn i32[-2147483648 2147483647] Run(bool flag) {
                stack mut i32[-2147483648 2147483647] value = 0;
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

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                stack i32[-2147483648 2147483647] first = left + right;
                stack i32[-2147483648 2147483647] second = right + left;
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
    public void OptimizedSsaRemovesTrivialPhiNodesAndRewritesReturns()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(bool flag) {
                stack mut i32[-2147483648 2147483647] value = 7;
                if (flag) {
                    value = 7;
                } else {
                    value = 7;
                }

                return value;
            }
        """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetOptimizedSsa(result).Functions);

        Assert.All(function.Blocks, static block => Assert.Empty(block.Phis));
        Assert.Contains(
            function.Blocks,
            static block => block.Terminator.Value is SsaIntegerConstant { Value: var value } && value == 7);
    }

    [Fact]
    public void EmptyTrampolineBlocksAreCollapsed()
    {
        var mir = new MidLevelIrModule(
            "Demo",
            [
                new MidLevelIrFunction(
                    "Run",
                    "Run() -> i32",
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

            fn i32[-2147483648 2147483647] Run() {
                stack mut i32[-2147483648 2147483647] i = 0;
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
    public void AggregateBranchJoinProducesByValuePhi()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run(bool flag) {
                stack mut Box box = new Box() { Value = 0 };
                if (flag) {
                    box = new Box() { Value = 1 };
                } else {
                    box = new Box() { Value = 2 };
                }

                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);

        var joinBlock = Assert.Single(function.Blocks, static block => block.Phis.Count == 1);
        var phi = Assert.Single(joinBlock.Phis);
        Assert.Equal("box", phi.VariableName);
        Assert.Equal(StarkTypeKind.Named, phi.Type.Kind);
        Assert.Equal(2, phi.Incomings.Count);
    }

    [Fact]
    public void UnreachableJoinBlocksArePrunedFromSsa()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
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
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
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
    public void RegisterObjectCreationRemainsScalarizedInSsa()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                register Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "box" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "box" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaInsertFieldRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaExtractFieldRValue });
    }

    [Fact]
    public void RegisterScalarInitializerRemainsDirectSsaValue()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                register mut i32[-2147483648 2147483647] value = 7;
                value = value + 1;
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "value" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "value" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaLifetimeStartInstruction { LocalName: "value" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaLifetimeEndInstruction { LocalName: "value" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaBinaryRValue { Operator: SsaBinaryOperator.Add } });
    }

    [Fact]
    public void HeapObjectCreationUsesStorageBackedSsaLocalWithoutStackLifetimeMarkers()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                heap Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "box", StorageClass: "heap" });
        Assert.Contains(instructions, static instruction => instruction is SsaDeallocateLocalInstruction { LocalName: "box", StorageClass: "heap" });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "box" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaLifetimeStartInstruction { LocalName: "box" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaLifetimeEndInstruction { LocalName: "box" });
    }

    [Fact]
    public void HeapFieldInitializationUsesAddressStoresWithoutAggregateLoads()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                heap mut Pair pair;
                pair.Left = left;
                pair.Right = right;
                return pair.Left + pair.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "pair", StorageClass: "heap" });
        Assert.Equal(2, instructions.Count(static instruction => instruction is SsaStoreIndirectInstruction));
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaFieldAddressRValue { FieldName: "Left" } });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaFieldAddressRValue { FieldName: "Right" } });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "pair" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaLoadLocalRValue { LocalName: "pair" } });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaInsertFieldRValue });
    }

    [Fact]
    public void HeapFixedArrayElementInitializationUsesAddressStores()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                heap mut i32[-2147483648 2147483647][2] values;
                values[0] = left;
                values[1] = right;
                return values[0] + values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "values", StorageClass: "heap" });
        Assert.Equal(2, instructions.Count(static instruction => instruction is SsaStoreIndirectInstruction));
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaElementAddressRValue { ConstantIndex: 0 } });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaElementAddressRValue { ConstantIndex: 1 } });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "values" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaInsertIndexRValue });
    }

    [Fact]
    public void AddressableAggregateAssignmentLowersToMemoryCopy()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Pair source = new Pair() { Left = 1, Right = 2 };
                stack mut Pair dest = new Pair() { Left = 0, Right = 0 };
                stack rawptr<Pair> sourcePtr = &source;
                stack rawptr<Pair> destPtr = &dest;
                dest = source;
                return dest.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(
            instructions,
            static instruction => instruction is SsaCopyMemoryInstruction
            {
                CopyType.Kind: StarkTypeKind.Named,
                TransferKind: SsaMemoryTransferKind.Move
            });
        Assert.Contains(
            instructions,
            static instruction => instruction is SsaStoreLocalInstruction
            {
                LocalName: "source",
                Value: SsaUndefValue { Type.Kind: StarkTypeKind.Named }
            });
    }

    [Fact]
    public void AggregateByValueCallInvalidatesMovedAddressableSource()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn void Touch(Pair value) {
            }

            fn i32[-2147483648 2147483647] Run() {
                stack mut Pair source = new Pair() { Left = 1, Right = 2 };
                stack rawptr<Pair> sourcePtr = &source;
                Touch(source);
                source = new Pair() { Left = 3, Right = 4 };
                return source.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions, static function => function.Name == "Run");
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaCallRValue { FunctionName: "Touch" } });
        Assert.Contains(
            instructions,
            static instruction => instruction is SsaStoreLocalInstruction
            {
                LocalName: "source",
                Value: SsaUndefValue { Type.Kind: StarkTypeKind.Named }
            });
    }

    [Fact]
    public void AddressableAggregateInitializerDoesNotMaterializeAggregateTempLocals()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Pair value = new Pair() { Left = 1, Right = 2 };
                stack rawptr<Pair> ptr = &value;
                return value.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "value", LocalType.Kind: StarkTypeKind.Named });
        Assert.DoesNotContain(
            instructions,
            instruction => instruction is SsaAllocateLocalInstruction { LocalType.Kind: StarkTypeKind.Named } allocate
                && allocate.LocalName.StartsWith("$tmp", StringComparison.Ordinal));
        Assert.DoesNotContain(
            instructions,
            instruction => instruction is SsaStoreLocalInstruction { LocalType.Kind: StarkTypeKind.Named } store
                && store.LocalName.StartsWith("$tmp", StringComparison.Ordinal));
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaCopyMemoryInstruction { CopyType.Kind: StarkTypeKind.Named });
    }

    [Fact]
    public void AddressableAggregateConditionalDoesNotMaterializeAggregateTempLocals()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i32[-2147483648 2147483647] Left;
                i32[-2147483648 2147483647] Right;
            }

            fn i32[-2147483648 2147483647] Run(bool flag) {
                stack Pair value = flag ? new Pair() { Left = 1, Right = 2 } : new Pair() { Left = 3, Right = 4 };
                stack rawptr<Pair> ptr = &value;
                return value.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "value", LocalType.Kind: StarkTypeKind.Named });
        Assert.DoesNotContain(
            instructions,
            instruction => instruction is SsaAllocateLocalInstruction { LocalType.Kind: StarkTypeKind.Named } allocate
                && allocate.LocalName.StartsWith("$tmp", StringComparison.Ordinal));
        Assert.DoesNotContain(
            instructions,
            instruction => instruction is SsaStoreLocalInstruction { LocalType.Kind: StarkTypeKind.Named } store
                && store.LocalName.StartsWith("$tmp", StringComparison.Ordinal));
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaCopyMemoryInstruction { CopyType.Kind: StarkTypeKind.Named });
    }

    [Fact]
    public void FixedArrayIndexOperationsLowerToSsaExtractAndInsert()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                values[1] = 9;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "values" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "values" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaLifetimeStartInstruction { LocalName: "values" });
        Assert.DoesNotContain(instructions, static instruction => instruction is SsaLifetimeEndInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaInsertIndexRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaExtractIndexRValue });
    }

    [Fact]
    public void SliceLoweringUsesLocalSlotsAndSliceLoads()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack i32[-2147483648 2147483647][3] values = { 4, 7, 9 };
                stack i32[-2147483648 2147483647][] view = values;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaLifetimeStartInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaLifetimeEndInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreLocalInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaMakeSliceFromLocalRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaSliceElementAddressRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaLoadIndirectRValue });
    }

    [Fact]
    public void TextSlicesLowerToSsaViewOperations()
    {
        var result = Compile(
            """
            module Demo

            fn unicode Run(unicode text, i32[-2147483648 2147483647] start, i32[-2147483648 2147483647] length) {
                return text[start, length];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(
            instructions,
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaTextSliceRValue
                {
                    Type.Kind: StarkTypeKind.Unicode,
                    Start.Type.Kind: StarkTypeKind.Integer,
                    Start.Type.BitWidth: 64,
                    Length.Type.Kind: StarkTypeKind.Integer,
                    Length.Type.BitWidth: 64
                }
            });
    }

    [Fact]
    public void ExplicitAsciiLiteralToUnicodeConversionLowersToUnicodeConstantInSsa()
    {
        var result = Compile(
            """
            module Demo

            fn unicode Run() {
                return (unicode)"Hello";
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var returnValue = function.Blocks.Single().Terminator.Value;

        var unicodeConstant = Assert.IsType<SsaStringConstant>(returnValue);
        Assert.Equal(StarkTypeKind.Unicode, unicodeConstant.Type.Kind);
        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaConvertRValue
                {
                    Operand.Type.Kind: StarkTypeKind.Ascii,
                    TargetType.Kind: StarkTypeKind.Unicode
                }
            });
    }

    [Fact]
    public void DynamicFixedArrayIndexMutationUsesIndirectAddressOps()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaLifetimeStartInstruction { LocalName: "values" });
        Assert.Contains(instructions, static instruction => instruction is SsaLifetimeEndInstruction { LocalName: "values" });
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

            fn i32[-2147483648 2147483647] Run(mut i32[-2147483648 2147483647][] view, i32[-2147483648 2147483647] index) {
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

    [Fact]
    public void ExplicitPointerOperatorsAndConversionsLowerToSsa()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i64[-9223372036854775808 9223372036854775807] bits) {
                stack mut i32[-2147483648 2147483647] value = 1;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
                stack rawptr<i32[-2147483648 2147483647]> readonlyPtr = (rawptr<i32[-2147483648 2147483647]>)ptr;
                *ptr = (i32[-2147483648 2147483647])bits;
                return *readonlyPtr;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "value" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaAddressOfLocalRValue });
        Assert.Contains(
            instructions,
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaConvertRValue { TargetType.Kind: StarkTypeKind.RawPointer, Operand.Type.Kind: StarkTypeKind.RawPointer }
            });
        Assert.Contains(
            instructions,
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaConvertRValue { TargetType.Kind: StarkTypeKind.Integer, Operand.Type.Kind: StarkTypeKind.Integer }
            });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreIndirectInstruction);
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaLoadIndirectRValue });
    }

    [Fact]
    public void FieldAndGlobalAddressExpressionsLowerToSsaAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] input) {
                stack mut Box box = new Box() { Value = 1 };
                *(&(box.Value)) = input;
                Counter = *(&(box.Value));
                return *(&Counter);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaAllocateLocalInstruction { LocalName: "box" });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaFieldAddressRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreIndirectInstruction);
        Assert.Contains(
            instructions,
            instruction => instruction is SsaValueInstruction
            {
                Value: SsaLoadIndirectRValue
                {
                    Address: SsaGlobalAddressValue { GlobalName: "Counter" }
                }
            });
    }

    [Fact]
    public void IndexedFieldAddressBehindRawPointerLowersToParameterBackedSsaAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i8[-128 127][16] Storage;
                i64[-9223372036854775808 9223372036854775807] WritePos;
            }

            fn i32[-2147483648 2147483647] Touch(rawmutptr<Buffer> buffer, i64[-9223372036854775808 9223372036854775807] index, i8[-128 127] value) {
                *(&(*buffer).Storage[index]) = value;
                return (i32[-2147483648 2147483647])*(&(*buffer).Storage[index]);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetSsa(result).Functions);
        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaFieldAddressRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaElementAddressRValue });
        Assert.Contains(instructions, static instruction => instruction is SsaStoreIndirectInstruction);
        Assert.Contains(instructions, static instruction => instruction is SsaValueInstruction { Value: SsaLoadIndirectRValue });
    }

    [Fact]
    public void ImmutableGlobalAddressesLowerToReadonlySsaAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static i32[-2147483648 2147483647] Counter = 0;
            static Box Current = new Box() { Value = 5 };

            fn rawptr<i32[-2147483648 2147483647]> CounterPtr() {
                return &Counter;
            }

            fn rawptr<i32[-2147483648 2147483647]> FieldPtr() {
                return &(Current.Value);
            }
            """);

        Assert.True(result.Succeeded);

        var ssa = GetSsa(result);
        var counterPtr = Assert.Single(ssa.Functions, static function => function.Name == "CounterPtr");
        var fieldPtr = Assert.Single(ssa.Functions, static function => function.Name == "FieldPtr");

        Assert.True(counterPtr.SupportsDirectCodeGeneration);
        Assert.True(fieldPtr.SupportsDirectCodeGeneration);

        var counterReturn = Assert.IsType<SsaGlobalAddressValue>(Assert.Single(counterPtr.Blocks).Terminator.Value);
        Assert.False(counterReturn.Type.IsMutablePointer);

        var fieldInstructions = fieldPtr.Blocks.SelectMany(static block => block.Instructions).ToArray();
        Assert.Contains(
            fieldInstructions,
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaFieldAddressRValue { Type.IsMutablePointer: false }
            });
        Assert.DoesNotContain(
            fieldInstructions,
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaConvertRValue
                {
                    TargetType: { Kind: StarkTypeKind.RawPointer },
                    Operand.Type: { Kind: StarkTypeKind.RawPointer }
                }
            });
    }

    [Fact]
    public void FrozenSliceAddressesLowerToReadonlySsaAddresses()
    {
        var result = Compile(
            """
            module Demo

            fn rawptr<frozen i32[-2147483648 2147483647]> FirstPtr(frozen i32[-2147483648 2147483647][] view) {
                return &(view[0]);
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetSsa(result).Functions);
        Assert.True(function.SupportsDirectCodeGeneration);

        var instructions = function.Blocks.SelectMany(static block => block.Instructions).ToArray();
        Assert.Contains(
            instructions,
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaSliceElementAddressRValue { Type.IsMutablePointer: false }
            });
    }

    [Fact]
    public void RuntimeDisjointTrueBranchCarriesScopedNoAliasFactsIntoSsaMemoryOperations()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(
                rawmutptr<i32[-2147483648 2147483647]> left,
                rawmutptr<i32[-2147483648 2147483647]> right) {
                if disjoint(left, right) {
                    *left = 7;
                    return *right;
                }

                *left = 1;
                return *right;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var function = Assert.Single(GetSsa(result).Functions);
        var trueBranch = Assert.Single(function.Blocks, static block => block.Label.Contains("if_then", StringComparison.Ordinal));
        var falsePath = Assert.Single(function.Blocks, static block => block.Label.Contains("if_join", StringComparison.Ordinal));

        var trueStore = Assert.Single(trueBranch.Instructions.OfType<SsaStoreIndirectInstruction>());
        var trueLoad = Assert.Single(
            trueBranch.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);
        var trueStoreGroup = Assert.Single(trueStore.ScopedNoAliasGroups ?? []);
        var trueLoadGroup = Assert.Single(trueLoad.ScopedNoAliasGroups ?? []);

        Assert.Equal(trueStoreGroup, trueLoadGroup);
        Assert.Contains("param:left", trueStoreGroup.RootKeys);
        Assert.Contains("param:right", trueStoreGroup.RootKeys);

        var falseStore = Assert.Single(falsePath.Instructions.OfType<SsaStoreIndirectInstruction>());
        var falseLoad = Assert.Single(
            falsePath.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaLoadIndirectRValue);

        Assert.Null(falseStore.ScopedNoAliasGroups);
        Assert.Null(falseLoad.ScopedNoAliasGroups);
    }

    [Fact]
    public void IndependentForLoopsPreserveLoopContractsInSsa()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 10] Run() {
                stack mut i32[0 10] sum = 0;
                for willexit independent (stack mut i32[0 10] index = 0; index < 4; index += 1) {
                    sum += index;
                }

                return sum;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var function = Assert.Single(GetSsa(result).Functions);

        Assert.Contains(
            function.Blocks.Select(static block => block.Terminator),
            static terminator => terminator.LoopContracts is { Count: > 0 }
                && terminator.LoopContracts.Contains("independent", StringComparer.Ordinal));
    }

    [Fact]
    public void IndependentSliceLoopsCarryAccessGroupsInSsa()
    {
        var result = Compile(
            """
            module Demo

            fn void Add(
                disjoint borrow i32[-2147483648 2147483647][] left,
                disjoint borrow i32[-2147483648 2147483647][] right,
                disjoint borrow mut i32[-2147483648 2147483647][] output,
                i32[0 10] count) {
                for willexit independent (stack mut i32[0 10] index = 0; index < count; index += 1) {
                    output[index] = left[index] + right[index];
                }

                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var function = Assert.Single(GetSsa(result).Functions);

        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is SsaValueInstruction
            {
                Value: SsaLoadIndirectRValue,
                LoopAccessGroups.Count: > 0
            });
        Assert.Contains(
            function.Blocks.Select(static block => block.Terminator),
            static terminator => terminator.LoopAccessGroups is { Count: > 0 });
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

    private static SsaIrModule GetOptimizedSsa(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        return ssa;
    }
}
