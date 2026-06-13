using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Multiline raw string literals follow C# raw-string rules: the newline
/// after the opening quotes and the newline before the closing quotes are
/// not part of the value, the whitespace before the closing quotes is
/// stripped from every content line, content on the opening-quote line and
/// under-indented lines are diagnosed, and interpolated raw literals
/// normalize the same way before hole splitting.
/// </summary>
public sealed class RawMultilineStringParityTests
{
    [Fact]
    public void MultilineRawStringsStripDelimiterNewlinesAndClosingIndentation()
    {
        var result = CompileLlvm(
            """""
            module Demo

            unsafe fn ascii Value()
            {
                return raw"""
                    first
                      indented
                    second
                    """;
            }
            """"");

        Assert.True(result.Succeeded, RenderDiagnostics(result));
        var llvm = GetLlvm(result);
        Assert.Contains("c\"first\\0A  indented\\0Asecond\\00\"", llvm);
    }

    [Fact]
    public void WhitespaceOnlyLinesShorterThanTheClosingIndentationBecomeEmptyLines()
    {
        var result = CompileLlvm(
            "module Demo\n\nunsafe fn ascii Value()\n{\n    return raw\"\"\"\n        first\n  \n        second\n        \"\"\";\n}\n");

        Assert.True(result.Succeeded, RenderDiagnostics(result));
        var llvm = GetLlvm(result);
        Assert.Contains("c\"first\\0A\\0Asecond\\00\"", llvm);
    }

    [Fact]
    public void SingleLineRawTripleQuoteStringsKeepTheirContentVerbatim()
    {
        var result = CompileLlvm(
            """""
            module Demo

            unsafe fn ascii Value()
            {
                return raw"""quote " and \d inside""";
            }
            """"");

        Assert.True(result.Succeeded, RenderDiagnostics(result));
        var llvm = GetLlvm(result);
        Assert.Contains("c\"quote \\22 and \\5Cd inside\\00\"", llvm);
    }

    [Fact]
    public void ContentOnTheOpeningQuoteLineIsDiagnosed()
    {
        var result = CompileLlvm(
            """""
            module Demo

            unsafe fn ascii Value()
            {
                return raw"""first
                    second
                    """;
            }
            """"");

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                "must start their content on the line after the opening quotes",
                StringComparison.Ordinal));
    }

    [Fact]
    public void LinesIndentedLessThanTheClosingQuotesAreDiagnosed()
    {
        var result = CompileLlvm(
            "module Demo\n\nunsafe fn ascii Value()\n{\n    return raw\"\"\"\n  under\n        \"\"\";\n}\n");

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                "must start with the same whitespace as the closing quotes",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ContentBeforeTheClosingQuotesOnTheirLineIsDiagnosed()
    {
        var result = CompileLlvm(
            """""
            module Demo

            unsafe fn ascii Value()
            {
                return raw"""
                    first
                    second """;
            }
            """"");

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                "closing quotes of a multiline raw string literal must be on their own line",
                StringComparison.Ordinal));
    }

    [Fact]
    public void InterpolatedRawMultilineStringsNormalizeBeforeHoleSplitting()
    {
        var result = CompileLlvm(
            """""
            module Demo

            unsafe fn ascii Value()
            {
                return $raw"""
                    Score: {100}
                    next \d
                    """;
            }
            """"");

        Assert.True(result.Succeeded, RenderDiagnostics(result));
        var llvm = GetLlvm(result);
        Assert.Contains("c\"Score: 100\\0Anext \\5Cd\\00\"", llvm);
    }

    private static CompilationResult CompileLlvm(string source)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true));
    }

    private static string GetLlvm(CompilationResult result)
    {
        return result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
    }

    private static string RenderDiagnostics(CompilationResult result)
    {
        return string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
    }
}
