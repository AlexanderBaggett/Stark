using Stark.Compiler;
using Stark.Parsing;
using System.Text.RegularExpressions;

namespace compiler.Tests;

public sealed class PackageImageArchitectureTests
{
    [Fact]
    public void PackageImagePreservesBackendOpaqueModuleBoundary()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-backend-opaque-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    [Backend(Opaque)]
                    module Facade

                    public fn i32[min max] Identity(i32[min max] value)
                    {
                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.Equal("opaque", facadeModule.CompilerSections?.CompilerFacts?.BackendOptimizationMode);
            Assert.Contains("\"BackendOptimizationMode\": \"opaque\"", manifest.ToJson(), StringComparison.Ordinal);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("[Backend(Opaque)]", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, syntaxModel.BackendOptimizationMode);
            var attribute = Assert.Single(syntaxModel.ModuleAttributes ?? []);
            Assert.Equal("Backend", attribute.Name);
            Assert.Equal(["Opaque"], attribute.Arguments);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, facts.BackendOptimizationMode);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedModule, out var importedDocument));
            Assert.False(CompilerCli.ShouldEnableDependencyLto(importedDocument));
            var optimizationFacts = CompilerCli.AnalyzeModuleOptimizationSafety(importedDocument, toolchainCanUseThinLto: true);
            Assert.False(optimizationFacts.CanEmitThinLtoBitcode);
            Assert.False(optimizationFacts.CanRunNormalLlvmPasses);
            Assert.True(optimizationFacts.ContainsKnownFragileConstructs);
            Assert.Equal("backend-opaque", optimizationFacts.DecisionReason);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesAssociatedTypesAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-associated-types-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public trait Reader
                    {
                        alias Item;

                        finite law Self.Item Read(borrow Self self);
                    }

                    public trait HasHash
                    {
                        alias Code = u64[0 max];

                        finite law Self.Code Hash(borrow Self self);
                    }

                    public struct Counter : Reader, HasHash
                    {
                        alias Item = i32[min max];

                        i32[min max] Value;

                        public finite law i32[min max] Read(borrow Counter self)
                        {
                            return self.Value;
                        }

                        public finite law u64[0 max] Hash(borrow Counter self)
                        {
                            return (u64[0 max])self.Value;
                        }
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterface = facadeModule.CompilerSections?.TypedInterface;
            Assert.NotNull(typedInterface);

            var reader = Assert.Single(typedInterface!.Types, static type => type.Name == "Reader");
            var readerItem = Assert.Single(reader.AssociatedTypes ?? [], static associatedType => associatedType.Name == "Item");
            Assert.Null(readerItem.TargetType);

            var hasHash = Assert.Single(typedInterface.Types, static type => type.Name == "HasHash");
            var hashCode = Assert.Single(hasHash.AssociatedTypes ?? [], static associatedType => associatedType.Name == "Code");
            Assert.Equal("integer", hashCode.TargetType?.Kind);
            Assert.Equal(64, hashCode.TargetType?.BitWidth);

            var counter = Assert.Single(typedInterface.Types, static type => type.Name == "Counter");
            var counterItem = Assert.Single(counter.AssociatedTypes ?? [], static associatedType => associatedType.Name == "Item");
            Assert.Equal("integer", counterItem.TargetType?.Kind);
            Assert.Equal(32, counterItem.TargetType?.BitWidth);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("alias Item;", sourceText, StringComparison.Ordinal);
            Assert.Contains("alias Code = ", sourceText, StringComparison.Ordinal);
            Assert.Contains("alias Item = ", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            var readerFacts = facts.NamedTypes["Facade.Reader"];
            Assert.True(readerFacts.AssociatedTypes["Item"].IsRequired);
            var hashFacts = facts.NamedTypes["Facade.HasHash"];
            Assert.Equal(StarkTypeKind.Integer, hashFacts.AssociatedTypes["Code"].TargetType?.Kind);
            var counterFacts = facts.NamedTypes["Facade.Counter"];
            Assert.Equal(StarkTypeKind.Integer, counterFacts.AssociatedTypes["Item"].TargetType?.Kind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesFineGrainedBackendOpaqueBoundaries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-fine-backend-opaque-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    [Backend(Opaque)]
                    public finite law i32[min max] Identity(i32[min max] value)
                    {
                        return value;
                    }

                    [Backend(Opaque)]
                    public finite law T Echo<T>(T value)
                    {
                        return value;
                    }

                    [Backend(Opaque)]
                    public struct Box
                    {
                        i32[min max] Value;

                        public finite law i32[min max] Read(borrow Box self)
                        {
                            return self.Value;
                        }
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterface = facadeModule.CompilerSections?.TypedInterface;
            var compilerFacts = facadeModule.CompilerSections?.CompilerFacts;
            Assert.NotNull(typedInterface);
            Assert.NotNull(compilerFacts);

            var identity = Assert.Single(typedInterface!.Functions, static function => function.Name == "Identity");
            Assert.Equal("opaque", identity.BackendOptimizationMode);
            var echo = Assert.Single(typedInterface.Functions, static function => function.Name == "Echo");
            Assert.Equal("opaque", echo.BackendOptimizationMode);

            var box = Assert.Single(typedInterface.Types, static type => type.Name == "Box");
            Assert.Equal("opaque", box.BackendOptimizationMode);
            var read = Assert.Single(box.Methods ?? [], static method => method.Name == "Read");
            Assert.Equal("opaque", read.BackendOptimizationMode);
            var identityEffects = Assert.Single(
                compilerFacts!.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Identity");
            Assert.Equal("opaque", identityEffects.BackendOptimizationMode);
            var echoEffects = Assert.Single(
                compilerFacts.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Echo");
            Assert.Equal("opaque", echoEffects.BackendOptimizationMode);
            var readEffects = Assert.Single(
                compilerFacts.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Box.Read");
            Assert.Equal("opaque", readEffects.BackendOptimizationMode);
            var echoTemplate = Assert.Single(
                facadeModule.CompilerSections?.GenericTemplates?.Functions ?? [],
                static function => function.QualifiedResolvedName == "Facade.Echo");
            Assert.Equal("opaque", echoTemplate.BackendOptimizationMode);

            Assert.Contains("\"BackendOptimizationMode\": \"opaque\"", manifest.ToJson(), StringComparison.Ordinal);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("[Backend(Opaque)]", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
            var importedIdentity = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Identity");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedIdentity.Function!.BackendOptimizationMode);
            var importedEcho = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Echo");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedEcho.Function!.BackendOptimizationMode);
            var importedBox = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedBox.BackendOptimizationMode);
            var importedRead = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box.Read");
            Assert.Equal(ModuleBackendOptimizationMode.Opaque, importedRead.Function!.BackendOptimizationMode);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.Equal(
                ModuleBackendOptimizationMode.Opaque,
                facts.FunctionTemplates["Facade.Echo"].BackendOptimizationMode);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void NonOpaqueSourceDependencyCanParticipateInLto()
    {
        var parseResult = StarkSyntax.ParseCompilationUnit("module Helpers");
        var syntaxModel = SyntaxModelFactory.Create(parseResult);
        var document = new LoadedModuleDocument(
            new ResolvedModuleReference(
                "Helpers",
                "/virtual/Helpers.stark",
                IsExternal: false,
                IsRoot: false),
            parseResult,
            syntaxModel);

        Assert.True(CompilerCli.ShouldEnableDependencyLto(document));
        var optimizationFacts = CompilerCli.AnalyzeModuleOptimizationSafety(document, toolchainCanUseThinLto: true);
        Assert.True(optimizationFacts.CanEmitThinLtoBitcode);
        Assert.True(optimizationFacts.CanRunNormalLlvmPasses);
        Assert.False(optimizationFacts.ContainsKnownFragileConstructs);
        Assert.False(optimizationFacts.ExposesHotInlineCandidates);
        Assert.Equal("thinlto-enabled", optimizationFacts.DecisionReason);

        var hotParseResult = StarkSyntax.ParseCompilationUnit(
            """
            module Helpers

            public finite law i32[min max] Identity(i32[min max] value)
            {
                return value;
            }
            """);
        var hotDocument = new LoadedModuleDocument(
            new ResolvedModuleReference(
                "Helpers",
                "/virtual/Helpers.stark",
                IsExternal: false,
                IsRoot: false),
            hotParseResult,
            SyntaxModelFactory.Create(hotParseResult));
        var hotFacts = CompilerCli.AnalyzeModuleOptimizationSafety(hotDocument, toolchainCanUseThinLto: true);
        Assert.True(hotFacts.CanEmitThinLtoBitcode);
        Assert.True(hotFacts.ExposesHotInlineCandidates);
        Assert.Equal("thinlto-enabled-hot-inline-candidates", hotFacts.DecisionReason);
    }

    [Fact]
    public void SystemCollectionsSourceUsesDefaultBackendOptimizationWithoutModuleNameGate()
    {
        var repositoryRoot = FindRepositoryRoot();
        var collectionsPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Collections.stark");
        var parseResult = StarkSyntax.ParseCompilationUnit(File.ReadAllText(collectionsPath));
        var syntaxModel = SyntaxModelFactory.Create(parseResult);

        Assert.Equal("System.Collections", syntaxModel.ModuleName);
        Assert.Equal(ModuleBackendOptimizationMode.Default, syntaxModel.BackendOptimizationMode);

        var dictionary = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Dictionary");
        Assert.Equal(ModuleBackendOptimizationMode.Default, dictionary.BackendOptimizationMode);
        var dictionaryReserve = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Dictionary.Reserve");
        Assert.Equal(ModuleBackendOptimizationMode.Default, dictionaryReserve.Function!.BackendOptimizationMode);

        var list = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "List");
        Assert.Equal(ModuleBackendOptimizationMode.Default, list.BackendOptimizationMode);
        var linkedListAddLast = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "LinkedList.AddLast");
        Assert.Equal(ModuleBackendOptimizationMode.Default, linkedListAddLast.Function!.BackendOptimizationMode);

        var nonOpaqueCollections = StarkSyntax.ParseCompilationUnit("module System.Collections");
        var nonOpaqueSyntaxModel = SyntaxModelFactory.Create(nonOpaqueCollections);
        var nonOpaqueDocument = new LoadedModuleDocument(
            new ResolvedModuleReference(
                "System.Collections",
                "/virtual/System/Collections.stark",
                IsExternal: false,
                IsRoot: false),
            nonOpaqueCollections,
            nonOpaqueSyntaxModel);

        Assert.True(CompilerCli.ShouldEnableDependencyLto(nonOpaqueDocument));

        var collectionsDocument = new LoadedModuleDocument(
            new ResolvedModuleReference(
                "System.Collections",
                collectionsPath,
                IsExternal: false,
                IsRoot: false),
            parseResult,
            syntaxModel);

        Assert.True(CompilerCli.ShouldEnableDependencyLto(collectionsDocument));
    }

    [Fact]
    public void PackageImagePreservesFfiVarargsFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-varargs-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public unsafe ffi varargs fn i32[min max] printf(ascii format);
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var function = Assert.Single(facadeModule.CompilerSections?.TypedInterface?.Functions ?? []);
            var effect = Assert.Single(facadeModule.CompilerSections?.CompilerFacts?.FunctionEffects ?? []);
            var abiFunction = Assert.Single(facadeModule.CompilerSections?.CompilerFacts?.AbiFunctions ?? []);

            Assert.True(function.IsVarargs);
            Assert.True(effect.IsVarargs);
            Assert.True(abiFunction.IsVarargs);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(facadeModule), out var sourceText));
            Assert.Contains("public unsafe ffi(c) varargs fn i32[", sourceText, StringComparison.Ordinal);
            Assert.Contains("printf(ascii format);", sourceText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesConstDefaultNonOverlapAndExplicitRelationQualifiers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-parameter-qualifiers-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public unsafe fn void Inspect(const rawmutptr<i32[min max]> ptr)
                    {
                        return;
                    }

                    public unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                    {
                        return;
                    }

                    public unsafe ffi fn void ExternalTouch(disjoint rawmutptr<i32[min max]> left, disjoint rawmutptr<i32[min max]> right);

                    public unsafe fn void TouchOverlap(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right) where overlap(left, right)
                    {
                        return;
                    }

                    public unsafe fn void TouchSame(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right) where same(left, right)
                    {
                        return;
                    }

                    public struct Reader
                    {
                        i32[min max] Value;

                        public unsafe fn void Read(borrow Reader self, const rawmutptr<i32[min max]> ptr)
                        {
                            return;
                        }
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterface = facadeModule.CompilerSections?.TypedInterface;
            Assert.NotNull(typedInterface);

            var inspect = Assert.Single(typedInterface!.Functions, static function => function.Name == "Inspect");
            Assert.True(Assert.Single(inspect.Parameters).IsConst);

            var touch = Assert.Single(typedInterface.Functions, static function => function.Name == "Touch");
            Assert.All(touch.Parameters, static parameter => Assert.False(parameter.IsDisjoint));
            Assert.Contains(touch.DisjointParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["left", "right"]));

            var externalTouch = Assert.Single(typedInterface.Functions, static function => function.Name == "ExternalTouch");
            Assert.All(externalTouch.Parameters, static parameter => Assert.True(parameter.IsDisjoint));
            Assert.Contains(externalTouch.DisjointParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["left", "right"]));

            var touchOverlap = Assert.Single(typedInterface.Functions, static function => function.Name == "TouchOverlap");
            Assert.Contains(touchOverlap.OverlapParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["left", "right"]));
            Assert.Null(touchOverlap.DisjointParameterGroups);

            var touchSame = Assert.Single(typedInterface.Functions, static function => function.Name == "TouchSame");
            Assert.Contains(touchSame.SameParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["left", "right"]));
            Assert.Null(touchSame.DisjointParameterGroups);

            var reader = Assert.Single(typedInterface.Types, static type => type.Name == "Reader");
            var read = Assert.Single(reader.Methods ?? [], static method => method.Name == "Read");
            Assert.True(read.Parameters[1].IsConst);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("Inspect(const rawmutptr", sourceText, StringComparison.Ordinal);
            Assert.Contains("Touch(rawmutptr", sourceText, StringComparison.Ordinal);
            Assert.Contains("ExternalTouch(disjoint rawmutptr", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("where disjoint(left, right)", sourceText, StringComparison.Ordinal);
            Assert.Contains("where overlap(left, right)", sourceText, StringComparison.Ordinal);
            Assert.Contains("where same(left, right)", sourceText, StringComparison.Ordinal);
            Assert.Contains("Read(borrow Reader self, const rawmutptr", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.True(facts.FunctionSignatures["Facade.Inspect"].Parameters[0].IsConst);
            Assert.All(facts.FunctionSignatures["Facade.Touch"].Parameters, static parameter => Assert.False(parameter.IsDisjoint));
            Assert.Contains(facts.FunctionSignatures["Facade.Touch"].DisjointGroups, static group => group.ParameterNames.SequenceEqual(["left", "right"]));
            Assert.All(facts.FunctionSignatures["Facade.ExternalTouch"].Parameters, static parameter => Assert.True(parameter.IsDisjoint));
            Assert.Contains(facts.FunctionSignatures["Facade.ExternalTouch"].DisjointGroups, static group => group.ParameterNames.SequenceEqual(["left", "right"]));
            Assert.Contains(facts.FunctionSignatures["Facade.TouchOverlap"].OverlapGroups, static group => group.ParameterNames.SequenceEqual(["left", "right"]));
            Assert.Contains(facts.FunctionSignatures["Facade.TouchSame"].SameGroups, static group => group.ParameterNames.SequenceEqual(["left", "right"]));
            Assert.True(facts.FunctionSignatures["Facade.Reader.Read"].Parameters[1].IsConst);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesIndependentLoopContractsInTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-independent-loop-contracts-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public fn i32[min max] CountIndependent<T>(i32[min max] limit, T tag)
                    {
                        stack mut i32[min max] value = 0;
                        while willexit independent (value < limit)
                        {
                            value += 1;
                        }

                        for willexit independent (stack mut u8[0 10] index = 0; index < 4; index += 1)
                        {
                            value += 1;
                        }

                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var template = Assert.Single(
                module.CompilerSections?.GenericTemplates?.Functions ?? [],
                static item => item.QualifiedResolvedName == "Facade.CountIndependent");
            Assert.NotNull(template.TypedBody);

            var whileManifest = Assert.Single(template.TypedBody!.Statements, static statement => statement.Kind == "while");
            Assert.Equal(["independent"], whileManifest.LoopContracts ?? []);
            var forManifest = Assert.Single(template.TypedBody.Statements, static statement => statement.Kind == "for");
            Assert.Equal(["independent"], forManifest.LoopContracts ?? []);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(module), out var facts));
            var importedTemplate = facts.FunctionTemplates["Facade.CountIndependent"];
            Assert.NotNull(importedTemplate.TypedBody);
            var whileSummary = Assert.Single(
                importedTemplate.TypedBody!.Statements,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.While);
            Assert.Equal(["independent"], whileSummary.LoopContractNames);
            var forSummary = Assert.Single(
                importedTemplate.TypedBody.Statements,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.For);
            Assert.Equal(["independent"], forSummary.LoopContractNames);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageConsumerLowersImportedGenericForTraversalTypedBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-for-traversal-template-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Box<T>
                    {
                        T Value;
                    }

                    public fn u64[0 max] CountIndexed<T>(Box<T>[] boxes, T tag)
                    {
                        stack mut u64[0 max] total = 0;
                        for willexit (stack u64[0 max] index, borrow Box<T> box in boxes)
                        {
                            total += index;
                            total += 1;
                        }

                        return total;
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var template = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.CountIndexed");
            Assert.NotNull(template.TypedBody);
            var traversalManifest = Assert.Single(template.TypedBody!.Statements, static statement => statement.Kind == "for-traversal");
            Assert.Equal("index", traversalManifest.TraversalIndexName);
            Assert.Equal("stack", traversalManifest.TraversalIndexStorageClass);
            Assert.Equal("box", traversalManifest.TraversalElementName);
            Assert.NotNull(traversalManifest.TraversalSource);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            var importedTemplate = facts.FunctionTemplates["Facade.CountIndexed"];
            Assert.NotNull(importedTemplate.TypedBody);
            var traversalSummary = Assert.Single(
                importedTemplate.TypedBody!.Statements,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.ForTraversal);
            Assert.Equal("index", traversalSummary.TraversalIndexName);
            Assert.Equal("box", traversalSummary.TraversalElementName);
            Assert.NotNull(traversalSummary.TraversalSourceExpression);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates!.Functions
                    .Select(static item => item.QualifiedResolvedName == "Facade.CountIndexed"
                        ? item with { BodyText = "{ return this is not valid Stark; }" }
                        : item)
                    .ToArray());
            var corruptedModule = facadeModule with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = facadeModule.CompilerSections is null
                    ? null
                    : facadeModule.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            var corruptedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(item => item.ModuleName == "Facade" ? corruptedModule : item)
                    .ToArray()
            };
            File.WriteAllText(manifestPath, corruptedManifest.ToJson());
            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, corruptedManifest, corruptedModule),
                out var sourceText));
            Assert.DoesNotContain("this is not valid Stark", sourceText, StringComparison.Ordinal);
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn u64[0 max] Run()
                    {
                        stack mut Facade.Box<i32[min max]>[2] boxes =
                        {
                            new Facade.Box<i32[min max]>() { Value = 1 },
                            new Facade.Box<i32[min max]>() { Value = 2 }
                        };
                        stack mut Facade.Box<i32[min max]>[] view = boxes;
                        return Facade.CountIndexed(view, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported for-traversal generic typed body builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            MidLevelIrLoweringTests.AssertMirHasNoNullLoweringArtifacts(mir);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.DoesNotContain("this is not valid Stark", llvm!.Text, StringComparison.Ordinal);
            Assert.Contains("__stark_mono_fn_Demo__Facade_CountIndexed__", llvm.Text, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesComptimeGenericDeclarationsAndSymbolicTemplateCalls()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-generics-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Bytes<comptime u8[1 8] N>
                    {
                        u8[0 max][N] Items;
                    }

                    public alias ByteArray<comptime u8[1 8] N> = u8[0 max][N];

                    public finite law u8[0 max] Pick<comptime u8[1 8] N>()
                    {
                        return N;
                    }

                    public finite law u8[0 max] Forward<comptime u8[1 8] N>()
                    {
                        return Pick<comptime N>();
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedInterface = facadeModule.EffectiveTypedInterface!;

            var bytes = Assert.Single(typedInterface.Types, static type => type.Name == "Bytes");
            var bytesParameter = Assert.Single(bytes.ComptimeGenericParameters!);
            Assert.Equal("N", bytesParameter.Name);
            Assert.Equal("integer", bytesParameter.Type.Kind);
            Assert.Equal(8, bytesParameter.Type.BitWidth);

            var byteArray = Assert.Single(typedInterface.TypeAliases!, static alias => alias.Name == "ByteArray");
            var byteArrayParameter = Assert.Single(byteArray.ComptimeGenericParameters!);
            Assert.Equal("N", byteArrayParameter.Name);
            Assert.Equal("integer", byteArrayParameter.Type.Kind);

            var forward = Assert.Single(typedInterface.Functions, static function => function.Name == "Forward");
            var forwardParameter = Assert.Single(forward.ComptimeGenericParameters!);
            Assert.Equal("N", forwardParameter.Name);
            Assert.Equal("integer", forwardParameter.Type.Kind);
            var pick = Assert.Single(typedInterface.Functions, static function => function.Name == "Pick");
            var pickParameter = Assert.Single(pick.ComptimeGenericParameters!);
            Assert.Equal("N", pickParameter.Name);
            Assert.Equal("integer", pickParameter.Type.Kind);

            var forwardTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Forward");
            var directCall = Assert.Single(forwardTemplate.DirectCalls!);
            var directValueArgument = Assert.Single(directCall.ComptimeValueArguments!);
            Assert.Equal("N", directValueArgument.ParameterName);
            Assert.True(directValueArgument.IsSymbolic);
            Assert.Equal("N", directValueArgument.SymbolicSourceName);

            var deferred = Assert.Single(forwardTemplate.DeferredFunctionInstantiations!);
            Assert.Equal("Facade.Pick", deferred.CalleeTemplateName);
            var deferredValueArgument = Assert.Single(deferred.ComptimeValueArguments!);
            Assert.True(deferredValueArgument.IsSymbolic);
            Assert.Equal("N", deferredValueArgument.SymbolicSourceName);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(facadeModule), out var sourceText));
            Assert.Contains("public struct Bytes<comptime u8[1 8] N>", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ByteArray<comptime u8[1 8] N> = u8[0 max][N];", sourceText, StringComparison.Ordinal);
            Assert.Contains("public finite law u8[0 max] Forward<comptime u8[1 8] N>()", sourceText, StringComparison.Ordinal);
            Assert.Contains("return Facade.Pick<comptime N>();", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            var factBytesParameter = Assert.Single(facts.NamedTypes["Facade.Bytes"].ComptimeGenericParams);
            Assert.Equal("N", factBytesParameter.Name);
            var factAliasParameter = Assert.Single(facts.TypeAliases["Facade.ByteArray"].ComptimeGenericParams);
            Assert.Equal("N", factAliasParameter.Name);
            var factForwardParameter = Assert.Single(facts.FunctionSignatures["Facade.Forward"].ComptimeGenericParams);
            Assert.Equal("N", factForwardParameter.Name);
            var factPickParameter = Assert.Single(facts.FunctionSignatures["Facade.Pick"].ComptimeGenericParams);
            Assert.Equal("N", factPickParameter.Name);
            var factDirectValueArgument = Assert.Single(facts.FunctionTemplates["Facade.Forward"].DirectCalls[0].Signature.ComptimeValues);
            Assert.True(factDirectValueArgument.IsSymbolic);
            Assert.Equal("N", factDirectValueArgument.SymbolicSourceName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageConsumerLowersImportedComptimeGenericTemplateTypedBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-template-consumer-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Facade

                    public finite law u8[0 max] Pick<comptime u8[1 8] N>()
                    {
                        return N;
                    }

                    public finite law u8[0 max] Forward<comptime u8[1 8] N>()
                    {
                        return Pick<comptime N>();
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates!.Functions
                    .Select(static item => item.QualifiedResolvedName == "Facade.Forward"
                        ? item with { BodyText = "{ return this is not valid Stark; }" }
                        : item)
                    .ToArray());
            var corruptedModule = facadeModule with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = facadeModule.CompilerSections is null
                    ? null
                    : facadeModule.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            var corruptedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(item => item.ModuleName == "Facade" ? corruptedModule : item)
                    .ToArray()
            };
            File.WriteAllText(manifestPath, corruptedManifest.ToJson());
            File.Delete(sourcePath);

            var hirProbeResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law u8[0 max] Run()
                    {
                        return Facade.Forward<5>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-hir"));

            Assert.True(hirProbeResult.Succeeded, string.Join(", ", hirProbeResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(hirProbeResult.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
            Assert.NotNull(hir);
            var pickSpecialization = Assert.Single(
                hir!.Functions,
                static function => function.Name.Contains("Facade_Pick", StringComparison.Ordinal)
                    && function.Name.Contains("N_5", StringComparison.Ordinal));
            var pickValue = Assert.Single(pickSpecialization.Signature.ComptimeValues);
            Assert.Equal("N", pickValue.ParameterName);
            Assert.Equal(5, pickValue.IntegerValue);
            Assert.False(pickValue.IsSymbolic);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law u8[0 max] Run()
                    {
                        return Facade.Forward<5>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported comptime generic typed body builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.Contains("__stark_mono_fn_Demo__Facade_Forward__N_5", llvm!.Text, StringComparison.Ordinal);
            Assert.Contains("ret i8 5", ExtractDefinitionBody(llvm.Text, "__stark_mono_fn_Demo__Facade_Pick__N_5"));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePublishesEmptyImportedModulesToKeepManifestModuleGraphClosed()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-empty-import-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var markerPath = Path.Combine(tempDirectory.FullName, "Marker.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            File.WriteAllText(
                facadePath,
                """
                export import Marker
                module Facade

                public fn i32[min max] Identity(i32[min max] value)
                {
                    return value;
                }
                """);
            File.WriteAllText(markerPath, "module Marker\n");

            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(File.ReadAllText(facadePath), facadePath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);
            File.Delete(markerPath);

            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.Contains(facadeModule.EffectiveSourceSurface.Imports ?? [], static import => import.ModuleName == "Marker" && import.IsExported);

            var markerModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Marker");
            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(markerModule), out var markerSource));
            Assert.Contains("module Marker", markerSource, StringComparison.Ordinal);

            var resolver = new FileSystemModuleResolver(tempDirectory.FullName);
            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run()
                    {
                        return Facade.Identity(4);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: resolver,
                    StopAfterPassId: "type-check"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(resolver.TryResolveModule("Marker", out var resolvedMarker));
            Assert.Equal(manifestPath, resolvedMarker.ManifestPath);
            Assert.True(resolver.TryLoadModuleDocument(resolvedMarker, targetInfo: null, out var markerDocument));
            Assert.Equal("Marker", markerDocument.SyntaxModel.ModuleName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageBackedDefaultNonOverlapCallsRejectOverlappingArguments()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-where-disjoint-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn void Touch(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                {
                    return;
                }
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn void Run(rawmutptr<i32[min max]> ptr)
                    {
                        Facade.Touch(ptr, ptr);
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "type-check"));

            Assert.False(result.Succeeded);
            Assert.Contains(
                result.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3030"
                    && diagnostic.Message.Contains("violates disjoint parameter contract", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageBackedWhereOverlapCallsAllowOverlappingArguments()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-where-overlap-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn void TouchOverlap(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right) where overlap(left, right)
                {
                    return;
                }
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn void Run(rawmutptr<i32[min max]> ptr)
                    {
                        Facade.TouchOverlap(ptr, ptr);
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "type-check"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageBackedSubregionDisjointContractsRejectOverlappingImportedCalls()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-subregion-disjoint-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn void Window(
                    rawptr<i32[min max]>[8] source,
                    rawmutptr<i32[min max]>[8] destination)
                    where overlap(source, destination)
                    where disjoint(source[2, 4], destination[0, 4])
                    {
                        return;
                }
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(facadeModule), out var sourceText));
            Assert.Contains("where disjoint(source[2, 4], destination[0, 4])", sourceText, StringComparison.Ordinal);

            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn void Run(rawmutptr<i32[min max]>[8] buffer)
                    {
                        Facade.Window(buffer, buffer);
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "type-check"));

            Assert.False(result.Succeeded);
            Assert.Contains(
                result.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3030"
                    && diagnostic.Message.Contains("disjoint subregion parameter contract", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageBackedRetborrowDynamicIndexTemplatesReturnElementAddresses()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-retborrow-dynamic-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Bag<T>
                {
                    public T[] Items;

                    public law retborrow T Get(borrow Bag<T> self, u64[0 2 ** 63 - 1] index)
                    {
                        return self.Items[index];
                    }
                }
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run()
                    {
                        stack mut Facade.Bag<i32[min max]> bag = new();
                        return bag.Get(0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    EmitLlvmIr: true));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);

            var getBody = ExtractDefinitionBodyContaining(llvmModule.Text, "Facade_Bag_Get__i32");
            var returned = Regex.Match(getBody, @"ret ptr (?<value>%[A-Za-z0-9_]+)");
            Assert.True(returned.Success, getBody);
            var returnedValue = returned.Groups["value"].Value;
            Assert.Contains($"{returnedValue} = getelementptr i32, ptr ", getBody, StringComparison.Ordinal);
            Assert.Contains($"ret ptr {returnedValue}", getBody, StringComparison.Ordinal);
            Assert.DoesNotContain($"{returnedValue} = load i32", getBody, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesUnsignedIntegerFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-unsigned-integers-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Bytes.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Bytes

                    public fn u8[0 127] Keep(u8[min 127] value)
                    {
                        return value;
                    }

                    public fn u32[0 max] Keep32(u32[0 max] value)
                    {
                        return value;
                    }

                    public fn u96[0 max] Keep96(u96[0 max] value)
                    {
                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Bytes.lib" : "libBytes.a"));
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Bytes");
            var functions = module.CompilerSections?.TypedInterface?.Functions ?? [];
            var function = Assert.Single(functions, static item => item.Name == "Keep");
            var function32 = Assert.Single(functions, static item => item.Name == "Keep32");
            var function96 = Assert.Single(functions, static item => item.Name == "Keep96");

            Assert.True(function.ReturnType.IsUnsigned);
            Assert.True(function.Parameters[0].Type.IsUnsigned);
            Assert.True(function32.ReturnType.IsUnsigned);
            Assert.True(function32.Parameters[0].Type.IsUnsigned);
            Assert.True(function96.ReturnType.IsUnsigned);
            Assert.True(function96.Parameters[0].Type.IsUnsigned);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(module), out var sourceText));
            Assert.Contains("public fn u8[0 127] Keep(u8[0 127] value)", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn u32[0 max] Keep32(u32[0 max] value)", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn u96[0 max] Keep96(u96[0 max] value)", sourceText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesStructLayoutMetadataAndConcreteFieldOffsets()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-struct-layout-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Native.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Native

                    [StructLayout(C), Pack(1), Align(4)]
                    public struct Packet
                    {
                        public u8[0 max] Tag;
                        public u32[0 max] Length;
                    }

                    [StructLayout(Explicit), Align(4)]
                    public struct WordParts
                    {
                        [FieldOffset(0)] public u32[0 max] Whole;
                        [FieldOffset(0)] public u16[0 max] Low;
                        [FieldOffset(2)] public u16[0 max] High;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Native.lib" : "libNative.a"));
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Native");

            var sourcePacket = Assert.Single(module.EffectiveSourceSurface.Types ?? [], static type => type.Name == "Packet");
            Assert.Equal("C", sourcePacket.StructLayout);
            Assert.Equal(1, sourcePacket.PackBytes);
            Assert.Equal(4, sourcePacket.AlignBytes);

            var sourceWordParts = Assert.Single(module.EffectiveSourceSurface.Types ?? [], static type => type.Name == "WordParts");
            Assert.Equal("Explicit", sourceWordParts.StructLayout);
            Assert.Equal(4, sourceWordParts.AlignBytes);
            Assert.Equal(2, Assert.Single(sourceWordParts.Fields, static field => field.Name == "High").ExplicitOffsetBytes);

            var typedPacket = Assert.Single(module.EffectiveTypedInterface?.Types ?? [], static type => type.Name == "Packet");
            Assert.Equal("C", typedPacket.StructLayout);
            Assert.Equal(1, typedPacket.PackBytes);
            Assert.Equal(4, typedPacket.AlignBytes);

            var typedWordParts = Assert.Single(module.EffectiveTypedInterface?.Types ?? [], static type => type.Name == "WordParts");
            Assert.Equal("Explicit", typedWordParts.StructLayout);
            Assert.Equal(4, typedWordParts.AlignBytes);
            Assert.Equal(0, Assert.Single(typedWordParts.Fields, static field => field.Name == "Low").ExplicitOffsetBytes);

            var packetLayout = Assert.Single(module.EffectiveCompilerFacts?.ConcreteLayouts ?? [], static layout => layout.QualifiedTypeName == "Native.Packet");
            Assert.Equal(8, packetLayout.SizeBytes);
            Assert.Equal(4, packetLayout.AlignmentBytes);
            var lengthLayout = Assert.Single(packetLayout.Fields ?? [], static field => field.Name == "Length");
            Assert.Equal(1, lengthLayout.OffsetBytes);
            Assert.True(lengthLayout.IsMisaligned);

            var wordPartsLayout = Assert.Single(module.EffectiveCompilerFacts?.ConcreteLayouts ?? [], static layout => layout.QualifiedTypeName == "Native.WordParts");
            Assert.Equal(4, wordPartsLayout.SizeBytes);
            Assert.Equal(4, wordPartsLayout.AlignmentBytes);
            Assert.Equal(2, Assert.Single(wordPartsLayout.Fields ?? [], static field => field.Name == "High").OffsetBytes);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(module), out var sourceText));
            Assert.Contains("[StructLayout(C), Pack(1), Align(4)]", sourceText, StringComparison.Ordinal);
            Assert.Contains("[StructLayout(Explicit), Align(4)]", sourceText, StringComparison.Ordinal);
            Assert.Contains("[FieldOffset(2)]", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(module), out var facts));
            Assert.Equal(1, facts.ConcreteLayouts["Native.Packet"].Fields.Single(static field => field.Name == "Length").OffsetBytes);
            Assert.True(facts.ConcreteLayouts["Native.Packet"].Fields.Single(static field => field.Name == "Length").IsMisaligned);
            Assert.Equal(2, facts.ConcreteLayouts["Native.WordParts"].Fields.Single(static field => field.Name == "High").OffsetBytes);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImageBuilderPublishesTypedInterfaceImportsAsStructuredDependencySurface()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-imports-");

        try
        {
            var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Bits.stark"),
                """
                module Bits

                public record Token(i32[min max] value)
                {
                }
                """);
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public fn i32[min max] Id(i32[min max] value)
                {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    export import Bits
                    module Facade

                    public fn Bits.Token Forward(Bits.Token value)
                    {
                        return value;
                    }
                    """,
                    rootPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterfaceImports = facadeModule.CompilerSections?.TypedInterface?.Imports;

            Assert.NotNull(typedInterfaceImports);
            Assert.Contains(typedInterfaceImports!, static import => import.ModuleName == "Math" && !import.IsExported);
            Assert.Contains(typedInterfaceImports!, static import => import.ModuleName == "Bits" && import.IsExported);
            Assert.Null(facadeModule.Imports);
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
    public void PackageImageBuilderPublishesInternalDependencyImportsNeededByImportedBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-internal-imports-");

        try
        {
            var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Runtime.stark"),
                """
                module Runtime

                internal fn i32[min max] Hidden()
                {
                    return 7;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Runtime
                    module Facade

                    public fn i32[min max] Run()
                    {
                        return Runtime.Hidden();
                    }
                    """,
                    rootPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterfaceImports = facadeModule.CompilerSections?.TypedInterface?.Imports;

            Assert.NotNull(typedInterfaceImports);
            Assert.Contains(typedInterfaceImports!, static import => import.ModuleName == "Runtime" && !import.IsExported);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(
                    Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json"),
                    Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                    manifest,
                    facadeModule),
                out var sourceText));
            Assert.Contains("import Runtime", sourceText, StringComparison.Ordinal);
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
    public void PackageImageBuilderPublishesLinkageMetadataForModuleObjectSelection()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-linkage-");

        try
        {
            var rootPath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Runtime.stark"),
                """
                module Runtime

                internal fn i32[min max] Hidden()
                {
                    return 7;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Runtime
                    module Facade

                    public fn i32[min max] Run()
                    {
                        return Runtime.Hidden();
                    }
                    """,
                    rootPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var runtimeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Runtime");
            var objectExtension = OperatingSystem.IsWindows() ? ".obj" : ".o";

            var facadeLinkage = facadeModule.CompilerSections?.CompilerFacts?.Linkage;
            Assert.NotNull(facadeLinkage);
            Assert.Equal($"root{objectExtension}", facadeLinkage!.ObjectFileName);
            Assert.Contains("Facade_Run", facadeLinkage.DefinedSymbols);
            Assert.Contains("Runtime_Hidden", facadeLinkage.ReferencedSymbols ?? []);

            var runtimeLinkage = runtimeModule.CompilerSections?.CompilerFacts?.Linkage;
            Assert.NotNull(runtimeLinkage);
            Assert.Equal($"Runtime{objectExtension}", runtimeLinkage!.ObjectFileName);
            Assert.Contains("Runtime_Hidden", runtimeLinkage.DefinedSymbols);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(
                new ResolvedPackageModule(
                    Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json"),
                    libraryPath,
                    manifest,
                    facadeModule),
                out var facadeFacts));
            Assert.Equal($"root{objectExtension}", facadeFacts.Linkage?.ObjectFileName);
            Assert.Contains("Runtime_Hidden", facadeFacts.Linkage?.ReferencedSymbols ?? new HashSet<string>(StringComparer.Ordinal));
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
    public void PackageImagePreservesConstNumericStorageWithoutReconstructingScalarRanges()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-const-numeric-storage-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public const Small = 80;
                    public const Big = 2 ** 16;
                    public const Float64 = 80.0;
                    public const Float32 = 80.0f;
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedGlobals = module.EffectiveTypedInterface!.Globals;

            AssertConstIntegerType(typedGlobals, "Small", 8, "80", "80");
            AssertConstIntegerType(typedGlobals, "Big", 24, "65536", "65536");
            AssertConstFloatType(typedGlobals, "Float64", 64);
            AssertConstFloatType(typedGlobals, "Float32", 32);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var sourceText));

            Assert.Contains("public const u8 Small = 0;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public const u24 Big = 0;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public const f64 Float64 = 0;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public const f32 Float32 = 0;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("const u8[80 80]", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("const u24[65536 65536]", sourceText, StringComparison.Ordinal);
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
    public void StructuredPackageImageSourceIgnoresCorruptedBodyTextWhenTypedBodyFactsExist()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-corrupt-body-text-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public fn T Identity<T>(T value)
                    {
                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var templates = module.EffectiveGenericTemplates!.Functions
                .Select(static template => template.QualifiedResolvedName == "Facade.Identity"
                    ? template with { BodyText = "{ return this is not valid Stark; }" }
                    : template)
                .ToArray();
            var corruptedTemplates = new StarkPackageGenericTemplateSection(templates);
            var corruptedModule = module with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = module.CompilerSections is null
                    ? null
                    : module.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            var corruptedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(item => item.ModuleName == "Facade" ? corruptedModule : item)
                    .ToArray()
            };
            var identityTemplate = Assert.Single(
                corruptedModule.EffectiveGenericTemplates!.Functions,
                static template => template.QualifiedResolvedName == "Facade.Identity");
            Assert.NotNull(identityTemplate.TypedBody);

            Assert.True(PackageImageLoader.TryBuildStructuredModuleDocument(
                new ResolvedPackageModule(manifestPath, libraryPath, corruptedManifest, corruptedModule),
                out var document));

            Assert.Contains("public fn T Identity<T>(T value);", document.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("this is not valid Stark", document.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return value", document.ParseResult.SourceText, StringComparison.Ordinal);
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
    public void PackageImageGenericTemplatesPublishAllBoundOperationFamilies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-bound-ops-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "System.Text.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "System.Text.starkpkg.json" : "libSystem.Text.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "System.Text.lib" : "libSystem.Text.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module System.Text

                    public finite law ascii AsciiView(Ascii source);
                    public unsafe finite bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);

                    enum Status
                    {
                        Ok,
                        Err(i32[min max]),
                        Named
                        {
                            Code: i32[min max]
                        },
                    }

                    struct Box
                    {
                        i32[min max] Value;

                        fn i32[min max] Get(borrow Box self)
                        {
                            return self.Value;
                        }

                        fn bool Fill(borrow Box self, out i32[min max] value)
                        {
                            value = self.Value;
                            return true;
                        }
                    }

                    fn i32[min max] Inc(i32[min max] value)
                    {
                        return value + 1;
                    }

                    unsafe fn i32[min max] Apply(fnptr<fn i32[min max](i32[min max])> op, i32[min max] value)
                    {
                        return op(value);
                    }

                    fn bool Write(out i32[min max] value)
                    {
                        value = 9;
                        return true;
                    }

                    unsafe fn bool ApplyOut(fnptr<fn bool(out i32[min max])> op, out i32[min max] value)
                    {
                        return op(value);
                    }

                    fn i32[min max] Choose(bool flag)
                    {
                        switch (flag)
                        {
                            case true:
                                return 1;
                            case false:
                                return 0;
                        }
                    }

                    fn i32[min max] Score(Status status)
                    {
                        switch (status)
                        {
                            case Status.Ok:
                                return 1;
                            case Status.Err(var error):
                                return error;
                            case Status.Named
                            {
                                Code: var code
                            }:
                                return code;
                        }
                    }

                    public unsafe fn T Run<T>(
                        T input,
                        fnptr<fn i32[min max](i32[min max])> pointerOp,
                        closure<fn i32[min max](i32[min max])> closureOp)
                        {
                            stack mut Box box = new Box()
                        {
                            Value = 3
                        };
                        stack mut i32[min max][2] values =
                        {
                            4, 5
                        };
                        stack mut dynamic u32[0 max] items = new(1);
                        stack Ascii label[4 + 4] = $"ok";
                        stack Ascii joined[12] = label + "!";
                        stack i64[min max] boxSize = sizeof(Box);
                        stack i64[min max] boxAlign = alignof(Box);
                        stack mut i64[min max] marker = 0;
                        switch (true)
                        {
                            case true:
                                marker = boxSize;
                            case false:
                                marker = boxAlign;
                        }
                        stack Status ok = Status.Ok;
                        stack Status named = Status.Named
                        {
                            Code: 5
                        };
                        values[0] = Inc(box.Get());
                        items.Reserve(1);
                        if (!box.Fill(values[1]))
                        {
                            return input;
                        }
                        if (!ApplyOut(Write, values[1]))
                        {
                            return input;
                        }
                        if (Score(named) == 0)
                        {
                            return input;
                        }
                        true ? pointerOp(1) : closureOp(2);
                        stack i32[min max] total = values[0] + values[1] + Apply(Inc, 2) + pointerOp(3) + closureOp(6) + Choose(true) + Score(Status.Ok) + Score(Status.Err(4));
                        return input;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "System.Text");
            var template = Assert.Single(
                module.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "System.Text.Run");
            Assert.NotNull(template.TypedBody);
            Assert.Contains("\"Kind\": \"enum-value\"", manifest.ToJson(), StringComparison.Ordinal);
            Assert.Contains("\"EnumLayouts\"", manifest.ToJson(), StringComparison.Ordinal);
            Assert.Contains("\"StorageCapacity\": 8", manifest.ToJson(), StringComparison.Ordinal);
            Assert.NotNull(template.BoundOperations);

            var kinds = template.BoundOperations!
                .Select(static operation => operation.Kind)
                .ToHashSet(StringComparer.Ordinal);
            var requiredKinds = new HashSet<string>(
                [
                    "direct-call",
                    "member-call",
                    "function-pointer-call",
                    "closure-call",
                    "index-access",
                    "object-creation",
                    "enum-construction",
                    "enum-call",
                    "enum-value",
                    "dynamic-storage-operation",
                    "text-interpolation",
                    "text-build",
                    "layout-query",
                    "switch-dispatch"
                ],
                StringComparer.Ordinal);
            foreach (var requiredKind in requiredKinds)
            {
                Assert.Contains(requiredKind, kinds);
            }

            Assert.Contains(
                template.BoundOperations!,
                static operation => operation.Kind == "function-pointer-call"
                    && operation.FunctionPointerType?.Kind == "functionpointer");
            Assert.Contains(
                template.BoundOperations!,
                static operation => operation.Kind == "dynamic-storage-operation"
                    && operation.ReceiverType?.ElementType?.Kind == "integer"
                    && operation.ResultType.Kind == "void");

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(module), out var facts));
            var importedTemplate = Assert.Single(facts.FunctionTemplates, static pair => pair.Key == "System.Text.Run").Value;
            Assert.Contains(importedTemplate.BoundOperations, static operation => operation.Operation is BoundFunctionPointerCallOperation);
            Assert.Contains(importedTemplate.BoundOperations, static operation => operation.Operation is BoundDynamicStorageOperation);
            Assert.Contains(importedTemplate.BoundOperations, static operation => operation.Operation is BoundSwitchDispatchOperation);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                module.EffectiveGenericTemplates!.Functions
                    .Select(static item => item.QualifiedResolvedName == "System.Text.Run"
                        ? item with { BodyText = "{ return this is not valid Stark; }" }
                        : item)
                    .ToArray());
            var corruptedModule = module with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = module.CompilerSections is null
                    ? null
                    : module.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            var corruptedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(item => item.ModuleName == "System.Text" ? corruptedModule : item)
                    .ToArray()
            };
            File.WriteAllText(manifestPath, corruptedManifest.ToJson());
            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, corruptedManifest, corruptedModule),
                out var corruptedSourceText));
            Assert.DoesNotContain("this is not valid Stark", corruptedSourceText, StringComparison.Ordinal);
            File.Delete(sourcePath);

            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import System.Text
                    module Demo

                    fn i32[min max] LocalInc(i32[min max] value)
                    {
                        return value + 1;
                    }

                    unsafe fn i32[min max] Run()
                    {
                        stack fnptr<fn i32[min max](i32[min max])> pointerOp = LocalInc;
                        stack closure<fn i32[min max](i32[min max])> closureOp =
                            (i32[min max] value) => value + 6;
                        return System.Text.Run(7, pointerOp, closureOp);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported generic typed-body consumer builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            MidLevelIrLoweringTests.AssertMirHasNoNullLoweringArtifacts(mir);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.DoesNotContain("this is not valid Stark", llvm!.Text, StringComparison.Ordinal);
            Assert.Contains("__stark_mono_fn_Demo__System_Text_Run__", llvm.Text, StringComparison.Ordinal);
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
    public void PackageImageGenericTemplatesPreservePropertyAndListSwitchPatterns()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-switch-patterns-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Boxed
                    {
                        i32[min max] Value;
                        bool Enabled;
                    }

                    public finite law T Run<T>(T input, Boxed box, i32[min max][2] values)
                    {
                        stack mut i32[min max] marker = 0;
                        switch (box)
                        {
                            case Boxed { Enabled: true, Value: var found }:
                                marker = found;
                            case Boxed { Value: _, Enabled: _ }:
                                marker = 1;
                        }

                        switch (values)
                        {
                            case [1, var right]:
                                marker += right;
                            case [_, _]:
                                marker += 0;
                        }

                        return input;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var template = Assert.Single(
                module.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Run");
            var json = manifest.ToJson();

            Assert.NotNull(template.TypedBody);
            Assert.Contains("\"Kind\": \"list-pattern\"", json, StringComparison.Ordinal);
            Assert.Contains("\"AggregatePatterns\"", json, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(module), out var facts));
            var importedTemplate = facts.FunctionTemplates["Facade.Run"];
            Assert.Contains(
                importedTemplate.AggregatePatterns,
                static pattern => pattern.Members.Count == 2
                    && pattern.Members.Any(static member => member.FieldName == "Enabled" && member.FieldIndex == 1)
                    && pattern.Members.Any(static member => member.FieldName == "Value" && member.FieldIndex == 0));
            Assert.Contains(
                importedTemplate.TypedBody!.Statements
                    .Where(static statement => statement.SwitchCases.Count > 0)
                    .SelectMany(static statement => statement.SwitchCases),
                static switchCase => switchCase.Kind == ImportedTemplateTypedSwitchCaseKind.ListPattern);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                module.EffectiveGenericTemplates!.Functions
                    .Select(static item => item.QualifiedResolvedName == "Facade.Run"
                        ? item with { BodyText = "{ return this is not valid Stark; }" }
                        : item)
                    .ToArray());
            var corruptedModule = module with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = module.CompilerSections is null
                    ? null
                    : module.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            var corruptedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(item => item.ModuleName == "Facade" ? corruptedModule : item)
                    .ToArray()
            };
            File.WriteAllText(manifestPath, corruptedManifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run()
                    {
                        stack Facade.Boxed box = new Facade.Boxed()
                        {
                            Value = 3,
                            Enabled = true
                        };
                        stack i32[min max][2] values =
                        {
                            1, 2
                        };
                        return Facade.Run(7, box, values);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported generic typed-body switch patterns build");
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
    public void PackageImageConsumerLowersImportedGenericTypedBodyAfterSourceAndBodyTextAreRemoved()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-bound-typed-consumer-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Pair<T>
                    {
                        T Left;
                        T Right;
                    }

                    public fn T Pick<T>(T left, T right, bool choose)
                    {
                        stack Pair<T> pair = new Pair<T>()
                        {
                            Left = left, Right = right
                        };
                        switch (choose)
                        {
                            case true:
                                return pair.Left;
                            case false:
                                return pair.Right;
                        }
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var pickTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Pick");
            Assert.NotNull(pickTemplate.TypedBody);
            Assert.Contains(pickTemplate.BoundOperations ?? [], static operation => operation.Kind == "object-creation");
            Assert.Contains(pickTemplate.BoundOperations ?? [], static operation => operation.Kind == "switch-dispatch");

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates!.Functions
                    .Select(static template => template.QualifiedResolvedName == "Facade.Pick"
                        ? template with { BodyText = "{ return this is not valid Stark; }" }
                        : template)
                    .ToArray());
            var corruptedModule = facadeModule with
            {
                GenericTemplates = corruptedTemplates,
                CompilerSections = facadeModule.CompilerSections is null
                    ? null
                    : facadeModule.CompilerSections with { GenericTemplates = corruptedTemplates }
            };
            var corruptedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(item => item.ModuleName == "Facade" ? corruptedModule : item)
                    .ToArray()
            };
            File.WriteAllText(manifestPath, corruptedManifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(bool choose)
                    {
                        stack i32[min max] left = 3;
                        stack i32[min max] right = 4;
                        return Facade.Pick(left, right, choose);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported generic typed-body consumer builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            MidLevelIrLoweringTests.AssertMirHasNoNullLoweringArtifacts(mir);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.DoesNotContain("this is not valid Stark", llvm!.Text, StringComparison.Ordinal);
            Assert.Contains("__stark_mono_fn_Demo__Facade_Pick__i32", llvm.Text, StringComparison.Ordinal);
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
    public void PackageImageLoaderPrefersTypedInterfaceImportsOverExplicitSourceSurfaceImports()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions: [],
                Types: [],
                Globals: [],
                TypeAliases: [],
                Imports:
                [
                    new StarkPackageImportManifest("TypedDep", IsExported: false)
                ]),
            GenericTemplates: new StarkPackageGenericTemplateSection(
                [
                    new StarkPackageFunctionTemplateManifest(
                        QualifiedResolvedName: "Facade.Identity#(i32)",
                        QualifiedName: "Facade.Identity",
                        OverloadKey: "(i32)",
                        BodyText: "{ return value; }")
                ]),
            SourceSurface: new StarkPackageSourceSurfaceSection(
                Imports:
                [
                    new StarkPackageImportManifest("LegacyDep", IsExported: false)
                ],
                ReExports: [],
                Functions: [],
                Types: [],
                Globals: [],
                TypeAliases: []));

        var resolvedModule = CreateResolvedPackageModule(facadeModule);

        Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
        Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));

        var typedImport = Assert.Single(syntaxModel.Imports);
        Assert.Equal("TypedDep", typedImport.ModuleName);
        Assert.False(typedImport.IsReExport);
        Assert.Contains("import TypedDep", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("import LegacyDep", sourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageImageLoaderFallsBackToLegacyFlatImportsWhenTypedInterfaceImportsAreMissing()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "Identity",
                        QualifiedName: "Facade.Identity",
                        Visibility: "public",
                        SymbolName: "Facade.Identity",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference("integer", BitWidth: 32),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "value",
                                new StarkPackageTypeReference("integer", BitWidth: 32))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: []),
            Imports:
            [
                new StarkPackageImportManifest("LegacyMath", IsExported: false)
            ],
            SourceSurface: null);

        var resolvedModule = CreateResolvedPackageModule(facadeModule);

        Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
        Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
        Assert.Contains(syntaxModel.Imports, static import => import.ModuleName == "LegacyMath" && !import.IsReExport);
        Assert.Contains("import LegacyMath", sourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageImageLoaderLegacyFlatImportsDoNotHideLegacyReExports()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports:
            [
                new StarkPackageReExportManifest("Bits")
            ],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions: [],
                Types: [],
                Globals: [],
                TypeAliases: []),
            Imports: [],
            SourceSurface: null);

        var resolvedModule = CreateResolvedPackageModule(facadeModule);

        Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));
        Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
        Assert.Contains(syntaxModel.Imports, static import => import.ModuleName == "Bits" && import.IsReExport);
        Assert.Contains("export import Bits", sourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageImagePreservesTraitConformanceMetadata()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-traits-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Facade

                    public trait Drawable
                    {
                        finite law i32[min max] Width(borrow Self self);

                        finite law i32[min max] Twice(borrow Self self)
                        {
                            return self.Width() + self.Width();
                        }
                    }

                    public struct Widget : Drawable
                    {
                        public i32[min max] W;

                        public finite law i32[min max] Width(borrow Widget self)
                        {
                            return self.W;
                        }
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");

            var typedWidget = Assert.Single(module.EffectiveTypedInterface?.Types ?? [], static type => type.Name == "Widget");
            Assert.Contains("Drawable", typedWidget.ImplementedTraits ?? []);

            var typedDrawable = Assert.Single(module.EffectiveTypedInterface?.Types ?? [], static type => type.Name == "Drawable");
            Assert.False(Assert.Single(typedDrawable.Methods ?? [], static method => method.Name == "Width").HasBody);
            Assert.True(Assert.Single(typedDrawable.Methods ?? [], static method => method.Name == "Twice").HasBody);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(module), out var sourceText));
            Assert.Contains("public struct Widget : Drawable", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(module), out var facts));
            Assert.Contains("Facade.Drawable", facts.NamedTypes["Facade.Widget"].ImplementedTraits);
            Assert.False(facts.FunctionSignatures["Facade.Drawable.Width"].HasBody);
            Assert.True(facts.FunctionSignatures["Facade.Drawable.Twice"].HasBody);

            File.Delete(sourcePath);
            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    struct Broken : Facade.Drawable
                    {
                        i32[min max] W;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "semantic-validate"));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3032"
                    && diagnostic.Message.Contains("Facade.Drawable.Width", StringComparison.Ordinal));
            Assert.DoesNotContain(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3032"
                    && diagnostic.Message.Contains("Facade.Drawable.Twice", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void PackageImagePreservesSystemCCVoidTypedInterfaceSurface()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-system-c-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var manifestPath = Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json");

        try
        {
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public unsafe ffi(c) fn rawmutptr<System.C.c_void> Allocate(System.C.c_size_t bytes);
                    public unsafe ffi(c) fn void Free(rawmutptr<System.C.c_void> ptr);
                    """,
                    sourcePath),
                new CompilerOptions(
                    StopAfterPassId: "lower-abi",
                    TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedInterface = module.EffectiveTypedInterface!;

            var allocate = Assert.Single(typedInterface.Functions, static function => function.Name == "Allocate");
            Assert.Equal("rawpointer", allocate.ReturnType.Kind);
            Assert.True(allocate.ReturnType.IsMutablePointer);
            Assert.Equal("cvoid", allocate.ReturnType.ElementType?.Kind);
            Assert.Equal("integer", Assert.Single(allocate.Parameters).Type.Kind);
            Assert.Equal(64, Assert.Single(allocate.Parameters).Type.BitWidth);
            Assert.True(Assert.Single(allocate.Parameters).Type.IsUnsigned);

            var free = Assert.Single(typedInterface.Functions, static function => function.Name == "Free");
            var freeParameter = Assert.Single(free.Parameters).Type;
            Assert.Equal("rawpointer", freeParameter.Kind);
            Assert.True(freeParameter.IsMutablePointer);
            Assert.Equal("cvoid", freeParameter.ElementType?.Kind);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var sourceText));

            Assert.Contains("rawmutptr<System.C.c_void> Allocate", sourceText, StringComparison.Ordinal);
            Assert.Contains("void Free(rawmutptr<System.C.c_void> ptr)", sourceText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static ResolvedPackageModule CreateResolvedPackageModule(StarkPackageModuleManifest module)
    {
        return new ResolvedPackageModule(
            $"/virtual/{module.ModuleName}.starkpkg.json",
            $"/virtual/lib{module.ModuleName}.a",
            new StarkPackageManifest(module.ModuleName, $"lib{module.ModuleName}.a", [module]),
            module);
    }

    private static string ExtractDefinitionBody(string llvm, string symbolName)
    {
        var headerMatch = Regex.Match(
            llvm,
            $@"^define [^\n]*@{Regex.Escape(symbolName)}\([^\n]*\)[^\n]*",
            RegexOptions.Multiline);
        Assert.True(headerMatch.Success, $"Expected LLVM definition for '{symbolName}'.");

        var start = headerMatch.Index;
        var nextDefinition = llvm.IndexOf("\ndefine ", start + headerMatch.Length, StringComparison.Ordinal);
        return nextDefinition < 0
            ? llvm[start..]
            : llvm[start..nextDefinition];
    }

    private static string ExtractDefinitionBodyContaining(string llvm, string symbolNameFragment)
    {
        var headerMatch = Regex.Match(
            llvm,
            $@"^define [^\n]*@[^\s(]*{Regex.Escape(symbolNameFragment)}[^\s(]*\([^\n]*\)[^\n]*",
            RegexOptions.Multiline);
        Assert.True(headerMatch.Success, $"Expected LLVM definition containing '{symbolNameFragment}'.");

        var start = headerMatch.Index;
        var nextDefinition = llvm.IndexOf("\ndefine ", start + headerMatch.Length, StringComparison.Ordinal);
        return nextDefinition < 0
            ? llvm[start..]
            : llvm[start..nextDefinition];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root.");
    }

    private static void AssertConstIntegerType(
        IReadOnlyList<StarkPackageTypedGlobalManifest> globals,
        string name,
        int bitWidth,
        string rangeMin,
        string rangeMax)
    {
        var global = Assert.Single(globals, item => item.Name == name);

        Assert.Equal("globalconstant", global.Kind);
        Assert.Equal("integer", global.Type.Kind);
        Assert.Equal(bitWidth, global.Type.BitWidth);
        Assert.Equal(rangeMin, global.Type.RangeMin);
        Assert.Equal(rangeMax, global.Type.RangeMax);
    }

    private static void AssertConstFloatType(
        IReadOnlyList<StarkPackageTypedGlobalManifest> globals,
        string name,
        int bitWidth)
    {
        var global = Assert.Single(globals, item => item.Name == name);

        Assert.Equal("globalconstant", global.Kind);
        Assert.Equal("float", global.Type.Kind);
        Assert.Equal(bitWidth, global.Type.BitWidth);
    }
}
