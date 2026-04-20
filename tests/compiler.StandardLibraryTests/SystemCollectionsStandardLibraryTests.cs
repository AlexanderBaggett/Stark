namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceCollectionsSupportOwnedAllocatorBackedSurface() => _suite.StdLibSourceCollectionsSupportOwnedAllocatorBackedSurface();

    [Fact]
    public Task SourceStdLibCollectionsGrowMoveDropExecutableRuns() => _suite.SourceStdLibCollectionsGrowMoveDropExecutableRuns();

    [Fact]
    public Task PackagedStdLibCollectionsGrowMoveDropExecutableRunsWithoutSource() => _suite.PackagedStdLibCollectionsGrowMoveDropExecutableRunsWithoutSource();
}
