namespace compiler.StandardLibraryTests;

public sealed class SystemTextStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile() => _suite.StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile();
}
