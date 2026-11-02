using System.Diagnostics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class SsaLoopCounterWideningRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCounterKeepsNarrowCallsMemoryAndMutationSemantics(bool mutateCounter)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-counter-width-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(source, mutateCounter ? """
                module CounterMutation
                unsafe noinline finite void Visit(rawmutptr<u24[0 100000]>[1] value) {
                    if (*value == 17) { *value = 18; }
                }
                export unsafe ffi fn i32[min max] main() {
                    stack mut i64[min max] sum = 0;
                    for willexit(stack mut u24[0 100000] i=0; i<100000; i+=1) {
                        sum += (i64[min max])i;
                        Visit(&i);
                    }
                    if (sum != 4999949982) { return 1; }
                    return 0;
                }
                """ : """
                module CounterDataUses
                noinline finite law u32[0 max] Echo(u24[0 100000] value) { return (u32[0 max])value; }
                export unsafe ffi fn i32[min max] main() {
                    stack mut i8[min max][8] bytes = {0};
                    stack mut i64[min max] sum = 0;
                    for willexit(stack mut u24[0 100000] i=0; i<100000; i+=1) {
                        stack u32[0 max] value = Echo(i);
                        if (value != (u32[0 max])i) { return 1; }
                        bytes[(u8[0 7])(i % 8)] = (i8[min max])(i % 128);
                        if (i % 2 == 0) { sum += (i64[min max])value * 2; }
                        else { sum += (i64[min max])value; }
                    }
                    if (sum != 7499900000 || bytes[7] != 31) { return 2; }
                    return 0;
                }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var exit = await CompilerCli.RunAsync([source, "--emit-exe", "--target", target.Triple, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            using var process = Process.Start(new ProcessStartInfo(binary) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            Assert.NotNull(process);
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
