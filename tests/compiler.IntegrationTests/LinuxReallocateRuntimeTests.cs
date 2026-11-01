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
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("aarch64-pc-windows-msvc")]
    [InlineData("i686-pc-windows-msvc")]
    [InlineData("x86_64-apple-darwin")]
    [InlineData("aarch64-apple-darwin")]
    public async Task OsAndBucketReallocationCrossCompile(string target)
    {
        var clang = NativeToolchain.Resolve().Clang.Path;
        var extract = NativeToolchain.FindExecutable("llvm-extract");
        if (clang is null || extract is null) return;
        var directory = Directory.CreateTempSubdirectory("stark-reallocate-target-");
        try
        {
            var llvm = await EmitAsync(directory.FullName, target);
            // Compile the changed runtime consumer and its OS/proof helpers
            // independently of unrelated runtime trap calling conventions.
            var helper = Path.Combine(directory.FullName, "reallocate.bc");
            var extracted = await RunAsync(extract, ["--func=__stark_os_reallocate", llvm, "-o", helper]);
            Assert.True(extracted.ExitCode == 0, extracted.Output);
            var compile = await RunAsync(clang, ["--target=" + target, "-O3", "-c", helper, "-o", Path.Combine(directory.FullName, "heap.o")]);
            Assert.True(compile.ExitCode == 0, compile.Output);
            foreach (var name in new[] { "__stark_runtime_try_expand_bucket", "__stark_runtime_try_realloc", "__stark_runtime_try_alloc" })
            {
                var bucket = Path.Combine(directory.FullName, name + ".bc");
                extracted = await RunAsync(extract, ["--func=" + name, llvm, "-o", bucket]);
                Assert.True(extracted.ExitCode == 0, extracted.Output);
                compile = await RunAsync(clang, ["--target=" + target, "-O3", "-c", bucket, "-o", Path.Combine(directory.FullName, name + ".o")]);
                Assert.True(compile.ExitCode == 0, compile.Output);
            }
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

    [Fact]
    public async Task CachedSlabGrowthPreservesLiveSiblingsBytesAlignmentAndFailure()
    {
        if (!OperatingSystem.IsLinux() || !NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var clang = NativeToolchain.Resolve().Clang.Path;
        if (clang is null) return;
        var directory = Directory.CreateTempSubdirectory("stark-bucket-growth-");
        try
        {
            var llvm = await EmitAsync(directory.FullName, target.Triple);
            await File.AppendAllTextAsync(llvm, """

                @llvm.used = appending global [4 x ptr] [ptr @__stark_runtime_try_alloc, ptr @__stark_runtime_try_realloc, ptr @__stark_runtime_realloc, ptr @__stark_runtime_free], section "llvm.metadata"
                """);
            var c = Path.Combine(directory.FullName, "check.c");
            await File.WriteAllTextAsync(c, """
                #define _GNU_SOURCE
                #include <stdint.h>
                #include <stddef.h>
                #include <stdlib.h>
                #include <string.h>
                #include <pthread.h>
                #include <sys/resource.h>
                #include <sys/mman.h>
                #include <unistd.h>
                #include <errno.h>
                extern void *__stark_runtime_try_alloc(size_t, size_t);
                extern void *__stark_runtime_try_realloc(void *, size_t, size_t, size_t);
                extern void *__stark_runtime_realloc(void *, size_t, size_t, size_t);
                extern void __stark_runtime_free(void *);
                // Read the existing internal header only to place a blocking
                // mapping after its actual extent and test its proof boundary.
                typedef struct { void *base; size_t extent; size_t physical; } RuntimeHeader;
                static RuntimeHeader *header(void *p) { return (RuntimeHeader*)p-1; }
                static int bytes(const unsigned char *p, size_t n, unsigned char value) {
                    if (!p) return 0;
                    for (size_t i=0; i<n; ++i) if (p[i]!=value) return 0;
                    return 1;
                }
                static void *free_on_thread(void *p) { __stark_runtime_free(p); return NULL; }
                static unsigned char pattern(size_t i) { return (unsigned char)(i*37+i/251); }
                static void fill(unsigned char *p, size_t n) {
                    for (size_t i=0;i<n;++i) p[i]=pattern(i);
                }
                static int patterned(const unsigned char *p, size_t n) {
                    if (!p) return 0;
                    for (size_t i=0;i<n;++i) if (p[i]!=pattern(i)) return 0;
                    return 1;
                }
                static void *cache_and_grow_on_thread(void *p) {
                    __stark_runtime_free(p);
                    unsigned char *small=__stark_runtime_try_alloc(16,sizeof(void*));
                    if (!small) return (void*)(uintptr_t)1;
                    fill(small,16);
                    unsigned char *grown=__stark_runtime_try_realloc(small,16,8192,sizeof(void*));
                    if (!patterned(grown,16)) return (void*)(uintptr_t)2;
                    fill(grown,8192); __stark_runtime_free(grown); return NULL;
                }
                static void label(unsigned char *p, uint32_t id) {
                    memset(p,(unsigned char)(id+31),16); memcpy(p,&id,sizeof(id));
                }
                static int labeled(unsigned char *p, uint32_t id) {
                    uint32_t actual; if (!p) return 0; memcpy(&actual,p,sizeof(actual));
                    return actual==id && bytes(p+sizeof(id),16-sizeof(id),(unsigned char)(id+31));
                }
                static int lazy_tail(int mode) {
                    unsigned char *slots[4096];
                    slots[0]=__stark_runtime_try_alloc(16,8); if (!slots[0]) return 46;
                    label(slots[0],0);
                    if (mode==38) {
                        // The first header remains allocator metadata even
                        // after its logical block is freed on another thread.
                        pthread_t worker; void *result;
                        if (pthread_create(&worker,NULL,cache_and_grow_on_thread,slots[0])
                            || pthread_join(worker,&result) || result) return 47;
                        for (uint32_t i=1;i<2048;++i) {
                            slots[i]=__stark_runtime_try_alloc(16,8);
                            if (!slots[i]) return 48; label(slots[i],i);
                        }
                        for (uint32_t i=1;i<2048;++i) {
                            if (!labeled(slots[i],i)) return 49;
                            __stark_runtime_free(slots[i]);
                        }
                        return 0;
                    }
                    void *base=header(slots[0])->base;
                    uint32_t count=1;
                    for (;count<4096;++count) {
                        slots[count]=__stark_runtime_try_alloc(16,8);
                        if (!slots[count]) return 50; label(slots[count],count);
                        if (mode!=39 && header(slots[count])->base!=base) break;
                    }
                    if (mode==39) {
                        unsigned char *grown=__stark_runtime_try_realloc(slots[0],16,8192,8);
                        if (!labeled(grown,0)) return 51; fill(grown,8192);
                        for (uint32_t i=1;i<count;++i) {
                            if (!labeled(slots[i],i) || !patterned(grown,8192)) return 52;
                            __stark_runtime_free(slots[i]);
                        }
                        __stark_runtime_free(grown); return 0;
                    }
                    if (count==4096) return 53;
                    unsigned char *old=mode==36?slots[count]:slots[0];
                    if (mode==36) __stark_runtime_free(slots[0]);
                    else for (uint32_t i=1;i<count;++i) __stark_runtime_free(slots[i]);
                    unsigned char *grown=__stark_runtime_try_realloc(old,16,4096,8);
                    if (grown!=old || !labeled(grown,mode==36?count:0)) return 54;
                    fill(grown,4096);
                    struct rlimit saved, denied;
                    if (getrlimit(RLIMIT_AS,&saved)) return 55;
                    denied=saved; denied.rlim_cur=1;
                    if (setrlimit(RLIMIT_AS,&denied)) return 56;
                    unsigned char *next=__stark_runtime_try_alloc(16,8);
                    if (setrlimit(RLIMIT_AS,&saved)) return 57;
                    if (!next || !patterned(grown,4096)) return 58;
                    if (mode==36) {
                        // Claiming an untouched tail must preserve cached
                        // blocks from other slabs in the same size class.
                        if (next!=slots[0]) return 59;
                        for (uint32_t i=1;i<count;++i) {
                            if (!labeled(slots[i],i)) return 60;
                            __stark_runtime_free(slots[i]);
                        }
                    } else {
                        // Claiming a complete free chain from an older slab
                        // must preserve the current, unrelated untouched tail.
                        if (header(next)->base!=header(slots[count])->base
                            || !labeled(slots[count],count)) return 61;
                        __stark_runtime_free(slots[count]);
                    }
                    __stark_runtime_free(next); __stark_runtime_free(grown); return 0;
                }
                static int grow_expanded(int mode) {
                    if (mode>=36) return lazy_tail(mode);
                    size_t original=mode<=24?(size_t)16<<(mode-17):16;
                    size_t expanded=original==2048?4096:2048;
                    if (mode==30 || mode==31) {
                        // Neither a live sibling nor a cached sibling may be
                        // mistaken for exclusive ownership of an ordinary slab.
                        unsigned char *p=__stark_runtime_try_alloc(2048,8);
                        unsigned char *peer=mode==30?__stark_runtime_try_alloc(2048,8):NULL;
                        if (!p || (mode==30 && !peer)) return 28;
                        fill(p,2048); if (peer) memset(peer,71,2048);
                        unsigned char *grown=__stark_runtime_try_realloc(p,2048,8192,8);
                        if (!patterned(grown,2048) || (peer && !bytes(peer,2048,71))) return 29;
                        if (!peer) peer=__stark_runtime_try_alloc(2048,8);
                        if (!peer) return 30;
                        memset(peer,93,2048);
                        if (!patterned(grown,2048)) return 31;
                        __stark_runtime_free(peer); __stark_runtime_free(grown); return 0;
                    }
                    unsigned char *p=__stark_runtime_try_alloc(original,16);
                    if (!p) return 32;
                    fill(p,original);
                    unsigned char *owned=__stark_runtime_try_realloc(p,original,expanded,16);
                    if (owned!=p || !patterned(owned,original)) return 33;
                    fill(owned,expanded);
                    if (mode==26) {
                        pthread_t worker; void *result;
                        if (pthread_create(&worker,NULL,cache_and_grow_on_thread,owned)
                            || pthread_join(worker,&result) || result) return 34;
                        return 0;
                    }
                    size_t old_size=expanded;
                    if (mode==25) {
                        // The complete ownership tuple survives free/pop into
                        // a smaller logical allocation, without adding a tag.
                        __stark_runtime_free(owned);
                        owned=__stark_runtime_try_alloc(16,sizeof(void*));
                        if (owned!=p) return 35;
                        old_size=16; fill(owned,old_size);
                    }
                    struct rlimit saved, denied;
                    if (mode==27 || mode==28) {
                        if (getrlimit(RLIMIT_AS,&saved)) return 36;
                        denied=saved; denied.rlim_cur=1;
                        if (setrlimit(RLIMIT_AS,&denied)) return 37;
                    }
                    RuntimeHeader saved_header=*header(owned);
                    size_t physical=saved_header.physical, extent=saved_header.extent;
                    size_t size=mode==28?physical+7:expanded*4+123;
                    if (mode==27) size=header(owned)->extent*4+123;
                    size_t alignment=mode==29?64:sizeof(void*);
                    if (mode==33) alignment=24;
                    if (mode==34) size=SIZE_MAX;
                    void *block=MAP_FAILED; uintptr_t old_base=0;
                    size_t page=(size_t)sysconf(_SC_PAGESIZE); int must_move=0;
                    if (mode==35) {
                        old_base=(uintptr_t)header(owned)->base;
                        size_t mapped=(header(owned)->extent+page-1)/page*page;
                        size=header(owned)->extent*4+123;
                #ifdef MAP_FIXED_NOREPLACE
                        block=mmap((void*)(old_base+mapped),page,PROT_NONE,
                                   MAP_PRIVATE|MAP_ANONYMOUS|MAP_FIXED_NOREPLACE,-1,0);
                        must_move=block==(void*)(old_base+mapped) || (block==MAP_FAILED && errno==EEXIST);
                #endif
                    }
                    unsigned char *grown=__stark_runtime_try_realloc(owned,old_size,size,alignment);
                    if ((mode==27 || mode==28) && setrlimit(RLIMIT_AS,&saved)) return 38;
                    if (mode==28 && grown) {
                        // Both conversion and later logical growth keep the
                        // existing owned extent instead of discarding it.
                        if (!patterned(grown,old_size) || header(grown)->extent!=extent) return 62;
                        fill(grown,size);
                        unsigned char *next=__stark_runtime_try_realloc(grown,size,physical*2+15,alignment);
                        if (next!=grown || !patterned(next,size) || header(next)->extent!=extent) return 63;
                        grown=next; old_size=size; size=physical*2+15;
                    }
                    if (mode==27 || mode==33 || mode==34) {
                        // Rejected OS growth/fallback, invalid alignment and
                        // overflow must not relocate bytes or rewrite headers.
                        if (grown || !patterned(owned,old_size)) return 39;
                        RuntimeHeader actual=*header(owned);
                        if (actual.base!=saved_header.base || actual.extent!=saved_header.extent
                            || actual.physical!=saved_header.physical) return 64;
                        size=expanded*4+123; alignment=sizeof(void*);
                        grown=__stark_runtime_realloc(owned,old_size,size,alignment);
                    }
                    if (!patterned(grown,old_size) || (uintptr_t)grown%alignment) return 40;
                    if (must_move && (uintptr_t)grown/page*page==old_base) return 41;
                    if (block!=MAP_FAILED && munmap(block,page)) return 42;
                    fill(grown,size);
                    if (mode==32) {
                        owned=__stark_runtime_try_realloc(grown,size,0,alignment);
                        if (!owned || (uintptr_t)owned%alignment) return 43;
                        __stark_runtime_free(owned); return 0;
                    }
                    owned=__stark_runtime_try_realloc(grown,size,512,alignment);
                    if (!patterned(owned,512)) return 44;
                    grown=__stark_runtime_try_realloc(owned,512,65536,alignment);
                    if (!patterned(grown,512)) return 45;
                    __stark_runtime_free(grown); return 0;
                }
                int main(int argc, char **argv) {
                    if (argc!=2) return 1;
                    int mode=atoi(argv[1]);
                    if (mode>=17) return grow_expanded(mode);
                    if (mode>=9) {
                        size_t original=(size_t)16<<(mode-9);
                        size_t expanded=original==2048?4096:2048;
                        unsigned char *p=__stark_runtime_try_alloc(original,16);
                        if (!p) return 23;
                        memset(p,71,original);
                        unsigned char *grown=__stark_runtime_try_realloc(p,original,expanded,16);
                        if (grown!=p || !bytes(grown,original,71) || (uintptr_t)grown%16) return 24;
                        memset(grown,93,expanded);
                        void *peer=__stark_runtime_try_alloc(original,16);
                        if (!peer) return 25;
                        memset(peer,47,original);
                        if (!bytes(grown,expanded,93)) return 26;
                        __stark_runtime_free(peer); __stark_runtime_free(grown); return 0;
                    }
                    if (mode==1) {
                        // An empty exact bin may borrow a larger cached block;
                        // its later growth must remember the physical capacity.
                        unsigned char *p=__stark_runtime_try_alloc(4096,8);
                        if (!p) return 2;
                        __stark_runtime_free(p);
                        unsigned char *small=__stark_runtime_try_alloc(16,8);
                        if (small!=p) return 3;
                        memset(small,71,16);
                        p=__stark_runtime_try_realloc(small,16,2048,8);
                        if (p!=small || !bytes(p,16,71)) return 4;
                        memset(p,93,2048);
                        unsigned char *peer=__stark_runtime_try_alloc(2048,8);
                        if (!peer || peer==p) return 5;
                        memset(peer,47,2048);
                        if (!bytes(p,2048,93)) return 6;
                        __stark_runtime_free(peer); __stark_runtime_free(p);
                        return 0;
                    }
                    unsigned char *p=__stark_runtime_try_alloc(16,8);
                    if (!p) return 7;
                    memset(p,31,16);
                    unsigned char *peer=NULL;
                    if (mode==2 || mode==3 || mode==8) {
                        peer=__stark_runtime_try_alloc(16,8);
                        if (!peer) return 8;
                        memset(peer,47,16);
                    }
                    if (mode==8) {
                        // A sibling cached by another thread is absent from
                        // this thread's proof, so it cannot be reclaimed here.
                        pthread_t worker;
                        if (pthread_create(&worker,NULL,free_on_thread,peer) || pthread_join(worker,NULL)) return 27;
                        peer=NULL;
                    }
                    if (mode==4) {
                        // Every sibling is free, but their order no longer
                        // establishes the conservative untouched-chain proof.
                        void *a=__stark_runtime_try_alloc(16,8);
                        void *b=__stark_runtime_try_alloc(16,8);
                        if (!a || !b) return 9;
                        __stark_runtime_free(a); __stark_runtime_free(b);
                    }
                    struct rlimit saved, denied;
                    if (mode==3 || mode==7) {
                        if (getrlimit(RLIMIT_AS,&saved)) return 10;
                        denied=saved; denied.rlim_cur=1;
                        if (setrlimit(RLIMIT_AS,&denied)) return 11;
                    }
                    size_t align=mode==5?64:8;
                    size_t size=mode==3?8192:2048;
                    unsigned char *grown=mode==6
                        ?__stark_runtime_realloc(p,16,size,align)
                        :__stark_runtime_try_realloc(p,16,size,align);
                    if ((mode==3 || mode==7) && setrlimit(RLIMIT_AS,&saved)) return 12;
                    if (mode==3) {
                        if (grown || !bytes(p,16,31) || !bytes(peer,16,47)) return 13;
                        __stark_runtime_free(peer); __stark_runtime_free(p); return 0;
                    }
                    if (!bytes(grown,16,31) || (uintptr_t)grown%align) return 14;
                    if ((mode==0 || mode==6 || mode==7) && grown!=p) return 15;
                    if ((mode==2 || mode==4 || mode==8) && grown==p) return 16;
                    memset(grown,93,size);
                    if (peer && !bytes(peer,16,47)) return 17;
                    // Exercise the full promoted payload while allocating many
                    // siblings, then repeatedly free/reuse both physical bins.
                    void *others[256];
                    for (size_t i=0;i<256;++i) {
                        others[i]=__stark_runtime_try_alloc(16,8);
                        if (!others[i]) return 18;
                        memset(others[i],(unsigned char)i,16);
                        if (!bytes(grown,size,93)) return 19;
                    }
                    for (size_t i=0;i<256;++i) {
                        if (!bytes(others[i],16,(unsigned char)i)) return 20;
                        __stark_runtime_free(others[i]);
                    }
                    if (peer) __stark_runtime_free(peer);
                    __stark_runtime_free(grown);
                    for (size_t i=0;i<200;++i) {
                        p=__stark_runtime_try_alloc(16,8);
                        if (!p) return 21;
                        memset(p,71,16);
                        grown=__stark_runtime_try_realloc(p,16,2048,8);
                        if (!bytes(grown,16,71)) return 22;
                        memset(grown,93,2048);
                        __stark_runtime_free(grown);
                    }
                    return 0;
                }
                """);
            var output = Path.Combine(directory.FullName, "check");
            var link = await RunAsync(clang, ["-O3", "-pthread", llvm, c, "-o", output]);
            Assert.True(link.ExitCode == 0, link.Output);
            // Each proof boundary starts with fresh thread-local allocator state.
            for (var mode = 0; mode <= 39; mode++)
            {
                var run = await RunAsync(output, [mode.ToString()]);
                Assert.True(run.ExitCode == 0, $"Mode {mode}, exit {run.ExitCode}: {run.Output}");
            }
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
