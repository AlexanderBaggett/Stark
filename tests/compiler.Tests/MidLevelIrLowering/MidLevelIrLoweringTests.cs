using Stark.Compiler;
using System.Text;
using Xunit.Abstractions;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    private readonly ITestOutputHelper _output;

    public MidLevelIrLoweringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        using var logScope = CompilerLogOutput.Push(new TestOutputWriter(_output), DiagnosticSeverity.Info);
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static MidLevelIrModule GetMir(CompilationResult result)
    {
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        return mir;
    }

    private sealed class TestOutputWriter(ITestOutputHelper output) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            output.WriteLine(value ?? string.Empty);
        }
    }
}
