namespace compiler.StandardLibraryTests;

public sealed class StandardLibraryGenericTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceGraphIncludesMilestone7ModuleLayout() => _suite.StdLibSourceGraphIncludesMilestone7ModuleLayout();

    [Fact]
    public void StdLibSourceTreeHasNoExperimentalModules() => _suite.StdLibSourceTreeHasNoExperimentalModules();

    [Fact]
    public void StdLibSourceCommonErrorResultModelUsesCompactEnumLayouts() => _suite.StdLibSourceCommonErrorResultModelUsesCompactEnumLayouts();

    [Fact]
    public Task StdLibPackageBuildsFromRepositorySources() => _suite.StdLibPackageBuildsFromRepositorySources();

    [Fact]
    public Task PackagedStdLibCommonErrorResultModelWorksWithoutSource() => _suite.PackagedStdLibCommonErrorResultModelWorksWithoutSource();

    [Fact]
    public Task PackagedStdLibCanBeConsumedWithoutSource() => _suite.PackagedStdLibCanBeConsumedWithoutSource();

    [Fact]
    public Task PackagedStdLibLinuxArchiveHasNoLibcSymbolReferences() => _suite.PackagedStdLibLinuxArchiveHasNoLibcSymbolReferences();

    [Fact]
    public Task PackagedStdLibWindowsArchiveHasNoCrtSymbolReferences() => _suite.PackagedStdLibWindowsArchiveHasNoCrtSymbolReferences();
}
