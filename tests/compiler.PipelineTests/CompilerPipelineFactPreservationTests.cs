using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineFactPreservationTests
{
    [Theory]
    [InlineData("dynamic-storage-ssa")]
    [InlineData("shape-branches")]
    [InlineData("arithmetic-fold-ssa")]
    public void DynamicStorageFactsSurviveReadOnlyCallsThroughLatePasses(string pass)
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput("""
                module Demo

                noinline finite law u64[0 2 ** 63 - 1] Observe(borrow dynamic u32[0 max] values)
                {
                    return values.Capacity;
                }

                unsafe noinline fn void Grow(mut borrow dynamic u32[0 max] values)
                {
                    values.Reserve(16);
                    return;
                }

                unsafe ffi fn void Opaque(rawmutptr<dynamic u32[0 max]> values);

                unsafe fn dynamic u32[0 max] ReadOnly()
                {
                    stack mut dynamic u32[0 max] values = new(8);
                    init values[0] = (u32[0 max])Observe(values);
                    return values;
                }

                unsafe fn dynamic u32[0 max] Mutating()
                {
                    stack mut dynamic u32[0 max] values = new(8);
                    Grow(values);
                    return values;
                }

                unsafe fn dynamic u32[0 max] Unknown()
                {
                    stack mut dynamic u32[0 max] values = new(8);
                    Opaque(&values);
                    return values;
                }

                unsafe fn dynamic u32[0 max] Indirect(fnptr<fn void(mut borrow dynamic u32[0 max])> change)
                {
                    stack mut dynamic u32[0 max] values = new(8);
                    change(values);
                    return values;
                }

                unsafe fn dynamic u32[0 max] ReadOnlyBranch(bool choose)
                {
                    stack mut dynamic u32[0 max] values = new(8);
                    if (choose)
                    {
                        init values[0] = (u32[0 max])Observe(values);
                    }
                    else
                    {
                        init values[0] = (u32[0 max])Observe(values) + 1;
                    }
                    return values;
                }

                unsafe fn dynamic u32[0 max] MaybeGrow(bool choose)
                {
                    stack mut dynamic u32[0 max] values = new(8);
                    if (choose)
                    {
                        Grow(values);
                    }
                    return values;
                }
                """),
            new CompilerOptions(StopAfterPassId: pass));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var ssa = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
        var facts = result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts);
        foreach (var name in new[] { "ReadOnly", "ReadOnlyBranch" })
        {
            var readOnly = ReturnFacts(name);
            Assert.Equal(SsaFactLatticeKind.Known, readOnly.CapacityKind);
            Assert.Equal(new System.Numerics.BigInteger(8), readOnly.CapacityRange!.Min);
            Assert.Equal(new System.Numerics.BigInteger(8), readOnly.CapacityRange.Max);
            Assert.Equal(new System.Numerics.BigInteger(1), readOnly.LengthRange!.Min);
            Assert.Equal(new System.Numerics.BigInteger(1), readOnly.LengthRange.Max);
            Assert.NotNull(readOnly.DynamicStorageRegion?.BackingAllocationId);
        }

        foreach (var name in new[] { "Mutating", "Unknown", "Indirect", "MaybeGrow" })
        {
            var invalidated = ReturnFacts(name);
            Assert.False(invalidated.CapacityRange is { Min: var min, Max: var max } && min == 8 && max == 8);
            Assert.Null(invalidated.DynamicStorageRegion?.BackingAllocationId);
        }

        SsaValueFacts ReturnFacts(string name)
        {
            var function = Assert.Single(ssa.Functions, f => f.Name == name);
            var block = Assert.Single(function.Blocks, b => b.Terminator.Kind == SsaTerminatorKind.Return);
            var returned = Assert.IsType<SsaValueReference>(block.Terminator.Value);
            return facts.Functions[name].Values[returned.Name];
        }
    }

    [Fact]
    public void LateFactsUsePackagedReadOnlySummaries()
    {
        var directory = Directory.CreateTempSubdirectory("stark-late-imported-facts-");
        try
        {
            var facadePath = Path.Combine(directory.FullName, "Facade.stark");
            const string library = """
                module Facade
                public noinline finite law u64[0 2 ** 63 - 1] Observe(borrow dynamic u32[0 max] values)
                {
                    return values.Capacity;
                }
                """;
            var compiled = DefaultCompilerPipeline.Create().Run(new CompilationInput(library, facadePath));
            Assert.True(compiled.Succeeded, string.Join(Environment.NewLine, compiled.Diagnostics));
            var manifest = PackageImageBuilder.Create(compiled, Path.Combine(directory.FullName, "libFacade.a"));
            File.WriteAllText(Path.Combine(directory.FullName, "libFacade.starkpkg.json"), manifest.ToJson());

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput("""
                    import Facade
                    module Demo
                    unsafe fn dynamic u32[0 max] Run()
                    {
                        stack mut dynamic u32[0 max] values = new(8);
                        init values[0] = (u32[0 max])Facade.Observe(values);
                        return values;
                    }
                    """, Path.Combine(directory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName),
                    StopAfterPassId: "shape-branches"));
            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
            var run = Assert.Single(result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions,
                f => f.Name == "Run");
            var returned = Assert.IsType<SsaValueReference>(Assert.Single(run.Blocks,
                b => b.Terminator.Kind == SsaTerminatorKind.Return).Terminator.Value);
            var facts = result.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts).Functions["Run"].Values[returned.Name];
            Assert.Equal(new System.Numerics.BigInteger(8), facts.CapacityRange!.Min);
            Assert.Equal(new System.Numerics.BigInteger(8), facts.CapacityRange.Max);
            Assert.NotNull(facts.DynamicStorageRegion?.BackingAllocationId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
