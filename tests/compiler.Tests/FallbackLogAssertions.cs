using Stark.Compiler;

namespace compiler.Tests;

internal static class FallbackLogAssertions
{
    private static readonly HashSet<string> FallbackEventIds = new(StringComparer.Ordinal)
    {
        "unsupported-lowering",
        "llvm-body-fallback",
        "llvm-asm-fallback",
        "llvm-body-pending",
        "missing-function-body"
    };

    public static void AssertNoFallbackLogs(
        CompilationResult result,
        string buildDescription,
        string? sourcePath = null)
    {
        var fallbackLogs = result.Logs
            .Where(log => FallbackEventIds.Contains(log.EventId))
            .Select(log => sourcePath is null
                ? $"{log.Stage}:{log.EventId}: {log.Message}"
                : $"{sourcePath}: {log.Stage}:{log.EventId}: {log.Message}")
            .ToArray();

        Assert.True(
            fallbackLogs.Length == 0,
            $"{buildDescription} must not use lowering/backend fallback logs."
            + Environment.NewLine
            + string.Join(Environment.NewLine, fallbackLogs));
    }
}
