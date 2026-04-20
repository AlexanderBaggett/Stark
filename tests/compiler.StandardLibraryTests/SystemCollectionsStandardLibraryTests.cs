namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceCollectionsSupportOwnedAllocatorBackedSurface() => _suite.StdLibSourceCollectionsSupportOwnedAllocatorBackedSurface();
}
