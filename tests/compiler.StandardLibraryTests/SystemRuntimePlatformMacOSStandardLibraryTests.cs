using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemRuntimePlatformMacOSStandardLibraryTests
{
    private const string MacOSTargetTriple = "arm64-apple-macosx26.0.0";

    [Fact]
    public void MacOSDispatchTemplateMirrorsLinuxDispatchSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var templatesRoot = Path.Combine(repositoryRoot, "stdlib", "templates");
        var linuxPath = Path.Combine(templatesRoot, "System.Runtime.Platform.LinuxDispatch.stark");
        var macOSPath = Path.Combine(templatesRoot, "System.Runtime.Platform.MacOSDispatch.stark");

        var linuxFunctions = ExtractInternalFunctionNames(File.ReadAllText(linuxPath));
        var macOSFunctions = ExtractInternalFunctionNames(File.ReadAllText(macOSPath));

        Assert.Equal(linuxFunctions, macOSFunctions);
    }

    [Fact]
    public void StdLibSourceMacOSPlatformUsesLibSystemPosixApis()
    {
        var result = CompileMacOSPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare i64 @write(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i64 @read(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @open(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @close(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @fsync(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i64 @lseek(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @stat(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @getcwd(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @opendir(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @readdir(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @pthread_create(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @pthread_join(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @pthread_detach(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @sched_yield()", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @nanosleep(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @socket(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @poll(", llvm, StringComparison.Ordinal);
        Assert.Contains("@malloc(", llvm, StringComparison.Ordinal);
        Assert.Contains("@free(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare void @exit(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @os_sync_wait_on_address(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @os_sync_wake_by_address_any(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @os_sync_wake_by_address_all(", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @exit(", llvm, StringComparison.Ordinal);
        Assert.Contains("define internal dso_local void @__stark_oom_trap(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("coldcc void @__stark_oom_trap(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain(" comdat", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain(", comdat", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("thread_local(localexec)", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@GetStdHandle(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fopen(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fread(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fwrite(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@access(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceMacOSPathMetadataUsesStatModeBits()
    {
        var result = CompileMacOSPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        var tryReadPathModeBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryReadPathMode(",
            "Expected TryReadPathMode definition in emitted LLVM.");
        var pathExistsBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @PathExists(",
            "Expected PathExists definition in emitted LLVM.");
        var isDirectoryBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @IsDirectory(",
            "Expected IsDirectory definition in emitted LLVM.");
        var isFileBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @IsFile(",
            "Expected IsFile definition in emitted LLVM.");

        Assert.Contains("call i32 @stat(", tryReadPathModeBody, StringComparison.Ordinal);
        Assert.Contains("@MacOSStatModeOffset", tryReadPathModeBody, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 @UnsignedShort(", tryReadPathModeBody, StringComparison.Ordinal);
        Assert.Contains("call fastcc i1 @TryReadPathMode(", pathExistsBody, StringComparison.Ordinal);
        Assert.Contains("@MacOSStatDirectoryType", isDirectoryBody, StringComparison.Ordinal);
        Assert.Contains("@MacOSStatRegularType", isFileBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@opendir(", tryReadPathModeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@access(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceMacOSThreadingPreservesEntryReturnCodeThroughPthreadJoin()
    {
        var result = CompileMacOSPlatformSource();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        var thunkBody = ExtractDefinedFunctionText(
            llvm,
            "define ptr @ThreadEntryThunk(",
            "Expected ThreadEntryThunk definition in emitted LLVM.");
        var joinBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i32 @JoinThread(",
            "Expected JoinThread definition in emitted LLVM.");

        Assert.Contains("inttoptr i32", thunkBody, StringComparison.Ordinal);
        Assert.Contains("call i32 @pthread_join(", joinBody, StringComparison.Ordinal);
        Assert.Contains("ptrtoint ptr", joinBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", thunkBody, StringComparison.Ordinal);
        Assert.DoesNotContain("pthread_join(ptr %arg_handle, ptr null)", joinBody, StringComparison.Ordinal);
        Assert.DoesNotContain("store i32 0, ptr %arg_exitCode", joinBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOSDispatchProcessExitCallsLibSystemExitWithoutSymbolCollision()
    {
        var result = CompileMacOSDispatchTemplate();
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("define fastcc void @ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare fastcc void @System_Runtime_Platform_MacOS_ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc void @System_Runtime_Platform_MacOS_ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("define fastcc void @System_Runtime_Platform_MacOS_ExitProcess(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("define fastcc void @exit(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceStdLibBuildRoutesPlatformCallsThroughMacOSModuleForMacOSTargets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var targetInfo = new LlvmTargetInfo(MacOSTargetTriple, null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Runtime.Platform
                module Demo

                export fn i32[min max] main() {
                    return System.Runtime.Platform.ProcessId();
                }
                """,
                "MacOSPlatformDispatchProbe.stark"),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.MacOS"));
        Assert.False(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Linux"));
        Assert.False(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Windows"));

        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("@System_Runtime_Platform_MacOS_ProcessId", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Platform_Linux_ProcessId", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Platform_Windows_ProcessId", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOSRuntimeAllocatorUsesMallocReallocAndFreeForAppleTargets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Memory
                module Demo

                export fn i32[min max] main() {
                    stack System.Memory.Allocator allocator = System.Memory.Allocator.Default();
                    stack mut System.Memory.Allocation allocation = System.Memory.Allocate(allocator, 16, 8);
                    allocation = System.Memory.Reallocate(allocation, 32, 8);
                    System.Memory.Free(allocation);
                    return 0;
                }
                """,
                "MacOSAllocatorProbe.stark"),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(MacOSTargetTriple, null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("@malloc(", llvm, StringComparison.Ordinal);
        Assert.Contains("@realloc(", llvm, StringComparison.Ordinal);
        Assert.Contains("@free(", llvm, StringComparison.Ordinal);
        Assert.Contains("call", llvm, StringComparison.Ordinal);
        Assert.Contains("ptr @malloc(", llvm, StringComparison.Ordinal);
        Assert.Contains("ptr @realloc(", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @free(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@HeapAlloc(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("asm sideeffect \"syscall\"", llvm, StringComparison.Ordinal);
    }

    private static CompilationResult CompileMacOSPlatformSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var macOSPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "MacOS.stark");
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(macOSPath),
                macOSPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(MacOSTargetTriple, null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));
    }

    private static CompilationResult CompileMacOSDispatchTemplate()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var templatePath = Path.Combine(repositoryRoot, "stdlib", "templates", "System.Runtime.Platform.MacOSDispatch.stark");
        return DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(templatePath),
                templatePath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(MacOSTargetTriple, null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));
    }

    private static string[] ExtractInternalFunctionNames(string source)
    {
        var names = new List<string>();
        using var reader = new StringReader(source);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("internal fn ", StringComparison.Ordinal)
                && !trimmed.StartsWith("internal unsafe fn ", StringComparison.Ordinal)
                && !trimmed.StartsWith("internal finite law ", StringComparison.Ordinal))
            {
                continue;
            }

            var openParen = trimmed.IndexOf('(');
            if (openParen < 0)
            {
                continue;
            }

            var declarationPrefix = trimmed[..openParen];
            var nameStart = declarationPrefix.LastIndexOf(' ');
            if (nameStart < 0 || nameStart + 1 >= declarationPrefix.Length)
            {
                continue;
            }

            names.Add(declarationPrefix[(nameStart + 1)..]);
        }

        return names
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ExtractDefinedFunctionText(string llvm, string signaturePrefix, string missingMessage)
    {
        var functionStart = llvm.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(functionStart >= 0, missingMessage);

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
}
