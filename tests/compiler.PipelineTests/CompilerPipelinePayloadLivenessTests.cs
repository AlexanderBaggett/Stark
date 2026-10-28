using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelinePayloadLivenessTests
{
    [Fact]
    public void PackagedTypedDynamicAccessPreservesItsStorageOrigin()
    {
        var directory = Directory.CreateTempSubdirectory("stark-payload-origin-package-");
        try
        {
            var library = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                module Facade
                public inline unsafe fn void Fill<T>(T tag, mut borrow dynamic u8[0 max] values)
                {
                    init values[0] = 42;
                    init values[1] = 7;
                    return;
                }
                """, Path.Combine(directory.FullName, "Facade.stark")));
            Assert.True(library.Succeeded, string.Join(Environment.NewLine, library.Diagnostics));
            var manifest = PackageImageBuilder.Create(library, Path.Combine(directory.FullName, "libFacade.a"));
            File.WriteAllText(Path.Combine(directory.FullName, "libFacade.starkpkg.json"), manifest.ToJson());
            var consumer = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                import Facade
                module Demo
                export unsafe fn u64[0 max] Probe()
                {
                    stack mut dynamic u8[0 max] values = new(8);
                    Fill(true, values);
                    return values.Length;
                }
                """, Path.Combine(directory.FullName, "Demo.stark")), new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName), StopAfterPassId: "arithmetic-fold-ssa"));
            Assert.True(consumer.Succeeded, string.Join(Environment.NewLine, consumer.Diagnostics));
            var original = consumer.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var plan = new SsaDeadPayloadSpecializer(consumer.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel),
                consumer.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects).Functions,
                consumer.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts))
                .Optimize(original, new HashSet<string>(StringComparer.Ordinal) { "Probe" });
            Assert.NotSame(original, plan.Module);
            var instructions = plan.Module.Functions.Where(f => f.Name == "Probe" || plan.Origins.ContainsKey(f.Name))
                .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions);
            Assert.DoesNotContain(instructions, i => i is SsaStoreIndirectInstruction { ValueType.BitWidth: 8 });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DirectPayloadWritesAreRemovedWithoutACallClone()
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
            module Demo
            export unsafe fn u64[0 max] Probe()
            {
                stack mut dynamic u8[0 max] values = new(8);
                init values[0] = 42;
                return values.Length;
            }
            """));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var body = System.Text.RegularExpressions.Regex.Match(llvm, @"(?ms)^define [^\n]*@Probe\([^\n]*\n\{.*?^}");
        Assert.True(body.Success);
        Assert.DoesNotContain("store i8 ", body.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PathLengthOnlyDiscardsPayloadStoresWithoutChangingHeaderWrites(bool prune)
    {
        var stdlib = CompilerPipelineTestSupport.StandardLibrarySourceRoot();
        var path = Path.GetFullPath(Path.Combine(stdlib, "..", "..", "benchmarks", "text", "PathRepeatedSmallOps.stark"));
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(File.ReadAllText(path), path),
            new CompilerOptions(ModuleResolver: new FileSystemModuleResolver(stdlib), StopAfterPassId: "arithmetic-fold-ssa",
                PruneUnusedLoweredFunctions: prune));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var plan = new SsaDeadPayloadSpecializer(
            result.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel),
            result.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects).Functions,
            result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts))
            .Optimize(result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr), new HashSet<string>(StringComparer.Ordinal) { "main" });
        Assert.NotEmpty(plan.Origins);
        var privateInstructions = plan.Module.Functions.Where(f => plan.Origins.ContainsKey(f.Name))
            .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToArray();
        Assert.DoesNotContain(privateInstructions, i => i is SsaStoreIndirectInstruction { ValueType.BitWidth: 8 });
        Assert.Contains(privateInstructions, i => i is SsaStoreIndirectInstruction { ValueType.BitWidth: 64 });
    }

    [Theory]
    [InlineData("return values.Length;", true)]
    [InlineData("return values[0];", false)]
    [InlineData("return values[0, values.Length][0];", false)]
    [InlineData("stack u8[0 max][] view = values[0, values.Length]; return view.Length;", true)]
    [InlineData("if (choose) { return values[0]; } return values.Length;", false)]
    [InlineData("Expose(&values); return values.Length;", false)]
    [InlineData("read(values); return values.Length;", false)]
    [InlineData("return Read(values);", false)]
    [InlineData("Exported(values); return values.Length;", false)]
    [InlineData("ExposeData(&values[0]); return values.Length;", false)]
    [InlineData("return (u64[0 max])(&values[0]);", false)]
    [InlineData("stack rawmutptr<u8[0 max]> data = &values[0]; data[index] = 7; return values.Length;", false)]
    [InlineData("stack rawmutptr<u8[0 max]> data = &values[0]; data[16] = 7; return values.Length;", false)]
    [InlineData("stack rawmutptr<u8[0 max]> data = &values[0]; data[0] = 7; return values.Length;", true)]
    [InlineData("stack u8[0 max][] view = slice(&values[0], 1); return view.Length;", false)]
    [InlineData("stack rawmutptr<u8[0 max]> data = choose ? &values[0] : other; *data = 7; return values.Length;", false)]
    public void OnlyUnobservedOwnedPayloadIsSpecialized(string observe, bool expected)
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
            module Demo
            unsafe ffi fn void Expose(rawmutptr<dynamic u8[0 max]> value);
            unsafe ffi fn void ExposeData(rawmutptr<u8[0 max]> value);
            export unsafe fn void Exported(mut borrow dynamic u8[0 max] values)
            {
                init values[0] = 7;
                return;
            }
            noinline unsafe finite law u64[0 max] Read(borrow dynamic u8[0 max] values)
            {
                return values[0];
            }
            noinline unsafe fn void Fill(mut borrow dynamic u8[0 max] values)
            {
                init values[0] = 42;
                return;
            }
            export unsafe fn u64[0 max] Probe(bool choose, fnptr<fn void(borrow dynamic u8[0 max])> read,
                u64[0 max] index, rawmutptr<u8[0 max]> other)
            {
                stack mut dynamic u8[0 max] values = new(8);
                Fill(values);
            """ + observe + "\n}"), new CompilerOptions(StopAfterPassId: "arithmetic-fold-ssa"));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var original = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
        var plan = new SsaDeadPayloadSpecializer(
            result.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel),
            result.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects).Functions,
            result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts))
            .Optimize(original, new HashSet<string>(StringComparer.Ordinal) { "Probe" });
        Assert.Equal(expected, plan.Origins.Count > 0);
        if (expected)
        {
            var originalFill = Assert.Single(original.Functions, f => f.Name == "Fill");
            Assert.Contains(originalFill.Blocks.SelectMany(b => b.Instructions), i => i is SsaStoreIndirectInstruction { ValueType.Kind: StarkTypeKind.Integer } store && store.ValueType.BitWidth == 8);
            var specializedFill = Assert.Single(plan.Module.Functions, f => plan.Origins.TryGetValue(f.Name, out var origin) && origin == "Fill");
            Assert.DoesNotContain(specializedFill.Blocks.SelectMany(b => b.Instructions), i => i is SsaStoreIndirectInstruction { ValueType.Kind: StarkTypeKind.Integer } store && store.ValueType.BitWidth == 8);
            // The logical initialized length must still change for this store.
            Assert.Contains(specializedFill.Blocks.SelectMany(b => b.Instructions), i => i is SsaStoreIndirectInstruction);
        }
    }
}
