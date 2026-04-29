using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsStandardLibraryTests : StandardLibraryTestSuite
{
    private const string CollectionsGrowthMoveDropProgram = """
        import System.Collections
        import System.Memory
        module App

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool IsPowerOfTwo(i64[0 max] value) {
            if (value == 0) {
                return false;
            }

            stack i64[0 max] mask = (i64[0 max])(value - 1);
            return (value & mask) == 0;
        }

        fn bool ConsumeList(List<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected && values.Capacity() >= expected;
        }

        fn bool ConsumeStack(Stack<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected && values.Peek() == 79;
        }

        fn bool ConsumeQueue(Queue<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected && values.Peek() == 0;
        }

        fn bool ConsumeLinkedList(LinkedList<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected;
        }

        fn bool ConsumeDictionary(Dictionary<i32[0 max], i32[0 max]> values, i64[0 max] expected) {
            stack i32[0 max] key = 17;
            stack mut i32[0 max] found = 0;
            return values.Count() == expected
                && IsPowerOfTwo(values.Capacity())
                && values.ContainsKey(key)
                && values.TryGet(key, found)
                && found == 34;
        }

        export ffi fn i32[min max] main() {
            stack mut List<i32[0 max]> list = new();
            for willexit (stack mut i32[0 96] i = 0; i < 96; i += 1) {
                if (!Ok(list.Push(i))) {
                    return 1;
                }
            }

            if (!ConsumeList(list, 96)) {
                return 2;
            }

            stack mut Stack<i32[0 max]> stackValues = new();
            for willexit (stack mut i32[0 80] i = 0; i < 80; i += 1) {
                if (!Ok(stackValues.Push(i))) {
                    return 3;
                }
            }

            if (!ConsumeStack(stackValues, 80)) {
                return 4;
            }

            stack mut Queue<i32[0 max]> queue = new();
            for willexit (stack mut i32[0 96] i = 0; i < 96; i += 1) {
                if (!Ok(queue.Enqueue(i))) {
                    return 5;
                }
            }

            if (!ConsumeQueue(queue, 96)) {
                return 6;
            }

            stack mut LinkedList<i32[0 max]> linked = new();
            for willexit (stack mut i32[0 48] i = 0; i < 48; i += 1) {
                if (!Ok(linked.AddLast(i))) {
                    return 7;
                }
            }

            if (!ConsumeLinkedList(linked, 48)) {
                return 8;
            }

            stack mut Dictionary<i32[0 max], i32[0 max]> dictionary = new();
            if (!Ok(dictionary.Reserve(3)) || !IsPowerOfTwo(dictionary.Capacity())) {
                return 9;
            }

            for willexit (stack mut i32[0 64] i = 0; i < 64; i += 1) {
                stack i32[0 max] key = i;
                stack i32[0 max] value = (i32[0 max])(i * 2);
                if (!Ok(dictionary.Set(key, value))) {
                    return 9;
                }

                if (!IsPowerOfTwo(dictionary.Capacity())) {
                    return 9;
                }

                if (i == 4 && dictionary.Capacity() < 16) {
                    return 9;
                }
            }

            if (!ConsumeDictionary(dictionary, 64)) {
                return 10;
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

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UseCollections() {
                    stack mut List<i32[0 max]> values = new();
                    if (!Ok(values.Push(10))) {
                        return false;
                    }
                    values.GetMut(0) = 11;
                    values.AsMutableSlice()[0] = 12;
                    if (values.Get(0) != 12) {
                        return false;
                    }
                    if (values.AsSlice()[0] != 12) {
                        return false;
                    }
                    stack mut i32[0 max] popped = 0;
                    if (!values.TryPop(popped) || popped != 12 || values.Count() != 0) {
                        return false;
                    }

                    stack mut Stack<i32[0 max]> numbers = new();
                    if (!Ok(numbers.Push(20))) {
                        return false;
                    }
                    if (numbers.Peek() != 20) {
                        return false;
                    }
                    if (!numbers.TryPop(popped) || popped != 20 || numbers.Count() != 0) {
                        return false;
                    }

                    stack mut Queue<i32[0 max]> queue = new();
                    if (!Ok(queue.Enqueue(30))) {
                        return false;
                    }
                    if (queue.Peek() != 30) {
                        return false;
                    }
                    if (!queue.TryDequeue(popped) || popped != 30 || queue.Count() != 0) {
                        return false;
                    }

                    stack mut LinkedList<i32[0 max]> linked = new();
                    if (!Ok(linked.ReserveNodes(2)) || linked.Count() != 0) {
                        return false;
                    }
                    if (!Ok(linked.AddFirst(40))) {
                        return false;
                    }
                    if (!Ok(linked.AddLast(50))) {
                        return false;
                    }
                    if (!linked.TryRemoveFirst(popped) || popped != 40 || linked.Count() != 1) {
                        return false;
                    }
                    if (!linked.TryRemoveLast(popped) || popped != 50 || linked.Count() != 0) {
                        return false;
                    }

                    stack mut Dictionary<i32[0 max], i32[0 max]> dictionary = new();
                    stack i32[0 max] dictionaryKey = 3;
                    if (!Ok(dictionary.Set(dictionaryKey, 33))) {
                        return false;
                    }
                    if (!dictionary.ContainsKey(dictionaryKey)) {
                        return false;
                    }
                    stack mut i32[0 max] found = 0;
                    if (!dictionary.TryGet(dictionaryKey, found) || found != 33) {
                        return false;
                    }
                    if (!dictionary.Remove(dictionaryKey) || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0) {
                        return false;
                    }

                    stack System.Memory.Allocator listAllocator = new System.Memory.Allocator() {
                        Kind = 7
                    };
                    stack mut List<i32[0 max]> customList = new(listAllocator);
                    if (!Ok(customList.Push(1)) || !Ok(customList.Push(2)) || customList.Count() != 2) {
                        return false;
                    }

                    stack System.Memory.Allocator queueAllocator = new System.Memory.Allocator() {
                        Kind = 7
                    };
                    stack mut Queue<i32[0 max]> customQueue = new(queueAllocator);
                    if (!Ok(customQueue.Enqueue(3)) || !Ok(customQueue.Enqueue(4)) || customQueue.Count() != 2) {
                        return false;
                    }

                    stack System.Memory.Allocator dictionaryAllocator = new System.Memory.Allocator() {
                        Kind = 7
                    };
                    stack mut Dictionary<i32[0 max], i32[0 max]> customDictionary = new(dictionaryAllocator);
                    stack i32[0 max] customDictionaryKey = 9;
                    if (!Ok(customDictionary.Set(customDictionaryKey, 18)) || !customDictionary.ContainsKey(customDictionaryKey)) {
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
    public void StdLibSourceDictionaryGrowthLowersThroughSharedCapacityHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibDictionaryGrowth.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool GrowDictionary() {
                    stack mut Dictionary<i32[0 max], i32[0 max]> dictionary = new();
                    stack mut i32[0 max] index = 0;
                    while willexit (index < 9) {
                        stack i32[0 max] value = (i32[0 max])(index + 1);
                        if (!Ok(dictionary.Set(index, value))) {
                            return false;
                        }

                        index += 1;
                    }

                    stack i32[0 max] lookupKey = 4;
                    stack mut i32[0 max] found = 0;
                    return dictionary.Capacity() >= 16
                        && dictionary.TryGet(lookupKey, found)
                        && found == 5;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.Contains("ComputeHashStorageGrowthCapacity", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("ComputeContiguousGrowthCapacity", llvm.Text, StringComparison.Ordinal);
        var tryGetBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__i32_0_2147483647__i32_0_2147483647",
            "Expected integer Dictionary.TryGet specialization to be emitted.");
        Assert.Contains(" = and i64 ", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain(" srem i64 ", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i64 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i1 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Equals__", tryGetBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibCollectionsGrowMoveDropExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
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
    public async Task PackagedStdLibCollectionsGrowMoveDropExecutableRunsWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-collections-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.True(
                buildExitCode == 0,
                buildStdout + Environment.NewLine + buildStderr);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(appPath, CollectionsGrowthMoveDropProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, appDirectory, string.Empty);
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
}
