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

    [Theory]
    [InlineData(SsaBinaryOperator.Divide, 7, false)]
    [InlineData(SsaBinaryOperator.Modulo, 17, false)]
    [InlineData(SsaBinaryOperator.Modulo, 1, false)]
    [InlineData(SsaBinaryOperator.Modulo, 400001, true)]
    public void ComputesProvenCounterArithmeticNativelyAndPreservesItsResultType(
        SsaBinaryOperator operation, int divisor, bool signedAlias)
    {
        var input = ArithmeticLoop(operation, divisor, signedAlias ? "signed" : "direct");
        var function = Assert.Single(new SsaLoopCounterWidening().Optimize(input).Functions);
        var body = function.Blocks.Single(b => b.Id == 2);
        var original = body.Instructions.OfType<SsaValueInstruction>().Single(i => i.ResultName == "result");
        Assert.Equal(24, original.Value.Type.BitWidth);
        var converted = Assert.IsType<SsaConvertRValue>(original.Value);
        Assert.Equal(32, converted.Operand.Type.BitWidth);
        Assert.Equal(0, converted.Operand.Type.RangeMin);
        Assert.Equal(operation == SsaBinaryOperator.Divide ? 300000 / divisor : Math.Min(300000, divisor - 1),
            converted.Operand.Type.RangeMax);
        var returned = function.Blocks.Single(b => b.Id == 3).Terminator.Value!;
        Assert.Equal(24, returned.Type.BitWidth);
    }

    [Fact]
    public void SharesRemainderInductionsAndPreservesTheirBoundsThroughAnalysis()
    {
        var input = ArithmeticLoop(SsaBinaryOperator.Modulo, 17, "signed");
        var function = input.Functions[0];
        var body = function.Blocks.Single(b => b.Id == 2);
        var original = body.Instructions.OfType<SsaValueInstruction>().Single(i => i.ResultName == "result");
        var binary = Assert.IsType<SsaBinaryRValue>(original.Value);
        var duplicate = original with { ResultName = "duplicate" };
        var different = original with { ResultName = "different", Value = binary with
            { Right = new SsaIntegerConstant(31, binary.Right.Type) } };
        input = input with { Functions = [function with { Blocks = function.Blocks.Select(b => b.Id == 2
            ? b with { Instructions = b.Instructions.Concat([duplicate, different]).ToArray() } : b).ToArray() }] };
        var output = new SsaLoopCounterWidening().Optimize(input);
        var phis = output.Functions[0].Blocks.Single(b => b.Id == 1).Phis;
        Assert.Equal(3, phis.Count);
        var facts = new SsaValueFactAnalyzer().Analyze(output).Functions["Run"].Values;
        foreach (var max in new[] { 16, 30 })
        {
            var phi = Assert.Single(phis, p => p.Type.RangeMax == max);
            Assert.Equal(0, facts[phi.ResultName].IntegerRange!.Min);
            Assert.Equal(max, facts[phi.ResultName].IntegerRange!.Max);
        }
    }

    [Fact]
    public void ConditionalRemaindersDoNotAddPerIterationState()
    {
        var input = ArithmeticLoop(SsaBinaryOperator.Modulo, 17, "direct");
        var function = input.Functions[0];
        var header = function.Blocks[1];
        var body = function.Blocks[2];
        var counter = new SsaValueReference("counter", header.Phis[0].Type);
        var conditional = body.Instructions.OfType<SsaValueInstruction>().Single(i => i.ResultName == "result");
        var update = body.Instructions.OfType<SsaValueInstruction>().Single(i => i.ResultName == "next");
        var blocks = function.Blocks.Select(b => b.Id switch
        {
            1 => b with { Phis = [header.Phis[0] with { Incomings =
                [header.Phis[0].Incomings[0], new SsaPhiIncoming(4, header.Phis[0].Incomings[1].Value)] }] },
            2 => b with
            {
                Instructions = [.. body.Instructions.Where(i => !ReferenceEquals(i, conditional) && !ReferenceEquals(i, update)),
                    new SsaValueInstruction("first", new SsaBinaryRValue(SsaBinaryOperator.Equal, counter,
                        new SsaIntegerConstant(0, counter.Type), StarkTypeSymbols.Bool, "first iteration"))],
                Terminator = new SsaTerminator(SsaTerminatorKind.Branch, [5, 4],
                    Condition: new SsaValueReference("first", StarkTypeSymbols.Bool))
            },
            _ => b
        }).Concat(new[]
        {
            new SsaBasicBlock(4, "latch", [], [update], new SsaTerminator(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(5, "conditional", [], [conditional], new SsaTerminator(SsaTerminatorKind.Goto, [4]))
        }).ToArray();
        var output = new SsaLoopCounterWidening().Optimize(input with { Functions = [function with { Blocks = blocks }] });
        Assert.Single(output.Functions[0].Blocks.Single(b => b.Id == 1).Phis);
        var arithmetic = Assert.Single(output.Functions[0].Blocks.Single(b => b.Id == 5).Instructions
            .OfType<SsaValueInstruction>(), i => i.Value is SsaBinaryRValue);
        Assert.Equal(32, arithmetic.Value.Type.BitWidth);
    }

    [Fact]
    public void PowerOfTwoRemaindersDoNotAddPerIterationState()
    {
        var output = new SsaLoopCounterWidening().Optimize(ArithmeticLoop(SsaBinaryOperator.Modulo, 2048, "direct"));
        Assert.Single(output.Functions[0].Blocks.Single(b => b.Id == 1).Phis);
        var arithmetic = Assert.Single(output.Functions[0].Blocks.Single(b => b.Id == 2).Instructions
            .OfType<SsaValueInstruction>(), i => i.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Modulo });
        Assert.Equal(32, arithmetic.Value.Type.BitWidth);
    }

    [Theory]
    [InlineData(SsaBinaryOperator.Modulo, 0, "direct")]
    [InlineData(SsaBinaryOperator.Divide, -3, "signed")]
    [InlineData(SsaBinaryOperator.Modulo, 7, "lossy")]
    [InlineData(SsaBinaryOperator.Modulo, 7, "shifted")]
    [InlineData(SsaBinaryOperator.WrappingAdd, 7, "direct")]
    public void LeavesUnsupportedArithmeticAtSourceWidth(SsaBinaryOperator operation, int divisor, string alias)
    {
        var input = ArithmeticLoop(operation, divisor, alias);
        var function = Assert.Single(new SsaLoopCounterWidening().Optimize(input).Functions);
        var result = function.Blocks.Single(b => b.Id == 2).Instructions.OfType<SsaValueInstruction>()
            .Single(i => i.ResultName == "result");
        Assert.IsType<SsaBinaryRValue>(result.Value);
        Assert.Equal(24, result.Value.Type.BitWidth);
    }

    private static SsaIrModule ArithmeticLoop(SsaBinaryOperator operation, int divisor, string alias)
    {
        var module = Loop(300000);
        var function = module.Functions[0];
        var type = alias == "direct" ? function.Blocks[1].Phis[0].Type : StarkTypeSymbols.Integer(24);
        var counter = new SsaValueReference("counter", function.Blocks[1].Phis[0].Type);
        var instructions = new List<SsaInstruction>();
        SsaValue operand = counter;
        if (alias != "direct")
        {
            if (alias == "lossy")
            {
                var byteType = StarkTypeSymbols.Integer(8, isUnsigned: true);
                instructions.Add(new SsaValueInstruction("byte", new SsaConvertRValue(counter, byteType, "truncate")));
                operand = new SsaValueReference("byte", byteType);
            }
            if (alias == "shifted")
            {
                instructions.Add(new SsaValueInstruction("signedCounter", new SsaConvertRValue(counter, type, "convert")));
                operand = new SsaValueReference("signedCounter", type);
            }
            instructions.Add(new SsaValueInstruction("alias", alias == "shifted"
                ? new SsaBinaryRValue(SsaBinaryOperator.Subtract, operand, new SsaIntegerConstant(1, type), type, "subtract")
                : new SsaConvertRValue(operand, type, "convert")));
            operand = new SsaValueReference("alias", type);
        }
        // Copy aliases are checked independently from conversions.
        instructions.Add(new SsaValueInstruction("copy", new SsaUseRValue(operand)));
        instructions.Add(new SsaValueInstruction("result", new SsaBinaryRValue(operation,
            new SsaValueReference("copy", type), new SsaIntegerConstant(divisor, type), type, "arithmetic")));
        instructions.AddRange(function.Blocks[2].Instructions);
        return module with { Functions = [function with { ReturnType = counter.Type, Blocks = function.Blocks.Select(b => b.Id switch
        {
            2 => b with { Instructions = instructions },
            3 => b with { Terminator = b.Terminator with { Value = counter } },
            _ => b
        }).ToArray() }] };
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
