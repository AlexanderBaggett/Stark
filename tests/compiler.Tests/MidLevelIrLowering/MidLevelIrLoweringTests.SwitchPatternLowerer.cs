using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    [Fact]
    public void LiteralSwitchLowersToMirSwitchTerminator()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(i32[min max] value) {
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
    public void SwitchSectionsCanAssignAndBreakToSharedExit()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value) {
                stack mut i32[min max] result = 0;
                switch (value) {
                    case 1:
                        result = 10;
                        break;
                    case 2:
                        result = 20;
                        break;
                    default:
                        result = 30;
                        break;
                }

                return result;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        var exitBlock = Assert.Single(function.Blocks, block => block.Label.Contains("switch_exit", StringComparison.Ordinal));
        var caseBlocks = function.Blocks
            .Where(block => block.Label.Contains("switch_case_", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, caseBlocks.Length);
        Assert.All(
            caseBlocks,
            block =>
            {
                Assert.Equal(MidLevelIrTerminatorKind.Goto, block.Terminator.Kind);
                Assert.Single(block.Terminator.Targets);
                Assert.Equal(exitBlock.Id, block.Terminator.Targets[0]);
            });
    }

    [Fact]
    public void GuardedSwitchLowersToBranchBasedCfg()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(i32[min max] value, bool allow) {
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

            unsafe finite law i32[min max] Run(i32[min max] value, bool allow) {
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

            unsafe finite law i32[min max] Run(i32[min max] value, bool allow) {
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

            unsafe finite law i32[min max] Run(i32[min max] value, bool allow) {
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

            unsafe finite law i32[min max] Run(i32[min max] value, bool allow) {
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
        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Statements),
            statement => statement.Kind == MidLevelIrStatementKind.Assign && statement.TargetName == "capture");

        var captureBlock = Assert.Single(
            function.Blocks,
            static block => block.Statements.Any(static statement =>
                statement.Kind == MidLevelIrStatementKind.Assign
                && statement.TargetName == "capture"));
        Assert.Contains("switch_bind", captureBlock.Label, StringComparison.Ordinal);
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement =>
                statement.Kind == MidLevelIrStatementKind.Assign
                && statement.TargetName == "capture"));
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
    }

    [Fact]
    public void AggregateSwitchPatternBindsScalarFieldsAfterPatternSelection()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right) { }

            unsafe finite law i32[min max] Run(Pair value) {
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
    public void AggregateWholeValueSwitchPatternBindsMatchLocalAfterPatternSelection()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right) { }

            unsafe finite law i32[min max] Run(Pair value) {
                switch (value) {
                    case Pair capture:
                        return capture.Right;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, static local => local.Name == "capture");
        var captureBlock = Assert.Single(
            function.Blocks,
            static block => block.Label.Contains("switch_agg_match", StringComparison.Ordinal)
                && block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_agg_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.Contains("switch_agg_match", captureBlock.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedAggregateWholeValueSwitchPatternBindsMatchLocalAfterPatternSelection()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right) { }
            record Outer(Pair Values, i32[min max] Tail) { }

            unsafe finite law i32[min max] Run(Outer value) {
                switch (value) {
                    case Outer(Pair capture, var tail):
                        return tail;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, static local => local.Name == "capture");
        var captureBlock = Assert.Single(
            function.Blocks,
            static block => block.Label.Contains("switch_agg_match", StringComparison.Ordinal)
                && block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_agg_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.Contains("switch_agg_match", captureBlock.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumWholeValueSwitchPatternBindsMatchLocalAfterTagSelection()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                Empty,
                Pair(i32[min max], i32[min max]),
            }

            unsafe fn i32[min max] Run(Token value) {
                switch (value) {
                    case Token.Pair capture:
                        return 1;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, static local => local.Name == "capture");
        var captureBlock = Assert.Single(
            function.Blocks,
            static block => block.Label.Contains("switch_agg_match", StringComparison.Ordinal)
                && block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.DoesNotContain(
            function.Blocks.Where(static block => block.Label.Contains("switch_test", StringComparison.Ordinal)),
            static block => block.Statements.Any(static statement => statement.TargetName == "capture"));
        Assert.Contains("switch_agg_match", captureBlock.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedAggregateSwitchPatternBindsScalarLeavesAfterPatternSelection()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right) { }
            record Outer(Pair Values, i32[min max] Tail) { }

            unsafe finite law i32[min max] Run(Outer value) {
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
    public void EnumSwitchPatternsLowerToTagTestsAndActivePayloadExtractions()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32[min max]),
                Move { X: i32[min max], Y: i32[min max] },
            }

            unsafe fn i32[min max] Run(Token token) {
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
                Err(i32[min max]),
            }

            unsafe fn Status Next() {
                return Status.Ok;
            }

            unsafe fn i32[min max] Run() {
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
            statements.Count(static statement => IsDirectCallStatement(statement, "Next")));
    }

    [Fact]
    public void TextLiteralSwitchLowersToBranchBasedComparisonTree()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(ascii value, bool allow) {
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

            unsafe fn i32[min max] Run(ascii value) {
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

            unsafe fn i32[min max] Run(unicode value) {
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
    public void FloatLiteralSwitchLowersToBranchBasedCfg()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(f32 value, bool allow) {
                switch (value) {
                    case 1.5:
                        return 1;
                    case 2.5 when allow:
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
        Assert.True(function.Blocks.Count(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch) >= 2);
    }

    [Fact]
    public void RawPointerNullSwitchLowersToBranchBasedCfg()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run(rawptr<i32[min max]> value) {
                switch (value) {
                    case null:
                        return 1;
                    default:
                        return 2;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.DoesNotContain(function.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Equal });
    }

    [Fact]
    public void AggregateSwitchPatternsCanMatchAndCaptureTextLeaves()
    {
        var result = Compile(
            """
            module Demo

            record Label(ascii Tag, unicode Word) { }

            unsafe fn i32[min max] Run(Label value) {
                switch (value) {
                    case Label("ab", var word):
                        return word == (unicode)"cat" ? 7 : 3;
                    default:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, static local => local.Name == "word");
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Tag" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Word" });
        Assert.Contains(function.Blocks, block => block.Label.Contains("textcmp_byte_0", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, static block => block.Statements.Any(static statement => statement.TargetName == "word"));
    }

    [Fact]
    public void EnumSwitchPatternsCanMatchAndCaptureTextLeaves()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                Empty,
                Text { Tag: ascii, Word: unicode },
            }

            unsafe fn i32[min max] Run(Token value) {
                switch (value) {
                    case Token.Text { Tag: "ab", Word: var word }:
                        return word == (unicode)"cat" ? 7 : 3;
                    case Token.Empty:
                        return 0;
                }
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, static local => local.Name == "word");
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "$tag" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "$Text_Tag" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "$Text_Word" });
        Assert.Contains(function.Blocks, static block => block.Label.Contains("switch_enum_match", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("textcmp_byte_0", StringComparison.Ordinal));
    }
}
