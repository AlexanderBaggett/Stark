using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// MIR lowering regression for single-line raw string literals used as plain
/// expression operands. CreateLiteralOperand classified literals by their
/// first character, so a raw literal token ("raw\"...\"") fell through the
/// text branches and crashed in integer parsing (BigInteger.Parse:
/// "The value could not be parsed."). Multiline raw literals were unaffected
/// because the crash required the operand path, which switch/binding positions
/// did not hit the same way.
/// </summary>
public sealed class RawSingleLineLiteralRegressionTests
{
    [Fact]
    public void RawSingleLineLiteralsLowerAsComparisonOperands()
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Demo

                export fn i32[min max] main()
                {
                    stack ascii value = "abc";
                    if (value == raw"abc")
                    {
                        return 0;
                    }

                    stack ascii pattern = raw"\d+\.\d+";
                    if (pattern == raw"\d+\.\d+")
                    {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(EmitLlvmIr: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
}
