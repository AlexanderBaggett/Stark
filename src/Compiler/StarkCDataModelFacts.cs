using System.Runtime.InteropServices;

namespace Stark.Compiler;

internal enum StarkCDataModelKind
{
    ILP32,
    LP64,
    LLP64
}

internal sealed record StarkCDataModel(
    StarkCDataModelKind Kind,
    bool CharIsSigned,
    int PointerBitWidth,
    int LongBitWidth,
    int SizeTBitWidth,
    int PtrDiffTBitWidth);

internal static class StarkCDataModelFacts
{
    public const string ModuleName = "System.C";
    public const string CCharIsSignedGlobalName = $"{ModuleName}.c_char_is_signed";

    private static readonly IReadOnlySet<string> KnownAliasNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "c_char",
        "c_schar",
        "c_uchar",
        "c_short",
        "c_ushort",
        "c_int",
        "c_uint",
        "c_long",
        "c_ulong",
        "c_longlong",
        "c_ulonglong",
        "c_size_t",
        "c_ptrdiff_t",
        "c_void",
        "VaList"
    };

    public static bool TryResolveAlias(
        string lookupName,
        LlvmTargetInfo? targetInfo,
        out StarkTypeSymbol type,
        out string? diagnostic)
    {
        type = StarkTypeSymbols.Error;
        diagnostic = null;

        if (!TryGetLocalAliasName(lookupName, out var localAliasName)
            || !KnownAliasNames.Contains(localAliasName))
        {
            return false;
        }

        if (string.Equals(localAliasName, "c_void", StringComparison.Ordinal))
        {
            type = StarkTypeSymbols.WithCSourceAlias(
                StarkTypeSymbols.CVoid,
                QualifyAliasName(localAliasName));
            return true;
        }

        if (string.Equals(localAliasName, "VaList", StringComparison.Ordinal))
        {
            if (!TryResolve(targetInfo, out _))
            {
                diagnostic = $"Target '{targetInfo?.Triple ?? "<host>"}' does not define a C ABI va_list carrier for System.C.VaList.";
                return true;
            }

            type = StarkTypeSymbols.WithCSourceAlias(
                StarkTypeSymbols.CVaList,
                QualifyAliasName(localAliasName));
            return true;
        }

        if (!TryResolve(targetInfo, out var dataModel))
        {
            diagnostic = $"Target '{targetInfo?.Triple ?? "<host>"}' does not define C primitive type mappings for System.C.{localAliasName}.";
            return true;
        }

        type = localAliasName switch
        {
            "c_char" => Integer(8, isUnsigned: !dataModel.CharIsSigned),
            "c_schar" => Integer(8, isUnsigned: false),
            "c_uchar" => Integer(8, isUnsigned: true),
            "c_short" => Integer(16, isUnsigned: false),
            "c_ushort" => Integer(16, isUnsigned: true),
            "c_int" => Integer(32, isUnsigned: false),
            "c_uint" => Integer(32, isUnsigned: true),
            "c_long" => Integer(dataModel.LongBitWidth, isUnsigned: false),
            "c_ulong" => Integer(dataModel.LongBitWidth, isUnsigned: true),
            "c_longlong" => Integer(64, isUnsigned: false),
            "c_ulonglong" => Integer(64, isUnsigned: true),
            "c_size_t" => Integer(dataModel.SizeTBitWidth, isUnsigned: true),
            "c_ptrdiff_t" => Integer(dataModel.PtrDiffTBitWidth, isUnsigned: false),
            _ => StarkTypeSymbols.Error
        };
        type = StarkTypeSymbols.WithCSourceAlias(type, QualifyAliasName(localAliasName));
        return true;
    }

    public static string QualifyAliasName(string aliasName)
    {
        return aliasName.StartsWith($"{ModuleName}.", StringComparison.Ordinal)
            ? aliasName
            : $"{ModuleName}.{aliasName}";
    }

    public static bool TryResolve(LlvmTargetInfo? targetInfo, out StarkCDataModel dataModel)
    {
        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(targetInfo);
        var os = ResolveOperatingSystem(targetInfo);
        var charIsSigned = ResolveDefaultCharSignedness(architecture, os);

        dataModel = architecture switch
        {
            StarkAsmArchitecture.X86 or StarkAsmArchitecture.Arm32 => new StarkCDataModel(
                StarkCDataModelKind.ILP32,
                charIsSigned,
                PointerBitWidth: 32,
                LongBitWidth: 32,
                SizeTBitWidth: 32,
                PtrDiffTBitWidth: 32),
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64
                when os == StarkTargetOperatingSystem.Windows => new StarkCDataModel(
                    StarkCDataModelKind.LLP64,
                    charIsSigned,
                    PointerBitWidth: 64,
                    LongBitWidth: 32,
                    SizeTBitWidth: 64,
                    PtrDiffTBitWidth: 64),
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64 => new StarkCDataModel(
                StarkCDataModelKind.LP64,
                charIsSigned,
                PointerBitWidth: 64,
                LongBitWidth: 64,
                SizeTBitWidth: 64,
                PtrDiffTBitWidth: 64),
            _ => null!
        };

        return dataModel is not null;
    }

    private static StarkTypeSymbol Integer(int bitWidth, bool isUnsigned)
    {
        IntegerRangeStorageFacts.GetIntegerTypeBounds(bitWidth, isUnsigned, out var min, out var max);
        return StarkTypeSymbols.Integer(bitWidth, min, max, isUnsigned);
    }

    private static bool TryGetLocalAliasName(string lookupName, out string localAliasName)
    {
        localAliasName = lookupName;
        if (lookupName.StartsWith($"{ModuleName}.", StringComparison.Ordinal))
        {
            localAliasName = lookupName[(ModuleName.Length + 1)..];
            return true;
        }

        return !lookupName.Contains('.', StringComparison.Ordinal);
    }

    private static bool ResolveDefaultCharSignedness(
        StarkAsmArchitecture architecture,
        StarkTargetOperatingSystem os)
    {
        if (os is StarkTargetOperatingSystem.Windows or StarkTargetOperatingSystem.MacOS)
        {
            return true;
        }

        return architecture is not (StarkAsmArchitecture.Arm32 or StarkAsmArchitecture.AArch64);
    }

    private static StarkTargetOperatingSystem ResolveOperatingSystem(LlvmTargetInfo? targetInfo)
    {
        var triple = targetInfo?.Triple;
        if (!string.IsNullOrWhiteSpace(triple))
        {
            if (triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("msvc", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase))
            {
                return StarkTargetOperatingSystem.Windows;
            }

            if (triple.Contains("apple", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("darwin", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("macos", StringComparison.OrdinalIgnoreCase))
            {
                return StarkTargetOperatingSystem.MacOS;
            }

            if (triple.Contains("android", StringComparison.OrdinalIgnoreCase))
            {
                return StarkTargetOperatingSystem.Linux;
            }

            if (triple.Contains("linux", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("gnu", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("musl", StringComparison.OrdinalIgnoreCase))
            {
                return StarkTargetOperatingSystem.Linux;
            }

            if (triple.Contains("wasi", StringComparison.OrdinalIgnoreCase))
            {
                return StarkTargetOperatingSystem.Unknown;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return StarkTargetOperatingSystem.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return StarkTargetOperatingSystem.MacOS;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return StarkTargetOperatingSystem.Linux;
        }

        return StarkTargetOperatingSystem.Unknown;
    }
}
