namespace Stark.Compiler;

public sealed record LlvmTargetInfo(
    string Triple,
    string? DataLayout,
    string? Cpu = null,
    IReadOnlyList<string>? Features = null);
