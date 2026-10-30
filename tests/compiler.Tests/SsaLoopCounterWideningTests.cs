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

    [Fact]
    public void LeavesCounterNarrowForEmptyLoops()
    {
        var input = Loop(0);
        Assert.Same(input, new SsaLoopCounterWidening().Optimize(input));
    }

    [Fact]
    public void PreservesTheSourceWidthOfAnObservedExitValue()
    {
        var input = Loop(300000, escape: true);
        var function = Assert.Single(new SsaLoopCounterWidening().Optimize(input).Functions);
        Assert.Equal(24, function.ReturnType.BitWidth);
        Assert.Equal(24, function.Blocks.Single(block => block.Id == 3).Terminator.Value!.Type.BitWidth);
        Assert.Equal(32, Assert.Single(function.Blocks.Single(block => block.Id == 1).Phis).Type.BitWidth);
    }

    [Fact]
    public void WidensAcrossBranchesAndUnrelatedStorageKeepingNarrowDataUses()
    {
        var module = Loop(300000);
        var function = Assert.Single(module.Functions);
        var header = function.Blocks.Single(block => block.Id == 1);
        var body = function.Blocks.Single(block => block.Id == 2);
        var narrow = header.Phis[0].Type;
        var blocks = function.Blocks.Select(block => block.Id switch
        {
            0 => block with { Instructions = [new SsaAllocateLocalInstruction("saved", narrow)] },
            1 => block with { Phis = [header.Phis[0] with { Incomings =
                [header.Phis[0].Incomings[0], new SsaPhiIncoming(4, header.Phis[0].Incomings[1].Value)] }] },
            2 => block with { Instructions = [new SsaStoreLocalInstruction("saved", narrow, new SsaValueReference("counter", narrow))],
                Terminator = new SsaTerminator(SsaTerminatorKind.Branch, [4, 5], Condition: new SsaBoolConstant(true)) },
            _ => block
        }).Concat(new[]
        {
            new SsaBasicBlock(4, "latch", [], body.Instructions, new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(5, "early-exit", [], [], new SsaTerminator(SsaTerminatorKind.Return, [], Value: new SsaIntegerConstant(2, function.ReturnType)))
        }).ToArray();
        var output = Assert.Single(new SsaLoopCounterWidening().Optimize(module with { Functions = [function with { Blocks = blocks }] }).Functions);
        var phi = Assert.Single(output.Blocks.Single(block => block.Id == 1).Phis);
        Assert.Equal(32, phi.Type.BitWidth);
        Assert.Equal(300000, phi.Type.RangeMax);
        var store = Assert.IsType<SsaStoreLocalInstruction>(Assert.Single(output.Blocks.Single(block => block.Id == 2).Instructions));
        Assert.Equal(24, store.LocalType.BitWidth);
        Assert.Equal(24, store.Value.Type.BitWidth);
    }

    [Fact]
    public void DoesNotWidenWhenTheFailedGuardCanStillExecuteTheIncrement()
    {
        var module = Loop(300000);
        var function = Assert.Single(module.Functions);
        var input = module with { Functions = [function with { Blocks = function.Blocks.Select(block => block.Id == 3
            ? block with { Terminator = new SsaTerminator(SsaTerminatorKind.Goto, [2]) } : block).ToArray() }] };
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
        return new SsaIrModule("Demo", [new SsaFunction("Run", escape ? narrow : result, [], true, true, 0, blocks)]);
    }
}
