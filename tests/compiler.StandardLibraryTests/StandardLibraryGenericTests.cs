namespace compiler.StandardLibraryTests;

public sealed class StandardLibraryGenericTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceGraphIncludesMilestone7ModuleLayout() => _suite.StdLibSourceGraphIncludesMilestone7ModuleLayout();

    [Fact]
    public Task StdLibPackageBuildsFromRepositorySources() => _suite.StdLibPackageBuildsFromRepositorySources();

    [Fact]
    public Task PackagedStdLibCanBeConsumedWithoutSource() => _suite.PackagedStdLibCanBeConsumedWithoutSource();

    [Fact]
    public Task PackagedStdLibUnicodeConsoleAndRawFileWritesWorkWithoutSource() => _suite.PackagedStdLibUnicodeConsoleAndRawFileWritesWorkWithoutSource();

    [Fact]
    public Task PackagedStdLibWindowsUnicodePathsCurrentDirectoryAndOwnedUnicodeWritesWorkWithoutSource() => _suite.PackagedStdLibWindowsUnicodePathsCurrentDirectoryAndOwnedUnicodeWritesWorkWithoutSource();

    [Fact]
    public Task PackagedStdLibLinuxArchiveHasNoLibcSymbolReferences() => _suite.PackagedStdLibLinuxArchiveHasNoLibcSymbolReferences();

    [Fact]
    public Task PackagedStdLibWindowsArchiveHasNoCrtSymbolReferences() => _suite.PackagedStdLibWindowsArchiveHasNoCrtSymbolReferences();
}
