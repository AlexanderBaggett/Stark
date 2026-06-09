using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemFileSystemStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task SourceStdLibFileSystemMetadataTempWalkAndLineReadingWorkOnLinux()
    {
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

            fn bool MemoryOk(System.Memory.MemoryStatus status)
            {
                switch (status)
                {
                    case System.Memory.MemoryStatus.Ok:
                        return true;
                    case System.Memory.MemoryStatus.Err(var error):
                        return false;
                }
            }

            fn bool TextEquals(mut borrow System.Text.OwnedAscii text, ascii expected)
            {
                return System.Text.OwnedAsciiRangeEqualsAscii(text, 0, text.Length(), expected);
            }

            fn System.IO.IOStatus VisitPath(ascii path, System.FileSystem.FileSystemEntryKind kind)
            {
                if (System.Text.AsciiLength(path) <= 0)
                {
                    return System.IO.IOStatus.Err(System.IO.IOError.InvalidPath);
                }

                return System.IO.IOStatus.Ok;
            }

            fn void Cleanup(
                mut borrow System.Text.OwnedAscii root,
                mut borrow System.Text.OwnedAscii child,
                mut borrow System.Text.OwnedAscii nested,
                mut borrow System.Text.OwnedAscii nestedChild,
                mut borrow System.Text.OwnedAscii lines)
            {
                System.IO.File.Delete(lines.View());
                System.IO.File.Delete(nestedChild.View());
                System.IO.File.Delete(child.View());
                System.FileSystem.DeleteDirectory(nested.View());
                System.FileSystem.DeleteDirectory(root.View());
            }

            fn i32[min max] CheckMetadata(System.IO.IOResult<System.FileSystem.FileMetadata> result)
            {
                switch (result)
                {
                    case System.IO.IOResult<System.FileSystem.FileMetadata>.Err(var error):
                        return 10;
                    case System.IO.IOResult<System.FileSystem.FileMetadata>.Ok(var metadata):
                        if (metadata.Kind != System.FileSystem.FileSystemEntryKind.File)
                        {
                            return 11;
                        }

                        if (metadata.Size != 3)
                        {
                            return 12;
                        }

                        if (metadata.ModifiedUnixSeconds <= 0)
                        {
                            return 13;
                        }

                        if (metadata.Permissions == 0)
                        {
                            return 14;
                        }

                        return 0;
                }
            }

            fn i32[min max] ExpectLine(
                mut borrow System.IO.File.File file,
                mut borrow System.Text.OwnedAscii line,
                ascii expected)
            {
                switch (file.ReadLine(line))
                {
                    case System.IO.File.FileLineReadResult.Line:
                        if (!TextEquals(line, expected))
                        {
                            return 20;
                        }

                        return 0;
                    case System.IO.File.FileLineReadResult.End:
                        return 21;
                    case System.IO.File.FileLineReadResult.Err(var error):
                        return 22;
                }
            }

            fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result)
            {
                switch (result)
                {
                    case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                        return value;
                    case System.IO.IOResult<System.IO.File.File>.Err(var error):
                        return new();
                }
            }

            export unsafe fn i32[min max] main()
            {
                stack mut System.Text.OwnedAscii root = new();
                switch (System.FileSystem.CreateTempDirectory("stark-fs-meta-"))
                {
                    case System.IO.IOResult<System.Text.OwnedAscii>.Err(var createError):
                        return 1;
                    case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var value):
                        root = value;
                }

                stack mut System.Text.OwnedAscii child = new();
                stack mut System.Text.OwnedAscii nested = new();
                stack mut System.Text.OwnedAscii nestedChild = new();
                stack mut System.Text.OwnedAscii lines = new();

                if (!MemoryOk(System.IO.Path.TryJoin(child, root.View(), "child.txt"))
                    || !MemoryOk(System.IO.Path.TryJoin(nested, root.View(), "nested"))
                    || !MemoryOk(System.IO.Path.TryJoin(nestedChild, nested.View(), "nested.txt"))
                    || !MemoryOk(System.IO.Path.TryJoin(lines, root.View(), "lines.txt")))
                    {
                        Cleanup(root, child, nested, nestedChild, lines);
                        return 2;
                }

                if (!IsOk(System.IO.File.WriteAllText(child.View(), "abc")))
                {
                    Cleanup(root, child, nested, nestedChild, lines);
                    return 3;
                }

                stack i32[min max] metadataResult = CheckMetadata(System.FileSystem.Metadata(child.View()));
                if (metadataResult != 0)
                {
                    Cleanup(root, child, nested, nestedChild, lines);
                    return metadataResult;
                }

                if (!IsOk(System.FileSystem.CreateDirectory(nested.View()))
                    || !IsOk(System.IO.File.WriteAllText(nestedChild.View(), "nested")))
                    {
                        Cleanup(root, child, nested, nestedChild, lines);
                        return 4;
                }

                if (!IsOk(System.FileSystem.WalkRecursive(root.View(), VisitPath)))
                {
                    Cleanup(root, child, nested, nestedChild, lines);
                    return 5;
                }

                if (!IsOk(System.IO.File.WriteAllText(lines.View(), "alpha\nbeta\r\ngamma")))
                {
                    Cleanup(root, child, nested, nestedChild, lines);
                    return 6;
                }

                stack mut System.IO.File.File reader =
                    OpenOrEmpty(System.IO.File.Open(lines.View(), System.IO.File.FileMode.Read));
                stack mut System.Text.OwnedAscii line = new();
                stack i32[min max] first = ExpectLine(reader, line, "alpha");
                stack i32[min max] second = ExpectLine(reader, line, "beta");
                stack i32[min max] third = ExpectLine(reader, line, "gamma");
                switch (reader.ReadLine(line))
                {
                    case System.IO.File.FileLineReadResult.Line:
                        Cleanup(root, child, nested, nestedChild, lines);
                        return 7;
                    case System.IO.File.FileLineReadResult.Err(var readError):
                        Cleanup(root, child, nested, nestedChild, lines);
                        return 8;
                    case System.IO.File.FileLineReadResult.End:
                }

                reader.Close();
                Cleanup(root, child, nested, nestedChild, lines);

                if (first != 0)
                {
                    return first;
                }

                if (second != 0)
                {
                    return second;
                }

                if (third != 0)
                {
                    return third;
                }

                return 0;
            }
            """,
            "stark-stdlib-filesystem-metadata-",
            skipWindows: true);
    }

    [Fact]
    public async Task SourceStdLibFileSystemGlobStreamsRecursiveMatchesOnLinux()
    {
        await AssertSourceExecutableRunsAsync(
            """
            import System
            import System.Text
            module App

            static mut i32[min max] MatchCount = 0;
            static mut bool SawDirect = false;
            static mut bool SawChild = false;

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

            fn bool MemoryOk(System.Memory.MemoryStatus status)
            {
                switch (status)
                {
                    case System.Memory.MemoryStatus.Ok:
                        return true;
                    case System.Memory.MemoryStatus.Err(var error):
                        return false;
                }
            }

            fn void ResetGlobState()
            {
                MatchCount = 0;
                SawDirect = false;
                SawChild = false;
                return;
            }

            fn System.IO.IOStatus VisitStark(ascii path, System.FileSystem.FileSystemEntryKind kind)
            {
                if (kind != System.FileSystem.FileSystemEntryKind.File)
                {
                    return System.IO.IOStatus.Err(System.IO.IOError.InvalidPath);
                }

                if (System.IO.Path.GlobMatches("**/direct.stark", path))
                {
                    SawDirect = true;
                }
                else if (System.IO.Path.GlobMatches("**/child.stark", path))
                {
                    SawChild = true;
                }
                else
                {
                    return System.IO.IOStatus.Err(System.IO.IOError.InvalidPath);
                }

                MatchCount += 1;
                return System.IO.IOStatus.Ok;
            }

            fn System.IO.IOStatus VisitDirectOnly(ascii path, System.FileSystem.FileSystemEntryKind kind)
            {
                if (kind != System.FileSystem.FileSystemEntryKind.File)
                {
                    return System.IO.IOStatus.Err(System.IO.IOError.InvalidPath);
                }

                if (!System.IO.Path.GlobMatches("**/direct.stark", path))
                {
                    return System.IO.IOStatus.Err(System.IO.IOError.InvalidPath);
                }

                MatchCount += 1;
                return System.IO.IOStatus.Ok;
            }

            fn void Cleanup(
                mut borrow System.Text.OwnedAscii root,
                mut borrow System.Text.OwnedAscii direct,
                mut borrow System.Text.OwnedAscii nested,
                mut borrow System.Text.OwnedAscii child,
                mut borrow System.Text.OwnedAscii ignored)
            {
                System.IO.File.Delete(ignored.View());
                System.IO.File.Delete(child.View());
                System.IO.File.Delete(direct.View());
                System.FileSystem.DeleteDirectory(nested.View());
                System.FileSystem.DeleteDirectory(root.View());
            }

            export unsafe fn i32[min max] main()
            {
                stack mut System.Text.OwnedAscii root = new();
                switch (System.FileSystem.CreateTempDirectory("stark-fs-glob-"))
                {
                    case System.IO.IOResult<System.Text.OwnedAscii>.Err(var createError):
                        return 1;
                    case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var value):
                        root = value;
                }

                stack mut System.Text.OwnedAscii direct = new();
                stack mut System.Text.OwnedAscii nested = new();
                stack mut System.Text.OwnedAscii child = new();
                stack mut System.Text.OwnedAscii ignored = new();

                if (!MemoryOk(System.IO.Path.TryJoin(direct, root.View(), "direct.stark"))
                    || !MemoryOk(System.IO.Path.TryJoin(nested, root.View(), "nested"))
                    || !MemoryOk(System.IO.Path.TryJoin(child, nested.View(), "child.stark"))
                    || !MemoryOk(System.IO.Path.TryJoin(ignored, nested.View(), "ignored.txt")))
                    {
                        Cleanup(root, direct, nested, child, ignored);
                        return 2;
                }

                if (!IsOk(System.IO.File.WriteAllText(direct.View(), "module Direct\n"))
                    || !IsOk(System.FileSystem.CreateDirectory(nested.View()))
                    || !IsOk(System.IO.File.WriteAllText(child.View(), "module Child\n"))
                    || !IsOk(System.IO.File.WriteAllText(ignored.View(), "ignore\n")))
                    {
                        Cleanup(root, direct, nested, child, ignored);
                        return 3;
                }

                ResetGlobState();
                if (!IsOk(System.FileSystem.Glob(root.View(), "**/*.stark", VisitStark)))
                {
                    Cleanup(root, direct, nested, child, ignored);
                    return 4;
                }

                if (MatchCount != 2 || !SawDirect || !SawChild)
                {
                    Cleanup(root, direct, nested, child, ignored);
                    return 5;
                }

                ResetGlobState();
                if (!IsOk(System.FileSystem.Glob(root.View(), "*.stark", VisitDirectOnly)))
                {
                    Cleanup(root, direct, nested, child, ignored);
                    return 6;
                }

                if (MatchCount != 1)
                {
                    Cleanup(root, direct, nested, child, ignored);
                    return 7;
                }

                Cleanup(root, direct, nested, child, ignored);
                return 0;
            }
            """,
            "stark-stdlib-filesystem-glob-",
            skipWindows: true);
    }

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
