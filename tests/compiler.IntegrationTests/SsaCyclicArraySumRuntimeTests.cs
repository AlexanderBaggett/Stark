using System.Diagnostics;
using System.Text;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class SsaCyclicArraySumRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArbitrarySignedInputsAndObservableFallbacksWorkAcrossSourceAndNativePackages(bool packaged)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-cyclic-sum-");
        try
        {
            var source = Path.Combine(directory.FullName, "Cycles.stark");
            var kernel = """
                module Cycles

                public noinline finite law i64[min max] Sum(i8[-11 13] seed) {
                    stack mut i64[min max][5] cells = {-7, -4, -1, 2, 5};
                    stack mut i64[min max] sum = -19;
                    for willexit(stack mut u8[0 23] i=0; i<23; i+=1) {
                        stack u8[0 4] j = (u8[0 4])(i % 5);
                        cells[j] = cells[j] + (i64[min max])j + (i64[min max])seed;
                        sum += cells[j];
                    }
                    return sum;
                }

                unsafe noinline finite law u8[0 max] Byte(rawptr<i64[min max]>[1] value) {
                    return *((rawptr<u8[0 max]>)value);
                }

                public unsafe noinline finite law i64[min max] Observed(i8[-11 13] seed) {
                    stack mut i64[min max][5] cells = {-7, -4, -1, 2, 5};
                    stack mut i64[min max] sum = -19;
                    for willexit(stack mut u8[0 23] i=0; i<23; i+=1) {
                        stack u8[0 4] j = (u8[0 4])(i % 5);
                        cells[j] = cells[j] + (i64[min max])j + (i64[min max])seed;
                        sum += cells[j];
                    }
                    return sum + cells[0] + (i64[min max])Byte(&cells[1]);
                }

                unsafe noinline finite void Mutate(rawmutptr<i64[min max]>[1] value) { *value += 3; }

                public unsafe noinline finite i64[min max] Mutated(i8[-11 13] seed) {
                    stack mut i64[min max][5] cells = {-7, -4, -1, 2, 5};
                    stack mut i64[min max] sum = -19;
                    for willexit(stack mut u8[0 23] i=0; i<23; i+=1) {
                        stack u8[0 4] j = (u8[0 4])(i % 5);
                        cells[j] = cells[j] + (i64[min max])j + (i64[min max])seed;
                        Mutate(&cells[0]);
                        sum += cells[j];
                    }
                    return sum;
                }

                public noinline finite law i64[min max] Dynamic(i8[-11 13] seed, u8[0 25] count) {
                    stack mut i64[min max][5] cells = {-7, -4, -1, 2, 5};
                    stack mut i64[min max] sum = -19;
                    for willexit(stack mut u8[0 25] i=0; i<count; i+=1) {
                        stack u8[0 4] j = (u8[0 4])(i % 5);
                        cells[j] = cells[j] + (i64[min max])j + (i64[min max])seed;
                        sum += cells[j];
                    }
                    return sum;
                }
                """;
            await File.WriteAllTextAsync(source, kernel);
            // Inspect the consuming pipeline as well as executing linked native code.
            var analyzed = DefaultCompilerPipeline.Create().Run(new CompilationInput(kernel, source),
                new CompilerOptions(EmitLlvmIr: true, TargetInfo: target));
            Assert.True(analyzed.Succeeded, string.Join("\n", analyzed.Diagnostics));
            var ssa = analyzed.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var sum = Assert.Single(ssa.Functions, f => f.Name == "Sum");
            Assert.True(!sum.Blocks.Any(b => b.Phis.Count != 0), "Expected the private constant-cycle sum to fold.");
            foreach (var name in new[] { "Observed", "Mutated", "Dynamic" })
                Assert.Contains(Assert.Single(ssa.Functions, f => f.Name == name).Blocks, b => b.Phis.Count != 0);

            if (packaged)
            {
                var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Cycles.lib" : "libCycles.a");
                await Compile([source, "--emit-lib", "--target", target.Triple, "-o", library]);
                File.Delete(source);
                Assert.Empty(Directory.GetFiles(directory.FullName, "*.stark", SearchOption.AllDirectories));
            }
            var app = new StringBuilder("import Cycles\nmodule App\nexport unsafe ffi finite i32[min max] main() {\n");
            for (var seed = -11; seed <= 13; seed++)
            {
                app.AppendLine($"if (Sum({seed}) != {Reference(seed, 23, false, false)}) {{ return 1; }}");
                app.AppendLine($"if (Observed({seed}) != {Reference(seed, 23, false, true)}) {{ return 2; }}");
                app.AppendLine($"if (Mutated({seed}) != {Reference(seed, 23, true, false)}) {{ return 3; }}");
                foreach (var count in new[] { 0, 1, 17, 25 })
                    app.AppendLine($"if (Dynamic({seed}, {count}) != {Reference(seed, count, false, false)}) {{ return 4; }}");
            }
            app.AppendLine("return 0; }");
            var appPath = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(appPath, app.ToString());
            await Compile([appPath, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-o", binary]);
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

    private static long Reference(int seed, int count, bool mutate, bool observe)
    {
        long[] cells = [-7, -4, -1, 2, 5];
        long sum = -19;
        for (var i = 0; i < count; i++)
        {
            var j = i % 5;
            cells[j] += j + seed;
            if (mutate) cells[0] += 3;
            sum += cells[j];
        }
        return observe ? sum + cells[0] + BitConverter.GetBytes(cells[1])[0] : sum;
    }

    private static async Task Compile(string[] arguments)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var result = await CompilerCli.RunAsync(arguments, new StringReader(""), output, errors);
        Assert.True(result == 0, output + "\n" + errors);
    }
}
