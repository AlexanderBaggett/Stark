namespace compiler.StandardLibraryTests;

public sealed class SystemMemoryContractAuditStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibOverlapSensitiveApisDeclareExplicitMemoryContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var memorySource = File.ReadAllText(Path.Combine(sourceRoot, "System", "Memory.stark"));
        var textSource = File.ReadAllText(Path.Combine(sourceRoot, "System", "Text.stark"));
        var pathSource = File.ReadAllText(Path.Combine(sourceRoot, "System", "IO", "Path.stark"));
        var bufferSource = File.ReadAllText(Path.Combine(sourceRoot, "System", "Runtime", "Buffer.stark"));

        Assert.Contains("public fn MemoryStatus AppendBytes(", memorySource, StringComparison.Ordinal);
        Assert.Contains("where overlap(storage, source)", memorySource, StringComparison.Ordinal);
        Assert.Contains("public fn MemoryStatus AppendCodePoints(", memorySource, StringComparison.Ordinal);
        Assert.Contains("public fn MemoryStatus CopyBytes(", memorySource, StringComparison.Ordinal);
        Assert.Contains("public fn MemoryStatus CopyCodePoints(", memorySource, StringComparison.Ordinal);
        Assert.Contains("where overlap(source, destination)", memorySource, StringComparison.Ordinal);

        Assert.Contains("fn System.Memory.MemoryStatus AppendAscii(", textSource, StringComparison.Ordinal);
        Assert.Contains("fn System.Memory.MemoryStatus AppendUnicode(", textSource, StringComparison.Ordinal);
        Assert.Contains("where overlap(self, source)", textSource, StringComparison.Ordinal);
        Assert.Contains("where overlap(left, right);", textSource, StringComparison.Ordinal);

        Assert.Contains("fn System.Memory.MemoryStatus TryJoin(", pathSource, StringComparison.Ordinal);
        Assert.Contains("fn System.Memory.MemoryStatus TryJoinConst(", pathSource, StringComparison.Ordinal);
        Assert.Contains("where overlap(destination, left)", pathSource, StringComparison.Ordinal);
        Assert.Contains("where overlap(left, right)", pathSource, StringComparison.Ordinal);

        Assert.Contains("where overlap(storage, source)", bufferSource, StringComparison.Ordinal);
        Assert.Contains("where overlap(self, source)", bufferSource, StringComparison.Ordinal);
    }
}
