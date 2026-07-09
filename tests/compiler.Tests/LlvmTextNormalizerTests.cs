using Stark.Compiler;

namespace compiler.Tests;

public sealed class LlvmTextNormalizerTests
{
    [Fact]
    public void EquatesRegisterAndLabelRenamings()
    {
        var left = LlvmTextNormalizer.ExtractNormalizedFunctions(
            """
            define i32 @main() {
            b1:
              %v1 = add i32 0, 99
              br label %b2
            b2:
              ret i32 %v1
            }
            """);

        var right = LlvmTextNormalizer.ExtractNormalizedFunctions(
            """
            define i32 @main() {
            entry:
              %sum = add i32 0, 99
              br label %exit
            exit:
              ret i32 %sum
            }
            """);

        Assert.Equal(left["main"], right["main"]);
    }

    [Fact]
    public void EquatesAttributedAndBareEmissions()
    {
        // Stage0 emits full attributes, metadata, and debug info; stage1 emits
        // the bare skeleton. Both must normalize to the same text.
        var stage0 = LlvmTextNormalizer.ExtractNormalizedFunctions(
            """
            define fastcc noundef i32 @main() nounwind memory(readwrite, argmem: none) inlinehint !dbg !8 {
            b1:
              %v1 = add i32 0, 99, !dbg !12
              ret i32 %v1, !dbg !13
            }
            """);

        var stage1 = LlvmTextNormalizer.ExtractNormalizedFunctions(
            """
            define i32 @main() {
            b1:
              %v1 = add i32 0, 99
              ret i32 %v1
            }
            """);

        Assert.Equal(stage0["main"], stage1["main"]);
    }

    [Fact]
    public void FlagsConstantDifferenceAndMissingFunctions()
    {
        var report = LlvmTextNormalizer.DiffModules(
            """
            define i32 @main() {
            b1:
              ret i32 7
            }
            define i32 @only_expected() {
            b1:
              ret i32 0
            }
            """,
            """
            define i32 @main() {
            b1:
              ret i32 8
            }
            """);

        Assert.Contains("@main: skeletons differ", report, StringComparison.Ordinal);
        Assert.Contains("@only_expected: only in stage0", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsEquivalentModulesAsEmpty()
    {
        var module =
            """
            define { i8 } @pick() {
            b1:
              %v1 = insertvalue { i8 } zeroinitializer, i8 0, 0
              ret { i8 } %v1
            }
            """;

        Assert.Equal(string.Empty, LlvmTextNormalizer.DiffModules(module, module));
    }
}
