using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineTypeCheckTests
{
    [Fact]
    public void ManifestBackedGenericEnumsRecordTypeInstantiationTriggersWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-enum-trigger-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum IOResult<T> {
                    Ok(T),
                    Err(i32[-2147483648 2147483647]),
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[-2147483648 2147483647] Unwrap(Facade.IOResult<i32[-2147483648 2147483647]> result) {
                        switch (result) {
                            case Facade.IOResult<i32[-2147483648 2147483647]>.Ok(var value):
                                return value;
                            case Facade.IOResult<i32[-2147483648 2147483647]>.Err(var code):
                                return code;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.Contains(typeCheckModel.TypeTriggers, static trigger => trigger.TypeName == "Facade.IOResult<i32>");
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
    public void ManifestBackedGenericFunctionsRecordInstantiationTriggersWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-function-trigger-pipeline-");
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

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run() {
                        stack i32[-2147483648 2147483647] value = 4;
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);

            var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
            Assert.Equal("Facade.Identity", trigger.FunctionName);
            Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
            Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
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
    public void ManifestBackedStaticMemberFunctionsPreserveStaticAndFunctionKindContracts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-static-member-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Allocator {
                    i32[0 255] Tag;

                    static finite law Allocator Default() {
                        return new() { Tag = 0 };
                    }

                    finite law bool IsDefault(borrow Allocator self) {
                        return self.Tag == 0;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var allocator = Assert.Single(facadeModule.EffectiveTypedInterface!.Types, static type => type.QualifiedName == "Facade.Allocator");
            var defaultMethod = Assert.Single(allocator.Methods!, static method => method.Name == "Default");
            Assert.True(defaultMethod.IsStatic);
            Assert.Equal("finitelaw", defaultMethod.Kind);
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn Facade.Allocator Run() {
                        return Facade.Allocator.Default();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.Functions["Facade.Allocator.Default"].IsStatic);
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
    public void ManifestBackedMemberFunctionsPreserveVisibility()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-method-visibility-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[min max] Value;

                    public fn i32[min max] Visible(Box self) {
                        return self.Value;
                    }

                    internal fn i32[min max] Hidden(Box self) {
                        return self.Value;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedBox = Assert.Single(facadeModule.EffectiveTypedInterface!.Types, static type => type.QualifiedName == "Facade.Box");
            Assert.Equal("public", Assert.Single(typedBox.Methods!, static method => method.Name == "Visible").Visibility);
            Assert.Equal("internal", Assert.Single(typedBox.Methods!, static method => method.Name == "Hidden").Visibility);

            var sourceBox = Assert.Single(facadeModule.EffectiveSourceSurface.Types!, static type => type.QualifiedName == "Facade.Box");
            Assert.Equal("public", Assert.Single(sourceBox.Methods!, static method => method.Name == "Visible").Visibility);
            Assert.Equal("internal", Assert.Single(sourceBox.Methods!, static method => method.Name == "Hidden").Visibility);
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run() {
                        stack Facade.Box box = new Facade.Box() { Value = 1 };
                        return box.Hidden();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            var importedFacade = Assert.Single(loadedModules.Modules.Values, static module => module.SyntaxModel.ModuleName == "Facade");
            Assert.Equal(StarkVisibility.Internal, Assert.Single(importedFacade.SyntaxModel.Declarations, static declaration => declaration.Name == "Box.Hidden").Visibility);
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
    public void ManifestBackedGlobalsResolveFromPackageImageFactsWhenBridgeGlobalSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-global-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public const i32[-2147483648 2147483647] Answer = 42;
                public static mut i32[-2147483648 2147483647] Counter = 0;
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Answer", importedDocument.PackageImageFacts!.Globals.Keys);
            Assert.Contains("Facade.Counter", importedDocument.PackageImageFacts.Globals.Keys);

            var corruptedSourceText = importedDocument.ParseResult.SourceText
                .Replace(StrictIntegerSource("public const i32 Answer;"), "public const Missing Answer;", StringComparison.Ordinal)
                .Replace(StrictIntegerSource("public static mut i32 Counter;"), "public static mut Missing Counter;", StringComparison.Ordinal);
            Assert.NotEqual(importedDocument.ParseResult.SourceText, corruptedSourceText);

            var corruptedDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(corruptedSourceText)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run() {
                        return Facade.Answer;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.Equal("i32", typeCheckModel.Globals["Facade.Answer"].Type.DisplayName);
            Assert.True(typeCheckModel.Globals["Facade.Answer"].IsConst);
            Assert.Equal("i32", typeCheckModel.Globals["Facade.Counter"].Type.DisplayName);
            Assert.True(typeCheckModel.Globals["Facade.Counter"].IsMutable);
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
    public void ManifestBackedNamedTypeShapeResolvesFromPackageImageFactsWhenBridgeTypeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-type-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[-2147483648 2147483647] Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Box", importedDocument.PackageImageFacts!.NamedTypes.Keys);
            Assert.True(importedDocument.PackageImageFacts.NamedTypes["Facade.Box"].TryGetField("Value", out var field, out _));
            Assert.Equal("i32", field.Type.DisplayName);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                StrictIntegerSource("i32 Value;"),
                "Missing Wrong;",
                StringComparison.Ordinal);
            Assert.NotEqual(importedDocument.ParseResult.SourceText, corruptedSourceText);

            var corruptedDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(corruptedSourceText)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run() {
                        stack Facade.Box box = new Facade.Box() { Value = 3 };
                        return box.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.NamedTypes["Facade.Box"].TryGetField("Value", out var importedField, out _));
            Assert.Equal("i32", importedField.Type.DisplayName);
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
    public void ManifestBackedRecordPrimaryConstructorsResolveFromPackageImageFactsWhenBridgeTypeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-constructor-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32[-2147483648 2147483647] Value) {
                    i32[-2147483648 2147483647] Count;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Counter", importedDocument.PackageImageFacts!.Constructors.Keys);
            var primaryConstructor = Assert.Single(importedDocument.PackageImageFacts.Constructors["Facade.Counter"]);
            Assert.True(primaryConstructor.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(primaryConstructor.Parameters).Type.DisplayName);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                StrictIntegerSource("record Counter(i32 Value)"),
                "record Counter(Missing Value)",
                StringComparison.Ordinal);
            Assert.NotEqual(importedDocument.ParseResult.SourceText, corruptedSourceText);

            var corruptedDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(corruptedSourceText)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                        stack Facade.Counter counter = new Facade.Counter(value);
                        return counter.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            var objectCreation = Assert.Single(typeCheckModel.ObjectCreations);
            Assert.NotNull(objectCreation.Constructor);
            Assert.True(objectCreation.Constructor!.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(objectCreation.Constructor.Parameters).Type.DisplayName);
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
    public void ManifestBackedExplicitStructConstructorsResolveFromPackageImageFactsWithoutBridgeDeclarations()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-explicit-struct-constructors-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[-2147483648 2147483647] Value;

                    Box(i32[-2147483648 2147483647] value) {
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                manifestPath,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Box", importedDocument.PackageImageFacts!.Constructors.Keys);
            var explicitConstructor = Assert.Single(importedDocument.PackageImageFacts.Constructors["Facade.Box"]);
            Assert.False(explicitConstructor.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(explicitConstructor.Parameters).Type.DisplayName);
            Assert.DoesNotContain("Box(i32 value)", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run(i32[-2147483648 2147483647] value) {
                        stack Facade.Box box = new Facade.Box(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            var objectCreation = Assert.Single(typeCheckModel.ObjectCreations);
            Assert.NotNull(objectCreation.Constructor);
            Assert.False(objectCreation.Constructor!.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(objectCreation.Constructor.Parameters).Type.DisplayName);
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
    public void ManifestBackedExplicitRecordConstructorsResolveFromPackageImageFactsWithoutBridgeDeclarations()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-explicit-record-constructors-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32[-2147483648 2147483647] Value) {
                    i32[-2147483648 2147483647] Count;

                    Counter(i32[-2147483648 2147483647] value, i32[-2147483648 2147483647] count) {
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                manifestPath,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Counter", importedDocument.PackageImageFacts!.Constructors.Keys);
            Assert.Collection(
                importedDocument.PackageImageFacts.Constructors["Facade.Counter"],
                primaryConstructor =>
                {
                    Assert.True(primaryConstructor.IsPrimaryShape);
                    Assert.Equal("i32", Assert.Single(primaryConstructor.Parameters).Type.DisplayName);
                },
                explicitConstructor =>
                {
                    Assert.False(explicitConstructor.IsPrimaryShape);
                    Assert.Equal(2, explicitConstructor.Parameters.Count);
                    Assert.All(explicitConstructor.Parameters, static parameter => Assert.Equal("i32", parameter.Type.DisplayName));
                });
            Assert.DoesNotContain("Counter(i32 value, i32 count)", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                        stack Facade.Counter counter = new Facade.Counter(value, 7);
                        return counter.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            var objectCreation = Assert.Single(typeCheckModel.ObjectCreations);
            Assert.NotNull(objectCreation.Constructor);
            Assert.False(objectCreation.Constructor!.IsPrimaryShape);
            Assert.Equal(2, objectCreation.Constructor.Parameters.Count);
            Assert.All(objectCreation.Constructor.Parameters, static parameter => Assert.Equal("i32", parameter.Type.DisplayName));
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
