namespace compiler.StandardLibraryTests;

public sealed class SystemSyscallStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Theory]
    [MemberData(nameof(StandardLibraryTestSuite.SystemSyscallArchitectureCases), MemberType = typeof(StandardLibraryTestSuite))]
    public void SystemSyscallModuleSelectsExpectedLinuxShimPerArchitecture(string targetTriple, string expectedInlineAsm)
        => _suite.SystemSyscallModuleSelectsExpectedLinuxShimPerArchitecture(targetTriple, expectedInlineAsm);

    [Fact]
    public Task PackagedStdLibSyscallModuleCanBeConsumedWithoutSource() => _suite.PackagedStdLibSyscallModuleCanBeConsumedWithoutSource();
}
