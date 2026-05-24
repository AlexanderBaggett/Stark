using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemFileSystemStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task SourceStdLibDirectoryReadNextInfoRawReportsEntryLengthsAndEnd()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await AssertSourceExecutableRunsAsync(
            """
            import System
            import System.Text
            module App

            fn bool IsOk(System.IO.IOStatus status)
            {
                switch (status)
                {
                    case System.IO.IOStatus.Ok:
                        return true;
                    case System.IO.IOStatus.Err(var error):
                        return false;
                }
            }

            fn System.IO.File.File OpenFileOrEmpty(System.IO.IOResult<System.IO.File.File> result)
            {
                switch (result)
                {
                    case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                        return value;
                    case System.IO.IOResult<System.IO.File.File>.Err(var error):
                        return new();
                }
            }

            unsafe fn bool CreateEmptyFile(ascii path)
            {
                stack mut System.IO.File.File file =
                    OpenFileOrEmpty(System.IO.File.Open(path, System.IO.File.FileMode.Write, System.IO.File.FileBuffering.None));
                if (!file.IsOpen())
                {
                    return false;
                }

                return IsOk(file.Close());
            }

            unsafe fn void Cleanup(ascii unicodePath)
            {
                System.IO.File.Delete("raw-dir-info/ascii.txt");
                System.IO.File.Delete(unicodePath);
                System.FileSystem.DeleteDirectory("raw-dir-info");
            }

            unsafe fn i32[min max] CountOpenFdEntries()
            {
                stack System.IO.IOResult<System.FileSystem.Directory> opened =
                    System.FileSystem.OpenDirectory("/proc/self/fd");
                switch (opened)
                {
                    case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                        return -1;
                    case System.IO.IOResult<System.FileSystem.Directory>.Ok(var value):
                        stack mut System.FileSystem.Directory directory = value;
                        stack mut i32[min max] count = 0;

                        for willexit (stack mut i32[min max] index = 0; index < 256; index += 1)
                        {
                            stack mut i64[min max] length = 0;
                            stack mut i32[min max] kind = 0;
                            stack i32[min max] status = directory.ReadNextInfoRaw(&length, &kind);
                            if (status == 0)
                            {
                                directory.Close();
                                return count;
                            }

                            if (status != 1)
                            {
                                directory.Close();
                                return -2;
                            }

                            count += 1;
                        }

                        directory.Close();
                        return -3;
                }
            }

            unsafe fn i32[min max] EarlyReturnAfterOneRead(ascii path)
            {
                stack System.IO.IOResult<System.FileSystem.Directory> opened = System.FileSystem.OpenDirectory(path);
                switch (opened)
                {
                    case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                        return -1;
                    case System.IO.IOResult<System.FileSystem.Directory>.Ok(var value):
                        stack mut System.FileSystem.Directory directory = value;
                        stack mut i64[min max] length = 0;
                        stack mut i32[min max] kind = 0;
                        stack i32[min max] status = directory.ReadNextInfoRaw(&length, &kind);
                        if (status == 1)
                        {
                            return 0;
                        }

                        return -2;
                }
            }

            unsafe fn i64[min max] DirectoryNameLengthChecksum(ascii path, i64[min max] expectedCount)
            {
                stack System.IO.IOResult<System.FileSystem.Directory> opened = System.FileSystem.OpenDirectory(path);
                switch (opened)
                {
                    case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                        return -1;
                    case System.IO.IOResult<System.FileSystem.Directory>.Ok(var value):
                        stack mut System.FileSystem.Directory directory = value;
                        return directory.ReadRemainingNameLengthChecksumRaw(expectedCount);
                }
            }

            export unsafe fn i32[min max] main()
            {
                stack mut i8[min max][32] unicodePathStorage =
                {
                    114, 97, 119, 45, 100, 105, 114, 45,
                    105, 110, 102, 111, 47, 119, 105, 100,
                    101, 45, -61, -87, 46, 116, 120, 116,
                    0, 0, 0, 0, 0, 0, 0, 0
                };
                stack mut Ascii unicodePath = new Ascii()
                {
                    Data = &unicodePathStorage[0],
                    Length = 24,
                    Capacity = 32
                };
                stack ascii unicodePathView = System.Text.AsciiView(unicodePath);

                stack mut System.FileSystem.Directory unopenedDirectory = new();
                stack mut i64[min max] unopenedLength = 0;
                stack mut i32[min max] unopenedKind = 0;
                if (unopenedDirectory.ReadNextInfoRaw(&unopenedLength, &unopenedKind) >= 0
                    || unopenedLength != -2
                    || unopenedKind != -1)
                    {
                        return 1;
                }

                if (unopenedDirectory.ReadRemainingNameLengthChecksumRaw(0) != -1)
                {
                    return 2;
                }

                switch (unopenedDirectory.ReadNextInfo())
                {
                    case System.FileSystem.DirectoryReadInfoResult.Entry(var unopenedEntry):
                        return 3;
                    case System.FileSystem.DirectoryReadInfoResult.End:
                        return 4;
                    case System.FileSystem.DirectoryReadInfoResult.Err(var unopenedError):
                }

                Cleanup(unicodePathView);
                if (!IsOk(System.FileSystem.CreateDirectory("raw-dir-info")))
                {
                    return 5;
                }

                if (!CreateEmptyFile("raw-dir-info/ascii.txt"))
                {
                    Cleanup(unicodePathView);
                    return 6;
                }

                if (!CreateEmptyFile(unicodePathView))
                {
                    Cleanup(unicodePathView);
                    return 7;
                }

                if (DirectoryNameLengthChecksum("raw-dir-info", 2) != 20)
                {
                    Cleanup(unicodePathView);
                    return 8;
                }

                if (DirectoryNameLengthChecksum("raw-dir-info", 1) != -1)
                {
                    Cleanup(unicodePathView);
                    return 9;
                }

                stack i32[min max] beforeFdCount = CountOpenFdEntries();
                if (beforeFdCount <= 0)
                {
                    Cleanup(unicodePathView);
                    return 10;
                }

                if (EarlyReturnAfterOneRead("raw-dir-info") != 0)
                {
                    Cleanup(unicodePathView);
                    return 11;
                }

                stack i32[min max] afterFdCount = CountOpenFdEntries();
                if (afterFdCount != beforeFdCount)
                {
                    Cleanup(unicodePathView);
                    return 12;
                }

                stack System.IO.IOResult<System.FileSystem.Directory> opened = System.FileSystem.OpenDirectory("raw-dir-info");
                switch (opened)
                {
                    case System.IO.IOResult<System.FileSystem.Directory>.Err(var openError):
                        Cleanup(unicodePathView);
                        return 13;
                    case System.IO.IOResult<System.FileSystem.Directory>.Ok(var value):
                        stack mut System.FileSystem.Directory directory = value;
                        stack mut bool sawAscii = false;
                        stack mut bool sawUnicode = false;
                        stack mut i32[min max] count = 0;

                        for willexit (stack mut i32[min max] index = 0; index < 4; index += 1)
                        {
                            stack mut i64[min max] length = 0;
                            stack mut i32[min max] kind = 0;
                            stack i32[min max] status = directory.ReadNextInfoRaw(&length, &kind);

                            if (status == 0)
                            {
                                if (!IsOk(directory.Close()))
                                {
                                    Cleanup(unicodePathView);
                                    return 14;
                                }

                                if (!IsOk(directory.Close()))
                                {
                                    Cleanup(unicodePathView);
                                    return 15;
                                }

                                stack mut i64[min max] closedLength = 0;
                                stack mut i32[min max] closedKind = 0;
                                if (directory.ReadNextInfoRaw(&closedLength, &closedKind) >= 0
                                    || closedLength != -2
                                    || closedKind != -1)
                                    {
                                        Cleanup(unicodePathView);
                                    return 16;
                                }

                                if (directory.ReadRemainingNameLengthChecksumRaw(0) != -1)
                                {
                                    Cleanup(unicodePathView);
                                    return 17;
                                }

                                switch (directory.ReadNextInfo())
                                {
                                    case System.FileSystem.DirectoryReadInfoResult.Entry(var closedEntry):
                                        Cleanup(unicodePathView);
                                        return 18;
                                    case System.FileSystem.DirectoryReadInfoResult.End:
                                        Cleanup(unicodePathView);
                                        return 19;
                                    case System.FileSystem.DirectoryReadInfoResult.Err(var closedError):
                                }

                                Cleanup(unicodePathView);
                                if (count != 2 || !sawAscii || !sawUnicode)
                                {
                                    return 20;
                                }

                                return 0;
                            }

                            if (status != 1)
                            {
                                directory.Close();
                                Cleanup(unicodePathView);
                                return 21;
                            }

                            if (kind != 1)
                            {
                                directory.Close();
                                Cleanup(unicodePathView);
                                return 22;
                            }

                            if (length == 9)
                            {
                                sawAscii = true;
                            }
                            else if (length == 11)
                            {
                                sawUnicode = true;
                            }
                            else
                            {
                                directory.Close();
                                Cleanup(unicodePathView);
                                return 23;
                            }

                            count += 1;
                        }

                        directory.Close();
                        Cleanup(unicodePathView);
                        return 24;
                }
            }
            """,
            "stark-stdlib-filesystem-raw-info-",
            skipWindows: true);
    }

    [Fact]
    public async Task PackagedStdLibFileSystemDirectoryLifecycleAndQueriesWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-filesystem-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                fn bool IsOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolValue(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenFileOrEmpty(System.IO.IOResult<System.IO.File.File> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }

                fn bool IsChildFileName(mut borrow System.FileSystem.FileSystemEntry entry)
                {
                    if (entry.Name.Length() != 9)
                    {
                        return false;
                    }

                    stack i8[min max][] view = entry.Name.AsSlice();
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

                fn bool DirectoryReadResultIsChild(System.FileSystem.DirectoryReadResult next)
                {
                    switch (next)
                    {
                        case System.FileSystem.DirectoryReadResult.End:
                            return false;
                        case System.FileSystem.DirectoryReadResult.Err(var error):
                            return false;
                        case System.FileSystem.DirectoryReadResult.Entry(var entry):
                            stack mut System.FileSystem.FileSystemEntry mutableEntry = entry;
                            return IsChildFileName(mutableEntry);
                    }
                }

                fn bool ContainsChildFile(ascii path)
                {
                    stack System.IO.IOResult<System.FileSystem.Directory> opened = System.FileSystem.OpenDirectory(path);
                    switch (opened)
                    {
                        case System.IO.IOResult<System.FileSystem.Directory>.Err(var error):
                            return false;
                        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var value):
                            stack mut System.FileSystem.Directory directory = value;
                            stack bool found = DirectoryReadResultIsChild(directory.ReadNext());
                            directory.Close();
                            return found;
                    }
                }

                export unsafe fn i32[min max] main()
                {
                    if (!IsOk(System.FileSystem.CreateDirectory("fs-root")))
                    {
                        return 1;
                    }

                    if (!BoolValue(System.FileSystem.Exists("fs-root")))
                    {
                        return 2;
                    }

                    if (!BoolValue(System.FileSystem.IsDirectory("fs-root")))
                    {
                        return 3;
                    }

                    if (BoolValue(System.FileSystem.IsFile("fs-root")))
                    {
                        return 4;
                    }

                    stack mut System.IO.File.File child =
                        OpenFileOrEmpty(System.IO.File.Open("fs-root/child.txt", System.IO.File.FileMode.Write));
                    if (!child.IsOpen())
                    {
                        return 5;
                    }

                    if (!IsOk(child.WriteLine("child")))
                    {
                        return 6;
                    }

                    if (!IsOk(child.Close()))
                    {
                        return 6;
                    }

                    if (!BoolValue(System.FileSystem.IsFile("fs-root/child.txt")))
                    {
                        return 7;
                    }

                    if (!ContainsChildFile("fs-root"))
                    {
                        return 8;
                    }

                    if (IsOk(System.FileSystem.DeleteDirectory("fs-root")))
                    {
                        return 9;
                    }

                    if (!IsOk(System.IO.File.Delete("fs-root/child.txt")))
                    {
                        return 10;
                    }

                    if (!IsOk(System.FileSystem.DeleteDirectory("fs-root")))
                    {
                        return 11;
                    }

                    if (BoolValue(System.FileSystem.Exists("fs-root")))
                    {
                        return 12;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = appDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
            Assert.False(Directory.Exists(Path.Combine(appDirectory, "fs-root")));
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
