using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsPackageDropRegressionTests
{
    [Fact]
    public async Task ManifestBackedGenericFieldDropResolvesListClearFromStdlibPackage()
    {
        var stdlibDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-list-field-drop-");
        var facadeManifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var facadeResult = pipeline.Run(
                new CompilationInput(
                    """
                    import System.Collections
                    module Facade

                    public struct Owner<T>
                    {
                        System.Collections.List<T> Items;

                        Owner()
                        {
                            self.Items = new();
                        }
                    }
                    """,
                    facadePath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver([tempDirectory.FullName, stdlibDirectory])));

            Assert.True(facadeResult.Succeeded, string.Join(", ", facadeResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var facadeManifest = PackageImageBuilder.Create(
                facadeResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeOnlyManifest = facadeManifest with
            {
                Modules = facadeManifest.Modules
                    .Where(static module => module.ModuleName == "Facade")
                    .ToArray()
            };
            File.WriteAllText(facadeManifestPath, facadeOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run()
                    {
                        stack Facade.Owner<u32[0 max]> owner = new Facade.Owner<u32[0 max]>();
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver([tempDirectory.FullName, stdlibDirectory])));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.DoesNotContain(consumerResult.Diagnostics, static diagnostic => diagnostic.Code == "STK9999");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            Assert.Contains(
                mir!.Functions.SelectMany(static function => function.Blocks)
                    .SelectMany(static block => block.Statements)
                    .Select(static statement => statement.Value),
                static value => value is MidLevelIrDynamicStorageMoveLastRValue);
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
