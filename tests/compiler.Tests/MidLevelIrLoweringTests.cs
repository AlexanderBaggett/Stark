using Stark.Compiler;
using System.Text;
using Xunit.Abstractions;

namespace compiler.Tests;

public sealed class MidLevelIrLoweringTests
{
    private readonly ITestOutputHelper _output;

    public MidLevelIrLoweringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void IfStatementsLowerToBranchingBlocks()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run() {
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

            finite law void Run() {
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

            finite law void Run() {
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

            finite law i32 Run(i32 value) {
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

            finite law i32 Run(i32 value, bool allow) {
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

            finite law i32 Run(i32 value, bool allow) {
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

            finite law i32 Run(i32 value, bool allow) {
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

            finite law i32 Run(i32 value, bool allow) {
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

            finite law i32 Run(i32 value, bool allow) {
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
    public void AggregateSwitchPatternBindsScalarFieldsAfterPatternSelection()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32 Left, i32 Right) { }

            finite law i32 Run(Pair value) {
                switch (value) {
                    case Pair(1, var right):
                        return right;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, static local => local.Name == "right");
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Left" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Right" });

        var captureBlock = Assert.Single(
            function.Blocks,
            static block => block.Label.Contains("switch_agg_match", StringComparison.Ordinal)
                && block.Statements.Any(static statement => statement.TargetName == "right"));
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_agg_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement => statement.TargetName == "right"));
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement => statement.TargetName == "right"));
        Assert.Contains("switch_agg_match", captureBlock.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedAggregateSwitchPatternBindsScalarLeavesAfterPatternSelection()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32 Left, i32 Right) { }
            record Outer(Pair Values, i32 Tail) { }

            finite law i32 Run(Outer value) {
                switch (value) {
                    case Outer(Pair(1, var right), var tail):
                        return right + tail;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, static local => local.Name == "right");
        Assert.Contains(function.Locals, static local => local.Name == "tail");
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Values" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Tail" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Left" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Right" });

        var captureBlock = Assert.Single(
            function.Blocks,
            static block => block.Label.Contains("switch_agg_match", StringComparison.Ordinal)
                && block.Statements.Any(static statement => statement.TargetName == "right")
                && block.Statements.Any(static statement => statement.TargetName == "tail"));
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_agg_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement => statement.TargetName == "right" || statement.TargetName == "tail"));
        Assert.Contains("switch_agg_match", captureBlock.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumConstructorsLowerToDirectTagFieldInserts()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            fn i32 Run() {
                stack Token a = Token.End;
                stack Token b = Token.Integer(5);
                stack Token c = Token.Move { X: 1, Y: 2 };
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "$Integer_0" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "$Move_X" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "$Move_Y" });
    }

    [Fact]
    public void EnumSwitchPatternsLowerToTagTestsAndActivePayloadExtractions()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            fn i32 Run(Token token) {
                switch (token) {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value;
                    case Token.Move { X: var x, Y: var y }:
                        return x + y;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "$tag" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "$Integer_0" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "$Move_X" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "$Move_Y" });
        Assert.Contains(function.Blocks, static block => block.Label.Contains("switch_enum_match", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumSwitchExpressionCallIsLoweredOnceInMir()
    {
        var result = Compile(
            """
            module Demo

            enum Status {
                Ok,
                Err(i32),
            }

            fn Status Next() {
                return Status.Ok;
            }

            fn i32 Run() {
                switch (Next()) {
                    case Status.Ok:
                        return 1;
                    case Status.Err(var error):
                        return error;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.Equal(
            1,
            statements.Count(static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Next" }));
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

            finite law i32 Run(i32 left, i32 right) {
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
    public void BitwiseAndShiftChainsRespectPrecedenceAndAssociativity()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 middle, i32 right, i32 mask) {
                return left | middle ^ right & mask << 1 >> 1;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var operators = function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrBinaryRValue>()
            .Select(static binary => binary.Operator)
            .ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal(
            [
                MidLevelIrBinaryOperator.ShiftLeft,
                MidLevelIrBinaryOperator.ShiftRight,
                MidLevelIrBinaryOperator.BitwiseAnd,
                MidLevelIrBinaryOperator.BitwiseXor,
                MidLevelIrBinaryOperator.BitwiseOr
            ],
            operators);
    }

    [Fact]
    public void WrappingAndSaturatingArithmeticLowerToDistinctMirOperators()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 right) {
                stack mut i32 wrapped = left;
                wrapped +%= right;
                stack i32 wrapProduct = -%wrapped *% 2;

                stack mut i32 saturated = left;
                saturated +|= right;
                saturated *|= 2;
                stack i32 bounded = saturated -| 3;

                return wrapProduct + bounded;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var operators = function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrBinaryRValue>()
            .Select(static binary => binary.Operator)
            .ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(MidLevelIrBinaryOperator.WrappingAdd, operators);
        Assert.Contains(MidLevelIrBinaryOperator.WrappingSubtract, operators);
        Assert.Contains(MidLevelIrBinaryOperator.WrappingMultiply, operators);
        Assert.Contains(MidLevelIrBinaryOperator.SaturatingAdd, operators);
        Assert.Contains(MidLevelIrBinaryOperator.SaturatingSubtract, operators);
        Assert.Contains(MidLevelIrBinaryOperator.SaturatingMultiply, operators);
    }

    [Fact]
    public void ExponentExpressionLowersToMirBinaryOperation()
    {
        var result = Compile(
            """
            module Demo

            finite law f32 Run() {
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

            finite law ascii AsciiChar() {
                return 'a';
            }

            finite law unicode UnicodeChar() {
                return (unicode)'\u03B1';
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
    public void ExplicitAsciiLiteralToUnicodeConversionLowersToUnicodeStringConstant()
    {
        var result = Compile(
            """
            module Demo

            finite law unicode Run() {
                return (unicode)"Hello";
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var returnValue = function.Blocks.Single().Terminator.Value;

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.IsType<MidLevelIrStringConstantOperand>(returnValue);
        Assert.Equal(StarkTypeKind.Unicode, returnValue!.Type.Kind);
        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrConvertRValue
            {
                Operand.Type.Kind: StarkTypeKind.Ascii,
                TargetType.Kind: StarkTypeKind.Unicode
            });
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
    public void FieldCompoundAssignmentLowersToAggregateReadModifyWrite()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value += 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, statement => statement.Text.Contains("box.Value += 2", StringComparison.Ordinal));
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
        Assert.True(statements.Count(static statement => statement.Value is MidLevelIrInsertFieldRValue) >= 2);
    }

    [Fact]
    public void PrimaryRecordConstructorArgumentsLowerInEvaluationOrder()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32 Left, i32 Right) { }

            fn i32 First() {
                return 1;
            }

            fn i32 Second() {
                return 2;
            }

            fn i32 Run() {
                stack Pair pair = new Pair(First(), Second());
                return pair.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var calls = statements
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>()
            .Select(static call => call.FunctionName)
            .ToArray();
        var insertTexts = statements
            .Where(static statement => statement.Value is MidLevelIrInsertFieldRValue)
            .Select(static statement => statement.Text)
            .ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal(["First", "Second"], calls);
        Assert.Equal(2, insertTexts.Length);
        Assert.Contains(".Left = First()", insertTexts[0], StringComparison.Ordinal);
        Assert.Contains(".Right = Second()", insertTexts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorInitializerCombinationAppliesInitializerAfterConstructorFields()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32 Left) {
                i32 Right;
            }

            fn i32 First() {
                return 1;
            }

            fn i32 Override() {
                return 4;
            }

            fn i32 Run() {
                stack Pair pair = new Pair(First()) { Right = Override() };
                return pair.Right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var calls = statements
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>()
            .Select(static call => call.FunctionName)
            .ToArray();
        var insertTexts = statements
            .Where(static statement => statement.Value is MidLevelIrInsertFieldRValue)
            .Select(static statement => statement.Text)
            .ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal(["First", "Override"], calls);
        Assert.Equal(2, insertTexts.Length);
        Assert.Contains(".Left = First()", insertTexts[0], StringComparison.Ordinal);
        Assert.Contains(".Right = Override()", insertTexts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NestedObjectAndArrayInitializersLowerRecursivelyInSourceOrder()
    {
        var result = Compile(
            """
            module Demo

            struct Inner {
                i32[2] Pair;
            }

            struct Outer {
                i32 Score;
                Inner Node;
            }

            fn i32 MakeScore() {
                return 9;
            }

            fn i32 MakeLeft() {
                return 4;
            }

            fn i32 MakeRight() {
                return 7;
            }

            fn i32 Run() {
                stack Outer outer = new Outer() {
                    Score = MakeScore(),
                    Node = { Pair = { MakeLeft(), MakeRight() } }
                };
                return outer.Node.Pair[1] + outer.Score;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var calls = statements
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>()
            .Select(static call => call.FunctionName)
            .ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal(["MakeScore", "MakeLeft", "MakeRight"], calls);
        Assert.Contains(statements, statement => statement.Text.Contains(".Score = MakeScore()", StringComparison.Ordinal));
        Assert.Contains(statements, statement => statement.Value is MidLevelIrInsertIndexRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "Node" });
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
    public void TextSlicesLowerToViewProducingMir()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Run(ascii text, i32 start, i32 length) {
                return text[start, length];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrTextSliceRValue
            {
                Type.Kind: StarkTypeKind.Ascii,
                Start.Type.Kind: StarkTypeKind.Integer,
                Start.Type.BitWidth: 64,
                Length.Type.Kind: StarkTypeKind.Integer,
                Length.Type.BitWidth: 64
            });
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
    public void NestedLvalueChainsWithDynamicIndexCompoundAssignmentsUseAddressBasedMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            struct Cell {
                i32 Value;
            }

            struct Holder {
                Cell[2] Cells;
            }

            fn i32 Run(i32 index) {
                stack mut Holder holder = new Holder() {
                    Cells = { new Cell() { Value = 1 }, new Cell() { Value = 2 } }
                };
                holder.Cells[index].Value += 4;
                return holder.Cells[index].Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "holder" && local.IsAddressable);
        Assert.Contains(statements, statement => statement.Text.Contains("holder.Cells[index].Value += 4", StringComparison.Ordinal));
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
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
    public void MixedCallMemberAndIndexPostfixChainsLowerToMir()
    {
        var result = Compile(
            """
            module Demo

            struct Cell {
                i32 Value;
            }

            struct Holder {
                Cell[2] Cells;
            }

            fn Holder Make() {
                return new Holder() {
                    Cells = { new Cell() { Value = 3 }, new Cell() { Value = 5 } }
                };
            }

            fn i32 Run() {
                return Make().Cells[1].Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Make" });
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Cells" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractIndexRValue { ElementIndex: 1 });
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Value" });
    }

    [Fact]
    public void RegisterObjectCreationKeepsValueStyleLocalLowering()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                register Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var box = Assert.Single(function.Locals, static local => local.Name == "box");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal("register", box.StorageClass);
        Assert.False(box.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue);
    }

    [Fact]
    public void HeapObjectCreationMarksLocalAsAddressableStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
                heap Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var box = Assert.Single(function.Locals, static local => local.Name == "box");

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Equal("heap", box.StorageClass);
        Assert.True(box.IsAddressable);
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
    public void IndexedFieldAddressBehindRawPointerLowersToParameterBackedMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i8[16] Storage;
                i64 WritePos;
            }

            fn i32 Touch(rawmutptr<Buffer> buffer, i64 index, i8 value) {
                *(&(*buffer).Storage[index]) = value;
                return (i32)*(&(*buffer).Storage[index]);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
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

    [Fact]
    public void DestructorBlocksLowerBeforeStorageDeadAtScopeExit()
    {
        var result = Compile(
            """
            module Demo

            static mut i32 Counter = 0;

            fn void Bump(i32 value) {
                Counter = Counter + value;
                return;
            }

            struct Buffer {
                i32 Value;

                drop {
                    Bump(self.Value);
                }
            }

            fn void Run() {
                stack Buffer box = new Buffer() { Value = 4 };
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var callIndex = Array.FindIndex(
            statements,
            static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });
        var storageDeadIndex = Array.FindIndex(
            statements,
            static statement => statement.Kind == MidLevelIrStatementKind.StorageDead && statement.TargetName == "box");

        Assert.True(callIndex >= 0);
        Assert.True(storageDeadIndex > callIndex);
    }

    [Fact]
    public void ReassigningADestructibleLocalLowersTheOldDropBeforeOverwrite()
    {
        var result = Compile(
            """
            module Demo

            static mut i32 Counter = 0;

            fn void Bump(i32 value) {
                Counter = Counter + value;
                return;
            }

            struct Buffer {
                i32 Value;

                drop {
                    Bump(self.Value);
                }
            }

            fn void Run() {
                stack mut Buffer box = new Buffer() { Value = 1 };
                box = new Buffer() { Value = 7 };
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        var boxAssignments = statements
            .Select((statement, index) => (statement, index))
            .Where(static item => item.statement.Kind == MidLevelIrStatementKind.Assign && item.statement.TargetName == "box")
            .ToArray();
        var dropCalls = statements
            .Select((statement, index) => (statement, index))
            .Where(static item => item.statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" })
            .ToArray();

        Assert.Equal(2, boxAssignments.Length);
        Assert.True(dropCalls.Length >= 2);
        Assert.True(dropCalls[0].index > boxAssignments[0].index);
        Assert.True(dropCalls[0].index < boxAssignments[1].index);
    }

    [Fact]
    public void ImportedTypeDestructorsResolveHelpersInTheirDefiningModule()
    {
        var result = Compile(
            """
            import Lib
            module Demo

            fn void Run() {
                stack Lib.Buffer box = new Lib.Buffer() { Value = 4 };
                return;
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Lib", "/virtual/Lib.stark", IsExternal: false),
                        """
                        module Lib

                        fn void Bump(i32 value) {
                            return;
                        }

                        public struct Buffer {
                            i32 Value;

                            drop {
                                Bump(self.Value);
                            }
                        }
                        """,
                        "/virtual/Lib.stark"
                    )
                ])));

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Lib.Bump" });
    }

    [Fact]
    public void EnumPayloadDropsLowerThroughActiveTagDispatch()
    {
        var result = Compile(
            """
            module Demo

            static mut i32 Counter = 0;

            fn void Bump(i32 value) {
                Counter = Counter + value;
                return;
            }

            struct Resource {
                i32 Value;

                drop {
                    Bump(self.Value);
                }
            }

            enum Token {
                End,
                Text(Resource),
            }

            fn void Run() {
                stack Token token = Token.Text(new Resource() { Value = 4 });
                return;
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.Contains(function.Blocks, static block => block.Label.Contains("enum_drop_", StringComparison.Ordinal));
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Bump" });
        Assert.Contains(
            statements,
            static statement => statement.Kind == MidLevelIrStatementKind.StorageDead && statement.TargetName == "token");
    }

    private CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        using var logScope = CompilerLogOutput.Push(new TestOutputWriter(_output), DiagnosticSeverity.Info);
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static MidLevelIrModule GetMir(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        return mir;
    }

    private sealed class TestOutputWriter(ITestOutputHelper output) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            output.WriteLine(value ?? string.Empty);
        }
    }
}
