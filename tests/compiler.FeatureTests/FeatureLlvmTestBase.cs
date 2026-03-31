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

    private static string FormatDiagnostics(IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "Compilation failed without diagnostics.";
        }

        return string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
    }
}
