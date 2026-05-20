using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineSpecializationCodegenStrategyTests
{
    [Fact]
    public void RootGenericInstantiationsChooseOwnedBodyCodegenStrategy()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32[min max] Run(i32[min max] value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialization-codegen-strategy"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
        Assert.NotNull(strategy);

        var function = Assert.Single(strategy.Functions);
        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", function.SymbolName);
        Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
        Assert.Equal(FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBody, function.StrategyKind);
        Assert.False(function.SupportsAbiFallback);
    }


    [Fact]
    public void SourceBackedImportedLawGenericsChooseLawCloneAwareCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-codegen-strategy-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers {
                    finite law T Identity<T>(T value) {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32[min max] Run(i32[min max] value) {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal("__stark_mono_fn_Math__Math_Numbers_Identity__i32", function.SymbolName);
            Assert.Equal(MonomorphizationLinkageKind.LinkOnceOdrComdat, function.Linkage);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBodyAndPreferLawCallerClone, function.StrategyKind);
            Assert.True(function.SupportsAbiFallback);
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
    public void ColdImportedLawGenericsUseAbiFallbackOnlyCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cold-source-generic-codegen-strategy-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers {
                    cold finite law T Identity<T>(T value) {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32[min max] Run(i32[min max] value) {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly, function.StrategyKind);
            Assert.True(function.SupportsAbiFallback);
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
    public void DeclarationOnlyImportedGenericInstantiationsChooseAbiFallbackOnlyCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-codegen-strategy-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Identity__i32", function.SymbolName);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly, function.StrategyKind);
            Assert.True(function.SupportsAbiFallback);
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
    public void ManifestBackedImportedGenericsWithoutPublishedAbiFactsDoNotClaimAbiFallbackInCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-codegen-abi-facts-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.NotNull(facadeModule.CompilerSections?.CompilerFacts?.AbiFunctions);

            var abiStrippedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    AbiFunctions = []
                                }
                            }
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, abiStrippedManifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBodyAndPreferLawCallerClone, function.StrategyKind);
            Assert.False(function.SupportsAbiFallback);
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
