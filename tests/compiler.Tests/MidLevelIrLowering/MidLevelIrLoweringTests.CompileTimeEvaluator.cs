using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    [Fact]
    public void PureConstantArithmeticReturnsFoldedMirConstant()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                return (1 + 2) * 3;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Statements);
        var returnValue = Assert.IsType<MidLevelIrIntegerConstantOperand>(block.Terminator.Value);
        Assert.Equal(new BigInteger(9), returnValue.Value);
    }

    [Fact]
    public void ConstantIntegerExponentComparisonOperandFoldsBeforeMirWidthSelection()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn bool Run(u64[0 2 ** 63 - 1] count)
            {
                if (count > 2 ** 61 - 1)
                {
                    return true;
                }

                return false;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.DoesNotContain(
            statements,
            static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Exponent });
        var comparison = Assert.Single(
            statements,
            static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.GreaterThan });
        var greaterThan = Assert.IsType<MidLevelIrBinaryRValue>(comparison.Value);
        var right = Assert.IsType<MidLevelIrIntegerConstantOperand>(greaterThan.Right);
        Assert.Equal(BigInteger.Pow(new BigInteger(2), 61) - BigInteger.One, right.Value);
        Assert.Equal(64, right.Type.BitWidth);
    }

    [Fact]
    public void ConstantLawCallsFoldToMirConstants()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Adjust(i32[min max] value)
            {
                stack mut i32[min max] current = value;
                if (current < 10)
                {
                    current = current + 3;
                }

                return current;
            }

            unsafe fn i32[min max] Run()
            {
                return Adjust(4);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var block = Assert.Single(function.Blocks);

        Assert.Empty(block.Statements);
        var returnValue = Assert.IsType<MidLevelIrIntegerConstantOperand>(block.Terminator.Value);
        Assert.Equal(new BigInteger(7), returnValue.Value);
    }

    [Fact]
    public void BackendOpaqueLawCallsDoNotFoldToMirConstants()
    {
        var result = Compile(
            """
            module Demo

            [Backend(Opaque)]
            unsafe finite law i32[min max] Adjust(i32[min max] value)
            {
                return value + 3;
            }

            unsafe fn i32[min max] Run()
            {
                return Adjust(4);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var block = Assert.Single(function.Blocks);

        Assert.Contains(block.Statements, static statement => IsDirectCallStatement(statement, "Adjust"));
    }

    [Fact]
    public void FixedArrayArithmeticIndexStillLowersToAggregateIndexOperations()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Run()
            {
                stack i32[min max][3] values =
                {
                    1, 2, 3
                };
                return values[1 + 1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = Assert.Single(function.Blocks).Statements;

        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertIndexRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractIndexRValue { ElementIndex: 2 });
        Assert.DoesNotContain(statements, static statement => statement.Value is MidLevelIrBinaryRValue);
    }
}
