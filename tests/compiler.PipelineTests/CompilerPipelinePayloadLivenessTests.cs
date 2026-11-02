using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelinePayloadLivenessTests
{
    [Theory]
    [InlineData(false, "u32[7 max]", "return values.Length;", true)]
    [InlineData(true, "u32[7 max]", "return values.Length;", true)]
    [InlineData(false, "i16[min -7]", "return values.Length;", true)]
    [InlineData(true, "i16[min -7]", "return values.Length;", true)]
    [InlineData(false, "bool", "return values.Length;", true)]
    [InlineData(true, "bool", "return values.Length;", true)]
    [InlineData(false, "bool", "return values[1] ? (u64[0 max])3 : (u64[0 max])5;", false)]
    [InlineData(true, "bool", "return values[1] ? (u64[0 max])3 : (u64[0 max])5;", false)]
    [InlineData(false, "f64", "return values.Length;", true)]
    [InlineData(true, "f64", "return values.Length;", true)]
    [InlineData(false, "u32[7 max]", "if (values[1] != input) { return 3; } return values.Length;", false)]
    [InlineData(true, "u32[7 max]", "if (values[1] != input) { return 3; } return values.Length;", false)]
    [InlineData(false, "u32[7 max]", "Save(values[1]); return values.Length;", false)]
    [InlineData(true, "u32[7 max]", "Save(values[1]); return values.Length;", false)]
    [InlineData(false, "u32[7 max]", "stack u32[7 max] copied = values[1]; callback(copied); return values.Length;", false)]
    [InlineData(true, "u32[7 max]", "stack u32[7 max] copied = values[1]; callback(copied); return values.Length;", false)]
    [InlineData(false, "u32[7 max]", "*other = values[1]; return values.Length;", false)]
    [InlineData(true, "u32[7 max]", "*other = values[1]; return values.Length;", false)]
    [InlineData(false, "u8[0 1]", "stack u8[0 1][] outside = slice(other, 2); return outside[values[1]];", false)]
    [InlineData(true, "u8[0 1]", "stack u8[0 1][] outside = slice(other, 2); return outside[values[1]];", false)]
    [InlineData(false, "u32[7 max]", "init values[0] = choose ? values[1] : input; return values.Length;", true)]
    [InlineData(true, "u32[7 max]", "init values[0] = choose ? values[1] : input; return values.Length;", true)]
    public void ScalarCopiesAreDeadOnlyWhenAllUsesStayInUnobservedPayload(bool packaged, string elementType, string observe, bool expected)
    {
        var directory = Directory.CreateTempSubdirectory("stark-payload-copy-");
        try
        {
            var librarySource = $$"""
                module PayloadCopies
                public noinline unsafe finite void Copy<T>(borrow T tag, mut borrow dynamic {{elementType}} values)
                {
                    init values[1] = values[0];
                }
                """;
            var libraryPath = Path.Combine(directory.FullName, "PayloadCopies.stark");
            if (packaged)
            {
                var library = DefaultCompilerPipeline.Create().Run(new CompilationInput(librarySource, libraryPath));
                Assert.True(library.Succeeded, string.Join(Environment.NewLine, library.Diagnostics));
                var manifest = PackageImageBuilder.Create(library, Path.Combine(directory.FullName, "libPayloadCopies.a"));
                File.WriteAllText(Path.Combine(directory.FullName, "libPayloadCopies.starkpkg.json"), manifest.ToJson());
            }
            else File.WriteAllText(libraryPath, librarySource);
            var consumer = DefaultCompilerPipeline.Create().Run(new CompilationInput($$"""
                import PayloadCopies
                module Demo
                unsafe ffi fn void Save({{elementType}} value);
                export unsafe fn u64[0 max] Probe({{elementType}} input, bool choose, rawmutptr<{{elementType}}> other,
                    fnptr<fn void(borrow {{elementType}})> callback)
                {
                    stack bool tag = true;
                    stack mut dynamic {{elementType}} values = new(2);
                    init values[0] = input;
                    Copy(tag, values);
                """ + observe + "\n}", Path.Combine(directory.FullName, "Demo.stark")), new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName), StopAfterPassId: "arithmetic-fold-ssa"));
            Assert.True(consumer.Succeeded, string.Join(Environment.NewLine, consumer.Diagnostics));
            var original = consumer.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var plan = new SsaDeadPayloadSpecializer(consumer.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel),
                consumer.Artifacts.GetRequired(CompilerArtifactKeys.HighLevelIr).Functions
                    .ToDictionary(static function => function.Name, static function => function.Effects, StringComparer.Ordinal),
                consumer.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts))
                .Optimize(original, new HashSet<string>(StringComparer.Ordinal) { "Probe" });
            Assert.Equal(expected, plan.ReplacesPayloadReads);
            Assert.Equal(expected, !ReferenceEquals(original, plan.Module));
            if (expected)
            {
                var copy = Assert.Single(plan.Module.Functions, function =>
                    plan.Origins.TryGetValue(function.Name, out var origin) && origin.Contains("Copy", StringComparison.Ordinal));
                var originalCopy = Assert.Single(original.Functions, function => function.Name == plan.Origins[copy.Name]);
                var element = originalCopy.Parameters[1].Type.ElementType;
                Assert.DoesNotContain(copy.Blocks.SelectMany(block => block.Instructions), instruction =>
                    instruction is SsaValueInstruction { Value: SsaLoadIndirectRValue load } && load.Type == element);
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false, "return (u64[0 max])owner.Buffer.Length + (u64[0 max])owner.Marker;", true)]
    [InlineData(true, "return (u64[0 max])owner.Buffer.Length + (u64[0 max])owner.Marker;", true)]
    [InlineData(false, "owner.Buffer = new(16); Fill(tag, owner); return owner.Buffer.Length;", true)]
    [InlineData(false, "stack mut dynamic u8[0 max] replacement = new(); if (!replacement.TryReserveCapacity(16)) return 9; init replacement[0] = 42; owner.Buffer = replacement; return owner.Buffer.Length;", true)]
    [InlineData(true, "stack mut dynamic u8[0 max] replacement = new(); if (!replacement.TryReserveCapacity(16)) return 9; init replacement[0] = 42; owner.Buffer = replacement; return owner.Buffer.Length;", true)]
    [InlineData(false, "stack mut dynamic u8[0 max] replacement = new(); if (!replacement.TryReserveCapacity(16)) return 9; init replacement[0] = 42; owner.Buffer = replacement; return owner.Buffer[0];", false)]
    [InlineData(true, "stack mut dynamic u8[0 max] replacement = new(); if (!replacement.TryReserveCapacity(16)) return 9; init replacement[0] = 42; owner.Buffer = replacement; return owner.Buffer[0];", false)]
    [InlineData(true, "owner.Buffer = new(16); Fill(tag, owner); return owner.Buffer.Length;", true)]
    [InlineData(false, "return owner.Buffer[0];", false)]
    [InlineData(true, "return owner.Buffer[0];", false)]
    [InlineData(false, "Expose(&owner.Buffer[0]); return owner.Marker;", false)]
    [InlineData(true, "Expose(&owner.Buffer[0]); return owner.Marker;", false)]
    [InlineData(false, "owner.Buffer = incoming; Fill(tag, owner); return owner.Buffer.Length;", false)]
    [InlineData(true, "owner.Buffer = incoming; Fill(tag, owner); return owner.Buffer.Length;", false)]
    [InlineData(false, "stack mut dynamic u8[0 max] replacement = new(); if (!replacement.TryReserveCapacity(16)) return 9; init replacement[0] = 42; Expose(&replacement[0]); owner.Buffer = replacement; return owner.Buffer.Length;", false)]
    [InlineData(true, "stack mut dynamic u8[0 max] replacement = new(); if (!replacement.TryReserveCapacity(16)) return 9; init replacement[0] = 42; Expose(&replacement[0]); owner.Buffer = replacement; return owner.Buffer.Length;", false)]
    public void AggregateHeaderUpdatesPreserveOnlyOwnedUnobservedPayload(bool packaged, string observe, bool expected)
    {
        var directory = Directory.CreateTempSubdirectory("stark-payload-header-");
        try
        {
            const string librarySource = """
                module PayloadOwners
                public struct Owner
                {
                    dynamic u8[0 max] Buffer;
                    u32[0 max] Marker;
                    Owner()
                    {
                        self.Buffer = new();
                        self.Marker = 17;
                    }
                }
                public inline unsafe finite void Fill<T>(borrow T tag, mut borrow Owner owner)
                {
                    init owner.Buffer[0] = 42;
                }
                """;
            var libraryPath = Path.Combine(directory.FullName, "PayloadOwners.stark");
            if (packaged)
            {
                var library = DefaultCompilerPipeline.Create().Run(new CompilationInput(librarySource, libraryPath));
                Assert.True(library.Succeeded, string.Join(Environment.NewLine, library.Diagnostics));
                var manifest = PackageImageBuilder.Create(library, Path.Combine(directory.FullName, "libPayloadOwners.a"));
                File.WriteAllText(Path.Combine(directory.FullName, "libPayloadOwners.starkpkg.json"), manifest.ToJson());
            }
            else File.WriteAllText(libraryPath, librarySource);
            var consumer = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                import PayloadOwners
                module Demo
                unsafe ffi fn void Expose(rawptr<u8[0 max]> data);
                export unsafe fn u64[0 max] Probe(dynamic u8[0 max] incoming)
                {
                    stack mut Owner owner = new();
                    stack bool tag = true;
                    owner.Buffer = new(8);
                    Fill(tag, owner);
                    owner.Marker = 29;
                """ + observe + "\n}", Path.Combine(directory.FullName, "Demo.stark")), new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName), StopAfterPassId: "arithmetic-fold-ssa"));
            Assert.True(consumer.Succeeded, string.Join(Environment.NewLine, consumer.Diagnostics));
            var original = consumer.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var plan = new SsaDeadPayloadSpecializer(consumer.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel),
                consumer.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects).Functions,
                consumer.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts))
                .Optimize(original, new HashSet<string>(StringComparer.Ordinal) { "Probe" });
            Assert.Equal(expected, !ReferenceEquals(original, plan.Module));
            if (expected)
            {
                var instructions = plan.Module.Functions.Where(f => f.Name == "Probe" || plan.Origins.ContainsKey(f.Name))
                    .SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToArray();
                Assert.DoesNotContain(instructions, i => i is SsaStoreIndirectInstruction { ValueType.BitWidth: 8 });
                Assert.Contains(instructions, i => i is SsaValueInstruction { Value: SsaDynamicStorageAllocationRValue });
            }
        }
        finally { directory.Delete(recursive: true); }
    }

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
