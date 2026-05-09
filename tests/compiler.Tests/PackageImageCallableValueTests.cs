using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageCallableValueTests
{
    [Fact]
    public void PackageImagePreservesFunctionPointerTypesAndUnsafeFunctionFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-callable-values-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json");

        try
        {
            var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(
                """
                module Facade

                public fn void Register(fnptr<fn void()> callback);
                public fn void RegisterFinite(fnptr<finite u32[0 2 ** 31 - 1]()> callback);
                public fn void RegisterLaw(fnptr<law bool()> callback);
                public fn void RegisterFiniteLaw(fnptr<finite law u32[0 2 ** 31 - 1]()> callback);
                public unsafe fn void Dangerous();
                """,
                sourcePath));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                result,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var register = Assert.Single(module.EffectiveTypedInterface!.Functions, static function => function.Name == "Register");
            var callbackType = Assert.Single(register.Parameters).Type;

            Assert.Equal("functionpointer", callbackType.Kind);
            Assert.Equal("fn", callbackType.FunctionKind);
            Assert.Equal("void", callbackType.ReturnType!.Kind);
            Assert.Empty(callbackType.ParameterTypes ?? []);
            AssertFunctionPointerKind(module, "RegisterFinite", "finite");
            AssertFunctionPointerKind(module, "RegisterLaw", "law");
            AssertFunctionPointerKind(module, "RegisterFiniteLaw", "finite law");

            var dangerous = Assert.Single(module.EffectiveTypedInterface!.Functions, static function => function.Name == "Dangerous");
            Assert.True(dangerous.IsUnsafe);

            Assert.True(PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(
                    manifestPath,
                    Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                    manifest,
                    module),
                out var sourceText));
            Assert.Contains("public unsafe fn void Dangerous();", sourceText, StringComparison.Ordinal);
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

    private static void AssertFunctionPointerKind(
        StarkPackageModuleManifest module,
        string functionName,
        string expectedKind)
    {
        var function = Assert.Single(module.EffectiveTypedInterface!.Functions, function => function.Name == functionName);
        var parameterType = Assert.Single(function.Parameters).Type;
        Assert.Equal("functionpointer", parameterType.Kind);
        Assert.Equal(expectedKind, parameterType.FunctionKind);
    }

    [Fact]
    public void PackageImageBackedExplicitConstructorWithAliasCallableParameterLowersWithoutSource()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-callable-constructor-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public alias Factory = fnptr<fn i32[min max]()>;

                public struct Box {
                    internal i32[min max] Value;

                    Box(Factory factory) {
                        self.Value = factory();
                    }
                }
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Make() {
                        return 11;
                    }

                    fn i32[min max] Run() {
                        stack Facade.Box box = new(Make);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
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
    public void PackageImageBackedCallableAliasPreservesFiniteLawFunctionPointerKind()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-callable-alias-kind-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public alias StrictFactory = fnptr<finite law u32[0 2 ** 31 - 1]()>;

                public struct Box {
                    internal u32[0 2 ** 31 - 1] Value;

                    Box(StrictFactory factory) {
                        self.Value = factory();
                    }
                }
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var module = Assert.Single(manifest.Modules, static item => item.ModuleName == "Facade");
            var alias = Assert.Single(module.EffectiveTypedInterface!.TypeAliases!, static item => item.Name == "StrictFactory");
            Assert.Equal("functionpointer", alias.TargetType.Kind);
            Assert.Equal("finite law", alias.TargetType.FunctionKind);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law u32[0 2 ** 31 - 1] Make() {
                        return 11;
                    }

                    fn u32[0 2 ** 31 - 1] Run() {
                        stack Facade.Box box = new(Make);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
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
    public void PackageImageBackedFunctionPointerParametersTargetTypeNonCapturingLambdas()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-callable-lambda-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Apply(
                    fnptr<finite law i32[min max](i32[min max])> callback,
                    i32[min max] value);
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run() {
                        return Facade.Apply((i32[min max] value) => value + 1, 41);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "emit-llvm"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);
            var lambda = Assert.Single(typeModel.Lambdas);
            Assert.Equal(StarkFunctionKind.FiniteLaw, lambda.FunctionPointerType.FunctionPointerKind);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
            Assert.NotNull(hir);
            Assert.NotNull(mir);
            Assert.NotNull(ssa);
            Assert.NotNull(optimizedSsa);
            Assert.NotNull(llvm);
            Assert.Equal(lambda.FunctionName, Assert.Single(hir.AddressTakenFunctions));
            Assert.Equal(lambda.FunctionName, Assert.Single(mir.AddressTakenFunctions));
            Assert.Equal(lambda.FunctionName, Assert.Single(ssa.AddressTakenFunctions));
            Assert.Equal(lambda.FunctionName, Assert.Single(optimizedSsa.AddressTakenFunctions));
            Assert.Equal(lambda.FunctionName, Assert.Single(llvm.AddressTakenFunctions));
            Assert.Contains("; synthetic definition: Run.__lambda_", llvm.Text, StringComparison.Ordinal);
            Assert.Matches(@"define internal dso_local fastcc noundef(?: range\([^)]*\))? i32 @Run___lambda_", llvm.Text);
            Assert.Matches(@"call fastcc i32 @Facade_Apply\(ptr (noundef )?@Run___lambda_", llvm.Text);
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
    public void PackageImageBackedLawFunctionPointerParametersValidateLambdaBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-callable-law-lambda-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn void Register(fnptr<law i32[min max]()> callback);
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    static i32[min max] Counter = 1;

                    fn i32[min max] Impure() {
                        return Counter;
                    }

                    fn void Run() {
                        Facade.Register(() => Impure());
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "semantic-validate"));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK4106"
                    && diagnostic.Message.Contains("Run.__lambda_", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("may only call other laws", StringComparison.Ordinal));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);
            var lambda = Assert.Single(typeModel.Lambdas);
            Assert.Equal(StarkFunctionKind.Law, lambda.FunctionPointerType.FunctionPointerKind);
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
    public void PackageImageBackedQualifiedFunctionItemsPromoteToOrdinaryFunctionPointers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-qualified-callable-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Make();
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run() {
                        stack fnptr<fn i32[min max]()> callback = Facade.Make;
                        return callback();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);
            var addressTaken = Assert.Single(typeModel.AddressTakenFunctions);
            Assert.Equal("Facade.Make", addressTaken.Signature.Name);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            Assert.Equal("Facade.Make", Assert.Single(mir.AddressTakenFunctions));
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
    public void PackageImageBackedFunctionItemsPromoteFromEachDeclaredFunctionKind()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-callable-kind-promotion-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Plain();
                public finite i32[min max] FiniteOnly();
                public law i32[min max] LawOnly();
                public finite law i32[min max] Strict();
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        stack fnptr<fn i32[min max]()> plain = Facade.Plain;
                        stack fnptr<finite i32[min max]()> finiteOnly = Facade.FiniteOnly;
                        stack fnptr<law i32[min max]()> lawOnly = Facade.LawOnly;
                        stack fnptr<finite law i32[min max]()> strict = Facade.Strict;
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);

            Assert.Equal(4, typeModel.FunctionPointerPromotions.Count);
            Assert.Equal(4, typeModel.AddressTakenFunctions.Count);
            Assert.Contains(typeModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Facade.Plain" && addressTaken.Signature.Kind == StarkFunctionKind.Fn);
            Assert.Contains(typeModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Facade.FiniteOnly" && addressTaken.Signature.Kind == StarkFunctionKind.Finite);
            Assert.Contains(typeModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Facade.LawOnly" && addressTaken.Signature.Kind == StarkFunctionKind.Law);
            Assert.Contains(typeModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Facade.Strict" && addressTaken.Signature.Kind == StarkFunctionKind.FiniteLaw);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            Assert.Equal(
                ["Facade.FiniteOnly", "Facade.LawOnly", "Facade.Plain", "Facade.Strict"],
                mir.AddressTakenFunctions.OrderBy(static functionName => functionName, StringComparer.Ordinal));
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
    public void PackageImageBackedOverloadedFunctionItemsPreserveDistinctAddressTakenFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-overloaded-callable-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Pick();
                public fn i32[min max] Pick(i32[min max] value);
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run() {
                        stack fnptr<fn i32[min max]()> first = Facade.Pick;
                        stack fnptr<fn i32[min max](i32[min max])> second = Facade.Pick;
                        return first() + second(2);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);
            Assert.Equal(2, typeModel.FunctionPointerPromotions.Count);
            Assert.Equal(2, typeModel.AddressTakenFunctions.Count);
            Assert.All(typeModel.AddressTakenFunctions, static addressTaken => Assert.Equal("Facade.Pick", addressTaken.Signature.DisplaySourceName));
            Assert.Equal(
                2,
                typeModel.AddressTakenFunctions
                    .Select(static addressTaken => addressTaken.Signature.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);
            Assert.Equal(2, mir.AddressTakenFunctions.Count);
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
    public void PackageImageBackedFunctionItemsPreserveFunctionKindObligationRejections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-callable-kind-rejection-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Plain();
                public finite i32[min max] FiniteOnly();
                public law i32[min max] LawOnly();
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        stack fnptr<finite i32[min max]()> needsFinite = Facade.Plain;
                        stack fnptr<law i32[min max]()> needsLaw = Facade.Plain;
                        stack fnptr<finite law i32[min max]()> needsBothFromFinite = Facade.FiniteOnly;
                        stack fnptr<finite law i32[min max]()> needsBothFromLaw = Facade.LawOnly;
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "type-check"));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3002"
                    && diagnostic.Message.Contains("Function item 'Facade.Plain' cannot be promoted", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("finite", StringComparison.Ordinal));
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3002"
                    && diagnostic.Message.Contains("Function item 'Facade.Plain' cannot be promoted", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("law", StringComparison.Ordinal));
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3002"
                    && diagnostic.Message.Contains("Function item 'Facade.FiniteOnly' cannot be promoted", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("finite law", StringComparison.Ordinal));
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3002"
                    && diagnostic.Message.Contains("Function item 'Facade.LawOnly' cannot be promoted", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("finite law", StringComparison.Ordinal));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);
            Assert.Empty(typeModel.AddressTakenFunctions);
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
    public void PackageImageBackedUnsafeFunctionItemsDoNotPromoteToOrdinaryFunctionPointers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-unsafe-callable-boundary-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn i32[min max] Touch();
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        stack fnptr<fn i32[min max]()> callback = Facade.Touch;
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "type-check"));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3024"
                    && diagnostic.Message.Contains("cannot be promoted to ordinary function pointer", StringComparison.Ordinal));
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
    public void PackageImageBackedUnsafeFunctionCallsRequireUnsafeContext()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-unsafe-call-boundary-");
        var sourcePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var manifestPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.starkpkg.json" : "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn void Touch();
                """,
                sourcePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(sourcePath);

            var bad = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        Facade.Touch();
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Bad.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "type-check"));

            Assert.False(bad.Succeeded);
            Assert.Contains(
                bad.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3024"
                    && diagnostic.Message.Contains("Unsafe function 'Facade.Touch' requires an unsafe context", StringComparison.Ordinal));

            var good = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        unsafe {
                            Facade.Touch();
                        }

                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Good.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "type-check"));

            Assert.True(good.Succeeded, string.Join(", ", good.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
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
