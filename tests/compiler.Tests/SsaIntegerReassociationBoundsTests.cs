using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaIntegerReassociationBoundsTests
{
    [Fact]
    public void RepeatedTermsWithCancellationPreserveEveryDefinedByteInput()
    {
        var type = StarkTypeSymbols.Integer(8);
        var input = Module(type, [new("seed", type), new("value", type)], [
            Add("first", Ref("arg_seed", type), Ref("arg_value", type), type),
            Add("result", Ref("first", type), Ref("arg_value", type), type)
        ]);
        var output = new SsaIntegerArithmeticFolder().Optimize(input);
        for (var seed = -128; seed <= 127; seed++)
        for (var value = -128; value <= 127; value++)
        {
            var first = seed + value;
            var expected = first + value;
            if (first < -128 || first > 127 || expected < -128 || expected > 127) continue;
            Assert.Equal(expected, Evaluate(output, new() { ["arg_seed"] = seed, ["arg_value"] = value }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeExpressionRepeatedTermsStillFold(bool wrapping)
    {
        var type = StarkTypeSymbols.Integer(8);
        var op = wrapping ? SsaBinaryOperator.WrappingAdd : SsaBinaryOperator.Add;
        var input = Module(type, [new("value", type)], [
            Add("first", Ref("arg_value", type), Ref("arg_value", type), type, op),
            Add("result", Ref("first", type), Ref("arg_value", type), type, op)
        ]);
        var output = new SsaIntegerArithmeticFolder().Optimize(input);
        Assert.Contains(output.Functions[0].Blocks[0].Instructions.OfType<SsaValueInstruction>(),
            i => i.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Multiply or SsaBinaryOperator.WrappingMultiply });
        for (var value = -42; value <= 42; value++)
            Assert.Equal(3 * value, Evaluate(output, new() { ["arg_value"] = value }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedConvertedOperandsRetainSafeCoefficientFolding(bool computed)
    {
        var type = StarkTypeSymbols.Integer(64);
        var small = StarkTypeSymbols.Integer(8, -10, 10);
        var input = Module(type, [new("seed", type), new("small", small)], [
            Add("partial", Ref("arg_small", small), Ref("arg_small", small), small),
            new SsaValueInstruction("value", new SsaConvertRValue(Ref(computed ? "partial" : "arg_small", small), type, "widen")),
            Add("first", Ref("arg_seed", type), Ref("value", type), type),
            Add("result", Ref("first", type), Ref("value", type), type)
        ]);
        var output = new SsaIntegerArithmeticFolder().Optimize(input);
        Assert.Contains(output.Functions[0].Blocks[0].Instructions.OfType<SsaValueInstruction>(),
            i => i.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Multiply });
        Assert.Equal(new BigInteger(long.MinValue) + (computed ? 40 : 20),
            Evaluate(output, new() { ["arg_seed"] = long.MinValue, ["arg_small"] = 10 }));
    }

    [Fact]
    public void ComputedOperandDoesNotInheritItsInputsNarrowRange()
    {
        var type = StarkTypeSymbols.Integer(8);
        var small = StarkTypeSymbols.Integer(8, -63, 0);
        var input = Module(type, [new("seed", type), new("small", small)], [
            Add("partial", Ref("arg_small", small), Ref("arg_small", small), small),
            new SsaValueInstruction("value", new SsaConvertRValue(Ref("partial", small), type, "widen range")),
            Add("first", Ref("arg_seed", type), Ref("value", type), type),
            Add("result", Ref("first", type), Ref("value", type), type)
        ]);
        var output = new SsaIntegerArithmeticFolder().Optimize(input);
        Assert.Equal(-125, Evaluate(output, new() { ["arg_seed"] = 127, ["arg_small"] = -63 }));
    }

    [Fact]
    public void AProvedCoefficientDoesNotAuthorizeAnOverflowingReassociatedPrefix()
    {
        var type = StarkTypeSymbols.Integer(64);
        var small = StarkTypeSymbols.Integer(8, -1, 1);
        var input = Module(type, [new("left", type), new("right", type), new("adjust", small)], [
            new SsaValueInstruction("adjust", new SsaConvertRValue(Ref("arg_adjust", small), type, "widen")),
            Add("inner", Ref("arg_right", type), Ref("adjust", type), type),
            Add("first", Ref("arg_left", type), Ref("inner", type), type),
            Add("result", Ref("first", type), Ref("adjust", type), type)
        ]);
        var output = new SsaIntegerArithmeticFolder().Optimize(input);
        Assert.Equal(new BigInteger(long.MaxValue) - 1, Evaluate(output, new() {
            ["arg_left"] = long.MaxValue, ["arg_right"] = 1, ["arg_adjust"] = -1
        }));
        Assert.Equal(new BigInteger(long.MinValue) + 1, Evaluate(output, new() {
            ["arg_left"] = long.MinValue, ["arg_right"] = -1, ["arg_adjust"] = 1
        }));
    }

    private static SsaIrModule Module(StarkTypeSymbol type, TypedParameterSymbol[] parameters, SsaInstruction[] instructions) =>
        new("Bounds", [new("Run", type, parameters, true, true, 0,
            [new(0, "entry", [], instructions, new(SsaTerminatorKind.Return, [], Value: Ref("result", type)))])]);

    private static SsaValueReference Ref(string name, StarkTypeSymbol type) => new(name, type);
    private static SsaValueInstruction Add(string name, SsaValue left, SsaValue right, StarkTypeSymbol type,
        SsaBinaryOperator op = SsaBinaryOperator.Add) => new(name, new SsaBinaryRValue(op, left, right, type, "sum"));

    private static BigInteger Evaluate(SsaIrModule module, Dictionary<string, BigInteger> values)
    {
        BigInteger Get(SsaValue v) => v is SsaIntegerConstant c ? c.Value : values[((SsaValueReference)v).Name];
        foreach (var instruction in module.Functions[0].Blocks[0].Instructions.OfType<SsaValueInstruction>())
        {
            var value = instruction.Value switch {
                SsaUseRValue use => Get(use.Value),
                SsaConvertRValue convert => Get(convert.Operand),
                SsaBinaryRValue { Operator: SsaBinaryOperator.Add or SsaBinaryOperator.WrappingAdd } b => Get(b.Left) + Get(b.Right),
                SsaBinaryRValue { Operator: SsaBinaryOperator.Subtract } b => Get(b.Left) - Get(b.Right),
                SsaBinaryRValue { Operator: SsaBinaryOperator.Multiply or SsaBinaryOperator.WrappingMultiply } b => Get(b.Left) * Get(b.Right),
                _ => throw new InvalidOperationException("Unexpected expression")
            };
            Assert.True(StarkTypeSymbols.TryGetEffectiveIntegerBounds(StarkTypeSymbols.Integer(instruction.Value.Type.BitWidth!.Value, isUnsigned: instruction.Value.Type.IsUnsigned), out var min, out var max)
                && value >= min && value <= max, "A defined source execution acquired an overflowing intermediate.");
            values[instruction.ResultName] = value;
        }
        return values["result"];
    }
}
