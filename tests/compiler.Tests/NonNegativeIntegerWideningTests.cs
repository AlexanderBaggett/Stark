using System.Text.RegularExpressions;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class NonNegativeIntegerWideningTests
{
    [Theory]
    [InlineData(8, 16)]
    [InlineData(16, 32)]
    [InlineData(24, 32)]
    [InlineData(32, 64)]
    [InlineData(64, 128)]
    public void WideningConsumesValueFactsAndRetainsNegativeAndUnsignedSemantics(int width, int wide)
    {
        var source = $$"""
            module Widening
            public noinline finite law i{{wide}}[min max] Masked(i{{width}}[min max] value) {
                return (i{{wide}}[min max])(value & 63);
            }
            public noinline finite law i{{wide}}[min max] Signed(i{{width}}[min max] value) {
                return (i{{wide}}[min max])value;
            }
            public noinline finite law i{{wide}}[min max] Unsigned(u{{width}}[0 max] value) {
                return (i{{wide}}[min max])value;
            }
            public unsafe noinline finite law u{{wide}}[0 max] NegativeToUnsigned(i{{width}}[min max] value) {
                return (u{{wide}}[0 max])value;
            }
            public unsafe noinline finite law i{{wide}}[min max] Reinterpret(u{{width}}[0 max] value) {
                return (i{{wide}}[min max])(i{{width}}[min max])value;
            }
            """;
        var llvm = Compile(source);
        Assert.Contains($"zext i{width}", Body(llvm, "Masked"));
        Assert.Contains($"sext i{width}", Body(llvm, "Signed"));
        Assert.Contains($"zext i{width}", Body(llvm, "Unsigned"));
        Assert.Contains($"sext i{width}", Body(llvm, "NegativeToUnsigned"));
        Assert.Contains($"sext i{width}", Body(llvm, "Reinterpret"));
    }

    [Fact]
    public void NarrowingAndJoinsDoNotReuseAnInapplicablePositiveFact()
    {
        var llvm = Compile("""
            module Widening
            public unsafe noinline finite law i64[min max] Narrow(u16[0 1088] value) {
                return (i64[min max])(i8[min max])value;
            }
            public noinline finite law i64[min max] Join(i8[min max] value, bool positive) {
                stack mut i8[min max] selected = value;
                if (positive) { selected = (i8[min max])(value & 63); }
                return (i64[min max])selected;
            }
            """);
        Assert.Contains("trunc i16", Body(llvm, "Narrow"));
        Assert.Contains("sext i8", Body(llvm, "Narrow"));
        Assert.Contains("sext i8", Body(llvm, "Join"));
    }

    private static string Compile(string source)
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source), new CompilerOptions(EmitLlvmIr: true));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        return result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
    }

    private static string Body(string llvm, string name)
    {
        var match = Regex.Match(llvm, @"^define [^\n]* @" + Regex.Escape(name) + @"\([^\n]*\n.*?^}",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, "Missing LLVM body for " + name);
        return match.Value;
    }
}
