using Stark.Compiler;
using static compiler.StandardLibraryTests.SystemCollectionsTestPrograms;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsStandardLibraryTests : StandardLibraryTestSuite
{
    private const string PromotedListParityProgram = """
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool TooLarge(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return false;
                case MemoryStatus.Err(var error):
                    return error == MemoryError.TooLarge;
            }
        }

        fn void Bump(i32[min max] value)
        {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource
        {
            i32[min max] Value;

            drop
            {
                Bump(self.Value);
            }
        }

        export fn i32[min max] main()
        {
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> experimental = new();

            if (!Ok(stable.Reserve(0)) || !Ok(experimental.Reserve(0)))
            {
                return 1;
            }

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                if (!Ok(stable.Push(i)) || !Ok(experimental.Push(i)))
                {
                    return 2;
                }
            }

            if (stable.Count() != experimental.Count() || stable.Capacity() < stable.Count() || experimental.Capacity() < experimental.Count())
            {
                return 3;
            }

            stable.GetMut(10) = 111;
            experimental.GetMut(10) = 111;
            stable.AsMutableSlice()[11] = 222;
            experimental.AsMutableSlice()[11] = 222;

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                if (stable.Get(i) != experimental.Get(i) || stable.AsSlice()[i] != experimental.AsSlice()[i])
                {
                    return 4;
                }
            }

            stack mut i64[min max] checksum = 0;
            while willexit (experimental.Count() > 0)
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue))
                {
                    return 5;
                }

                if (stableValue != experimentalValue)
                {
                    return 6;
                }

                checksum += (i64[min max])stableValue;
            }

            if (stable.Count() != 0 || experimental.Count() != 0 || checksum != 8440)
            {
                return 7;
            }

            if (!TooLarge(stable.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))) || !TooLarge(experimental.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))))
            {
                return 8;
            }

            {
                stack mut System.Collections.List<Resource> stableDrops = new();
                if (!Ok(stableDrops.Push(new Resource()
                {
                    Value = 1
                }
                )) || !Ok(stableDrops.Push(new Resource()
                {
                    Value = 2
                }
                )))
                {
                    return 9;
                }

                stableDrops.Clear();
                if (DropCounter != 3)
                {
                    return 10;
                }
            }

            if (DropCounter != 3)
            {
                return 11;
            }

            {
                stack mut System.Collections.List<Resource> experimentalDrops = new();
                if (!Ok(experimentalDrops.Push(new Resource()
                {
                    Value = 4
                }
                )) || !Ok(experimentalDrops.Push(new Resource()
                {
                    Value = 5
                }
                )))
                {
                    return 12;
                }

                experimentalDrops.Clear();
                if (DropCounter != 12)
                {
                    return 13;
                }
            }

            if (DropCounter != 12)
            {
                return 14;
            }

            {
                stack mut System.Collections.List<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.Push(new Resource()
                {
                    Value = 6
                }
                )) || !Ok(scopedDrops.Push(new Resource()
                {
                    Value = 7
                }
                )))
                {
                    return 15;
                }
            }

            if (DropCounter != 25)
            {
                return 16;
            }

            return 0;
        }
        """;

    private const string PromotedLinkedListParityProgram = """
        import System.Collections
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn void Bump(i32[min max] value)
        {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource
        {
            i32[min max] Value;

            drop
            {
                Bump(self.Value);
            }
        }

        export fn i32[min max] main()
        {
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> experimental = new();

            if (!Ok(stable.ReserveNodes(4)) || !Ok(experimental.ReserveNodes(4)))
            {
                return 1;
            }

            if (stable.Count() != 0 || experimental.Count() != 0 || !stable.IsEmpty() || !experimental.IsEmpty())
            {
                return 2;
            }

            if (!Ok(stable.AddLast(1)) || !Ok(experimental.AddLast(1)))
            {
                return 3;
            }

            if (!Ok(stable.AddLast(2)) || !Ok(experimental.AddLast(2)))
            {
                return 4;
            }

            if (!Ok(stable.AddFirst(0)) || !Ok(experimental.AddFirst(0)))
            {
                return 5;
            }

            if (stable.Count() != experimental.Count() || stable.IsEmpty() != experimental.IsEmpty())
            {
                return 6;
            }

            stack mut u32[0 2 ** 31 - 1] stableValue = 0;
            stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue))
            {
                return 7;
            }

            if (stableValue != 0 || experimentalValue != 0)
            {
                return 8;
            }

            if (!stable.TryRemoveLast(stableValue) || !experimental.TryRemoveLast(experimentalValue))
            {
                return 9;
            }

            if (stableValue != 2 || experimentalValue != 2)
            {
                return 10;
            }

            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue))
            {
                return 11;
            }

            if (stableValue != 1 || experimentalValue != 1 || !stable.IsEmpty() || !experimental.IsEmpty())
            {
                return 12;
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
            {
                if (!Ok(stable.AddLast(i)) || !Ok(experimental.AddLast(i)))
                {
                    return 13;
                }

                if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue))
                {
                    return 14;
                }

                if (stableValue != experimentalValue)
                {
                    return 15;
                }

                checksum += (i64[min max])experimentalValue;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty() || checksum != 2016)
            {
                return 16;
            }

            {
                stack mut System.Collections.LinkedList<Resource> drops = new();
                if (!Ok(drops.AddLast(new Resource()
                {
                    Value = 1
                }
                )) || !Ok(drops.AddFirst(new Resource()
                {
                    Value = 2
                }
                )))
                {
                    return 17;
                }

                drops.Clear();
                if (DropCounter != 3)
                {
                    return 18;
                }
            }

            if (DropCounter != 3)
            {
                return 19;
            }

            {
                stack mut System.Collections.LinkedList<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.AddLast(new Resource()
                {
                    Value = 4
                }
                )) || !Ok(scopedDrops.AddLast(new Resource()
                {
                    Value = 5
                }
                )))
                {
                    return 20;
                }
            }

            if (DropCounter != 12)
            {
                return 21;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceCollectionsSupportOwnedAllocatorBackedSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibCollectionsSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UseCollections()
                {
                    stack mut List<u32[0 2 ** 31 - 1]> values = new();
                    if (!Ok(values.Push(10)))
                    {
                        return false;
                    }
                    values.GetMut(0) = 11;
                    values.AsMutableSlice()[0] = 12;
                    if (values.Get(0) != 12)
                    {
                        return false;
                    }
                    if (values.AsSlice()[0] != 12)
                    {
                        return false;
                    }
                    stack mut u32[0 2 ** 31 - 1] popped = 0;
                    if (!values.TryPop(popped) || popped != 12 || values.Count() != 0)
                    {
                        return false;
                    }

                    stack mut Stack<u32[0 2 ** 31 - 1]> numbers = new();
                    if (!Ok(numbers.Push(20)))
                    {
                        return false;
                    }
                    if (numbers.Peek() != 20)
                    {
                        return false;
                    }
                    if (!numbers.TryPop(popped) || popped != 20 || numbers.Count() != 0)
                    {
                        return false;
                    }

                    stack mut Queue<u32[0 2 ** 31 - 1]> queue = new();
                    if (!Ok(queue.Enqueue(30)))
                    {
                        return false;
                    }
                    if (queue.Peek() != 30)
                    {
                        return false;
                    }
                    if (!queue.TryDequeue(popped) || popped != 30 || queue.Count() != 0)
                    {
                        return false;
                    }

                    stack mut LinkedList<u32[0 2 ** 31 - 1]> linked = new();
                    if (!Ok(linked.ReserveNodes(2)) || linked.Count() != 0)
                    {
                        return false;
                    }
                    if (!Ok(linked.AddFirst(40)))
                    {
                        return false;
                    }
                    if (!Ok(linked.AddLast(50)))
                    {
                        return false;
                    }
                    if (!linked.TryRemoveFirst(popped) || popped != 40 || linked.Count() != 1)
                    {
                        return false;
                    }
                    if (!linked.TryRemoveLast(popped) || popped != 50 || linked.Count() != 0)
                    {
                        return false;
                    }

                    stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    stack u32[0 2 ** 31 - 1] dictionaryKey = 3;
                    if (!Ok(dictionary.Set(dictionaryKey, 33)))
                    {
                        return false;
                    }
                    if (!dictionary.ContainsKey(dictionaryKey))
                    {
                        return false;
                    }
                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    if (!dictionary.TryGet(dictionaryKey, found) || found != 33)
                    {
                        return false;
                    }
                    if (!dictionary.Remove(dictionaryKey) || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0)
                    {
                        return false;
                    }

                    stack mut HashSet<u32[0 2 ** 31 - 1]> set = new();
                    stack u32[0 2 ** 31 - 1] setKey = 5;
                    if (!Ok(set.Add(setKey)))
                    {
                        return false;
                    }
                    if (!set.Contains(setKey) || set.Count() != 1)
                    {
                        return false;
                    }
                    if (!set.Remove(setKey) || set.Contains(setKey) || set.Count() != 0)
                    {
                        return false;
                    }

                    stack mut List<u32[0 2 ** 31 - 1]> customList = new();
                    if (!Ok(customList.Push(1)) || !Ok(customList.Push(2)) || customList.Count() != 2)
                    {
                        return false;
                    }

                    stack mut Queue<u32[0 2 ** 31 - 1]> customQueue = new();
                    if (!Ok(customQueue.Enqueue(3)) || !Ok(customQueue.Enqueue(4)) || customQueue.Count() != 2)
                    {
                        return false;
                    }

                    stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> customDictionary = new();
                    stack u32[0 2 ** 31 - 1] customDictionaryKey = 9;
                    if (!Ok(customDictionary.Set(customDictionaryKey, 18)) || !customDictionary.ContainsKey(customDictionaryKey))
                    {
                        return false;
                    }

                    stack mut HashSet<u32[0 2 ** 31 - 1]> customSet = new();
                    stack u32[0 2 ** 31 - 1] customSetKey = 11;
                    if (!Ok(customSet.Add(customSetKey)) || !customSet.Contains(customSetKey))
                    {
                        return false;
                    }

                    return values.Capacity() >= 1;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourcePromotedCollectionsExposeDynamicComparisonTypes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedCollectionsSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UsePromotedCollections()
                {
                    stack mut System.Collections.List<u32[0 2 ** 31 - 1]> values = new();
                    if (!Ok(values.Push(10)))
                    {
                        return false;
                    }

                    values.GetMut(0) = 11;
                    values.AsMutableSlice()[0] = 12;
                    if (values.Get(0) != 12 || values.AsSlice()[0] != 12)
                    {
                        return false;
                    }

                    stack mut u32[0 2 ** 31 - 1] popped = 0;
                    if (!values.TryPop(popped) || popped != 12 || values.Count() != 0)
                    {
                        return false;
                    }

                    stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> stackValues = new();
                    if (!Ok(stackValues.Push(20)) || stackValues.Peek() != 20)
                    {
                        return false;
                    }

                    if (!stackValues.TryPop(popped) || popped != 20 || stackValues.Count() != 0)
                    {
                        return false;
                    }

                    stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> queueValues = new();
                    if (!Ok(queueValues.Enqueue(30)) || queueValues.Peek() != 30)
                    {
                        return false;
                    }

                    if (!queueValues.TryDequeue(popped) || popped != 30 || queueValues.Count() != 0)
                    {
                        return false;
                    }

                    stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> ringValues = new();
                    if (!Ok(ringValues.Enqueue(40)) || !Ok(ringValues.Enqueue(41)))
                    {
                        return false;
                    }

                    if (!ringValues.TryDequeue(popped) || popped != 40 || ringValues.Count() != 1)
                    {
                        return false;
                    }

                    stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> linkedValues = new();
                    if (!Ok(linkedValues.ReserveNodes(2)) || !Ok(linkedValues.AddFirst(50)) || !Ok(linkedValues.AddLast(51)))
                    {
                        return false;
                    }

                    if (!linkedValues.TryRemoveFirst(popped) || popped != 50 || linkedValues.Count() != 1)
                    {
                        return false;
                    }

                    if (!linkedValues.TryRemoveLast(popped) || popped != 51 || linkedValues.Count() != 0)
                    {
                        return false;
                    }

                    stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    stack u32[0 2 ** 31 - 1] dictionaryKey = 3;
                    if (!Ok(dictionary.Reserve(8)) || !Ok(dictionary.Set(dictionaryKey, 33)))
                    {
                        return false;
                    }

                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    if (!dictionary.ContainsKey(dictionaryKey) || !dictionary.TryGet(dictionaryKey, found) || found != 33)
                    {
                        return false;
                    }

                    if (!Ok(dictionary.Set(dictionaryKey, 44)))
                    {
                        return false;
                    }

                    if (!dictionary.TryGet(dictionaryKey, found) || found != 44)
                    {
                        return false;
                    }

                    if (!dictionary.TryRemove(dictionaryKey, found) || found != 44 || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0)
                    {
                        return false;
                    }

                    if (!Ok(dictionary.Set(dictionaryKey, 55)))
                    {
                        return false;
                    }

                    stack DictionaryRemoveResult<u32[0 2 ** 31 - 1]> removed = dictionary.RemoveMove(dictionaryKey);
                    switch (removed)
                    {
                        case DictionaryRemoveResult<u32[0 2 ** 31 - 1]>.Missing:
                            return false;
                        case DictionaryRemoveResult<u32[0 2 ** 31 - 1]>.Removed(var removedValue):
                            if (removedValue != 55 || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0)
                            {
                                return false;
                            }
                    }

                    if (!Ok(dictionary.Set(dictionaryKey, 66)))
                    {
                        return false;
                    }

                    return dictionary.Remove(dictionaryKey) && !dictionary.ContainsKey(dictionaryKey) && dictionary.Count() == 0;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourcePromotedListLowersThroughDynamicStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedListLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn u64[0 2 ** 63 - 1] GrowAndSlice()
                {
                    stack mut System.Collections.List<u32[0 2 ** 31 - 1]> values = new();
                    if (!Ok(values.Reserve(8)))
                    {
                        return 0;
                    }

                    for willexit (stack mut u8[0 8] i = 0; i < 8; i += 1)
                    {
                        if (!Ok(values.Push(i)))
                        {
                            return 0;
                        }
                    }

                    values.AsMutableSlice()[3] = 99;
                    return (u64[0 2 ** 63 - 1])values.AsSlice()[3];
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                // Target Linux explicitly: this test asserts the libc-free dynamic-storage
                // lowering, and only the Linux OS allocation shim is syscall-backed (macOS
                // legitimately bottoms out in libc malloc, Windows in HeapAlloc).
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("@__stark_runtime_try_realloc", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("__stark_dynamic_try_reserve", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("extractvalue { ptr, i64, i64 }", llvm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibCollectionsGrowMoveDropExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-collections-source-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, CollectionsGrowthMoveDropProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedListMatchesStableListExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-list-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedListParityProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedLinkedListMatchesStableLinkedListExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-linked-list-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedLinkedListParityProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public void PromotedLinkedListReserveNodesDoesNotEagerlyBuildFreeList()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkPath = Path.Combine(repositoryRoot, "benchmarks", "collections", "LinkedListReservedPush.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkPath), benchmarkPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var reserveBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_LinkedList_ReserveNodes__u32(");
        var allocateBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_LinkedList_AllocateNode__u32(");

        Assert.Contains("__stark_dynamic_try_reserve", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkedListValueSlot", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkedListLinks", reserveBody, StringComparison.Ordinal);
        Assert.Contains("LinkedListValueSlot", allocateBody, StringComparison.Ordinal);
        Assert.Contains("LinkedList_ReserveNodes__u32", allocateBody, StringComparison.Ordinal);
        Assert.Contains("insertvalue %System_Collections_LinkedListValueSlot_u32_ zeroinitializer, i8 1", allocateBody, StringComparison.Ordinal);
    }

    private static string ExtractDefinedFunctionText(string llvm, string signaturePrefix)
    {
        var functionStart = llvm.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(functionStart >= 0, $"Expected '{signaturePrefix}' definition to be emitted.");

        var bodyStart = llvm.IndexOf('{', functionStart);
        Assert.True(bodyStart > functionStart, $"Expected '{signaturePrefix}' to include a function body.");

        var depth = 0;
        for (var index = bodyStart; index < llvm.Length; index++)
        {
            var current = llvm[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return llvm.Substring(functionStart, index - functionStart + 1);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected '{signaturePrefix}' body to terminate in emitted LLVM.");
    }
}
