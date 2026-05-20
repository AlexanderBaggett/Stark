using Stark.Compiler;

namespace compiler.FeatureTests;

public abstract class FeatureLlvmTestBase
{
    protected static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            options ?? new CompilerOptions(EmitLlvmIr: true));
    }

    protected static string CompileToLlvm(string source, CompilerOptions? options = null)
    {
        var result = Compile(source, options);
        Assert.True(result.Succeeded, FormatDiagnostics(result.Diagnostics));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        return llvmModule.Text;
    }

    protected static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    protected static string ExtractDefinitionHeader(string llvm, string symbolName)
    {
        var prefix = $"@{symbolName}(";

        foreach (var line in llvm.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("define ", StringComparison.Ordinal)
                && line.Contains(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected a definition header for symbol '{symbolName}'.");
    }

    protected static string ExtractDefinitionBody(string llvm, string symbolName)
    {
        var header = ExtractDefinitionHeader(llvm, symbolName);
        var start = llvm.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new Xunit.Sdk.XunitException($"Expected a definition body for symbol '{symbolName}'.");
        }

        var nextDefinition = llvm.IndexOf("\ndefine ", start + header.Length, StringComparison.Ordinal);
        return nextDefinition < 0
            ? llvm[start..]
            : llvm[start..nextDefinition];
    }

    private static string FormatDiagnostics(IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "Compilation failed without diagnostics.";
        }

        return string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
    }
}
