namespace compiler.StandardLibraryTests;

public sealed class SystemRuntimePlatformWindowsStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceWindowsConsoleAndFileOperationsUseWin32Apis() => _suite.StdLibSourceWindowsConsoleAndFileOperationsUseWin32Apis();

    [Fact]
    public void StdLibSourceWindowsWidePathCopiesUseInlineAsmHelper() => _suite.StdLibSourceWindowsWidePathCopiesUseInlineAsmHelper();

    [Fact]
    public void StagedWindowsStdLibBuildRoutesPlatformCallsThroughWindowsModule() => _suite.StagedWindowsStdLibBuildRoutesPlatformCallsThroughWindowsModule();

    [Fact]
    public void RootWindowsStdLibCompileKeepsWriteBufferToHandleOnDirectMirPath() => _suite.RootWindowsStdLibCompileKeepsWriteBufferToHandleOnDirectMirPath();
}
