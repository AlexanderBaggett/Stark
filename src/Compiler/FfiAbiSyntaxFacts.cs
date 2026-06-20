using System.Runtime.InteropServices;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal enum StarkTargetOperatingSystem
{
    Unknown,
    Windows,
    Linux,
    MacOS
}

internal readonly record struct StarkTargetPlatform(
    StarkTargetOperatingSystem OperatingSystem,
    StarkAsmArchitecture Architecture)
{
    public string DisplayName =>
        $"{DisplayOperatingSystem(OperatingSystem)}.{DisplayArchitecture(Architecture)}";

    private static string DisplayOperatingSystem(StarkTargetOperatingSystem operatingSystem)
    {
        return operatingSystem switch
        {
            StarkTargetOperatingSystem.Windows => "windows",
            StarkTargetOperatingSystem.Linux => "linux",
            StarkTargetOperatingSystem.MacOS => "macos",
            _ => "unknown"
        };
    }

    private static string DisplayArchitecture(StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 => "x64",
            StarkAsmArchitecture.AArch64 => "arm64",
            StarkAsmArchitecture.RiscV64 => "riscv64",
            StarkAsmArchitecture.X86 => "x86",
            StarkAsmArchitecture.Arm32 => "arm",
            _ => "unknown"
        };
    }
}

internal sealed record FfiAbiResolutionResult(
    bool HasFfi,
    bool HasExplicitAbi,
    StarkFfiAbi? Abi);

internal static class FfiAbiSyntaxFacts
{
    public static bool IsFfiModifier(StarkParser.FunctionModifierContext modifier) =>
        modifier.ffiModifier() is not null;

    public static StarkParser.FfiModifierContext? FindFfiModifier(IEnumerable<StarkParser.FunctionModifierContext> modifiers)
    {
        return modifiers
            .Select(static modifier => modifier.ffiModifier())
            .FirstOrDefault(static modifier => modifier is not null);
    }

    public static bool TryResolveFunctionAbi(
        IReadOnlyList<StarkParser.FunctionModifierContext> modifiers,
        LlvmTargetInfo? targetInfo,
        out FfiAbiResolutionResult result,
        out string errorMessage,
        out ParserRuleContext errorContext)
    {
        var ffiModifier = FindFfiModifier(modifiers);
        if (ffiModifier is null)
        {
            result = new FfiAbiResolutionResult(false, false, null);
            errorMessage = string.Empty;
            errorContext = null!;
            return true;
        }

        return TryResolveFfiModifierAbi(ffiModifier, targetInfo, out result, out errorMessage, out errorContext);
    }

    public static bool TryResolveFfiModifierAbi(
        StarkParser.FfiModifierContext ffiModifier,
        LlvmTargetInfo? targetInfo,
        out FfiAbiResolutionResult result,
        out string errorMessage,
        out ParserRuleContext errorContext)
    {
        if (ffiModifier.ffiAbiSpecifier() is not { } specifier)
        {
            result = new FfiAbiResolutionResult(true, false, StarkFfiAbi.C);
            errorMessage = string.Empty;
            errorContext = ffiModifier;
            return true;
        }

        if (!TryResolveFfiAbi(
                specifier.ffiAbi(),
                targetInfo,
                out var abi,
                out errorMessage,
                out errorContext))
        {
            result = new FfiAbiResolutionResult(true, true, null);
            return false;
        }

        result = new FfiAbiResolutionResult(true, true, abi);
        errorMessage = string.Empty;
        errorContext = specifier;
        return true;
    }

    public static bool TryResolveFfiAbi(
        StarkParser.FfiAbiContext abiContext,
        LlvmTargetInfo? targetInfo,
        out StarkFfiAbi abi,
        out string errorMessage,
        out ParserRuleContext errorContext)
    {
        if (abiContext.LPAREN() is null)
        {
            return TryParseAndValidateAbi(
                abiContext.Identifier().GetText(),
                targetInfo,
                out abi,
                out errorMessage,
                out errorContext,
                abiContext);
        }

        if (!string.Equals(abiContext.Identifier().GetText(), "platform", StringComparison.Ordinal))
        {
            abi = StarkFfiAbi.C;
            errorMessage = $"Unknown FFI ABI selector '{abiContext.Identifier().GetText()}'. Use an ABI name such as 'c' or the selector form 'platform(...)'.";
            errorContext = abiContext;
            return false;
        }

        return TryResolvePlatformAbi(abiContext, targetInfo, out abi, out errorMessage, out errorContext);
    }

