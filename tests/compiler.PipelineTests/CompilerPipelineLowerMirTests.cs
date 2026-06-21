using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineLowerMirTests
{
    private static bool IsDirectCallStatement(MidLevelIrStatement statement, string functionName)
    {
        return statement.Value is MidLevelIrCallRValue directValueCall
                   && string.Equals(directValueCall.FunctionName, functionName, StringComparison.Ordinal)
               || statement.Call is MidLevelIrDirectCallStatementOperation directStatementCall
                   && string.Equals(directStatementCall.FunctionName, functionName, StringComparison.Ordinal);
    }

    private static IEnumerable<string> DirectCallNames(MidLevelIrFunction function)
    {
        foreach (var statement in function.Blocks.SelectMany(static block => block.Statements))
        {
            if (statement.Value is MidLevelIrCallRValue directValueCall)
            {
                yield return directValueCall.FunctionName;
            }
            else if (statement.Call is MidLevelIrDirectCallStatementOperation directStatementCall)
            {
                yield return directStatementCall.FunctionName;
            }
        }
    }

    private static IEnumerable<MidLevelIrCallRValue> DirectValueCalls(MidLevelIrFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>();
    }

    private static IEnumerable<MidLevelIrDirectCallStatementOperation> DirectStatementCalls(MidLevelIrFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Call)
            .OfType<MidLevelIrDirectCallStatementOperation>();
    }

    [Fact]
    public void ExplicitConstructorBodiesLowerIntoObjectCreation()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;

                    Box()
                    {
                        self.Value = 41;
                    }

                    Box(i32[min max] value)
                    {
                        self.Value = value + 1;
                    }
                }

                fn i32[min max] Run(i32[min max] value)
                {
                    stack Box defaultBox = new();
                    stack Box constructedBox = new(value);
                    return defaultBox.Value + constructedBox.Value;
                }
                """),
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);

        var run = Assert.Single(mir.Functions, static function => function.Name == "Run");
        Assert.True(run.SupportsDirectCodeGeneration);

        var statements = run.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.True(
            statements.Count(static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "Value" }) >= 2,
            string.Join(Environment.NewLine, statements.Select(static statement => statement.Text)));

        var constructions = statements
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrUseRValue>()
            .Select(static use => use.Operand)
            .OfType<MidLevelIrObjectConstructionOperand>()
            .ToArray();
        Assert.Equal(2, constructions.Length);
        Assert.All(constructions, construction =>
        {
            Assert.Equal(MidLevelIrObjectConstructionKind.ExplicitConstructor, construction.Facts.Kind);
            Assert.NotNull(construction.Facts.Constructor);
            Assert.False(string.IsNullOrWhiteSpace(construction.Facts.ConstructorBodyKey));
        });
    }

    [Fact]
    public void ManifestBackedTypedTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    stack T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack T copy = value;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return copy;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run()
                    {
                        stack i32[min max] value = 7;
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var identity = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(identity.HasBody);
            Assert.True(identity.SupportsDirectCodeGeneration);
            Assert.Contains(identity.Locals, static local => local.Name == "copy");
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
    public void ManifestBackedTypedTemplateBodiesLowerFromPackageImageFactsWhenImportedDeclarationSyntaxIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-body-corrupted-declaration-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32[min max] Dummy)
                {
                    fn T Echo<T>(borrow Box self, T value)
                    {
                        return value;
                    }
                }

                public fn T Relay<T>(Box box, T value)
                {
                    return box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

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
            Assert.Contains("Facade.Relay", importedDocument.PackageImageFacts!.FunctionTemplates.Keys);
            Assert.Contains("Facade.Box.Echo", importedDocument.PackageImageFacts.FunctionTemplates.Keys);

            var corruptedSourceText = importedDocument.ParseResult.SourceText
                .Replace(
                    "fn T Echo<T>(borrow Box self, T value);",
                    "fn T Broken<T>(borrow Box self, T value);",
                    StringComparison.Ordinal)
                .Replace(
                    "public fn T Relay<T>(Box box, T value);",
                    "public fn T BrokenRelay<T>(Box box, T value);",
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

                    fn i32[min max] Run(Facade.Box box, i32[min max] value)
                    {
                        stack i32[min max] echoed = Facade.Relay(box, value);
                        return echoed;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var relay = Assert.Single(
                mir.Functions,
                static function => function.Name == "__stark_mono_fn_Demo__Facade_Relay__i32");
            Assert.True(relay.HasBody);
            Assert.True(relay.SupportsDirectCodeGeneration);

            var echo = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Facade_Box_Echo", StringComparison.Ordinal));
            Assert.True(echo.HasBody);
            Assert.True(echo.SupportsDirectCodeGeneration);
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
    public void ManifestBackedTypedTemplateBodiesLowerFromPackageImageFactsWhenImportedParseTreeIsEmpty()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-body-empty-parse-tree-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32[min max] Dummy)
                {
                    fn T Echo<T>(borrow Box self, T value)
                    {
                        return value;
                    }
                }

                public fn T Relay<T>(Box box, T value)
                {
                    return box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

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
            Assert.True(importedDocument.PackageImageFacts!.HasPublishedTypedTemplateBodies);

            var emptyParseDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(
                    """
                    module Facade
                    """)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(Facade.Box box, i32[min max] value)
                    {
                        stack i32[min max] echoed = Facade.Relay(box, value);
                        return echoed;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new DocumentOnlyModuleResolver(emptyParseDocument),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var relay = Assert.Single(
                mir.Functions,
                static function => function.Name == "__stark_mono_fn_Demo__Facade_Relay__i32");
            Assert.True(relay.HasBody);
            Assert.True(relay.SupportsDirectCodeGeneration);

            var echo = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Facade_Box_Echo", StringComparison.Ordinal));
            Assert.True(echo.HasBody);
            Assert.True(echo.SupportsDirectCodeGeneration);
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
    public void ManifestBackedTypedGroupedLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-grouped-local-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] SumTo<T>(i32[min max] limit, T tag)
                {
                    stack mut i32[min max] total = 0, stop = limit;
                    for willexit (stack mut i32[min max] index = 0, max = stop; index < max; index += 1)
                    {
                        total += index;
                    }

                    return total;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 SumTo<T>(i32 limit, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack mut i32 total = 0, stop = limit;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0, max = stop;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] limit)
                    {
                        stack i32[min max] tag = 0;
                        return Facade.SumTo(limit, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumTo = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SumTo__i32");
            Assert.True(sumTo.HasBody);
            Assert.True(sumTo.SupportsDirectCodeGeneration);
            Assert.Contains(sumTo.Locals, static local => local.Name == "total");
            Assert.Contains(sumTo.Locals, static local => local.Name == "stop");
            Assert.Contains(sumTo.Locals, static local => local.Name == "index");
            Assert.Contains(sumTo.Locals, static local => local.Name == "max");
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
    public void ManifestBackedTypedUninitializedLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-uninitialized-local-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Observe<T>(i32[min max] value, T tag)
                {
                    stack mut i32[min max] current;
                    current = value;
                    return current;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 Observe<T>(i32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack mut i32 current;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("current = value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "current");
            Assert.Contains(
                observe.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text == "current = value");
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
    public void ManifestBackedTypedDiscardedExpressionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-expression-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Observe<T>(i32[min max] value, T tag)
                {
                    value + 1;
                    return value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 Observe<T>(i32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("value + 1;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(
                observe.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Evaluate
                    && statement.Text == "value + 1");
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
    public void ManifestBackedTypedConversionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-conversion-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] TruncateTyped<T>(f32 value, T tag)
                {
                    return (i32[min max])value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 TruncateTyped<T>(f32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return (i32)value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(f32 value)
                    {
                        return Facade.TruncateTyped(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var truncate = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_TruncateTyped__", StringComparison.Ordinal));
            Assert.True(truncate.HasBody);
            Assert.True(truncate.SupportsDirectCodeGeneration);
            Assert.Contains(
                truncate.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrConvertRValue
                {
                    TargetType.Kind: StarkTypeKind.Integer,
                    TargetType.BitWidth: 32
                });
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
    public void ManifestBackedTypedAssignmentTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-assignment-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] AddViaAssign<T>(T tag, i32[min max] left, i32[min max] right)
                {
                    stack mut i32[min max] sum = left;
                    sum = sum + right;
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 AddViaAssign<T>(T tag, i32 left, i32 right);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack mut i32 sum = left;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("sum = sum + right;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
                        return Facade.AddViaAssign(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var addViaAssign = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_AddViaAssign__", StringComparison.Ordinal));
            Assert.True(addViaAssign.HasBody);
            Assert.True(addViaAssign.SupportsDirectCodeGeneration);
            Assert.Contains(addViaAssign.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.True(
                addViaAssign.Blocks.SelectMany(static block => block.Statements)
                    .Count(static statement => statement.TargetName == "sum" && statement.Kind == MidLevelIrStatementKind.Assign) >= 2);
            Assert.Contains(
                addViaAssign.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
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
    public void ManifestBackedTypedIfTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-if-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] ChooseBranch<T>(bool takeLeft, i32[min max] left, i32[min max] right, T tag)
                {
                    stack mut i32[min max] result = 0;
                    if (takeLeft)
                    {
                        result = left;
                    }
                    else
                    {
                        result = right;
                    }
                    return result;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 ChooseBranch<T>(bool takeLeft, i32 left, i32 right, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("if (takeLeft)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("result = left;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("result = right;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(bool takeLeft, i32[min max] left, i32[min max] right)
                    {
                        return Facade.ChooseBranch(takeLeft, left, right, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var chooseBranch = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_ChooseBranch__", StringComparison.Ordinal));
            Assert.True(chooseBranch.HasBody);
            Assert.True(chooseBranch.SupportsDirectCodeGeneration);
            Assert.Contains(chooseBranch.Locals, static local => local.Name == "result" && local.IsMutable);
            Assert.Contains(chooseBranch.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                chooseBranch.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrUseRValue
                {
                    Operand: MidLevelIrIntegerConstantOperand { Value: var integerValue }
                } && integerValue == System.Numerics.BigInteger.Zero
                    || value is MidLevelIrConvertRValue
                    {
                        Operand: MidLevelIrIntegerConstantOperand { Value: var convertedIntegerValue }
                    } && convertedIntegerValue == System.Numerics.BigInteger.Zero);
            Assert.Contains(
                chooseBranch.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "result");
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
    public void ManifestBackedTypedTerminalIfTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-terminal-if-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] ChooseTerminal<T>(bool takeLeft, bool takeMiddle, i32[min max] left, i32[min max] middle, i32[min max] right, T tag)
                {
                    if (takeLeft)
                    {
                        return left;
                    }
                    else if (takeMiddle)
                    {
                        return middle;
                    }
                    else
                    {
                        return right;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var chooseTerminal = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ChooseTerminal");
            Assert.Null(chooseTerminal.BodyText);
            Assert.NotNull(chooseTerminal.TypedBody);

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 ChooseTerminal<T>(bool takeLeft, bool takeMiddle, i32 left, i32 middle, i32 right, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("if (takeLeft)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return left;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return middle;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return right;", sourceText, StringComparison.Ordinal);

            var corruptedTemplate = chooseTerminal with
            {
                BodyText = "{ return 0; }"
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(bool takeLeft, bool takeMiddle, i32[min max] left, i32[min max] middle, i32[min max] right)
                    {
                        return Facade.ChooseTerminal(takeLeft, takeMiddle, left, middle, right, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var chooseBranch = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_ChooseTerminal__", StringComparison.Ordinal));
            Assert.True(chooseBranch.HasBody);
            Assert.True(chooseBranch.SupportsDirectCodeGeneration);
            Assert.Contains(chooseBranch.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.True(
                chooseBranch.Blocks.Count(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Return) >= 3);
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
    public void ManifestBackedTypedWhileTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-while-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] SumTo<T>(i32[min max] count, T tag)
                {
                    stack mut i32[min max] index = 0;
                    stack mut i32[min max] sum = 0;
                    while willexit (index < count)
                    {
                        sum = sum + index;
                        index = index + 1;
                    }
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 SumTo<T>(i32 count, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("while willexit (index < count)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("sum = sum + index;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("index = index + 1;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] count)
                    {
                        return Facade.SumTo(count, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumTo = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_SumTo__", StringComparison.Ordinal));
            Assert.True(sumTo.HasBody);
            Assert.True(sumTo.SupportsDirectCodeGeneration);
            Assert.Contains(sumTo.Locals, static local => local.Name == "index" && local.IsMutable);
            Assert.Contains(sumTo.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.Contains(sumTo.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.LessThan }
                    || value is MidLevelIrUseRValue { Operand: MidLevelIrLocalOperand { Name: var name } }
                        && name.Contains("bin", StringComparison.Ordinal));
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "sum");
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "index");
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
    public void ManifestBackedTypedPatternConditionTemplateBodiesPublishAndImportForGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-pattern-condition-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Step<T>
                {
                    More(i32[min max]),
                    Done
                }

                public fn i32[min max] Classify<T>(Step<T> step, T tag)
                {
                    if (step is Step<T>.More(var first))
                    {
                        return first;
                    }

                    while willexit (step is Step<T>.More(var value))
                    {
                        return value;
                    }

                    return 0;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var classifyTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Classify");
            Assert.Null(classifyTemplate.BodyText);
            Assert.NotNull(classifyTemplate.TypedBody);
            Assert.Contains(classifyTemplate.TypedBody!.Statements, static statement => statement.Kind == "if" && statement.ConditionPattern is not null);
            Assert.Contains(classifyTemplate.TypedBody!.Statements, static statement => statement.Kind == "while" && statement.ConditionPattern is not null);

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.EffectiveGenericTemplates!.Functions
                                    .Select(template => template with
                                    {
                                        BodyText = "{ return this is not valid Stark; }"
                                    })
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };
            var typedOnlyFacadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedOnlyFacadeModule),
                    out var sourceText));

            Assert.Contains("Classify<T>", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("if (step is", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("while willexit (step is", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run()
                    {
                        return Facade.Classify(Facade.Step<i32[min max]>.More(7), 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var classify = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_Classify__", StringComparison.Ordinal));
            Assert.True(classify.HasBody);
            Assert.True(classify.SupportsDirectCodeGeneration);
            Assert.Contains(classify.Locals, static local => local.Name == "first");
            Assert.Contains(classify.Locals, static local => local.Name == "value");
            Assert.Contains(classify.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
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
    public void ManifestBackedTypedForTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-for-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] SumFor<T>(i32[min max] count, T tag)
                {
                    stack mut i32[min max] sum = 0;
                    for willexit (stack mut i32[min max] index = 0; index < count; index = index + 1)
                    {
                        sum = sum + index;
                    }
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 SumFor<T>(i32 count, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0; index < count; index = index + 1)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("sum = sum + index;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] count)
                    {
                        return Facade.SumFor(count, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumFor = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_SumFor__", StringComparison.Ordinal));
            Assert.True(sumFor.HasBody);
            Assert.True(sumFor.SupportsDirectCodeGeneration);
            Assert.Contains(sumFor.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.Contains(sumFor.Locals, static local => local.Name == "index" && local.IsMutable);
            Assert.Contains(sumFor.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.LessThan });
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "sum");
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "index");
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
    public void ManifestBackedTypedLoopControlTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-loop-control-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] SumForControl<T>(i32[min max] count, i32[min max] stopAt, T tag)
                {
                    stack mut i32[min max] sum = 0;
                    for willexit (stack mut i32[min max] index = 0; index < count; index = index + 1)
                    {
                        if (index < 2)
                        {
                            continue;
                        }
                        if (index == stopAt)
                        {
                            break;
                        }
                        sum = sum + index;
                    }
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("continue;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("break;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0; index < count; index = index + 1)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] count, i32[min max] stopAt)
                    {
                        return Facade.SumForControl(count, stopAt, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumForControl = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_SumForControl__", StringComparison.Ordinal));
            Assert.True(sumForControl.HasBody);
            Assert.True(sumForControl.SupportsDirectCodeGeneration);
            Assert.Contains(sumForControl.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.Contains(sumForControl.Locals, static local => local.Name == "index" && local.IsMutable);
            Assert.Contains(sumForControl.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(sumForControl.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Goto);
            Assert.Contains(
                sumForControl.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Equal });
            Assert.Contains(
                sumForControl.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "sum");
            Assert.Contains(
                sumForControl.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "index");
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
    public void ManifestBackedTypedGenericMethodBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-method-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32[min max] Dummy)
                {
                    fn T Echo<T>(borrow Box self, T value)
                    {
                        stack T copy = value;
                        return copy;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("fn T Echo<T>(borrow Box self, T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack T copy = value;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return copy;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Box box = new Facade.Box(1);
                        return box.Echo(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Echo", StringComparison.Ordinal)
                    && function.Name.StartsWith("__stark_mono_fn_Demo__", StringComparison.Ordinal));
            Assert.True(specialized.HasBody);
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy");
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "copy");

            var run = Assert.Single(mir.Functions, static function => function.Name == "Run");
            Assert.Contains(
                run.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                value => value is MidLevelIrCallRValue call
                    && string.Equals(call.FunctionName, specialized.Name, StringComparison.Ordinal));
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
    public void ManifestBackedTypedTemplateBodiesForMethodsOnGenericTypesLowerWithoutBridgeBodyText()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-generic-type-method-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T>
                {
                    T Value;

                    fn T Echo(borrow Box<T> self, T fallback)
                    {
                        return self.Value;
                    }
                }

                public fn T Relay<T>(Box<T> box, T fallback)
                {
                    return box.Echo(fallback);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("fn T Echo(borrow Box<T> self, T fallback);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return self.Value;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return box.Echo(fallback);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Box<i32[min max]> box = new Facade.Box<i32[min max]>()
                        {
                            Value = value
                        };
                        return Facade.Relay(box, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var relay = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Relay__i32");
            Assert.True(relay.HasBody);
            Assert.True(relay.SupportsDirectCodeGeneration);

            var echo = Assert.Single(mir.Functions, static function => function.Name.Contains("Facade_Box_Echo", StringComparison.Ordinal));
            Assert.True(echo.HasBody);
            Assert.True(echo.SupportsDirectCodeGeneration);
            Assert.Contains(
                echo.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Value" });
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
    public void ManifestBackedTypedTemplateBodiesPreferStructuredBodyOverLegacySourceSurfaceBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-prefer-typed-generic-method-body-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Rows<T>
                {
                    u64[0 2 ** 63 - 1] Length;

                    Rows()
                    {
                        self.Length = 0;
                    }

                    public inline finite law u64[0 2 ** 63 - 1] Count(borrow Rows<T> self)
                    {
                        return self.Length;
                    }
                }

                public struct Table<T>
                {
                    Rows<T> Rows;

                    Table()
                    {
                        self.Rows = new();
                    }

                    public inline finite law u64[0 2 ** 63 - 1] Count(borrow Table<T> self)
                    {
                        return self.Rows.Count();
                    }
                }

                public inline finite law u64[0 2 ** 63 - 1] RelayCount<T>(borrow Table<T> table)
                {
                    return table.Count();
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var countTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Table.Count");
            Assert.NotNull(countTemplate.TypedBody);
            var countTemplateWithLegacyBody = countTemplate with
            {
                BodyText = "{ return self.Rows.Count(); }"
            };
            var genericTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(template => template.QualifiedResolvedName == countTemplateWithLegacyBody.QualifiedResolvedName
                        ? countTemplateWithLegacyBody
                        : template)
                    .ToArray());
            var manifestWithLegacyBody = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            GenericTemplates = genericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: genericTemplates)
                        }
                        : module)
                    .ToArray()
            };
            var facadeModuleWithLegacyBody = Assert.Single(
                manifestWithLegacyBody.Modules,
                static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, manifestWithLegacyBody, facadeModuleWithLegacyBody),
                    out var sourceText));
            Assert.Contains("Rows.Count", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifestWithLegacyBody.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn u64[0 2 ** 63 - 1] Run()
                    {
                        stack Facade.Table<i32[min max]> table = new Facade.Table<i32[min max]>();
                        return Facade.RelayCount(table);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var count = Assert.Single(mir.Functions, static function => function.Name.Contains("Facade_Table_Count", StringComparison.Ordinal));
            Assert.True(count.HasBody);
            Assert.True(count.SupportsDirectCodeGeneration);
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
    public void ManifestBackedTypedComparisonChainTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-comparison-chain-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] NextOrdered()
                {
                    return 1;
                }

                public fn i32[min max] NextEquality()
                {
                    return 1;
                }

                public fn bool ObserveOrdered<T>(T tag)
                {
                    return 0 < NextOrdered() < 3;
                }

                public fn bool ObserveEquality<T>(T tag)
                {
                    return 1 == NextEquality() == 1;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn bool ObserveOrdered<T>(T tag);", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn bool ObserveEquality<T>(T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return 0 < NextOrdered() < 3;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return 1 == NextEquality() == 1;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn bool RunOrdered()
                    {
                        stack i32[min max] tag = 0;
                        return Facade.ObserveOrdered(tag);
                    }

                    fn bool RunEquality()
                    {
                        stack i32[min max] tag = 0;
                        return Facade.ObserveEquality(tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var ordered = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ObserveOrdered__i32");
            Assert.True(ordered.HasBody);
            Assert.True(ordered.SupportsDirectCodeGeneration);
            Assert.Equal(
                1,
                ordered.Blocks
                    .SelectMany(static block => block.Statements)
                    .Count(static statement => statement.Value is MidLevelIrCallRValue));
            Assert.True(ordered.Blocks.Count(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch) >= 1);
            Assert.Equal(
                2,
                ordered.Blocks
                    .SelectMany(static block => block.Statements)
                    .Count(static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.LessThan }));

            var equality = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ObserveEquality__i32");
            Assert.True(equality.HasBody);
            Assert.True(equality.SupportsDirectCodeGeneration);
            Assert.Equal(
                1,
                equality.Blocks
                    .SelectMany(static block => block.Statements)
                    .Count(static statement => statement.Value is MidLevelIrCallRValue));
            Assert.True(equality.Blocks.Count(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch) >= 1);
            Assert.Equal(
                2,
                equality.Blocks
                    .SelectMany(static block => block.Statements)
                    .Count(static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Equal }));
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
    public void ManifestBackedTypedRawPointerDereferenceTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-raw-pointer-deref-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn i32[min max] Observe<T>(rawmutptr<i32[min max]> ptr, i32[min max] value, T tag)
                {
                    stack mut i32[min max] copy = *ptr;
                    return *ptr += copy + value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public unsafe fn i32 Observe<T>(rawmutptr<i32> ptr, i32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return *ptr += copy + value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run(i32[min max] value)
                    {
                        stack mut i32[min max] current = 5;
                        stack i32[min max] tag = 0;
                        return Facade.Observe(&current, value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "copy");
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(observeStatements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
            Assert.Contains(
                observeStatements.Select(static statement => statement.Value),
                static value => value is MidLevelIrLoadIndirectRValue);
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
    public void ManifestBackedTypedProjectedRawPointerDereferenceTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-projected-raw-pointer-deref-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32[min max] First, i32[min max][4] Values)
                {
                }

                public unsafe fn rawmutptr<Buffer> Pick<T>(rawmutptr<Buffer> ptr, T tag)
                {
                    return ptr;
                }

                public unsafe fn i32[min max] Observe<T>(rawmutptr<Buffer> ptr, i32[min max] slot, i32[min max] value, T tag)
                {
                    (*ptr).First += value;
                    return (*Pick(ptr, tag)).Values[slot] = (*ptr).First + value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public unsafe fn i32 Observe<T>(rawmutptr<Buffer> ptr, i32 slot, i32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("(*ptr).First += value;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("(*Pick(ptr, tag)).Values[slot] = (*ptr).First + value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run(i32[min max] value)
                    {
                        stack mut i32[min max][4] values =
                        {
                            10, 20, 30, 40
                        };
                        stack mut Facade.Buffer buffer =
                        {
                            First = 5, Values = values
                        };
                        stack i32[min max] tag = 0;
                        return Facade.Observe(&buffer, 2, value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.True(observeStatements.Count(static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect) >= 2);
            Assert.Contains(observeStatements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
            Assert.Contains(observeStatements, static statement => statement.Value is MidLevelIrElementAddressRValue);
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
    public void ManifestBackedTypedAddressOfTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-address-of-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32[min max] First, i32[min max][4] Values)
                {
                }

                public unsafe fn i32[min max] Observe<T>(i32[min max] value, T tag)
                {
                    stack mut i32[min max][4] data =
                    {
                        1, 2, 3, 4
                    };
                    stack mut Buffer buffer =
                    {
                        First = value, Values = data
                    };
                    stack rawmutptr<i32[min max]> firstPtr = &buffer.First;
                    stack rawmutptr<i32[min max]> slotPtr = &buffer.Values[2];
                    stack rawmutptr<i32[min max]> aliasPtr = &*slotPtr;
                    return *aliasPtr = *firstPtr + value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public unsafe fn i32 Observe<T>(i32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack rawmutptr<i32> firstPtr = &buffer.First;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack rawmutptr<i32> slotPtr = &buffer.Values[2];", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] tag = 0;
                        unsafe
                        {
                            return Facade.Observe(value, tag);
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "firstPtr");
            Assert.Contains(observe.Locals, static local => local.Name == "slotPtr");
            Assert.Contains(observe.Locals, static local => local.Name == "aliasPtr");
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(observeStatements, static statement => statement.Value is MidLevelIrAddressOfLocalRValue);
            Assert.Contains(observeStatements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
            Assert.Contains(observeStatements, static statement => statement.Value is MidLevelIrElementAddressRValue);
            Assert.Contains(observeStatements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
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
    public void ManifestBackedTypedPowerTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-power-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Observe<T>(i32[min max] value, i32[min max] exponent, T tag)
                {
                    return value ** exponent;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 Observe<T>(i32 value, i32 exponent, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return value ** exponent;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value, i32[min max] exponent)
                    {
                        stack i32[min max] tag = 0;
                        return Facade.Observe(value, exponent, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(
                observe.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Exponent });
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
    public void ManifestBackedTypedAssignmentExpressionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-assignment-expression-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Observe<T>(i32[min max] value, T tag)
                {
                    stack mut i32[min max] current = 1;
                    return current += value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 Observe<T>(i32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return current += value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "current");
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(
                observeStatements,
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text == "current += value");
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
    public void ManifestBackedTypedObjectInitializerLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-object-initializer-local-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair(i32[min max] First, i32[min max] Second)
                {
                }

                public fn i32[min max] Observe<T>(i32[min max] value, T tag)
                {
                    stack Pair pair =
                    {
                        First = value, Second = value + 1
                    };
                    return pair.First + pair.Second;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public fn i32 Observe<T>(i32 value, T tag);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack Pair pair = { First = value, Second = value + 1 };", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "pair");
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(
                observeStatements,
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text.Contains("First", StringComparison.Ordinal));
            Assert.Contains(
                observeStatements,
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text.Contains("Second", StringComparison.Ordinal));
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
    public void ManifestBackedEmptyBlockAndOpenEndedLoopTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-empty-block-loop-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn void NoOp<T>(T tag)
                {
                }

                public fn i32[min max] KeepValue<T>(i32[min max] value, T tag)
                {
                    ;
                    return value;
                }

                public fn i32[min max] NestedScope<T>(i32[min max] value, T tag)
                {
                    {
                        ;
                    }

                    return value;
                }

                public fn i32[min max] CountTo<T>(i32[min max] count, T tag)
                {
                    stack mut i32[min max] index = 0;
                    for willexit (;;)
                    {
                        if (index == count)
                        {
                            break;
                        }

                        index = index + 1;
                    }

                    return index;
                }

                public fn i32[min max] EmptySwitch<T>(i32[min max] value, T tag)
                {
                    switch (value)
                    {
                        case 0:
                        default:
                    }

                    return value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn void NoOp<T>(T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (;;)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("switch (value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] count, i32[min max] value)
                    {
                        Facade.NoOp(value);
                        stack i32[min max] kept = Facade.KeepValue(value, value);
                        stack i32[min max] scoped = Facade.NestedScope(value, value);
                        stack i32[min max] counted = Facade.CountTo(count, value);
                        stack i32[min max] switched = Facade.EmptySwitch(value, value);
                        return kept + scoped + counted + switched;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            Assert.All(
                new[]
                {
                    "__stark_mono_fn_Demo__Facade_NoOp__i32",
                    "__stark_mono_fn_Demo__Facade_KeepValue__i32",
                    "__stark_mono_fn_Demo__Facade_NestedScope__i32",
                    "__stark_mono_fn_Demo__Facade_CountTo__i32",
                    "__stark_mono_fn_Demo__Facade_EmptySwitch__i32"
                },
                functionName =>
                {
                    var function = Assert.Single(mir.Functions, candidate => candidate.Name == functionName);
                    Assert.True(function.HasBody);
                    Assert.True(function.SupportsDirectCodeGeneration);
                });
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
    public void ManifestBackedGenericBodiesCanConstructImportedPrimaryConstructorTypes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-imported-primary-ctor-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32[min max] Value)
                {
                }

                public fn i32[min max] MakeFlag<T>(T value)
                {
                    stack Counter counter = new Counter(1);
                    return counter.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.MakeFlag(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_MakeFlag__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value" });
            var construction = Assert.Single(specialized.Blocks
                .SelectMany(static block => block.Statements)
                .Select(static statement => statement.Value)
                .OfType<MidLevelIrUseRValue>()
                .Select(static use => use.Operand)
                .Concat(specialized.Blocks.Select(static block => block.Terminator.Value).OfType<MidLevelIrOperand>())
                .OfType<MidLevelIrObjectConstructionOperand>());
            Assert.Equal(MidLevelIrObjectConstructionKind.PrimaryConstructor, construction.Facts.Kind);
            Assert.NotNull(construction.Facts.Constructor);
            Assert.True(construction.Facts.Constructor!.IsPrimaryShape);
            Assert.Equal(StarkTypeKind.Integer, construction.Facts.Constructor.Parameters[0].Type.Kind);
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
    public void ManifestBackedGenericBodiesUsePublishedLocalDeclarationTypesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-local-type-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    stack T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Identity") with
            {
                BodyText = """
                    {
                        stack Missing copy = value;
                        return copy;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            var typedOnlyFacadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedOnlyFacadeModule),
                    out var sourceText));
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack Missing copy = value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
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
    public void ManifestBackedGenericOwnershipFactsAreSubstitutedForImportedTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-ownership-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    stack T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var genericType = new StarkPackageTypeReference("named", Name: "T");
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static candidate => candidate.QualifiedResolvedName == "Facade.Identity");
            Assert.NotNull(template.Semantics);
            var templateWithOwnership = template with
            {
                BodyText = """
                    {
                        stack Missing copy = value;
                        return copy;
                    }
                    """,
                Semantics = template.Semantics! with
                {
                    Ownership = new StarkPackageFunctionOwnershipManifest(
                        OwnershipValid: true,
                        ImplicitDrops: ["copy"],
                        Moves: ["copy"],
                        Events:
                        [
                            new StarkPackageOwnershipEventManifest(
                                "move",
                                new StarkPackageOwnershipPlaceManifest("copy", genericType))
                        ],
                        Roots:
                        [
                            new StarkPackageOwnershipRootManifest(
                                "copy",
                                genericType,
                                "local",
                                IsMutable: false,
                                IsConstant: false,
                                IsAddressTaken: false,
                                HasRawPointerEscape: false,
                                HasMove: true,
                                HasPartialMove: false,
                                HasImplicitDrop: true,
                                HasAssignmentDrop: false,
                                HasReinitialization: false,
                                RequiresDrop: true,
                                FinalAvailability: "initialized")
                        ])
                }
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(candidate => candidate.QualifiedResolvedName == templateWithOwnership.QualifiedResolvedName
                                        ? templateWithOwnership
                                        : candidate)
                                    .ToArray()),
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: new StarkPackageGenericTemplateSection(
                                    module.EffectiveGenericTemplates!.Functions
                                        .Select(candidate => candidate.QualifiedResolvedName == templateWithOwnership.QualifiedResolvedName
                                            ? templateWithOwnership
                                            : candidate)
                                        .ToArray()))
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.NotNull(specialized.Ownership);
            var root = Assert.Single(specialized.Ownership!.Roots, static candidate => candidate.Name == "copy");
            Assert.Equal("i32", root.Type.DisplayName);
            Assert.Contains(
                specialized.Ownership.Events,
                static ev => ev.Kind == OwnershipEventKind.Move
                             && ev.Place.RootName == "copy"
                             && ev.Place.Type.DisplayName == "i32");
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
    public void ManifestBackedGenericBodiesPreferTypedTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    stack T copy = value;
                    return copy;
                }

                public fn T Forward<T>(T value)
                {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplates = facadeModule.GenericTemplates!.Functions
                .Select(template => template.QualifiedResolvedName switch
                {
                    "Facade.Identity" => template with
                    {
                        BodyText = """
                            {
                                return value;
                            }
                            """
                    },
                    "Facade.Forward" => template with
                    {
                        BodyText = """
                            {
                                return value;
                            }
                            """
                    },
                    _ => template
                })
                .ToArray();

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(corruptedTemplates)
                        }
                        : module)
                    .ToArray()
            };

            var typedOnlyFacadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedOnlyFacadeModule),
                    out var sourceText));
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn T Forward<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] identity = Facade.Identity(value);
                        return Facade.Forward(identity);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var identity = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(identity.SupportsDirectCodeGeneration);
            Assert.Contains(identity.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");

            var forward = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(forward.SupportsDirectCodeGeneration);
            Assert.Contains(
                forward.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "__stark_mono_fn_Demo__Facade_Identity__i32" });
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
    public void ManifestBackedGenericBodiesPreferTypedConstTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-const-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T ConstIdentity<T>(T value)
                {
                    const T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ConstIdentity") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.ConstIdentity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var constIdentity = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_ConstIdentity__", StringComparison.Ordinal));
            Assert.True(constIdentity.SupportsDirectCodeGeneration);
            Assert.Contains(
                constIdentity.Locals,
                static local => local.Name == "copy"
                    && local.Type.DisplayName == "i32"
                    && local.IsConstant);
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
    public void ManifestBackedGenericBodiesPreserveConstProvenanceLocalsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-const-provenance-local-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box
                {
                    i32[min max] Value;
                }

                public fn i32[min max] Forward<T>(const Box box, T tag)
                {
                    stack frozen Box local = box;
                    return local.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward");
            var localStatement = Assert.Single(template.TypedBody!.Statements, static statement => statement.Name == "local");
            Assert.Equal("permanent-const", localStatement.ConstProvenance);

            var corruptedTemplate = template with
            {
                BodyText = """
                    {
                        return Missing(ptr);
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates.Functions
                                    .Select(candidate => candidate.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : candidate)
                                    .ToArray())
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

                    fn i32[min max] Run(const Facade.Box box)
                    {
                        stack i32[min max] tag = 0;
                        return Facade.Forward(box, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var forward = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_Forward__", StringComparison.Ordinal));
            var local = Assert.Single(forward.Locals, static local => local.Name == "local");
            Assert.True(local.HasConstProvenance);
            Assert.Equal(ConstProvenanceKind.PermanentConst, local.ConstProvenance);
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
    public void ManifestBackedGenericBodiesPreferTypedMultiLocalTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-multi-local-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    return value;
                }

                public fn T Relay<T>(T value)
                {
                    stack T copy = value;
                    stack T echoed = Identity(copy);
                    return echoed;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Relay") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Relay(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var relay = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_Relay__", StringComparison.Ordinal));
            Assert.True(relay.SupportsDirectCodeGeneration);
            Assert.Contains(relay.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
            Assert.Contains(relay.Locals, static local => local.Name == "echoed" && local.Type.DisplayName == "i32");
            Assert.Contains(
                relay.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "__stark_mono_fn_Demo__Facade_Identity__i32" });
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
    public void ManifestBackedGenericBodiesPreferTypedConditionalTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-conditional-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Choose<T>(bool takeLeft, T left, T right)
                {
                    return takeLeft ? left : right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Choose") with
            {
                BodyText = """
                    {
                        return left;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(bool takeLeft, i32[min max] left, i32[min max] right)
                    {
                        return Facade.Choose(takeLeft, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var choose = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Choose__i32");
            Assert.True(choose.SupportsDirectCodeGeneration);
            Assert.Contains(choose.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                choose.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.TargetName is not null
                    && statement.TargetName.Contains("typed_cond", StringComparison.Ordinal));
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
    public void ManifestBackedGenericBodiesPreferTypedBinaryTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-binary-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] AddTagged<T>(T tag, i32[min max] left, i32[min max] right)
                {
                    return left + right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.AddTagged") with
            {
                BodyText = """
                    {
                        return left;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
                        return Facade.AddTagged(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var addTagged = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_AddTagged__", StringComparison.Ordinal));
            Assert.True(addTagged.SupportsDirectCodeGeneration);
            Assert.Contains(
                addTagged.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
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
    public void ManifestBackedGenericBodiesPreferTypedShortCircuitTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-short-circuit-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn bool Both<T>(T tag, bool left, bool right)
                {
                    return left && right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Both") with
            {
                BodyText = """
                    {
                        return right;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn bool Run(bool left, bool right)
                    {
                        return Facade.Both(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var both = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_Both__", StringComparison.Ordinal));
            Assert.True(both.SupportsDirectCodeGeneration);
            Assert.Contains(both.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                both.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.TargetName is not null
                    && statement.TargetName.Contains("typed_and", StringComparison.Ordinal));
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
    public void ManifestBackedGenericBodiesPreferTypedComparisonConditionsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-comparison-condition-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] MinTagged<T>(T tag, i32[min max] left, i32[min max] right)
                {
                    return left < right ? left : right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.MinTagged") with
            {
                BodyText = """
                    {
                        return right;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] left, i32[min max] right)
                    {
                        return Facade.MinTagged(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var minTagged = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_MinTagged__", StringComparison.Ordinal));
            Assert.True(minTagged.SupportsDirectCodeGeneration);
            Assert.Contains(minTagged.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                minTagged.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.LessThan });
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
    public void ManifestBackedGenericImportsCanLoadFromExplicitCompilerSectionsWhenLegacyFieldsAreMissing()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-section-only-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    stack T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.CompilerSections);

            var sectionOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            TypedInterface = null,
                            CompilerFacts = null,
                            GenericTemplates = null
                        }
                        : module)
                    .ToArray()
            };
            var sectionOnlyFacade = Assert.Single(sectionOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.Null(sectionOnlyFacade.TypedInterface);
            Assert.Null(sectionOnlyFacade.CompilerFacts);
            Assert.Null(sectionOnlyFacade.GenericTemplates);
            Assert.NotNull(sectionOnlyFacade.CompilerSections);

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        sectionOnlyManifest,
                        sectionOnlyFacade),
                    out var sourceText));
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack T copy = value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, sectionOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
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
    public void ManifestBackedGenericImportsPreferExplicitCompilerSectionsOverConflictingLegacyFields()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-conflicting-sections-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    stack T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var conflictingManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            TypedInterface = new StarkPackageTypedInterfaceSection(
                                Functions:
                                [
                                    new StarkPackageTypedFunctionManifest(
                                        Name: "Identity",
                                        QualifiedName: "Facade.Identity",
                                        Visibility: "public",
                                        SymbolName: "Facade.Identity",
                                        Kind: "fn",
                                        ReturnType: new StarkPackageTypeReference("integer", BitWidth: 8),
                                        Parameters:
                                        [
                                            new StarkPackageTypedParameterManifest(
                                                "value",
                                                new StarkPackageTypeReference("named", Name: "T"))
                                        ],
                                        IsFfi: false,
                                        IsStrictFp: false,
                                        UseFastCallingConvention: true,
                                        GenericParameters: new[] { "T" })
                                ],
                                Types: [],
                                Globals: []),
                            CompilerFacts = new StarkPackageCompilerFactsSection(
                                FunctionEffects: Array.Empty<StarkPackageFunctionEffectManifest>()),
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                Array.Empty<StarkPackageFunctionTemplateManifest>())
                        }
                        : module)
                    .ToArray()
            };
            var conflictingFacade = Assert.Single(conflictingManifest.Modules, static module => module.ModuleName == "Facade");

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        conflictingManifest,
                        conflictingFacade),
                    out var sourceText));
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("public fn i8 Identity<T>(T value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, conflictingManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
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
    public void ManifestBackedGenericBodiesPreferTypedFieldAccessTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-field-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T>
                {
                    T Value;
                }

                public fn T ReadValue<T>(Box<T> box, T fallback)
                {
                    return box.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadValue") with
            {
                BodyText = """
                    {
                        return fallback;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack i32[min max] fallback = 0;
                        return Facade.ReadValue(new Facade.Box<i32[min max]>()
                        {
                            Value = value
                        }
                        , fallback);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadValue__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Value" });
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
    public void ManifestBackedGenericBodiesPreferTypedMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32[min max] Dummy)
                {
                    fn i32[min max] Echo(borrow Box self, i32[min max] value)
                    {
                        return value;
                    }
                }

                public fn i32[min max] Forward<T>(Box box, i32[min max] value, T tag)
                {
                    return box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("Forward<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return box.Echo(value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Box box = new Facade.Box(1);
                        return Facade.Forward(box, value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.Box.Echo" });
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
    public void ManifestBackedGenericBodiesPreferTypedChainedMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-chained-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32[min max] Dummy)
                {
                    fn i32[min max] Echo(borrow EchoBox self, i32[min max] value)
                    {
                        return value;
                    }
                }

                public struct EchoHolder
                {
                    EchoBox Box;
                }

                public fn i32[min max] CallHeldEcho<T>(EchoHolder holder, i32[min max] value, T tag)
                {
                    return holder.Box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.CallHeldEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("CallHeldEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("holder.Box.Echo(value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.EchoHolder holder = new Facade.EchoHolder()
                        {
                            Box = new Facade.EchoBox(1)
                        };
                        return Facade.CallHeldEcho(holder, value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_CallHeldEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
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
    public void ManifestBackedGenericBodiesPreferTypedDirectCallReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-direct-receiver-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32[min max] Dummy)
                {
                    fn i32[min max] Echo(borrow EchoBox self, i32[min max] value)
                    {
                        return value;
                    }
                }

                public fn EchoBox MakeEchoBox(i32[min max] dummy)
                {
                    return new EchoBox(dummy);
                }

                public fn i32[min max] CallMadeEcho<T>(i32[min max] value, T tag)
                {
                    return MakeEchoBox(1).Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.CallMadeEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("CallMadeEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("MakeEchoBox(1).Echo(value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.CallMadeEcho(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_CallMadeEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.MakeEchoBox" });
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
    public void ManifestBackedGenericBodiesPreferTypedObjectCreationReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-object-receiver-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32[min max] Dummy)
                {
                    fn i32[min max] Echo(borrow EchoBox self, i32[min max] value)
                    {
                        return value;
                    }
                }

                public fn i32[min max] CallConstructedEcho<T>(i32[min max] value, T tag)
                {
                    return new EchoBox(1).Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.CallConstructedEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("CallConstructedEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("new EchoBox(1).Echo(value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.CallConstructedEcho(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_CallConstructedEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue);
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
    public void ManifestBackedGenericBodiesPreferTypedGroupedConditionalReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-grouped-receiver-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32[min max] Dummy)
                {
                    fn i32[min max] Echo(borrow EchoBox self, i32[min max] value)
                    {
                        return value;
                    }
                }

                public fn i32[min max] ChooseEcho<T>(bool takeLeft, EchoBox left, EchoBox right, i32[min max] value, T tag)
                {
                    return (takeLeft ? left : right).Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ChooseEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ChooseEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("(takeLeft ? left : right).Echo(value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(bool takeLeft, i32[min max] value)
                    {
                        stack Facade.EchoBox left = new Facade.EchoBox(1);
                        stack Facade.EchoBox right = new Facade.EchoBox(2);
                        return Facade.ChooseEcho(takeLeft, left, right, value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ChooseEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
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
    public void ManifestBackedGenericBodiesPreferTypedVoidDirectCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-void-direct-statement-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32[min max] Value)
                {
                }

                public fn void ResetValue(borrow mut ResetBox box)
                {
                    box.Value = 0;
                }

                public fn void ForwardReset<T>(borrow mut ResetBox box, T tag)
                {
                    ResetValue(box);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ForwardReset") with
            {
                BodyText = """
                    {
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ForwardReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("ResetValue(box);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(borrow mut Facade.ResetBox box, i32[min max] tag)
                    {
                        Facade.ForwardReset(box, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ForwardReset__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements),
                static statement => IsDirectCallStatement(statement, "Facade.ResetValue"));
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
    public void ManifestBackedGenericBodiesPreferTypedVoidMemberCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-void-member-statement-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32[min max] Value)
                {
                    fn void Reset(borrow mut ResetBox self)
                    {
                        self.Value = 0;
                    }
                }

                public fn void ForwardMethodReset<T>(borrow mut ResetBox box, T tag)
                {
                    box.Reset();
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ForwardMethodReset") with
            {
                BodyText = """
                    {
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ForwardMethodReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("box.Reset();", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(borrow mut Facade.ResetBox box, i32[min max] tag)
                    {
                        Facade.ForwardMethodReset(box, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ForwardMethodReset__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements),
                static statement => IsDirectCallStatement(statement, "Facade.ResetBox.Reset"));
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
    public void ManifestBackedGenericBodiesPreferTypedConditionalCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-conditional-call-statement-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32[min max] Value)
                {
                }

                public fn void ResetValue(borrow mut ResetBox box, i32[min max] next)
                {
                    box.Value = next;
                }

                public fn void SelectReset<T>(bool chooseLeft, borrow mut ResetBox left, borrow mut ResetBox right, T tag)
                {
                    chooseLeft ? ResetValue(left, 7) : ResetValue(right, 9);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.SelectReset") with
            {
                BodyText = """
                    {
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("SelectReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("chooseLeft ? ResetValue(left, 7) : ResetValue(right, 9);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(bool chooseLeft, borrow mut Facade.ResetBox left, borrow mut Facade.ResetBox right, i32[min max] tag)
                    {
                        Facade.SelectReset(chooseLeft, left, right, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SelectReset__i32");
            Assert.True(
                specialized.SupportsDirectCodeGeneration,
                string.Join(
                    Environment.NewLine,
                    consumerResult.Logs.Select(static log => $"{log.Stage}:{log.Operation}:{log.Message}")));
            Assert.Contains(specialized.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);

            var resetCalls = specialized.Blocks
                .SelectMany(static block => block.Statements)
                .Count(static statement => IsDirectCallStatement(statement, "Facade.ResetValue"));
            Assert.Equal(2, resetCalls);
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
    public void ManifestBackedGenericBodiesPreferTypedFieldAndIndexAssignmentTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-field-index-assignment-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(mut i32[min max][] Values, i32[min max] Count)
                {
                }

                public fn void WriteValue<T>(borrow mut Buffer buffer, i32[min max] index, i32[min max] next, T tag)
                {
                    buffer.Count = next;
                    buffer.Values[index] = next;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.WriteValue") with
            {
                BodyText = """
                    {
                        return;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("WriteValue<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Count = next;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Values[index] = next;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(borrow mut Facade.Buffer buffer, i32[min max] index, i32[min max] next, i32[min max] tag)
                    {
                        Facade.WriteValue(buffer, index, next, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_WriteValue__i32");
            Assert.True(
                specialized.SupportsDirectCodeGeneration,
                string.Join(
                    Environment.NewLine,
                    consumerResult.Logs.Select(static log => $"{log.Stage}:{log.Operation}:{log.Message}")));

            var statements = specialized.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Count" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Values" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
            Assert.True(statements.Count(static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect) >= 2);
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
    public void ManifestBackedGenericBodiesPreferTypedCompoundFieldAndIndexAssignmentTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-compound-field-index-assignment-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(mut i32[min max][] Values, i32[min max] Count)
                {
                }

                public fn void AddValue<T>(borrow mut Buffer buffer, i32[min max] index, i32[min max] next, T tag)
                {
                    buffer.Count += next;
                    buffer.Values[index] += next;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.AddValue") with
            {
                BodyText = """
                    {
                        return;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("AddValue<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Count += next;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Values[index] += next;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(borrow mut Facade.Buffer buffer, i32[min max] index, i32[min max] next, i32[min max] tag)
                    {
                        Facade.AddValue(buffer, index, next, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_AddValue__i32");
            Assert.True(
                specialized.SupportsDirectCodeGeneration,
                string.Join(
                    Environment.NewLine,
                    consumerResult.Logs.Select(static log => $"{log.Stage}:{log.Operation}:{log.Message}")));

            var statements = specialized.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Count" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Values" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
            Assert.True(statements.Count(static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add }) >= 2);
            Assert.True(statements.Count(static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect) >= 2);
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
    public void ManifestBackedGenericBodiesPreferTypedIndexAccessTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-index-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] ReadSliceAt<T>(i32[min max][] view, i32[min max] index, T tag)
                {
                    return view[index];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadSliceAt") with
            {
                BodyText = """
                    {
                        return tag;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ReadSliceAt<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("view[index]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] index, i32[min max] tag)
                    {
                        stack i32[min max][3] values =
                        {
                            4, 7, 9
                        };
                        stack i32[min max][] view = values;
                        return Facade.ReadSliceAt(view, index, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadSliceAt__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrLoadIndirectRValue { Text: "view[index]" });
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
    public void ManifestBackedGenericBodiesPreferTypedFullViewTextSliceTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-full-view-text-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn ascii WholeAscii<T>(ascii text, T tag)
                {
                    return text[];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var wholeAscii = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.WholeAscii");
            Assert.NotNull(wholeAscii.TypedBody);
            var wholeAsciiReturn = Assert.Single(wholeAscii.TypedBody!.Statements);
            Assert.Equal("return", wholeAsciiReturn.Kind);
            Assert.Equal("index-access", wholeAsciiReturn.Expression.Kind);
            var wholeAsciiArguments = Assert.Single(wholeAsciiReturn.Expression.Arguments!);
            Assert.Equal("name", wholeAsciiArguments.Kind);
            Assert.Equal("text", wholeAsciiArguments.Name);

            var corruptedTemplate = wholeAscii with
            {
                BodyText = "{ return this is not valid Stark; }"
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildStructuredModuleDocument(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var importedDocument));
            Assert.DoesNotContain("this is not valid Stark", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn ascii WholeAscii<T>(ascii text, T tag);", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return text[];", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn ascii Run()
                    {
                        return Facade.WholeAscii("hello", 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.DoesNotContain("this is not valid Stark", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn ascii WholeAscii<T>(ascii text, T tag);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return text[];", importedModule.ParseResult.SourceText, StringComparison.Ordinal);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Facade_WholeAscii", StringComparison.Ordinal));
            Assert.True(specialized.SupportsDirectCodeGeneration);
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
    public void ManifestBackedGenericBodiesPublishAndLowerRetborrowTypedTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-retborrow-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T>
                {
                    T Value;
                }

                public fn retborrow T Get<T>(borrow Box<T> box)
                {
                    return box.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var get = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Get");
            Assert.NotNull(get.TypedBody);
            Assert.Null(get.BodyText);
            var getReturn = Assert.Single(get.TypedBody!.Statements);
            Assert.Equal("return", getReturn.Kind);
            Assert.Equal("field-access", getReturn.Expression.Kind);

            var corruptedTemplate = get with
            {
                BodyText = "{ return this is not valid Stark; }"
            };
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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn retborrow i32[min max] Run(borrow Facade.Box<i32[min max]> box)
                    {
                        return Facade.Get(box);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(Environment.NewLine, consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.DoesNotContain(
                consumerResult.Logs,
                static log => log.EventId is "unsupported-lowering" or "missing-function-body");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.DoesNotContain("this is not valid Stark", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn retborrow T Get<T>(borrow Box<T> box);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Facade_Get", StringComparison.Ordinal));
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.DoesNotContain(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value).OfType<MidLevelIrCallRValue>(),
                static call => call.FunctionName.Contains("this is not valid Stark", StringComparison.Ordinal));
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
    public void ManifestBackedGenericBodiesPreferTypedTextSliceTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-text-slice-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn ascii SliceAsciiWindow<T>(ascii text, i32[min max] start, i32[min max] length, T tag)
                {
                    return text[start, length];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.SliceAsciiWindow") with
            {
                BodyText = """
                    {
                        return text;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("SliceAsciiWindow<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("text[start, length]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn ascii Run(i32[min max] start, i32[min max] length)
                    {
                        return Facade.SliceAsciiWindow("hello", start, length, start);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SliceAsciiWindow__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrTextSliceRValue textSlice
                    && textSlice.TextValue is MidLevelIrParameterOperand { Name: "text" }
                    && textSlice.Type.Kind == StarkTypeKind.Ascii
                    && textSlice.Start.Type.DisplayName == "i64"
                    && textSlice.Length.Type.DisplayName == "i64");
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
    public void ManifestBackedGenericBodiesPreferTypedSingleElementTextIndexTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-text-index-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn ascii PickAsciiUnit<T>(ascii text, i32[min max] index, T tag)
                {
                    return text[index];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.PickAsciiUnit") with
            {
                BodyText = """
                    {
                        return text;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("PickAsciiUnit<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("text[index]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn ascii Run(i32[min max] index)
                    {
                        return Facade.PickAsciiUnit("hello", index, index);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_PickAsciiUnit__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrTextSliceRValue textSlice
                    && textSlice.TextValue is MidLevelIrParameterOperand { Name: "text" }
                    && textSlice.Type.Kind == StarkTypeKind.Ascii
                    && textSlice.Start.Type.DisplayName == "i64"
                    && textSlice.Length is MidLevelIrIntegerConstantOperand { Value: var lengthValue, Type.DisplayName: "i64" }
                    && lengthValue == System.Numerics.BigInteger.One);
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
    public void ManifestBackedGenericBodiesPreferTypedChainedFieldIndexTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-field-index-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct SliceBox<T>
                {
                    i32[min max][] Values;
                }

                public fn i32[min max] ReadBoxSliceAt<T>(SliceBox<T> box, i32[min max] index, T tag)
                {
                    return box.Values[index];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadBoxSliceAt") with
            {
                BodyText = """
                    {
                        return tag;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ReadBoxSliceAt<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("box.Values[index]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] index, i32[min max] tag)
                    {
                        stack i32[min max][3] values =
                        {
                            4, 7, 9
                        };
                        stack i32[min max][] view = values;
                        stack Facade.SliceBox<i32[min max]> box = new Facade.SliceBox<i32[min max]>()
                        {
                            Values = view
                        };
                        return Facade.ReadBoxSliceAt(box, index, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadBoxSliceAt__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Values", Text: "box.Values" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrLoadIndirectRValue);
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
    public void ManifestBackedGenericBodiesPreferTypedVoidReturnTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-void-return-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32[min max] Value)
                {
                }

                public fn void ResetValue(borrow mut ResetBox box)
                {
                    box.Value = 0;
                }

                public fn void GuardedReset<T>(bool shouldStop, borrow mut ResetBox box, T tag)
                {
                    if (shouldStop)
                    {
                        return;
                    }
                    ResetValue(box);
                    return;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.GuardedReset") with
            {
                BodyText = """
                    {
                        return;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("GuardedReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("ResetValue(box);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(bool shouldStop, borrow mut Facade.ResetBox box, i32[min max] tag)
                    {
                        Facade.GuardedReset(shouldStop, box, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_GuardedReset__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements),
                static statement => IsDirectCallStatement(statement, "Facade.ResetValue"));
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
    public void ManifestBackedGenericBodiesPreferTypedObjectCreationTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-object-creation-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box<T>(T Value)
                {
                }

                public fn Box<T> Wrap<T>(T value, Box<T> fallback)
                {
                    return new Box<T>(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return fallback;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn Facade.Box<i32[min max]> Run(i32[min max] value)
                    {
                        return Facade.Wrap(value, new Facade.Box<i32[min max]>(0));
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value" });
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
    public void ManifestBackedTypedNestedInitializerObjectCreationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-nested-object-creation-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Inner<T>
                {
                    T Value;
                }

                public struct Outer<T>
                {
                    Inner<T> Item;
                    i32[min max][2] Values;
                }

                public fn Outer<T> Wrap<T>(T value, T tag)
                {
                    return new Outer<T>()
                    {
                        Item =
                        {
                            Value = value
                        },
                        Values =
                        {
                            7, 9
                        }
                    };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return new Outer<T>();
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildStructuredModuleDocument(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var importedDocument));
            Assert.DoesNotContain("return new Outer<T>();", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn Outer<T> Wrap<T>(T value, T tag);", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Outer<i32[min max]> wrapped = Facade.Wrap(value, value);
                        return wrapped.Item.Value + wrapped.Values[1];
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Item" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Values" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
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


    [Fact]
    public void ManifestBackedGenericBodiesPreferTypedEnumCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-enum-call-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T>
                {
                    None,
                    Some(T),
                }

                public fn Option<T> Wrap<T>(T value)
                {
                    return Option<T>.Some(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Option<T>.None;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Option<i32[min max]> result = Facade.Wrap(value);
                        switch (result)
                        {
                            case Facade.Option<i32[min max]>.Some(var payload):
                                return payload;
                            case Facade.Option<i32[min max]>.None:
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesPreferTypedTryPropagationTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-try-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum ParseError
                {
                    Bad,
                }

                public enum Result<T, E>
                {
                    [Ok] Ok(T),
                    [Err] Err(E),
                }

                public fn Result<T, ParseError> Pass<T>(T value, bool ok)
                {
                    if (ok)
                    {
                        stack T payload = try Result<T, ParseError>.Ok(value);
                        return Result<T, ParseError>.Ok(payload);
                    }

                    return Result<T, ParseError>.Err(ParseError.Bad);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var passTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Pass");
            Assert.Null(passTemplate.BodyText);
            Assert.NotNull(passTemplate.TypedBody);
            var tryPropagation = Assert.Single(passTemplate.TryPropagations ?? []);
            Assert.Equal(0, tryPropagation.Ordinal);

            var corruptedTemplate = passTemplate with
            {
                BodyText = """
                    {
                        return this is not valid Stark;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Result<i32[min max], Facade.ParseError> result = Facade.Pass(value, true);
                        switch (result)
                        {
                            case Facade.Result<i32[min max], Facade.ParseError>.Ok(var payload):
                                return payload;
                            case Facade.Result<i32[min max], Facade.ParseError>.Err(var error):
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Pass__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Blocks, static block => block.Terminator.ConditionText == "try Err");
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Ok_0" });
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
    public void ManifestBackedGenericBodiesPreferTypedEnumValueTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-enum-value-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Marker<T>
                {
                    Empty,
                    Missing,
                }

                public fn Marker<T> EmptyLike<T>(T value)
                {
                    return Marker<T>.Empty;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.EmptyLike") with
            {
                BodyText = """
                    {
                        return Marker<T>.Missing;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Marker<i32[min max]> result = Facade.EmptyLike(value);
                        switch (result)
                        {
                            case Facade.Marker<i32[min max]>.Empty:
                                return 0;
                            case Facade.Marker<i32[min max]>.Missing:
                                return 1;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_EmptyLike__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            var tagWrite = Assert.Single(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Equal(
                System.Numerics.BigInteger.Zero,
                Assert.IsType<MidLevelIrIntegerConstantOperand>(Assert.IsType<MidLevelIrInsertFieldRValue>(tagWrite).Value).Value);
            Assert.DoesNotContain(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesPreferTypedLiteralTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-literal-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i8[min max] One<T>(T value)
                {
                    return 1;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.One") with
            {
                BodyText = """
                    {
                        return 2;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i8[min max] Run(i32[min max] value)
                    {
                        return Facade.One(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_One__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            var integerConstants = CollectMirIntegerConstants(specialized);
            Assert.Contains(System.Numerics.BigInteger.One, integerConstants);
            Assert.DoesNotContain(new System.Numerics.BigInteger(2), integerConstants);
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
    public void ManifestBackedGenericBodiesPreferTypedEnumConstructorTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-enum-constructor-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T>
                {
                    Value
                    {
                        Data: T, Tag: i32[min max]
                    },
                }

                public fn Boxed<T> Wrap<T>(T value)
                {
                    return Boxed<T>.Value
                    {
                        Data: value, Tag: 1
                    };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Boxed<T>.Value { Data: value, Tag: 2 };
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Boxed<i32[min max]> result = Facade.Wrap(value);
                        switch (result)
                        {
                            case Facade.Boxed<i32[min max]>.Value
                            {
                                Data: _, Tag: var tag
                            }:
                                return tag;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            var integerConstants = CollectMirIntegerConstants(specialized);
            Assert.Contains(System.Numerics.BigInteger.One, integerConstants);
            Assert.DoesNotContain(new System.Numerics.BigInteger(2), integerConstants);
            var construction = Assert.Single(specialized.Blocks
                .SelectMany(static block => block.Statements)
                .Select(static statement => statement.Value)
                .OfType<MidLevelIrUseRValue>()
                .Select(static use => use.Operand)
                .Concat(specialized.Blocks.Select(static block => block.Terminator.Value).OfType<MidLevelIrOperand>())
                .OfType<MidLevelIrEnumConstructionOperand>());
            Assert.Equal("Value", construction.Facts.Variant.Name);
            Assert.Equal(2, construction.Facts.PayloadFields.Count);
            Assert.All(construction.Facts.PayloadFields, field => Assert.NotEqual(StarkTypeKind.Error, field.FieldType.Kind));
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
    public void ManifestBackedGenericBodiesUsePublishedConversionTargetsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-conversion-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Truncate<T>(f32 value, T tag)
                {
                    return (i32[min max])value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Truncate") with
            {
                BodyText = """
                    {
                        return (i64[min max])value;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(f32 value)
                    {
                        return Facade.Truncate(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Truncate__f32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrConvertRValue
                {
                    TargetType.Kind: StarkTypeKind.Integer,
                    TargetType.BitWidth: 32
                });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumConstructorFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-constructor-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T>
                {
                    Value
                    {
                        Data: T, Tag: i32[min max]
                    },
                }

                public fn Boxed<T> Wrap<T>(T value)
                {
                    return Boxed<T>.Value
                    {
                        Data: value, Tag: 1
                    };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Boxed<T>.Missing { Wrong: value, AlsoWrong: 1 };
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Boxed<i32[min max]> boxed = Facade.Wrap(value);
                        switch (boxed)
                        {
                            case Facade.Boxed<i32[min max]>.Value
                            {
                                Data: var data, Tag: var tag
                            }:
                                return data + tag;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Value_Data" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Value_Tag" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumCallFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-call-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T>
                {
                    None,
                    Some(T),
                }

                public fn Option<T> Wrap<T>(T value)
                {
                    return Option<T>.Some(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Option<T>.Missing(value);
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Option<i32[min max]> result = Facade.Wrap(value);
                        switch (result)
                        {
                            case Facade.Option<i32[min max]>.Some(var payload):
                                return payload;
                            case Facade.Option<i32[min max]>.None:
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumValueFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-value-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T>
                {
                    None,
                    Some(T),
                }

                public fn Option<T> EmptyLike<T>(T value)
                {
                    return Option<T>.None;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.EmptyLike") with
            {
                BodyText = """
                    {
                        return Option<T>.Missing;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Option<i32[min max]> result = Facade.EmptyLike(value);
                        switch (result)
                        {
                            case Facade.Option<i32[min max]>.Some(var payload):
                                return payload;
                            case Facade.Option<i32[min max]>.None:
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_EmptyLike__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.DoesNotContain(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumPatternFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-pattern-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T>
                {
                    None,
                    Some(T),
                }

                public fn i32[min max] HasValue<T>(Option<T> value)
                {
                    switch (value)
                    {
                        case Option<T>.Some(var payload):
                            return 1;
                        case Option<T>.None:
                            return 0;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.HasValue") with
            {
                BodyText = """
                    {
                        switch (value)
                        {
                            case Option<T>.Missing(var payload):
                                return 1;
                            case Option<T>.Absent:
                                return 0;
                        }
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.HasValue(Facade.Option<i32[min max]>.Some(value));
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_HasValue__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "$tag" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumPatternMemberFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-pattern-member-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T>
                {
                    Value
                    {
                        Data: T, Tag: i32[min max]
                    },
                }

                public fn i32[min max] ReadTag<T>(Boxed<T> boxed)
                {
                    switch (boxed)
                    {
                        case Boxed<T>.Value
                        {
                            Data: _, Tag: var tag
                        }:
                            return tag;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadTag") with
            {
                BodyText = """
                    {
                        switch (boxed)
                        {
                            case Boxed<T>.Value
                            {
                                Wrong: _, AlsoWrong: var tag
                            }:
                                return tag;
                        }
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.ReadTag(Facade.Boxed<i32[min max]>.Value
                        {
                            Data: value, Tag: 7
                        });
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadTag__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "$Value_Tag" });
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
    public void ManifestBackedGenericBodiesUsePublishedAggregatePatternFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-aggregate-pattern-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32[min max] Value, i32[min max] Count)
                {
                }

                public fn i32[min max] ReadCount<T>(Counter counter, T tag)
                {
                    switch (counter)
                    {
                        case Counter(_, var count):
                            return count;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadCount") with
            {
                BodyText = """
                    {
                        switch (counter)
                        {
                            case Missing(_, var count):
                                return count;
                        }
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.ReadCount(new Facade.Counter(value, 7), value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadCount__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Count" });
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
    public void ManifestBackedGenericBodiesUsePublishedNestedAndLiteralSwitchPatternFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-nested-pattern-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32[min max] Value, i32[min max] Count)
                {
                }

                public enum Wrapped<T>
                {
                    Value
                    {
                        Data: Counter, Marker: i32[min max]
                    },
                }

                public fn i32[min max] ReadNestedCount<T>(Wrapped<T> wrapped, T tag)
                {
                    switch (wrapped)
                    {
                        case Wrapped<T>.Value
                        {
                            Data: Counter(7, var count), Marker: 1
                        }:
                            return count;
                        default:
                            return -1;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadNestedCount") with
            {
                BodyText = """
                    {
                        switch (wrapped)
                        {
                            case Wrapped<T>.Missing
                            {
                                Data: Counter(0, var count), Marker: 99
                            }:
                                return 0;
                            default:
                                return -100;
                        }
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.ReadNestedCount(
                            Facade.Wrapped<i32[min max]>.Value
                            {
                                Data: new Facade.Counter(7, value), Marker: 1
                            },
                            value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadNestedCount__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "$Value_Data" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Count" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumWholeCaptureSwitchPatternFactsWithoutBridgeBodyText()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-whole-capture-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Wrapped<T>
                {
                    None,
                    Pair(i32[min max], i32[min max]),
                }

                public fn i32[min max] ReadEnumWhole<T>(Wrapped<T> wrapped, T tag)
                {
                    switch (wrapped)
                    {
                        case Wrapped<T>.Pair capture:
                            return 5;
                        default:
                            return -2;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadEnumWhole");
            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Wrapped<i32[min max]> wrapped =
                            Facade.Wrapped<i32[min max]>.Pair(2, value);
                        return Facade.ReadEnumWhole(wrapped, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadEnumWhole__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local is { Name: "capture", StorageClass: "match" });
            Assert.Contains(
                specialized.Blocks,
                static block => block.Label.Contains("switch_agg_match", StringComparison.Ordinal)
                    && block.Statements.Any(static statement => statement.TargetName == "capture"));
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
    public void ManifestBackedGenericBodiesUsePublishedLiteralAndGuardedSwitchFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-literal-guard-switch-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] ClassifySwitch<T>(i32[min max] value, T tag)
                {
                    switch (value)
                    {
                        case 0:
                        case 1:
                            return 10;
                        case var current when current > 5:
                            return current;
                        default:
                            return -1;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ClassifySwitch") with
            {
                BodyText = """
                    {
                        switch (value)
                        {
                            case 99:
                                return 10;
                            case var current when current < 0:
                                return 0;
                            default:
                                return -100;
                        }
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.ClassifySwitch(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ClassifySwitch__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local is { Name: "current", StorageClass: "match" });

            var values = specialized.Blocks
                .SelectMany(static block => block.Statements)
                .Select(static statement => statement.Value)
                .ToArray();
            Assert.True(values.Count(static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Equal }) >= 2);
            Assert.Contains(values, static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.GreaterThan });
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
    public void ManifestBackedGenericBodiesFoldComptimeStructuralFactsInMir()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-structural-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn u64[0 max] Width<T>(T value)
                {
                    return comptime System.Compiler.TypeIntegerBitWidth<T>();
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Width") with
            {
                BodyText = "{ return 99; }"
            };
            Assert.NotNull(corruptedTemplate.TypedBody);

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn u64[0 max] Run(i32[min max] value)
                    {
                        return Facade.Width(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Width__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Empty(DirectCallNames(specialized));

            var returnedWidth = Assert.Single(specialized.Blocks
                .Where(static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Return)
                .Select(static block => block.Terminator.Value)
                .OfType<MidLevelIrIntegerConstantOperand>());
            Assert.Equal(new System.Numerics.BigInteger(32), returnedWidth.Value);
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
    public void ManifestBackedGenericBodiesUsePublishedObjectInitializerMembersWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-object-initializer-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair<T>
                {
                    T Value;
                    i32[min max] Count;
                }

                public fn Pair<T> MakePair<T>(T value)
                {
                    return new Pair<T>()
                    {
                        Value = value, Count = 1
                    };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.MakePair") with
            {
                BodyText = """
                    {
                        return new Pair<T>() { Missing = value, Wrong = 1 };
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Pair<i32[min max]> pair = Facade.MakePair(value);
                        return pair.Count;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_MakePair__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Count", FieldIndex: 1 });
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
    public void ManifestBackedGenericBodiesUsePublishedObjectCreationTypesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-object-type-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair<T>
                {
                    T Value;
                    i32[min max] Count;
                }

                public fn Pair<T> MakePair<T>(T value)
                {
                    return new Pair<T>()
                    {
                        Value = value, Count = 1
                    };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.MakePair") with
            {
                BodyText = """
                    {
                        return new Missing<T>() { Value = value, Count = 1 };
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        stack Facade.Pair<i32[min max]> pair = Facade.MakePair(value);
                        return pair.Count;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_MakePair__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Count", FieldIndex: 1 });
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
    public void ManifestBackedGenericBodiesUsePublishedDirectCallTargetsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-direct-call-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value)
                {
                    return value;
                }

                public fn T Forward<T>(T value)
                {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward") with
            {
                BodyText = """
                    {
                        return Missing(value);
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Forward(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            Assert.Contains(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");

            var forward = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(forward.SupportsDirectCodeGeneration);
            Assert.Contains(
                forward.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "__stark_mono_fn_Demo__Facade_Identity__i32" });
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
    public void ManifestBackedGenericBodiesUsePublishedFieldAccessFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-field-access-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value)
                {
                }

                public fn T ReadValue<T>(Pair<T> pair)
                {
                    return pair.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadValue") with
            {
                BodyText = """
                    {
                        return pair.Missing;
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(Facade.Pair<i32[min max]> pair)
                    {
                        return Facade.ReadValue(pair);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadValue__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Value", FieldIndex: 0 });
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
    public void ManifestBackedGenericBodiesUsePublishedMemberCallTargetsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-member-call-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32[min max] Dummy)
                {
                    fn i32[min max] Echo(borrow Box self, i32[min max] value)
                    {
                        return value;
                    }
                }

                public fn i32[min max] Forward<T>(T tag, Box box, i32[min max] value)
                {
                    return box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward") with
            {
                BodyText = """
                    {
                        return box.Missing(value);
                    }
                    """
            };

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
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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

                    fn i32[min max] Run(Facade.Box box, i32[min max] value)
                    {
                        return Facade.Forward(value, box, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.Box.Echo" });
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
    public void ManifestBackedGenericBodiesPreserveTransitiveImportedModuleSurfaceAcrossPackageImages()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-transitive-import-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            File.WriteAllText(
                mathPath,
                """
                module Math

                public fn T Identity<T>(T value)
                {
                    return value;
                }
                """);

            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Facade

                    public fn T Forward<T>(T value)
                    {
                        return Math.Identity(value);
                    }
                    """,
                    facadePath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module with
                    {
                        Functions = [],
                        Types = [],
                        Globals = [],
                        TypeAliases = [],
                        TypedInterface = module.EffectiveTypedInterface,
                        CompilerFacts = module.EffectiveCompilerFacts,
                        GenericTemplates = module.EffectiveGenericTemplates
                    })
                    .ToArray()
            };
            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            var forwardTemplate = Assert.Single(typedFacadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward");
            Assert.NotNull(forwardTemplate.TypedBody);
            var forwardReturn = Assert.Single(forwardTemplate.TypedBody!.Statements);
            Assert.Equal("return", forwardReturn.Kind);
            Assert.Equal("direct-call", forwardReturn.Expression.Kind);
            var forwardDirectCall = Assert.Single(forwardTemplate.DirectCalls!);
            Assert.Equal("Math.Identity", forwardDirectCall.QualifiedResolvedName);
            Assert.Equal("Math.Identity", forwardDirectCall.QualifiedSourceName);
            Assert.Equal("Math.Identity", forwardDirectCall.QualifiedTemplateName);

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var sourceText));
            Assert.Contains("import Math", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn T Forward<T>(T value)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return Math.Identity(value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);
            File.Delete(mathPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Forward(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedFacade));
            Assert.NotNull(importedFacade);
            Assert.True(loadedModules.TryGet("Math", out var importedMath));
            Assert.NotNull(importedMath);
            Assert.Contains(importedFacade.SyntaxModel.Imports, static import => import.ModuleName == "Math" && !import.IsReExport);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            Assert.Contains(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Math_Identity__i32");
            var forward = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.Contains(
                forward.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value).OfType<MidLevelIrCallRValue>(),
                static call => call.FunctionName == "__stark_mono_fn_Demo__Math_Identity__i32");
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
    public void LowerMirSubstitutesConcreteTypesInsideMaterializedGenericBodies()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value)
                {
                    stack T copy = value;
                    return copy;
                }

                fn i32[min max] Run(i32[min max] value)
                {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);

        var monomorphized = Assert.Single(
            mir.Functions,
            static function => function.Name == "__stark_mono_fn_Demo__Identity__i32");
        Assert.True(monomorphized.HasBody);
        Assert.True(monomorphized.SupportsDirectCodeGeneration);
        Assert.Equal("i32", monomorphized.ReturnType.DisplayName);
        Assert.Equal("i32", Assert.Single(monomorphized.Parameters).Type.DisplayName);
        Assert.Contains(
            monomorphized.Locals,
            static local => local.Name == "copy" && local.Type.DisplayName == "i32");
    }


    [Fact]
    public void LowerMirRewritesGenericCallsToMaterializedSpecializationSymbols()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value)
                {
                    return value;
                }

                fn i32[min max] Run(i32[min max] value)
                {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);

        var run = Assert.Single(mir.Functions, static function => function.Name == "Run");
        var call = run.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>()
            .Single();

        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", call.FunctionName);
    }


    [Fact]
    public void ManifestBackedConcreteGenericAliasesMaterializeObjectInitializersAndGroupedConditionalsInMir()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-alias-generic-mir-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T>
                {
                    T Value;
                }

                public alias IntBox = Box<i32[min max]>;
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn Facade.IntBox Make(i32[min max] value)
                    {
                        stack Facade.IntBox box =
                        {
                            Value = value
                        };
                        return box;
                    }

                    fn Facade.IntBox Choose(bool takeLeft)
                    {
                        stack Facade.IntBox left =
                        {
                            Value = 1
                        };
                        stack Facade.IntBox right =
                        {
                            Value = 2
                        };
                        return (takeLeft ? left : right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var make = Assert.Single(mir.Functions, static function => function.Name == "Make");
            var choose = Assert.Single(mir.Functions, static function => function.Name == "Choose");

            Assert.True(make.SupportsDirectCodeGeneration);
            Assert.True(choose.SupportsDirectCodeGeneration);
            Assert.Contains(
                make.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 });
            Assert.Equal(
                2,
                choose.Blocks
                    .SelectMany(static block => block.Statements)
                    .Count(static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 }));
            Assert.Contains(choose.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
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
