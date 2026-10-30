using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsStackQueueStandardLibraryTests : StandardLibraryTestSuite
{
    private const string PromotedStackParityProgram = """
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
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> experimental = new();

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                if (!Ok(stable.Push(i)) || !Ok(experimental.Push(i)))
                {
                    return 1;
                }

                if (stable.Peek() != experimental.Peek())
                {
                    return 2;
                }
            }

            if (stable.Count() != experimental.Count() || stable.IsEmpty() != experimental.IsEmpty())
            {
                return 3;
            }

            stack mut i64[min max] checksum = 0;
            while willexit (!experimental.IsEmpty())
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue))
                {
                    return 4;
                }

                if (stableValue != experimentalValue)
                {
                    return 5;
                }

                checksum += (i64[min max])stableValue;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty() || checksum != 8128)
            {
                return 6;
            }

            {
                stack mut System.Collections.Stack<Resource> stableDrops = new();
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
                    return 7;
                }

                stableDrops.Clear();
                if (DropCounter != 3)
                {
                    return 8;
                }
            }

            if (DropCounter != 3)
            {
                return 9;
            }

            {
                stack mut System.Collections.Stack<Resource> experimentalDrops = new();
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
                    return 10;
                }

                experimentalDrops.Clear();
                if (DropCounter != 12)
                {
                    return 11;
                }
            }

            if (DropCounter != 12)
            {
                return 12;
            }

            {
                stack mut System.Collections.Stack<Resource> scopedDrops = new();
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
                    return 13;
                }
            }

            if (DropCounter != 25)
            {
                return 14;
            }

            return 0;
        }
        """;

    private const string PromotedQueueParityProgram = """
        import System.Collections
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;
        static mut i32[min max] Drops1 = 0;
        static mut i32[min max] Drops2 = 0;
        static mut i32[min max] Drops3 = 0;
        static mut i32[min max] Drops4 = 0;
        static mut i32[min max] Drops5 = 0;
        static mut i32[min max] Drops6 = 0;
        static mut i32[min max] Drops7 = 0;

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
            if (value == 1) { Drops1 += 1; }
            if (value == 2) { Drops2 += 1; }
            if (value == 3) { Drops3 += 1; }
            if (value == 4) { Drops4 += 1; }
            if (value == 5) { Drops5 += 1; }
            if (value == 6) { Drops6 += 1; }
            if (value == 7) { Drops7 += 1; }
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

        noinline fn void WriteResource(out Resource output)
        {
            stack mut Resource produced = new Resource() { Value = 8 };
            output = produced;
            return;
        }

        export fn i32[min max] main()
        {
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> experimental = new();

            if (!Ok(stable.Reserve(0)) || !Ok(experimental.Reserve(0)))
            {
                return 1;
            }

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i)))
                {
                    return 2;
                }

                if (stable.Peek() != experimental.Peek())
                {
                    return 3;
                }
            }

            if (stable.Count() != experimental.Count() || stable.IsEmpty() != experimental.IsEmpty())
            {
                return 4;
            }

            {
                stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> wrapped = new();
                if (!Ok(wrapped.Reserve(0)) || !Ok(wrapped.Reserve(8))) { return 17; }
                for willexit (stack mut u8[0 8] i = 0; i < 8; i += 1)
                {
                    if (!Ok(wrapped.Enqueue(i))) { return 18; }
                }
                stack mut u32[0 2 ** 31 - 1] value = 0;
                for willexit (stack mut u8[0 6] i = 0; i < 6; i += 1)
                {
                    if (!wrapped.TryDequeue(value) || value != i) { return 19; }
                }
                if (!Ok(wrapped.Reserve(0)) || !Ok(wrapped.Reserve(6))) { return 20; }
                for willexit (stack mut u8[0 20] i = 8; i < 20; i += 1)
                {
                    if (!Ok(wrapped.Enqueue(i))) { return 21; }
                }
                if (!TooLarge(wrapped.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))) || wrapped.Count() != 14)
                {
                    return 22;
                }
                for willexit (stack mut u8[0 20] i = 6; i < 20; i += 1)
                {
                    if (!wrapped.TryDequeue(value) || value != i) { return 23; }
                }
                if (!wrapped.IsEmpty()) { return 24; }
            }

            stack mut i64[min max] checksum = 0;
            while willexit (!experimental.IsEmpty())
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue))
                {
                    return 5;
                }

                if (stableValue != experimentalValue)
                {
                    return 6;
                }

                checksum += (i64[min max])stableValue;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty() || checksum != 8128)
            {
                return 7;
            }

            if (!TooLarge(stable.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))) || !TooLarge(experimental.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))))
            {
                return 8;
            }

            {
                stack mut System.Collections.Queue<Resource> stableDrops = new();
                if (!Ok(stableDrops.Enqueue(new Resource()
                {
                    Value = 1
                }
                )) || !Ok(stableDrops.Enqueue(new Resource()
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
                stack mut System.Collections.Queue<Resource> experimentalDrops = new();
                if (!Ok(experimentalDrops.Enqueue(new Resource()
                {
                    Value = 4
                }
                )) || !Ok(experimentalDrops.Enqueue(new Resource()
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
                stack mut System.Collections.Queue<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.Enqueue(new Resource()
                {
                    Value = 6
                }
                )) || !Ok(scopedDrops.Enqueue(new Resource()
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

            {
                stack mut System.Collections.Queue<Resource> growingDrops = new();
                if (!Ok(growingDrops.Enqueue(new Resource() { Value = 1 }))
                    || !Ok(growingDrops.Enqueue(new Resource() { Value = 2 }))
                    || !Ok(growingDrops.Enqueue(new Resource() { Value = 3 }))
                    || !Ok(growingDrops.Enqueue(new Resource() { Value = 4 }))
                    || !Ok(growingDrops.Enqueue(new Resource() { Value = 5 })))
                {
                    return 25;
                }
                growingDrops.Clear();
                if (DropCounter != 40)
                {
                    return 26;
                }
            }
            if (DropCounter != 40)
            {
                return 27;
            }

            Drops1 = 0;
            Drops2 = 0;
            Drops3 = 0;
            Drops4 = 0;
            Drops5 = 0;
            Drops6 = 0;
            Drops7 = 0;
            {
                stack mut System.Collections.Queue<Resource> ring = new();
                if (!Ok(ring.Enqueue(new Resource() { Value = 1 }))
                    || !Ok(ring.Enqueue(new Resource() { Value = 2 }))
                    || !Ok(ring.Enqueue(new Resource() { Value = 3 }))
                    || !Ok(ring.Enqueue(new Resource() { Value = 4 })))
                {
                    return 28;
                }
                stack mut Resource first = new Resource() { Value = 0 };
                stack mut Resource second = new Resource() { Value = 0 };
                if (!ring.TryDequeue(first) || !ring.TryDequeue(second)
                    || first.Value != 1 || second.Value != 2)
                {
                    return 29;
                }
                if (!Ok(ring.Enqueue(new Resource() { Value = 5 }))
                    || !Ok(ring.Enqueue(new Resource() { Value = 6 }))
                    || !Ok(ring.Enqueue(new Resource() { Value = 7 })))
                {
                    return 30;
                }
                if (ring.Peek().Value != 3 || ring.Count() != 5)
                {
                    return 31;
                }
                ring.Clear();
                if (DropCounter != 65 || Drops1 != 0 || Drops2 != 0
                    || Drops3 != 1 || Drops4 != 1 || Drops5 != 1 || Drops6 != 1 || Drops7 != 1)
                {
                    return 32;
                }
            }
            if (DropCounter != 68 || Drops1 != 1 || Drops2 != 1
                || Drops3 != 1 || Drops4 != 1 || Drops5 != 1 || Drops6 != 1 || Drops7 != 1)
            {
                return 33;
            }
            {
                stack mut Resource output = new Resource() { Value = 0 };
                WriteResource(output);
                if (output.Value != 8)
                {
                    return 34;
                }
            }
            if (DropCounter != 76)
            {
                return 35;
            }
            {
                stack mut Resource output = new Resource() { Value = 0 };
                stack fnptr<fn void(out Resource)> writer = WriteResource;
                writer(output);
                if (output.Value != 8)
                {
                    return 36;
                }
            }
            if (DropCounter != 84)
            {
                return 37;
            }
            {
                stack mut System.Collections.Queue<Resource> empty = new();
                stack mut Resource output = new Resource() { Value = 9 };
                if (empty.TryDequeue(output) || output.Value != 9)
                {
                    return 38;
                }
            }
            if (DropCounter != 93)
            {
                return 39;
            }

            return 0;
        }
        """;

    private const string PromotedRingQueueCandidateProgram = """
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
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> ring = new();

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
            {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i)))
                {
                    return 1;
                }
            }

            if (ring.Peek() != 0)
            {
                return 14;
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1)
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue))
                {
                    return 2;
                }

                if (stableValue != ringValue)
                {
                    return 3;
                }

                checksum += (i64[min max])stableValue;
            }

            for willexit (stack mut u8[0 128] i = 64; i < 128; i += 1)
            {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i)))
                {
                    return 4;
                }
            }

            if (stable.Count() != ring.Count() || ring.Capacity() < ring.Count())
            {
                return 5;
            }

            if (ring.Peek() != 32)
            {
                return 15;
            }

            while willexit (!ring.IsEmpty())
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue))
                {
                    return 6;
                }

                if (stableValue != ringValue)
                {
                    return 7;
                }

                checksum += (i64[min max])stableValue;
            }

            if (!stable.IsEmpty() || !ring.IsEmpty() || checksum != 8128)
            {
                return 8;
            }

            {
                stack mut System.Collections.RingQueue<Resource> ringDrops = new();
                if (!Ok(ringDrops.Enqueue(new Resource()
                {
                    Value = 1
                }
                )) || !Ok(ringDrops.Enqueue(new Resource()
                {
                    Value = 2
                }
                )))
                {
                    return 9;
                }

                ringDrops.Clear();
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
                stack mut System.Collections.RingQueue<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.Enqueue(new Resource()
                {
                    Value = 4
                }
                )) || !Ok(scopedDrops.Enqueue(new Resource()
                {
                    Value = 5
                }
                )))
                {
                    return 12;
                }
            }

            if (DropCounter != 12)
            {
                return 13;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourcePromotedCollectionReservesUseSparseSlotStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedCollectionReserveLowering.stark");
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

                fn bool GrowCollections()
                {
                    stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> queue = new();
                    stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    return Ok(queue.Reserve(32)) && Ok(dictionary.Reserve(32));
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);

        var ringQueueReserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_RingQueue_Reserve__u32_0_2147483647(");
        var sparseReserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_SparseSlots_ReserveRing__u32_0_2147483647(");
        var dictionaryStorageBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_Dictionary_GrowStorage__u32_0_2147483647__u32_0_2147483647(");

        Assert.Contains("SparseSlots_ReserveRing__u32", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Allocate(", sparseReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", sparseReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueSlot", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("%slot_addedSlots", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.memmove", sparseReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Allocate(", dictionaryStorageBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", dictionaryStorageBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryValueSlot", dictionaryStorageBody, StringComparison.Ordinal);
        Assert.DoesNotContain("%slot_nextValueSlots", dictionaryStorageBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", dictionaryStorageBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibPromotedStackMatchesStableStackExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-stack-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedStackParityProgram);

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
    public async Task SourceStdLibPromotedQueueMatchesStableQueueExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-queue-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedQueueParityProgram);

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
    public async Task SourceStdLibPromotedRingQueueCandidateExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-ring-queue-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedRingQueueCandidateProgram);

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
    public void PromotedQueueTryDequeueUsesSparseSlotRingPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkPath = Path.Combine(repositoryRoot, "benchmarks", "collections", "QueueChurn.stark");
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
        var tryDequeueBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Queue_TryDequeue__u32(");

        var sparseMoveBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef i32 @__stark_mono_fn_System_Collections__System_Collections_SparseSlots_MoveAt__u32(");

        var tryDequeueUsesSparseMove =
            tryDequeueBody.Contains("SparseSlots_MoveAt__u32", StringComparison.Ordinal)
            || (tryDequeueBody.Contains("getelementptr i32", StringComparison.Ordinal)
                && tryDequeueBody.Contains("load i32", StringComparison.Ordinal)
                && tryDequeueBody.Contains("store i32", StringComparison.Ordinal));
        Assert.True(tryDequeueUsesSparseMove, tryDequeueBody);
        Assert.Contains("store i32", tryDequeueBody, StringComparison.Ordinal);
        Assert.Contains("getelementptr i32", sparseMoveBody, StringComparison.Ordinal);
        Assert.Contains("load i32", sparseMoveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Collections_QueueSlot_u32_", tryDequeueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_move_at", tryDequeueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.memmove", tryDequeueBody, StringComparison.Ordinal);
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
