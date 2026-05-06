using Stark.Compiler;

namespace compiler.IntegrationTests;

public sealed class ExamplesCompileRunTests
{
    [Fact]
    public async Task HelloExampleCompilesAndRunsWithStdlibPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-hello-");
        var stdlibPackageDirectory = Path.Combine(tempDirectory.FullName, "stdlib");
        Directory.CreateDirectory(stdlibPackageDirectory);

        var stdlibSource = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var stdlibLibrary = Path.Combine(stdlibPackageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var helloSource = Path.Combine(repositoryRoot, "examples", "hello.stark");
        var helloOutput = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "hello.exe" : "hello");

        try
        {
            await BuildStdlibPackageAsync(stdlibSource, stdlibLibrary);
            var compileResult = await CompileExecutableAsync(helloSource, helloOutput, stdlibPackageDirectory);

            Assert.True(File.Exists(helloOutput));
            Assert.Contains("Emitted executable:", compileResult.Stdout);

            var processResult = await RunNativeExecutableAsync(helloOutput);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("Hello, World!\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task MultiModuleExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-multi-");
        var appOutput = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "multi-module", "App.stark"),
                appOutput);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(appOutput);

            Assert.Equal(7, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BasicSyntaxExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-basic-syntax-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "basic-syntax.exe" : "basic-syntax");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "basic-syntax", "BasicSyntax.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task TypeSystemExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-type-system-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "type-system.exe" : "type-system");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "type-system", "TypeSystem.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ModulesExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-modules-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "modules.exe" : "modules");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "modules", "App.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BorrowingExamplesCompileAndRun()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-borrowing-");
        try
        {
            foreach (var exampleName in new[] { "Borrowing", "OwnershipMoves", "BorrowKinds", "OutParameters" })
            {
                var outputPath = Path.Combine(
                    tempDirectory.FullName,
                    OperatingSystem.IsWindows() ? $"{exampleName}.exe" : exampleName);

                var result = await CompileExecutableAsync(
                    Path.Combine(repositoryRoot, "examples", "borrowing", $"{exampleName}.stark"),
                    outputPath);

                Assert.Contains("Emitted executable:", result.Stdout);

                var processResult = await RunNativeExecutableAsync(outputPath);

                Assert.Equal(0, processResult.ExitCode);
                Assert.Equal(string.Empty, processResult.StandardOutput);
                Assert.Equal(string.Empty, processResult.StandardError);
            }
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task FfiExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-ffi-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "ffi.exe" : "ffi");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "ffi", "Ffi.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(7, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task StandardLibraryExampleCompilesAndRunsWithStdlibPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-stdlib-");
        var stdlibPackageDirectory = Path.Combine(tempDirectory.FullName, "stdlib");
        Directory.CreateDirectory(stdlibPackageDirectory);

        var stdlibSource = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var stdlibLibrary = Path.Combine(stdlibPackageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var sourcePath = Path.Combine(repositoryRoot, "examples", "standard-library", "StandardLibrary.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "standard-library.exe" : "standard-library");

        try
        {
            await BuildStdlibPackageAsync(stdlibSource, stdlibLibrary);
            var result = await CompileExecutableAsync(sourcePath, outputPath, stdlibPackageDirectory);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal("Standard library ready\n", processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BuildYourOwnGitExamplesInitializeWriteCommitUpdateRefListInspectAndReportStatusWithStdlibPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-git-");
        var stdlibPackageDirectory = Path.Combine(tempDirectory.FullName, "stdlib");
        var workingDirectory = Path.Combine(tempDirectory.FullName, "work");
        Directory.CreateDirectory(stdlibPackageDirectory);
        Directory.CreateDirectory(workingDirectory);

        var stdlibSource = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var stdlibLibrary = Path.Combine(stdlibPackageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var initSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Init.stark");
        var commitSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Commit.stark");
        var refSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Ref.stark");
        var objectsSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Objects.stark");
        var inspectSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Inspect.stark");
        var statusSourcePath = Path.Combine(repositoryRoot, "examples", "build-your-own-git", "Status.stark");
        var initOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-init.exe" : "git-init");
        var commitOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-commit.exe" : "git-commit");
        var refOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-ref.exe" : "git-ref");
        var objectsOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-objects.exe" : "git-objects");
        var inspectOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-inspect.exe" : "git-inspect");
        var statusOutputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "git-status.exe" : "git-status");

        try
        {
            await BuildStdlibPackageAsync(stdlibSource, stdlibLibrary);
            var initResult = await CompileExecutableAsync(initSourcePath, initOutputPath, stdlibPackageDirectory);
            var commitResult = await CompileExecutableAsync(commitSourcePath, commitOutputPath, stdlibPackageDirectory);
            var refResult = await CompileExecutableAsync(refSourcePath, refOutputPath, stdlibPackageDirectory);
            var objectsResult = await CompileExecutableAsync(objectsSourcePath, objectsOutputPath, stdlibPackageDirectory);
            var inspectResult = await CompileExecutableAsync(inspectSourcePath, inspectOutputPath, stdlibPackageDirectory);
            var statusResult = await CompileExecutableAsync(statusSourcePath, statusOutputPath, stdlibPackageDirectory);

            Assert.Contains("Emitted executable:", initResult.Stdout);
            Assert.Contains("Emitted executable:", commitResult.Stdout);
            Assert.Contains("Emitted executable:", refResult.Stdout);
            Assert.Contains("Emitted executable:", objectsResult.Stdout);
            Assert.Contains("Emitted executable:", inspectResult.Stdout);
            Assert.Contains("Emitted executable:", statusResult.Stdout);

            var initProcessResult = await RunNativeExecutableAsync(initOutputPath, workingDirectory);

            Assert.Equal(0, initProcessResult.ExitCode);
            Assert.Equal("Initialized starkgit-demo/.starkgit\n", initProcessResult.StandardOutput);
            Assert.Equal(string.Empty, initProcessResult.StandardError);
            Assert.True(Directory.Exists(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "objects")));
            Assert.True(Directory.Exists(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "refs", "heads")));
            Assert.Equal(
                "ref: refs/heads/main\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "HEAD")));

            var commitProcessResult = await RunNativeExecutableAsync(commitOutputPath, workingDirectory);

            Assert.Equal(0, commitProcessResult.ExitCode);
            Assert.Equal("Wrote demo commit object\n", commitProcessResult.StandardOutput);
            Assert.Equal(string.Empty, commitProcessResult.StandardError);
            Assert.Equal(
                "tree empty\nauthor Stark Example\n\ninitial commit\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "objects", "demo-commit")));

            var refProcessResult = await RunNativeExecutableAsync(refOutputPath, workingDirectory);

            Assert.Equal(0, refProcessResult.ExitCode);
            Assert.Equal("Updated main ref\n", refProcessResult.StandardOutput);
            Assert.Equal(string.Empty, refProcessResult.StandardError);
            Assert.Equal(
                "demo-commit\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "starkgit-demo", ".starkgit", "refs", "heads", "main")));

            var objectsProcessResult = await RunNativeExecutableAsync(objectsOutputPath, workingDirectory);

            Assert.Equal(0, objectsProcessResult.ExitCode);
            Assert.Equal("Object demo-commit\n", objectsProcessResult.StandardOutput);
            Assert.Equal(string.Empty, objectsProcessResult.StandardError);

            var inspectProcessResult = await RunNativeExecutableAsync(inspectOutputPath, workingDirectory);

            Assert.Equal(0, inspectProcessResult.ExitCode);
            Assert.Equal("Repository metadata present\n", inspectProcessResult.StandardOutput);
            Assert.Equal(string.Empty, inspectProcessResult.StandardError);

            var statusProcessResult = await RunNativeExecutableAsync(statusOutputPath, workingDirectory);

            Assert.Equal(0, statusProcessResult.ExitCode);
            Assert.Equal("Repository status clean\n", statusProcessResult.StandardOutput);
            Assert.Equal(string.Empty, statusProcessResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task NeuralNetworkExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-neural-network-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "neural-network.exe" : "neural-network");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "neural-network", "Inference.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task SimpleDatabaseExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-simple-database-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "simple-database.exe" : "simple-database");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "simple-database", "MemoryTable.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BitTorrentTrackerResponseExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-bit-torrent-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "bit-torrent.exe" : "bit-torrent");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "bit-torrent", "TrackerResponse.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BitTorrentHandshakeExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-bit-torrent-handshake-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "bit-torrent-handshake.exe" : "bit-torrent-handshake");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "bit-torrent", "Handshake.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task BreakoutCoreExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-breakout-core-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "breakout-core.exe" : "breakout-core");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "breakout", "BreakoutCore.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task RaylibStarkModulesCheckWithoutNativeExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var raylibImportDirectory = Path.Combine(repositoryRoot, "examples", "raylib");
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");

        await CheckSourceAsync(
            Path.Combine(raylibImportDirectory, "Raylib.stark"),
            raylibImportDirectory);

        await CheckSourceAsync(
            Path.Combine(repositoryRoot, "examples", "breakout", "BreakoutRaylib.stark"),
            raylibImportDirectory,
            stdlibImportDirectory);
    }

    [Fact]
    public async Task BreakoutRaylibBuildsThroughPackageOwnedNativeMetadataWithoutGraphicalExecution()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _) || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var stdlibImportDirectory = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-breakout-raylib-pkg-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var nativeIncludeDirectory = Path.Combine(packageDirectory, "native-include");
        var nativeLibraryDirectory = Path.Combine(packageDirectory, "native-libs");
        var tempsDirectory = Path.Combine(tempDirectory.FullName, "temps");
        var linkerLogPath = Path.Combine(tempDirectory.FullName, "linker.log");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(nativeIncludeDirectory);
        Directory.CreateDirectory(nativeLibraryDirectory);

        var fakeNativeSource = Path.Combine(packageDirectory, "RaylibNativeSmoke.c");
        var raylibLibrary = Path.Combine(packageDirectory, "libRaylibStark.a");
        var breakoutOutput = Path.Combine(tempDirectory.FullName, "breakout-raylib");
        var linkerPath = await CreateUnixCaptureLinkerAsync(tempDirectory.FullName, linkerLogPath);

        try
        {
            await File.WriteAllTextAsync(
                fakeNativeSource,
                """
                int stark_raylib_native_metadata_anchor(void) {
                    return 0;
                }
                """);

            var emitStdout = new StringWriter();
            var emitStderr = new StringWriter();
            var emitExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "raylib", "Raylib.stark"),
                    "--emit-lib",
                    "-I", Path.Combine(repositoryRoot, "examples", "raylib"),
                    "-o", raylibLibrary,
                    "--native-source", fakeNativeSource,
                    "--native-include-dir", nativeIncludeDirectory,
                    "--native-library-dir", nativeLibraryDirectory,
                    "--native-library", "raylib",
                    "--native-library", "GL",
                    "--native-library", "m",
                    "--native-library", "pthread",
                    "--native-library", "dl",
                    "--native-library", "rt",
                    "--native-library", "X11",
                    "--native-library", "Xrandr",
                    "--native-library", "Xi",
                    "--native-library", "Xcursor",
                    "--native-library", "Xinerama",
                    "-O0"
                ],
                new StringReader(string.Empty),
                emitStdout,
                emitStderr);

            Assert.True(emitExitCode == 0, emitStderr.ToString());
            Assert.True(File.Exists(raylibLibrary));

            var compileStdout = new StringWriter();
            var compileStderr = new StringWriter();
            var compileExitCode = await CompilerCli.RunAsync(
                [
                    Path.Combine(repositoryRoot, "examples", "breakout", "BreakoutRaylib.stark"),
                    "--emit-exe",
                    "-I", packageDirectory,
                    "-I", stdlibImportDirectory,
                    "-o", breakoutOutput,
                    "--linker", linkerPath,
                    "--save-temps", tempsDirectory,
                    "-O0"
                ],
                new StringReader(string.Empty),
                compileStdout,
                compileStderr);

            Assert.True(compileExitCode == 0, compileStderr.ToString());
            Assert.Contains("Emitted executable:", compileStdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(breakoutOutput));

            var linkerLog = await File.ReadAllTextAsync(linkerLogPath);
            Assert.Contains(Path.GetFullPath(raylibLibrary), linkerLog, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(nativeLibraryDirectory), linkerLog, StringComparison.Ordinal);
            Assert.Contains("-lraylib", linkerLog, StringComparison.Ordinal);
            Assert.Contains("-lGL", linkerLog, StringComparison.Ordinal);
            Assert.Contains("-lXinerama", linkerLog, StringComparison.Ordinal);
            Assert.Contains("native_0_RaylibNativeSmoke", linkerLog, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task DataModelExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-data-model-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "data-model.exe" : "data-model");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "data-model", "DataModel.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(15, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ArithmeticExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-arithmetic-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "arithmetic.exe" : "arithmetic");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "arithmetic", "Arithmetic.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task ControlFlowExampleCompilesAndRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-control-flow-");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "control-flow.exe" : "control-flow");

        try
        {
            var result = await CompileExecutableAsync(
                Path.Combine(repositoryRoot, "examples", "control-flow", "ControlFlow.stark"),
                outputPath);

            Assert.Contains("Emitted executable:", result.Stdout);

            var processResult = await RunNativeExecutableAsync(outputPath);

            Assert.Equal(0, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    [Fact]
    public async Task StaticLibraryExampleBuildsAndRunsFromPackage()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-examples-static-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var facadeSource = Path.Combine(repositoryRoot, "examples", "static-library", "Facade.stark");
        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var appSource = Path.Combine(appDirectory, "App.stark");
        var appOutput = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            await CompileLibraryAsync(facadeSource, libraryPath);

            await File.WriteAllTextAsync(
                appSource,
                """
                import Facade
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    return Facade.Quadruple(5);
                }
                """);

            var compileResult = await CompileExecutableAsync(appSource, appOutput, packageDirectory);
            Assert.Contains("Emitted executable:", compileResult.Stdout);

            var processResult = await RunNativeExecutableAsync(appOutput);

            Assert.Equal(20, processResult.ExitCode);
            Assert.Equal(string.Empty, processResult.StandardOutput);
            Assert.Equal(string.Empty, processResult.StandardError);
        }
        finally
        {
            Cleanup(tempDirectory);
        }
    }

    private static async Task CompileLibraryAsync(string sourcePath, string libraryPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [sourcePath, "--emit-lib", "-o", libraryPath],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(libraryPath));
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task<(string Stdout, string Stderr)> CompileExecutableAsync(
        string sourcePath,
        string outputPath,
        string? libraryDirectory = null)
    {
        var args = new List<string> { sourcePath, "--emit-exe", "-o", outputPath };
        if (libraryDirectory is not null)
        {
            args.Add("-I");
            args.Add(libraryDirectory);
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            args.ToArray(),
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        AssertCompilerLogsEmitted(stderr.ToString());
        return (stdout.ToString(), stderr.ToString());
    }

    private static async Task CheckSourceAsync(string sourcePath, params string[] importDirectories)
    {
        var args = new List<string> { sourcePath, "--check" };
        foreach (var importDirectory in importDirectories)
        {
            args.Add("-I");
            args.Add(importDirectory);
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            args.ToArray(),
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Check succeeded.", stdout.ToString());
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task BuildStdlibPackageAsync(string sourcePath, string libraryPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CompilerCli.RunAsync(
            [sourcePath, "--emit-lib", "-o", libraryPath, "-O0"],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(libraryPath));
        AssertCompilerLogsEmitted(stderr.ToString());
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunNativeExecutableAsync(
        string executablePath,
        string? workingDirectory = null)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, standardOutput, standardError);
    }

    private static async Task<string> CreateUnixCaptureLinkerAsync(string directory, string logPath)
    {
        var path = Path.Combine(directory, "capture-linker.sh");
        await File.WriteAllTextAsync(
            path,
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$@" > "{{logPath}}"
            out=""
            prev=""
            for arg in "$@"; do
              if [ "$prev" = "-o" ]; then
                out="$arg"
                break
              fi
              prev="$arg"
            done
            : > "$out"
            """);
        System.Diagnostics.Process.Start("chmod", $"+x {path}")!.WaitForExit();
        return path;
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

        throw new InvalidOperationException("Unable to locate the Stark repository root for example tests.");
    }

    private static void Cleanup(DirectoryInfo tempDirectory)
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

    private static void AssertCompilerLogsEmitted(string text)
    {
        Assert.Equal(string.Empty, text);
    }
}
