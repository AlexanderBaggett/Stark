using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineSyntaxModelTests
{
    [Fact]
    public void StructAndRecordDestructorsFlowIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Buffer {
                    i32[-2147483648 2147483647] Value;

                    drop {
                        ;
                    }
                }

                record Cursor(i32[-2147483648 2147483647] Position) {
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

                public alias Byte = i8[-128 127];
                alias BufferView<T> = borrow T[];
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var byteAlias = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.TypeAlias && declaration.Name == "Byte");
        Assert.Equal(StarkVisibility.Public, byteAlias.Visibility);
        Assert.NotNull(byteAlias.TypeAlias);
        Assert.Equal("i8[-128127]", byteAlias.TypeAlias!.AliasedType);
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
