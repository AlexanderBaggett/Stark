using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemExperimentalIOFileSystemStandardLibraryTests : StandardLibraryTestSuite
{
    private const string ExperimentalFileProgram = """
        import System.IO.File
        import System.Runtime.Buffer
        import System.IO
        import System.Memory
        module App

        fn bool StatusOk(System.IO.IOStatus status) {
            switch (status) {
                case System.IO.IOStatus.Ok:
                    return true;
                case System.IO.IOStatus.Err(var error):
                    return false;
            }
        }

        fn bool MemoryOk(System.Memory.MemoryStatus status) {
            switch (status) {
                case System.Memory.MemoryStatus.Ok:
                    return true;
                case System.Memory.MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn i64[min max] CountOrNegative(System.IO.IOResult<i64[0 max]> result) {
            switch (result) {
                case System.IO.IOResult<i64[0 max]>.Ok(var value):
                    return (i64[min max])value;
                case System.IO.IOResult<i64[0 max]>.Err(var error):
                    return -1;
            }
        }

        fn bool BoolOrFalse(System.IO.IOResult<bool> result) {
            switch (result) {
                case System.IO.IOResult<bool>.Ok(var value):
                    return value;
                case System.IO.IOResult<bool>.Err(var error):
                    return false;
            }
        }

        fn bool FileOpenFailed(System.IO.IOResult<System.IO.File.File> result) {
            switch (result) {
                case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                    return false;
                case System.IO.IOResult<System.IO.File.File>.Err(var error):
                    return true;
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut i8[-128 127][3] source = { 65, 66, 67 };
            stack System.IO.IOResult<System.IO.File.File> opened =
                System.IO.File.Open("experimental-file.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.None);
            switch (opened) {
                case System.IO.IOResult<System.IO.File.File>.Err(var error):
                    return 1;
                case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                    stack mut System.IO.File.File writer = value;
                    if (CountOrNegative(writer.Write(source)) != 3) {
                        return 2;
                    }

                    stack mut System.Runtime.Buffer.DynamicByteBuffer extra = new();
                    if (!MemoryOk(extra.WriteSlice(source, 3))) {
                        return 3;
                    }

                    if (CountOrNegative(writer.Write(extra)) != 3) {
                        return 4;
                    }

                    stack mut System.Runtime.Buffer.FixedByteBuffer512 fixedSource = new();
                    if (!MemoryOk(fixedSource.WriteSlice(source, 3))) {
                        return 5;
                    }

                    if (CountOrNegative(writer.Write(fixedSource)) != 3) {
                        return 6;
                    }

                    if (!StatusOk(writer.Close())) {
                        return 7;
                    }

                    if (StatusOk(writer.WriteLine("closed"))) {
                        return 8;
                    }
            }

            if (!BoolOrFalse(System.IO.File.Exists("experimental-file.txt"))) {
                return 9;
            }

            if (!FileOpenFailed(System.IO.File.Open("missing-experimental-file.txt", System.IO.File.FileMode.Read))) {
                return 10;
            }

            stack mut i8[-128 127][4] destination = { 0, 0, 0, 0 };
            stack System.IO.IOResult<System.IO.File.File> readerResult =
                System.IO.File.Open("experimental-file.txt", System.IO.File.FileMode.Read);
            switch (readerResult) {
                case System.IO.IOResult<System.IO.File.File>.Err(var readError):
                    return 11;
                case System.IO.IOResult<System.IO.File.File>.Ok(var readValue):
                    stack mut System.IO.File.File reader = readValue;
                    if (CountOrNegative(reader.Seek(3, System.IO.File.SeekOrigin.Begin)) != 3) {
                        return 12;
                    }

                    if (CountOrNegative(reader.Read(destination)) != 4) {
                        return 13;
                    }

                    if (CountOrNegative(reader.Seek(0, System.IO.File.SeekOrigin.Begin)) != 0) {
                        return 14;
                    }

                    stack mut System.Runtime.Buffer.FixedByteBuffer512 readBuffer = new();
                    if (!MemoryOk(readBuffer.WriteFill(0, 0))) {
                        return 15;
                    }

                    stack i64[min max] fixedReadCount = CountOrNegative(reader.Read(readBuffer.WriteSlice()));
                    if (fixedReadCount != 9) {
                        return 22;
                    }
                    readBuffer.AdvanceWrite((i64[0 max])fixedReadCount);

                    stack i8[-128 127][] buffered = readBuffer.ReadSlice();
                    if (readBuffer.Readable() != 9 || buffered[0] != 65 || buffered[8] != 67) {
                        return 21;
                    }

                    if (!StatusOk(reader.Close())) {
                        return 16;
                    }
            }

            if (destination[0] != 65 || destination[1] != 66 || destination[2] != 67
                || destination[3] != 65) {
                return 17;
            }

            if (!StatusOk(System.IO.File.Move("experimental-file.txt", "experimental-file-renamed.txt"))) {
                return 18;
            }

            if (!BoolOrFalse(System.IO.File.Exists("experimental-file-renamed.txt"))) {
                return 19;
            }

            if (!StatusOk(System.IO.File.Delete("experimental-file-renamed.txt"))) {
                return 20;
            }

            return 0;
        }
        """;

    private const string ExperimentalFileSystemProgram = """
        import System.FileSystem
        import System.IO.File
        import System.IO.Path
        import System.Text
        import System.IO
        import System.Memory
        module App

        fn bool StatusOk(System.IO.IOStatus status) {
            switch (status) {
                case System.IO.IOStatus.Ok:
                    return true;
                case System.IO.IOStatus.Err(var error):
                    return false;
            }
        }

        fn bool BoolOrFalse(System.IO.IOResult<bool> result) {
            switch (result) {
                case System.IO.IOResult<bool>.Ok(var value):
                    return value;
                case System.IO.IOResult<bool>.Err(var error):
                    return false;
            }
        }

        fn bool MemoryOk(System.Memory.MemoryStatus status) {
            switch (status) {
                case System.Memory.MemoryStatus.Ok:
                    return true;
                case System.Memory.MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool MemoryTooLarge(System.Memory.MemoryStatus status) {
            switch (status) {
                case System.Memory.MemoryStatus.Ok:
                    return false;
                case System.Memory.MemoryStatus.Err(var error):
                    switch (error) {
                        case System.Memory.MemoryError.OutOfMemory:
                            return false;
                        case System.Memory.MemoryError.TooLarge:
                            return true;
                        case System.Memory.MemoryError.InvalidLayout:
                            return false;
                        case System.Memory.MemoryError.UnsupportedAlignment:
                            return false;
                    }
            }
        }

        fn i32[min max] CheckTooLargeNormalization() {
            stack mut i8[-128 127][1] storage = { 47 };
            stack Ascii huge = new Ascii() {
                Data = &storage[0],
                Length = (i64[min max])((2**63) - 1),
                Capacity = (i64[min max])((2**63) - 1)
            };
            stack mut System.Text.OwnedAscii destination = new();
            stack ascii hugeView = System.Text.AsciiView(huge);
            if (System.Text.AsciiLength(hugeView) == 0) {
                return 1;
            }

            if (!MemoryTooLarge(System.IO.Path.TryNormalizeSeparators(destination, hugeView))) {
                return 2;
            }

            if (destination.Length() != 0) {
                return 3;
            }

            return 0;
        }

        fn bool IsChildName(mut borrow System.FileSystem.FileSystemEntry entry) {
            if (entry.Name.Length() != 9) {
                return false;
            }

            stack i8[-128 127][] view = entry.Name.AsSlice();
            return view[0] == 99
                && view[1] == 104
                && view[2] == 105
                && view[3] == 108
                && view[4] == 100
                && view[5] == 46
                && view[6] == 116
                && view[7] == 120
                && view[8] == 116;
        }

        fn bool IsChildEntry(System.FileSystem.DirectoryReadResult result) {
            switch (result) {
                case System.FileSystem.DirectoryReadResult.End:
                    return false;
                case System.FileSystem.DirectoryReadResult.Err(var error):
                    return false;
                case System.FileSystem.DirectoryReadResult.Entry(var entry):
                    stack mut System.FileSystem.FileSystemEntry mutableEntry = entry;
                    return IsChildName(mutableEntry);
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Text.OwnedAscii currentDirectory = new();
            if (!MemoryOk(System.IO.Path.CurrentDirectory(currentDirectory))
                || currentDirectory.Length() == 0) {
                return 20;
            }

            stack i32[min max] tooLarge = CheckTooLargeNormalization();
            if (tooLarge != 0) {
                return 30 + tooLarge;
            }

            if (!StatusOk(System.FileSystem.CreateDirectory("experimental-fs-root"))) {
                return 1;
            }

            stack System.IO.IOResult<System.IO.File.File> opened =
                System.IO.File.Open("experimental-fs-root/child.txt", System.IO.File.FileMode.Write);
            switch (opened) {
                case System.IO.IOResult<System.IO.File.File>.Err(var error):
                    return 2;
                case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                    stack mut System.IO.File.File writer = value;
                    if (!StatusOk(writer.WriteLine("child")) || !StatusOk(writer.Close())) {
                        return 3;
                    }
            }

            if (!BoolOrFalse(System.FileSystem.IsDirectory("experimental-fs-root"))) {
                return 4;
            }

            if (!BoolOrFalse(System.FileSystem.IsFile("experimental-fs-root/child.txt"))) {
                return 5;
            }

            stack System.IO.IOResult<System.FileSystem.Directory> directoryResult =
                System.FileSystem.OpenDirectory("experimental-fs-root");
            switch (directoryResult) {
                case System.IO.IOResult<System.FileSystem.Directory>.Err(var directoryError):
                    return 6;
                case System.IO.IOResult<System.FileSystem.Directory>.Ok(var directoryValue):
                    stack mut System.FileSystem.Directory directory = directoryValue;
                    if (!IsChildEntry(directory.ReadNext())) {
                        return 7;
                    }

                    if (!StatusOk(directory.Close())) {
                        return 8;
                    }
            }

            if (!StatusOk(System.IO.File.Delete("experimental-fs-root/child.txt"))) {
                return 9;
            }

            if (!StatusOk(System.FileSystem.DeleteDirectory("experimental-fs-root"))) {
                return 10;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceExperimentalIOFileSystemSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalIOFileSystemSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.FileSystem
                import System.IO
                import System.IO.File
                import System.IO.Path
                import System.Text
                import System.IO
                import System.Memory
                module Demo

                fn System.IO.IOResult<i64[0 max]> WriteBytes(
                    mut borrow System.IO.File.File file,
                    borrow i8[-128 127][] source) {
                    return file.Write(source);
                }

                fn System.IO.IOStatus SyncFile(mut borrow System.IO.File.File file) {
                    return file.SyncAll();
                }

                fn System.IO.IOResult<System.FileSystem.Directory> OpenDir(ascii path) {
                    return System.FileSystem.OpenDirectory(path);
                }

                fn System.FileSystem.DirectoryReadResult ReadOne(
                    mut borrow System.FileSystem.Directory directory) {
                    return directory.ReadNext();
                }

                fn System.FileSystem.DirectoryReadInfoResult ReadOneInfo(
                    mut borrow System.FileSystem.Directory directory) {
                    return directory.ReadNextInfo();
                }

                fn System.Memory.MemoryStatus CurrentDir(
                    mut borrow System.Text.OwnedAscii destination) {
                    return System.IO.Path.CurrentDirectory(destination);
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("ReadDirectoryEntry", llvm, StringComparison.Ordinal);
        Assert.Contains("ReadDirectoryEntryInfo", llvm, StringComparison.Ordinal);
        Assert.Contains("OpenDirectoryFastInto", llvm, StringComparison.Ordinal);
        Assert.Contains("System_FileSystem_Directory_ReadNextInfo", llvm, StringComparison.Ordinal);
        Assert.Contains("ReadFileBytes", llvm, StringComparison.Ordinal);
        Assert.Contains("WriteFileBytes", llvm, StringComparison.Ordinal);
        Assert.Contains("ReadFileBytesFast", llvm, StringComparison.Ordinal);
        Assert.Contains("WriteFileBytesFast", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Experimental_FileSystem_", llvm, StringComparison.Ordinal);

        var platformSource = File.ReadAllText(Path.Combine(sourceRoot, "System", "Runtime", "Platform.stark"));
        Assert.Contains("rawmutptr<i8[-128 127]>[capacity] buffer", platformSource, StringComparison.Ordinal);
        Assert.Contains("OpenDirectoryFastInto", platformSource, StringComparison.Ordinal);
        Assert.Contains("ReadFileBytes(rawmutptr<i8[-128 127]>[length] buffer", platformSource, StringComparison.Ordinal);
        Assert.Contains("WriteFileBytes(rawptr<i8[-128 127]>[length] buffer", platformSource, StringComparison.Ordinal);
        Assert.Contains("ReadFileBytesFast(rawmutptr<i8[-128 127]>[length] buffer", platformSource, StringComparison.Ordinal);
        Assert.Contains("WriteFileBytesFast(rawptr<i8[-128 127]>[length] buffer", platformSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibExperimentalFileExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(ExperimentalFileProgram, "stark-stdlib-experimental-file-", skipWindows: true);
    }

    [Fact]
    public async Task SourceStdLibExperimentalFileSystemExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(ExperimentalFileSystemProgram, "stark-stdlib-experimental-filesystem-", skipWindows: true);
    }

    private async Task AssertSourceExecutableRunsAsync(string source, string tempPrefix, bool skipWindows)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || (skipWindows && OperatingSystem.IsWindows()))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
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
}

