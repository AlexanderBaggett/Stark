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
        Assert.Equal(15, result.Executions.Count(static execution => execution.Status == PassExecutionStatus.Executed));
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

                public fn void Main() { return; }
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

                fn i32 Main() {
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

                fn i32 Main() {
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

                fn i32 Main() {
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

                    fn i32 Main() {
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

            fn i32 Main() {
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
        Assert.Equal("i32", typeCheckModel.Functions["Main"].ReturnType.DisplayName);
        Assert.Contains(typeCheckModel.Literals, literal => literal.LiteralText == "42" && literal.Type.DisplayName == "i8[42 42]");
        Assert.Contains(typeCheckModel.Literals, literal => literal.LiteralText == "null" && literal.Type.Kind == StarkTypeKind.Null);
    }

    [Fact]
    public void UnresolvedImportsFailBeforeTypingAndLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            import Missing.Module
            module Demo

            fn void Main() { return; }
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
            public fn void Main();
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
