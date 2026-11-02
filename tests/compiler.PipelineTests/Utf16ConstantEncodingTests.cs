using System.Numerics;
using System.Text;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class Utf16ConstantEncodingTests
{
    private const string Declarations = """
        module System.IO.File
        public struct File
        {
            i64[min max] Value;
            public noinline fn i64[min max] WriteLine(mut borrow File self, ascii text);
            public noinline fn i64[min max] WriteLine(mut borrow File self, unicode text);
            public noinline fn i64[min max] WriteText(mut borrow File self, ascii text);
            public noinline fn i64[min max] WriteText(mut borrow File self, unicode text);
            internal unsafe noinline fn i64[min max] WriteLinePreencoded(mut borrow File self, ascii text, rawptr<i16[min max]> data, i64[min max] units, rawptr<i64[min max]> chunks, i64[min max] count, bool combine);
            internal unsafe noinline fn i64[min max] WriteLinePreencoded(mut borrow File self, unicode text, rawptr<i16[min max]> data, i64[min max] units, rawptr<i64[min max]> chunks, i64[min max] count, bool combine);
            internal unsafe noinline fn i64[min max] WriteTextPreencoded(mut borrow File self, ascii text, rawptr<i16[min max]> data, i64[min max] units, rawptr<i64[min max]> chunks, i64[min max] count, bool combine);
            internal unsafe noinline fn i64[min max] WriteTextPreencoded(mut borrow File self, unicode text, rawptr<i16[min max]> data, i64[min max] units, rawptr<i64[min max]> chunks, i64[min max] count, bool combine);
        }
        """;

    [Theory]
    [InlineData("ascii", "unrelated text", true)]
    [InlineData("ascii", "café 🌍\0", false)]
    [InlineData("unicode", "unrelated text", true)]
    [InlineData("unicode", "café 🌍\0", false)]
    [InlineData("ascii", "", false)]
    [InlineData("unicode", "", false)]
    public void PreencodesExactUtf16UnitsThroughLocalCopies(string kind, string text, bool combine)
    {
        var result = Compile($$"""
            public noinline fn i64[min max] Run(mut borrow File writer)
            {
                stack {{kind}} original = ({{kind}}){{JsonSerializer.Serialize(text)}};
                stack {{kind}} copy = original;
                return writer.WriteLine(copy);
            }
            """);
        var call = Assert.Single(Calls(result, "Run"));
        Assert.Equal(7, call.Arguments.Count);
        var data = Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[2]);
        Assert.Equal((text + "\n").Select(unit => new BigInteger(unchecked((short)unit))), data.Elements);
        Assert.Equal(text.Length, Assert.IsType<SsaIntegerConstant>(call.Arguments[3]).Value);
        Assert.Equal(combine, Assert.IsType<SsaBoolConstant>(call.Arguments[6]).Value);
    }

    [Theory]
    [InlineData("ascii", 2049, 2048, 1)]
    [InlineData("unicode", 1025, 1024, 1)]
    public void PreservesRuntimeChunkBoundaries(string kind, int length, int first, int second)
    {
        var text = new string(kind == "ascii" ? 'a' : 'Ω', length);
        var result = Compile($$"""
            public noinline fn i64[min max] Run(mut borrow File writer)
            {
                stack {{kind}} text = ({{kind}}){{JsonSerializer.Serialize(text)}};
                return writer.WriteText(text);
            }
            """);
        var call = Assert.Single(Calls(result, "Run"));
        Assert.Equal(7, call.Arguments.Count);
        Assert.Equal(new BigInteger[] { first, second }, Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[4]).Elements);
        Assert.False(Assert.IsType<SsaBoolConstant>(call.Arguments[6]).Value);
    }

    [Fact]
    public void UsesScalarIndicesForSupplementaryUnicodeSlicesAndByteIndicesForUtf8Slices()
    {
        var result = Compile("""
            public noinline fn i64[min max] Run(mut borrow File writer)
            {
                stack unicode u = (unicode)"A🌍Z";
                stack ascii a = "AéZ";
                writer.WriteLine(u[1, 1]);
                return writer.WriteLine(a[2, 1]);
            }
            """);
        var calls = Calls(result, "Run");
        Assert.Equal(2, calls.Length);
        Assert.Equal(new BigInteger[] { unchecked((short)0xd83c), unchecked((short)0xdf0d), 10 },
            Assert.IsType<SsaStaticArrayAddressValue>(calls[0].Arguments[2]).Elements);
        Assert.Equal(new BigInteger[] { unchecked((short)0xfffd), 10 },
            Assert.IsType<SsaStaticArrayAddressValue>(calls[1].Arguments[2]).Elements);
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", "e-p:64:64-i64:64-n8:16:32:64-S128")]
    [InlineData("x86_64-pc-windows-msvc", "e-p:64:64-i64:64-n8:16:32:64-S128")]
    [InlineData("powerpc64-unknown-linux-gnu", "E-p:64:64-i64:64-n32:64-S128")]
    public void EmitsTargetTypedReadOnlyUnits(string triple, string layout)
    {
        var result = Compile("""
            public noinline fn i64[min max] Run(mut borrow File writer)
            {
                return writer.WriteLine((unicode)"é🌍");
            }
            """, new LlvmTargetInfo(triple, layout), emitLlvm: true);
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("constant [4 x i16] [i16 233, i16 -10180, i16 -8435, i16 10]", llvm);
        Assert.DoesNotContain("alloca [", llvm);
    }

    [Fact]
    public void PreencodesWritesThroughAnOwnedLocalReceiver()
    {
        var result = Compile("""
            public noinline fn i64[min max] Run()
            {
                stack mut File writer = new File() { Value = 0 };
                return writer.WriteLine("local receiver");
            }
            """);
        Assert.Equal(7, Assert.Single(Calls(result, "Run")).Arguments.Count);
    }

    [Fact]
    public void RetainsRuntimeCallsForDynamicAndMutatedText()
    {
        var result = Compile("""
            public noinline fn i64[min max] Dynamic(mut borrow File writer, ascii text)
            {
                return writer.WriteLine(text);
            }
            public unsafe noinline fn i64[min max] Mutable(mut borrow File writer, i8[min max] value)
            {
                stack mut i8[min max][2] bytes = { 65, 66 };
                bytes[0] = value;
                stack Ascii text = new Ascii() { Data = &bytes[0], Length = 2, Capacity = 2 };
                return writer.WriteLine(System.Text.AsciiView(text));
            }
            """, includeText: true);
        Assert.Equal(2, Assert.Single(Calls(result, "Dynamic")).Arguments.Count);
        Assert.DoesNotContain(Calls(result, "Mutable"), call => call.Arguments.Any(arg => arg is SsaStaticArrayAddressValue));
    }

    [Theory]
    [InlineData("Ascii", "ascii", true)]
    [InlineData("Unicode", "unicode", false)]
    public void PreencodesBoundedConvertersWithoutChangingCapacityOrResultPointers(string suffix, string kind, bool prefix)
    {
        var source = $$"""
            module System.Text
            public unsafe noinline fn bool TryConvert{{suffix}}ToUtf16(rawmutptr<i16[min max]> destination, u64[0 2 ** 63 - 1] capacity, {{kind}} source, rawmutptr<i64[min max]> written);
            internal unsafe noinline finite bool TryCopyPreencodedUtf16(rawmutptr<i16[min max]> destination, u64[0 2 ** 63 - 1] capacity, rawptr<i16[min max]> data, i64[min max] units, rawmutptr<i64[min max]> written, bool prefix);
            public unsafe noinline fn bool Run(rawmutptr<i16[min max]> destination, u64[0 2 ** 63 - 1] capacity, rawmutptr<i64[min max]> written)
            {
                return TryConvert{{suffix}}ToUtf16(destination, capacity, ({{kind}})"Aé🌍Z", written);
            }
            """;
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var call = Assert.Single(Calls(result, "Run"));
        Assert.Equal(6, call.Arguments.Count);
        Assert.Equal(new BigInteger[] { 65, 233, unchecked((short)0xd83c), unchecked((short)0xdf0d), 90 },
            Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[2]).Elements);
        Assert.Equal(5, Assert.IsType<SsaIntegerConstant>(call.Arguments[3]).Value);
        Assert.Equal(prefix, Assert.IsType<SsaBoolConstant>(call.Arguments[5]).Value);
    }

    private static CompilationResult Compile(string functions, LlvmTargetInfo? target = null, bool emitLlvm = false, bool includeText = false)
    {
        var source = Declarations + "\n" + functions;
        if (includeText)
        {
            // Exercise a runtime view against the real text builtin declarations.
            source = "import System.Text\n" + source;
        }
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source), new CompilerOptions(
            StopAfterPassId: emitLlvm ? null : "specialize-const-stdlib-helpers-ssa",
            EmitLlvmIr: emitLlvm, TargetInfo: target,
            ModuleResolver: includeText ? new FileSystemModuleResolver(StandardLibrarySourceRoot()) : null));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        return result;
    }

    private static ISsaDirectCallOperation[] Calls(CompilationResult result, string name) =>
        result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions.Single(function => function.Name == name)
            .Blocks.SelectMany(block => block.Instructions)
            .Select(instruction => instruction is SsaValueInstruction value ? value.Value as ISsaDirectCallOperation : instruction as ISsaDirectCallOperation)
            .OfType<ISsaDirectCallOperation>().ToArray();

    private static string StandardLibrarySourceRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !Directory.Exists(Path.Combine(path.FullName, "stdlib", "src"))) path = path.Parent;
        return Path.Combine(path!.FullName, "stdlib", "src");
    }
}
