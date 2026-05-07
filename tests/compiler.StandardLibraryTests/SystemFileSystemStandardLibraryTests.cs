using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemFileSystemStandardLibraryTests : StandardLibraryTestSuite
{
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

                fn bool IsOk(System.IO.IOStatus status) {
                    switch (status) {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolValue(System.IO.IOResult<bool> result) {
                    switch (result) {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
                            return false;
                    }
                }

                fn System.IO.File.File OpenFileOrEmpty(System.IO.IOResult<System.IO.File.File> result) {
                    switch (result) {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }

                fn bool IsChildFileName(mut borrow System.FileSystem.FileSystemEntry entry) {
                    if (entry.Name.Length() != 9) {
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

                fn bool DirectoryReadResultIsChild(System.FileSystem.DirectoryReadResult next) {
                    switch (next) {
                        case System.FileSystem.DirectoryReadResult.End:
                            return false;
                        case System.FileSystem.DirectoryReadResult.Err(var error):
                            return false;
                        case System.FileSystem.DirectoryReadResult.Entry(var entry):
                            stack mut System.FileSystem.FileSystemEntry mutableEntry = entry;
                            return IsChildFileName(mutableEntry);
                    }
                }

                fn bool ContainsChildFile(ascii path) {
                    stack System.IO.IOResult<System.FileSystem.Directory> opened = System.FileSystem.OpenDirectory(path);
                    switch (opened) {
                        case System.IO.IOResult<System.FileSystem.Directory>.Err(var error):
                            return false;
                        case System.IO.IOResult<System.FileSystem.Directory>.Ok(var value):
                            stack mut System.FileSystem.Directory directory = value;
                            stack bool found = DirectoryReadResultIsChild(directory.ReadNext());
                            directory.Close();
                            return found;
                    }
                }

                export unsafe ffi fn i32[min max] main() {
                    if (!IsOk(System.FileSystem.CreateDirectory("fs-root"))) {
                        return 1;
                    }

                    if (!BoolValue(System.FileSystem.Exists("fs-root"))) {
                        return 2;
                    }

                    if (!BoolValue(System.FileSystem.IsDirectory("fs-root"))) {
                        return 3;
                    }

                    if (BoolValue(System.FileSystem.IsFile("fs-root"))) {
                        return 4;
                    }

                    stack mut System.IO.File.File child =
                        OpenFileOrEmpty(System.IO.File.Open("fs-root/child.txt", System.IO.File.FileMode.Write));
                    if (!child.IsOpen()) {
                        return 5;
                    }

                    if (!IsOk(child.WriteLine("child"))) {
                        return 6;
                    }

                    if (!IsOk(child.Close())) {
                        return 6;
                    }

                    if (!BoolValue(System.FileSystem.IsFile("fs-root/child.txt"))) {
                        return 7;
                    }

                    if (!ContainsChildFile("fs-root")) {
                        return 8;
                    }

                    if (IsOk(System.FileSystem.DeleteDirectory("fs-root"))) {
                        return 9;
                    }

                    if (!IsOk(System.IO.File.Delete("fs-root/child.txt"))) {
                        return 10;
                    }

                    if (!IsOk(System.FileSystem.DeleteDirectory("fs-root"))) {
                        return 11;
                    }

                    if (BoolValue(System.FileSystem.Exists("fs-root"))) {
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
}
