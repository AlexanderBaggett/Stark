using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed partial class LlvmFunctionBodyEmitter
{
    private string CreateAbiTempName(string purpose) => $"abi_{purpose}_{_nextAbiTempId++}";

    private string AllocatorSizeType => _context.AllocatorSizeType;

    private string EmptyMetadataRef => _context.EmptyTupleMetadataRef;

    private string TrapCallingConventionPrefix()
    {
        var triple = _context.TargetInfo?.Triple;
        if (!string.IsNullOrWhiteSpace(triple)
            && (triple.StartsWith("aarch64", StringComparison.OrdinalIgnoreCase)
                || triple.StartsWith("arm64", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        return "coldcc ";
    }

    private string MapType(StarkTypeSymbol type) => _context.MapType(type);

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type) => _context.TryGetConcreteTypeLayout(type);

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type) => _context.ResolveNamedTypeSymbol(type);

    private EmittedStringConstant ResolveStringConstant(string literalText, StarkTypeSymbol type) =>
        _context.ResolveStringConstant(literalText, type);

    private string ResolveGlobalSymbolName(string globalName) => _context.ResolveGlobalSymbolName(globalName);

    private bool IsImmutableGlobalName(string globalName) => _context.IsImmutableGlobalName(globalName);

    private static string GetFloatIntrinsicSuffix(StarkTypeSymbol type)
    {
        return type.BitWidth switch
        {
            16 => "f16",
            32 => "f32",
            64 => "f64",
            80 => "f80",
            128 => "f128",
            _ => throw new InvalidOperationException($"Unsupported float intrinsic width '{type.BitWidth}'.")
        };
    }

    private static string GetIntegerExponentHelperName(int bitWidth)
    {
        return $"{IntegerExponentHelperNamePrefix}{bitWidth}";
    }

    private static string GetFixedArrayOrderedComparisonHelperName(StarkTypeSymbol fixedArrayType)
    {
        return $"{FixedArrayCompareHelperNamePrefix}{EscapeIdentifier(fixedArrayType.DisplayName)}";
    }

    private static string GetScalarizedAggregateOrderedComparisonHelperName(StarkTypeSymbol aggregateType)
    {
        return $"{ScalarizedAggregateCompareHelperNamePrefix}{EscapeIdentifier(aggregateType.DisplayName)}";
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private static SourceLocation? GetInstructionLocation(SsaInstruction instruction)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => valueInstruction.Location,
            SsaAllocateLocalInstruction allocateLocal => allocateLocal.Location,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart.Location,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd.Location,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal.Location,
            SsaArenaFrameEnterInstruction arenaFrameEnter => arenaFrameEnter.Location,
            SsaArenaFrameLeaveInstruction arenaFrameLeave => arenaFrameLeave.Location,
            SsaStoreLocalInstruction storeLocal => storeLocal.Location,
            SsaCopyMemoryInstruction copyMemory => copyMemory.Location,
            SsaStoreIndirectInstruction storeIndirect => storeIndirect.Location,
            SsaStoreGlobalInstruction storeGlobal => storeGlobal.Location,
            _ => null
        };
    }

    private readonly record struct TbaaAddressAccess(
        StarkTypeSymbol RootType,
        long OffsetBytes,
        bool UseStructPath);

    private readonly record struct ScopedNoAliasRoot(
        string Key,
        string DisplayName);

    private sealed record SameParameterAliasClass(
        ScopedNoAliasRoot Root,
        IReadOnlySet<string> ParameterNames);

    private sealed record ScopedNoAliasMetadataModel(
        IReadOnlyDictionary<string, ScopedNoAliasAccessMetadata> Accesses,
        IReadOnlyDictionary<string, string> ScopeRefs);

    private sealed record ScopedNoAliasAccessMetadata(
        string AliasScopeListRef,
        string? NoAliasListRef);

    private static bool IsStringType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private enum LlvmAssumeOperandBundleKind
    {
        NonNull,
        Align,
        SeparateStorage
    }

    private sealed record LlvmAssumeOperandBundle(
        LlvmAssumeOperandBundleKind Kind,
        SsaValue Pointer,
        int? AlignmentBytes = null,
        SsaValue? OtherPointer = null);

    private sealed record LlvmAssumeFact(
        SsaValue? Condition,
        bool NegateCondition,
        IReadOnlyList<LlvmAssumeOperandBundle> OperandBundles);

    private static StarkTypeSymbol GetTextUnitType(StarkTypeSymbol textType)
    {
        return textType.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => throw new UnsupportedBodyEmissionException($"Text operations require an ascii/unicode value, but found '{textType.DisplayName}'.")
        };
    }

    private void AppendLine(string text)
    {
        if (_debugFunction is not null
            && _currentDebugLocation is not null
            && ShouldAttachDebugLocation(text))
        {
            text = $"{text}, !dbg {_debugFunction.GetLocationRef(_currentDebugLocation)}";
        }

        _builder.AppendLine(text);
    }

    private static bool ShouldAttachDebugLocation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !text.StartsWith("  ", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return !trimmed.StartsWith(';')
            && !trimmed.StartsWith('}');
    }
}
