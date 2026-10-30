using System.Numerics;
using System.Text;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.PipelineTests;

public sealed class ConsoleConstantEncodingTests
{
    private const string Declarations = """
        module System.Console
        public noinline fn i32[min max] WriteLine(ascii text);
        public noinline fn i32[min max] WriteLine(unicode text);
        public noinline fn i32[min max] WriteErrorLine(unicode text);
        internal unsafe noinline fn i32[min max] WritePreencodedUtf8(rawptr<i8[min max]> data, u64[0 2 ** 63 - 1] length, bool error);
        public struct BufferedUtf8Writer {
            i64[min max] Value;
            public noinline fn i32[min max] WriteLine(mut borrow BufferedUtf8Writer self, unicode text);
            internal unsafe noinline fn i32[min max] WritePreencodedUtf8(mut borrow BufferedUtf8Writer self, rawptr<i8[min max]> data, u64[0 2 ** 63 - 1] length);
        }
        """;

    [Theory]
    [InlineData("ascii", "")]
    [InlineData("ascii", "arbitrary café 🌍\0")]
    [InlineData("unicode", "")]
    [InlineData("unicode", "arbitrary café 🌍\0")]
    public void CarriesExactUtf8ThroughCopiedConstantText(string kind, string text)
    {
        var call = Assert.Single(Calls(Compile($$"""
            public noinline fn i32[min max] Run() {
                stack {{kind}} original = ({{kind}}){{JsonSerializer.Serialize(text)}};
                stack {{kind}} copy = original;
                return WriteLine(copy);
            }
            """), "Run"));
        Assert.Equal(3, call.Arguments.Count);
        Assert.Equal(Encoding.UTF8.GetBytes(text + "\n").Select(value => new BigInteger(unchecked((sbyte)value))),
            Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[0]).Elements);
        Assert.False(Assert.IsType<SsaBoolConstant>(call.Arguments[2]).Value);
    }

    [Theory]
    [InlineData("ascii", 8191, true)]
    [InlineData("ascii", 8192, false)]
    [InlineData("unicode", 4095, true)]
    [InlineData("unicode", 4096, false)]
    public void RetainsImmediateChunkBoundaries(string kind, int length, bool encoded)
    {
        var call = Assert.Single(Calls(Compile($$"""
            public noinline fn i32[min max] Run() { return WriteLine(({{kind}}){{JsonSerializer.Serialize(new string('x', length))}}); }
            """), "Run"));
        Assert.Equal(encoded ? 3 : 1, call.Arguments.Count);
    }

    [Fact]
    public void PreservesScalarSlicesAndErrorDestination()
    {
        var call = Assert.Single(Calls(Compile("""
            public noinline fn i32[min max] Run() {
                stack unicode text = (unicode)"A🌍Z";
                return WriteErrorLine(text[1,1]);
            }
            """), "Run"));
        Assert.Equal(Encoding.UTF8.GetBytes("🌍\n").Select(value => new BigInteger(unchecked((sbyte)value))),
            Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[0]).Elements);
        Assert.True(Assert.IsType<SsaBoolConstant>(call.Arguments[2]).Value);
    }

    [Fact]
    public void AsciiSlicesRetainBytesWithoutUnicodeDecoding()
    {
        var call = Assert.Single(Calls(Compile("""
            public noinline fn i32[min max] Run() {
                stack ascii text = "AéZ";
                return WriteLine(text[2,1]);
            }
            """), "Run"));
        Assert.Equal(new BigInteger[] { -87, 10 },
            Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[0]).Elements);
    }

    [Fact]
    public void PreencodesWhenTheCallerDiscardsTheStatus()
    {
        var call = Assert.Single(Calls(Compile("""
            public noinline fn void Run() { WriteLine((unicode)"unused result é🌍"); return; }
            """), "Run"));
        Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[0]);
    }

    [Fact]
    public void KeepsDynamicTextAndWindowsImmediateConsoleSemantics()
    {
        var result = Compile("""
            public noinline fn i32[min max] Dynamic(unicode text) { return WriteLine(text); }
            public noinline fn i32[min max] Run() { return WriteLine((unicode)"constant"); }
            """, windows: true);
        Assert.Single(Assert.Single(Calls(result, "Dynamic")).Arguments);
        Assert.Single(Assert.Single(Calls(result, "Run")).Arguments);
        Assert.Single(Assert.Single(Calls(Compile("""
            public noinline fn i32[min max] Run(unicode text) { return WriteLine(text); }
            """), "Run")).Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferedConstantEncodingPreservesTheReceiverOnEveryTarget(bool windows)
    {
        var call = Assert.Single(Calls(Compile("""
            public noinline fn i32[min max] Run(mut borrow BufferedUtf8Writer writer) {
                return writer.WriteLine((unicode)"é🌍");
            }
            """, windows), "Run"));
        Assert.Equal(3, call.Arguments.Count);
        Assert.IsType<SsaStaticArrayAddressValue>(call.Arguments[1]);
        Assert.Equal(7, Assert.IsType<SsaIntegerConstant>(call.Arguments[2]).Value);
    }

    private static CompilationResult Compile(string functions, bool windows = false)
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(Declarations + "\n" + functions),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa",
                TargetInfo: new LlvmTargetInfo(windows ? "x86_64-pc-windows-msvc" : "x86_64-unknown-linux-gnu",
                    "e-p:64:64-i64:64-n8:16:32:64-S128")));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        return result;
    }

    private static ISsaDirectCallOperation[] Calls(CompilationResult result, string name) =>
        result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions.Single(function => function.Name == name)
            .Blocks.SelectMany(block => block.Instructions)
            .Select(instruction => instruction is SsaValueInstruction value ? value.Value as ISsaDirectCallOperation : instruction as ISsaDirectCallOperation)
            .OfType<ISsaDirectCallOperation>().ToArray();
}
