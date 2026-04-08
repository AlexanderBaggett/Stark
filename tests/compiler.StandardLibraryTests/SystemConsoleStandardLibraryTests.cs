namespace compiler.StandardLibraryTests;

public sealed class SystemConsoleStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceConsoleSupportsAsciiAndUnicodeOverloads() => _suite.StdLibSourceConsoleSupportsAsciiAndUnicodeOverloads();

    [Fact]
    public void StdLibSourceConsoleSupportsUnicodeInputSurface() => _suite.StdLibSourceConsoleSupportsUnicodeInputSurface();

    [Fact]
    public Task StdLibSourceUnicodeConsoleInputWorksAtRuntime() => _suite.StdLibSourceUnicodeConsoleInputWorksAtRuntime();

    [Fact]
    public Task PackagedStdLibConsoleReturnsIoStatusWithoutSource() => _suite.PackagedStdLibConsoleReturnsIoStatusWithoutSource();

    [Fact]
    public Task PackagedStdLibUnicodeConsoleInputWorksWithoutSource() => _suite.PackagedStdLibUnicodeConsoleInputWorksWithoutSource();
}
