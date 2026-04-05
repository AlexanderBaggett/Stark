namespace compiler.StandardLibraryTests;

public sealed class SystemRuntimeBufferStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceRuntimeBufferModuleSupportsLinearAndRingOperations() => _suite.StdLibSourceRuntimeBufferModuleSupportsLinearAndRingOperations();

    [Fact]
    public Task SourceRuntimeBufferModuleCanExecuteLinearAndRingOperations() => _suite.SourceRuntimeBufferModuleCanExecuteLinearAndRingOperations();
}
