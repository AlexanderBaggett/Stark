namespace compiler.StandardLibraryTests;

public sealed class SystemMemoryStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceMemoryModuleSupportsDefaultAllocatorSurface() => _suite.StdLibSourceMemoryModuleSupportsDefaultAllocatorSurface();
}
