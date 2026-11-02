using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class IntegerReassociationRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReassociationPreservesDefinedCancellationThroughNativePackages(bool package)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-reassociation-");
        try
        {
            var library = Path.Combine(directory.FullName, "Kernel.stark");
            var app = Path.Combine(directory.FullName, "App.stark");
            var native = Path.Combine(directory.FullName, "values.c");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(library, """
                module Kernel
                public noinline finite law bool Positive(i64[min max] seed, i64[min max] value) {
                    stack i64[min max] first = seed + value;
                    stack i64[min max] second = first + value;
                    return second > 0;
                }
                public noinline finite law i64[min max] Prefix(i64[min max] seed, i64[min max] value, i8[-1 1] adjust) {
                    stack i64[min max] inner = value + (i64[min max])adjust;
                    stack i64[min max] first = seed + inner;
                    return first + (i64[min max])adjust;
                }
                public noinline finite law bool Computed(i64[min max] seed, i64[-4611686018427387903 0] value) {
                    stack i64[min max] partial = (i64[min max])(value + value);
                    stack i64[min max] first = seed + partial;
                    return first + partial < 0;
                }
                public noinline finite law i64[min max] Triple(i64[min max] value) {
                    return value + value + value;
                }
                """);
            await File.WriteAllTextAsync(app, """
                import Kernel
                module App
                unsafe ffi finite i64[min max] Value(u8[0 1] index);
                export unsafe ffi finite i32[min max] main() {
                    stack i64[min max] high = Value(0);
                    stack i64[min max] low = Value(1);
                    if (!Positive(-9223372036854775808, high)) { return 1; }
                    if (Positive(9223372036854775807, low)) { return 2; }
                    if (Prefix(high, 1, -1) != high - 1) { return 3; }
                    if (Prefix(-9223372036854775808, -1, 1) != -9223372036854775807) { return 4; }
                    if (Triple(123) != 369 || Triple(-123) != -369) { return 5; }
                    if (!Computed(high, -4611686018427387903)) { return 6; }
                    return 0;
                }
                """);
            await File.WriteAllTextAsync(native, """
                #include <stdint.h>
                static volatile int64_t values[2] = {INT64_MAX, -INT64_MAX};
                int64_t Value(uint8_t index) { return values[index]; }
                """);
            async Task Compile(params string[] arguments)
            {
                var output = new StringWriter();
                var errors = new StringWriter();
                var exit = await CompilerCli.RunAsync(arguments, new StringReader(""), output, errors);
                Assert.True(exit == 0, output + "\n" + errors);
            }
            if (package)
            {
                await Compile(library, "--emit-lib", "--target", target.Triple, "--no-stark-path", "-o",
                    Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Kernel.lib" : "libKernel.a"));
                File.Delete(library);
            }
            await Compile(app, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName,
                "--native-source", native, "-o", binary);
            using var process = Process.Start(new ProcessStartInfo(binary) {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stdout);
            Assert.Equal("", await stderr);
        }
        finally { directory.Delete(recursive: true); }
    }
}
