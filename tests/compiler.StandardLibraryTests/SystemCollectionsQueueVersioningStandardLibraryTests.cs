using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsQueueVersioningStandardLibraryTests : StandardLibraryTestSuite
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedDrainsPreserveEveryStateAndExitFromSourceAndPackages(bool package)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-queue-versioning-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(source, Program);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var root = FindRepositoryRoot();
            var modules = package ? await SharedStdlibPackage.GetDirectoryAsync() : Path.Combine(root, "stdlib", "src");
            SsaFunction CompileThrough(string pass)
            {
                var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(Program, source), new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(modules), TargetInfo: target, StopAfterPassId: pass));
                Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
                return result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr).Functions.Single(f => f.Name == "CheckCase");
            }
            var before = CompileThrough("inline-ssa");
            var after = CompileThrough("queue-dequeue-loop-ssa");
            Assert.True(after.Blocks.Count > before.Blocks.Count, "The state/exit checks must exercise a compiled fast version.");
            string[] imports = package
                ? ["-I", modules]
                : ["--sdk-root", root];
            var exit = await CompilerCli.RunAsync([source, "--emit-exe", "--no-stark-path", "--target", target.Triple,
                "-o", executable, .. imports], new StringReader(string.Empty), stdout, stderr);
            Assert.True(exit == 0, $"{stdout}{Environment.NewLine}{stderr}");
            var execution = await RunProcessWithUtf8StdinAsync(executable, directory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal const string Program = """
        import System.Collections
        import System.Memory
        module QueueVersioningCheck

        finite law bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok: return true;
                case MemoryStatus.Err(var error): return false;
            }
        }

        noinline fn bool Step(mut borrow Queue<u32[0 max]> queue, out u32[0 max] value)
        {
            return queue.TryDequeue(value);
        }

        finite law i64[min max] Expected(i64[min max] fill, i64[min max] removed, i64[min max] index)
        {
            if (index < fill - removed) { return index + removed + 1; }
            return index - (fill - removed) + 11;
        }

        noinline law bool Observe(borrow Queue<u32[0 max]> queue, i64[min max] count, i64[min max] front)
        {
            if (queue.Count() != (u64[0 max])count || queue.IsEmpty() != (count == 0)) { return false; }
            return count == 0 || queue.Peek() == (u32[0 max])front;
        }

        noinline unsafe fn i32[min max] CheckCase(i64[min max] fill, i64[min max] removed,
            i64[min max] added, i64[min max] stop)
        {
            stack mut Queue<u32[0 max]> queue = new();
            for willexit (stack mut i64[min max] i = 0; i < fill; i += 1)
            {
                if (!Ok(queue.Enqueue((u32[0 max])(i + 1)))) { return 1; }
            }
            stack mut u32[0 max] value = 0;
            for willexit (stack mut i64[min max] i = 0; i < removed; i += 1)
            {
                if (!Step(queue, value) || value != (u32[0 max])(i + 1)) { return 2; }
            }
            for willexit (stack mut i64[min max] i = 0; i < added; i += 1)
            {
                if (!Ok(queue.Enqueue((u32[0 max])(i + 11)))) { return 3; }
            }
            stack i64[min max] total = fill - removed + added;
            stack mut i64[min max] taken = 0;
            while willexit (!queue.IsEmpty())
            {
                if (taken == stop) { break; }
                if (!queue.TryDequeue(value) || value != (u32[0 max])Expected(fill, removed, taken)) { return 4; }
                taken += 1;
                if (!Observe(queue, total - taken, Expected(fill, removed, taken))) { return 5; }
            }
            if (!Observe(queue, total - taken, Expected(fill, removed, taken))) { return 6; }
            if (!Ok(queue.Enqueue(91))) { return 7; }
            while willexit (taken < total)
            {
                if (!Step(queue, value) || value != (u32[0 max])Expected(fill, removed, taken)) { return 8; }
                taken += 1;
            }
            if (!Step(queue, value) || value != 91 || !queue.IsEmpty() || Step(queue, value)) { return 9; }
            return 0;
        }

        noinline fn void AppendPair(mut borrow Queue<u32[0 max]> queue)
        {
            queue.Enqueue(5);
            queue.Enqueue(6);
        }

        unsafe fn i32[min max] MutationInvalidatesEntryGuard()
        {
            stack mut Queue<u32[0 max]> queue = new();
            queue.Enqueue(1); queue.Enqueue(2); queue.Enqueue(3); queue.Enqueue(4);
            stack mut u32[0 max] value = 0;
            if (!Step(queue, value) || !Step(queue, value)) { return 10; }
            stack mut u32[0 max] taken = 0;
            while willexit (!queue.IsEmpty())
            {
                if (!queue.TryDequeue(value) || value != taken + 3) { return 11; }
                if (taken == 0) { AppendPair(queue); }
                taken += 1;
            }
            if (taken != 4 || queue.Count() != 0) { return 12; }
            return 0;
        }

        export unsafe ffi fn i32[min max] main()
        {
            for willexit (stack mut i64[min max] fill = 0; fill <= 8; fill += 1)
            {
                for willexit (stack mut i64[min max] removed = 0; removed <= fill; removed += 1)
                {
                    for willexit (stack mut i64[min max] added = 0; added <= 8; added += 1)
                    {
                        for willexit (stack mut i64[min max] stop = 0; stop <= 8; stop += 1)
                        {
                            stack i32[min max] result = CheckCase(fill, removed, added, stop);
                            if (result != 0) { return result; }
                        }
                    }
                }
            }
            return MutationInvalidatesEntryGuard();
        }
        """;
}
