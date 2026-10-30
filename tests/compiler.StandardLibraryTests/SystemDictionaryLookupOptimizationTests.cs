using System.Diagnostics;
using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemDictionaryLookupOptimizationTests : StandardLibraryTestSuite
{
    private const string Program = """
        import System.Collections
        import System.Memory
        module DictionaryProbeCases
        fn bool Ok(MemoryStatus status) {
            switch(status) { case MemoryStatus.Ok: return true; case MemoryStatus.Err(var error): return false; }
        }
        struct CollisionKey {
            u32[0 max] Value;
            static finite law u64[0 max] Hash(borrow CollisionKey key) { return 63; }
            static finite law bool Equals(borrow CollisionKey a, borrow CollisionKey b) where overlap(a,b) { return a.Value == b.Value; }
        }
        export unsafe ffi fn i32[min max] main() {
            stack mut Dictionary<u32[0 max],u32[0 max]> table = new();
            stack mut u32[0 max] found = 123;
            stack u32[0 max] absent = 999;
            stack u32[0 max] zero = 0;
            stack u32[0 max] home = 4095;
            if (table.TryGet(absent,found) || !Ok(table.Reserve(32)) || table.Capacity() != 64) { return 1; }
            for willexit(stack mut u8[0 32] i=0; i<32; i+=1) {
                stack u32[0 max] key = 4095 + (u32[0 max])i * 4096;
                if (!Ok(table.Set(key,(u32[0 max])i+1)) || table.Capacity()!=64 || table.Count()!=(u64[0 2 ** 63 - 1])i+1) { return 2; }
            }
            if (!Ok(table.Set(0,77)) || !table.TryGet(zero,found) || found!=77 || table.Count()!=33) { return 3; }
            if (table.TryGet(absent,found)) { return 4; }
            stack u32[0 max] clusterMiss = (u32[0 max])4095 + (u32[0 max])40 * 4096;
            if (table.TryGet(clusterMiss,found)) { return 5; }
            for willexit(stack mut u8[0 32] i=0; i<32; i+=1) {
                stack u32[0 max] key = 4095 + (u32[0 max])i * 4096;
                if (!table.TryGet(key,found) || found!=(u32[0 max])i+1) { return 6; }
            }
            stack mut u32[0 max] overlappingKey = 4095;
            if (!table.TryGet(overlappingKey,overlappingKey) || overlappingKey!=1) { return 7; }
            stack u32[0 max] removed = (u32[0 max])4095 + (u32[0 max])17 * 4096;
            if (!table.Remove(removed) || table.TryGet(removed,found) || table.Count()!=32) { return 8; }
            if (!Ok(table.Set(4095,900)) || table.Count()!=32 || table.Capacity()!=128) { return 9; }
            if (!table.TryGet(home,found) || found!=900) { return 10; }
            if (!Ok(table.Reserve(500)) || table.Count()!=32 || table.Capacity()<1024) { return 11; }
            if (!table.TryGet(home,found) || found!=900 || !table.TryGet(zero,found) || found!=77) { return 12; }
            stack mut Dictionary<CollisionKey,u32[0 max]> custom = new();
            if (!Ok(custom.Reserve(32))) { return 13; }
            for willexit(stack mut u8[0 24] i=0; i<24; i+=1) {
                stack CollisionKey key = new CollisionKey() { Value=(u32[0 max])i };
                if (!Ok(custom.Set(key,(u32[0 max])i+3))) { return 14; }
            }
            for willexit(stack mut u8[0 24] i=0; i<24; i+=1) {
                stack CollisionKey key = new CollisionKey() { Value=(u32[0 max])i };
                if (!custom.TryGet(key,found) || found!=(u32[0 max])i+3) { return 15; }
            }
            stack CollisionKey missing = new CollisionKey() { Value=99 };
            if (custom.TryGet(missing,found)) { return 16; }
            stack mut HashSet<u32[0 max]> set = new();
            if (!Ok(set.Reserve(32))) { return 18; }
            for willexit(stack mut u8[0 32] i=0; i<32; i+=1) {
                stack u32[0 max] key = 4095 + (u32[0 max])i * 4096;
                if (!Ok(set.Add(key)) || set.Count()!=(u64[0 2 ** 63 - 1])i+1) { return 19; }
            }
            if (set.Contains(zero) || !Ok(set.Add(0)) || !set.Contains(zero) || set.Count()!=33) { return 20; }
            if (set.Contains(clusterMiss) || !set.Remove(home) || set.Contains(home) || set.Count()!=32) { return 21; }
            stack mut Dictionary<u32[0 max],u32[0 max]> failing = new();
            if (Ok(failing.Reserve((u64[0 2 ** 63 - 1])(2 ** 62 + 1))) || failing.Capacity()!=0 || failing.Count()!=0) { return 17; }
            stack u64[0 2 ** 63 - 1] oldCapacity = table.Capacity();
            if (Ok(table.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))) || table.Capacity()!=oldCapacity || table.Count()!=32
                || !table.TryGet(zero,found) || found!=77) { return 22; }
            return 0;
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeBucketsCollisionsWraparoundGrowthAndFailureWork(bool packaged)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var root = packaged ? await SharedStdlibPackage.GetDirectoryAsync()
            : Path.Combine(FindRepositoryRoot(), "stdlib", "src");
        var directory = Directory.CreateTempSubdirectory("stark-dictionary-probes-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(source, Program);
            var output = new StringWriter();
            var errors = new StringWriter();
            var code = await CompilerCli.RunAsync([source, "--emit-exe", "--target", target.Triple, "-I", root, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(code==0, output+"\n"+errors);
            using var process = Process.Start(new ProcessStartInfo(binary) {
                RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false
            });
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0,process.ExitCode);
            Assert.Equal("",await stdout);
            Assert.Equal("",await stderr);
        }
        finally { directory.Delete(recursive:true); }
    }
}
