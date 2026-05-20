using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    [Fact]
    public void PayloadEnumConstructorCallsDoNotProduceIndirectCallProbeDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            enum Error {
                Unknown(i32[min max]),
            }

            enum Result<T> {
                Ok(T),
                Err(Error),
            }

            unsafe fn Result<bool> Ok() {
                return Result<bool>.Ok(true);
            }

            unsafe fn Result<bool> Bad() {
                return Result<bool>.Err(Error.Unknown(-1));
            }
            """,
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var mir = GetMir(result);
        Assert.Contains(mir.Functions, static function => function.Name == "Ok" && function.SupportsDirectCodeGeneration);
        Assert.Contains(mir.Functions, static function => function.Name == "Bad" && function.SupportsDirectCodeGeneration);
    }

    [Fact]
    public void IfStatementsLowerToBranchingBlocks()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run() {
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

            unsafe finite law void Run() {
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

            unsafe finite law void Run() {
                for willexit (stack mut i32[min max] i = 0; i < 4; i = i + 1) {
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
    public void EnumConstructorsLowerToDirectTagFieldInserts()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32[min max]),
                Move { X: i32[min max], Y: i32[min max] },
            }

            unsafe fn i32[min max] Run() {
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

        var constructions = statements
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrUseRValue>()
            .Select(static use => use.Operand)
            .OfType<MidLevelIrEnumConstructionOperand>()
            .Select(static construction => construction.Facts)
            .ToArray();
        Assert.Contains(constructions, static facts => facts.Variant.Name == "End" && facts.PayloadFields.Count == 0);
        Assert.Contains(constructions, static facts => facts.Variant.Name == "Integer" && facts.PayloadFields.Count == 1);
        Assert.Contains(constructions, static facts => facts.Variant.Name == "Move" && facts.PayloadFields.Count == 2);
    }

    [Fact]
    public void ComparisonChainsLowerToShortCircuitBlocksAndReuseSharedOperands()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Next() {
                return 1;
            }

            unsafe fn bool Run() {
                return 0 < Next() < 3;
            }
            """);

        Assert.True(result.Succeeded);
        var run = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");

        Assert.True(run.SupportsDirectCodeGeneration);
        Assert.Single(
            run.Blocks.SelectMany(static block => block.Statements),
            static statement => IsDirectCallStatement(statement, "Next"));
        Assert.Contains(run.Blocks, block => block.Label.Contains("cmpchain_next_1", StringComparison.Ordinal));
        Assert.Contains(run.Blocks, block => block.Label.Contains("cmpchain_false_0", StringComparison.Ordinal));
        Assert.Contains(run.Blocks, block => block.Label.Contains("cmpchain_join", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortCircuitOrLowersToMultipleBlocksAndDirectCodegen()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Run(bool left, bool right, bool fallback) {
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

            unsafe fn i32[min max] Run(bool flag) {
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
    public void VoidConditionalCallStatementsLowerToJoinableBlocks()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Reset(rawmutptr<i32[min max]> value, i32[min max] next) {
                *value = next;
                return;
            }

            unsafe fn void Run(bool flag, rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right) {
                flag ? Reset(left, 7) : Reset(right, 9);
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Blocks, block => block.Label.Contains("cond_true", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("cond_false", StringComparison.Ordinal));
        Assert.Contains(function.Blocks, block => block.Label.Contains("cond_join", StringComparison.Ordinal));

        var trueBlock = Assert.Single(function.Blocks, static block => block.Label.Contains("cond_true", StringComparison.Ordinal));
        var falseBlock = Assert.Single(function.Blocks, static block => block.Label.Contains("cond_false", StringComparison.Ordinal));
        Assert.Contains(trueBlock.Statements, static statement => statement.Kind == MidLevelIrStatementKind.Evaluate && IsDirectCallStatement(statement, "Reset"));
        Assert.Contains(falseBlock.Statements, static statement => statement.Kind == MidLevelIrStatementKind.Evaluate && IsDirectCallStatement(statement, "Reset"));
    }

    [Fact]
    public void VoidFunctionPointerCallsLowerAsStatementOnlyOperations()
    {
        var result = Compile(
            """
            module Demo

            noinline finite law void Target(i32[min max] value) {
                return;
            }

            unsafe fn void Run() {
                stack fnptr<fn void(i32[min max])> op = Target;
                op(1);
                return;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.Contains(statements, static statement => statement.Call is MidLevelIrIndirectCallStatementOperation);
        Assert.DoesNotContain(statements, static statement => statement.Value is MidLevelIrIndirectCallRValue { Type.Kind: StarkTypeKind.Void });
    }

    [Fact]
    public void LocalDeclarationsAndAssignmentsLowerToMirStatements()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run() {
                stack mut i32[min max] value = 1;
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
        Assert.Contains("_bin", binary.TargetName, StringComparison.Ordinal);
        Assert.Equal(MidLevelIrBinaryOperator.Add, ((MidLevelIrBinaryRValue)binary.Value!).Operator);
    }

    [Fact]
    public void SizeofAndAlignofLowerToConcreteIntegerConstants()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i64[min max] Run() {
                return sizeof(i32[min max]) + alignof(i64[min max]);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrBinaryRValue
            {
                Operator: MidLevelIrBinaryOperator.Add,
                Left: MidLevelIrIntegerConstantOperand { Value: var left },
                Right: MidLevelIrIntegerConstantOperand { Value: var right }
            }
            && left == 4
            && right == 8);
    }

    [Fact]
    public void HugeCompileTimeIntegerConversionMaterializesConcreteMirConstant()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law u1024[0 max] Run() {
                return (u1024[0 max])(2 ** 1024 - 1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var function = Assert.Single(GetMir(result).Functions);
        var returned = Assert.IsType<MidLevelIrIntegerConstantOperand>(function.Blocks[function.EntryBlockId].Terminator.Value);
        var expected = (BigInteger.One << 1024) - BigInteger.One;

        Assert.Equal(expected, returned.Value);
        Assert.Equal(1024, returned.Type.BitWidth);
        Assert.True(returned.Type.IsUnsigned);
        Assert.Equal(BigInteger.Zero, returned.Type.RangeMin);
        Assert.Equal(expected, returned.Type.RangeMax);
        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrConvertRValue);
    }

    [Fact]
    public void ConstantNarrowingConversionMaterializesWrappedMirConstant()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i8[min max] Run() {
                return (i8[min max])300;
            }
            """,
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var function = Assert.Single(GetMir(result).Functions);
        var returned = Assert.IsType<MidLevelIrIntegerConstantOperand>(function.Blocks[function.EntryBlockId].Terminator.Value);

        Assert.Equal(new BigInteger(44), returned.Value);
        Assert.Equal(8, returned.Type.BitWidth);
        Assert.False(returned.Type.IsUnsigned);
        Assert.DoesNotContain(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrConvertRValue);
    }

    [Fact]
    public void BitwiseXorExpressionLowersToMirBinaryOperation()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(i32[min max] left, i32[min max] right) {
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

            unsafe fn i32[min max] Run() {
                stack mut i32[min max] value = 6;
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

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] middle, i32[min max] right, i32[min max] mask) {
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

            unsafe fn i32[min max] Run(i32[min max] left, i32[min max] right) {
                stack mut i32[min max] wrapped = left;
                wrapped +%= right;
                stack i32[min max] wrapProduct = -%wrapped *% 2;

                stack mut i32[min max] saturated = left;
                saturated +|= right;
                saturated *|= 2;
                stack i32[min max] bounded = saturated -| 3;

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

            unsafe finite law f32 Run(f32 left, f32 right) {
                return left ** right;
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
    public void IntegerExponentExpressionLowersToMirBinaryOperation()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(i32[min max] left, i32[min max] right) {
                return left ** right;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var binary = Assert.Single(
            function.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrBinaryRValue);

        Assert.True(function.SupportsDirectCodeGeneration);
        var exponent = Assert.IsType<MidLevelIrBinaryRValue>(binary.Value);
        Assert.Equal(MidLevelIrBinaryOperator.Exponent, exponent.Operator);
        Assert.Equal(StarkTypeKind.Integer, exponent.Type.Kind);
    }

    [Fact]
    public void FloatingPointArithmeticChainsLowerToFloatMirBinaryOperations()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f64 Run(f32 left, i32[min max] middle, f64 right, f32 divisor) {
                return left + middle * right / divisor - 1.0;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var binaries = function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrBinaryRValue>()
            .ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(binaries, static binary => binary.Operator == MidLevelIrBinaryOperator.Multiply && binary.Type.Kind == StarkTypeKind.Float);
        Assert.Contains(binaries, static binary => binary.Operator == MidLevelIrBinaryOperator.Divide && binary.Type.Kind == StarkTypeKind.Float);
        Assert.Contains(binaries, static binary => binary.Operator == MidLevelIrBinaryOperator.Add && binary.Type.Kind == StarkTypeKind.Float);
        Assert.Contains(binaries, static binary => binary.Operator == MidLevelIrBinaryOperator.Subtract && binary.Type.Kind == StarkTypeKind.Float);
    }

    [Fact]
    public void CharacterLiteralsLowerToMirStringConstants()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law ascii AsciiChar() {
                return 'a';
            }

            unsafe finite law unicode UnicodeChar() {
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

            unsafe finite law unicode Run() {
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
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run() {
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

        var construction = Assert.Single(statements
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrUseRValue>()
            .Select(static use => use.Operand)
            .OfType<MidLevelIrObjectConstructionOperand>());
        Assert.Equal(MidLevelIrObjectConstructionKind.Initializer, construction.Facts.Kind);
        Assert.Equal("Box", construction.Facts.TypeName);
        Assert.Single(construction.Facts.InitializerMembers, static member => member.FieldName == "Value" && member.FieldIndex == 0);
    }

    [Fact]
    public void PrimaryRecordConstructorArgumentsLowerInEvaluationOrder()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right) { }

            unsafe fn i32[min max] First() {
                return 1;
            }

            unsafe fn i32[min max] Second() {
                return 2;
            }

            unsafe fn i32[min max] Run() {
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

        var construction = Assert.Single(statements
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrUseRValue>()
            .Select(static use => use.Operand)
            .OfType<MidLevelIrObjectConstructionOperand>());
        Assert.Equal(MidLevelIrObjectConstructionKind.PrimaryConstructor, construction.Facts.Kind);
        Assert.NotNull(construction.Facts.Constructor);
        Assert.True(construction.Facts.Constructor!.IsPrimaryShape);
    }

    [Fact]
    public void ConstructorInitializerCombinationAppliesInitializerAfterConstructorFields()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left) {
                i32[min max] Right;
            }

            unsafe fn i32[min max] First() {
                return 1;
            }

            unsafe fn i32[min max] Override() {
                return 4;
            }

            unsafe fn i32[min max] Run() {
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
                i32[min max][2] Pair;
            }

            struct Outer {
                i32[min max] Score;
                Inner Node;
            }

            unsafe fn i32[min max] MakeScore() {
                return 9;
            }

            unsafe fn i32[min max] MakeLeft() {
                return 4;
            }

            unsafe fn i32[min max] MakeRight() {
                return 7;
            }

            unsafe fn i32[min max] Run() {
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
                unsafe fn i32[min max] Pick(Box box, i32[min max] value) {
                    return value;
                }
            }

            unsafe fn Box Make() {
                return new Box();
            }

            unsafe fn i32[min max] Next() {
                return 7;
            }

            unsafe fn i32[min max] Run() {
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
    public void TargetTypedObjectCreationLowersWithDestinationType()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            unsafe fn Box Make(i32[min max] value) {
                return new() { Value = value };
            }

            unsafe fn i32[min max] Run(i32[min max] value) {
                stack mut Box box = new() { Value = value };
                box = new() { Value = value + 1 };
                return box.Value + Make(value).Value;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var functions = GetMir(result).Functions.ToArray();
        var make = Assert.Single(functions, static function => function.Name == "Make");
        var run = Assert.Single(functions, static function => function.Name == "Run");

        Assert.True(make.SupportsDirectCodeGeneration);
        Assert.True(run.SupportsDirectCodeGeneration);
        Assert.Contains(
            make.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "Value" });
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Statements),
            static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "Value" });
    }

    [Fact]
    public void RegisterObjectCreationKeepsValueStyleLocalLowering()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run() {
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
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run() {
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
}
