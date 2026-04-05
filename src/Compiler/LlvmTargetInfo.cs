namespace Stark.Compiler;

public sealed record LlvmTargetInfo(
    string Triple,
    string? DataLayout,
    string? Cpu = null,
    IReadOnlyList<string>? Features = null,
    LlvmRelocationModel RelocationModel = LlvmRelocationModel.Default,
    LlvmCodeModel? CodeModel = null);

public enum LlvmRelocationModel
{
    Default,
    Static,
    Pic,
    Pie
}

public enum LlvmCodeModel
{
    Tiny,
    Small,
    Kernel,
    Medium,
    Large
}
