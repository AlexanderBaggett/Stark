using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class LinuxReallocateRuntimeTests
{
    private const string Source = """
        module HeapHelpers
        unsafe fn bool Grow()
        {
            stack mut dynamic u8[0 max] values = new(8192);
            return values.TryReserveCapacity(32768);
        }
        """;

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("aarch64-unknown-linux-gnu")]
    [InlineData("riscv64-unknown-linux-gnu")]
    [InlineData("i686-unknown-linux-gnu")]
    [InlineData("arm-unknown-linux-gnueabihf")]
    public async Task LinuxOsReallocationCrossCompiles(string target)
    {
        var clang = NativeToolchain.Resolve().Clang.Path;
        var extract = NativeToolchain.FindExecutable("llvm-extract");
        if (clang is null || extract is null) return;
        var directory = Directory.CreateTempSubdirectory("stark-reallocate-target-");
        try
        {
            var llvm = await EmitAsync(directory.FullName, target);
            // Verify the complete emitted module, then compile the OS boundary
            // independently of unrelated runtime trap calling conventions.
            var helper = Path.Combine(directory.FullName, "reallocate.bc");
            var extracted = await RunAsync(extract, ["--func=__stark_os_reallocate", llvm, "-o", helper]);
            Assert.True(extracted.ExitCode == 0, extracted.Output);
            var compile = await RunAsync(clang, ["--target=" + target, "-O3", "-c", helper, "-o", Path.Combine(directory.FullName, "heap.o")]);
            Assert.True(compile.ExitCode == 0, compile.Output);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task LinuxReallocationPreservesContentsAlignmentAndFailedGrowth()
    {
        if (!OperatingSystem.IsLinux() || !NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var clang = NativeToolchain.Resolve().Clang.Path;
        if (clang is null) return;
        var directory = Directory.CreateTempSubdirectory("stark-reallocate-runtime-");
        try
        {
            var llvm = await EmitAsync(directory.FullName, target.Triple);
            // Keep helpers independently callable by the byte-observing C translation unit.
            var sizeType = IntPtr.Size == 8 ? "i64" : "i32";
            await File.AppendAllTextAsync(llvm, $$"""

                @llvm.used = appending global [4 x ptr] [ptr @__stark_runtime_try_alloc, ptr @__stark_runtime_try_realloc, ptr @__stark_runtime_realloc, ptr @__stark_runtime_free], section "llvm.metadata"
                define ptr @map_allocate({{sizeType}} %size) {
                  %p = call ptr @__stark_os_allocate({{sizeType}} %size)
                  ret ptr %p
                }
                define ptr @map_reallocate(ptr %p, {{sizeType}} %old, {{sizeType}} %new) {
                  %r = call ptr @__stark_os_reallocate(ptr %p, {{sizeType}} %old, {{sizeType}} %new)
                  ret ptr %r
                }
                define void @map_free(ptr %p, {{sizeType}} %size) {
                  call void @__stark_os_free(ptr %p, {{sizeType}} %size)
                  ret void
                }
                """);
            var c = Path.Combine(directory.FullName, "check.c");
            await File.WriteAllTextAsync(c, """
                #define _GNU_SOURCE
                #include <stdint.h>
                #include <stdio.h>
                #include <string.h>
                #include <errno.h>
                #include <sys/mman.h>
                #include <sys/resource.h>
                #include <unistd.h>
                extern void *__stark_runtime_try_alloc(size_t, size_t);
                extern void *__stark_runtime_try_realloc(void *, size_t, size_t, size_t);
                extern void *__stark_runtime_realloc(void *, size_t, size_t, size_t);
                extern void __stark_runtime_free(void *);
                extern void *map_allocate(size_t);
                extern void *map_reallocate(void *, size_t, size_t);
                extern void map_free(void *, size_t);
                static unsigned char pattern(size_t i) { return (unsigned char)(i * 37 + i / 251); }
                static int valid(unsigned char *p, size_t n, size_t align) {
                    if (!p || (uintptr_t)p % align) return 0;
                    for (size_t i = 0; i < n; ++i) if (p[i] != pattern(i)) return 0;
                    return 1;
                }
                int main(void) {
                    const size_t alignments[] = {sizeof(void *), 64, 128};
                    for (size_t a = 0; a < sizeof(alignments)/sizeof(*alignments); ++a) {
                        size_t align = alignments[a];
                        unsigned char *p = __stark_runtime_try_alloc(8192, align);
                        if (!p) return 1;
                        for (size_t i = 0; i < 8192; ++i) p[i] = pattern(i);
                        unsigned char *grown = __stark_runtime_try_realloc(p, 8192, 65536, align);
                        if (!valid(grown, 8192, align)) return 2;
                        for (size_t i = 8192; i < 65536; ++i) grown[i] = pattern(i);
                        if (__stark_runtime_try_realloc(grown, 65536, SIZE_MAX, align)) return 3;
                        if (!valid(grown, 65536, align)) return 4;
                        p = __stark_runtime_try_realloc(grown, 65536, 4096, align);
                        if (!valid(p, 4096, align)) return 5;
                        grown = __stark_runtime_try_realloc(p, 4096, 0, align);
                        if (!grown || (uintptr_t)grown % align) return 6;
                        __stark_runtime_free(grown);
                    }
                    // Null-old reallocation is fresh allocation, including zero size.
                    // It ignores old_size and must preserve alignment and disjointness.
                    for (size_t a = 0; a < sizeof(alignments)/sizeof(*alignments); ++a) {
                        size_t align = alignments[a];
                        unsigned char *first = __stark_runtime_try_realloc(NULL, SIZE_MAX, 17, align);
                        unsigned char *second = __stark_runtime_realloc(NULL, 123, 17, align);
                        if (!first || !second || first == second || (uintptr_t)first % align || (uintptr_t)second % align) return 20;
                        memset(first, 31, 17);
                        memset(second, 47, 17);
                        for (size_t i = 0; i < 17; ++i) if (first[i] != 31 || second[i] != 47) return 21;
                        __stark_runtime_free(first);
                        __stark_runtime_free(second);
                        first = __stark_runtime_try_realloc(NULL, SIZE_MAX, 0, align);
                        if (!first || (uintptr_t)first % align) return 22;
                        __stark_runtime_free(first);
                    }
                    if (__stark_runtime_try_realloc(NULL, 0, 32, 24)) return 23;
                    if (__stark_runtime_try_realloc(NULL, 0, SIZE_MAX, 8)) return 24;

                    // A bucket allocation must retain its own reuse/fallback path.
                    unsigned char *p = __stark_runtime_try_alloc(32, 8);
                    if (!p) return 7;
                    memset(p, 91, 32);
                    unsigned char *grown = __stark_runtime_try_realloc(p, 32, 8192, 8);
                    if (!grown) return 8;
                    for (size_t i = 0; i < 32; ++i) if (grown[i] != 91) return 9;
                    __stark_runtime_free(grown);

                    // Block adjacent pages so successful growth must move the mapping.
                    size_t page = (size_t)sysconf(_SC_PAGESIZE);
                    p = map_allocate(2 * page);
                    if (!p) return 10;
                    memset(p, 57, 2 * page);
                    void *block = MAP_FAILED;
                    int must_move = 0;
                #ifdef MAP_FIXED_NOREPLACE
                    block = mmap(p + 2 * page, 2 * page, PROT_NONE,
                                 MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE, -1, 0);
                    must_move = block == p + 2 * page || (block == MAP_FAILED && errno == EEXIST);
                #endif
                    grown = map_reallocate(p, 2 * page, 4 * page);
                    if (!grown || (must_move && grown == p)) return 11;
                    for (size_t i = 0; i < 2 * page; ++i) if (grown[i] != 57) return 12;
                    map_free(grown, 4 * page);
                    if (block != MAP_FAILED) munmap(block, 2 * page);

                    // Kernel rejection and failed fallback must both leave the owner live.
                    p = __stark_runtime_try_alloc(8192, sizeof(void *));
                    if (!p) return 13;
                    for (size_t i = 0; i < 8192; ++i) p[i] = pattern(i);
                    FILE *statm = fopen("/proc/self/statm", "r");
                    unsigned long pages;
                    if (!statm || fscanf(statm, "%lu", &pages) != 1) return 14;
                    fclose(statm);
                    struct rlimit saved, limited;
                    if (getrlimit(RLIMIT_AS, &saved)) return 15;
                    limited = saved;
                    limited.rlim_cur = (rlim_t)pages * page + 2 * page;
                    if (limited.rlim_cur > saved.rlim_cur) limited.rlim_cur = saved.rlim_cur;
                    if (setrlimit(RLIMIT_AS, &limited)) return 16;
                    grown = __stark_runtime_try_realloc(p, 8192, 64 * 1024 * 1024, sizeof(void *));
                    if (setrlimit(RLIMIT_AS, &saved)) return 17;
                    if (grown || !valid(p, 8192, sizeof(void *))) return 18;
                    grown = __stark_runtime_realloc(p, 8192, 65536, sizeof(void *));
                    if (!valid(grown, 8192, sizeof(void *))) return 19;
                    __stark_runtime_free(grown);
                    return 0;
                }
                """);
            var output = Path.Combine(directory.FullName, "check");
            var link = await RunAsync(clang, ["-O3", llvm, c, "-o", output]);
            Assert.True(link.ExitCode == 0, link.Output);
            var run = await RunAsync(output, []);
            Assert.True(run.ExitCode == 0, $"Exit {run.ExitCode}: {run.Output}");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<string> EmitAsync(string directory, string target)
    {
        var input = Path.Combine(directory, "Heap.stark");
        var llvm = Path.Combine(directory, "Heap.ll");
        await File.WriteAllTextAsync(input, Source);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var compile = await CompilerCli.RunAsync([input, "--target", target, "--emit-llvm", "-o", llvm], new StringReader(string.Empty), stdout, stderr);
        Assert.True(compile == 0, stdout + Environment.NewLine + stderr);
        return llvm;
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
