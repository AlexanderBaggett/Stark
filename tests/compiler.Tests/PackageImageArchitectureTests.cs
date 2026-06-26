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
    public void PackageImageKeepsPackageBackedImportsWithoutRepublishingImportedModules()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-source-owned-imports-");

        try
        {
            var dependencySourcePath = Path.Combine(tempDirectory.FullName, "Dependency.stark");
            var dependencyResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Dependency

                    public finite law i32[min max] Value()
                    {
                        return 7;
                    }
                    """,
                    dependencySourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(dependencyResult.Succeeded, string.Join(", ", dependencyResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var dependencyPackagePath = Path.Combine(tempDirectory.FullName, "libDependency.starkpkg");
            var dependencyManifest = PackageImageBuilder.Create(
                dependencyResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Dependency.lib" : "libDependency.a"));
            File.WriteAllBytes(dependencyPackagePath, PackageImageBinaryFormat.Encode(dependencyManifest));

            var consumerSourcePath = Path.Combine(tempDirectory.FullName, "Consumer.stark");
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Dependency
                    module Consumer

                    public finite law i32[min max] Use()
                    {
                        return Value();
                    }
                    """,
                    consumerSourcePath),
                new CompilerOptions(
                    StopAfterPassId: "lower-abi",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var consumerManifest = PackageImageBuilder.Create(
                consumerResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Consumer.lib" : "libConsumer.a"));
            var consumerModule = Assert.Single(consumerManifest.Modules);
            Assert.Equal("Consumer", consumerModule.ModuleName);

            var typedImport = Assert.Single(consumerModule.CompilerSections?.TypedInterface?.Imports ?? []);
            Assert.Equal("Dependency", typedImport.ModuleName);
            var sourceImport = Assert.Single(consumerModule.SourceSurface?.Imports ?? []);
            Assert.Equal("Dependency", sourceImport.ModuleName);
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
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
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

                    public struct Holder<T, comptime u8[1 8] N>
                    {
                        T[N] Items;
                    }

                    public trait HasBuffer
                    {
                        alias Buffer = Holder<u8[0 max], 4>;
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
                libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
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

            var hasBuffer = Assert.Single(typedInterface.Types, static type => type.Name == "HasBuffer");
            var bufferType = Assert.Single(hasBuffer.AssociatedTypes ?? [], static associatedType => associatedType.Name == "Buffer");
            Assert.Equal("named", bufferType.TargetType?.Kind);

            var counter = Assert.Single(typedInterface.Types, static type => type.Name == "Counter");
            var counterItem = Assert.Single(counter.AssociatedTypes ?? [], static associatedType => associatedType.Name == "Item");
            Assert.Equal("integer", counterItem.TargetType?.Kind);
            Assert.Equal(32, counterItem.TargetType?.BitWidth);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("alias Item;", sourceText, StringComparison.Ordinal);
            Assert.Contains("alias Code = ", sourceText, StringComparison.Ordinal);
            Assert.Contains("alias Buffer = ", sourceText, StringComparison.Ordinal);
            Assert.Contains("alias Item = ", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            var readerFacts = facts.NamedTypes["Facade.Reader"];
            Assert.True(readerFacts.AssociatedTypes["Item"].IsRequired);
            var hashFacts = facts.NamedTypes["Facade.HasHash"];
            Assert.Equal(StarkTypeKind.Integer, hashFacts.AssociatedTypes["Code"].TargetType?.Kind);
            var bufferFacts = facts.NamedTypes["Facade.HasBuffer"];
            Assert.Equal(StarkTypeKind.Named, bufferFacts.AssociatedTypes["Buffer"].TargetType?.Kind);
            var counterFacts = facts.NamedTypes["Facade.Counter"];
            Assert.Equal(StarkTypeKind.Integer, counterFacts.AssociatedTypes["Item"].TargetType?.Kind);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                        finite law i64[min max] AssociatedScore<ReaderType, HasHashType, CounterType, BufferType>()
                        {
                            if (comptime System.Compiler.AssociatedTypeCount<ReaderType>() == 1
                                && comptime !System.Compiler.AssociatedTypeHasTarget<ReaderType, 0>()
                                && comptime (System.Compiler.AssociatedTypeName<HasHashType, 0>() == "Code")
                                && comptime System.Compiler.AssociatedTypeTargetTypeIs<HasHashType, u64[0 max], 0>()
                                && comptime System.Compiler.AssociatedTypeTargetTypeIs<CounterType, i32[min max], 0>()
                                && comptime System.Compiler.AssociatedTypeTargetTypeIsInteger<HasHashType, 0>()
                                && comptime System.Compiler.AssociatedTypeTargetTypeHasConcreteLayout<CounterType, 0>()
                                && comptime (System.Compiler.AssociatedTypeTargetTypeDisplayName<ReaderType, 0>() == "")
                                && comptime (System.Compiler.AssociatedTypeTargetTypeModuleName<ReaderType, 0>() == "")
                                && comptime !System.Compiler.AssociatedTypeTargetTypeIsGenericInstantiation<ReaderType, 0>()
                                && comptime (System.Compiler.AssociatedTypeTargetTypeDisplayName<BufferType, 0>() == "Facade.Holder<u8, 4>")
                                && comptime (System.Compiler.AssociatedTypeTargetTypeBaseName<BufferType, 0>() == "Facade.Holder")
                                && comptime (System.Compiler.AssociatedTypeTargetTypeModuleName<BufferType, 0>() == "Facade")
                                && comptime System.Compiler.AssociatedTypeTargetTypeIsGenericInstantiation<BufferType, 0>()
                                && comptime System.Compiler.AssociatedTypeTargetTypeArgumentCount<BufferType, 0>() == 1
                                && comptime System.Compiler.AssociatedTypeTargetTypeComptimeArgumentCount<BufferType, 0>() == 1)
                            {
                                return 13;
                            }

                            return 0;
                        }

                        finite law i64[min max] Run()
                        {
                            return comptime AssociatedScore<Facade.Reader, Facade.HasHash, Facade.Counter, Facade.HasBuffer>();
                        }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i64 13", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesThreadSafetyLawSurfaceAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-thread-safety-laws-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    [Grant(Transferable)]
                    public struct Packet<T>
                    {
                        [Grant(Shareable) where Transferable(T)]
                        public T Payload;

                        [Deny(Transferable)]
                        i32[min max] LocalOnly;
                    }

                    [Deny(Shareable)]
                    public enum Gate
                    {
                        Closed
                    }

                    public finite law T Move<T>(T value) where Transferable(T), Shareable(T)
                    {
                        return value;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            Assert.Contains("\"ThreadSafetyLawAttributes\"", manifest.ToJson(), StringComparison.Ordinal);
            Assert.Contains("\"ThreadSafetyLawPredicates\"", manifest.ToJson(), StringComparison.Ordinal);

            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterface = facadeModule.CompilerSections?.TypedInterface;
            Assert.NotNull(typedInterface);

            var packet = Assert.Single(typedInterface!.Types, static type => type.Name == "Packet");
            var packetLaw = Assert.Single(packet.ThreadSafetyLawAttributes ?? []);
            Assert.Equal("grant", packetLaw.Kind);
            Assert.Equal("Transferable", packetLaw.LawName);

            var payload = Assert.Single(packet.Fields, static field => field.Name == "Payload");
            var payloadLaw = Assert.Single(payload.ThreadSafetyLawAttributes ?? []);
            Assert.Equal("grant", payloadLaw.Kind);
            Assert.Equal("Shareable", payloadLaw.LawName);
            Assert.NotNull(payloadLaw.Condition);
            Assert.Equal("Transferable", payloadLaw.Condition!.LawName);
            Assert.Equal("T", payloadLaw.Condition.Type.Name);

            var localOnly = Assert.Single(packet.Fields, static field => field.Name == "LocalOnly");
            var localOnlyLaw = Assert.Single(localOnly.ThreadSafetyLawAttributes ?? []);
            Assert.Equal("deny", localOnlyLaw.Kind);
            Assert.Equal("Transferable", localOnlyLaw.LawName);

            var gate = Assert.Single(typedInterface.Types, static type => type.Name == "Gate");
            var gateLaw = Assert.Single(gate.ThreadSafetyLawAttributes ?? []);
            Assert.Equal("deny", gateLaw.Kind);
            Assert.Equal("Shareable", gateLaw.LawName);

            var move = Assert.Single(typedInterface.Functions, static function => function.Name == "Move");
            Assert.Equal(
                ["Transferable", "Shareable"],
                (move.ThreadSafetyLawPredicates ?? []).Select(static predicate => predicate.LawName).ToArray());
            Assert.All(move.ThreadSafetyLawPredicates ?? [], static predicate => Assert.Equal("T", predicate.Type.Name));

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("[Grant(Transferable)] public struct Packet<T>", sourceText, StringComparison.Ordinal);
            Assert.Contains("[Grant(Shareable) where Transferable(T)] T Payload;", sourceText, StringComparison.Ordinal);
            Assert.Contains("[Deny(Transferable)] i32[min max] LocalOnly;", sourceText, StringComparison.Ordinal);
            Assert.Contains("[Deny(Shareable)] public enum Gate", sourceText, StringComparison.Ordinal);
            Assert.Contains("where Transferable(T)", sourceText, StringComparison.Ordinal);
            Assert.Contains("where Shareable(T)", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            var packetFacts = facts.NamedTypes["Facade.Packet"];
            var packetFactLaw = Assert.Single(packetFacts.ThreadSafetyLaws);
            Assert.Equal(ThreadSafetyLawAttributeKind.Grant, packetFactLaw.Kind);
            Assert.Equal("Transferable", packetFactLaw.LawName);

            Assert.True(packetFacts.TryGetField("Payload", out var payloadFacts, out _));
            var payloadFactLaw = Assert.Single(payloadFacts.ThreadSafetyLaws);
            Assert.Equal(ThreadSafetyLawAttributeKind.Grant, payloadFactLaw.Kind);
            Assert.Equal("Shareable", payloadFactLaw.LawName);
            Assert.NotNull(payloadFactLaw.Condition);
            Assert.Equal("Transferable", payloadFactLaw.Condition!.LawName);
            Assert.Equal("T", payloadFactLaw.Condition.Type.DisplayName);

            Assert.True(packetFacts.TryGetField("LocalOnly", out var localOnlyFacts, out _));
            var localOnlyFactLaw = Assert.Single(localOnlyFacts.ThreadSafetyLaws);
            Assert.Equal(ThreadSafetyLawAttributeKind.Deny, localOnlyFactLaw.Kind);
            Assert.Equal("Transferable", localOnlyFactLaw.LawName);

            var gateFactLaw = Assert.Single(facts.NamedTypes["Facade.Gate"].ThreadSafetyLaws);
            Assert.Equal(ThreadSafetyLawAttributeKind.Deny, gateFactLaw.Kind);
            Assert.Equal("Shareable", gateFactLaw.LawName);

            var moveFacts = facts.FunctionSignatures["Facade.Move"];
            Assert.Equal(
                ["Transferable", "Shareable"],
                moveFacts.ThreadSafetyLaws.Select(static law => law.LawName).ToArray());
            Assert.All(moveFacts.ThreadSafetyLaws, static law => Assert.Equal("T", law.Type.DisplayName));
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
    public void PackageImagePreservesMethodStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-method-facts-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var libraryResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Holder<T, comptime u8[1 8] N>
                    {
                        T[N] Items;
                    }

                    public trait Drawable
                    {
                        finite law i32[min max] Draw(borrow Self self);
                    }

                    public trait Bound<T, comptime u8[1 8] N>
                    {
                        finite law T ReadBound(borrow Self self);
                    }

                    public struct Box
                    {
                        i32[min max] Value;

                        finite law Holder<i32[min max], 4> Echo(borrow Box self, Holder<i32[min max], 4> fallback)
                        {
                            return fallback;
                        }

                        finite law T Pick<T, comptime u8[1 8] N>(borrow Box self, T value) where T: Drawable, Bound<T, comptime N>
                        {
                            return value;
                        }

                        static unsafe fn void ReadBounded(rawptr<i32[min max]>[length] data, u8[1 10] length, i32[min max] fallback)
                        {
                            return;
                        }
                    }

                    public trait BorrowService
                    {
                        fn retborrow Box Pick(
                            borrow Self self,
                            retborrow Box value,
                            borrow mut Box target,
                            out i32[min max] code,
                            shared Box sharedBox,
                            frozen Box frozenBox,
                            init i32[min max][] output);
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var resolvedModule = CreateResolvedPackageModule(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(
                resolvedModule,
                out var facts));
            Assert.Contains("Facade.Box.Echo", facts.FunctionSignatures.Keys);
            Assert.Contains("Facade.Box.Pick", facts.FunctionSignatures.Keys);
            Assert.Contains("Facade.Box.ReadBounded", facts.FunctionSignatures.Keys);
            Assert.Contains("Facade.BorrowService.Pick", facts.FunctionSignatures.Keys);
            Assert.Equal("length", facts.FunctionSignatures["Facade.Box.ReadBounded"].Parameters[0].RawPointerElementCountExpression);
            Assert.Null(facts.FunctionSignatures["Facade.Box.ReadBounded"].Parameters[2].RawPointerElementCountExpression);
            Assert.Equal(2, Assert.Single(facts.FunctionSignatures["Facade.Box.Pick"].Constraints).BoundTraits.Count);
            var borrowServicePick = facts.FunctionSignatures["Facade.BorrowService.Pick"];
            Assert.Equal(StarkBorrowKind.RetBorrow, borrowServicePick.ReturnType.BorrowKind);
            Assert.Equal(StarkBorrowKind.Borrow, borrowServicePick.Parameters[0].Type.BorrowKind);
            Assert.Equal(StarkBorrowKind.RetBorrow, borrowServicePick.Parameters[1].Type.BorrowKind);
            Assert.Equal(StarkBorrowKind.Borrow, borrowServicePick.Parameters[2].Type.BorrowKind);
            Assert.True(borrowServicePick.Parameters[2].Type.IsMutableView);
            Assert.Equal(StarkInitializationKind.Out, borrowServicePick.Parameters[3].Type.InitializationKind);
            Assert.Equal(StarkAccessKind.Shared, borrowServicePick.Parameters[4].Type.AccessKind);
            Assert.Equal(StarkAccessKind.Frozen, borrowServicePick.Parameters[5].Type.AccessKind);
            Assert.Equal(StarkInitializationKind.Init, borrowServicePick.Parameters[6].Type.InitializationKind);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("where T: Drawable, Bound<T, comptime N>", sourceText, StringComparison.Ordinal);
            Assert.Contains("rawptr<i32[min max]>[length] data", sourceText, StringComparison.Ordinal);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[min max] Run()
                    {
                        if (!(comptime System.Compiler.MethodCount<Facade.BorrowService>() == 1
                            && comptime (System.Compiler.MethodModuleName<Facade.BorrowService, 0>() == "Facade")
                            && comptime System.Compiler.MethodReturnTypeBorrowKindIsRetBorrow<Facade.BorrowService, 0>()
                            && comptime System.Compiler.MethodReturnTypeUnqualifiedTypeIs<Facade.BorrowService, Facade.Box, 0>()
                            && comptime System.Compiler.MethodParameterTypeBorrowKindIsBorrow<Facade.BorrowService, 0, 0>()
                            && comptime System.Compiler.MethodParameterTypeBorrowKindIsRetBorrow<Facade.BorrowService, 0, 1>()
                            && comptime System.Compiler.MethodParameterTypeBorrowKindIsBorrow<Facade.BorrowService, 0, 2>()
                            && comptime System.Compiler.MethodParameterTypeIsMutableView<Facade.BorrowService, 0, 2>()
                            && comptime System.Compiler.MethodParameterTypeInitializationKindIsOut<Facade.BorrowService, 0, 3>()
                            && comptime System.Compiler.MethodParameterTypeAccessKindIsShared<Facade.BorrowService, 0, 4>()
                            && comptime System.Compiler.MethodParameterTypeAccessKindIsFrozen<Facade.BorrowService, 0, 5>()
                            && comptime System.Compiler.MethodParameterTypeInitializationKindIsInit<Facade.BorrowService, 0, 6>()
                            && comptime System.Compiler.MethodParameterTypeUnqualifiedTypeIs<Facade.BorrowService, i32[min max][], 0, 6>()))
                        {
                            return 0;
                        }

                        if (comptime System.Compiler.MethodCount<Facade.Box>() == 3)
                        {
                            if (comptime (System.Compiler.MethodName<Facade.Box, 0>() == "Echo")
                                && comptime (System.Compiler.MethodModuleName<Facade.Box, 0>() == "Facade"))
                            {
                                if (comptime System.Compiler.MethodParameterCount<Facade.Box, 0>() == 2
                                    && comptime (System.Compiler.MethodParameterName<Facade.Box, 0, 0>() == "self")
                                    && comptime (System.Compiler.MethodParameterName<Facade.Box, 0, 1>() == "fallback"))
                                {
                                    if (comptime System.Compiler.MethodReturnTypeIs<Facade.Box, Facade.Holder<i32[min max], 4>, 0>())
                                    {
                                        if (comptime System.Compiler.MethodParameterTypeIs<Facade.Box, Facade.Holder<i32[min max], 4>, 0, 1>())
                                        {
                                            if (comptime (System.Compiler.MethodReturnTypeDisplayName<Facade.Box, 0>() == "Facade.Holder<i32, 4>")
                                                && comptime (System.Compiler.MethodReturnTypeBaseName<Facade.Box, 0>() == "Facade.Holder")
                                                && comptime (System.Compiler.MethodReturnTypeModuleName<Facade.Box, 0>() == "Facade")
                                                && comptime System.Compiler.MethodReturnTypeIsGenericInstantiation<Facade.Box, 0>()
                                                && comptime System.Compiler.MethodReturnTypeArgumentCount<Facade.Box, 0>() == 1
                                                && comptime System.Compiler.MethodReturnTypeComptimeArgumentCount<Facade.Box, 0>() == 1
                                                && comptime System.Compiler.MethodReturnTypeArgumentTypeIs<Facade.Box, i32[min max], 0, 0>()
                                                && comptime System.Compiler.MethodReturnTypeArgumentTypeIsInteger<Facade.Box, 0, 0>()
                                                && comptime System.Compiler.MethodReturnTypeArgumentTypeHasConcreteLayout<Facade.Box, 0, 0>()
                                                && comptime (System.Compiler.MethodReturnTypeArgumentTypeDisplayName<Facade.Box, 0, 0>() == "i32")
                                                && comptime (System.Compiler.MethodReturnTypeArgumentTypeBaseName<Facade.Box, 0, 0>() == "")
                                                && comptime (System.Compiler.MethodReturnTypeArgumentTypeModuleName<Facade.Box, 0, 0>() == "")
                                                && comptime !System.Compiler.MethodReturnTypeArgumentTypeIsGenericInstantiation<Facade.Box, 0, 0>()
                                                && comptime System.Compiler.MethodReturnTypeArgumentTypeArgumentCount<Facade.Box, 0, 0>() == 0
                                                && comptime System.Compiler.MethodReturnTypeArgumentTypeComptimeArgumentCount<Facade.Box, 0, 0>() == 0)
                                            {
                                                if (comptime (System.Compiler.MethodParameterTypeDisplayName<Facade.Box, 0, 1>() == "Facade.Holder<i32, 4>")
                                                    && comptime (System.Compiler.MethodParameterTypeBaseName<Facade.Box, 0, 1>() == "Facade.Holder")
                                                    && comptime (System.Compiler.MethodParameterTypeModuleName<Facade.Box, 0, 1>() == "Facade")
                                                    && comptime System.Compiler.MethodParameterTypeIsGenericInstantiation<Facade.Box, 0, 1>()
                                                    && comptime System.Compiler.MethodParameterTypeArgumentCount<Facade.Box, 0, 1>() == 1
                                                    && comptime System.Compiler.MethodParameterTypeComptimeArgumentCount<Facade.Box, 0, 1>() == 1
                                                    && comptime System.Compiler.MethodParameterTypeArgumentTypeIs<Facade.Box, i32[min max], 0, 1, 0>()
                                                    && comptime System.Compiler.MethodParameterTypeArgumentTypeIsInteger<Facade.Box, 0, 1, 0>()
                                                    && comptime System.Compiler.MethodParameterTypeArgumentTypeHasConcreteLayout<Facade.Box, 0, 1, 0>()
                                                    && comptime (System.Compiler.MethodParameterTypeArgumentTypeDisplayName<Facade.Box, 0, 1, 0>() == "i32")
                                                    && comptime (System.Compiler.MethodParameterTypeArgumentTypeBaseName<Facade.Box, 0, 1, 0>() == "")
                                                    && comptime (System.Compiler.MethodParameterTypeArgumentTypeModuleName<Facade.Box, 0, 1, 0>() == "")
                                                    && comptime !System.Compiler.MethodParameterTypeArgumentTypeIsGenericInstantiation<Facade.Box, 0, 1, 0>()
                                                    && comptime System.Compiler.MethodParameterTypeArgumentTypeArgumentCount<Facade.Box, 0, 1, 0>() == 0
                                                    && comptime System.Compiler.MethodParameterTypeArgumentTypeComptimeArgumentCount<Facade.Box, 0, 1, 0>() == 0)
                                                {
                                                    if (comptime (System.Compiler.MethodName<Facade.Box, 1>() == "Pick")
                                                        && comptime (System.Compiler.MethodModuleName<Facade.Box, 1>() == "Facade")
                                                        && comptime System.Compiler.MethodGenericParameterCount<Facade.Box, 1>() == 1
                                                        && comptime System.Compiler.MethodComptimeGenericParameterCount<Facade.Box, 1>() == 1
                                                        && comptime (System.Compiler.MethodComptimeGenericParameterName<Facade.Box, 1, 0>() == "N")
                                                        && comptime System.Compiler.MethodComptimeGenericParameterTypeIs<Facade.Box, u8[1 8], 1, 0>()
                                                        && comptime System.Compiler.MethodComptimeGenericParameterTypeIsInteger<Facade.Box, 1, 0>()
                                                        && comptime System.Compiler.MethodComptimeGenericParameterTypeHasConcreteLayout<Facade.Box, 1, 0>()
                                                        && comptime (System.Compiler.MethodComptimeGenericParameterTypeDisplayName<Facade.Box, 1, 0>() == "u8[1 8]")
                                                        && comptime (System.Compiler.MethodComptimeGenericParameterTypeBaseName<Facade.Box, 1, 0>() == "")
                                                        && comptime (System.Compiler.MethodComptimeGenericParameterTypeModuleName<Facade.Box, 1, 0>() == "")
                                                        && comptime !System.Compiler.MethodComptimeGenericParameterTypeIsGenericInstantiation<Facade.Box, 1, 0>()
                                                        && comptime System.Compiler.MethodComptimeGenericParameterTypeArgumentCount<Facade.Box, 1, 0>() == 0
                                                        && comptime System.Compiler.MethodComptimeGenericParameterTypeComptimeArgumentCount<Facade.Box, 1, 0>() == 0
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundCount<Facade.Box, 1, 0>() == 2
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIs<Facade.Box, Facade.Drawable, 1, 0, 0>()
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsNamed<Facade.Box, 1, 0, 0>()
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsTrait<Facade.Box, 1, 0, 0>()
                                                        && comptime (System.Compiler.MethodGenericParameterTraitBoundTypeModuleName<Facade.Box, 1, 0, 0>() == "Facade")
                                                        && comptime !System.Compiler.MethodGenericParameterTraitBoundTypeHasConcreteLayout<Facade.Box, 1, 0, 0>()
                                                        && comptime (System.Compiler.MethodGenericParameterTraitBoundTypeBaseName<Facade.Box, 1, 0, 1>() == "Facade.Bound")
                                                        && comptime (System.Compiler.MethodGenericParameterTraitBoundTypeModuleName<Facade.Box, 1, 0, 1>() == "Facade")
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsNamed<Facade.Box, 1, 0, 1>()
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsTrait<Facade.Box, 1, 0, 1>()
                                                        && comptime !System.Compiler.MethodGenericParameterTraitBoundTypeHasConcreteLayout<Facade.Box, 1, 0, 1>()
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeIsGenericInstantiation<Facade.Box, 1, 0, 1>()
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeArgumentCount<Facade.Box, 1, 0, 1>() == 1
                                                        && comptime System.Compiler.MethodGenericParameterTraitBoundTypeComptimeArgumentCount<Facade.Box, 1, 0, 1>() == 1)
                                                    {
                                                        if (comptime (System.Compiler.MethodName<Facade.Box, 2>() == "ReadBounded")
                                                            && comptime (System.Compiler.MethodModuleName<Facade.Box, 2>() == "Facade")
                                                            && comptime System.Compiler.MethodParameterHasRawPointerElementCountExpression<Facade.Box, 2, 0>()
                                                            && comptime !System.Compiler.MethodParameterHasRawPointerElementCountExpression<Facade.Box, 2, 2>()
                                                            && comptime (System.Compiler.MethodParameterRawPointerElementCountExpression<Facade.Box, 2, 0>() == "length")
                                                            && comptime (System.Compiler.MethodParameterRawPointerElementCountExpression<Facade.Box, 2, 2>() == ""))
                                                        {
                                                            return 42;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i32 42", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesVisibilityStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-visibility-facts-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var libraryResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    internal struct InternalType
                    {
                        internal i32[min max] Value;
                    }

                    public struct PublicType
                    {
                        internal i32[min max] Hidden;
                        public i32[min max] Visible;

                        internal finite law i32[min max] HiddenMethod(borrow PublicType self)
                        {
                            return self.Hidden;
                        }

                        public finite law i32[min max] VisibleMethod(borrow PublicType self)
                        {
                            return self.Visible;
                        }
                    }

                    export struct ExportType
                    {
                        public i32[min max] Value;
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var resolvedModule = CreateResolvedPackageModule(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.Equal(StarkVisibility.Internal, facts.NamedTypes["Facade.InternalType"].Visibility);
            Assert.Equal(StarkVisibility.Public, facts.NamedTypes["Facade.PublicType"].Visibility);
            Assert.Equal(StarkVisibility.Export, facts.NamedTypes["Facade.ExportType"].Visibility);
            Assert.Equal(StarkVisibility.Internal, facts.NamedTypes["Facade.PublicType"].OrderedFields[0].Visibility);
            Assert.Equal(StarkVisibility.Public, facts.NamedTypes["Facade.PublicType"].OrderedFields[1].Visibility);
            Assert.Equal(StarkVisibility.Internal, facts.FunctionSignatures["Facade.PublicType.HiddenMethod"].Visibility);
            Assert.Equal(StarkVisibility.Public, facts.FunctionSignatures["Facade.PublicType.VisibleMethod"].Visibility);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("internal i32[min max] Hidden;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public finite law i32[min max] VisibleMethod", sourceText, StringComparison.Ordinal);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[min max] Run()
                    {
                        if (comptime System.Compiler.TypeVisibilityIsPublic<Facade.PublicType>()
                            && comptime System.Compiler.TypeVisibilityIsExport<Facade.ExportType>()
                            && comptime System.Compiler.FieldVisibilityIsInternal<Facade.PublicType, 0>()
                            && comptime System.Compiler.FieldVisibilityIsPublic<Facade.PublicType, 1>()
                            && comptime (System.Compiler.MethodName<Facade.PublicType, 0>() == "HiddenMethod")
                            && comptime System.Compiler.MethodVisibilityIsInternal<Facade.PublicType, 0>()
                            && comptime (System.Compiler.MethodName<Facade.PublicType, 1>() == "VisibleMethod")
                            && comptime System.Compiler.MethodVisibilityIsPublic<Facade.PublicType, 1>())
                        {
                            return 42;
                        }

                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i32 42", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesEnumErrorFunnelsAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-funnels-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public enum IOError
                    {
                        Disk
                    }

                    public enum OtherError
                    {
                        Disk
                    }

                    public enum ParseError
                    {
                        Io from IOError,
                        Syntax
                    }

                    public enum GenericError<T>
                    {
                        Wrapped from T,
                        Other
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var typedInterface = facadeModule.CompilerSections?.TypedInterface;
            Assert.NotNull(typedInterface);

            var parseError = Assert.Single(typedInterface!.Types, static type => type.Name == "ParseError");
            var ioVariant = Assert.Single(parseError.Variants ?? [], static variant => variant.Name == "Io");
            Assert.NotNull(ioVariant.AbsorbsErrorType);

            var genericError = Assert.Single(typedInterface.Types, static type => type.Name == "GenericError");
            var wrappedVariant = Assert.Single(genericError.Variants ?? [], static variant => variant.Name == "Wrapped");
            Assert.NotNull(wrappedVariant.AbsorbsErrorType);

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("Io from IOError", sourceText, StringComparison.Ordinal);
            Assert.Contains("Wrapped from T", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.True(facts.NamedTypes["Facade.ParseError"].Variants[0].IsErrorFunnel);
            Assert.True(facts.NamedTypes["Facade.GenericError"].Variants[0].IsErrorFunnel);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i64[min max] FunnelScore()
                    {
                        if (comptime System.Compiler.EnumVariantIsErrorFunnel<Facade.ParseError, 0>())
                        {
                            if (comptime !System.Compiler.EnumVariantIsErrorFunnel<Facade.ParseError, 1>())
                            {
                                if (comptime System.Compiler.EnumVariantAbsorbsErrorTypeIs<Facade.ParseError, Facade.IOError, 0>())
                                {
                                    if (comptime !System.Compiler.EnumVariantAbsorbsErrorTypeIs<Facade.ParseError, Facade.OtherError, 0>())
                                    {
                                        if (comptime System.Compiler.EnumVariantAbsorbsErrorTypeIs<Facade.GenericError<Facade.IOError>, Facade.IOError, 0>())
                                        {
                                            return 7;
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }

                    finite law i64[min max] Run()
                    {
                        return comptime FunnelScore();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i64 7", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesFieldAndPayloadTypePredicatesAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-type-predicates-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Point
                    {
                        i32[min max] Value;
                    }

                    public enum Kind
                    {
                        One
                    }

                    public struct Header
                    {
                        bool Active;
                        Point Position;
                        Kind Tag;
                        i32[min max][4] Values;
                    }

                    public enum Token
                    {
                        Position(Point),
                        Tag(Kind),
                        Values(i32[min max][4])
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("public struct Header", sourceText, StringComparison.Ordinal);
            Assert.Contains("Position(Point)", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            Assert.Equal(StarkTypeKind.Bool, facts.NamedTypes["Facade.Header"].OrderedFields[0].Type.Kind);
            Assert.Equal(StarkTypeKind.Named, facts.NamedTypes["Facade.Header"].OrderedFields[1].Type.Kind);
            Assert.Equal(StarkTypeKind.FixedArray, facts.NamedTypes["Facade.Header"].OrderedFields[3].Type.Kind);
            Assert.Equal(StarkTypeKind.Named, facts.NamedTypes["Facade.Token"].Variants[0].Fields[0].Type.Kind);
            Assert.Equal(StarkTypeKind.FixedArray, facts.NamedTypes["Facade.Token"].Variants[2].Fields[0].Type.Kind);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i64[min max] PredicateScore()
                    {
                        if (comptime System.Compiler.FieldTypeIsBool<Facade.Header, 0>())
                        {
                            if (comptime System.Compiler.FieldTypeIsStruct<Facade.Header, 1>())
                            {
                                if (comptime System.Compiler.FieldTypeIsEnum<Facade.Header, 2>())
                                {
                                    if (comptime System.Compiler.FieldTypeIsFixedArray<Facade.Header, 3>())
                                    {
                                        if (comptime System.Compiler.EnumVariantPayloadTypeIsStruct<Facade.Token, 0, 0>())
                                        {
                                            if (comptime System.Compiler.EnumVariantPayloadTypeIsEnum<Facade.Token, 1, 0>())
                                            {
                                                if (comptime System.Compiler.EnumVariantPayloadTypeIsFixedArray<Facade.Token, 2, 0>())
                                                {
                                                    if (comptime (System.Compiler.FieldTypeModuleName<Facade.Header, 0>() == ""))
                                                    {
                                                        if (comptime (System.Compiler.FieldTypeModuleName<Facade.Header, 1>() == "Facade"))
                                                        {
                                                            if (comptime (System.Compiler.EnumVariantPayloadTypeModuleName<Facade.Token, 0, 0>() == "Facade")
                                                                && comptime (System.Compiler.EnumVariantPayloadTypeModuleName<Facade.Token, 2, 0>() == ""))
                                                            {
                                                                return 7;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }

                    finite law i64[min max] Run()
                    {
                        return comptime PredicateScore();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i64 7", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesFfiLinkNameFactsAndBridgeSource()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-link-name-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    [LinkName("native_alias_i32")]
                    public unsafe ffi(c) fn i32[min max] StarkAlias(i32[min max] value);
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var function = Assert.Single(facadeModule.CompilerSections?.TypedInterface?.Functions ?? []);
            var abiFunction = Assert.Single(facadeModule.CompilerSections?.CompilerFacts?.AbiFunctions ?? []);

            Assert.Equal("StarkAlias", function.Name);
            Assert.Equal("native_alias_i32", function.SymbolName);
            Assert.Equal("native_alias_i32", function.LinkName);
            Assert.Equal("native_alias_i32", abiFunction.SymbolName);
            Assert.Equal("native_alias_i32", abiFunction.LinkName);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            var loadedSignature = Assert.Single(facts.FunctionSignatures.Values);
            Assert.Equal("native_alias_i32", loadedSignature.ExternalLinkName);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(facadeModule), out var sourceText));
            Assert.Contains("[LinkName(\"native_alias_i32\")]", sourceText, StringComparison.Ordinal);
            Assert.Contains("public unsafe ffi(c) fn i32[", sourceText, StringComparison.Ordinal);
            Assert.Contains("StarkAlias(i32[min max] value);", sourceText, StringComparison.Ordinal);
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
    public void PackageImageImportedFfiLinkNameDrivesConsumerLlvmCall()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-link-name-call-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "NativeAlias.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "NativeAlias.starkpkg.json" : "libNativeAlias.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "NativeAlias.lib" : "libNativeAlias.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module NativeAlias

                    [LinkName("native_alias_i32")]
                    public unsafe ffi(c) fn i32[min max] StarkAlias(i32[min max] value);
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import NativeAlias
                    module App

                    export fn i32[min max] main()
                    {
                        unsafe
                        {
                            return StarkAlias(41);
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "App.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.Contains("declare i32 @native_alias_i32", llvmModule!.Text, StringComparison.Ordinal);
            Assert.Contains("call i32 @native_alias_i32(", llvmModule.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@StarkAlias", llvmModule.Text, StringComparison.Ordinal);
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
    public void PackageImagePreservesExpandedFfiAggregateCarrierFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-ffi-aggregate-carriers-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "NativeRect.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "NativeRect.starkpkg.json" : "libNativeRect.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "NativeRect.lib" : "libNativeRect.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module NativeRect

                    [StructLayout(C)]
                    public struct Rectangle
                    {
                        public f32 X;
                        public f32 Y;
                        public f32 Width;
                        public f32 Height;
                    }

                    [LinkName("DrawRectangleRec")]
                    public unsafe ffi(c) fn void Draw(Rectangle rec);
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var nativeRectModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "NativeRect");
            var abiFunction = Assert.Single(nativeRectModule.CompilerSections?.CompilerFacts?.AbiFunctions ?? []);
            var recParameter = Assert.Single(abiFunction.Parameters, static parameter => parameter.SourceName == "rec");
            Assert.Equal("llvmstruct", recParameter.LlvmType.Kind);
            Assert.NotNull(recParameter.LlvmParameterTypes);
            Assert.Equal(["llvmvector", "llvmvector"], recParameter.LlvmParameterTypes!.Select(static type => type.Kind).ToArray());

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import NativeRect
                    module App

                    export unsafe fn i32[min max] main()
                    {
                        stack Rectangle rec = new Rectangle()
                        {
                            X = 1.0,
                            Y = 2.0,
                            Width = 3.0,
                            Height = 4.0
                        };
                        Draw(rec);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "App.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.Contains("declare void @DrawRectangleRec(<2 x float>, <2 x float>) nounwind", llvmModule!.Text, StringComparison.Ordinal);
            Assert.Contains("call void @DrawRectangleRec(<2 x float>", llvmModule.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@Draw(", llvmModule.Text, StringComparison.Ordinal);
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
    public void PackageImagePreservesNoRecurseFunctionEffectFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-norecurse-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public fn i32[min max] Leaf(i32[min max] value)
                    {
                        return value;
                    }

                    public fn i32[min max] Apply(
                        fnptr<fn i32[min max](i32[min max])> op,
                        i32[min max] value)
                    {
                        return op(value);
                    }

                    public fn i32[min max] CallsApply(i32[min max] value)
                    {
                        return Apply(Leaf, value);
                    }

                    public fn i32[min max] Recursive(i32[min max] value)
                    {
                        return Recursive(value);
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var compilerFacts = facadeModule.CompilerSections?.CompilerFacts;
            Assert.NotNull(compilerFacts);

            var leafEffect = Assert.Single(
                compilerFacts!.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Leaf");
            var applyEffect = Assert.Single(
                compilerFacts.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Apply");
            var callsApplyEffect = Assert.Single(
                compilerFacts.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.CallsApply");
            var recursiveEffect = Assert.Single(
                compilerFacts.FunctionEffects,
                static function => function.QualifiedResolvedName == "Facade.Recursive");

            Assert.True(leafEffect.NoRecurse);
            Assert.False(applyEffect.NoRecurse);
            Assert.False(callsApplyEffect.NoRecurse);
            Assert.False(recursiveEffect.NoRecurse);

            var leafSemantics = Assert.Single(
                compilerFacts.FunctionSemantics ?? [],
                static function => function.QualifiedResolvedName == "Facade.Leaf");
            var applySemantics = Assert.Single(
                compilerFacts.FunctionSemantics ?? [],
                static function => function.QualifiedResolvedName == "Facade.Apply");
            Assert.False(leafSemantics.HasOpaqueCall);
            Assert.True(applySemantics.HasOpaqueCall);

            Assert.Contains("\"NoRecurse\": true", manifest.ToJson(), StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            Assert.True(facts.FunctionEffects["Facade.Leaf"].NoRecurse);
            Assert.False(facts.FunctionEffects["Facade.Apply"].NoRecurse);
            Assert.False(facts.FunctionEffects["Facade.CallsApply"].NoRecurse);
            Assert.False(facts.FunctionEffects["Facade.Recursive"].NoRecurse);
            Assert.False(facts.FunctionSemantics["Facade.Leaf"].HasOpaqueCall);
            Assert.True(facts.FunctionSemantics["Facade.Apply"].HasOpaqueCall);
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
    public void PackageImagePreservesArenaObjectCreationStorageSelectorInGenericTemplateFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-arena-template-");
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

                    public struct Box
                    {
                        public i32[min max] Value;
                    }

                    public fn u64[0 max] Count<T>(T item)
                    {
                        stack mut dynamic T values = new(arena, 1);
                        init values[0] = item;
                        return values.Length;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(libraryResult.Succeeded, string.Join(Environment.NewLine, libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(libraryResult, "Package-image arena-template library builds", sourcePath);

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var countTemplate = Assert.Single(facadeModule.EffectiveGenericTemplates!.Functions, static function => function.QualifiedResolvedName == "Facade.Count");
            Assert.Contains(
                countTemplate.ObjectCreations!,
                static creation => creation.StorageSelector == ObjectCreationStorageSelector.Arena
                    && creation.ExpressionText == "new(arena,1)");
            Assert.Contains(
                countTemplate.BoundOperations!,
                static operation => operation.Kind == "object-creation"
                    && operation.StorageSelector == ObjectCreationStorageSelector.Arena);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            var importedTemplate = facts.FunctionTemplates["Facade.Count"];
            Assert.Contains(
                importedTemplate.ObjectCreations,
                static creation => creation.StorageSelector == ObjectCreationStorageSelector.Arena
                    && creation.ExpressionText == "new(arena,1)");
            Assert.Contains(importedTemplate.BoundOperations, static operation =>
                operation.Operation is BoundObjectCreationOperation { StorageSelector: ObjectCreationStorageSelector.Arena });

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(facadeModule), out var sourceText));
            Assert.Contains("new(arena,1)", sourceText, StringComparison.Ordinal);
            Assert.Contains("init values[0] = item;", sourceText, StringComparison.Ordinal);

            File.Delete(sourcePath);
            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn u64[0 max] Run()
                    {
                        stack Facade.Box box = new Facade.Box()
                        {
                            Value = 7
                        };
                        return Facade.Count(box);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(consumerResult.Succeeded, string.Join(Environment.NewLine, consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
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
    public void PackageImageBackedRetainingBorrowSurfaceRejectsArenaBackedArguments()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-arena-retain-");
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

                    public struct Box
                    {
                        public i32[min max] Value;
                    }

                    public fn void Observe(borrow Box box)
                    {
                        return;
                    }

                    public fn void Retain(storeborrow Box box)
                    {
                        return;
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(libraryResult.Succeeded, string.Join(Environment.NewLine, libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(libraryResult, "Package-image arena-retain library builds", sourcePath);

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var retainSemantics = Assert.Single(
                facadeModule.CompilerSections!.CompilerFacts!.FunctionSemantics ?? [],
                static function => function.QualifiedResolvedName == "Facade.Retain");
            var retainParameter = Assert.Single(retainSemantics.Parameters!, static parameter => parameter.Name == "box");
            Assert.Equal("escape", retainParameter.CaptureKind);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            var loadedRetainParameter = Assert.Single(facts.FunctionSemantics["Facade.Retain"].Parameters!, static parameter => parameter.Name == "box");
            Assert.Equal(ParameterCaptureKind.Escape, loadedRetainParameter.CaptureKind);

            File.Delete(sourcePath);
            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Bad()
                    {
                        arena Facade.Box box = new Facade.Box()
                        {
                            Value = 1
                        };
                        Facade.Observe(box);
                        Facade.Retain(box);
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "ownership"));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK4202"
                    && diagnostic.Message.Contains("Arena lifetime error", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("callee may retain", StringComparison.Ordinal));
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
    public void PackageImagePreservesLabeledLoopControlInImportedGenericTypedBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-labeled-loop-control-");

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

                    public fn i32[min max] Score<T>(i32[min max] limit, T tag)
                    {
                        stack mut i32[min max] total = 0;
                        stack mut i32[min max] outer = 0;
                        outerLoop: while willexit (outer < limit)
                        {
                            outer += 1;
                            stack mut i32[min max] inner = 0;
                            while willexit (inner < 4)
                            {
                                inner += 1;
                                if (outer == 2)
                                {
                                    continue outerLoop;
                                }

                                if (inner == 3)
                                {
                                    break outerLoop;
                                }

                                total += inner;
                            }
                        }

                        selector: switch (limit)
                        {
                            case 0:
                                break selector;
                            default:
                                total += 0;
                        }

                        return total + outer;
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var template = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Score");
            Assert.NotNull(template.TypedBody);
            var outerWhile = Assert.Single(
                template.TypedBody!.Statements,
                static statement => statement.Kind == "while" && statement.Name == "outerLoop");
            var innerWhile = Assert.Single(
                outerWhile.BodyStatements!,
                static statement => statement.Kind == "while");
            Assert.Contains(
                innerWhile.BodyStatements!,
                static statement => statement.Kind == "if"
                    && statement.ThenStatements is not null
                    && statement.ThenStatements.Any(static inner => inner.Kind == "continue" && inner.Name == "outerLoop"));
            Assert.Contains(
                innerWhile.BodyStatements!,
                static statement => statement.Kind == "if"
                    && statement.ThenStatements is not null
                    && statement.ThenStatements.Any(static inner => inner.Kind == "break" && inner.Name == "outerLoop"));
            Assert.Contains(
                template.TypedBody.Statements,
                static statement => statement.Kind == "switch"
                    && statement.Name == "selector"
                    && statement.SwitchCases is not null
                    && statement.SwitchCases.Any(static switchCase =>
                        switchCase.Statements is not null
                        && switchCase.Statements.Any(static inner => inner.Kind == "break" && inner.Name == "selector")));

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            var importedTemplate = facts.FunctionTemplates["Facade.Score"];
            Assert.NotNull(importedTemplate.TypedBody);
            var importedOuterWhile = Assert.Single(
                importedTemplate.TypedBody!.Statements,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.While && statement.Name == "outerLoop");
            var importedInnerWhile = Assert.Single(
                importedOuterWhile.Body,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.While);
            Assert.Contains(
                importedInnerWhile.Body,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.If
                    && statement.ThenBranch.Any(static inner => inner.Kind == ImportedTemplateTypedBodyStatementKind.Continue && inner.Name == "outerLoop"));
            Assert.Contains(
                importedInnerWhile.Body,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.If
                    && statement.ThenBranch.Any(static inner => inner.Kind == ImportedTemplateTypedBodyStatementKind.Break && inner.Name == "outerLoop"));
            Assert.Contains(
                importedTemplate.TypedBody.Statements,
                static statement => statement.Kind == ImportedTemplateTypedBodyStatementKind.Switch
                    && statement.Name == "selector"
                    && statement.SwitchCases.Any(static switchCase =>
                        switchCase.Statements.Any(static inner => inner.Kind == ImportedTemplateTypedBodyStatementKind.Break && inner.Name == "selector")));

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates!.Functions
                    .Select(static item => item.QualifiedResolvedName == "Facade.Score"
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

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run()
                    {
                        return Facade.Score<i32[min max]>(4, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported labeled loop generic typed body builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            MidLevelIrLoweringTests.AssertMirHasNoNullLoweringArtifacts(mir);
            Assert.Contains(
                mir!.Functions,
                static function => function.Name == "__stark_mono_fn_Demo__Facade_Score__i32");
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
    public void PackageImagePreservesFunctionPointerStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-function-pointer-facts-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public record Point(i32[min max] X)
                    {
                    }

                    public struct Holder<T, comptime u8[1 8] N>
                    {
                        T Value;
                    }

                    public enum Kind
                    {
                        One
                    }

                    public alias Callback = fnptr<ffi(c) finite law Point(i32[min max], rawptr<i8[min max]>, Kind)>;
                    public alias UnsafeCallback = fnptr<unsafe ffi(c) fn void()>;
                    public alias GenericCallback = fnptr<fn Holder<i32[min max], 4>(Holder<bool, 2>, rawptr<i8[min max]>)>;
                    public alias BoundedCallback = fnptr<fn void(rawptr<i32[min max]>[arg1], u8[1 10], i32[min max])>;
                    public alias RawPoint = rawptr<Point>;
                    public alias RawMutableNumber = rawmutptr<i32[min max]>;
                    public alias DefaultSafe = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)>;
                    public alias Overlapping = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where overlap(arg0, arg1)>;
                    public alias SameRegion = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>;
                    public alias ClosureCallback = heap closure<finite law Point(i32[min max], rawptr<i8[min max]>, Kind)>;
                    public alias CClosureCallback = heap closure<finite law System.C.c_long(System.C.c_int, rawptr<System.C.c_char>)>;
                    public alias ClosureGenericCallback = borrow closure<fn Holder<i32[min max], 4>(Holder<bool, 2>, rawptr<i8[min max]>)>;
                    public alias ClosureBoundedCallback = borrow closure<fn void(rawptr<i32[min max]>[arg1], u8[1 10], i32[min max])>;
                    public alias ClosureMut = heap closure<mut fn Point(i32[min max])>;
                    public alias ClosureOnce = heap closure<once fn i32[min max]()>;
                    public alias ClosureDefaultSafe = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)>;
                    public alias ClosureOverlapping = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where overlap(arg0, arg1)>;
                    public alias ClosureSameRegion = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>;
                    public alias QualifiedCallback = fnptr<fn retborrow Point(retborrow Point, borrow mut Point, out i32[min max], shared Point, frozen Point, init i32[min max][])>;
                    public alias QualifiedClosure = borrow closure<fn retborrow Point(retborrow Point, borrow mut Point, out i32[min max], shared Point, frozen Point, init i32[min max][])>;
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("public struct Holder<T, comptime u8[1 8] N>", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias Callback = fnptr<ffi(c) finite law Point(i32[min max], rawptr<i8[min max]>, Kind)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias UnsafeCallback = fnptr<unsafe ffi(c) fn void()>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias GenericCallback = fnptr<fn Holder<i32[min max], 4>(Holder<bool, 2>, rawptr<i8[min max]>)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias BoundedCallback = fnptr<fn void(rawptr<i32[min max]>[arg1], u8[1 10], i32[min max])>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias RawPoint = rawptr<Point>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias RawMutableNumber = rawmutptr<i32[min max]>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias DefaultSafe = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias Overlapping = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where overlap(arg0, arg1)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias SameRegion = fnptr<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureCallback = heap closure<finite law Point(i32[min max], rawptr<i8[min max]>, Kind)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias CClosureCallback = heap closure<finite law System.C.c_long(System.C.c_int, rawptr<System.C.c_char>)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureGenericCallback = borrow closure<fn Holder<i32[min max], 4>(Holder<bool, 2>, rawptr<i8[min max]>)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureBoundedCallback = borrow closure<fn void(rawptr<i32[min max]>[arg1], u8[1 10], i32[min max])>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureMut = heap closure<mut fn Point(i32[min max])>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureOnce = heap closure<once fn i32[min max]()>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureDefaultSafe = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureOverlapping = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where overlap(arg0, arg1)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ClosureSameRegion = borrow closure<fn void(rawmutptr<i32[min max]>, rawmutptr<i32[min max]>) where same(arg0, arg1)>;", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            var callbackAlias = facts.TypeAliases["Facade.Callback"];
            Assert.Equal(StarkTypeKind.FunctionPointer, callbackAlias.TargetType.Kind);
            Assert.Equal(StarkFunctionKind.FiniteLaw, callbackAlias.TargetType.FunctionPointerKind);
            Assert.Equal(StarkFfiAbi.C, callbackAlias.TargetType.FunctionPointerAbi);
            Assert.False(callbackAlias.TargetType.FunctionPointerIsUnsafe);
            Assert.Equal(3, callbackAlias.TargetType.FunctionPointerParameterTypes?.Count);
            var unsafeCallbackAlias = facts.TypeAliases["Facade.UnsafeCallback"];
            Assert.True(unsafeCallbackAlias.TargetType.FunctionPointerIsUnsafe);
            Assert.Equal(StarkFfiAbi.C, unsafeCallbackAlias.TargetType.FunctionPointerAbi);
            var genericCallbackAlias = facts.TypeAliases["Facade.GenericCallback"];
            Assert.Equal("Facade.Holder<i32, 4>", genericCallbackAlias.TargetType.FunctionPointerReturnType?.DisplayName);
            Assert.Equal("Facade.Holder<bool, 2>", genericCallbackAlias.TargetType.FunctionPointerParameterTypes?[0].DisplayName);
            var boundedCallbackAlias = facts.TypeAliases["Facade.BoundedCallback"];
            Assert.Equal("arg1", StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(boundedCallbackAlias.TargetType, 0));
            Assert.Null(StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(boundedCallbackAlias.TargetType, 2));
            var rawPointAlias = facts.TypeAliases["Facade.RawPoint"];
            Assert.Equal(StarkTypeKind.RawPointer, rawPointAlias.TargetType.Kind);
            Assert.False(rawPointAlias.TargetType.IsMutablePointer);
            Assert.Equal("Facade.Point", rawPointAlias.TargetType.ElementType?.NamedType);
            var rawMutableNumberAlias = facts.TypeAliases["Facade.RawMutableNumber"];
            Assert.Equal(StarkTypeKind.RawPointer, rawMutableNumberAlias.TargetType.Kind);
            Assert.True(rawMutableNumberAlias.TargetType.IsMutablePointer);
            Assert.Equal(StarkTypeKind.Integer, rawMutableNumberAlias.TargetType.ElementType?.Kind);
            var defaultSafeAlias = facts.TypeAliases["Facade.DefaultSafe"];
            Assert.Contains(defaultSafeAlias.TargetType.FunctionPointerDisjointParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["arg0", "arg1"]));
            Assert.Empty(defaultSafeAlias.TargetType.FunctionPointerOverlapParameterGroups ?? []);
            Assert.Empty(defaultSafeAlias.TargetType.FunctionPointerSameParameterGroups ?? []);
            var overlappingAlias = facts.TypeAliases["Facade.Overlapping"];
            Assert.Contains(overlappingAlias.TargetType.FunctionPointerOverlapParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["arg0", "arg1"]));
            Assert.Empty(overlappingAlias.TargetType.FunctionPointerDisjointParameterGroups ?? []);
            Assert.Empty(overlappingAlias.TargetType.FunctionPointerSameParameterGroups ?? []);
            var sameRegionAlias = facts.TypeAliases["Facade.SameRegion"];
            Assert.Contains(sameRegionAlias.TargetType.FunctionPointerSameParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["arg0", "arg1"]));
            Assert.Empty(sameRegionAlias.TargetType.FunctionPointerDisjointParameterGroups ?? []);
            Assert.Empty(sameRegionAlias.TargetType.FunctionPointerOverlapParameterGroups ?? []);
            var qualifiedCallbackAlias = facts.TypeAliases["Facade.QualifiedCallback"];
            Assert.Equal(StarkBorrowKind.RetBorrow, qualifiedCallbackAlias.TargetType.FunctionPointerReturnType?.BorrowKind);
            Assert.Equal(StarkBorrowKind.RetBorrow, qualifiedCallbackAlias.TargetType.FunctionPointerParameterTypes?[0].BorrowKind);
            Assert.Equal(StarkBorrowKind.Borrow, qualifiedCallbackAlias.TargetType.FunctionPointerParameterTypes?[1].BorrowKind);
            Assert.True(qualifiedCallbackAlias.TargetType.FunctionPointerParameterTypes?[1].IsMutableView);
            Assert.Equal(StarkInitializationKind.Out, qualifiedCallbackAlias.TargetType.FunctionPointerParameterTypes?[2].InitializationKind);
            Assert.Equal(StarkAccessKind.Shared, qualifiedCallbackAlias.TargetType.FunctionPointerParameterTypes?[3].AccessKind);
            Assert.Equal(StarkAccessKind.Frozen, qualifiedCallbackAlias.TargetType.FunctionPointerParameterTypes?[4].AccessKind);
            Assert.Equal(StarkInitializationKind.Init, qualifiedCallbackAlias.TargetType.FunctionPointerParameterTypes?[5].InitializationKind);
            var closureCallbackAlias = facts.TypeAliases["Facade.ClosureCallback"];
            Assert.Equal(StarkTypeKind.Closure, closureCallbackAlias.TargetType.Kind);
            Assert.Equal(StarkFunctionKind.FiniteLaw, closureCallbackAlias.TargetType.ClosureFunctionKind);
            Assert.Equal(StarkClosureStorageKind.Heap, closureCallbackAlias.TargetType.ClosureStorageKind);
            Assert.Equal(StarkClosureCallCapability.None, closureCallbackAlias.TargetType.ClosureCallCapability);
            Assert.Equal(3, closureCallbackAlias.TargetType.ClosureParameterTypes?.Count);
            var cClosureCallbackAlias = facts.TypeAliases["Facade.CClosureCallback"];
            Assert.Equal("System.C.c_long", cClosureCallbackAlias.TargetType.ClosureReturnType?.CSourceAliasName);
            Assert.Equal("System.C.c_int", cClosureCallbackAlias.TargetType.ClosureParameterTypes?[0].CSourceAliasName);
            Assert.Null(cClosureCallbackAlias.TargetType.ClosureParameterTypes?[1].CSourceAliasName);
            var closureGenericCallbackAlias = facts.TypeAliases["Facade.ClosureGenericCallback"];
            Assert.Equal("Facade.Holder<i32, 4>", closureGenericCallbackAlias.TargetType.ClosureReturnType?.DisplayName);
            Assert.Equal("Facade.Holder<bool, 2>", closureGenericCallbackAlias.TargetType.ClosureParameterTypes?[0].DisplayName);
            var closureBoundedCallbackAlias = facts.TypeAliases["Facade.ClosureBoundedCallback"];
            Assert.Equal("arg1", StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(closureBoundedCallbackAlias.TargetType, 0));
            Assert.Null(StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(closureBoundedCallbackAlias.TargetType, 2));
            var closureMutAlias = facts.TypeAliases["Facade.ClosureMut"];
            Assert.Equal(StarkClosureCallCapability.Mut, closureMutAlias.TargetType.ClosureCallCapability);
            var closureOnceAlias = facts.TypeAliases["Facade.ClosureOnce"];
            Assert.Equal(StarkClosureCallCapability.Once, closureOnceAlias.TargetType.ClosureCallCapability);
            var closureDefaultSafeAlias = facts.TypeAliases["Facade.ClosureDefaultSafe"];
            Assert.Contains(closureDefaultSafeAlias.TargetType.ClosureDisjointParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["arg0", "arg1"]));
            Assert.Empty(closureDefaultSafeAlias.TargetType.ClosureOverlapParameterGroups ?? []);
            Assert.Empty(closureDefaultSafeAlias.TargetType.ClosureSameParameterGroups ?? []);
            var closureOverlappingAlias = facts.TypeAliases["Facade.ClosureOverlapping"];
            Assert.Contains(closureOverlappingAlias.TargetType.ClosureOverlapParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["arg0", "arg1"]));
            Assert.Empty(closureOverlappingAlias.TargetType.ClosureDisjointParameterGroups ?? []);
            Assert.Empty(closureOverlappingAlias.TargetType.ClosureSameParameterGroups ?? []);
            var closureSameRegionAlias = facts.TypeAliases["Facade.ClosureSameRegion"];
            Assert.Contains(closureSameRegionAlias.TargetType.ClosureSameParameterGroups ?? [], static group => group.ParameterNames.SequenceEqual(["arg0", "arg1"]));
            Assert.Empty(closureSameRegionAlias.TargetType.ClosureDisjointParameterGroups ?? []);
            Assert.Empty(closureSameRegionAlias.TargetType.ClosureOverlapParameterGroups ?? []);
            var qualifiedClosureAlias = facts.TypeAliases["Facade.QualifiedClosure"];
            Assert.Equal(StarkBorrowKind.RetBorrow, qualifiedClosureAlias.TargetType.ClosureReturnType?.BorrowKind);
            Assert.Equal(StarkBorrowKind.RetBorrow, qualifiedClosureAlias.TargetType.ClosureParameterTypes?[0].BorrowKind);
            Assert.Equal(StarkBorrowKind.Borrow, qualifiedClosureAlias.TargetType.ClosureParameterTypes?[1].BorrowKind);
            Assert.True(qualifiedClosureAlias.TargetType.ClosureParameterTypes?[1].IsMutableView);
            Assert.Equal(StarkInitializationKind.Out, qualifiedClosureAlias.TargetType.ClosureParameterTypes?[2].InitializationKind);
            Assert.Equal(StarkAccessKind.Shared, qualifiedClosureAlias.TargetType.ClosureParameterTypes?[3].AccessKind);
            Assert.Equal(StarkAccessKind.Frozen, qualifiedClosureAlias.TargetType.ClosureParameterTypes?[4].AccessKind);
            Assert.Equal(StarkInitializationKind.Init, qualifiedClosureAlias.TargetType.ClosureParameterTypes?[5].InitializationKind);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i64[min max] MetadataScore()
                    {
                        if (!(comptime System.Compiler.FunctionPointerParameterCount<Facade.GenericCallback>() == 2))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.FunctionPointerReturnTypeDisplayName<Facade.GenericCallback>() == "Facade.Holder<i32, 4>")))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.FunctionPointerReturnTypeModuleName<Facade.GenericCallback>() == "Facade")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerReturnTypeIsGenericInstantiation<Facade.GenericCallback>()))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerReturnTypeArgumentCount<Facade.GenericCallback>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerReturnTypeComptimeArgumentCount<Facade.GenericCallback>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIs<Facade.GenericCallback, i32[min max], 0>()
                            && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeIsInteger<Facade.GenericCallback, 0>()
                            && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeHasConcreteLayout<Facade.GenericCallback, 0>()
                            && comptime (System.Compiler.FunctionPointerReturnTypeArgumentTypeDisplayName<Facade.GenericCallback, 0>() == "i32")
                            && comptime (System.Compiler.FunctionPointerReturnTypeArgumentTypeBaseName<Facade.GenericCallback, 0>() == "")
                            && comptime (System.Compiler.FunctionPointerReturnTypeArgumentTypeModuleName<Facade.GenericCallback, 0>() == "")
                            && comptime !System.Compiler.FunctionPointerReturnTypeArgumentTypeIsGenericInstantiation<Facade.GenericCallback, 0>()
                            && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeArgumentCount<Facade.GenericCallback, 0>() == 0
                            && comptime System.Compiler.FunctionPointerReturnTypeArgumentTypeComptimeArgumentCount<Facade.GenericCallback, 0>() == 0))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.FunctionPointerParameterTypeDisplayName<Facade.GenericCallback, 0>() == "Facade.Holder<bool, 2>")))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.FunctionPointerParameterTypeModuleName<Facade.GenericCallback, 0>() == "Facade")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerParameterTypeIsGenericInstantiation<Facade.GenericCallback, 0>()))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerParameterTypeArgumentCount<Facade.GenericCallback, 0>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerParameterTypeComptimeArgumentCount<Facade.GenericCallback, 0>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeIs<Facade.GenericCallback, bool, 0, 0>()
                            && comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeIsBool<Facade.GenericCallback, 0, 0>()
                            && comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeHasConcreteLayout<Facade.GenericCallback, 0, 0>()
                            && comptime (System.Compiler.FunctionPointerParameterTypeArgumentTypeDisplayName<Facade.GenericCallback, 0, 0>() == "bool")
                            && comptime (System.Compiler.FunctionPointerParameterTypeArgumentTypeBaseName<Facade.GenericCallback, 0, 0>() == "")
                            && comptime (System.Compiler.FunctionPointerParameterTypeArgumentTypeModuleName<Facade.GenericCallback, 0, 0>() == "")
                            && comptime !System.Compiler.FunctionPointerParameterTypeArgumentTypeIsGenericInstantiation<Facade.GenericCallback, 0, 0>()
                            && comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeArgumentCount<Facade.GenericCallback, 0, 0>() == 0
                            && comptime System.Compiler.FunctionPointerParameterTypeArgumentTypeComptimeArgumentCount<Facade.GenericCallback, 0, 0>() == 0))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.FunctionPointerParameterTypeBaseName<Facade.GenericCallback, 1>() == "")))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.FunctionPointerParameterTypeModuleName<Facade.GenericCallback, 1>() == "")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerParameterHasRawPointerElementCountExpression<Facade.BoundedCallback, 0>()
                            && comptime !System.Compiler.FunctionPointerParameterHasRawPointerElementCountExpression<Facade.BoundedCallback, 2>()
                            && comptime (System.Compiler.FunctionPointerParameterRawPointerElementCountExpression<Facade.BoundedCallback, 0>() == "arg1")
                            && comptime (System.Compiler.FunctionPointerParameterRawPointerElementCountExpression<Facade.BoundedCallback, 2>() == "")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerReturnTypeBorrowKindIsRetBorrow<Facade.QualifiedCallback>()
                            && comptime System.Compiler.FunctionPointerReturnTypeUnqualifiedTypeIs<Facade.QualifiedCallback, Facade.Point>()
                            && comptime System.Compiler.FunctionPointerParameterTypeBorrowKindIsRetBorrow<Facade.QualifiedCallback, 0>()
                            && comptime System.Compiler.FunctionPointerParameterTypeBorrowKindIsBorrow<Facade.QualifiedCallback, 1>()
                            && comptime System.Compiler.FunctionPointerParameterTypeIsMutableView<Facade.QualifiedCallback, 1>()
                            && comptime System.Compiler.FunctionPointerParameterTypeInitializationKindIsOut<Facade.QualifiedCallback, 2>()
                            && comptime System.Compiler.FunctionPointerParameterTypeAccessKindIsShared<Facade.QualifiedCallback, 3>()
                            && comptime System.Compiler.FunctionPointerParameterTypeAccessKindIsFrozen<Facade.QualifiedCallback, 4>()
                            && comptime System.Compiler.FunctionPointerParameterTypeInitializationKindIsInit<Facade.QualifiedCallback, 5>()
                            && comptime System.Compiler.FunctionPointerParameterTypeUnqualifiedTypeIs<Facade.QualifiedCallback, i32[min max][], 5>()))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.FunctionPointerIsUnsafe<Facade.UnsafeCallback>()
                            && comptime !System.Compiler.FunctionPointerIsUnsafe<Facade.Callback>()))
                        {
                            return 0;
                        }

                        if (comptime System.Compiler.FunctionPointerParameterCount<Facade.Callback>() == 3)
                        {
                            if (comptime System.Compiler.FunctionPointerReturnTypeIsRecord<Facade.Callback>())
                            {
                                if (comptime System.Compiler.FunctionPointerReturnTypeHasConcreteLayout<Facade.Callback>())
                                {
                                    if (comptime System.Compiler.FunctionPointerParameterTypeIsInteger<Facade.Callback, 0>())
                                    {
                                        if (comptime System.Compiler.FunctionPointerParameterTypeIsRawPointer<Facade.Callback, 1>())
                                        {
                                            if (comptime System.Compiler.FunctionPointerParameterTypeIsEnum<Facade.Callback, 2>())
                                            {
                                                if (comptime System.Compiler.FunctionPointerKindIsFiniteLaw<Facade.Callback>())
                                                {
                                                    if (comptime System.Compiler.FunctionPointerHasFfiAbi<Facade.Callback>())
                                                    {
                                                        if (comptime System.Compiler.FunctionPointerAbiIsC<Facade.Callback>())
                                                        {
                                                            if (comptime System.Compiler.FunctionPointerParametersAreDisjoint<Facade.DefaultSafe, 0, 1>())
                                                            {
                                                                if (comptime !System.Compiler.FunctionPointerParametersOverlap<Facade.DefaultSafe, 0, 1>())
                                                                {
                                                                    if (comptime System.Compiler.FunctionPointerParametersOverlap<Facade.Overlapping, 0, 1>())
                                                                    {
                                                                        if (comptime !System.Compiler.FunctionPointerParametersAreDisjoint<Facade.Overlapping, 0, 1>())
                                                                        {
                                                                            if (comptime System.Compiler.FunctionPointerParametersAreSame<Facade.SameRegion, 0, 1>())
                                                                            {
                                                                                if (comptime System.Compiler.FunctionPointerParametersOverlap<Facade.SameRegion, 0, 1>())
                                                                                {
                                                                                    return 7;
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }

                    finite law i64[min max] Run()
                    {
                        return comptime MetadataScore() + comptime ClosureMetadataScore() + comptime RawPointerMetadataScore();
                    }

                    finite law i64[min max] ClosureMetadataScore()
                    {
                        if (!(comptime System.Compiler.ClosureParameterCount<Facade.ClosureGenericCallback>() == 2))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.ClosureReturnTypeDisplayName<Facade.ClosureGenericCallback>() == "Facade.Holder<i32, 4>")))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.ClosureReturnTypeModuleName<Facade.ClosureGenericCallback>() == "Facade")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureReturnTypeIsGenericInstantiation<Facade.ClosureGenericCallback>()))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureReturnTypeArgumentCount<Facade.ClosureGenericCallback>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureReturnTypeComptimeArgumentCount<Facade.ClosureGenericCallback>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureReturnTypeArgumentTypeIs<Facade.ClosureGenericCallback, i32[min max], 0>()
                            && comptime System.Compiler.ClosureReturnTypeArgumentTypeIsInteger<Facade.ClosureGenericCallback, 0>()
                            && comptime System.Compiler.ClosureReturnTypeArgumentTypeHasConcreteLayout<Facade.ClosureGenericCallback, 0>()
                            && comptime (System.Compiler.ClosureReturnTypeArgumentTypeDisplayName<Facade.ClosureGenericCallback, 0>() == "i32")
                            && comptime (System.Compiler.ClosureReturnTypeArgumentTypeBaseName<Facade.ClosureGenericCallback, 0>() == "")
                            && comptime (System.Compiler.ClosureReturnTypeArgumentTypeModuleName<Facade.ClosureGenericCallback, 0>() == "")
                            && comptime !System.Compiler.ClosureReturnTypeArgumentTypeIsGenericInstantiation<Facade.ClosureGenericCallback, 0>()
                            && comptime System.Compiler.ClosureReturnTypeArgumentTypeArgumentCount<Facade.ClosureGenericCallback, 0>() == 0
                            && comptime System.Compiler.ClosureReturnTypeArgumentTypeComptimeArgumentCount<Facade.ClosureGenericCallback, 0>() == 0))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.ClosureParameterTypeDisplayName<Facade.ClosureGenericCallback, 0>() == "Facade.Holder<bool, 2>")))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.ClosureParameterTypeModuleName<Facade.ClosureGenericCallback, 0>() == "Facade")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureParameterTypeIsGenericInstantiation<Facade.ClosureGenericCallback, 0>()))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureParameterTypeArgumentCount<Facade.ClosureGenericCallback, 0>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureParameterTypeComptimeArgumentCount<Facade.ClosureGenericCallback, 0>() == 1))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureParameterTypeArgumentTypeIs<Facade.ClosureGenericCallback, bool, 0, 0>()
                            && comptime System.Compiler.ClosureParameterTypeArgumentTypeIsBool<Facade.ClosureGenericCallback, 0, 0>()
                            && comptime System.Compiler.ClosureParameterTypeArgumentTypeHasConcreteLayout<Facade.ClosureGenericCallback, 0, 0>()
                            && comptime (System.Compiler.ClosureParameterTypeArgumentTypeDisplayName<Facade.ClosureGenericCallback, 0, 0>() == "bool")
                            && comptime (System.Compiler.ClosureParameterTypeArgumentTypeBaseName<Facade.ClosureGenericCallback, 0, 0>() == "")
                            && comptime (System.Compiler.ClosureParameterTypeArgumentTypeModuleName<Facade.ClosureGenericCallback, 0, 0>() == "")
                            && comptime !System.Compiler.ClosureParameterTypeArgumentTypeIsGenericInstantiation<Facade.ClosureGenericCallback, 0, 0>()
                            && comptime System.Compiler.ClosureParameterTypeArgumentTypeArgumentCount<Facade.ClosureGenericCallback, 0, 0>() == 0
                            && comptime System.Compiler.ClosureParameterTypeArgumentTypeComptimeArgumentCount<Facade.ClosureGenericCallback, 0, 0>() == 0))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.ClosureParameterTypeBaseName<Facade.ClosureGenericCallback, 1>() == "")))
                        {
                            return 0;
                        }

                        if (!(comptime (System.Compiler.ClosureParameterTypeModuleName<Facade.ClosureGenericCallback, 1>() == "")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureParameterHasRawPointerElementCountExpression<Facade.ClosureBoundedCallback, 0>()
                            && comptime !System.Compiler.ClosureParameterHasRawPointerElementCountExpression<Facade.ClosureBoundedCallback, 2>()
                            && comptime (System.Compiler.ClosureParameterRawPointerElementCountExpression<Facade.ClosureBoundedCallback, 0>() == "arg1")
                            && comptime (System.Compiler.ClosureParameterRawPointerElementCountExpression<Facade.ClosureBoundedCallback, 2>() == "")))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureReturnTypeBorrowKindIsRetBorrow<Facade.QualifiedClosure>()
                            && comptime System.Compiler.ClosureReturnTypeUnqualifiedTypeIs<Facade.QualifiedClosure, Facade.Point>()
                            && comptime System.Compiler.ClosureParameterTypeBorrowKindIsRetBorrow<Facade.QualifiedClosure, 0>()
                            && comptime System.Compiler.ClosureParameterTypeBorrowKindIsBorrow<Facade.QualifiedClosure, 1>()
                            && comptime System.Compiler.ClosureParameterTypeIsMutableView<Facade.QualifiedClosure, 1>()
                            && comptime System.Compiler.ClosureParameterTypeInitializationKindIsOut<Facade.QualifiedClosure, 2>()
                            && comptime System.Compiler.ClosureParameterTypeAccessKindIsShared<Facade.QualifiedClosure, 3>()
                            && comptime System.Compiler.ClosureParameterTypeAccessKindIsFrozen<Facade.QualifiedClosure, 4>()
                            && comptime System.Compiler.ClosureParameterTypeInitializationKindIsInit<Facade.QualifiedClosure, 5>()
                            && comptime System.Compiler.ClosureParameterTypeUnqualifiedTypeIs<Facade.QualifiedClosure, i32[min max][], 5>()))
                        {
                            return 0;
                        }

                        if (!(comptime System.Compiler.ClosureReturnTypeHasCSourceAlias<Facade.CClosureCallback>()
                            && comptime (System.Compiler.ClosureReturnTypeCSourceAliasName<Facade.CClosureCallback>() == "System.C.c_long")
                            && comptime System.Compiler.ClosureParameterTypeHasCSourceAlias<Facade.CClosureCallback, 0>()
                            && comptime (System.Compiler.ClosureParameterTypeCSourceAliasName<Facade.CClosureCallback, 0>() == "System.C.c_int")
                            && comptime !System.Compiler.ClosureParameterTypeHasCSourceAlias<Facade.CClosureCallback, 1>()
                            && comptime (System.Compiler.ClosureParameterTypeCSourceAliasName<Facade.CClosureCallback, 1>() == "")))
                        {
                            return 0;
                        }

                        if (comptime System.Compiler.ClosureParameterCount<Facade.ClosureCallback>() == 3)
                        {
                            if (comptime System.Compiler.ClosureReturnTypeIsRecord<Facade.ClosureCallback>())
                            {
                                if (comptime System.Compiler.ClosureReturnTypeHasConcreteLayout<Facade.ClosureCallback>())
                                {
                                    if (comptime System.Compiler.ClosureParameterTypeIsInteger<Facade.ClosureCallback, 0>())
                                    {
                                        if (comptime System.Compiler.ClosureParameterTypeIsRawPointer<Facade.ClosureCallback, 1>())
                                        {
                                            if (comptime System.Compiler.ClosureParameterTypeIsEnum<Facade.ClosureCallback, 2>())
                                            {
                                                if (comptime System.Compiler.ClosureKindIsFiniteLaw<Facade.ClosureCallback>())
                                                {
                                                    if (comptime System.Compiler.ClosureStorageIsHeap<Facade.ClosureCallback>())
                                                    {
                                                        if (comptime System.Compiler.ClosureCallCapabilityIsNormal<Facade.ClosureCallback>())
                                                        {
                                                            if (comptime System.Compiler.ClosureCallCapabilityIsMut<Facade.ClosureMut>())
                                                            {
                                                                if (comptime System.Compiler.ClosureCallCapabilityIsOnce<Facade.ClosureOnce>())
                                                                {
                                                                    if (comptime System.Compiler.ClosureParametersAreDisjoint<Facade.ClosureDefaultSafe, 0, 1>())
                                                                    {
                                                                        if (comptime !System.Compiler.ClosureParametersOverlap<Facade.ClosureDefaultSafe, 0, 1>())
                                                                        {
                                                                            if (comptime System.Compiler.ClosureParametersOverlap<Facade.ClosureOverlapping, 0, 1>())
                                                                            {
                                                                                if (comptime !System.Compiler.ClosureParametersAreDisjoint<Facade.ClosureOverlapping, 0, 1>())
                                                                                {
                                                                                    if (comptime System.Compiler.ClosureParametersAreSame<Facade.ClosureSameRegion, 0, 1>())
                                                                                    {
                                                                                        if (comptime System.Compiler.ClosureParametersOverlap<Facade.ClosureSameRegion, 0, 1>())
                                                                                        {
                                                                                            return 5;
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }

                    finite law i64[min max] RawPointerMetadataScore()
                    {
                        if (comptime System.Compiler.RawPointerElementTypeIs<Facade.RawPoint, Facade.Point>())
                        {
                            if (comptime System.Compiler.RawPointerElementTypeIsRecord<Facade.RawPoint>())
                            {
                                if (comptime System.Compiler.RawPointerElementTypeHasConcreteLayout<Facade.RawPoint>())
                                {
                                    if (comptime System.Compiler.RawPointerIsReadOnly<Facade.RawPoint>())
                                    {
                                        if (comptime !System.Compiler.RawPointerIsMutable<Facade.RawPoint>())
                                        {
                                            if (comptime System.Compiler.RawPointerElementTypeIsInteger<Facade.RawMutableNumber>())
                                            {
                                                if (comptime System.Compiler.RawPointerIsMutable<Facade.RawMutableNumber>())
                                                {
                                                    if (comptime !System.Compiler.RawPointerIsReadOnly<Facade.RawMutableNumber>())
                                                    {
                                                        return 9;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i64 21", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesDynTraitStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-dyn-trait-facts-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public dyn trait Speaker
                    {
                        finite law i32[min max] Speak(borrow Self self);
                    }

                    public dyn trait Listener
                    {
                        finite law bool Listen(borrow Self self);
                    }

                    public alias SpeakerView = borrow dyn Speaker;
                    public alias SpeakerOwner = heap dyn Speaker;
                    public alias ListenerView = borrow dyn Listener;

                    public struct Holder<T>
                    {
                        T Value;
                    }

                    public struct Carrier
                    {
                        heap dyn Speaker Owned;
                    }

                    public enum Packet
                    {
                        Owned(heap dyn Speaker),
                        Count(i32[min max])
                    }

                    public trait Contract
                    {
                        alias View = heap dyn Speaker;
                    }

                    public struct Service
                    {
                        public finite law heap dyn Speaker Current(borrow Service self);

                        public finite law void Observe(borrow Service self, borrow dyn Speaker value);

                        public finite law Holder<heap dyn Speaker> Wrap(borrow Service self, Holder<heap dyn Speaker> input);
                    }

                    public alias Callback = fnptr<fn heap dyn Speaker(borrow dyn Speaker)>;
                    public alias Handler = borrow closure<fn heap dyn Speaker(borrow dyn Speaker)>;
                    public alias Boxed = Holder<heap dyn Speaker>;
                    public alias NestedCallback = fnptr<fn Holder<heap dyn Speaker>(Holder<heap dyn Speaker>)>;
                    public alias NestedHandler = borrow closure<fn Holder<heap dyn Speaker>(Holder<heap dyn Speaker>)>;
                    public alias DynArray = heap dyn Speaker[2];
                    public alias DynPointer = rawptr<heap dyn Speaker>;
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("public dyn trait Speaker", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias SpeakerView = borrow dyn Speaker;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias SpeakerOwner = heap dyn Speaker;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ListenerView = borrow dyn Listener;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias Callback = fnptr<fn heap dyn Speaker(borrow dyn Speaker)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias Handler = borrow closure<fn heap dyn Speaker(borrow dyn Speaker)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias Boxed = Holder<heap dyn Speaker>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias NestedCallback = fnptr<fn Holder<heap dyn Speaker>(Holder<heap dyn Speaker>)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias NestedHandler = borrow closure<fn Holder<heap dyn Speaker>(Holder<heap dyn Speaker>)>;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public finite law Holder<heap dyn Speaker> Wrap(borrow Service self, Holder<heap dyn Speaker> input);", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            var speakerViewAlias = facts.TypeAliases["Facade.SpeakerView"];
            Assert.Equal(StarkTypeKind.DynTrait, speakerViewAlias.TargetType.Kind);
            Assert.Equal(StarkDynTraitStorageKind.View, speakerViewAlias.TargetType.DynTraitStorageKind);
            Assert.Equal("Facade.Speaker", speakerViewAlias.TargetType.DynTraitName);
            var speakerOwnerAlias = facts.TypeAliases["Facade.SpeakerOwner"];
            Assert.Equal(StarkDynTraitStorageKind.Heap, speakerOwnerAlias.TargetType.DynTraitStorageKind);
            var listenerAlias = facts.TypeAliases["Facade.ListenerView"];
            Assert.Equal("Facade.Listener", listenerAlias.TargetType.DynTraitName);
            Assert.Empty(listenerAlias.TargetType.TypeArguments ?? []);
            var carrierType = facts.NamedTypes["Facade.Carrier"];
            Assert.Equal(StarkTypeKind.DynTrait, carrierType.OrderedFields[0].Type.Kind);
            Assert.Equal(StarkDynTraitStorageKind.Heap, carrierType.OrderedFields[0].Type.DynTraitStorageKind);
            Assert.Equal("Facade.Speaker", carrierType.OrderedFields[0].Type.DynTraitName);
            var packetType = facts.NamedTypes["Facade.Packet"];
            Assert.Equal(StarkTypeKind.DynTrait, packetType.Variants[0].Fields[0].Type.Kind);
            Assert.Equal(StarkDynTraitStorageKind.Heap, packetType.Variants[0].Fields[0].Type.DynTraitStorageKind);
            var contractType = facts.NamedTypes["Facade.Contract"];
            Assert.Equal(StarkTypeKind.DynTrait, contractType.AssociatedTypes["View"].TargetType?.Kind);
            Assert.Equal(StarkDynTraitStorageKind.Heap, contractType.AssociatedTypes["View"].TargetType?.DynTraitStorageKind);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i64[min max] MetadataScore()
                    {
                        if (comptime !System.Compiler.IsDynTrait<Facade.SpeakerView>()) { return 101; }
                        if (comptime !System.Compiler.DynTraitIsView<Facade.SpeakerView>()) { return 102; }
                        if (comptime !System.Compiler.DynTraitIsHeap<Facade.SpeakerOwner>()) { return 103; }
                        if (comptime !System.Compiler.DynTraitTargetTypeIs<Facade.SpeakerView, Facade.Speaker>()) { return 104; }
                        if (comptime !System.Compiler.DynTraitTargetTypeIs<Facade.ListenerView, Facade.Listener>()) { return 105; }
                        if (comptime System.Compiler.DynTraitTargetTypeIs<Facade.ListenerView, Facade.Speaker>()) { return 106; }
                        if (comptime !System.Compiler.FieldTypeIsDynTrait<Facade.Carrier, 0>()) { return 107; }
                        if (comptime !System.Compiler.EnumVariantPayloadTypeIsDynTrait<Facade.Packet, 0, 0>()) { return 108; }
                        if (comptime !System.Compiler.AssociatedTypeTargetTypeIsDynTrait<Facade.Contract, 0>()) { return 109; }
                        if (comptime !System.Compiler.MethodReturnTypeIsDynTrait<Facade.Service, 0>()) { return 110; }
                        if (comptime !System.Compiler.MethodParameterTypeIsDynTrait<Facade.Service, 1, 1>()) { return 111; }
                        if (comptime !System.Compiler.FunctionPointerReturnTypeIsDynTrait<Facade.Callback>()) { return 112; }
                        if (comptime !System.Compiler.FunctionPointerParameterTypeIsDynTrait<Facade.Callback, 0>()) { return 113; }
                        if (comptime !System.Compiler.ClosureReturnTypeIsDynTrait<Facade.Handler>()) { return 114; }
                        if (comptime !System.Compiler.ClosureParameterTypeIsDynTrait<Facade.Handler, 0>()) { return 115; }
                        if (comptime !System.Compiler.TypeArgumentTypeIsDynTrait<Facade.Boxed, 0>()) { return 116; }
                        if (comptime !System.Compiler.TypeElementTypeIsDynTrait<Facade.DynArray>()) { return 117; }
                        if (comptime !System.Compiler.RawPointerElementTypeIsDynTrait<Facade.DynPointer>()) { return 118; }
                        if (comptime !System.Compiler.FunctionPointerReturnTypeArgumentTypeIsDynTrait<Facade.NestedCallback, 0>()) { return 119; }
                        if (comptime !System.Compiler.FunctionPointerParameterTypeArgumentTypeIsDynTrait<Facade.NestedCallback, 0, 0>()) { return 120; }
                        if (comptime !System.Compiler.ClosureReturnTypeArgumentTypeIsDynTrait<Facade.NestedHandler, 0>()) { return 121; }
                        if (comptime !System.Compiler.ClosureParameterTypeArgumentTypeIsDynTrait<Facade.NestedHandler, 0, 0>()) { return 122; }
                        if (comptime !System.Compiler.MethodReturnTypeArgumentTypeIsDynTrait<Facade.Service, 2, 0>()) { return 123; }
                        if (comptime !System.Compiler.MethodParameterTypeArgumentTypeIsDynTrait<Facade.Service, 2, 1, 0>()) { return 124; }

                        return 7;
                    }

                    finite law i64[min max] Run()
                    {
                        return comptime MetadataScore();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i64 7", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesFieldAndEnumPayloadQualifierFactsAcrossTypedInterfaceSourceBridgeAndFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-declaration-qualifier-facts-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Box
                    {
                        i32[min max] Value;
                    }

                    public struct Holder
                    {
                        Box Plain;
                        storeborrow Box Stored;
                        shared Box SharedBox;
                        frozen Box FrozenBox;
                    }

                    public enum Packet
                    {
                        Plain(Box),
                        Stored(storeborrow Box),
                        SharedBox(shared Box),
                        FrozenBox(frozen Box)
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");

            var resolvedModule = CreateResolvedPackageModule(facadeModule);
            Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
            Assert.Contains("public struct Holder", sourceText, StringComparison.Ordinal);
            Assert.Contains("storeborrow Box Stored;", sourceText, StringComparison.Ordinal);
            Assert.Contains("shared Box SharedBox;", sourceText, StringComparison.Ordinal);
            Assert.Contains("frozen Box FrozenBox;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public enum Packet", sourceText, StringComparison.Ordinal);
            Assert.Contains("Stored(storeborrow Box)", sourceText, StringComparison.Ordinal);
            Assert.Contains("SharedBox(shared Box)", sourceText, StringComparison.Ordinal);
            Assert.Contains("FrozenBox(frozen Box)", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(resolvedModule, out var facts));
            var holder = facts.NamedTypes["Facade.Holder"];
            Assert.Equal(StarkBorrowKind.None, holder.OrderedFields[0].Type.BorrowKind);
            Assert.Equal(StarkBorrowKind.StoreBorrow, holder.OrderedFields[1].Type.BorrowKind);
            Assert.Equal(StarkAccessKind.Shared, holder.OrderedFields[2].Type.AccessKind);
            Assert.Equal(StarkAccessKind.Frozen, holder.OrderedFields[3].Type.AccessKind);
            var packet = facts.NamedTypes["Facade.Packet"];
            Assert.Equal(StarkBorrowKind.None, packet.Variants[0].Fields[0].Type.BorrowKind);
            Assert.Equal(StarkBorrowKind.StoreBorrow, packet.Variants[1].Fields[0].Type.BorrowKind);
            Assert.Equal(StarkAccessKind.Shared, packet.Variants[2].Fields[0].Type.AccessKind);
            Assert.Equal(StarkAccessKind.Frozen, packet.Variants[3].Fields[0].Type.AccessKind);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[min max] Run()
                    {
                        if (!(comptime !System.Compiler.FieldTypeHasQualifiers<Facade.Holder, 0>()))
                        {
                            return 1;
                        }

                        if (!(comptime System.Compiler.FieldTypeBorrowKindIsNone<Facade.Holder, 0>()
                            && comptime System.Compiler.FieldTypeAccessKindIsNone<Facade.Holder, 0>()
                            && comptime System.Compiler.FieldTypeInitializationKindIsNone<Facade.Holder, 0>()
                            && comptime System.Compiler.FieldTypeUnqualifiedTypeIs<Facade.Holder, Facade.Box, 0>()))
                        {
                            return 2;
                        }

                        if (!(comptime System.Compiler.FieldTypeHasQualifiers<Facade.Holder, 1>()
                            && comptime System.Compiler.FieldTypeBorrowKindIsStoreBorrow<Facade.Holder, 1>()
                            && comptime System.Compiler.FieldTypeUnqualifiedTypeIs<Facade.Holder, Facade.Box, 1>()))
                        {
                            return 3;
                        }

                        if (!(comptime System.Compiler.FieldTypeAccessKindIsShared<Facade.Holder, 2>()
                            && comptime System.Compiler.FieldTypeAccessKindIsFrozen<Facade.Holder, 3>()))
                        {
                            return 4;
                        }

                        if (!(comptime !System.Compiler.EnumVariantPayloadTypeHasQualifiers<Facade.Packet, 0, 0>()))
                        {
                            return 5;
                        }

                        if (!(comptime System.Compiler.EnumVariantPayloadTypeBorrowKindIsNone<Facade.Packet, 0, 0>()
                            && comptime System.Compiler.EnumVariantPayloadTypeAccessKindIsNone<Facade.Packet, 0, 0>()
                            && comptime System.Compiler.EnumVariantPayloadTypeInitializationKindIsNone<Facade.Packet, 0, 0>()
                            && comptime System.Compiler.EnumVariantPayloadTypeUnqualifiedTypeIs<Facade.Packet, Facade.Box, 0, 0>()))
                        {
                            return 6;
                        }

                        if (!(comptime System.Compiler.EnumVariantPayloadTypeHasQualifiers<Facade.Packet, 1, 0>()
                            && comptime System.Compiler.EnumVariantPayloadTypeBorrowKindIsStoreBorrow<Facade.Packet, 1, 0>()
                            && comptime System.Compiler.EnumVariantPayloadTypeUnqualifiedTypeIs<Facade.Packet, Facade.Box, 1, 0>()))
                        {
                            return 7;
                        }

                        if (!(comptime System.Compiler.EnumVariantPayloadTypeAccessKindIsShared<Facade.Packet, 2, 0>()
                            && comptime System.Compiler.EnumVariantPayloadTypeAccessKindIsFrozen<Facade.Packet, 3, 0>()))
                        {
                            return 8;
                        }

                        return 42;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i32 42", runBody);
            Assert.DoesNotContain("call", runBody);
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
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Holder<T, comptime u8[1 8] N>
                    {
                        T[N] Items;
                    }

                    public struct Bytes<comptime u8[1 8] N>
                    {
                        u8[0 max][N] Items;
                    }

                    public struct Outer<T, comptime u8[1 8] N>
                    {
                        Holder<T, comptime N> Inner;
                    }

                    public enum Packet<T, comptime u8[1 8] N>
                    {
                        Item(Holder<T, comptime N>)
                    }

                    public alias ByteArray<comptime u8[1 8] N> = u8[0 max][N];

                    public alias BorrowedInt = borrow mut i32[min max];

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
                libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedInterface = facadeModule.EffectiveTypedInterface!;

            var holder = Assert.Single(typedInterface.Types, static type => type.Name == "Holder");
            Assert.Equal(["T"], holder.GenericParameters);
            var holderParameter = Assert.Single(holder.ComptimeGenericParameters!);
            Assert.Equal("N", holderParameter.Name);
            Assert.Equal("integer", holderParameter.Type.Kind);
            Assert.Equal(8, holderParameter.Type.BitWidth);

            var bytes = Assert.Single(typedInterface.Types, static type => type.Name == "Bytes");
            var bytesParameter = Assert.Single(bytes.ComptimeGenericParameters!);
            Assert.Equal("N", bytesParameter.Name);
            Assert.Equal("integer", bytesParameter.Type.Kind);
            Assert.Equal(8, bytesParameter.Type.BitWidth);

            var byteArray = Assert.Single(typedInterface.TypeAliases!, static alias => alias.Name == "ByteArray");
            var byteArrayParameter = Assert.Single(byteArray.ComptimeGenericParameters!);
            Assert.Equal("N", byteArrayParameter.Name);
            Assert.Equal("integer", byteArrayParameter.Type.Kind);
            var borrowedInt = Assert.Single(typedInterface.TypeAliases!, static alias => alias.Name == "BorrowedInt");
            Assert.Equal("borrow", borrowedInt.TargetType.BorrowKind);
            Assert.True(borrowedInt.TargetType.IsMutableView);

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
            Assert.Contains("public struct Holder<T, comptime u8[1 8] N>", sourceText, StringComparison.Ordinal);
            Assert.Contains("public struct Bytes<comptime u8[1 8] N>", sourceText, StringComparison.Ordinal);
            Assert.Contains("public struct Outer<T, comptime u8[1 8] N>", sourceText, StringComparison.Ordinal);
            Assert.Contains("Holder<T, comptime N> Inner;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public enum Packet<T, comptime u8[1 8] N>", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias ByteArray<comptime u8[1 8] N> = u8[0 max][N];", sourceText, StringComparison.Ordinal);
            Assert.Contains("public alias BorrowedInt = borrow mut i32[min max];", sourceText, StringComparison.Ordinal);
            Assert.Contains("public finite law u8[0 max] Forward<comptime u8[1 8] N>()", sourceText, StringComparison.Ordinal);
            Assert.Contains("public finite law u8[0 max] Pick<comptime u8[1 8] N>()", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return Facade.Pick<comptime N>();", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(facadeModule), out var facts));
            var factHolder = facts.NamedTypes["Facade.Holder"];
            Assert.Equal(["T"], factHolder.GenericParams);
            var factHolderParameter = Assert.Single(factHolder.ComptimeGenericParams);
            Assert.Equal("N", factHolderParameter.Name);
            var factBytesParameter = Assert.Single(facts.NamedTypes["Facade.Bytes"].ComptimeGenericParams);
            Assert.Equal("N", factBytesParameter.Name);
            var factAliasParameter = Assert.Single(facts.TypeAliases["Facade.ByteArray"].ComptimeGenericParams);
            Assert.Equal("N", factAliasParameter.Name);
            var factBorrowedInt = facts.TypeAliases["Facade.BorrowedInt"];
            Assert.Equal(StarkBorrowKind.Borrow, factBorrowedInt.TargetType.BorrowKind);
            Assert.True(factBorrowedInt.TargetType.IsMutableView);
            var factForwardParameter = Assert.Single(facts.FunctionSignatures["Facade.Forward"].ComptimeGenericParams);
            Assert.Equal("N", factForwardParameter.Name);
            var factPickParameter = Assert.Single(facts.FunctionSignatures["Facade.Pick"].ComptimeGenericParams);
            Assert.Equal("N", factPickParameter.Name);
            var factDirectValueArgument = Assert.Single(facts.FunctionTemplates["Facade.Forward"].DirectCalls[0].Signature.ComptimeValues);
            Assert.True(factDirectValueArgument.IsSymbolic);
            Assert.Equal("N", factDirectValueArgument.SymbolicSourceName);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i64[min max] MetadataScore()
                    {
                        if (comptime System.Compiler.TypeGenericParameterCount<Facade.Holder<i32[min max], 4>>() == 1
                            && comptime (System.Compiler.TypeGenericParameterName<Facade.Holder<i32[min max], 4>, 0>() == "T")
                            && comptime System.Compiler.TypeComptimeGenericParameterCount<Facade.Holder<i32[min max], 4>>() == 1
                            && comptime (System.Compiler.TypeComptimeGenericParameterName<Facade.Holder<i32[min max], 4>, 0>() == "N")
                            && comptime System.Compiler.TypeComptimeGenericParameterTypeIs<Facade.Holder<i32[min max], 4>, u8[1 8], 0>()
                            && comptime System.Compiler.TypeComptimeGenericParameterTypeIsInteger<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime System.Compiler.TypeComptimeGenericParameterTypeHasConcreteLayout<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime (System.Compiler.TypeComptimeGenericParameterTypeDisplayName<Facade.Holder<i32[min max], 4>, 0>() == "u8[1 8]")
                            && comptime (System.Compiler.TypeComptimeGenericParameterTypeBaseName<Facade.Holder<i32[min max], 4>, 0>() == "")
                            && comptime !System.Compiler.TypeComptimeGenericParameterTypeIsGenericInstantiation<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime System.Compiler.TypeComptimeGenericParameterTypeArgumentCount<Facade.Holder<i32[min max], 4>, 0>() == 0
                            && comptime System.Compiler.TypeComptimeGenericParameterTypeComptimeArgumentCount<Facade.Holder<i32[min max], 4>, 0>() == 0
                            && comptime (System.Compiler.TypeBaseName<Facade.Holder<i32[min max], 4>>() == "Facade.Holder")
                            && comptime System.Compiler.TypeArgumentCount<Facade.Holder<i32[min max], 4>>() == 1
                            && comptime System.Compiler.TypeComptimeArgumentCount<Facade.Holder<i32[min max], 4>>() == 1
                            && comptime (System.Compiler.TypeComptimeArgumentName<Facade.Holder<i32[min max], 4>, 0>() == "N")
                            && comptime System.Compiler.TypeComptimeArgumentTypeIs<Facade.Holder<i32[min max], 4>, u8[1 8], 0>()
                            && comptime System.Compiler.TypeComptimeArgumentTypeIsInteger<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime System.Compiler.TypeComptimeArgumentTypeHasConcreteLayout<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime (System.Compiler.TypeComptimeArgumentTypeDisplayName<Facade.Holder<i32[min max], 4>, 0>() == "u8[1 8]")
                            && comptime (System.Compiler.TypeComptimeArgumentTypeBaseName<Facade.Holder<i32[min max], 4>, 0>() == "")
                            && comptime !System.Compiler.TypeComptimeArgumentTypeIsGenericInstantiation<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime System.Compiler.TypeComptimeArgumentTypeArgumentCount<Facade.Holder<i32[min max], 4>, 0>() == 0
                            && comptime System.Compiler.TypeComptimeArgumentTypeComptimeArgumentCount<Facade.Holder<i32[min max], 4>, 0>() == 0
                            && comptime System.Compiler.TypeComptimeArgumentValueIs<Facade.Holder<i32[min max], 4>, 0, 4>()
                            && comptime System.Compiler.TypeArgumentTypeIsInteger<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime System.Compiler.TypeArgumentTypeHasConcreteLayout<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime (System.Compiler.TypeArgumentTypeDisplayName<Facade.Holder<i32[min max], 4>, 0>() == "i32")
                            && comptime (System.Compiler.TypeArgumentTypeBaseName<Facade.Holder<i32[min max], 4>, 0>() == "")
                            && comptime !System.Compiler.TypeArgumentTypeIsGenericInstantiation<Facade.Holder<i32[min max], 4>, 0>()
                            && comptime System.Compiler.TypeArgumentTypeArgumentCount<Facade.Holder<i32[min max], 4>, 0>() == 0
                            && comptime System.Compiler.TypeArgumentTypeComptimeArgumentCount<Facade.Holder<i32[min max], 4>, 0>() == 0
                            && comptime (System.Compiler.FieldTypeBaseName<Facade.Outer<i32[min max], 4>, 0>() == "Facade.Holder")
                            && comptime System.Compiler.FieldTypeIsGenericInstantiation<Facade.Outer<i32[min max], 4>, 0>()
                            && comptime System.Compiler.FieldTypeArgumentCount<Facade.Outer<i32[min max], 4>, 0>() == 1
                            && comptime System.Compiler.FieldTypeComptimeArgumentCount<Facade.Outer<i32[min max], 4>, 0>() == 1
                            && comptime (System.Compiler.EnumVariantPayloadTypeBaseName<Facade.Packet<i32[min max], 4>, 0, 0>() == "Facade.Holder")
                            && comptime System.Compiler.EnumVariantPayloadTypeIsGenericInstantiation<Facade.Packet<i32[min max], 4>, 0, 0>()
                            && comptime System.Compiler.EnumVariantPayloadTypeArgumentCount<Facade.Packet<i32[min max], 4>, 0, 0>() == 1
                            && comptime System.Compiler.EnumVariantPayloadTypeComptimeArgumentCount<Facade.Packet<i32[min max], 4>, 0, 0>() == 1
                            && comptime System.Compiler.TypeElementTypeIsInteger<Facade.ByteArray<4>>()
                            && comptime System.Compiler.TypeFixedArrayLengthIs<Facade.ByteArray<4>, 4>()
                            && comptime System.Compiler.TypeBorrowKindIsBorrow<Facade.BorrowedInt>()
                            && comptime System.Compiler.TypeIsMutableView<Facade.BorrowedInt>()
                            && comptime System.Compiler.TypeUnqualifiedTypeIs<Facade.BorrowedInt, i32[min max]>())
                        {
                            return (i64[min max])(7 + comptime System.Compiler.TypeFixedArrayLength<Facade.ByteArray<4>>());
                        }

                        return 0;
                    }

                    finite law i64[min max] Run()
                    {
                        return comptime MetadataScore();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i64 11", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImageConsumerLowersImportedOrdinaryComptimeTemplateTypedBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-ordinary-comptime-template-consumer-");

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

                    public finite law u8[0 max] Fold<T>(T tag)
                    {
                        return comptime (5 + 2);
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var foldTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Fold");
            Assert.NotNull(foldTemplate.TypedBody);
            Assert.Contains("\"Kind\": \"comptime\"", manifest.ToJson(), StringComparison.Ordinal);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(static item => item.QualifiedResolvedName == "Facade.Fold"
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

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law u8[0 max] Run()
                    {
                        stack i32[min max] tag = 0;
                        return Facade.Fold(tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported ordinary comptime typed body builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.Contains("ret i8 7", ExtractDefinitionBody(llvm!.Text, "__stark_mono_fn_Demo__Facade_Fold__i32"));
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
    public void PackageImageConsumerLowersImportedComptimeGenericArithmeticTemplateTypedBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-generic-arithmetic-template-consumer-");

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

                    public finite law u8[0 max] Fold<T, comptime u8[1 8] N>()
                    {
                        return comptime (N + 2);
                    }

                    public finite law u8[0 max] Forward<T, comptime u8[1 8] M>()
                    {
                        return comptime Fold<T, comptime M>();
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var foldTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Fold");
            Assert.NotNull(foldTemplate.TypedBody);
            var forwardTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Forward");
            Assert.NotNull(forwardTemplate.TypedBody);
            var forwardDirectCall = Assert.Single(forwardTemplate.DirectCalls!);
            Assert.Equal("Facade.Fold", forwardDirectCall.QualifiedTemplateName);
            var manifestJson = manifest.ToJson();
            Assert.Contains("\"Kind\": \"comptime\"", manifestJson, StringComparison.Ordinal);
            Assert.Contains("\"Name\": \"N\"", manifestJson, StringComparison.Ordinal);
            Assert.Contains("\"Name\": \"M\"", manifestJson, StringComparison.Ordinal);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(static item => item with { BodyText = "{ return this is not valid Stark; }" })
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

                    finite law u8[0 max] Run()
                    {
                        return Facade.Forward<i32[min max], 5>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported comptime-generic arithmetic typed body builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.Contains("ret i8 7", ExtractDefinitionBody(llvm!.Text, "__stark_mono_fn_Demo__Facade_Fold__i32__N_5"));
            Assert.Contains("ret i8 7", ExtractDefinitionBody(llvm.Text, "__stark_mono_fn_Demo__Facade_Forward__i32__M_5"));
            Assert.Contains("ret i8 7", ExtractDefinitionBody(llvm.Text, "Run"));
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
    public void PackageImageConsumerFoldsImportedComptimeTemplateCallWithStatementBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-statement-template-consumer-");

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

                    public finite law i32[min max] Build<T, comptime u8[1 8] N>()
                    {
                        stack mut i32[min max] value = (i32[min max])N;
                        value = value + 1;
                        if (value == 6)
                        {
                            return value * 7;
                        }

                        return 0;
                    }

                    public finite law i32[min max] Fold<T, comptime u8[1 8] M>()
                    {
                        return comptime Build<T, comptime M>();
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var buildTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Build");
            Assert.NotNull(buildTemplate.TypedBody);
            Assert.True(buildTemplate.TypedBody!.Statements.Count > 1);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(static item => item with { BodyText = "{ return this is not valid Stark; }" })
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

                    finite law i32[min max] Run()
                    {
                        return Facade.Fold<i32[min max], 5>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported statement-bodied comptime template call builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.Contains("ret i32 42", ExtractDefinitionBody(llvm!.Text, "__stark_mono_fn_Demo__Facade_Build__i32__N_5"));
            Assert.Contains("ret i32 42", ExtractDefinitionBody(llvm.Text, "__stark_mono_fn_Demo__Facade_Fold__i32__M_5"));
            Assert.Contains("ret i32 42", ExtractDefinitionBody(llvm.Text, "Run"));
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
    public void PackageImageConsumerFoldsImportedComptimeTemplateCallWithMemberCalls()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-member-template-consumer-");

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
                        public i32[min max] Value;

                        public finite law Box<T> Add(borrow Box<T> self, i32[min max] delta)
                        {
                            return new Box<T>()
                            {
                                Value = self.Value + delta
                            };
                        }

                        public finite law i32[min max] Read(borrow Box<T> self)
                        {
                            return self.Value;
                        }
                    }

                    public finite law Box<T> BuildBox<T, comptime u8[0 64] N>()
                    {
                        return new Box<T>()
                        {
                            Value = (i32[min max])N
                        };
                    }

                    public finite law i32[min max] MemberScore<T, comptime u8[0 64] N>()
                    {
                        return comptime BuildBox<T, comptime N>().Add(5).Read();
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var scoreTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.MemberScore");
            Assert.NotNull(scoreTemplate.TypedBody);
            Assert.True(scoreTemplate.MemberCalls is { Count: >= 2 });

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(static item => item with { BodyText = "{ return this is not valid Stark; }" })
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

                    finite law i32[min max] Run()
                    {
                        return Facade.MemberScore<i32[min max], 37>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported member-call comptime template call builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var memberScore = Assert.Single(
                mir!.Functions,
                static function => function.Name == "__stark_mono_fn_Demo__Facade_MemberScore__i32__N_37");
            var returnValue = Assert.IsType<MidLevelIrIntegerConstantOperand>(
                memberScore.Blocks[memberScore.EntryBlockId].Terminator.Value);
            Assert.Equal("42", returnValue.Value.ToString());
            Assert.DoesNotContain(
                memberScore.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue);
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
    public void PackageImageConsumerFoldsImportedComptimeTemplateCallWithObjectInitializers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-object-initializer-template-consumer-");

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

                    public struct Pair
                    {
                        public i32[min max] Left;
                        public i32[min max] Right;
                    }

                    public struct Holder
                    {
                        public Pair Item;
                        public i32[min max][2] Values;
                    }

                    public finite law i32[min max] AggregateScore<T>()
                    {
                        stack Holder holder =
                        {
                            Item =
                            {
                                Left = 10,
                                Right = 20
                            },
                            Values =
                            {
                                5,
                                7
                            }
                        };

                        return holder.Item.Left + holder.Item.Right + holder.Values[0] + holder.Values[1];
                    }

                    public finite law i32[min max] FoldAggregate<T>()
                    {
                        return comptime AggregateScore<T>();
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var aggregateTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.AggregateScore");
            var foldTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates.Functions,
                static item => item.QualifiedResolvedName == "Facade.FoldAggregate");
            Assert.NotNull(aggregateTemplate.TypedBody);
            Assert.NotNull(foldTemplate.TypedBody);
            Assert.Contains("\"Kind\": \"object-initializer\"", manifest.ToJson(), StringComparison.Ordinal);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(static item => item with { BodyText = "{ return this is not valid Stark; }" })
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

                    finite law i32[min max] Run()
                    {
                        return Facade.FoldAggregate<i32[min max]>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported object-initializer comptime template call builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var foldAggregate = Assert.Single(
                mir!.Functions,
                static function => function.Name.Contains("Facade_FoldAggregate", StringComparison.Ordinal));
            var returnValue = Assert.IsType<MidLevelIrIntegerConstantOperand>(
                foldAggregate.Blocks[foldAggregate.EntryBlockId].Terminator.Value);
            Assert.Equal("42", returnValue.Value.ToString());
            Assert.DoesNotContain(
                foldAggregate.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue);
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
    public void PackageImageConsumerFoldsImportedComptimeTemplateCallWithTextConstants()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-text-template-consumer-");

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

                    public finite law ascii Interpolated<T>()
                    {
                        return $"Score: {100}";
                    }

                    public finite law ascii Concatenated<T>()
                    {
                        return "Score: " + "100";
                    }

                    public finite law i32[min max] TextScore<T>()
                    {
                        if (comptime Interpolated<T>() == "Score: 100")
                        {
                            if (comptime Concatenated<T>() == "Score: 100")
                            {
                                return 42;
                            }
                        }

                        return 0;
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var interpolatedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.Interpolated");
            var concatenatedTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates.Functions,
                static item => item.QualifiedResolvedName == "Facade.Concatenated");
            var scoreTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates.Functions,
                static item => item.QualifiedResolvedName == "Facade.TextScore");
            Assert.NotNull(interpolatedTemplate.TypedBody);
            Assert.NotNull(concatenatedTemplate.TypedBody);
            Assert.NotNull(scoreTemplate.TypedBody);
            Assert.Contains("\"Kind\": \"text-interpolation\"", manifest.ToJson(), StringComparison.Ordinal);

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(static item => item with { BodyText = "{ return this is not valid Stark; }" })
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

                    finite law i32[min max] Run()
                    {
                        return Facade.TextScore<i32[min max]>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported text comptime template call builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.Contains("ret i32 42", ExtractDefinitionBody(llvm!.Text, "__stark_mono_fn_Demo__Facade_TextScore__i32"));
            Assert.Contains("ret i32 42", ExtractDefinitionBody(llvm.Text, "Run"));
            Assert.DoesNotContain("Score: {100}", llvm.Text, StringComparison.Ordinal);
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
    public void PackageImageConsumerFoldsImportedComptimeTemplateCallWithPatterns()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comptime-pattern-template-consumer-");

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

                    enum Step
                    {
                        More(i32[min max]),
                        Done
                    }

                    struct Point
                    {
                        i32[min max] X;
                        i32[min max] Y;
                    }

                    public finite law Step Next<T, comptime u8[0 8] N>(i32[min max] value)
                    {
                        if (value < (i32[min max])N)
                        {
                            return Step.More(value + 1);
                        }

                        return Step.Done;
                    }

                    public finite law i32[min max] PatternScore<T, comptime u8[0 8] N>()
                    {
                        stack mut i32[min max] total = 0;

                        if (Next<T, comptime N>(0) is Step.More(var first))
                        {
                            total += first;
                        }
                        else
                        {
                            total += 1000;
                        }

                        stack Point point = new Point()
                        {
                            X = 3,
                            Y = 26
                        };

                        switch (point)
                        {
                            case Point { X: 1..4, Y: var y }:
                                total += y;
                            default:
                                total += 1000;
                        }

                        const i32[min max][3] values =
                        {
                            1,
                            2,
                            3
                        };

                        switch (values)
                        {
                            case [1, 2, var last]:
                                total += last;
                            default:
                                total += 1000;
                        }

                        switch (Next<T, comptime N>(3))
                        {
                            case Step.More(var last) when last == 4:
                                total += last;
                            case Step.More(var other):
                                total += other + 1000;
                            default:
                                total += 1000;
                        }

                        stack mut i32[min max] value = 0;
                        while willexit (Next<T, comptime N>(value) is Step.More(var next))
                        {
                            value = next;
                            if (value == 2)
                            {
                                continue;
                            }

                            total += value;
                        }

                        return total;
                    }

                    public finite law i32[min max] Fold<T, comptime u8[0 8] M>()
                    {
                        return comptime PatternScore<T, comptime M>();
                    }
                    """,
                    sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var patternTemplate = Assert.Single(
                facadeModule.EffectiveGenericTemplates!.Functions,
                static item => item.QualifiedResolvedName == "Facade.PatternScore");
            Assert.NotNull(patternTemplate.TypedBody);
            Assert.Contains(patternTemplate.TypedBody!.Statements, static statement => statement.ConditionPattern is not null);
            Assert.Contains(patternTemplate.TypedBody.Statements, static statement => statement.SwitchCases is { Count: > 0 });

            var corruptedTemplates = new StarkPackageGenericTemplateSection(
                facadeModule.EffectiveGenericTemplates.Functions
                    .Select(static item => item with { BodyText = "{ return this is not valid Stark; }" })
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

                    finite law i32[min max] Run()
                    {
                        return Facade.Fold<i32[min max], 4>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            FallbackLogAssertions.AssertNoFallbackLogs(consumerResult, "Imported pattern-heavy comptime template call builds");
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(llvm);
            Assert.Contains("__stark_mono_fn_Demo__Facade_PatternScore__i32__N_4", llvm!.Text);
            Assert.Contains("ret i32 42", ExtractDefinitionBody(llvm.Text, "__stark_mono_fn_Demo__Facade_Fold__i32__M_4"));
            Assert.Contains("ret i32 42", ExtractDefinitionBody(llvm.Text, "Run"));
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
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Native.starkpkg.json" : "libNative.starkpkg.json");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Native.lib" : "libNative.a");
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

                    public enum Token
                    {
                        Empty,
                        Data(u8[0 max], u32[0 max])
                    }
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
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

            var tokenLayout = Assert.Single(module.EffectiveCompilerFacts?.ConcreteLayouts ?? [], static layout => layout.QualifiedTypeName == "Native.Token");
            Assert.Equal(8, tokenLayout.SizeBytes);
            Assert.Equal(4, tokenLayout.AlignmentBytes);
            Assert.Equal(0, Assert.Single(tokenLayout.Fields ?? [], static field => field.Name == "$tag").OffsetBytes);
            Assert.Equal(1, Assert.Single(tokenLayout.Fields ?? [], static field => field.Name == "$Data_0").OffsetBytes);
            Assert.Equal(4, Assert.Single(tokenLayout.Fields ?? [], static field => field.Name == "$Data_1").OffsetBytes);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(module), out var sourceText));
            Assert.Contains("[StructLayout(C), Pack(1), Align(4)]", sourceText, StringComparison.Ordinal);
            Assert.Contains("[StructLayout(Explicit), Align(4)]", sourceText, StringComparison.Ordinal);
            Assert.Contains("[FieldOffset(2)]", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(module), out var facts));
            Assert.Equal(1, facts.ConcreteLayouts["Native.Packet"].Fields.Single(static field => field.Name == "Length").OffsetBytes);
            Assert.True(facts.ConcreteLayouts["Native.Packet"].Fields.Single(static field => field.Name == "Length").IsMisaligned);
            Assert.Equal(2, facts.ConcreteLayouts["Native.WordParts"].Fields.Single(static field => field.Name == "High").OffsetBytes);
            Assert.Equal(0, facts.ConcreteLayouts["Native.Token"].Fields.Single(static field => field.Name == "$tag").OffsetBytes);
            Assert.Equal(4, facts.ConcreteLayouts["Native.Token"].Fields.Single(static field => field.Name == "$Data_1").OffsetBytes);

            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Native
                    module Demo

                    finite law u64[0 max] LayoutScore<PacketType, WordPartsType, TokenType>()
                    {
                        if (comptime System.Compiler.StructLayoutIsC<PacketType>())
                        {
                            if (comptime System.Compiler.StructLayoutIsExplicit<WordPartsType>())
                            {
                                if (comptime System.Compiler.StructHasPack<PacketType>())
                                {
                                    if (comptime System.Compiler.StructHasAlign<PacketType>())
                                    {
                                        if (comptime System.Compiler.FieldHasExplicitOffset<WordPartsType, 2>())
                                        {
                                            if (comptime !System.Compiler.EnumVariantPayloadIsMisaligned<TokenType, 1, 1>())
                                            {
                                                if (comptime !System.Compiler.EnumTagIsMisaligned<TokenType>())
                                                {
                                                    if (comptime !System.Compiler.TypeIsZeroSized<PacketType>())
                                                    {
                                                        return comptime System.Compiler.StructPack<PacketType>()
                                                            + comptime System.Compiler.StructAlign<PacketType>()
                                                            + comptime System.Compiler.FieldExplicitOffset<WordPartsType, 2>()
                                                            + comptime System.Compiler.EnumVariantPayloadOffset<TokenType, 1, 0>()
                                                            + comptime System.Compiler.EnumVariantPayloadSize<TokenType, 1, 0>()
                                                            + comptime System.Compiler.EnumVariantPayloadAlign<TokenType, 1, 0>()
                                                            + comptime System.Compiler.EnumVariantPayloadOffset<TokenType, 1, 1>()
                                                            + comptime System.Compiler.EnumVariantPayloadSize<TokenType, 1, 1>()
                                                            + comptime System.Compiler.EnumVariantPayloadAlign<TokenType, 1, 1>()
                                                            + comptime System.Compiler.EnumVariantTag<TokenType, 1>()
                                                            + comptime System.Compiler.EnumTagOffset<TokenType>()
                                                            + comptime System.Compiler.EnumTagSize<TokenType>()
                                                            + comptime System.Compiler.EnumTagAlign<TokenType>()
                                                            + comptime System.Compiler.TypeSize<PacketType>()
                                                            + comptime System.Compiler.TypeAlign<PacketType>()
                                                            + comptime System.Compiler.TypeSize<TokenType>()
                                                            + comptime System.Compiler.TypeAlign<TokenType>();
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }

                    finite law u64[0 max] Run()
                    {
                        return comptime LayoutScore<Native.Packet, Native.WordParts, Native.Token>();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i64 49", runBody);
            Assert.DoesNotContain("call", runBody);
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
    public void PackageImagePreservesNamedAggregateConstantInitializers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-named-aggregate-const-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public struct Pair
                    {
                        i32[min max] Left;
                        i32[min max] Right;
                    }

                    public const Pair Origin = comptime
                    {
                        return new Pair()
                        {
                            Left = 3,
                            Right = 9
                        };
                    };
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedGlobal = Assert.Single(module.EffectiveTypedInterface!.Globals, static global => global.Name == "Origin");

            Assert.Equal("namedaggregate", typedGlobal.ConstantInitializer?.Kind);
            Assert.Equal("named", typedGlobal.ConstantInitializer?.Type.Kind);
            Assert.Equal("Pair", typedGlobal.ConstantInitializer?.Type.Name);
            Assert.Equal(2, typedGlobal.ConstantInitializer?.Elements?.Count);
            Assert.Equal("3", typedGlobal.ConstantInitializer?.Elements?[0].IntegerValue);
            Assert.Equal("9", typedGlobal.ConstantInitializer?.Elements?[1].IntegerValue);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var facts));
            var loadedGlobal = facts.Globals["Facade.Origin"];
            Assert.Equal(TypedConstantInitializerKind.NamedAggregate, loadedGlobal.ConstantInitializer?.Kind);
            Assert.Equal(2, loadedGlobal.ConstantInitializer?.Elements?.Count);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var sourceText));
            Assert.Contains("public const Pair Origin = comptime { stack Pair value; return value; };", sourceText, StringComparison.Ordinal);
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
    public void PackageImagePreservesEnumConstantInitializers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-const-");

        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
            var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public enum Token
                    {
                        End,
                        Integer(i32[min max]),
                        Move
                        {
                            X: i32[min max], Y: i32[min max]
                        },
                    }

                    public const Token Favorite = comptime
                    {
                        return Token.Move
                        {
                            X: 3, Y: 9
                        };
                    };
                    """,
                    sourcePath),
                new CompilerOptions(StopAfterPassId: "lower-abi"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedGlobal = Assert.Single(module.EffectiveTypedInterface!.Globals, static global => global.Name == "Favorite");

            Assert.Equal("enumaggregate", typedGlobal.ConstantInitializer?.Kind);
            Assert.Equal("named", typedGlobal.ConstantInitializer?.Type.Kind);
            Assert.Equal("Token", typedGlobal.ConstantInitializer?.Type.Name);
            Assert.Equal("Move", typedGlobal.ConstantInitializer?.VariantName);
            Assert.Equal(2, typedGlobal.ConstantInitializer?.Elements?.Count);
            Assert.Equal("3", typedGlobal.ConstantInitializer?.Elements?[0].IntegerValue);
            Assert.Equal("9", typedGlobal.ConstantInitializer?.Elements?[1].IntegerValue);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var facts));
            var loadedGlobal = facts.Globals["Facade.Favorite"];
            Assert.Equal(TypedConstantInitializerKind.EnumAggregate, loadedGlobal.ConstantInitializer?.Kind);
            Assert.Equal("Move", loadedGlobal.ConstantInitializer?.VariantName);
            Assert.Equal(2, loadedGlobal.ConstantInitializer?.Elements?.Count);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var sourceText));
            Assert.Contains("public const Token Favorite = comptime { stack Token value; return value; };", sourceText, StringComparison.Ordinal);
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

                    public dyn trait Speaker
                    {
                        finite law i32[min max] Speak(borrow Self self);
                    }

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

                    public struct Dog : Speaker
                    {
                        public i32[min max] Volume;

                        public finite law i32[min max] Speak(borrow Dog self)
                        {
                            return self.Volume;
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
                            stack Dog dog = new Dog()
                        {
                            Volume = 8
                        };
                        stack borrow dyn Speaker natural = dog;
                        stack rawmutptr<i8[min max]> dynContext = natural.Context;
                        stack rawptr<Speaker.Vtable> dynTable = natural.Vtable;
                        stack borrow dyn Speaker dynView = dynview<Speaker>(dynContext, dynTable);
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
                        stack i32[min max] total = values[0] + values[1] + Apply(Inc, 2) + pointerOp(3) + closureOp(6) + Choose(true) + Score(Status.Ok) + Score(Status.Err(4)) + dynView.Speak();
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
                    "switch-dispatch",
                    "dyn-trait-from-parts"
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
            Assert.Contains(importedTemplate.BoundOperations, static operation => operation.Operation is BoundDynTraitFromPartsOperation);

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

                    public trait Tagged<T, comptime u8[1 8] N>
                    {
                    }

                    public struct Widget : Drawable, Tagged<i32[min max], 4>
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
            Assert.Contains("Tagged<i32,N=4>", typedWidget.ImplementedTraits ?? []);
            var typedTaggedTrait = Assert.Single(typedWidget.ImplementedTraitTypes ?? [], static type => type.Name == "Tagged");
            Assert.Single(typedTaggedTrait.TypeArguments ?? []);
            var typedTaggedComptimeArgument = Assert.Single(typedTaggedTrait.ComptimeValueArguments ?? []);
            Assert.Equal("N", typedTaggedComptimeArgument.ParameterName);
            Assert.Equal("4", typedTaggedComptimeArgument.IntegerValue);

            var typedDrawable = Assert.Single(module.EffectiveTypedInterface?.Types ?? [], static type => type.Name == "Drawable");
            Assert.False(Assert.Single(typedDrawable.Methods ?? [], static method => method.Name == "Width").HasBody);
            Assert.True(Assert.Single(typedDrawable.Methods ?? [], static method => method.Name == "Twice").HasBody);

            Assert.True(PackageImageLoader.TryBuildModuleSource(CreateResolvedPackageModule(module), out var sourceText));
            Assert.Contains("public struct Widget : Drawable, Tagged<i32[min max], 4>", sourceText, StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildLoadedPackageImageFacts(CreateResolvedPackageModule(module), out var facts));
            Assert.Contains("Facade.Drawable", facts.NamedTypes["Facade.Widget"].ImplementedTraits);
            Assert.Contains("Facade.Tagged<i32,N=4>", facts.NamedTypes["Facade.Widget"].ImplementedTraits);
            Assert.Contains(facts.NamedTypes["Facade.Widget"].ImplementedTraitTypes, static type =>
                string.Equals(type.NamedType, "Facade.Tagged<i32,N=4>", StringComparison.Ordinal)
                && type.TypeArguments is { Count: 1 }
                && type.ComptimeValueArguments is { Count: 1 });
            Assert.False(facts.FunctionSignatures["Facade.Drawable.Width"].HasBody);
            Assert.True(facts.FunctionSignatures["Facade.Drawable.Twice"].HasBody);

            File.Delete(sourcePath);
            var factConsumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[min max] Run()
                    {
                        if (comptime System.Compiler.ImplementedTraitCount<Facade.Widget>() == 2)
                        {
                            if (comptime System.Compiler.ImplementedTraitTypeIs<Facade.Widget, Facade.Drawable, 0>())
                            {
                                if (comptime System.Compiler.ImplementedTraitTypeIsNamed<Facade.Widget, 0>()
                                    && comptime System.Compiler.ImplementedTraitTypeIsTrait<Facade.Widget, 0>()
                                    && comptime !System.Compiler.ImplementedTraitTypeHasConcreteLayout<Facade.Widget, 0>())
                                {
                                    if (comptime (System.Compiler.ImplementedTraitTypeBaseName<Facade.Widget, 0>() == "Facade.Drawable")
                                        && comptime (System.Compiler.ImplementedTraitTypeModuleName<Facade.Widget, 0>() == "Facade"))
                                    {
                                        if (comptime System.Compiler.ImplementedTraitTypeIs<Facade.Widget, Facade.Tagged<i32[min max], 4>, 1>()
                                            && comptime System.Compiler.ImplementedTraitTypeArgumentTypeIs<Facade.Widget, i32[min max], 1, 0>()
                                            && comptime System.Compiler.ImplementedTraitTypeArgumentTypeIsInteger<Facade.Widget, 1, 0>()
                                            && comptime (System.Compiler.ImplementedTraitTypeArgumentTypeDisplayName<Facade.Widget, 1, 0>() == "i32")
                                            && comptime (System.Compiler.ImplementedTraitTypeComptimeArgumentName<Facade.Widget, 1, 0>() == "N")
                                            && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentTypeIs<Facade.Widget, u8[1 8], 1, 0>()
                                            && comptime (System.Compiler.ImplementedTraitTypeComptimeArgumentTypeDisplayName<Facade.Widget, 1, 0>() == "u8[1 8]")
                                            && comptime System.Compiler.ImplementedTraitTypeComptimeArgumentValueIs<Facade.Widget, 1, 0, 4>())
                                        {
                                            return 42;
                                        }
                                    }
                                }
                            }
                        }

                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "DemoFacts.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(factConsumerResult.Succeeded, string.Join(", ", factConsumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

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
    public void PackageImagePreservesSystemCTypedInterfaceSurface()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-system-c-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");

        try
        {
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    module Facade

                    public alias Fd = System.C.c_int;
                    public alias CountValue = System.C.c_long;
                    public alias VoidPtr = rawmutptr<System.C.c_void>;
                    public alias NamePtr = rawptr<System.C.c_char>;
                    public alias CharBuffer = System.C.c_char[4];
                    public alias NativeCallback = fnptr<ffi(c) finite law System.C.c_int(NamePtr, System.C.c_long)>;

                    public struct Native
                    {
                        System.C.c_int Code;

                        finite law System.C.c_long Echo(borrow Native self, System.C.c_long value)
                        {
                            return value;
                        }
                    }

                    public enum NativeToken
                    {
                        Count(System.C.c_long)
                    }

                    public unsafe ffi(c) fn rawmutptr<System.C.c_void> Allocate(System.C.c_size_t bytes);
                    public unsafe ffi(c) fn void Free(rawmutptr<System.C.c_void> ptr);
                    public unsafe ffi(c) fn System.C.c_long Count(System.C.c_int fd);
                    """,
                    sourcePath),
                new CompilerOptions(
                    StopAfterPassId: "lower-abi",
                    TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(result, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var typedInterface = module.EffectiveTypedInterface!;

            var fdAlias = Assert.Single(typedInterface.TypeAliases!, static alias => alias.Name == "Fd");
            Assert.Equal("integer", fdAlias.TargetType.Kind);
            Assert.Equal(32, fdAlias.TargetType.BitWidth);
            Assert.Equal("System.C.c_int", fdAlias.TargetType.SourceAliasName);
            var countAlias = Assert.Single(typedInterface.TypeAliases!, static alias => alias.Name == "CountValue");
            Assert.Equal("integer", countAlias.TargetType.Kind);
            Assert.Equal(64, countAlias.TargetType.BitWidth);
            Assert.Equal("System.C.c_long", countAlias.TargetType.SourceAliasName);
            var voidPointerAlias = Assert.Single(typedInterface.TypeAliases!, static alias => alias.Name == "VoidPtr");
            Assert.Equal("rawpointer", voidPointerAlias.TargetType.Kind);
            Assert.Equal("cvoid", voidPointerAlias.TargetType.ElementType?.Kind);
            Assert.Equal("System.C.c_void", voidPointerAlias.TargetType.ElementType?.SourceAliasName);

            var allocate = Assert.Single(typedInterface.Functions, static function => function.Name == "Allocate");
            Assert.Equal("rawpointer", allocate.ReturnType.Kind);
            Assert.True(allocate.ReturnType.IsMutablePointer);
            Assert.Equal("cvoid", allocate.ReturnType.ElementType?.Kind);
            Assert.Equal("integer", Assert.Single(allocate.Parameters).Type.Kind);
            Assert.Equal(64, Assert.Single(allocate.Parameters).Type.BitWidth);
            Assert.True(Assert.Single(allocate.Parameters).Type.IsUnsigned);
            Assert.Equal("System.C.c_size_t", Assert.Single(allocate.Parameters).Type.SourceAliasName);

            var free = Assert.Single(typedInterface.Functions, static function => function.Name == "Free");
            var freeParameter = Assert.Single(free.Parameters).Type;
            Assert.Equal("rawpointer", freeParameter.Kind);
            Assert.True(freeParameter.IsMutablePointer);
            Assert.Equal("cvoid", freeParameter.ElementType?.Kind);
            Assert.Equal("System.C.c_void", freeParameter.ElementType?.SourceAliasName);

            var count = Assert.Single(typedInterface.Functions, static function => function.Name == "Count");
            Assert.Equal("integer", count.ReturnType.Kind);
            Assert.Equal(64, count.ReturnType.BitWidth);
            Assert.NotEqual(true, count.ReturnType.IsUnsigned);
            Assert.Equal("System.C.c_long", count.ReturnType.SourceAliasName);
            var countParameter = Assert.Single(count.Parameters).Type;
            Assert.Equal("integer", countParameter.Kind);
            Assert.Equal(32, countParameter.BitWidth);
            Assert.NotEqual(true, countParameter.IsUnsigned);
            Assert.Equal("System.C.c_int", countParameter.SourceAliasName);

            Assert.Contains("\"SourceAliasName\"", manifest.ToJson(), StringComparison.Ordinal);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(manifestPath, libraryPath, manifest, module),
                out var sourceText));

            Assert.Contains("rawmutptr<System.C.c_void> Allocate(System.C.c_size_t bytes)", sourceText, StringComparison.Ordinal);
            Assert.Contains("void Free(rawmutptr<System.C.c_void> ptr)", sourceText, StringComparison.Ordinal);
            Assert.Contains("System.C.c_long Count(System.C.c_int fd)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);
            var consumerResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[min max] Run()
                    {
                        if (comptime System.Compiler.TypeHasCSourceAlias<Facade.Fd>()
                            && comptime (System.Compiler.TypeCSourceAliasName<Facade.Fd>() == "System.C.c_int")
                            && comptime System.Compiler.TypeHasCSourceAlias<Facade.CountValue>()
                            && comptime (System.Compiler.TypeCSourceAliasName<Facade.CountValue>() == "System.C.c_long")
                            && comptime System.Compiler.TypeHasCSourceAlias<System.C.c_char>()
                            && comptime (System.Compiler.TypeCSourceAliasName<System.C.c_char>() == "System.C.c_char")
                            && comptime System.Compiler.RawPointerElementTypeHasCSourceAlias<Facade.NamePtr>()
                            && comptime (System.Compiler.RawPointerElementTypeCSourceAliasName<Facade.NamePtr>() == "System.C.c_char")
                            && comptime System.Compiler.TypeElementTypeHasCSourceAlias<Facade.CharBuffer>()
                            && comptime (System.Compiler.TypeElementTypeCSourceAliasName<Facade.CharBuffer>() == "System.C.c_char")
                            && comptime System.Compiler.FunctionPointerReturnTypeHasCSourceAlias<Facade.NativeCallback>()
                            && comptime (System.Compiler.FunctionPointerReturnTypeCSourceAliasName<Facade.NativeCallback>() == "System.C.c_int")
                            && comptime System.Compiler.FunctionPointerParameterTypeHasCSourceAlias<Facade.NativeCallback, 1>()
                            && comptime (System.Compiler.FunctionPointerParameterTypeCSourceAliasName<Facade.NativeCallback, 1>() == "System.C.c_long")
                            && comptime System.Compiler.FieldTypeHasCSourceAlias<Facade.Native, 0>()
                            && comptime (System.Compiler.FieldTypeCSourceAliasName<Facade.Native, 0>() == "System.C.c_int")
                            && comptime System.Compiler.EnumVariantPayloadTypeHasCSourceAlias<Facade.NativeToken, 0, 0>()
                            && comptime (System.Compiler.EnumVariantPayloadTypeCSourceAliasName<Facade.NativeToken, 0, 0>() == "System.C.c_long")
                            && comptime System.Compiler.MethodReturnTypeHasCSourceAlias<Facade.Native, 0>()
                            && comptime (System.Compiler.MethodReturnTypeCSourceAliasName<Facade.Native, 0>() == "System.C.c_long")
                            && comptime System.Compiler.MethodParameterTypeHasCSourceAlias<Facade.Native, 0, 1>()
                            && comptime (System.Compiler.MethodParameterTypeCSourceAliasName<Facade.Native, 0, 1>() == "System.C.c_long")
                            && comptime !System.Compiler.TypeHasCSourceAlias<i32[min max]>()
                            && comptime (System.Compiler.TypeCSourceAliasName<i32[min max]>() == "")
                            && comptime (System.Compiler.TypeModuleName<Facade.Native>() == "Facade")
                            && comptime (System.Compiler.TypeModuleName<Facade.NativeToken>() == "Facade")
                            && comptime (System.Compiler.TypeModuleName<i32[min max]>() == ""))
                        {
                            return 42;
                        }

                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            var runBody = ExtractDefinitionBody(llvmModule.Text, "Run");
            Assert.Contains("ret i32 42", runBody);
            Assert.DoesNotContain("call", runBody);
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
