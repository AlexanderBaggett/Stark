namespace Stark.Compiler;

internal enum SdkEndianness
{
    Little,
    Big
}

internal sealed record SdkTargetDescriptor(
    string Id,
    string LlvmTriple,
    string Architecture,
    string OperatingSystem,
    string Abi,
    int PointerBitWidth,
    SdkEndianness Endianness,
    string? DataLayout,
    string? BaselineCpu,
    IReadOnlyList<string> BaselineFeatures,
    string RelocationModel,
    string? CodeModel,
    string? CDataModel,
    string? MinimumOperatingSystemVersion);
