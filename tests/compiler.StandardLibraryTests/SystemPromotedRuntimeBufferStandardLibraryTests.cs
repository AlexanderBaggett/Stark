using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemPromotedRuntimeBufferStandardLibraryTests : StandardLibraryTestSuite
{
    private const string PromotedRuntimeBufferProgram = """
        import System.Runtime.Buffer
        import System.Memory
        module App

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

        fn i64[min max] SumBytes(borrow i8[min max][] values, u64[0 2 ** 63 - 1] count)
        {
            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1)
            {
                checksum += (i64[min max])values[index];
            }

            return checksum;
        }

        fn bool WriteFirstWritable(
            mut borrow System.Runtime.Buffer.FixedByteBuffer512 buffer,
            i8[min max] value,
            u64[0 2 ** 63 - 1] expectedWritable)
            {
                if (buffer.Writable() != expectedWritable)
            {
                return false;
            }

            buffer.WriteSlice()[0] = value;
            return true;
        }

        fn bool IncrementFirstReadable(mut borrow System.Runtime.Buffer.FixedByteBuffer512 buffer)
        {
            if (buffer.Readable() == 0)
            {
                return false;
            }

            buffer.ReadMutableSlice()[0] += 1;
            return true;
        }

        export fn i32[min max] main()
        {
            stack mut System.Runtime.Buffer.FixedByteBuffer512 fixedBuffer = new();
            stack mut i8[min max][8] source =
            {
                1, 2, 3, 4, 5, 6, 7, 8
            };

            if (fixedBuffer.Capacity() != 512 || !fixedBuffer.IsEmpty() || fixedBuffer.Writable() != 512)
            {
                return 1;
            }

            if (!Ok(fixedBuffer.WriteSlice(source, 8)))
            {
                return 2;
            }

            if (fixedBuffer.Readable() != 8 || fixedBuffer.Writable() != 504)
            {
                return 3;
            }

            stack i8[min max][] initial = fixedBuffer.ReadSlice();
            if (SumBytes(initial, 8) != 36)
            {
                return 4;
            }

            fixedBuffer.AdvanceRead(3);
            if (fixedBuffer.Readable() != 5)
            {
                return 5;
            }

            fixedBuffer.Compact();
            if (fixedBuffer.Readable() != 5 || fixedBuffer.Writable() != 507)
            {
                return 6;
            }

            stack i8[min max][] compacted = fixedBuffer.ReadSlice();
            if (compacted[0] != 4 || compacted[4] != 8)
            {
                return 7;
            }

            if (!Ok(fixedBuffer.WriteFill(9, 507)) || !fixedBuffer.IsFull())
            {
                return 8;
            }

            if (!TooLarge(fixedBuffer.WriteByte(1)))
            {
                return 9;
            }

            fixedBuffer.AdvanceRead(512);
            if (!fixedBuffer.IsEmpty() || fixedBuffer.Writable() != 512)
            {
                return 10;
            }

            if (!Ok(fixedBuffer.WriteFill(5, 4)))
            {
                return 11;
            }

            if (!WriteFirstWritable(fixedBuffer, 6, 508))
            {
                return 12;
            }

            fixedBuffer.AdvanceWrite(1);
            if (!IncrementFirstReadable(fixedBuffer))
            {
                return 13;
            }

            stack i8[min max][] manual = fixedBuffer.ReadSlice();
            if (fixedBuffer.Readable() != 5 || manual[0] != 6 || manual[4] != 6)
            {
                return 14;
            }

            stack u64[0 2 ** 63 - 1] aliasedFixedCount = fixedBuffer.Readable();
            stack i8[min max][] aliasedFixedSource = fixedBuffer.ReadSlice();
            if (!Ok(fixedBuffer.WriteSlice(aliasedFixedSource, aliasedFixedCount)))
            {
                return 32;
            }

            stack i8[min max][] duplicatedFixed = fixedBuffer.ReadSlice();
            if (fixedBuffer.Readable() != 10 || duplicatedFixed[5] != duplicatedFixed[0] || duplicatedFixed[9] != duplicatedFixed[4])
            {
                return 33;
            }

            fixedBuffer.Clear();
            if (!fixedBuffer.IsEmpty() || fixedBuffer.Readable() != 0 || fixedBuffer.Writable() != 512)
            {
                return 15;
            }

            fixedBuffer.Compact();
            if (!fixedBuffer.IsEmpty() || fixedBuffer.Readable() != 0 || fixedBuffer.Writable() != 512)
            {
                return 34;
            }

            stack mut System.Runtime.Buffer.DynamicByteBuffer dynamicBuffer = new();
            if (!Ok(dynamicBuffer.Reserve(16)) || dynamicBuffer.Capacity() < 16)
            {
                return 16;
            }

            if (!Ok(dynamicBuffer.WriteByte(10)))
            {
                return 17;
            }

            if (!Ok(dynamicBuffer.WriteSlice(source, 8)))
            {
                return 18;
            }

            if (!Ok(dynamicBuffer.WriteFill(2, 4)))
            {
                return 19;
            }

            if (dynamicBuffer.Length() != 13 || dynamicBuffer.Readable() != 13)
            {
                return 20;
            }

            stack mut i8[min max] popped = 0;
            if (!dynamicBuffer.TryReadByte(popped) || popped != 10)
            {
                return 21;
            }

            dynamicBuffer.AdvanceRead(4);
            dynamicBuffer.Compact();

            if (dynamicBuffer.Length() != 8 || dynamicBuffer.Readable() != 8)
            {
                return 22;
            }

            stack i8[min max][] remaining = dynamicBuffer.ReadSlice();
            if (remaining[0] != 5 || remaining[3] != 8 || remaining[7] != 2)
            {
                return 23;
            }

            stack u64[0 2 ** 63 - 1] aliasedDynamicCount = dynamicBuffer.Readable();
            stack i8[min max][] aliasedDynamicSource = dynamicBuffer.ReadSlice();
            if (!Ok(dynamicBuffer.WriteSlice(aliasedDynamicSource, aliasedDynamicCount)))
            {
                return 30;
            }

            stack i8[min max][] duplicatedDynamic = dynamicBuffer.ReadSlice();
            if (dynamicBuffer.Readable() != 16 || duplicatedDynamic[8] != duplicatedDynamic[0] || duplicatedDynamic[15] != duplicatedDynamic[7])
            {
                return 31;
            }

            if (!TooLarge(dynamicBuffer.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))))
            {
                return 24;
            }

            dynamicBuffer.Clear();
            if (!dynamicBuffer.IsEmpty() || dynamicBuffer.Length() != 0)
            {
                return 25;
            }

            dynamicBuffer.Compact();
            if (!dynamicBuffer.IsEmpty() || dynamicBuffer.Length() != 0)
            {
                return 36;
            }

            stack mut System.Runtime.Buffer.FixedByteBuffer4096 fixed4096 = new();
            if (!Ok(fixed4096.WriteFill(3, 4096)) || !fixed4096.IsFull())
            {
                return 26;
            }

            fixed4096.Compact();
            if (!fixed4096.IsFull() || fixed4096.Readable() != 4096)
            {
                return 35;
            }

            stack mut System.Runtime.Buffer.FixedByteBuffer8192 fixed8192 = new();
            if (!Ok(fixed8192.WriteByte(4)) || fixed8192.Readable() != 1)
            {
                return 27;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourcePromotedRuntimeBufferSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedRuntimeBufferSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Runtime
                import System.Runtime.Buffer
                import System.Memory
                module Demo

                fn System.Memory.MemoryStatus FillFixed(
                    mut borrow System.Runtime.Buffer.FixedByteBuffer512 buffer,
                    i8[min max] value,
                    u64[0 2 ** 63 - 1] count)
                    {
                        return buffer.WriteFill(value, count);
                }

                fn System.Memory.MemoryStatus AppendDynamic(
                    mut borrow System.Runtime.Buffer.DynamicByteBuffer buffer,
                    borrow i8[min max][] source,
                    u64[0 2 ** 63 - 1] count)
                    {
                        return buffer.WriteSlice(source, count);
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("System_Runtime_Buffer_FixedByteBuffer512_WriteFill", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Buffer_DynamicByteBuffer_WriteSlice", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedRuntimeBufferModuleLowersRuntimeDisjointWriteFastPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Runtime", "Buffer.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var writeSliceBody = ExtractLlvmFunctionBody(llvm, "@WriteSliceToStorage(");
        var dynamicWriteSliceBody = ExtractLlvmFunctionBody(llvm, "@DynamicByteBuffer_WriteSlice(");

        Assert.Contains("icmp ule ptr", writeSliceBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", dynamicWriteSliceBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedRuntimeBufferModuleUsesTailRegionMemoryHelpers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Runtime", "Buffer.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var dynamicWriteSliceBody = ExtractLlvmFunctionBody(llvm, "@DynamicByteBuffer_WriteSlice(");
        var dynamicWriteFillBody = ExtractLlvmFunctionBody(llvm, "@DynamicByteBuffer_WriteFill(");

        Assert.True(
            dynamicWriteSliceBody.Contains("@__stark_inline_clone_System_Memory_InitializeBytesDisjoint", StringComparison.Ordinal)
            || dynamicWriteSliceBody.Contains("@llvm.memcpy.p0.p0.i64", StringComparison.Ordinal),
            "Expected DynamicByteBuffer.WriteSlice to use a tail-region copy helper or a direct memcpy.");
        Assert.Contains("@llvm.memset.p0.i64", dynamicWriteFillBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@DynamicByteBuffer_WriteByte", dynamicWriteFillBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedRuntimeBufferFixedBuffersUseInlineStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Runtime", "Buffer.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("%FixedByteBuffer512 = type { [512 x i8], i64, i64 }", llvm, StringComparison.Ordinal);
        Assert.Contains("%FixedByteBuffer4096 = type { [4096 x i8], i64, i64 }", llvm, StringComparison.Ordinal);
        Assert.Contains("%FixedByteBuffer8192 = type { [8192 x i8], i64, i64 }", llvm, StringComparison.Ordinal);

        var fixedBodies = string.Join(
            Environment.NewLine,
            [
                ExtractLlvmFunctionBody(llvm, "@WriteSliceToStorage("),
                ExtractLlvmFunctionBody(llvm, "@WriteFillToStorage("),
                ExtractLlvmFunctionBody(llvm, "@CompactStorage("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer512_WriteByte("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer512_WriteFill("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer512_Compact("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer4096_WriteByte("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer4096_WriteFill("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer4096_Compact("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer8192_WriteByte("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer8192_WriteFill("),
                ExtractLlvmFunctionBody(llvm, "@FixedByteBuffer8192_Compact(")
            ]);

        Assert.DoesNotContain("__stark_dynamic", fixedBodies, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_alloc", fixedBodies, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_try_alloc", fixedBodies, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_realloc", fixedBodies, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_try_realloc", fixedBodies, StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicByteBuffer", fixedBodies, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibPromotedRuntimeBufferExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(PromotedRuntimeBufferProgram, "stark-stdlib-promoted-runtime-buffer-");
    }

    private async Task AssertSourceExecutableRunsAsync(string source, string tempPrefix)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory(tempPrefix);
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, source);

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

    private static string ExtractLlvmFunctionBody(string llvm, string functionSignatureFragment)
    {
        var signatureIndex = llvm.IndexOf(functionSignatureFragment, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Unable to find LLVM function fragment '{functionSignatureFragment}'.");

        var start = llvm.LastIndexOf("\ndefine ", signatureIndex, StringComparison.Ordinal);
        if (start < 0)
        {
            start = llvm.LastIndexOf("define ", signatureIndex, StringComparison.Ordinal);
        }

        Assert.True(start >= 0, $"Unable to find LLVM function start for '{functionSignatureFragment}'.");

        var next = llvm.IndexOf("\ndefine ", signatureIndex + functionSignatureFragment.Length, StringComparison.Ordinal);
        if (next < 0)
        {
            next = llvm.Length;
        }

        return llvm[start..next];
    }
}
