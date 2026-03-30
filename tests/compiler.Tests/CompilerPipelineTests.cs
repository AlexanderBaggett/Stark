using Stark.Compiler;

namespace compiler.Tests;

public sealed class CompilerPipelineTests
{
    [Fact]
    public void MinimalModuleRunsThroughTheFullPipeline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);
        Assert.Equal("Demo", syntaxModel.ModuleName);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? semanticValidation));
        Assert.NotNull(semanticValidation);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OwnershipValidation, out OwnershipValidationModel? ownershipValidation));
        Assert.NotNull(ownershipValidation);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("ModuleID = 'Demo'", llvmModule.Text);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssaModule));
        Assert.NotNull(ssaModule);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsaModule));
        Assert.NotNull(optimizedSsaModule);
        Assert.Equal(19, result.Executions.Count(static execution => execution.Status == PassExecutionStatus.Executed));
    }

    [Fact]
    public void FunctionKindsAndModifiersDeriveExpectedEffectProfiles()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            public finite law i32 Add(i32 left, i32 right);
            export ffi cold fn void Accept(rawptr<i8> value);
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        var add = effectModel.Functions["Add"];
        Assert.True(add.IsPure);
        Assert.True(add.NoSync);
        Assert.True(add.NoFree);
        Assert.True(add.NoUnwind);
        Assert.True(add.WillReturn);
        Assert.True(add.MustProgress);
        Assert.True(add.UseFastCallingConvention);

        var accept = effectModel.Functions["Accept"];
        Assert.False(accept.IsPure);
        Assert.False(accept.UseFastCallingConvention);
        Assert.False(accept.NoUnwind);
        Assert.True(accept.IsFfi);
        Assert.True(accept.IsCold);
    }

    [Fact]
    public void ImportedModulesResolveThroughTheConfiguredResolver()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Core.Text
                module Demo

                public fn void Run() { return; }
                """),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    new ResolvedModuleReference("Core.Text", "/virtual/Core/Text.stark", IsExternal: false)
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Core.Text"));
        Assert.Single(moduleGraph.Imports);
        Assert.True(moduleGraph.Imports[0].IsResolved);
        Assert.False(moduleGraph.Imports[0].IsExported);
    }

    [Fact]
    public void ImportedFunctionsFromLoadedModulesParticipateInTypingAndEffects()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                fn i32 Run() {
                    return Math.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32 Add(i32 left, i32 right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.ContainsKey("Math.Add"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);
        Assert.True(effectModel.Functions["Math.Add"].WillReturn);
        Assert.True(effectModel.Functions["Math.Add"].IsPure);
    }

    [Fact]
    public void PrivateTransitiveImportsDoNotBecomeVisibleToTheRootModule()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                fn i32 Run() {
                    return Math.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Facade", "/virtual/Facade.stark", IsExternal: false),
                        """
                        import Math
                        module Facade

                        public fn i32 Double(i32 value) {
                            return Math.Add(value, value);
                        }
                        """,
                        "/virtual/Facade.stark"
                    ),
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32 Add(i32 left, i32 right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK3003" && diagnostic.Message.Contains("Math", StringComparison.Ordinal));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.False(moduleGraph.HasModule("Math"));
        Assert.True(moduleGraph.ContainsLoadedModule("Math"));
    }

    [Fact]
    public void PublicReExportsMakeTransitiveModulesVisibleToTheRootModule()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                fn i32 Run() {
                    return Math.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Facade", "/virtual/Facade.stark", IsExternal: false),
                        """
                        export import Math
                        module Facade

                        public fn i32 Double(i32 value) {
                            return Math.Add(value, value);
                        }
                        """,
                        "/virtual/Facade.stark"
                    ),
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32 Add(i32 left, i32 right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Math"));
        Assert.Contains(
            moduleGraph.Imports,
                    edge => edge.FromModule == "Facade"
                            && edge.RequestedModule == "Math"
                    && edge.IsExported
                    && edge.IsResolved);
    }

    [Fact]
    public async Task ManifestBackedLibrariesResolveWithoutSourceFiles()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32 Add(i32 left, i32 right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public finite law i32 Double(i32 value) {
                    return Math.Add(value, value);
                }
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [facadePath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Equal(string.Empty, buildStderr.ToString());

            File.Delete(facadePath);
            File.Delete(mathPath);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run() {
                        return Math.Add(3, 4);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
            Assert.NotNull(moduleGraph);
            Assert.True(moduleGraph.HasModule("Facade"));
            Assert.True(moduleGraph.HasModule("Math"));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.Functions.ContainsKey("Math.Add"));
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
    public void ManifestBackedEnumsPreserveVariantShapesWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-enum-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Token {
                    End,
                    Integer(i32),
                    Move { X: i32, Y: i32 },
                }

                fn void Touch() {
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded);

            var manifest = PackageManifestBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

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
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.Token", out var tokenEnum));
            Assert.NotNull(tokenEnum);
            Assert.Equal(DeclarationKind.Enum, tokenEnum.Kind);
            Assert.Equal(3, tokenEnum.Variants.Count);
            Assert.True(tokenEnum.Variants[0].IsUnit);
            Assert.Equal("Integer", tokenEnum.Variants[1].Name);
            Assert.Equal("i32", Assert.Single(tokenEnum.Variants[1].Fields).Type.DisplayName);
            Assert.True(tokenEnum.Variants[2].UsesNamedFields);
            Assert.Equal("X", tokenEnum.Variants[2].Fields[0].Name);
            Assert.Equal("Y", tokenEnum.Variants[2].Fields[1].Name);
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
    public void LiteralTypingAndBodyCheckingProduceTypedArtifacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Types

            struct Widget {
                i32 Value;
            }

            public const i32 Answer = 42;
            internal static rawptr<i8> Buffer = null;

            fn i32 Run() {
                stack Widget widget = new Widget() { Value = 1 };
                stack i32 value = widget.Value + 2;
                return value;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Widget"));
        Assert.True(typeCheckModel.Globals.ContainsKey("Answer"));
        Assert.Equal("i32", typeCheckModel.Functions["Run"].ReturnType.DisplayName);
        Assert.Contains(typeCheckModel.Literals, literal => literal.LiteralText == "42" && literal.Type.DisplayName == "i8[42 42]");
        Assert.Contains(typeCheckModel.Literals, literal => literal.LiteralText == "null" && literal.Type.Kind == StarkTypeKind.Null);
    }

    [Fact]
    public void EnumDeclarationsFlowIntoSyntaxAndTypeModels()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Enums

            public enum Result<T, E> {
                Ok(T),
                Err(E),
            }

            enum Token {
                End,
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            fn void Run() {
                return;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Enum && declaration.Name == "Result");
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Enum && declaration.Name == "Token");

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.TryGetValue("Result", out var resultEnum));
        Assert.NotNull(resultEnum);
        Assert.Equal(DeclarationKind.Enum, resultEnum.Kind);
        Assert.Equal(2, resultEnum.Variants.Count);
        Assert.Equal("Ok", resultEnum.Variants[0].Name);
        Assert.Equal("T", Assert.Single(resultEnum.Variants[0].Fields).Type.DisplayName);

        Assert.True(typeCheckModel.NamedTypes.TryGetValue("Token", out var tokenEnum));
        Assert.NotNull(tokenEnum);
        Assert.Equal(DeclarationKind.Enum, tokenEnum.Kind);
        Assert.Equal(3, tokenEnum.Variants.Count);
        Assert.True(tokenEnum.Variants[0].IsUnit);
        Assert.False(tokenEnum.Variants[1].UsesNamedFields);
        Assert.Equal("i32", Assert.Single(tokenEnum.Variants[1].Fields).Type.DisplayName);
        Assert.True(tokenEnum.Variants[2].UsesNamedFields);
        Assert.Equal("X", tokenEnum.Variants[2].Fields[0].Name);
        Assert.Equal("Y", tokenEnum.Variants[2].Fields[1].Name);
    }

    [Fact]
    public void InternalEnumsDeriveDirectTagLayoutsAndFlowThroughTheFullPipeline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Enums

            enum Token {
                End,
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            fn Token Make(i32 value) {
                if (value == 0) {
                    return Token.End;
                }

                if (value == 1) {
                    return Token.Integer(7);
                }

                return Token.Move { X: value, Y: 2 };
            }

            fn Token Echo(Token token) {
                return token;
            }

            fn i32 Run() {
                stack Token first = Token.Integer(5);
                stack Token second = Token.Move { X: 1, Y: 2 };
                stack Token third = Make(0);
                stack Token fourth = Echo(second);
                return 0;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
        Assert.NotNull(enumLayoutModel);
        Assert.True(enumLayoutModel.Layouts.TryGetValue("Token", out var tokenLayout));
        Assert.NotNull(tokenLayout);
        Assert.Equal(EnumLayoutKind.DirectTag, tokenLayout.Kind);
        Assert.Equal("$tag", tokenLayout.TagField.Name);
        Assert.Equal(4, tokenLayout.OrderedFields.Count);
        Assert.Equal("$Integer_0", tokenLayout.OrderedFields[1].Name);
        Assert.Equal("$Move_X", tokenLayout.OrderedFields[2].Name);
        Assert.Equal("$Move_Y", tokenLayout.OrderedFields[3].Name);

        Assert.True(tokenLayout.TryGetVariant("End", out var endVariant));
        Assert.Equal(0, endVariant.TagValue);
        Assert.Empty(endVariant.Fields);

        Assert.True(tokenLayout.TryGetVariant("Integer", out var integerVariant));
        Assert.Equal(1, integerVariant.TagValue);
        Assert.Equal("$Integer_0", Assert.Single(integerVariant.Fields).StorageFieldName);

        Assert.True(tokenLayout.TryGetVariant("Move", out var moveVariant));
        Assert.Equal(2, moveVariant.TagValue);
        Assert.Equal("X", moveVariant.Fields[0].SourceFieldName);
        Assert.Equal("Y", moveVariant.Fields[1].SourceFieldName);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("%Token = type {", llvmModule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedImportsFailBeforeTypingAndLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            import Missing.Module
            module Demo

            fn void Run() { return; }
            """));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2000");
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? _));
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? _));
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.OwnershipValidation, out OwnershipValidationModel? _));
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "module-graph" && execution.Status == PassExecutionStatus.Executed);
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "type-check" && execution.Status == PassExecutionStatus.Skipped);
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "semantic-validate" && execution.Status == PassExecutionStatus.Skipped);
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "ownership-validate" && execution.Status == PassExecutionStatus.Skipped);
    }

    [Fact]
    public void InvalidTypedAssignmentsProduceTypeDiagnostics()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            public const i32 Value = null;
            """));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK3002");
    }

    [Fact]
    public void ParseErrorsPreventLaterPassesFromRunning()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            public fn void Run();
            """));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK1000");
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? _));

        Assert.Equal(PassExecutionStatus.Executed, result.Executions[0].Status);
        Assert.All(
            result.Executions.Skip(1),
            static execution => Assert.Equal(PassExecutionStatus.Skipped, execution.Status));
    }
}
