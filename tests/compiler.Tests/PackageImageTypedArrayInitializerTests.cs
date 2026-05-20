using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageTypedArrayInitializerTests
{
    [Fact]
    public void ManifestBackedTypedArrayInitializerBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-array-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] SumArray<T>(i32[min max] left, i32[min max] right, T tag) {
                    stack i32[min max][2] values = { left, right };
                    return values[0] + values[1];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32[min max] SumArray<T>(i32[min max] left, i32[min max] right, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack i32[2] values = { left, right };", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return values[0] + values[1];", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] left, i32[min max] right) {
                        return Facade.SumArray(left, right, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumArray = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_SumArray__", StringComparison.Ordinal));
            Assert.True(sumArray.HasBody);
            Assert.True(sumArray.SupportsDirectCodeGeneration);
            Assert.Contains(sumArray.Locals, static local => local.Name == "values");
            Assert.Contains(
                sumArray.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertIndexRValue);
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
