using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOPathPackagedStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task PackagedStdLibPathCurrentDirectoryFillsCallerProvidedAsciiBuffer()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-current-directory-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");
        try
        {
            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                import System.Text
                module App

                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
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

                export fn i32[min max] main()
                {
                    stack mut System.Text.OwnedAscii owned = new();

                    if (!MemoryOk(System.IO.Path.CurrentDirectory(owned)))
                    {
                        return 1;
                    }

                    if (owned.Length() <= 0)
                    {
                        return 2;
                    }

                    stack System.IO.IOStatus status = System.Console.WriteLine(owned.View());
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return 0;
                        case System.IO.IOStatus.Err(var error):
                            return 3;
                    }

                    return 4;
                }
                """);

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
            Assert.Equal(appDirectory + "\n", processStdout);
            Assert.Equal(string.Empty, processStderr);
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
    public async Task PackagedStdLibPathHelpersWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-path-helpers-");
        var packageDirectory = await SharedStdlibPackage.GetDirectoryAsync();
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(appDirectory);

        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");
        try
        {
            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                import System.Text
                module App

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

                fn bool IsJoinedPath(ascii value)
                {
                    switch (value)
                    {
                        case "alpha/beta.txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsTextExtension(ascii value)
                {
                    switch (value)
                    {
                        case ".txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsBetaBaseName(ascii value)
                {
                    switch (value)
                    {
                        case "beta":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsAlphaDirectory(ascii value)
                {
                    switch (value)
                    {
                        case "alpha":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsOwnedJoinedPath(System.Memory.MemoryResult<System.Text.OwnedAscii> result)
                {
                    switch (result)
                    {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == 14 && IsJoinedPath(value.View());
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                export unsafe fn i32[min max] main()
                {
                    stack mut System.Text.OwnedAscii joined = new();

                    if (!MemoryOk(System.IO.Path.TryJoin(joined, "alpha", "beta.txt")))
                    {
                        return 1;
                    }

                    if (!IsJoinedPath(joined.View()))
                    {
                        return 2;
                    }

                    if (!IsTextExtension(System.IO.Path.Extension(joined.View())))
                    {
                        return 3;
                    }

                    if (!IsBetaBaseName(System.IO.Path.BaseName(joined.View())))
                    {
                        return 4;
                    }

                    if (!IsAlphaDirectory(System.IO.Path.DirectoryName(joined.View())))
                    {
                        return 5;
                    }

                    stack System.IO.Path.PathFacts facts = System.IO.Path.GetFacts(joined.View());
                    if (!IsTextExtension(facts.Extension()))
                    {
                        return 10;
                    }

                    if (!IsBetaBaseName(facts.BaseName()))
                    {
                        return 11;
                    }

                    if (!IsAlphaDirectory(facts.DirectoryName()))
                    {
                        return 12;
                    }

                    if (facts.PathLength() != 14 || facts.ExtensionLength() != 4 || facts.BaseNameLength() != 4 || facts.DirectoryNameLength() != 5)
                    {
                        return 13;
                    }

                    if (!MemoryOk(System.IO.Path.TryJoin(joined, "alpha/", "/beta.txt")))
                    {
                        return 6;
                    }

                    if (!IsJoinedPath(joined.View()))
                    {
                        return 7;
                    }

                    if (!IsOwnedJoinedPath(System.IO.Path.Join("alpha", "beta.txt")))
                    {
                        return 8;
                    }

                    if (!IsOwnedJoinedPath(System.IO.Path.Join("alpha/", "/beta.txt")))
                    {
                        return 9;
                    }

                    return 0;
                }
                """);

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
    public async Task PackagedStdLibWindowsUnicodePathsCurrentDirectoryAndOwnedUnicodeWritesWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || !OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-windows-unicode-path-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        var workingDirectory = Path.Combine(appDirectory, "unicode-\u03B1-\u65E5");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(workingDirectory);

        var libraryPath = Path.Combine(packageDirectory, "System.lib");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app.exe");

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
                $$"""
                import System
                import System.Text
                module App

                fn bool StatusOk(System.IO.IOStatus status)
                {
                    switch (status)
                    {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result)
                {
                    switch (result)
                    {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
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
                    stack mut System.Text.OwnedAscii cwd = new();
                    stack mut i8[min max][12] ownedNameBytes =
                    {
                        111, 119, 110, 101, 100, 45, -50, -79, 46, 116, 120, 116
                    };
                    stack mut Ascii ownedName = new Ascii()
                    {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    stack mut i8[min max][14] renamedNameBytes =
                    {
                        114, 101, 110, 97, 109, 101, 100, 45, -50, -78, 46, 116, 120, 116
                    };
                    stack mut Ascii renamedName = new Ascii()
                    {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    stack mut i8[min max][13] deleteNameBytes =
                    {
                        100, 101, 108, 101, 116, 101, 45, -50, -77, 46, 116, 120, 116
                    };
                    stack mut Ascii deleteName = new Ascii()
                    {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };

                    if (!MemoryOk(System.IO.Path.CurrentDirectory(cwd)))
                    {
                        return 1;
                    }

                    stack mut System.IO.File.File cwdFile = OpenOrEmpty(System.IO.File.Open("cwd.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line));
                    cwdFile.WriteLine(cwd.View());
                    if (!StatusOk(cwdFile.Close()))
                    {
                        return 3;
                    }

                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open(System.Text.AsciiView(ownedName), System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line));
                    file.WriteLine((unicode)"Owned");
                    if (!StatusOk(file.Close()))
                    {
                        return 4;
                    }

                    ownedName = new Ascii()
                    {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    if (!BoolOrFalse(System.IO.File.Exists(System.Text.AsciiView(ownedName))))
                    {
                        return 5;
                    }

                    ownedName = new Ascii()
                    {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    renamedName = new Ascii()
                    {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    if (!StatusOk(System.IO.File.Move(System.Text.AsciiView(ownedName), System.Text.AsciiView(renamedName))))
                    {
                        return 6;
                    }

                    renamedName = new Ascii()
                    {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    if (!BoolOrFalse(System.IO.File.Exists(System.Text.AsciiView(renamedName))))
                    {
                        return 7;
                    }

                    stack mut System.IO.File.File deleteFile = OpenOrEmpty(System.IO.File.Open(System.Text.AsciiView(deleteName), System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line));
                    deleteFile.WriteLine("Delete");
                    if (!StatusOk(deleteFile.Close()))
                    {
                        return 9;
                    }

                    deleteName = new Ascii()
                    {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };
                    if (!StatusOk(System.IO.File.Delete(System.Text.AsciiView(deleteName))))
                    {
                        return 10;
                    }

                    deleteName = new Ascii()
                    {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };
                    if (BoolOrFalse(System.IO.File.Exists(System.Text.AsciiView(deleteName))))
                    {
                        return 11;
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

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = workingDirectory,
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
            Assert.Equal(
                workingDirectory + "\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "cwd.txt"), System.Text.Encoding.UTF8));
            Assert.Equal(
                "Owned\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "renamed-\u03B2.txt"), System.Text.Encoding.UTF8));
            Assert.False(File.Exists(Path.Combine(workingDirectory, "owned-\u03B1.txt")));
            Assert.False(File.Exists(Path.Combine(workingDirectory, "delete-\u03B3.txt")));
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