    public static bool IsAbiSupportedOnTarget(StarkFfiAbi abi, LlvmTargetInfo? targetInfo)
    {
        var target = ResolveTargetPlatform(targetInfo);
        return abi switch
        {
            StarkFfiAbi.C => true,
            StarkFfiAbi.CDecl => target.Architecture == StarkAsmArchitecture.X86,
            StarkFfiAbi.StdCall => target.OperatingSystem == StarkTargetOperatingSystem.Windows
                                   && target.Architecture == StarkAsmArchitecture.X86,
            StarkFfiAbi.FastCall => target.Architecture == StarkAsmArchitecture.X86,
            StarkFfiAbi.ThisCall => target.Architecture == StarkAsmArchitecture.X86,
            StarkFfiAbi.VectorCall => target.OperatingSystem == StarkTargetOperatingSystem.Windows
                                      && target.Architecture is StarkAsmArchitecture.X86 or StarkAsmArchitecture.X86_64,
            StarkFfiAbi.SysV => target.Architecture == StarkAsmArchitecture.X86_64,
            StarkFfiAbi.Win64 => target.Architecture == StarkAsmArchitecture.X86_64,
            StarkFfiAbi.Aapcs => target.Architecture == StarkAsmArchitecture.Arm32,
            StarkFfiAbi.Aapcs64 => target.Architecture == StarkAsmArchitecture.AArch64,
            _ => false
        };
    }

    public static bool AbiSupportsCVarargs(StarkFfiAbi? abi)
    {
        return abi is null
            or StarkFfiAbi.C
            or StarkFfiAbi.CDecl
            or StarkFfiAbi.SysV
            or StarkFfiAbi.Win64
            or StarkFfiAbi.Aapcs
            or StarkFfiAbi.Aapcs64;
    }

    public static StarkTargetPlatform ResolveTargetPlatform(LlvmTargetInfo? targetInfo)
    {
        return new StarkTargetPlatform(
            ResolveTargetOperatingSystem(targetInfo?.Triple),
            StarkAsmArchitectureFacts.ResolveActiveArchitecture(targetInfo));
    }

    private static bool TryParseAndValidateAbi(
        string abiText,
        LlvmTargetInfo? targetInfo,
        out StarkFfiAbi abi,
        out string errorMessage,
        out ParserRuleContext errorContext,
        ParserRuleContext context)
    {
        if (!StarkFfiAbiFacts.TryParse(abiText, out abi))
        {
            errorMessage = $"Unknown FFI ABI '{abiText}'. Supported ABI names are: c, cdecl, stdcall, fastcall, thiscall, vectorcall, sysv, win64, aapcs, aapcs64.";
            errorContext = context;
            return false;
        }

        if (!IsAbiSupportedOnTarget(abi, targetInfo))
        {
            var target = ResolveTargetPlatform(targetInfo);
            errorMessage = $"FFI ABI '{StarkFfiAbiFacts.DisplayName(abi)}' is not supported for target '{target.DisplayName}'.";
            errorContext = context;
            return false;
        }

        errorMessage = string.Empty;
        errorContext = context;
        return true;
    }

