namespace Stark.Compiler;

public sealed record LlvmTargetInfo(
    string Triple,
    string? DataLayout);
