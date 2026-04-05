namespace compiler.StandardLibraryTests;

public sealed class SystemRuntimePlatformLinuxStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceCurrentDirectoryUsesSyscallBackedLinuxPath() => _suite.StdLibSourceCurrentDirectoryUsesSyscallBackedLinuxPath();

    [Fact]
    public void StdLibSourceConsoleAsciiWritesUseSyscallBackedLinuxPath() => _suite.StdLibSourceConsoleAsciiWritesUseSyscallBackedLinuxPath();

    [Fact]
    public void StdLibSourceConsoleUnicodeWritesUseSyscallBackedLinuxPath() => _suite.StdLibSourceConsoleUnicodeWritesUseSyscallBackedLinuxPath();

    [Fact]
    public void StdLibSourceLinuxFileOperationsUseSyscallBackedPath() => _suite.StdLibSourceLinuxFileOperationsUseSyscallBackedPath();

    [Fact]
    public void StdLibSourceLinuxFileExistsUsesStatSyscallPath() => _suite.StdLibSourceLinuxFileExistsUsesStatSyscallPath();

    [Fact]
    public void StdLibSourceLinuxTerminalDetectionUsesIoctlSyscallPath() => _suite.StdLibSourceLinuxTerminalDetectionUsesIoctlSyscallPath();

    [Fact]
    public void SourceStdLibBuildRoutesPlatformCallsThroughLinuxModuleForLinuxTargets() => _suite.SourceStdLibBuildRoutesPlatformCallsThroughLinuxModuleForLinuxTargets();

    [Fact]
    public Task SourceRuntimePlatformTerminalDetectionSeesRedirectedStdoutAsNonTerminal() => _suite.SourceRuntimePlatformTerminalDetectionSeesRedirectedStdoutAsNonTerminal();
}
