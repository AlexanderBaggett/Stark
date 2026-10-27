using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaLoopCounterWideningTests
{
    [Theory]
    [InlineData(300000)]
    [InlineData(8388607)]
    public void WidensPrivateBoundedCounterWithoutChangingOtherValues(int limit)
    {
        var input = Loop(limit);
        var result = new SsaLoopCounterWidening().Optimize(input);
        var header = Assert.Single(result.Functions).Blocks.Single(block => block.Id == 1);
        Assert.Equal(32, Assert.Single(header.Phis).Type.BitWidth);
        Assert.Equal(24, Assert.Single(input.Functions).Blocks.Single(block => block.Id == 1).Phis[0].Type.BitWidth);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(300000, true)]
    public void LeavesCounterNarrowForEmptyOrObservableCounters(int limit, bool escape)
    {
        var input = Loop(limit, escape);
        Assert.Same(input, new SsaLoopCounterWidening().Optimize(input));
    }

    [Fact]
    public void LeavesCounterNarrowWhenLimitExceedsItsDeclaredRange()
    {
        var input = Loop(300001, counterMax: 300000);
        Assert.Same(input, new SsaLoopCounterWidening().Optimize(input));
    }

    private static SsaIrModule Loop(int limit, bool escape = false, int? counterMax = null)
    {
        var narrow = StarkTypeSymbols.Integer(24, 0, counterMax ?? limit, isUnsigned: true);
        var signed = StarkTypeSymbols.Integer(24);
        var result = StarkTypeSymbols.Integer(32);
        var counter = new SsaValueReference("counter", narrow);
        var converted = new SsaValueReference("converted", signed);
        var next = new SsaValueReference("next", narrow);
        var condition = new SsaValueReference("condition", StarkTypeSymbols.Bool);
        var blocks = new[]
        {
            new SsaBasicBlock(0, "entry", [], [], new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(1, "condition", [new SsaPhi("counter", "counter", narrow,
                [new SsaPhiIncoming(0, new SsaIntegerConstant(0, narrow)), new SsaPhiIncoming(2, next)])],
                [new SsaValueInstruction("converted", new SsaConvertRValue(counter, signed, "convert")),
                 new SsaValueInstruction("condition", new SsaBinaryRValue(SsaBinaryOperator.LessThan, converted, new SsaIntegerConstant(limit, signed), StarkTypeSymbols.Bool, "compare"))],
                new SsaTerminator(SsaTerminatorKind.Branch, [2, 3], Condition: condition)),
            new SsaBasicBlock(2, "body", [],
                [new SsaValueInstruction("next", new SsaBinaryRValue(SsaBinaryOperator.Add, counter, new SsaIntegerConstant(1, narrow), narrow, "add"))],
                new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(3, "exit", [], [], new SsaTerminator(SsaTerminatorKind.Return, [],
                Value: escape ? counter : new SsaIntegerConstant(0, result)))
        };
        return new SsaIrModule("Demo", [new SsaFunction("Run", result, [], true, true, 0, blocks)]);
    }
}
