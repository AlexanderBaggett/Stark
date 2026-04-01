using System.Runtime.InteropServices;

namespace Stark.Compiler;

internal static class StarkAsmArchitectureFacts
{
    public static StarkAsmArchitecture ResolveActiveArchitecture(LlvmTargetInfo? targetInfo)
    {
        if (TryParseTargetTriple(targetInfo?.Triple, out var fromTriple))
        {
            return fromTriple;
        }

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => StarkAsmArchitecture.X86_64,
            Architecture.Arm64 => StarkAsmArchitecture.AArch64,
            Architecture.X86 => StarkAsmArchitecture.X86,
            Architecture.Arm => StarkAsmArchitecture.Arm32,
            _ => StarkAsmArchitecture.Unknown
        };
    }

    public static bool TryParseArchitectureName(string? architectureText, out StarkAsmArchitecture architecture)
    {
        architecture = architectureText?.Trim().ToLowerInvariant() switch
        {
            "x86_64" or "amd64" => StarkAsmArchitecture.X86_64,
            "aarch64" or "arm64" => StarkAsmArchitecture.AArch64,
            "riscv64" => StarkAsmArchitecture.RiscV64,
            "x86" or "i386" or "i486" or "i586" or "i686" => StarkAsmArchitecture.X86,
            "arm" or "arm32" or "thumb" => StarkAsmArchitecture.Arm32,
            _ => StarkAsmArchitecture.Unknown
        };

        return architecture != StarkAsmArchitecture.Unknown;
    }

    private static bool TryParseTargetTriple(string? triple, out StarkAsmArchitecture architecture)
    {
        architecture = StarkAsmArchitecture.Unknown;
        if (string.IsNullOrWhiteSpace(triple))
        {
            return false;
        }

        var dash = triple.IndexOf('-');
        var architectureText = dash >= 0
            ? triple[..dash]
            : triple;

        return TryParseArchitectureName(architectureText, out architecture);
    }
}
