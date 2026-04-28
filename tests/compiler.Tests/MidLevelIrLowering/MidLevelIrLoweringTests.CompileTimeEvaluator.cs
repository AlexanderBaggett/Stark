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

            fn i32[-2147483648 2147483647] Run() {
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
    public void ConstantLawCallsFoldToMirConstants()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[-2147483648 2147483647] Adjust(i32[-2147483648 2147483647] value) {
                stack mut i32[-2147483648 2147483647] current = value;
                if (current < 10) {
                    current = current + 3;
                }

                return current;
            }

            fn i32[-2147483648 2147483647] Run() {
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
            finite law i32[-2147483648 2147483647] Adjust(i32[-2147483648 2147483647] value) {
                return value + 3;
            }

            fn i32[-2147483648 2147483647] Run() {
                return Adjust(4);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var block = Assert.Single(function.Blocks);

        Assert.Contains(block.Statements, static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Adjust" });
    }

    [Fact]
    public void FixedArrayArithmeticIndexStillLowersToAggregateIndexOperations()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
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
