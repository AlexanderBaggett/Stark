using System.Text.RegularExpressions;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class BorrowedAlignmentEmissionTests
{
    [Theory]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("i686-unknown-linux-gnu")]
    [InlineData("aarch64-unknown-linux-gnu")]
    public void KeyLoadsRetainTheReferentAbiAlignment(string triple)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "stdlib", "src"))) root = root.Parent;
        Assert.NotNull(root);
        var source = "import System.Collections\nmodule BorrowAlignment\n";
        foreach (var width in new[] { 8, 16, 24, 32, 64, 128 })
            source += $$"""
                export noinline finite law bool Equal{{width}}(borrow u{{width}}[0 max] a, borrow u{{width}}[0 max] b)
                    where overlap(a, b)
                {
                    return DictionaryKey.Equals(a, b);
                }

                """;
        source += """
            export noinline finite law bool EqualAlias(borrow u32[0 max] a, borrow u32[0 max] b)
                where overlap(a, b)
            {
                stack borrow u32[0 max] saved = a;
                return DictionaryKey.Equals(saved, b);
            }
            """;
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true, TargetInfo: new LlvmTargetInfo(triple, null),
                ModuleResolver: new FileSystemModuleResolver(Path.Combine(root.FullName, "stdlib", "src"))));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var text = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Single(LlvmModuleLint.Check("""
            define i1 @equal(ptr readonly dereferenceable(1) %key) {
              %abi_dict_key_arg_0_0 = load i8, ptr %key, align 8
              ret i1 true
            }
            """));
        Assert.Empty(LlvmModuleLint.Check(text));
        foreach (var width in new[] { 8, 16, 24, 32, 64, 128 })
        {
            var body = Regex.Match(text, @"define[^\n]*@Equal" + width + @"\([^\n]*\).*?\n}", RegexOptions.Singleline);
            Assert.True(body.Success);
            var header = body.Value.Split('\n')[0];
            var firstParameter = header[..header.IndexOf("%arg_a", StringComparison.Ordinal)];
            var contract = Regex.Match(firstParameter, @"\balign (\d+)");
            var expected = contract.Success ? int.Parse(contract.Groups[1].Value) : 1;
            var loads = Regex.Matches(body.Value, @"load i\d+, ptr %arg_[ab](?:, align (\d+))?");
            Assert.Equal(2, loads.Count);
            foreach (Match load in loads)
                Assert.Equal(expected, load.Groups[1].Success ? int.Parse(load.Groups[1].Value) : 1);
        }
        var alias = Regex.Match(text, @"define[^\n]*@EqualAlias\([^\n]*\).*?\n}", RegexOptions.Singleline);
        Assert.True(alias.Success);
        var aliasLoads = Regex.Matches(alias.Value, @"load i32, ptr [^,\n]+, align (\d+)");
        Assert.Equal(2, aliasLoads.Count);
        foreach (Match load in aliasLoads) Assert.Equal(4, int.Parse(load.Groups[1].Value));

    }

    [Fact]
    public void LintRejectsMaterializationStrongerThanThePublishedBorrowContract()
    {
        Assert.Single(LlvmModuleLint.Check("""
            define i1 @equal(ptr readonly dereferenceable(4) align 4 %key) {
              %abi_dict_key_arg_0_0 = load i32, ptr %key, align 8
              ret i1 true
            }
            """));
        Assert.Empty(LlvmModuleLint.Check("""
            define i1 @equal(ptr readonly dereferenceable(16) align 16 %key) {
              %abi_dict_key_arg_0_0 = load i128, ptr %key, align 16
              ret i1 true
            }
            """));
    }
}
