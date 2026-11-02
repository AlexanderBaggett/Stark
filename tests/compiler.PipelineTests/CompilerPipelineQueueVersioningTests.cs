using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineQueueVersioningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedConcreteTransitionDoesNotAcquireAProofFromItsName(bool staleLength)
    {
        var result = CompileResult(Source(""), "inline-ssa");
        var module = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
        var concrete = module.Functions.Single(f => f.Name.Contains("Queue_TryDequeue__u32", StringComparison.Ordinal));
        var blocks = concrete.Blocks.ToArray();
        var changed = false;
        for (var b = 0; b < blocks.Length; b++)
        {
            var instructions = blocks[b].Instructions.ToArray();
            if (staleLength)
            {
                var subtract = Array.FindIndex(instructions, i => i is SsaStoreIndirectInstruction store
                    && instructions.OfType<SsaValueInstruction>().Any(v => v.ResultName == (store.Value as SsaValueReference)?.Name
                        && v.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Subtract }));
                if (subtract < 0) continue;
                var oldRead = instructions.Take(subtract).OfType<SsaValueInstruction>().First(v => v.Value is SsaLoadIndirectRValue);
                var finalRead = Array.FindIndex(instructions, subtract + 1, i => i is SsaValueInstruction { Value: SsaLoadIndirectRValue });
                Assert.True(finalRead > subtract);
                var value = (SsaValueInstruction)instructions[finalRead];
                instructions[finalRead] = value with { Value = new SsaUseRValue(new SsaValueReference(oldRead.ResultName, oldRead.Value.Type)) };
                changed = true;
            }
            else
            {
                var increment = Array.FindIndex(instructions, i => i is SsaValueInstruction { Value: SsaBinaryRValue { Operator: SsaBinaryOperator.Add } });
                if (increment < 0) continue;
                var value = (SsaValueInstruction)instructions[increment];
                var binary = (SsaBinaryRValue)value.Value;
                instructions[increment] = value with { Value = binary with { Right = new SsaIntegerConstant(2, binary.Right.Type) } };
                changed = true;
            }
            blocks[b] = blocks[b] with { Instructions = instructions };
        }
        Assert.True(changed);
        module = module with { Functions = module.Functions.Select(f => f.Name == concrete.Name ? f with { Blocks = blocks } : f).ToArray() };
        var optimized = new SsaQueueDequeueLoopVersioner(
            result.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy),
            result.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects)).Optimize(module);
        Assert.Same(module, optimized);
    }

    [Fact]
    public void AnEscapedHeaderDeclinesEvenWhenTheLoopOnlyDequeues()
    {
        var source = Source("").Replace("stack mut u32[0 max] value = 0;", "Interfere(&queue);\nstack mut u32[0 max] value = 0;", StringComparison.Ordinal);
        var before = Compile(source, "inline-ssa").Functions.Single(f => f.Name == "Run");
        var after = Compile(source, "queue-dequeue-loop-ssa").Functions.Single(f => f.Name == "Run");
        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
    }

    [Fact]
    public void OwnedElementsRetainTheOriginalDequeueAndDropPath()
    {
        const string source = """
            import System.Collections
            module QueueOwnedVersioning

            struct Item
            {
                u32[0 max] Value;
                mut drop { }
            }

            public unsafe fn i32[min max] Run()
            {
                stack mut Queue<Item> queue = new();
                queue.Enqueue(new Item() { Value = 1 });
                stack mut Item value = new Item() { Value = 0 };
                while willexit (!queue.IsEmpty())
                {
                    if (!queue.TryDequeue(value)) { return 1; }
                }
                return 0;
            }
            """;
        var before = Compile(source, "inline-ssa").Functions.Single(f => f.Name == "Run");
        var after = Compile(source, "queue-dequeue-loop-ssa").Functions.Single(f => f.Name == "Run");
        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
        Compile(source, "validate-ssa");
    }

    [Theory]
    [InlineData("")]
    [InlineData("if (queue.Count() == 2) { break; }")]
    [InlineData("if (queue.Count() > 0 && queue.Peek() == 3) { return 7; }")]
    public void ScalarDrainKeepsFallbackAndVersionsPerDequeueState(string observation)
    {
        var source = Source(observation);
        var before = Compile(source, "inline-ssa");
        var after = Compile(source, "queue-dequeue-loop-ssa");
        var original = before.Functions.Single(f => f.Name == "Run");
        var versioned = after.Functions.Single(f => f.Name == "Run");
        Assert.True(versioned.Blocks.Count > original.Blocks.Count);
        Assert.Equal(1, DequeueCalls(versioned));
        Assert.Contains(versioned.Blocks.SelectMany(b => b.Instructions), i => i is SsaStoreIndirectInstruction);
        Assert.True(versioned.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>()
            .Count(i => i.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.LessThanOrEqual }) >= 2);
        var concrete = after.Functions.Single(f => f.Name.Contains("Queue_TryDequeue__u32", StringComparison.Ordinal));
        Assert.Contains(concrete.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>(),
            i => i.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.GreaterThanOrEqual });
        // Inlining a body that already contains inline clones must not capture
        // one source definition with another generated name.
        Compile(source, "validate-ssa");
    }

    [Theory]
    [InlineData("queue.Enqueue(5); queue.Enqueue(6);")]
    [InlineData("Unknown();")]
    [InlineData("Interfere(&queue);")]
    [InlineData("callback();")]
    public void MutationOpaqueCallsAndEscapesDeclineVersioning(string operation)
    {
        var source = Source(operation);
        var before = Compile(source, "inline-ssa").Functions.Single(f => f.Name == "Run");
        var after = Compile(source, "queue-dequeue-loop-ssa").Functions.Single(f => f.Name == "Run");
        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
        Assert.Equal(1, DequeueCalls(after));
    }

    private static int DequeueCalls(SsaFunction function) => function.Blocks.SelectMany(b => b.Instructions)
        .OfType<SsaValueInstruction>().Count(i => i.Value is SsaCallRValue call
            && call.FunctionName.Contains("Queue_TryDequeue__u32", StringComparison.Ordinal));

    private static string Source(string operation) => $$"""
        import System.Collections
        module QueueVersioning

        unsafe ffi(c) fn void Unknown();
        unsafe ffi(c) fn void Interfere(rawmutptr<Queue<u32[0 max]>> queue);

        public unsafe fn i32[min max] Run(fnptr<unsafe fn void()> callback)
        {
            stack mut Queue<u32[0 max]> queue = new();
            queue.Enqueue(1);
            queue.Enqueue(2);
            queue.Enqueue(3);
            queue.Enqueue(4);
            stack mut u32[0 max] value = 0;
            stack mut u64[0 max] checksum = 0;
            while willexit (!queue.IsEmpty())
            {
                if (!queue.TryDequeue(value)) { return 1; }
                checksum += value;
                {{operation}}
            }
            return (i32[min max])checksum;
        }
        """;

    private static SsaIrModule Compile(string source, string pass)
        => CompileResult(source, pass).Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

    private static CompilationResult CompileResult(string source, string pass)
    {
        var root = RepositoryRoot();
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source), new CompilerOptions(
            ModuleResolver: new FileSystemModuleResolver(Path.Combine(root, "stdlib", "src")), StopAfterPassId: pass));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        return result;
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stark.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
