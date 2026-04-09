using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineFunctionEffectsTests
{
    [Fact]
    public void ManifestBackedModulesPreserveImportedFunctionEffectsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-effects-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public strictfp hot noinline finite law f32 Precise(f32 value);
                export cold ffi fn void Sink(rawptr<i8> value);
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

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
            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var sourceText));
            Assert.Contains("public strictfp hot noinline finite law f32 Precise(f32 value);", sourceText, StringComparison.Ordinal);
            Assert.Contains("export cold ffi fn void Sink(rawptr<i8> value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn f32 Run(f32 value) {
                        return value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "function-effects"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
            Assert.NotNull(effectModel);

            var precise = effectModel.Functions["Facade.Precise"];
            Assert.True(precise.IsStrictFp);
            Assert.True(precise.IsHot);
            Assert.False(precise.IsCold);
            Assert.True(precise.IsPure);
            Assert.Equal(InlinePreference.NoInline, precise.InlinePreference);
            Assert.True(precise.UseFastCallingConvention);

            var sink = effectModel.Functions["Facade.Sink"];
            Assert.True(sink.IsFfi);
            Assert.True(sink.IsCold);
            Assert.False(sink.UseFastCallingConvention);
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
