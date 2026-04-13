using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineLowerAbiTests
{
    [Fact]
    public void MonomorphizedGenericFunctionsReceiveExplicitAbiLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Big {
                    i64[-9223372036854775808 9223372036854775807] A;
                    i64[-9223372036854775808 9223372036854775807] B;
                    i64[-9223372036854775808 9223372036854775807] C;
                }

                fn T Bounce<T>(T value) {
                    return value;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return Bounce(value);
                }

                fn Big Make(Big value) {
                    return Bounce(value);
                }
                """),
            new CompilerOptions(
                QualifyModuleSymbols: true,
                StopAfterPassId: "lower-abi"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);

        var small = abiModel.Functions["__stark_mono_fn_Demo__Bounce__i32"];
        Assert.Equal("__stark_mono_fn_Demo__Bounce__i32", small.SymbolName);
        Assert.True(small.UsesFastCallingConvention);
        Assert.False(small.ReturnsIndirect);
        var smallParameter = Assert.Single(small.UserParameters);
        Assert.Equal(AbiParameterKind.Direct, smallParameter.Kind);
        Assert.Equal(StarkTypeKind.Integer, smallParameter.LlvmType.Kind);

        var big = abiModel.Functions["__stark_mono_fn_Demo__Bounce__Big"];
        Assert.Equal("__stark_mono_fn_Demo__Bounce__Big", big.SymbolName);
        Assert.True(big.UsesFastCallingConvention);
        Assert.True(big.ReturnsIndirect);
        var bigParameter = Assert.Single(big.UserParameters);
        Assert.Equal(AbiParameterKind.IndirectIn, bigParameter.Kind);
        Assert.Equal(StarkTypeKind.RawPointer, bigParameter.LlvmType.Kind);
    }


    [Fact]
    public void ManifestBackedModulesPreservePublishedAbiFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-abi-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Big {
                    i64[-9223372036854775808 9223372036854775807] A;
                    i64[-9223372036854775808 9223372036854775807] B;
                    i64[-9223372036854775808 9223372036854775807] C;
                }

                public fn Big Make();
                public fn i64[-9223372036854775808 9223372036854775807] Read(Big value);
                public fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] value);
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.TypedInterface);
            Assert.NotNull(facadeModule.CompilerFacts);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run() {
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
            Assert.NotNull(abiModel);

            var add = abiModel.Functions["Facade.Add"];
            Assert.Equal("Facade.Add", add.SymbolName);
            Assert.False(add.ReturnsIndirect);
            Assert.Equal(AbiParameterKind.Direct, Assert.Single(add.UserParameters).Kind);

            var make = abiModel.Functions["Facade.Make"];
            Assert.Equal("Facade.Make", make.SymbolName);
            Assert.True(make.ReturnsIndirect);
            Assert.Equal(AbiParameterKind.SRet, Assert.Single(make.Parameters).Kind);

            var read = abiModel.Functions["Facade.Read"];
            Assert.Equal("Facade.Read", read.SymbolName);
            Assert.Equal(AbiParameterKind.IndirectIn, Assert.Single(read.UserParameters).Kind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedMethodsPreservePublishedAbiFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-method-abi-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Counter {
                    i32[-2147483648 2147483647] Value;

                    fn void Reset(borrow mut Counter self) {
                        self.Value = 0;
                        return;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
            Assert.NotNull(abiModel);

            var reset = abiModel.Functions["Facade.Counter.Reset"];
            Assert.Equal("Facade.Counter.Reset", reset.SymbolName);
            Assert.Equal(AbiParameterKind.IndirectIn, Assert.Single(reset.UserParameters).Kind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
