namespace compiler.StandardLibraryTests;

public sealed class SystemIOPathStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public Task PackagedStdLibPathCurrentDirectoryFillsCallerProvidedAsciiBuffer() => _suite.PackagedStdLibPathCurrentDirectoryFillsCallerProvidedAsciiBuffer();

    [Fact]
    public Task PackagedStdLibPathHelpersWorkWithoutSource() => _suite.PackagedStdLibPathHelpersWorkWithoutSource();

    [Fact]
    public void StagedWindowsStdLibPathHelpersUseWindowsSeparatorsAndNormalizationRules() => _suite.StagedWindowsStdLibPathHelpersUseWindowsSeparatorsAndNormalizationRules();
}
