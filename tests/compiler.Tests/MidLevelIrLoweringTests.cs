using Stark.Compiler;

namespace compiler.Tests;

public sealed class MidLevelIrLoweringTests
{
    [Fact]
    public void IfStatementsLowerToBranchingBlocks()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                if (true) {
                    return 1;
                } else {
                    return 2;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var mir = GetMir(result);
        var function = Assert.Single(mir.Functions);

        Assert.True(function.Blocks.Count >= 4);
        Assert.Equal(MidLevelIrTerminatorKind.Branch, function.Blocks[0].Terminator.Kind);
        Assert.NotNull(function.Blocks[0].Terminator.Condition);
        Assert.Equal(StarkTypeKind.Bool, function.Blocks[0].Terminator.Condition!.Type.Kind);
        Assert.Contains(function.Blocks, block => block.Label.Contains("if_then", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("if_else", StringComparison.Ordinal));
    }

    [Fact]
    public void WhileLoopsLowerToBackedgeControlFlow()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                while willexit (true) {
                    break;
                }

                return;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.Contains(function.Blocks, block => block.Label.Contains("while_willexit_cond", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("while_body", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("while_exit", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Terminator.Kind == MidLevelIrTerminatorKind.Goto && block.Terminator.Targets.Count == 1);
    }

    [Fact]
    public void ForLoopsProduceConditionIteratorAndExitBlocks()
    {
        var result = Compile(
            """
            module Demo

            fn void Run() {
                for willexit (stack i32 i = 0; i < 4; i = i + 1) {
                    continue;
                }

                return;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.Contains(function.Locals, local => local.Name == "i");
        Assert.Contains(function.Blocks, block => block.Label.Contains("for_willexit_cond", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("for_iter", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("for_exit", StringComparison.Ordinal));
    }

    [Fact]
    public void LiteralSwitchLowersToMirSwitchTerminator()
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
                    default:
                        return 30;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_case_0", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_case_1", StringComparison.Ordinal));
    }

    [Fact]
    public void GuardedSwitchLowersToBranchBasedCfg()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
                    case 1 when allow:
                        return 10;
                    default:
                        return 20;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.True(function.Blocks.Count(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch) >= 2);
    }

    [Fact]
    public void GuardedDiscardSwitchLowersToBranchBasedCfg()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
                    case _ when allow:
                        return 10;
                    default:
                        return 20;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(function.Blocks, block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch && block.Terminator.ConditionText == "allow");
    }

    [Fact]
    public void MultiLabelSectionWithGuardedDiscardLowersInOrder()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
                    case 1:
                    case _ when allow:
                        return 10;
                    default:
                        return 20;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_test_1", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch && block.Terminator.ConditionText == "allow");
        Assert.True(function.Blocks.Count(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch) >= 2);
    }

    [Fact]
    public void MultiLabelSectionsNormalizeIntoSectionDecisionTrees()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
                    case 1:
                    case 2 when allow:
                        return 10;
                    default:
                        return 20;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_test_0", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_test_0_1", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_case_0", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureSwitchPatternLowersToMatchLocalAndBody()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value, bool allow) {
                switch (value) {
                    case var capture when allow:
                        return capture;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "capture");
        Assert.Contains(function.Blocks.SelectMany(static block => block.Statements), statement => statement.TargetName == "capture");

        var captureBlock = Assert.Single(
            function.Blocks,
            static block => block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.Contains("switch_bind", captureBlock.Label, StringComparison.Ordinal);
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
    }

    [Fact]
    public void ComparisonChainsLowerToShortCircuitBlocksAndReuseSharedOperands()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Next() {
                return 1;
            }

            fn bool Run() {
                return 0 < Next() < 3;
            }
            """);

        Assert.True(result.Succeeded);
        var run = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");

        Assert.True(run.SupportsDirectCodeGeneration);
        Assert.Single(
            run.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Next" });
        Assert.Contains(run.Blocks, block => block.Label.Contains("cmpchain_next_1", StringComparison.Ordinal));
        Assert.Contains(run.Blocks, block => block.Label.Contains("cmpchain_false_0", StringComparison.Ordinal));
        Assert.Contains(run.Blocks, block => block.Label.Contains("cmpchain_join", StringComparison.Ordinal));
    }

    [Fact]
    public void TextLiteralSwitchLowersToBranchBasedComparisonTree()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(ascii value, bool allow) {
                switch (value) {
                    case "ab":
                        return 1;
                    case "cd" when allow:
                        return 2;
                    default:
                        return 3;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(function.Blocks, block => block.Label.Contains("textcmp_byte_0", StringComparison.Ordinal));
        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldIndex: 1 });
        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrLoadIndirectRValue { Type.Kind: StarkTypeKind.Integer });
        Assert.Contains(function.Blocks, block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch && block.Terminator.ConditionText == "allow");
    }

    [Fact]
    public void LargeTextLiteralSwitchLowersThroughLengthPartitioning()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(ascii value) {
                switch (value) {
                    case "":
                        return 0;
                    case "a":
                        return 1;
                    case "b":
                        return 2;
                    case "cc":
                        return 3;
                    case "dd":
                        return 4;
                    case "eee":
                        return 5;
                    case "fff":
                        return 6;
                    case "gggg":
                        return 7;
                    case "hhhh":
                        return 8;
                    case "iiiii":
                        return 9;
                    default:
                        return 10;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_len_0", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_len_1", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_len_2", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("switch_len_5", StringComparison.Ordinal));
    }

    [Fact]
    public void UnicodeTextLiteralSwitchLowersSuccessfully()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(unicode value) {
                switch (value) {
                    case "\u03c0":
                        return 1;
                    case "\u03bb":
                        return 2;
                    default:
                        return 3;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch && block.Terminator.Condition?.Type.Kind == StarkTypeKind.Unicode);
    }

    [Fact]
    public void ShortCircuitOrLowersToMultipleBlocksAndDirectCodegen()
    {
        var result = Compile(
            """
            module Demo

            fn bool Run(bool left, bool right, bool fallback) {
                return left || right || fallback;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.True(function.Blocks.Count >= 5);
        Assert.Contains(function.Locals, local => local.Name.Contains("_or", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
    }

    [Fact]
    public void ConditionalExpressionLowersToJoinableBlocks()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(bool flag) {
                return flag ? 1 : 2;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name.Contains("_cond", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("cond_true", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("cond_false", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("cond_join", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalDeclarationsAndAssignmentsLowerToMirStatements()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack mut i32 value = 1;
                value = value + 1;
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var entry = function.Blocks[function.EntryBlockId];

        Assert.Contains(function.Locals, local => local.Name == "value" && local.IsMutable);
        Assert.Contains(entry.Statements, statement => statement.Kind == MidLevelIrStatementKind.StorageLive && statement.TargetName == "value");
        Assert.Contains(entry.Statements, statement => statement.Kind == MidLevelIrStatementKind.Assign && statement.Text.Contains("value = 1", StringComparison.Ordinal));
        Assert.Contains(entry.Statements, statement => statement.Kind == MidLevelIrStatementKind.Assign && statement.Text.Contains("value = value+1", StringComparison.Ordinal));
        Assert.True(function.SupportsDirectCodeGeneration);

        Assert.Contains(
            entry.Statements,
            statement => statement.Value is MidLevelIrConvertRValue && statement.TargetName == "$tmp0_intcast");

        var binary = Assert.Single(entry.Statements, static statement => statement.Value is MidLevelIrBinaryRValue);
        Assert.Equal("$tmp2_bin", binary.TargetName);
        Assert.Equal(MidLevelIrBinaryOperator.Add, ((MidLevelIrBinaryRValue)binary.Value!).Operator);
    }

    [Fact]
    public void BitwiseXorExpressionLowersToMirBinaryOperation()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 right) {
                return left ^ right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var binary = Assert.Single(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrBinaryRValue);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal(MidLevelIrBinaryOperator.BitwiseXor, ((MidLevelIrBinaryRValue)binary.Value!).Operator);
    }

    [Fact]
    public void BitwiseXorAssignmentLowersToMirBinaryOperation()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack mut i32 value = 6;
                value ^= 3;
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var binary = Assert.Single(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrBinaryRValue);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal(MidLevelIrBinaryOperator.BitwiseXor, ((MidLevelIrBinaryRValue)binary.Value!).Operator);
    }

    [Fact]
    public void ExponentExpressionLowersToMirBinaryOperation()
    {
        var result = Compile(
            """
            module Demo

            fn f32 Run() {
                return 2.0 ** 3.0;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var binary = Assert.Single(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrBinaryRValue);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal(MidLevelIrBinaryOperator.Exponent, ((MidLevelIrBinaryRValue)binary.Value!).Operator);
    }

    [Fact]
    public void CharacterLiteralsLowerToMirStringConstants()
    {
        var result = Compile(
            """
            module Demo

            fn ascii AsciiChar() {
                return 'a';
            }

            fn unicode UnicodeChar() {
                return '\u03B1';
            }
            """);

        Assert.True(result.Succeeded);
        var functions = GetMir(result).Functions.ToArray();

        Assert.Equal(2, functions.Length);
        Assert.All(functions, function => Assert.True(function.SupportsDirectCodeGeneration));

        var asciiReturn = Assert.Single(functions, function => function.Name == "AsciiChar").Blocks.Single().Terminator.Value;
        Assert.IsType<MidLevelIrStringConstantOperand>(asciiReturn);
        Assert.Equal(StarkTypeKind.Ascii, asciiReturn!.Type.Kind);

        var unicodeReturn = Assert.Single(functions, function => function.Name == "UnicodeChar").Blocks.Single().Terminator.Value;
        Assert.IsType<MidLevelIrStringConstantOperand>(unicodeReturn);
        Assert.Equal(StarkTypeKind.Unicode, unicodeReturn!.Type.Kind);
    }

    [Fact]
    public void ObjectCreationAndFieldAccessLowerToAggregateOperations()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack Box box = new Box() { Value = 41 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue);
    }

    [Fact]
    public void FieldAssignmentLowersToAggregateUpdate()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value = 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, statement => statement.Text.Contains("box.Value = 2", StringComparison.Ordinal));
        Assert.True(statements.Count(static statement => statement.Value is MidLevelIrInsertFieldRValue) >= 2);
    }

    [Fact]
    public void MemberCallsEvaluateReceiverBeforeExplicitArguments()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                fn i32 Pick(Box box, i32 value) {
                    return value;
                }
            }

            fn Box Make() {
                return new Box();
            }

            fn i32 Next() {
                return 7;
            }

            fn i32 Run() {
                return Make().Pick(Next());
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var calls = function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>()
            .Select(static call => call.FunctionName)
            .ToArray();

        Assert.Equal(["Make", "Next", "Box.Pick"], calls);
    }

    [Fact]
    public void FixedArrayInitializerAndConstantIndexLowerToAggregateIndexOperations()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack i32[3] values = { 1, 2, 3 };
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertIndexRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractIndexRValue);
    }

    [Fact]
    public void FixedArrayElementAssignmentLowersToAggregateIndexUpdate()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack mut i32[3] values = { 1, 2, 3 };
                values[1] = 9;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.True(statements.Count(static statement => statement.Value is MidLevelIrInsertIndexRValue) >= 2);
        Assert.Contains(statements, statement => statement.Text.Contains("values[1] = 9", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalFixedArrayCanLowerToSliceAndDynamicSliceRead()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 index) {
                stack i32[3] values = { 4, 7, 9 };
                stack i32[] view = values;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "values" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrMakeSliceFromLocalRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void DynamicFixedArrayIndexMutationUsesAddressBasedMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 index) {
                stack mut i32[3] values = { 1, 2, 3 };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "values" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrAddressOfLocalRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void SliceMutationUsesAddressBasedMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32[] view, i32 index) {
                view[index] = 9;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void ExplicitPointerOperatorsAndConversionsLowerToMir()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i64 bits) {
                stack mut i32 value = 1;
                stack rawmutptr<i32> ptr = &value;
                stack rawptr<i32> readonlyPtr = (rawptr<i32>)ptr;
                *ptr = (i32)bits;
                return *readonlyPtr;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "value" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrAddressOfLocalRValue);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrConvertRValue convert
                && convert.TargetType.Kind == StarkTypeKind.RawPointer
                && convert.Operand.Type.Kind == StarkTypeKind.RawPointer);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrConvertRValue convert
                && convert.TargetType.Kind == StarkTypeKind.Integer
                && convert.Operand.Type.Kind == StarkTypeKind.Integer);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void FieldAndGlobalAddressExpressionsLowerToMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            static mut i32 Counter = 0;

            fn i32 Run(i32 input) {
                stack mut Box box = new Box() { Value = 1 };
                *(&(box.Value)) = input;
                Counter = *(&(box.Value));
                return *(&Counter);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "box" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(
            statements,
            statement => statement.Value is MidLevelIrLoadIndirectRValue load
                && load.Address is MidLevelIrGlobalAddressOperand { Name: "Counter" });
    }

    [Fact]
    public void ImmutableGlobalAddressesLowerToReadonlyMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            static i32 Counter = 0;
            static Box Current = new Box() { Value = 5 };

            fn rawptr<i32> CounterPtr() {
                return &Counter;
            }

            fn rawptr<i32> FieldPtr() {
                return &(Current.Value);
            }
            """);

        Assert.True(result.Succeeded);

        var mir = GetMir(result);
        var counterPtr = Assert.Single(mir.Functions, static function => function.Name == "CounterPtr");
        var fieldPtr = Assert.Single(mir.Functions, static function => function.Name == "FieldPtr");

        Assert.True(counterPtr.SupportsDirectCodeGeneration);
        Assert.True(fieldPtr.SupportsDirectCodeGeneration);
        Assert.IsType<MidLevelIrGlobalAddressOperand>(Assert.Single(counterPtr.Blocks).Terminator.Value);
        Assert.Equal(
            new[] { false },
            counterPtr.Blocks
                .Select(block => block.Terminator.Value)
                .OfType<MidLevelIrGlobalAddressOperand>()
                .Select(static address => address.Type.IsMutablePointer)
                .ToArray());

        var fieldStatements = fieldPtr.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            fieldStatements,
            static statement => statement.Value is MidLevelIrFieldAddressRValue { Type.IsMutablePointer: false });
        Assert.DoesNotContain(
            fieldStatements,
            static statement => statement.Value is MidLevelIrConvertRValue
            {
                TargetType: { Kind: StarkTypeKind.RawPointer },
                Operand.Type: { Kind: StarkTypeKind.RawPointer }
            });
    }

    [Fact]
    public void FrozenSliceAddressesLowerToReadonlyMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            fn rawptr<frozen i32> FirstPtr(frozen i32[] view) {
                return &(view[0]);
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions);
        Assert.True(function.SupportsDirectCodeGeneration);

        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrSliceElementAddressRValue { Type.IsMutablePointer: false });

        var returned = Assert.IsType<MidLevelIrLocalOperand>(Assert.Single(function.Blocks).Terminator.Value);
        Assert.Equal(StarkTypeKind.RawPointer, returned.Type.Kind);
        Assert.False(returned.Type.IsMutablePointer);
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }

    private static MidLevelIrModule GetMir(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        return mir;
    }
}
