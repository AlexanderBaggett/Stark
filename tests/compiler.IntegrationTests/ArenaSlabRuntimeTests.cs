using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class ArenaSlabRuntimeTests
{
    [Fact]
    public async Task ArenaHelpersPreserveAlignmentAndFallibleOverflow()
    {
        if (!OperatingSystem.IsLinux() || !NativeToolchain.TryDetectDefaultTargetInfo(out _)) return;
        var clang = NativeToolchain.Resolve().Clang.Path;
        if (clang is null) return;

        const string stark = """
            module ArenaHelpers
            unsafe fn bool UseArena()
            {
                stack mut dynamic u64[0 max] values = new(arena, 1);
                return values.TryReserveCapacity(1024);
            }
            """;
        const string harness = """
            #include <stdint.h>
            #include <string.h>
            extern void __stark_arena_enter(void **);
            extern void __stark_arena_leave(void **);
            extern void *__stark_arena_alloc(void **, uint64_t, uint64_t);
            extern void *__stark_arena_try_alloc(void **, uint64_t, uint64_t);
            int main(void) {
                void *outer[3], *inner[3];
                __stark_arena_enter(outer);
                unsigned char *a = __stark_arena_alloc(outer, 4000, 64);
                if (((uintptr_t)a & 63) != 0) return 1;
                memset(a, 0x5a, 4000);
                unsigned char *b = __stark_arena_try_alloc(outer, 5000, 256);
                if (!b || ((uintptr_t)b & 255) != 0) return 2;
                memset(b, 0xa5, 5000);
                __stark_arena_enter(inner);
                unsigned char *c = __stark_arena_alloc(inner, 31, 4096);
                if (((uintptr_t)c & 4095) != 0) return 3;
                memset(c, 0x77, 31);
                __stark_arena_leave(inner);
                for (int i = 0; i < 4000; i++) if (a[i] != 0x5a) return 4;
                for (int i = 0; i < 5000; i++) if (b[i] != 0xa5) return 5;
                __stark_arena_leave(outer);
                __stark_arena_enter(outer);
                if (__stark_arena_try_alloc(outer, UINT64_MAX, 16)) return 6;
                __stark_arena_leave(outer);
                return 0;
            }
            """;
        var directory = Directory.CreateTempSubdirectory("stark-arena-helpers-");
        try
        {
            var input = Path.Combine(directory.FullName, "Arena.stark");
            var llvm = Path.Combine(directory.FullName, "Arena.ll");
            var c = Path.Combine(directory.FullName, "check.c");
            var output = Path.Combine(directory.FullName, "check");
            await File.WriteAllTextAsync(input, stark);
            await File.WriteAllTextAsync(c, harness);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var compile = await CompilerCli.RunAsync([input, "--emit-llvm", "-o", llvm], new StringReader(string.Empty), stdout, stderr);
            Assert.True(compile == 0, stdout + Environment.NewLine + stderr);
            await File.AppendAllTextAsync(llvm, "\n@llvm.used = appending global [4 x ptr] [ptr @__stark_arena_enter, ptr @__stark_arena_leave, ptr @__stark_arena_alloc, ptr @__stark_arena_try_alloc], section \"llvm.metadata\"\n");
            var link = await RunAsync(clang, ["-O3", llvm, c, "-o", output]);
            Assert.True(link.ExitCode == 0, link.Output);
            var run = await RunAsync(output, []);
            Assert.True(run.ExitCode == 0, $"Exit {run.ExitCode}: {run.Output}");
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ArenaFramesPreserveDataAcrossSlabsGrowthAndNestedCalls()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)) return;

        const string source = """
            module ArenaSlabTest

            unsafe fn bool Nested()
            {
                stack mut dynamic u64[0 max] values = new(arena, 512);
                init values[0] = 42;
                return values[0] == 42;
            }

            unsafe fn bool Check(u64[0 2 ** 63 - 1] capacity)
            {
                stack mut dynamic u64[0 max] values = new(arena, capacity);
                init values[0] = 17;
                if (!Nested() || values[0] != 17) { return false; }
                if (!values.TryReserveCapacity(1025) || values[0] != 17) { return false; }
                for willexit (stack mut u16[0 256] i = 1; i < 256; i += 1)
                {
                    init values[values.Length] = (u64[0 max])i;
                }
                for willexit (stack mut u16[0 256] i = 1; i < 256; i += 1)
                {
                    if (values[(u64[0 2 ** 63 - 1])i] != (u64[0 max])i) { return false; }
                }
                return values[0] == 17;
            }

            export unsafe ffi fn i32[min max] main()
            {
                for willexit (stack mut u8[0 40] round = 0; round < 40; round += 1)
                {
                    if (!Check(1) || !Check(509) || !Check(510) || !Check(511) || !Check(512) || !Check(513) || !Check(1025)) { return 1; }
                }
                return 0;
            }
            """;
        var directory = Directory.CreateTempSubdirectory("stark-arena-slab-");
        try
        {
            var input = Path.Combine(directory.FullName, "App.stark");
            var output = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(input, source);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync([input, "--emit-exe", "-o", output], new StringReader(string.Empty), stdout, stderr);
            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            using var process = Process.Start(new ProcessStartInfo(output)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            Assert.NotNull(process);
            var outputText = await process.StandardOutput.ReadToEndAsync();
            var errorText = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}: {outputText} {errorText}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string command, string[] arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + stderr);
    }
}
