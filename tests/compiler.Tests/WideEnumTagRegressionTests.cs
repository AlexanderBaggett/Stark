using System.Text;
using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Enum tag storage regression: tags are signed integers, but the layout
/// builder used the unsigned capacity boundary (256 variants per i8), so an
/// enum with more than 128 variants produced an i8 tag with an out-of-width
/// range such as 'i8[0 151]' and SSA validation rejected every variant
/// constant. The Stage 1 lexer's TokenKind (152 variants) hit this first.
/// </summary>
public sealed class WideEnumTagRegressionTests
{
    [Fact]
    public void EnumsWithMoreThan128VariantsCompileThroughTheWholePipeline()
    {
        var source = new StringBuilder();
        source.AppendLine("module Demo");
        source.AppendLine();
        source.AppendLine("enum Wide");
        source.AppendLine("{");
        for (var index = 0; index < 152; index++)
        {
            source.AppendLine($"    Variant{index},");
        }

        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("finite law bool IsLast(Wide value)");
        source.AppendLine("{");
        source.AppendLine("    return value == Wide.Variant151;");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("export fn i32[min max] main()");
        source.AppendLine("{");
        source.AppendLine("    if (!IsLast(Wide.Variant151))");
        source.AppendLine("    {");
        source.AppendLine("        return 1;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    if (IsLast(Wide.Variant0))");
        source.AppendLine("    {");
        source.AppendLine("        return 2;");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    return 0;");
        source.AppendLine("}");

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source.ToString()),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
