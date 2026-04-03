using Stark.Compiler;

namespace compiler.Tests;

public sealed class StandardLibraryTests
{
    [Fact]
    public void StdLibSourceGraphIncludesMilestone7ModuleLayout()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(systemPath), systemPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);

        Assert.True(moduleGraph.ContainsLoadedModule("System"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Console"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.IO"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.IO.File"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.IO.Path"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Text"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Buffer"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Linux"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Windows"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Syscall"));
        Assert.False(moduleGraph.ContainsLoadedModule("System.IO.Stdout"));
        Assert.False(moduleGraph.ContainsLoadedModule("System.IO.Stderr"));
    }

    [Fact]
    public async Task StdLibPackageBuildsFromRepositorySources()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-build-");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var manifestPath = Path.Combine(tempDirectory.FullName, Path.GetFileNameWithoutExtension(libraryPath) + ".starkpkg.json");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var modules = manifest.RootElement.GetProperty("Modules").EnumerateArray().ToArray();

            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.Console");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.IO");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.IO.File");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.IO.Path");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.Syscall");
            Assert.Contains(modules, module => module.GetProperty("ModuleName").GetString() == "System.Text");
            Assert.DoesNotContain(modules, module => module.GetProperty("ModuleName").GetString() == "System.Runtime.Buffer");

            var rootModule = modules.Single(module => module.GetProperty("ModuleName").GetString() == "System");
            var reExports = rootModule.GetProperty("ReExports").EnumerateArray().Select(static item => item.GetProperty("ModuleName").GetString()).ToArray();
            Assert.Contains("System.Console", reExports);
            Assert.Contains("System.IO", reExports);
            Assert.Contains("System.Text", reExports);

            var ioModule = modules.Single(module => module.GetProperty("ModuleName").GetString() == "System.IO");
            var ioReExports = ioModule.GetProperty("ReExports").EnumerateArray().Select(static item => item.GetProperty("ModuleName").GetString()).ToArray();
            Assert.Contains("System.IO.File", ioReExports);
            Assert.Contains("System.IO.Path", ioReExports);

            var ioTypes = ioModule.GetProperty("Types").EnumerateArray().Select(static item => item.GetProperty("Name").GetString()).ToArray();
            Assert.Contains("IOError", ioTypes);
            Assert.Contains("IOResult", ioTypes);
            Assert.Contains("IOStatus", ioTypes);

            var fileModule = modules.Single(module => module.GetProperty("ModuleName").GetString() == "System.IO.File");
            var fileTypes = fileModule.GetProperty("Types").EnumerateArray().Select(static item => item.GetProperty("Name").GetString()).ToArray();
            Assert.Contains("FileBuffering", fileTypes);
            Assert.Contains("FileMode", fileTypes);
            Assert.Contains("File", fileTypes);

            var fileType = fileModule.GetProperty("Types").EnumerateArray()
                .Single(type => type.GetProperty("Name").GetString() == "File");
            Assert.True(fileType.TryGetProperty("Destructor", out var fileDestructor));
            Assert.True(fileDestructor.GetProperty("IsMutable").GetBoolean());
            Assert.Equal("{self.Close();}", fileDestructor.GetProperty("BodyText").GetString());
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
    public void StdLibSourceConsoleSupportsAsciiAndUnicodeOverloads()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibConsoleUnicodeSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn void Use() {
                    System.Console.Write("ascii");
                    System.Console.Write((unicode)"ascii");
                    System.Console.WriteLine("line");
                    System.Console.WriteLine((unicode)"line");
                    System.Console.WriteError("error");
                    System.Console.WriteError((unicode)"error");
                    System.Console.WriteErrorLine("error-line");
                    System.Console.WriteErrorLine((unicode)"error-line");
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceRawFileHandlesSupportAsciiAndUnicodeWriteOverloads()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibFileUnicodeSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn void Use() {
                    stack rawptr<i8> handle = System.IO.File.OpenWrite("demo.txt");
                    System.IO.File.WriteText(handle, "ascii");
                    System.IO.File.WriteText(handle, (unicode)"ascii");
                    System.IO.File.WriteLine(handle, "line");
                    System.IO.File.WriteLine(handle, (unicode)"line");
                    System.IO.File.Close(handle);
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPathCallerBufferSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn i32 Use() {
                    stack mut Ascii owned = new Ascii() {
                        Data = null,
                        Length = 0,
                        Capacity = 0
                    };
                    stack mut i8[64] joinBuffer = {
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    };
                    stack mut Ascii joined = new Ascii() {
                        Data = &joinBuffer[0],
                        Length = 0,
                        Capacity = 64
                    };

                    stack rawptr<i8> asciiData = System.Text.AsciiData("demo");
                    stack i64 asciiLength = System.Text.AsciiLength("demo");
                    stack rawptr<i32> unicodeData = System.Text.UnicodeData((unicode)"demo");
                    stack i64 unicodeLength = System.Text.UnicodeLength((unicode)"demo");
                    stack bool status = System.IO.Path.CurrentDirectory(&owned);
                    stack bool joinedOk = System.IO.Path.TryJoin(&joined, "demo", "file.txt");
                    stack ascii extension = System.IO.Path.Extension("demo/file.txt");
                    stack ascii baseName = System.IO.Path.BaseName("demo/file.txt");
                    stack ascii directory = System.IO.Path.DirectoryName("demo/file.txt");

                    if (asciiData == null || unicodeData == null) {
                        return 1;
                    }

                    if (asciiLength != 4 || unicodeLength != 4) {
                        return 2;
                    }

                    if (status) {
                        return 3;
                    }

                    if (!joinedOk) {
                        return 4;
                    }

                    if (System.Text.AsciiLength(extension) == 0
                        || System.Text.AsciiLength(baseName) == 0
                        || System.Text.AsciiLength(directory) == 0) {
                        return 4;
                    }

                    return 0;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceRuntimeBufferModuleSupportsLinearAndRingOperations()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibRuntimeBufferSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Runtime.Buffer
                module Demo

                fn i32 Use() {
                    stack mut System.Runtime.Buffer.ByteBuffer512 linear = new System.Runtime.Buffer.ByteBuffer512();
                    stack rawmutptr<i8> writePtr = linear.WritePointer();
                    if (writePtr == null) {
                        return 1;
                    }

                    *writePtr = (i8)65;
                    linear.AdvanceWrite(1);

                    stack rawptr<i8> readPtr = linear.ReadPointer();
                    if (readPtr == null || *readPtr != (i8)65) {
                        return 2;
                    }

                    linear.AdvanceRead(1);
                    if (!linear.IsEmpty()) {
                        return 3;
                    }

                    stack mut System.Runtime.Buffer.RingBuffer512 ring = new System.Runtime.Buffer.RingBuffer512();
                    stack mut i8 value = 0;
                    if (!ring.TryPushByte((i8)66)) {
                        return 4;
                    }

                    if (!ring.TryPopByte(&value) || value != (i8)66) {
                        return 5;
                    }

                    return 0;
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourceCurrentDirectoryUsesSyscallBackedLinuxPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define i64 @LinuxSyscall2PathBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall2PathBuffer(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@getcwd(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@strlen(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceConsoleAsciiWritesUseSyscallBackedLinuxPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define fastcc i32 @WriteAsciiToHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @WriteAsciiToHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("inttoptr i8 1 to ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("inttoptr i8 2 to ptr", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceConsoleUnicodeWritesUseSyscallBackedLinuxPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define fastcc i32 @WriteUnicodeToHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fputws(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxFileOperationsUseSyscallBackedPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define fastcc ptr @OpenFileRead(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc i32 @CloseFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc i64 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc i64 @WriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4OpenAt(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall1Handle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3AtPath(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4RenameAt(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fopen(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fclose(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fread(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fwrite(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@remove(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@rename(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxFileExistsUsesStatSyscallPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define i64 @LinuxSyscall4StatAt(", llvm, StringComparison.Ordinal);

        var functionStart = llvm.IndexOf("define fastcc i1 @FileExists(", StringComparison.Ordinal);
        Assert.True(functionStart >= 0, "Expected FileExists definition in emitted LLVM.");
        var functionEnd = llvm.IndexOf("\n}\n", functionStart, StringComparison.Ordinal);
        Assert.True(functionEnd > functionStart, "Expected to capture the FileExists function body.");
        var functionBody = llvm.Substring(functionStart, functionEnd - functionStart);

        Assert.Contains("call i64 @LinuxSyscall4StatAt(", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@OpenFileRead(", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@CloseFile(", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@stat(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fstatat(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceLinuxTerminalDetectionUsesIoctlSyscallPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define i64 @LinuxSyscall3HandleRequestPointer(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc i1 @IsTerminal(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleRequestPointer(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@isatty(", llvm, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SystemSyscallArchitectureCases))]
    public void SystemSyscallModuleSelectsExpectedLinuxShimPerArchitecture(string targetTriple, string expectedInlineAsm)
    {
        var repositoryRoot = FindRepositoryRoot();
        var syscallPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Syscall.stark");
        var source = File.ReadAllText(syscallPath);
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(source, syscallPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(targetTriple, null)));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains(expectedInlineAsm, llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagedStdLibCanBeConsumedWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-app-");
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

                export ffi fn i32 main() {
                    stack mut i8[16] asciiBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

                    stack mut Ascii ownedAscii = new Ascii() {
                        Data = &asciiBuffer[0],
                        Length = 0,
                        Capacity = 16
                    };

                    stack Unicode ownedUnicode = new Unicode() {
                        Data = null,
                        Length = 0,
                        Capacity = 4
                    };

                    stack System.Text.Encoding encoding = System.Text.Encoding.UTF8;
                    stack System.IO.IOStatus status = System.IO.IOStatus.Ok;
                    if (ownedAscii.Capacity != 16) {
                        return 1;
                    }

                    if (ownedUnicode.Capacity != 4) {
                        return 2;
                    }

                    if (!System.Text.TryConcatAscii(&ownedAscii, "Stark", " IO")) {
                        return 3;
                    }

                    stack Ascii fileAscii = new Ascii() {
                        Data = ownedAscii.Data,
                        Length = ownedAscii.Length,
                        Capacity = ownedAscii.Capacity
                    };

                    stack Ascii consoleAscii = new Ascii() {
                        Data = ownedAscii.Data,
                        Length = ownedAscii.Length,
                        Capacity = ownedAscii.Capacity
                    };

                    stack rawptr<i8> handle = System.IO.File.OpenWrite("io-test.txt");
                    System.IO.File.WriteLine(handle, System.Text.AsciiView(fileAscii));
                    System.IO.File.Close(handle);

                    System.Console.WriteLine(System.Text.AsciiView(consoleAscii));
                    System.Console.WriteLine(System.IO.Path.DirectorySeparator());

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
            Assert.Equal("Stark IO\n/\n", processStdout);
            Assert.Equal(string.Empty, processStderr);
            Assert.Equal("Stark IO\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "io-test.txt")));
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
    public async Task PackagedStdLibConsoleReturnsIoStatusWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-console-status-");
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

                export ffi fn i32 main() {
                    if (!IsOk(System.Console.Write("Console"))) {
                        return 1;
                    }

                    if (!IsOk(System.Console.WriteLine(" Status"))) {
                        return 2;
                    }

                    if (!IsOk(System.Console.WriteErrorLine("stderr works"))) {
                        return 3;
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
            Assert.Equal("Console Status\n", processStdout);
            Assert.Equal("stderr works\n", processStderr);
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
    public async Task PackagedStdLibUnicodeConsoleAndRawFileWritesWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-unicode-io-");
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

                export ffi fn i32 main() {
                    stack rawptr<i8> handle = System.IO.File.OpenWrite("unicode.txt");
                    if (handle == null) {
                        return 1;
                    }

                    System.IO.File.WriteLine(handle, (unicode)"File \u03B1");
                    if (System.IO.File.Close(handle) != 0) {
                        return 2;
                    }

                    switch (System.Console.WriteLine((unicode)"Console \u03B1")) {
                        case System.IO.IOStatus.Ok:
                            return 0;
                        case System.IO.IOStatus.Err(var error):
                            return 3;
                    }
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
            Assert.Equal("Console α\n", processStdout);
            Assert.Equal(string.Empty, processStderr);
            Assert.Equal("File α\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "unicode.txt")));
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
    public async Task PackagedStdLibOwnedFileHandleFlushesAndClosesOnDrop()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-owned-file-");
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

                fn void WriteOwned() {
                    stack mut System.IO.File.File file = System.IO.File.Open("owned-test.txt", System.IO.File.FileMode.Write);
                    file.WriteLine("Owned");
                    return;
                }

                export ffi fn i32 main() {
                    WriteOwned();

                    if (!System.IO.File.Exists("owned-test.txt")) {
                        return 2;
                    }

                    if (System.IO.File.Exists("missing-test.txt")) {
                        return 3;
                    }

                    stack mut i8[8] buffer = { 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack rawptr<i8> handle = System.IO.File.OpenRead("owned-test.txt");
                    stack i64 count = System.IO.File.ReadBytes(&buffer[0], 1, 6, handle);
                    System.IO.File.Close(handle);

                    if (count != 6) {
                        return 4;
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
            Assert.Equal("Owned\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "owned-test.txt")));
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
    public async Task PackagedStdLibFileBufferingModesBehaveAsExpected()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-buffering-");
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

                fn i64 ReadCount(ascii path, i64 expected) {
                    stack mut i8[16] buffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack rawptr<i8> handle = System.IO.File.OpenRead(path);
                    stack i64 count = System.IO.File.ReadBytes(&buffer[0], 1, expected, handle);
                    System.IO.File.Close(handle);
                    return count;
                }

                export ffi fn i32 main() {
                    stack mut System.IO.File.File defaulted = System.IO.File.Open("default.txt", System.IO.File.FileMode.Write);
                    defaulted.WriteLine("Default");
                    if (ReadCount("default.txt", 8) != 0) {
                        return 1;
                    }

                    if (defaulted.Close() != 0) {
                        return 2;
                    }

                    if (ReadCount("default.txt", 8) != 8) {
                        return 3;
                    }

                    stack mut System.IO.File.File full = System.IO.File.Open("full.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Full);
                    full.WriteLine("Full");
                    if (ReadCount("full.txt", 5) != 0) {
                        return 4;
                    }

                    if (full.Flush() != 0) {
                        return 5;
                    }

                    if (ReadCount("full.txt", 5) != 5) {
                        return 6;
                    }

                    if (full.Close() != 0) {
                        return 7;
                    }

                    stack mut System.IO.File.File line = System.IO.File.Open("line.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line);
                    line.WriteLine("Line");
                    if (ReadCount("line.txt", 5) != 5) {
                        return 8;
                    }

                    if (line.Close() != 0) {
                        return 9;
                    }

                    stack mut System.IO.File.File none = System.IO.File.Open("none.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.None);
                    none.WriteText("None");
                    if (ReadCount("none.txt", 4) != 4) {
                        return 10;
                    }

                    if (none.Close() != 0) {
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
            Assert.Equal("Default\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "default.txt")));
            Assert.Equal("Full\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "full.txt")));
            Assert.Equal("Line\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "line.txt")));
            Assert.Equal("None", await File.ReadAllTextAsync(Path.Combine(appDirectory, "none.txt")));
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
    public async Task PackagedStdLibFileMoveDeleteAndExistsRoundTrip()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-move-delete-");
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

                export ffi fn i32 main() {
                    stack rawptr<i8> handle = System.IO.File.OpenWrite("before.txt");
                    if (handle == null) {
                        return 1;
                    }

                    System.IO.File.WriteLine(handle, "Move me");
                    if (System.IO.File.Close(handle) != 0) {
                        return 2;
                    }

                    if (!System.IO.File.Exists("before.txt")) {
                        return 3;
                    }

                    if (System.IO.File.Move("before.txt", "after.txt") != 0) {
                        return 4;
                    }

                    if (System.IO.File.Exists("before.txt")) {
                        return 5;
                    }

                    if (!System.IO.File.Exists("after.txt")) {
                        return 6;
                    }

                    if (System.IO.File.Delete("after.txt") != 0) {
                        return 7;
                    }

                    if (System.IO.File.Exists("after.txt")) {
                        return 8;
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
            Assert.False(File.Exists(Path.Combine(appDirectory, "before.txt")));
            Assert.False(File.Exists(Path.Combine(appDirectory, "after.txt")));
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
    public async Task SourceRuntimePlatformTerminalDetectionSeesRedirectedStdoutAsNonTerminal()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-runtime-terminal-detect-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Platform
                module App

                export ffi fn i32 main() {
                    if (System.Runtime.Platform.IsTerminal((rawptr<i8>)1)) {
                        return 1;
                    }

                    if (System.Runtime.Platform.IsTerminal((rawptr<i8>)2)) {
                        return 2;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath],
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
                WorkingDirectory = tempDirectory.FullName,
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
    public async Task PackagedStdLibPathCurrentDirectoryFillsCallerProvidedAsciiBuffer()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-current-directory-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");
        var zeroBytes = string.Join(", ", Enumerable.Repeat("0", 256));

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                module App

                export ffi fn i32 main() {
                    stack mut i8[256] buffer = { {{zeroBytes}} };
                    stack mut Ascii owned = new Ascii() {
                        Data = &buffer[0],
                        Length = 0,
                        Capacity = 256
                    };

                    if (!System.IO.Path.CurrentDirectory(&owned)) {
                        return 1;
                    }

                    if (owned.Length <= 0) {
                        return 2;
                    }

                    stack System.IO.IOStatus status = System.Console.WriteLine(System.Text.AsciiView(owned));
                    switch (status) {
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

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-path-helpers-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");
        var zeroBytes = string.Join(", ", Enumerable.Repeat("0", 64));

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                module App

                fn bool IsJoinedPath(ascii value) {
                    switch (value) {
                        case "alpha/beta.txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsTextExtension(ascii value) {
                    switch (value) {
                        case ".txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsBetaBaseName(ascii value) {
                    switch (value) {
                        case "beta":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsAlphaDirectory(ascii value) {
                    switch (value) {
                        case "alpha":
                            return true;
                        default:
                            return false;
                    }
                }

                export ffi fn i32 main() {
                    stack mut i8[64] buffer = { {{zeroBytes}} };
                    stack mut Ascii joined = new Ascii() {
                        Data = &buffer[0],
                        Length = 0,
                        Capacity = 64
                    };

                    if (!System.IO.Path.TryJoin(&joined, "alpha", "beta.txt")) {
                        return 1;
                    }

                    stack Ascii joinedPath = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsJoinedPath(System.Text.AsciiView(joinedPath))) {
                        return 2;
                    }

                    stack Ascii joinedExtension = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsTextExtension(System.IO.Path.Extension(System.Text.AsciiView(joinedExtension)))) {
                        return 3;
                    }

                    stack Ascii joinedBaseName = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsBetaBaseName(System.IO.Path.BaseName(System.Text.AsciiView(joinedBaseName)))) {
                        return 4;
                    }

                    stack Ascii joinedDirectory = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsAlphaDirectory(System.IO.Path.DirectoryName(System.Text.AsciiView(joinedDirectory)))) {
                        return 5;
                    }

                    if (!System.IO.Path.TryJoin(&joined, "alpha/", "/beta.txt")) {
                        return 6;
                    }

                    stack Ascii joinedNormalized = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsJoinedPath(System.Text.AsciiView(joinedNormalized))) {
                        return 7;
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
    public async Task PackagedStdLibLinuxArchiveHasNoLibcSymbolReferences()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var nmPath = FindFirstAvailableTool("nm", "llvm-nm");
        if (nmPath is null)
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-nm-");
        var libraryPath = Path.Combine(tempDirectory.FullName, "libSystem.a");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(libraryPath));

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = nmPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add("-A");
            startInfo.ArgumentList.Add(libraryPath);

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);

            var nmStdout = await process.StandardOutput.ReadToEndAsync();
            var nmStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, nmStderr);

            Assert.DoesNotContain(" U fopen", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fclose", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fflush", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fread", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fwrite", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fputs", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fputws", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U getcwd", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U remove", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U rename", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U strlen", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U memcpy", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U memmove", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U memset", nmStdout, StringComparison.Ordinal);
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
    public async Task PackagedStdLibSyscallModuleCanBeConsumedWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-syscall-");
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
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Syscall
                module App

                export ffi fn i32 main() {
                    if (System.Syscall.Syscall0(39) <= 0) {
                        return 1;
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
    public async Task SourceRuntimeBufferModuleCanExecuteLinearAndRingOperations()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var bufferPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Runtime", "Buffer.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-runtime-buffer-");
        var runtimeDirectory = Path.Combine(tempDirectory.FullName, "System", "Runtime");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
        Directory.CreateDirectory(runtimeDirectory);

        try
        {
            File.Copy(bufferPath, Path.Combine(runtimeDirectory, "Buffer.stark"));

            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Buffer
                module App

                export ffi fn i32 main() {
                    stack mut System.Runtime.Buffer.ByteBuffer512 linear = new System.Runtime.Buffer.ByteBuffer512();
                    if (linear.Capacity() != 512 || linear.Readable() != 0 || linear.Writable() != 512) {
                        return 1;
                    }

                    stack rawmutptr<i8> writePtr = linear.WritePointer();
                    if (writePtr == null) {
                        return 2;
                    }

                    *writePtr = (i8)65;
                    linear.AdvanceWrite(1);

                    stack rawptr<i8> readPtr = linear.ReadPointer();
                    if (readPtr == null || *readPtr != (i8)65) {
                        return 3;
                    }

                    linear.AdvanceRead(1);
                    if (!linear.IsEmpty()) {
                        return 4;
                    }

                    stack mut System.Runtime.Buffer.RingBuffer512 ring = new System.Runtime.Buffer.RingBuffer512();
                    stack mut i8 value = 0;

                    if (!ring.TryPushByte((i8)66) || !ring.TryPushByte((i8)67)) {
                        return 5;
                    }

                    if (!ring.TryPopByte(&value) || value != (i8)66) {
                        return 6;
                    }

                    if (!ring.TryPopByte(&value) || value != (i8)67) {
                        return 7;
                    }

                    for willexit (stack mut i64 i = 0; i < 1024; i += 1) {
                        if (!ring.TryPushByte((i8)90)) {
                            return 8;
                        }

                        if (!ring.TryPopByte(&value) || value != (i8)90) {
                            return 9;
                        }
                    }

                    if (!ring.IsEmpty()) {
                        return 10;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-o", outputPath],
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
                WorkingDirectory = tempDirectory.FullName,
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root for stdlib integration tests.");
    }

    private static string? FindFirstAvailableTool(params string[] toolNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var toolName in toolNames)
            {
                var candidate = Path.Combine(directory, toolName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void AssertCompilerLogsEmitted(string text)
    {
        Assert.Equal(string.Empty, text);
    }

    public static IEnumerable<object[]> SystemSyscallArchitectureCases()
    {
        yield return
        [
            "x86_64-unknown-linux-gnu",
            "call i64 asm sideeffect \"syscall\", \"={rax},0,{rdi},{rsi},{rdx},{r10},{r8},{r9},~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}\""
        ];
        yield return
        [
            "aarch64-unknown-linux-gnu",
            "call i64 asm sideeffect \"svc #0\", \"={x0},{x8},0,{x1},{x2},{x3},{x4},{x5},~{memory}\""
        ];
        yield return
        [
            "riscv64-unknown-linux-gnu",
            "call i64 asm sideeffect \"ecall\", \"={a0},{a7},0,{a1},{a2},{a3},{a4},{a5},~{memory}\""
        ];
        yield return
        [
            "i386-unknown-linux-gnu",
            "call i64 asm sideeffect \"int $$0x80\", \"={eax},0,{ebx},{ecx},{edx},{esi},{edi},{ebp},~{memory},~{dirflag},~{fpsr},~{flags}\""
        ];
        yield return
        [
            "arm-unknown-linux-gnueabihf",
            "call i64 asm sideeffect \"svc #0\", \"={r0},{r7},0,{r1},{r2},{r3},{r4},{r5},~{memory}\""
        ];
    }
}