    private static bool TryResolvePlatformAbi(
        StarkParser.FfiAbiContext abiContext,
        LlvmTargetInfo? targetInfo,
        out StarkFfiAbi abi,
        out string errorMessage,
        out ParserRuleContext errorContext)
    {
        var target = ResolveTargetPlatform(targetInfo);
        StarkParser.FfiPlatformAbiEntryContext? bestEntry = null;
        var bestSpecificity = -1;
        var ambiguous = false;

        foreach (var entry in abiContext.ffiPlatformAbiEntry())
        {
            if (!TryGetPlatformEntrySpecificity(entry.ffiPlatformKey(), target, out var specificity, out errorMessage, out errorContext))
            {
                abi = StarkFfiAbi.C;
                return false;
            }

            if (specificity < 0)
            {
                continue;
            }

            if (specificity > bestSpecificity)
            {
                bestSpecificity = specificity;
                bestEntry = entry;
                ambiguous = false;
                continue;
            }

            if (specificity == bestSpecificity)
            {
                ambiguous = true;
            }
        }

        if (bestEntry is null)
        {
            abi = StarkFfiAbi.C;
            errorMessage = $"FFI platform ABI selector has no entry for target '{target.DisplayName}' and no 'default' fallback.";
            errorContext = abiContext;
            return false;
        }

        if (ambiguous)
        {
            abi = StarkFfiAbi.C;
            errorMessage = $"FFI platform ABI selector has more than one equally specific entry for target '{target.DisplayName}'.";
            errorContext = abiContext;
            return false;
        }

        return TryParseAndValidateAbi(
            bestEntry.Identifier().GetText(),
            targetInfo,
            out abi,
            out errorMessage,
            out errorContext,
            bestEntry);
    }

    private static bool TryGetPlatformEntrySpecificity(
        StarkParser.FfiPlatformKeyContext key,
        StarkTargetPlatform target,
        out int specificity,
        out string errorMessage,
        out ParserRuleContext errorContext)
    {
        if (key.DEFAULT() is not null)
        {
            specificity = 0;
            errorMessage = string.Empty;
            errorContext = key;
            return true;
        }

        var parts = key.qualifiedName().Identifier().Select(static identifier => identifier.GetText()).ToArray();
        if (parts.Length is not 1 and not 2
            || !TryParseOperatingSystem(parts[0], out var os))
        {
            specificity = -1;
            errorMessage = $"Invalid FFI platform key '{key.GetText()}'. Use 'default', an operating system such as 'windows', or an operating-system/architecture pair such as 'windows.x86'.";
            errorContext = key;
            return false;
        }

        if (os != target.OperatingSystem)
        {
            specificity = -1;
            errorMessage = string.Empty;
            errorContext = key;
            return true;
        }

        if (parts.Length == 1)
        {
            specificity = 1;
            errorMessage = string.Empty;
            errorContext = key;
            return true;
        }

        if (!TryParseArchitecture(parts[1], out var architecture))
        {
            specificity = -1;
            errorMessage = $"Invalid FFI platform architecture '{parts[1]}' in key '{key.GetText()}'.";
            errorContext = key;
            return false;
        }

        specificity = architecture == target.Architecture ? 2 : -1;
        errorMessage = string.Empty;
        errorContext = key;
        return true;
    }

    private static StarkTargetOperatingSystem ResolveTargetOperatingSystem(string? triple)
    {
        if (!string.IsNullOrWhiteSpace(triple))
        {
            var lower = triple.ToLowerInvariant();
            if (lower.Contains("windows", StringComparison.Ordinal)
                || lower.Contains("win32", StringComparison.Ordinal)
                || lower.Contains("mingw", StringComparison.Ordinal)
                || lower.Contains("msvc", StringComparison.Ordinal))
            {
                return StarkTargetOperatingSystem.Windows;
            }

            if (lower.Contains("linux", StringComparison.Ordinal))
            {
                return StarkTargetOperatingSystem.Linux;
            }

            if (lower.Contains("darwin", StringComparison.Ordinal)
                || lower.Contains("macos", StringComparison.Ordinal)
                || lower.Contains("apple", StringComparison.Ordinal))
            {
                return StarkTargetOperatingSystem.MacOS;
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

    private static bool TryParseOperatingSystem(string text, out StarkTargetOperatingSystem operatingSystem)
    {
        operatingSystem = text.Trim().ToLowerInvariant() switch
        {
            "windows" or "win32" or "win64" => StarkTargetOperatingSystem.Windows,
            "linux" => StarkTargetOperatingSystem.Linux,
            "macos" or "darwin" => StarkTargetOperatingSystem.MacOS,
            _ => StarkTargetOperatingSystem.Unknown
        };

        return operatingSystem != StarkTargetOperatingSystem.Unknown;
    }

    private static bool TryParseArchitecture(string text, out StarkAsmArchitecture architecture) =>
        StarkAsmArchitectureFacts.TryParseArchitectureName(text, out architecture);
}
