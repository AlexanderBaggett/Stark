using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineEnumLayoutTests
{
    [Fact]
    public void ManifestBackedModulesPreservePublishedLayoutFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-layout-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Padded {
                    i8[-128 127] Small;
                    i32[-2147483648 2147483647] Value;
                }

                public enum Token {
                    End,
                    Move { X: i32[-2147483648 2147483647], Y: i32[-2147483648 2147483647] },
                }
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
            Assert.Contains("public struct Padded {", sourceText, StringComparison.Ordinal);
            Assert.Contains("public enum Token {", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
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
                    StopAfterPassId: "enum-layout"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(importedModule.PackageImageFacts!.ConcreteLayouts.TryGetValue("Facade.Padded", out var paddedLayout));
            Assert.Equal(8, paddedLayout.SizeBytes);
            Assert.Equal(4, paddedLayout.AlignmentBytes);
            Assert.True(importedModule.PackageImageFacts.EnumLayouts.TryGetValue("Facade.Token", out var importedTokenLayout));
            Assert.Equal(EnumLayoutKind.DirectTag, importedTokenLayout.Kind);
            Assert.Equal("$tag", importedTokenLayout.TagField.Name);
            Assert.Equal("$Move_X", importedTokenLayout.OrderedFields[1].Name);
            Assert.Equal("$Move_Y", importedTokenLayout.OrderedFields[2].Name);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);
            Assert.True(enumLayoutModel.Layouts.TryGetValue("Facade.Token", out var tokenLayout));
            Assert.Equal(EnumLayoutKind.DirectTag, tokenLayout.Kind);
            Assert.Equal("$tag", tokenLayout.TagField.Name);
            Assert.Equal("$Move_X", tokenLayout.OrderedFields[1].Name);
            Assert.Equal("$Move_Y", tokenLayout.OrderedFields[2].Name);
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
