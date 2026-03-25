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

            fn i32 Main() {
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

            fn void Main() {
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

            fn void Main() {
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

            fn i32 Main(i32 value) {
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

            fn i32 Main(i32 value, bool allow) {
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

            fn i32 Main(i32 value, bool allow) {
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

            fn i32 Main(i32 value, bool allow) {
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
    public void CaptureSwitchPatternLowersToMatchLocalAndBody()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main(i32 value, bool allow) {
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
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
    }

    [Fact]
    public void ShortCircuitOrLowersToMultipleBlocksAndDirectCodegen()
    {
        var result = Compile(
            """
            module Demo

            fn bool Main(bool left, bool right, bool fallback) {
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

            fn i32 Main(bool flag) {
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

            fn i32 Main() {
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

            fn i32 Main(i32 left, i32 right) {
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

            fn i32 Main() {
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

            fn f32 Main() {
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

            fn ascii MainAscii() {
                return 'a';
            }

            fn unicode MainUnicode() {
                return '\u03B1';
            }
            """);

        Assert.True(result.Succeeded);
        var functions = GetMir(result).Functions.ToArray();

        Assert.Equal(2, functions.Length);
        Assert.All(functions, function => Assert.True(function.SupportsDirectCodeGeneration));

        var asciiReturn = Assert.Single(functions, function => function.Name == "MainAscii").Blocks.Single().Terminator.Value;
        Assert.IsType<MidLevelIrStringConstantOperand>(asciiReturn);
        Assert.Equal(StarkTypeKind.Ascii, asciiReturn!.Type.Kind);

        var unicodeReturn = Assert.Single(functions, function => function.Name == "MainUnicode").Blocks.Single().Terminator.Value;
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

            fn i32 Main() {
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

            fn i32 Main() {
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
    public void FixedArrayInitializerAndConstantIndexLowerToAggregateIndexOperations()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Main() {
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

            fn i32 Main() {
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

            fn i32 Main(i32 index) {
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

            fn i32 Main(i32 index) {
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

            fn i32 Main(i32[] view, i32 index) {
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
