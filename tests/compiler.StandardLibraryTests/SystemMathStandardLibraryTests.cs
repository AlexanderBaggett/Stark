namespace compiler.StandardLibraryTests;

public sealed class SystemMathStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public Task PackagedStdLibMathIntrinsicsWorkWithoutSource() => _suite.PackagedStdLibMathIntrinsicsWorkWithoutSource();

    [Fact]
    public Task PackagedStdLibFusedMultiplyAddWorksWithoutSourceWhenRuntimeSupportsIt() => _suite.PackagedStdLibFusedMultiplyAddWorksWithoutSourceWhenRuntimeSupportsIt();
}
