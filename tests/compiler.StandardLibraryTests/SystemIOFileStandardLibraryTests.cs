namespace compiler.StandardLibraryTests;

public sealed class SystemIOFileStandardLibraryTests
{
    private readonly StandardLibraryTestSuite _suite = new();

    [Fact]
    public void StdLibSourceRawFileHandlesSupportAsciiAndUnicodeWriteOverloads() => _suite.StdLibSourceRawFileHandlesSupportAsciiAndUnicodeWriteOverloads();

    [Fact]
    public void StdLibSourceOwnedFileHandlesSupportAsciiAndUnicodeWriteOverloads() => _suite.StdLibSourceOwnedFileHandlesSupportAsciiAndUnicodeWriteOverloads();

    [Fact]
    public void StdLibSourceFileBufferedAsciiAppendsUseInlineAsmCopyHelper() => _suite.StdLibSourceFileBufferedAsciiAppendsUseInlineAsmCopyHelper();

    [Fact]
    public Task PackagedStdLibOwnedFileHandleFlushesAndClosesOnDrop() => _suite.PackagedStdLibOwnedFileHandleFlushesAndClosesOnDrop();

    [Fact]
    public Task PackagedStdLibFileBufferingModesBehaveAsExpected() => _suite.PackagedStdLibFileBufferingModesBehaveAsExpected();

    [Fact]
    public Task PackagedStdLibFileMoveDeleteAndExistsRoundTrip() => _suite.PackagedStdLibFileMoveDeleteAndExistsRoundTrip();
}
