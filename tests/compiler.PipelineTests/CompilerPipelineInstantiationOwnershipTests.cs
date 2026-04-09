using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineInstantiationOwnershipTests
{
    [Fact]
    public void RootGenericInstantiationsStayOwnedByTheRootModule()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair<T>(T Value) { }

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32 Run(Pair<i32> pair) {
                    return Identity(pair.Value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "instantiation-ownership"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.InstantiationOwnership, out InstantiationOwnershipModel? ownership));
        Assert.NotNull(ownership);

        var functionOwnership = Assert.Single(ownership.Functions);
        Assert.Equal("Identity", functionOwnership.TemplateName);
        Assert.Equal("Demo", functionOwnership.DeclaringModuleName);
        Assert.Equal("Demo", functionOwnership.OwnerModuleName);
        Assert.True(functionOwnership.IsDeclaringModuleSourceBacked);
        Assert.False(functionOwnership.RequiresConsumerOwnership);

        var typeOwnership = Assert.Single(ownership.Types);
        Assert.Equal("Pair", typeOwnership.TemplateName);
        Assert.Equal("Pair<i32>", typeOwnership.InstantiatedTypeName);
        Assert.Equal("Demo", typeOwnership.DeclaringModuleName);
        Assert.Equal("Demo", typeOwnership.OwnerModuleName);
        Assert.True(typeOwnership.IsDeclaringModuleSourceBacked);
        Assert.False(typeOwnership.RequiresConsumerOwnership);
    }


    [Fact]
    public void SourceBackedImportedGenericInstantiationsStayOwnedByTheDefiningModule()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-ownership-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Facade.stark"),
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value) {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Pair<i32> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "instantiation-ownership",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.InstantiationOwnership, out InstantiationOwnershipModel? ownership));
            Assert.NotNull(ownership);

            var functionOwnership = Assert.Single(ownership.Functions);
            Assert.Equal("Facade.Identity", functionOwnership.TemplateName);
            Assert.Equal("Facade", functionOwnership.DeclaringModuleName);
            Assert.Equal("Facade", functionOwnership.OwnerModuleName);
            Assert.True(functionOwnership.IsDeclaringModuleSourceBacked);
            Assert.False(functionOwnership.RequiresConsumerOwnership);

            var typeOwnership = Assert.Single(ownership.Types);
            Assert.Equal("Facade.Pair", typeOwnership.TemplateName);
            Assert.Equal("Facade.Pair<i32>", typeOwnership.InstantiatedTypeName);
            Assert.Equal("Facade", typeOwnership.DeclaringModuleName);
            Assert.Equal("Facade", typeOwnership.OwnerModuleName);
            Assert.True(typeOwnership.IsDeclaringModuleSourceBacked);
            Assert.False(typeOwnership.RequiresConsumerOwnership);
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
    public void ManifestBackedGenericInstantiationsFallBackToTheRootConsumerModule()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-ownership-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

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

                    fn i32 Run(Facade.Pair<i32> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "instantiation-ownership",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.InstantiationOwnership, out InstantiationOwnershipModel? ownership));
            Assert.NotNull(ownership);

            var functionOwnership = Assert.Single(ownership.Functions);
            Assert.Equal("Facade.Identity", functionOwnership.TemplateName);
            Assert.Equal("Facade", functionOwnership.DeclaringModuleName);
            Assert.Equal("Demo", functionOwnership.OwnerModuleName);
            Assert.False(functionOwnership.IsDeclaringModuleSourceBacked);
            Assert.True(functionOwnership.RequiresConsumerOwnership);

            var typeOwnership = Assert.Single(ownership.Types);
            Assert.Equal("Facade.Pair", typeOwnership.TemplateName);
            Assert.Equal("Facade.Pair<i32>", typeOwnership.InstantiatedTypeName);
            Assert.Equal("Facade", typeOwnership.DeclaringModuleName);
            Assert.Equal("Demo", typeOwnership.OwnerModuleName);
            Assert.False(typeOwnership.IsDeclaringModuleSourceBacked);
            Assert.True(typeOwnership.RequiresConsumerOwnership);
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
