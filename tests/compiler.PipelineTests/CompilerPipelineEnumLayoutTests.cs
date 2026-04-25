using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineEnumLayoutTests
{
    [Fact]
    public void EnumLayoutsUseSmallestSoundTagWidths()
    {
        var smallResult = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Facade

                public enum MemoryError {
                    OutOfMemory,
                    InvalidLayout,
                    UnsupportedAlignment,
                    TooLarge,
                }

                public enum IOError {
                    NotFound,
                    PermissionDenied,
                    AlreadyExists,
                    InvalidPath,
                    BrokenPipe,
                    DiskFull,
                    Unknown(i32[-2147483648 2147483647]),
                }
                """),
            new CompilerOptions(StopAfterPassId: "enum-layout"));

        Assert.True(smallResult.Succeeded, string.Join(", ", smallResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(smallResult.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? smallLayouts));
        Assert.NotNull(smallLayouts);

        var memoryErrorLayout = smallLayouts.Layouts["MemoryError"];
        AssertCompactTag(memoryErrorLayout, bitWidth: 8, maxTagValue: 3);
        Assert.Equal(["$tag"], memoryErrorLayout.OrderedFields.Select(static field => field.Name).ToArray());

        var ioErrorLayout = smallLayouts.Layouts["IOError"];
        AssertCompactTag(ioErrorLayout, bitWidth: 8, maxTagValue: 6);
        Assert.Equal(["$tag", "$Unknown_0"], ioErrorLayout.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("i32", ioErrorLayout.OrderedFields[1].Type.DisplayName);

        var manyVariants = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 257).Select(static index => $"                    Case{index},"));

        var mediumResult = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                $$"""
                module Facade

                public enum BigStatus {
                {{manyVariants}}
                }
                """),
            new CompilerOptions(StopAfterPassId: "enum-layout"));

        Assert.True(mediumResult.Succeeded, string.Join(", ", mediumResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(mediumResult.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? mediumLayouts));
        Assert.NotNull(mediumLayouts);

        var bigStatusLayout = mediumLayouts.Layouts["BigStatus"];
        AssertCompactTag(bigStatusLayout, bitWidth: 16, maxTagValue: 256);
        Assert.Equal(["$tag"], bigStatusLayout.OrderedFields.Select(static field => field.Name).ToArray());
    }

    [Fact]
    public void EnumPayloadLayoutsUseTargetSoundAlignmentForNonStandardAndWideIntegers()
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Facade

                public enum WidePayload {
                    Empty,
                    I24(i24[-8388608 8388607]),
                    I48(i48[-140737488355328 140737488355327]),
                    I96(i96[min max]),
                    I192(i192[min max]),
                }
                """),
            new CompilerOptions(StopAfterPassId: "enum-layout"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
        Assert.NotNull(typeModel);
        Assert.NotNull(enumLayoutModel);

        var layout = enumLayoutModel.Layouts["WidePayload"];
        Assert.Equal(["$tag", "$I24_0", "$I48_0", "$I96_0", "$I192_0"], layout.OrderedFields.Select(static field => field.Name).ToArray());

        Assert.Equal(new ConcreteTypeLayout(3, 4), ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(layout.OrderedFields[1].Type, typeModel.NamedTypes, enumLayoutModel.Layouts));
        Assert.Equal(new ConcreteTypeLayout(6, 8), ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(layout.OrderedFields[2].Type, typeModel.NamedTypes, enumLayoutModel.Layouts));
        Assert.Equal(new ConcreteTypeLayout(12, 16), ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(layout.OrderedFields[3].Type, typeModel.NamedTypes, enumLayoutModel.Layouts));
        Assert.Equal(new ConcreteTypeLayout(24, 16), ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(layout.OrderedFields[4].Type, typeModel.NamedTypes, enumLayoutModel.Layouts));

        var enumTypeLayout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
            StarkTypeSymbols.Named("WidePayload"),
            typeModel.NamedTypes,
            enumLayoutModel.Layouts);
        Assert.Equal(new ConcreteTypeLayout(64, 16), enumTypeLayout);
    }

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

    [Fact]
    public void PackageImagesPreserveGenericResultAndStatusEnumLayouts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-result-status-layout-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum CommonError {
                    NotFound,
                    PermissionDenied,
                    Unknown(i32[-2147483648 2147483647]),
                }

                public enum CommonStatus {
                    Ok,
                    Err(CommonError),
                }

                public enum CommonResult<T> {
                    Ok(T),
                    Err(CommonError),
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var compilerFacts = facadeModule.CompilerFacts!;

            var errorLayout = Assert.Single(compilerFacts.EnumLayouts!, static layout => layout.QualifiedTypeName == "Facade.CommonError");
            Assert.Equal("integer", errorLayout.TagField.Type.Kind);
            Assert.Equal(8, errorLayout.TagField.Type.BitWidth);
            Assert.Equal("0", errorLayout.TagField.Type.RangeMin);
            Assert.Equal("2", errorLayout.TagField.Type.RangeMax);
            Assert.Equal(["$tag", "$Unknown_0"], errorLayout.OrderedFields.Select(static field => field.Name).ToArray());
            Assert.Equal(32, errorLayout.OrderedFields[1].Type.BitWidth);

            var statusLayout = Assert.Single(compilerFacts.EnumLayouts!, static layout => layout.QualifiedTypeName == "Facade.CommonStatus");
            Assert.Equal(8, statusLayout.TagField.Type.BitWidth);
            Assert.Equal("1", statusLayout.TagField.Type.RangeMax);
            Assert.Equal(["$tag", "$Err_0"], statusLayout.OrderedFields.Select(static field => field.Name).ToArray());

            var resultLayout = Assert.Single(compilerFacts.EnumLayouts!, static layout => layout.QualifiedTypeName == "Facade.CommonResult");
            Assert.Equal(8, resultLayout.TagField.Type.BitWidth);
            Assert.Equal("1", resultLayout.TagField.Type.RangeMax);
            Assert.Equal(["$tag", "$Ok_0", "$Err_0"], resultLayout.OrderedFields.Select(static field => field.Name).ToArray());

            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[-2147483648 2147483647] Unwrap(Facade.CommonResult<i32[-2147483648 2147483647]> result) {
                        switch (result) {
                            case Facade.CommonResult<i32[-2147483648 2147483647]>.Ok(var value):
                                return value;
                            case Facade.CommonResult<i32[-2147483648 2147483647]>.Err(var error):
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "enum-layout"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);

            var concreteResultLayout = Assert.Single(
                enumLayoutModel.Layouts,
                static layout => layout.Key.StartsWith("Facade.CommonResult<", StringComparison.Ordinal)).Value;
            AssertCompactTag(concreteResultLayout, bitWidth: 8, maxTagValue: 1);
            Assert.Equal(["$tag", "$Ok_0", "$Err_0"], concreteResultLayout.OrderedFields.Select(static field => field.Name).ToArray());
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

    private static void AssertCompactTag(EnumLayoutSymbol layout, int bitWidth, int maxTagValue)
    {
        Assert.Equal("$tag", layout.TagField.Name);
        Assert.Equal(StarkTypeKind.Integer, layout.TagField.Type.Kind);
        Assert.Equal(bitWidth, layout.TagField.Type.BitWidth);
        Assert.Equal(System.Numerics.BigInteger.Zero, layout.TagField.Type.RangeMin);
        Assert.Equal(new System.Numerics.BigInteger(maxTagValue), layout.TagField.Type.RangeMax);
    }
}
