using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineMemoryRegionTests
{
    [Fact]
    public void MemorySummariesDistinguishHeaderFieldsFromReachablePayload()
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
            module Regions
            struct Header
            {
                rawmutptr<u8[0 max]> Data;
                u64[0 max] Length;
            }
            struct Owner
            {
                dynamic u8[0 max] Items;
            }
            finite law u64[0 max] HeaderOnly(borrow Header header)
            {
                return header.Length;
            }
            unsafe finite law rawmutptr<u8[0 max]> PointerValue(borrow Header header)
            {
                return header.Data;
            }
            unsafe finite law u8[0 max] PayloadRead(borrow Header header)
            {
                return *header.Data;
            }
            unsafe finite law u8[0 max] PayloadIndex(borrow Header header)
            {
                return header.Data[0];
            }
            unsafe fn void PayloadWrite(mut borrow Header header)
            {
                *header.Data = 7;
                return;
            }
            unsafe finite law u8[0 max] DirectPointer(rawptr<u8[0 max]> data)
            {
                return *data;
            }
            unsafe finite law u8[0 max] PayloadViaHelper(borrow Header header)
            {
                return DirectPointer(header.Data);
            }
            unsafe finite law u8[0 max] PayloadViaAddress(borrow Header header)
            {
                return DirectPointer(&header.Data[0]);
            }
            unsafe finite law u8[0 max] PayloadViaCast(borrow Header header)
            {
                return *((rawptr<u8[0 max]>)header.Data);
            }
            unsafe finite law u8[0 max] UnknownAddress(u64[0 max] address)
            {
                return *((rawptr<u8[0 max]>)address);
            }
            unsafe finite law u8[0 max] CopiedHeader(borrow Header header)
            {
                stack Header copy = new() { Data = header.Data, Length = header.Length };
                return *copy.Data;
            }
            unsafe fn void WriteCopiedHeader(borrow Header header)
            {
                stack Header copy = new() { Data = header.Data, Length = header.Length };
                *copy.Data = 7;
                return;
            }
            unsafe fn void WritePointer(rawmutptr<u8[0 max]> data)
            {
                *data = 7;
                return;
            }
            unsafe fn void WriteViaHelper(mut borrow Header header)
            {
                WritePointer(header.Data);
                return;
            }
            finite law u64[0 2 ** 63 - 1] OwnerLength(borrow Owner owner)
            {
                return owner.Items.Length;
            }
            unsafe finite law u8[0 max] OwnerElement(borrow Owner owner)
            {
                if (owner.Items.Length == 0) { return 0; }
                return owner.Items[0];
            }
            """), new CompilerOptions(StopAfterPassId: "semantic-validate"));
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var summaries = result.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation).Functions;
        foreach (var name in new[] { "HeaderOnly", "PointerValue", "DirectPointer", "OwnerLength" })
        {
            Assert.True(summaries[name].MemoryEffects!.ReadsArgumentMemory);
            Assert.False(summaries[name].MemoryEffects!.ReadsOtherMemory);
        }
        foreach (var name in new[] { "PayloadRead", "PayloadIndex", "PayloadViaHelper", "PayloadViaAddress", "PayloadViaCast", "OwnerElement" })
        {
            Assert.True(summaries[name].MemoryEffects!.ReadsArgumentMemory);
            Assert.True(summaries[name].MemoryEffects!.ReadsOtherMemory);
        }
        Assert.True(summaries["PayloadWrite"].MemoryEffects!.WritesOtherMemory);
        Assert.True(summaries["WriteViaHelper"].MemoryEffects!.WritesOtherMemory);
        Assert.True(summaries["UnknownAddress"].MemoryEffects!.ReadsOtherMemory);
        Assert.True(summaries["CopiedHeader"].MemoryEffects!.ReadsOtherMemory);
        Assert.True(summaries["WriteCopiedHeader"].MemoryEffects!.WritesOtherMemory);
    }

    [Fact]
    public void PackagedPayloadReadEffectsReachConsumers()
    {
        var directory = Directory.CreateTempSubdirectory("stark-payload-memory-effects-");
        try
        {
            var library = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                module Facade
                public struct Header
                {
                    rawptr<u8[0 max]> Data;
                    u64[0 max] Length;
                }
                public unsafe noinline finite law u8[0 max] Read(borrow Header header)
                {
                    return *header.Data;
                }
                public finite law u64[0 max] HeaderOnly(borrow Header header)
                {
                    return header.Length;
                }
                """, Path.Combine(directory.FullName, "Facade.stark")));
            Assert.True(library.Succeeded, string.Join(Environment.NewLine, library.Diagnostics));
            var manifest = PackageImageBuilder.Create(library, Path.Combine(directory.FullName, "libFacade.a"));
            File.WriteAllText(Path.Combine(directory.FullName, "libFacade.starkpkg.json"), manifest.ToJson());

            var consumer = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                import Facade
                module Consumer
                unsafe finite law u8[0 max] Read(borrow Facade.Header header)
                {
                    return Facade.Read(header);
                }
                finite law u64[0 max] HeaderOnly(borrow Facade.Header header)
                {
                    return Facade.HeaderOnly(header);
                }
                """, Path.Combine(directory.FullName, "Consumer.stark")), new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName),
                    StopAfterPassId: "emit-llvm"));
            Assert.True(consumer.Succeeded, string.Join(Environment.NewLine, consumer.Diagnostics));
            var summaries = consumer.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation).Functions;
            Assert.True(summaries["Read"].MemoryEffects!.ReadsOtherMemory);
            Assert.False(summaries["HeaderOnly"].MemoryEffects!.ReadsOtherMemory);
            Assert.True(consumer.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules).TryGet("Facade", out var imported));
            Assert.True(imported!.PackageImageFacts!.FunctionSemantics["Facade.Read"].MemoryEffects!.ReadsOtherMemory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
