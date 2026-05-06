using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineSyntaxModelTests
{
    [Fact]
    public void BackendOpaqueModuleAttributeFlowsIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                [Backend(Opaque)]
                module Demo
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        Assert.Equal("Demo", syntaxModel.ModuleName);
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, syntaxModel.BackendOptimizationMode);
        var attribute = Assert.Single(syntaxModel.ModuleAttributes ?? []);
        Assert.Equal("Backend", attribute.Name);
        Assert.Equal(["Opaque"], attribute.Arguments);
        Assert.False(CompilerCli.ShouldEnableRootModuleLto(result));
    }

    [Theory]
    [InlineData("[Other]\nmodule Demo", "STK2110")]
    [InlineData("[Backend(Transparent)]\nmodule Demo", "STK2112")]
    [InlineData("[Backend(Opaque)]\n[Backend(Opaque)]\nmodule Demo", "STK2111")]
    public void UnsupportedModuleAttributesReportSyntaxModelDiagnostics(string source, string diagnosticCode)
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == diagnosticCode);
    }

    [Theory]
    [InlineData("[Backend(Opaque)]\ninline fn void Run() { ; }")]
    [InlineData("[Backend(Opaque)]\ninlinehint fn void Run() { ; }")]
    [InlineData("[Backend(Opaque)]\nnoinline fn void Run() { ; }")]
    [InlineData("[Backend(Opaque)]\nhot fn void Run() { ; }")]
    [InlineData("[Backend(Opaque)]\ncold fn void Run() { ; }")]
    [InlineData("[Backend(Opaque)]\nffi fn void Run();")]
    public void BackendOpaqueCallableAttributesRejectConflictingModifiers(string declaration)
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                $$"""
                module Demo

                {{declaration}}
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2113");
    }

    [Fact]
    public void BackendOpaqueCallableAttributesRejectDuplicates()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                [Backend(Opaque)]
                [Backend(Opaque)]
                fn void Run() {
                    ;
                }
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2111");
    }

    [Fact]
    public void TestingCallableAttributesFlowIntoSyntaxModelAsMetadata()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                [Fact]
                fn bool AdditionWorks() {
                    return true;
                }

                [Theory]
                fn bool AdditionTheory() {
                    return true;
                }
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var fact = syntaxModel.Declarations.Single(static declaration => declaration.Name == "AdditionWorks");
        var theory = syntaxModel.Declarations.Single(static declaration => declaration.Name == "AdditionTheory");
        Assert.Equal("Fact", Assert.Single(fact.Attributes ?? []).Name);
        Assert.Equal("Theory", Assert.Single(theory.Attributes ?? []).Name);
    }

    [Theory]
    [InlineData(
        """
        [Backend(Opaque)]
        [Backend(Opaque)]
        struct Box {
        }
        """,
        "STK2111")]
    [InlineData(
        """
        [Backend(Opaque)]
        [Backend(Opaque)]
        record Cursor(i32[-(2 ** 31) 2 ** 31 - 1] Position) {
        }
        """,
        "STK2111")]
    [InlineData(
        """
        [Backend(Opaque)]
        [Backend(Opaque)]
        doctrine Numbers {
            finite law bool Equals(i32[-(2 ** 31) 2 ** 31 - 1] left, i32[-(2 ** 31) 2 ** 31 - 1] right);
        }
        """,
        "STK2111")]
    [InlineData(
        """
        struct Box {
            [Backend(Opaque)]
            i32[-(2 ** 31) 2 ** 31 - 1] Value;
        }
        """,
        "STK2110")]
    public void BackendOpaqueTypeAndContractAttributesReportDiagnostics(string declaration, string diagnosticCode)
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                $$"""
                module Demo

                {{declaration}}
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == diagnosticCode);
    }

    [Fact]
    public void BackendOpaqueAttributesFlowIntoCallableAndTypeSyntaxModels()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                [Backend(Opaque)]
                fn void Run() {
                    ;
                }

                [Backend(Opaque)]
                struct Box {
                    i32[-(2 ** 31) 2 ** 31 - 1] Value;

                    fn i32[-(2 ** 31) 2 ** 31 - 1] Read() {
                        return self.Value;
                    }
                }

                [Backend(Opaque)]
                record Cursor(i32[-(2 ** 31) 2 ** 31 - 1] Position) {
                    fn i32[-(2 ** 31) 2 ** 31 - 1] Read() {
                        return self.Position;
                    }
                }

                [Backend(Opaque)]
                doctrine Numbers<T> {
                    finite law bool Equals(borrow T left, borrow T right);
                }
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var run = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Run");
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, run.BackendOptimizationMode);
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, run.Function!.BackendOptimizationMode);

        var box = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box");
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, box.BackendOptimizationMode);
        var boxRead = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box.Read");
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, boxRead.BackendOptimizationMode);
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, boxRead.Function!.BackendOptimizationMode);

        var cursor = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Cursor");
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, cursor.BackendOptimizationMode);
        var cursorRead = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Cursor.Read");
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, cursorRead.BackendOptimizationMode);
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, cursorRead.Function!.BackendOptimizationMode);

        var numbers = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Numbers");
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, numbers.BackendOptimizationMode);
        var equals = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Numbers.Equals");
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, equals.BackendOptimizationMode);
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, equals.Function!.BackendOptimizationMode);
    }

    [Fact]
    public void BackendOpaqueCallableAttributeForcesNoInlineEffect()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                [Backend(Opaque)]
                finite law i32[-(2 ** 31) 2 ** 31 - 1] Read() {
                    return 1;
                }
                """),
            new CompilerOptions(StopAfterPassId: "refine-function-effects"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        var read = effectModel.Functions["Read"];
        Assert.Equal(ModuleBackendOptimizationMode.Opaque, read.BackendOptimizationMode);
        Assert.Equal(InlinePreference.NoInline, read.InlinePreference);
    }

    [Fact]
    public void BackendOpaqueCallableEmitsLlvmOptimizationBoundary()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                [Backend(Opaque)]
                finite law i32[-(2 ** 31) 2 ** 31 - 1] Read() {
                    return 1;
                }

                finite law i32[-(2 ** 31) 2 ** 31 - 1] Fast() {
                    return 2;
                }

                export unsafe ffi fn i32[-(2 ** 31) 2 ** 31 - 1] main() {
                    return Read() + Fast();
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("optnone", llvmModule.Text, StringComparison.Ordinal);
        Assert.Contains("noinline", llvmModule.Text, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 @Read(", llvmModule.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i32 @Fast(", llvmModule.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StructAndRecordDestructorsFlowIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Buffer {
                    i32[-(2 ** 31) 2 ** 31 - 1] Value;

                    drop {
                        ;
                    }
                }

                record Cursor(i32[-(2 ** 31) 2 ** 31 - 1] Position) {
                    mut drop {
                        self.Position = 0;
                    }
                }
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var buffer = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Struct && declaration.Name == "Buffer");
        Assert.NotNull(buffer.Destructor);
        Assert.False(buffer.Destructor!.IsMutable);

        var cursor = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Record && declaration.Name == "Cursor");
        Assert.NotNull(cursor.Destructor);
        Assert.True(cursor.Destructor!.IsMutable);
    }


    [Fact]
    public void TypeAliasDeclarationsFlowIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public alias Byte = i8[-(2 ** 7) 2 ** 7 - 1];
                alias BufferView<T> = borrow T[];
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var byteAlias = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.TypeAlias && declaration.Name == "Byte");
        Assert.Equal(StarkVisibility.Public, byteAlias.Visibility);
        Assert.NotNull(byteAlias.TypeAlias);
        Assert.Equal("i8[-(2**7)2**7-1]", byteAlias.TypeAlias!.AliasedType);
        Assert.Empty(byteAlias.TypeAlias.GenericParameters);

        var bufferViewAlias = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.TypeAlias && declaration.Name == "BufferView");
        Assert.Equal(StarkVisibility.Module, bufferViewAlias.Visibility);
        Assert.NotNull(bufferViewAlias.TypeAlias);
        Assert.Equal("borrowT[]", bufferViewAlias.TypeAlias!.AliasedType);
        Assert.Equal(["T"], bufferViewAlias.TypeAlias.GenericParameters);
    }


    [Fact]
    public void GenericFunctionDeclarationsCarryTypeParametersIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public fn T Identity<T>(T value) {
                    return value;
                }

                struct Box {
                    fn T Echo<T>(T value) {
                        return value;
                    }
                }

                trait Reader {
                    law T Read<T>(T value);
                }

                doctrine Projector {
                    law T Project<T>(T value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var identity = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Identity");
        Assert.NotNull(identity.Function);
        Assert.True(identity.Function!.IsGeneric);
        Assert.Equal(["T"], identity.Function.GenericParams);

        var echo = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box.Echo");
        Assert.NotNull(echo.Function);
        Assert.True(echo.Function!.IsGeneric);
        Assert.Equal(["T"], echo.Function.GenericParams);

        var read = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Reader.Read");
        Assert.NotNull(read.Function);
        Assert.True(read.Function!.IsGeneric);
        Assert.Equal(["T"], read.Function.GenericParams);

        var project = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Projector.Project");
        Assert.NotNull(project.Function);
        Assert.True(project.Function!.IsGeneric);
        Assert.Equal(["T"], project.Function.GenericParams);
    }
}
