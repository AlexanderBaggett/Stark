using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaCyclicArraySumTests
{
    [Theory]
    [InlineData(8, 1, 9)]
    [InlineData(16, 3, 17)]
    [InlineData(24, 5, 23)]
    [InlineData(32, 7, 28)]
    [InlineData(64, 4, 2)]
    public void DerivesExactSumsForArbitraryCellsPartialCyclesAndSignedSeeds(int width, int period, int count)
    {
        var input = Loop(width, period, count);
        var output = Fold(input);
        Assert.NotSame(input, output);
        var function = Assert.Single(output.Functions);
        Assert.DoesNotContain(function.Blocks, b => b.Phis.Count != 0);
        Assert.DoesNotContain(function.Blocks.SelectMany(b => b.Instructions), i => i is SsaStoreIndirectInstruction);
        for (var seed = -11; seed <= 13; seed++)
        {
            var cells = Enumerable.Range(0, period).Select(j => new BigInteger(j * 3 - 7)).ToArray();
            BigInteger expected = -19;
            for (var i = 0; i < count; i++)
            {
                var j = i % period;
                cells[j] += j + seed;
                expected += cells[j];
            }
            var values = Evaluate(function, seed);
            Assert.Equal(expected, values["sum"]);
            Assert.Equal(count, values["counter"]);
        }
    }

    [Fact]
    public void UsesAuthoritativeRangeFactsWithoutNarrowingTheStorageType()
    {
        var input = Loop(24, 3, 17, fullSeedRange: true);
        Assert.Same(input, Fold(input));
        var analyzer = new SsaValueFactAnalyzer();
        var facts = analyzer.Analyze(input);
        var function = facts.Functions["Kernel"];
        var values = new Dictionary<string, SsaValueFacts>(function.Values) {
            ["seed"] = function.Values["seed"] with {
                IntegerRangeKind = SsaFactLatticeKind.Known, IntegerRange = new(-11, 13)
            }
        };
        var bounded = facts with { Functions = new Dictionary<string, SsaFunctionFactModel> {
            ["Kernel"] = function with { Values = values }
        } };
        var output = new SsaCyclicArraySumOptimizer().Optimize(input, bounded);
        Assert.NotSame(input, output);
        Assert.Equal(64, Assert.Single(output.Functions).Parameters[0].Type.BitWidth);
        Assert.Equal(Evaluate(Assert.Single(Fold(Loop(24, 3, 17)).Functions), -11)["sum"],
            Evaluate(Assert.Single(output.Functions), -11)["sum"]);
    }

    [Fact]
    public void HoistsAnInvariantConversionWithoutChangingItsSourceWidth()
    {
        var input = Loop(8, 5, 23);
        var function = input.Functions[0];
        var convert = function.Blocks[0].Instructions.OfType<SsaValueInstruction>().Single(i => i.ResultName == "seed");
        input = input with { Functions = [function with { Blocks = function.Blocks.Select(b => b.Id switch {
            0 => b with { Instructions = b.Instructions.Where(i => i != convert).ToArray() },
            2 => b with { Instructions = new SsaInstruction[] { convert }.Concat(b.Instructions).ToArray() },
            _ => b
        }).ToArray() }] };
        var output = Fold(input);
        Assert.NotSame(input, output);
        Assert.Equal(Evaluate(Assert.Single(Fold(Loop(8, 5, 23)).Functions), -11)["sum"],
            Evaluate(Assert.Single(output.Functions), -11)["sum"]);
    }

    [Fact]
    public void LeavesEmptyOrDynamicallyBoundedLoopsToTheirExistingOptimizers()
    {
        var empty = Loop(8, 3, 0);
        Assert.Same(empty, Fold(empty));
        var input = Loop(8, 3, 17);
        var function = input.Functions[0];
        var counterType = function.Blocks[1].Phis[0].Type;
        input = input with { Functions = [function with {
            Parameters = function.Parameters.Concat(new[] { new TypedParameterSymbol("count", counterType) }).ToArray(),
            Blocks = function.Blocks.Select(b => b.Id == 1 ? b with {
                Instructions = b.Instructions.Select(i => i is SsaValueInstruction { Value: SsaBinaryRValue guard } v
                    ? v with { Value = guard with { Right = new SsaValueReference("arg_count", counterType) } } : i).ToArray()
            } : b).ToArray()
        }] };
        Assert.Same(input, Fold(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportsUnsignedElementsAndEitherCounterSignedness(bool signedCounter)
    {
        var input = Loop(16, 3, 17, initialCell: 7, initialSum: 0,
            unsignedElement: true, signedCounter: signedCounter);
        var output = Fold(input);
        Assert.NotSame(input, output);
        for (var seed = 0; seed <= 13; seed++)
        {
            long[] cells = [7, 7, 7];
            long expected = 0;
            for (var i = 0; i < 17; i++) { var j = i % 3; cells[j] += j + seed; expected += cells[j]; }
            Assert.Equal(expected, Evaluate(output.Functions[0], seed)["sum"]);
        }
    }

    [Theory]
    [InlineData("array-read")]
    [InlineData("byte-read")]
    [InlineData("escape")]
    [InlineData("extra-store")]
    [InlineData("early-exit")]
    [InlineData("wrapping")]
    [InlineData("saturating")]
    [InlineData("lossy-counter")]
    [InlineData("unknown-initializer")]
    [InlineData("heap")]
    public void RetainsOriginalLoopWhenTheProofDoesNotApply(string kind)
    {
        var input = Loop(24, 3, 17);
        var function = Assert.Single(input.Functions);
        var element = function.ReturnType;
        var array = StarkTypeSymbols.FixedArray(element, 3);
        var pointer = StarkTypeSymbols.RawPointer(array, true);
        var blocks = function.Blocks.Select(b => b with { Instructions = b.Instructions.ToArray() }).ToArray();
        var entry = blocks[0].Instructions.ToList();
        var body = blocks[2].Instructions.ToList();
        var exit = blocks[3].Instructions.ToList();
        switch (kind)
        {
            case "array-read":
                exit.Add(new SsaValueInstruction("observed", new SsaLoadLocalRValue("cells", array)));
                break;
            case "byte-read":
                exit.Add(new SsaValueInstruction("observedAddress", new SsaAddressOfLocalRValue("cells", array, pointer, "address")));
                exit.Add(new SsaValueInstruction("bytePointer", new SsaConvertRValue(new SsaValueReference("observedAddress", pointer),
                    StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), false), "bytes")));
                break;
            case "escape":
                entry.Insert(0, new SsaValueInstruction("escaped", new SsaAddressOfLocalRValue("cells", array, pointer, "escape")));
                break;
            case "extra-store":
                body.Add(new SsaStoreLocalInstruction("other", element, new SsaValueReference("updated", element)));
                break;
            case "early-exit":
                blocks[2] = blocks[2] with { Terminator = new(SsaTerminatorKind.Branch, [1, 3], Condition: new SsaBoolConstant(true)) };
                break;
            case "wrapping":
            case "saturating":
                body = body.Select(i => i is SsaValueInstruction { ResultName: "updated", Value: SsaBinaryRValue add } v
                    ? v with { Value = add with { Operator = kind == "wrapping" ? SsaBinaryOperator.WrappingAdd : SsaBinaryOperator.SaturatingAdd } }
                    : i).ToList();
                break;
            case "lossy-counter":
                var narrow = StarkTypeSymbols.Integer(8, isUnsigned: true);
                // 300 does not fit through this alias even though the modulo result does.
                input = Loop(24, 3, 300);
                function = input.Functions[0]; blocks = function.Blocks.ToArray();
                entry = blocks[0].Instructions.ToList(); exit = blocks[3].Instructions.ToList();
                body = blocks[2].Instructions.ToList();
                var counter = blocks[1].Phis[0];
                body.Insert(0, new SsaValueInstruction("truncated", new SsaConvertRValue(new SsaValueReference(counter.ResultName, counter.Type), narrow, "truncate")));
                body = body.Select(i => i is SsaValueInstruction { ResultName: "index", Value: SsaBinaryRValue modulo } v
                    ? v with { Value = modulo with { Left = new SsaValueReference("truncated", narrow) } } : i).ToList();
                break;
            case "unknown-initializer":
                entry = entry.Select(i => i is SsaValueInstruction { Value: SsaInsertIndexRValue insert } v
                    ? v with { Value = insert with { Value = new SsaValueReference("arg_seed", function.Parameters[0].Type) } } : i).ToList();
                break;
            case "heap":
                entry = entry.Select(i => i is SsaAllocateLocalInstruction a ? a with { StorageClass = "heap" } : i).ToList();
                break;
        }
        blocks[0] = blocks[0] with { Instructions = entry };
        blocks[2] = blocks[2] with { Instructions = body };
        blocks[3] = blocks[3] with { Instructions = exit };
        input = input with { Functions = [function with { Blocks = blocks }] };
        Assert.Same(input, Fold(input));
    }

    [Theory]
    // Even when a final sum would fit, intermediate cell/prefix overflow is insufficient proof.
    [InlineData(127, 1, 2)]
    [InlineData(100, 0, 2)]
    public void RejectsSourceIntermediateOrChecksumPrefixOverflow(int initialCell, int seed, int count)
    {
        var input = Loop(8, 1, count, elementWidth: 8, initialCell: initialCell, fixedSeed: seed, initialSum: 0);
        Assert.Same(input, Fold(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresSourceIntermediatesAndEveryPrefixToFitEvenWhenTheFinalSumFits(bool prefix)
    {
        // Mathematical final sums are respectively 126 and 53, both within i8.
        var input = prefix
            ? Loop(8, 2, 4, elementWidth: 8, initialCell: 100, fixedSeed: 0, initialSum: 50)
            : Loop(8, 2, 2, elementWidth: 8, initialCell: 127, fixedSeed: -1, initialSum: -127);
        if (prefix)
        {
            var function = input.Functions[0];
            input = input with { Functions = [function with { Blocks = function.Blocks.Select(b => b.Id == 0
                ? b with { Instructions = b.Instructions.Select(i => i is SsaValueInstruction
                    { ResultName: "initial1", Value: SsaInsertIndexRValue insert } v
                    ? v with { Value = insert with { Value = new SsaIntegerConstant(-100, function.ReturnType) } } : i).ToArray() }
                : b).ToArray() }] };
        }
        Assert.Same(input, Fold(input));
    }

    private static SsaIrModule Fold(SsaIrModule input) =>
        new SsaCyclicArraySumOptimizer().Optimize(input, new SsaValueFactAnalyzer().Analyze(input));

    private static Dictionary<string, BigInteger> Evaluate(SsaFunction function, int seed)
    {
        var values = new Dictionary<string, BigInteger> { ["arg_seed"] = seed, ["seed"] = seed };
        BigInteger Get(SsaValue v) => v is SsaIntegerConstant c ? c.Value : values[((SsaValueReference)v).Name];
        foreach (var instruction in function.Blocks.Single(b => b.Id == 1).Instructions.OfType<SsaValueInstruction>())
            values[instruction.ResultName] = instruction.Value switch {
                SsaUseRValue use => Get(use.Value),
                SsaConvertRValue convert => Get(convert.Operand),
                SsaBinaryRValue { Operator: SsaBinaryOperator.Multiply } b => Get(b.Left) * Get(b.Right),
                SsaBinaryRValue { Operator: SsaBinaryOperator.Add } b => Get(b.Left) + Get(b.Right),
                SsaBinaryRValue { Operator: SsaBinaryOperator.LessThan } b => Get(b.Left) < Get(b.Right) ? 1 : 0,
                _ => throw new InvalidOperationException("Unexpected folded arithmetic")
            };
        return values;
    }

    private static SsaIrModule Loop(int counterWidth, int period, int count, bool fullSeedRange = false,
        int elementWidth = 64, int? initialCell = null, int? fixedSeed = null, int initialSum = -19,
        bool unsignedElement = false, bool signedCounter = false)
    {
        var type = StarkTypeSymbols.Integer(elementWidth, isUnsigned: unsignedElement);
        var seedType = fullSeedRange ? type : StarkTypeSymbols.Integer(8, unsignedElement ? 0 : -11, 13, isUnsigned: unsignedElement);
        var array = StarkTypeSymbols.FixedArray(type, period);
        var pointer = StarkTypeSymbols.RawPointer(array, true);
        var elementPointer = StarkTypeSymbols.RawPointer(type, true);
        var counterType = StarkTypeSymbols.Integer(counterWidth, 0, count, isUnsigned: !signedCounter);
        SsaValueReference Ref(string name, StarkTypeSymbol? t = null) => new(name, t ?? type);
        SsaIntegerConstant C(int value, StarkTypeSymbol? t = null) => new(value, t ?? type);
        var entry = new List<SsaInstruction> { new SsaAllocateLocalInstruction("cells", array) };
        SsaValue initializer = new SsaZeroInitializerValue(array);
        for (var j = 0; j < period; j++)
        {
            var name = "initial" + j;
            entry.Add(new SsaValueInstruction(name, new SsaInsertIndexRValue(initializer, j,
                IndexedElementOperationFamily.FixedArrayElement, C(initialCell ?? j * 3 - 7), array, "insert")));
            initializer = Ref(name, array);
        }
        entry.Add(new SsaStoreLocalInstruction("cells", array, initializer));
        entry.Add(new SsaValueInstruction("seed", new SsaConvertRValue(Ref("arg_seed", seedType), type, "seed")));
        SsaValue seedValue = fixedSeed is int s ? C(s) : Ref("seed");
        var blocks = new[] {
            new SsaBasicBlock(0, "entry", [], entry, new(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(1, "header", [
                new SsaPhi("counter", "counter", counterType, [new(0, C(0, counterType)), new(2, Ref("next", counterType))]),
                new SsaPhi("sum", "sum", type, [new(0, C(initialSum)), new(2, Ref("sumNext"))])
            ], [new SsaValueInstruction("condition", new SsaBinaryRValue(SsaBinaryOperator.LessThan,
                Ref("counter", counterType), C(count, counterType), StarkTypeSymbols.Bool, "guard"))],
                new(SsaTerminatorKind.Branch, [2, 3], Condition: Ref("condition", StarkTypeSymbols.Bool))),
            new SsaBasicBlock(2, "body", [], [
                new SsaValueInstruction("index", new SsaBinaryRValue(SsaBinaryOperator.Modulo, Ref("counter", counterType), C(period, counterType), counterType, "index")),
                new SsaValueInstruction("wideIndex", new SsaConvertRValue(Ref("index", counterType), type, "index")),
                new SsaValueInstruction("root", new SsaAddressOfLocalRValue("cells", array, pointer, "address")),
                new SsaValueInstruction("address", new SsaElementAddressRValue(Ref("root", pointer), array, Ref("index", counterType), null, elementPointer, "element")),
                new SsaValueInstruction("old", new SsaLoadIndirectRValue(Ref("address", elementPointer), type, "old")),
                new SsaValueInstruction("indexed", new SsaBinaryRValue(SsaBinaryOperator.Add, Ref("old"), Ref("wideIndex"), type, "index delta")),
                new SsaValueInstruction("updated", new SsaBinaryRValue(SsaBinaryOperator.Add, Ref("indexed"), seedValue, type, "seed delta")),
                new SsaStoreIndirectInstruction(Ref("address", elementPointer), type, Ref("updated")),
                new SsaValueInstruction("readUpdated", new SsaLoadIndirectRValue(Ref("address", elementPointer), type, "read updated")),
                new SsaValueInstruction("sumNext", new SsaBinaryRValue(SsaBinaryOperator.Add, Ref("sum"), Ref("readUpdated"), type, "checksum")),
                new SsaValueInstruction("next", new SsaBinaryRValue(SsaBinaryOperator.Add, Ref("counter", counterType), C(1, counterType), counterType, "increment"))
            ], new(SsaTerminatorKind.Goto, [1])),
            new SsaBasicBlock(3, "exit", [], [], new(SsaTerminatorKind.Return, [], Value: Ref("sum")))
        };
        return new("Cyclic", [new("Kernel", type, [new("seed", seedType)], true, true, 0, blocks)]);
    }
}
