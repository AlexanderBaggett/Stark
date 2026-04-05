namespace compiler.StandardLibraryTests;

public sealed class SystemConsoleStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceConsoleSupportsAsciiAndUnicodeOverloads() => _suite.StdLibSourceConsoleSupportsAsciiAndUnicodeOverloads();

    [Fact]
    public Task PackagedStdLibConsoleReturnsIoStatusWithoutSource() => _suite.PackagedStdLibConsoleReturnsIoStatusWithoutSource();
}
