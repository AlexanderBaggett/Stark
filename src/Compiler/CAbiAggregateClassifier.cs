using Stark.Compiler.LlvmIrEmission;

namespace Stark.Compiler;

internal enum CAbiAggregatePassKind
{
    Direct,
    Indirect
}

internal sealed record CAbiAggregateClassification(
    CAbiAggregatePassKind PassKind,
    StarkTypeSymbol LlvmType,
    IReadOnlyList<StarkTypeSymbol>? LlvmParameterTypes = null)
{
    public IReadOnlyList<StarkTypeSymbol> EffectiveLlvmParameterTypes =>
        LlvmParameterTypes is { Count: > 0 }
            ? LlvmParameterTypes
            : [LlvmType];
}

internal static class CAbiAggregateClassifier
{
    public static bool TryClassify(
        StarkTypeSymbol type,
        StarkFfiAbi? abi,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        out CAbiAggregateClassification classification)
    {
        classification = null!;

        var normalizedType = NormalizeType(type);
        if (!IsLayoutControlledAggregate(normalizedType, namedTypes))
        {
            return false;
        }

        var layout = LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
            normalizedType,
            targetInfo,
            namedTypes,
            enumLayouts,
            publishedConcreteLayouts ?? EmptyPublishedLayouts);
        if (layout is null || layout.SizeBytes <= 0)
        {
            return false;
        }

        return ResolveCAbiFamily(abi, targetInfo) switch
        {
            CAbiFamily.X86_64SysV => TryClassifyX86_64SysV(
                normalizedType,
                layout,
                targetInfo,
                namedTypes,
                enumLayouts,
                publishedConcreteLayouts ?? EmptyPublishedLayouts,
                out classification),
            CAbiFamily.X86_64Win64 => TryClassifyX86_64Win64(layout, out classification),
            CAbiFamily.AArch64Aapcs64 => TryClassifyAArch64Aapcs64(
                normalizedType,
                layout,
                targetInfo,
                namedTypes,
                enumLayouts,
                publishedConcreteLayouts ?? EmptyPublishedLayouts,
                out classification),
            _ => false
        };
    }

    public static bool IsCarrierType(StarkTypeSymbol sourceType, StarkTypeSymbol llvmType)
    {
        var normalizedSource = NormalizeType(sourceType);
        var normalizedLlvm = NormalizeType(llvmType);
        return normalizedSource != normalizedLlvm
            && normalizedSource.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray
            && normalizedLlvm.Kind is StarkTypeKind.Integer or StarkTypeKind.Float or StarkTypeKind.FixedArray or StarkTypeKind.LlvmVector or StarkTypeKind.LlvmStruct;
    }

    private static bool TryClassifyX86_64SysV(
        StarkTypeSymbol type,
        ConcreteTypeLayout layout,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
        out CAbiAggregateClassification classification)
    {
        classification = null!;

        if (layout.SizeBytes > 16 || HasMisalignedStorage(type, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts))
        {
            classification = new CAbiAggregateClassification(CAbiAggregatePassKind.Indirect, StarkTypeSymbols.RawPointer(type, isMutable: false));
            return true;
        }

        if (!TryBuildX86_64SysVDirectCarriers(type, layout, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts, out var parameterCarriers))
        {
            return false;
        }

        var returnCarrier = parameterCarriers.Count == 1
            ? parameterCarriers[0]
            : StarkTypeSymbols.LlvmStruct(parameterCarriers);
        classification = new CAbiAggregateClassification(
            CAbiAggregatePassKind.Direct,
            returnCarrier,
            parameterCarriers);
        return true;
    }

    private static bool TryClassifyX86_64Win64(
        ConcreteTypeLayout layout,
        out CAbiAggregateClassification classification)
    {
        classification = null!;

        if (layout.SizeBytes is 1 or 2 or 4 or 8)
        {
            classification = new CAbiAggregateClassification(
                CAbiAggregatePassKind.Direct,
                StarkTypeSymbols.Integer(layout.SizeBytes * 8, isUnsigned: true),
                [StarkTypeSymbols.Integer(layout.SizeBytes * 8, isUnsigned: true)]);
            return true;
        }

        return false;
    }

    private static bool TryClassifyAArch64Aapcs64(
        StarkTypeSymbol type,
        ConcreteTypeLayout layout,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
        out CAbiAggregateClassification classification)
    {
        classification = null!;

        // AAPCS64 homogeneous floating-point/vector aggregates use SIMD
        // registers and require a distinct recursive HFA/HVA classifier.  Do
        // not coerce them through general-purpose registers until that
        // classification is implemented.  This path deliberately supports
        // only integer-like composites whose leaves are integer, bool, or
        // pointer storage.
        if (!TryCollectStorageUnits(
                type,
                baseOffsetBytes: 0,
                targetInfo,
                namedTypes,
                enumLayouts,
                publishedConcreteLayouts,
                out var units)
            || units.Any(static unit => unit.Kind != CAbiStorageKind.Integer))
        {
            return false;
        }

        if (layout.SizeBytes > 16)
        {
            classification = new CAbiAggregateClassification(
                CAbiAggregatePassKind.Indirect,
                StarkTypeSymbols.RawPointer(type, isMutable: false));
            return true;
        }

        // AAPCS64 C.8: a non-HFA composite no larger than 16 bytes is copied
        // into consecutive general-purpose parameter registers. Clang rounds
        // a parameter of up to 8 bytes to i64 and uses [2 x i64] otherwise.
        // Returns keep their exact integer width up to 8 bytes, then use the
        // same two-register carrier. Keeping these two shapes distinct is
        // required for four-byte structs such as Raylib's Color: i32 return,
        // i64 parameter.
        var returnCarrier = layout.SizeBytes <= 8
            ? StarkTypeSymbols.Integer(layout.SizeBytes * 8, isUnsigned: true)
            : StarkTypeSymbols.FixedArray(StarkTypeSymbols.Integer(64, isUnsigned: true), fixedLength: 2);
        var parameterCarrier = layout.SizeBytes <= 8
            ? StarkTypeSymbols.Integer(64, isUnsigned: true)
            : returnCarrier;
        classification = new CAbiAggregateClassification(
            CAbiAggregatePassKind.Direct,
            returnCarrier,
            [parameterCarrier]);
        return true;
    }

    private static bool TryBuildX86_64SysVDirectCarriers(
        StarkTypeSymbol type,
        ConcreteTypeLayout layout,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
        out IReadOnlyList<StarkTypeSymbol> carriers)
    {
        carriers = [];

        if (layout.SizeBytes <= 0 || layout.SizeBytes > 16)
        {
            return false;
        }

        if (!TryCollectStorageUnits(type, baseOffsetBytes: 0, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts, out var units))
        {
            return false;
        }

        var result = new List<StarkTypeSymbol>(layout.SizeBytes <= 8 ? 1 : 2);
        for (var slotStart = 0; slotStart < layout.SizeBytes; slotStart += 8)
        {
            var slotSize = Math.Min(8, layout.SizeBytes - slotStart);
            var slotUnits = units
                .Where(unit => unit.OffsetBytes < slotStart + slotSize && unit.EndOffsetBytes > slotStart)
                .OrderBy(static unit => unit.OffsetBytes)
                .ToArray();

            if (slotUnits.Length == 0)
            {
                result.Add(BuildIntegerCarrier(slotSize));
                continue;
            }

            if (slotUnits.Any(unit => unit.OffsetBytes < slotStart || unit.EndOffsetBytes > slotStart + slotSize))
            {
                return false;
            }

            if (slotUnits.Any(static unit => unit.Kind == CAbiStorageKind.Integer))
            {
                result.Add(BuildIntegerCarrier(slotSize));
                continue;
            }

            if (slotUnits.Any(static unit => unit.Kind != CAbiStorageKind.Floating))
            {
                return false;
            }

            if (!TryBuildSseCarrier(slotStart, slotSize, slotUnits, out var sseCarrier))
            {
                return false;
            }

            result.Add(sseCarrier);
        }

        carriers = result;
        return carriers.Count > 0;
    }

    private static bool TryBuildSseCarrier(
        int slotStart,
        int slotSize,
        IReadOnlyList<CAbiStorageUnit> slotUnits,
        out StarkTypeSymbol carrier)
    {
        carrier = StarkTypeSymbols.Error;

        if (slotUnits.Count == 1
            && slotUnits[0].OffsetBytes == slotStart
            && slotUnits[0].Type.Kind == StarkTypeKind.Float)
        {
            carrier = slotUnits[0].Type;
            return slotUnits[0].SizeBytes == slotSize || slotSize <= 8;
        }

        if (slotUnits.Count == 2
            && slotUnits[0].OffsetBytes == slotStart
            && slotUnits[1].OffsetBytes == slotStart + 4
            && slotUnits.All(static unit => unit.Type.Kind == StarkTypeKind.Float && unit.Type.BitWidth == 32))
        {
            carrier = StarkTypeSymbols.LlvmVector(StarkTypeSymbols.Float(32), 2);
            return true;
        }

        return false;
    }

    private static StarkTypeSymbol BuildIntegerCarrier(int sizeBytes)
    {
        return sizeBytes switch
        {
            <= 1 => StarkTypeSymbols.Integer(8, isUnsigned: true),
            <= 2 => StarkTypeSymbols.Integer(16, isUnsigned: true),
            <= 4 => StarkTypeSymbols.Integer(32, isUnsigned: true),
            <= 8 => StarkTypeSymbols.Integer(64, isUnsigned: true),
            _ => throw new ArgumentOutOfRangeException(nameof(sizeBytes), "A direct SysV eightbyte carrier cannot exceed 8 bytes.")
        };
    }

    private static bool TryCollectStorageUnits(
        StarkTypeSymbol type,
        int baseOffsetBytes,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
        out IReadOnlyList<CAbiStorageUnit> units)
    {
        var normalizedType = NormalizeType(type);
        var result = new List<CAbiStorageUnit>();
        if (!TryCollectStorageUnits(normalizedType, baseOffsetBytes, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts, result))
        {
            units = [];
            return false;
        }

        units = result;
        return true;
    }

    private static bool TryCollectStorageUnits(
        StarkTypeSymbol type,
        int baseOffsetBytes,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
        List<CAbiStorageUnit> units)
    {
        var normalizedType = NormalizeType(type);
        switch (normalizedType.Kind)
        {
            case StarkTypeKind.Bool:
                units.Add(new CAbiStorageUnit(baseOffsetBytes, 1, CAbiStorageKind.Integer, normalizedType));
                return true;
            case StarkTypeKind.Integer when normalizedType.BitWidth is int bitWidth:
                units.Add(new CAbiStorageUnit(baseOffsetBytes, Math.Max(1, (bitWidth + 7) / 8), CAbiStorageKind.Integer, normalizedType));
                return true;
            case StarkTypeKind.Float when normalizedType.BitWidth is 16 or 32 or 64:
                units.Add(new CAbiStorageUnit(baseOffsetBytes, Math.Max(1, (normalizedType.BitWidth.Value + 7) / 8), CAbiStorageKind.Floating, normalizedType));
                return true;
            case StarkTypeKind.RawPointer:
            case StarkTypeKind.FunctionPointer:
            case StarkTypeKind.Null:
                units.Add(new CAbiStorageUnit(baseOffsetBytes, 8, CAbiStorageKind.Integer, normalizedType));
                return true;
            case StarkTypeKind.FixedArray when normalizedType.ElementType is not null
                                               && normalizedType.FixedLength is int fixedLength
                                               && TryGetSimpleStorageSize(normalizedType.ElementType, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts) is int elementSizeBytes:
                for (var index = 0; index < fixedLength; index++)
                {
                    if (!TryCollectStorageUnits(
                            normalizedType.ElementType,
                            baseOffsetBytes + checked(index * elementSizeBytes),
                            targetInfo,
                            namedTypes,
                            enumLayouts,
                            publishedConcreteLayouts,
                            units))
                    {
                        return false;
                    }
                }

                return true;
            case StarkTypeKind.Named when normalizedType.NamedType is { } name
                                          && namedTypes.TryGetValue(name, out var namedType)
                                          && namedType.Layout is { Kind: StructLayoutKind.C or StructLayoutKind.Explicit }
                                          && TryGetSimpleLayout(normalizedType, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts) is { } layout:
                foreach (var field in namedType.OrderedFields)
                {
                    var fieldLayout = layout.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, field.Name, StringComparison.Ordinal));
                    if (fieldLayout is null)
                    {
                        return false;
                    }

                    if (!TryCollectStorageUnits(field.Type, baseOffsetBytes + fieldLayout.OffsetBytes, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts, units))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    private static int? TryGetSimpleStorageSize(
        StarkTypeSymbol type,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts)
    {
        return TryGetSimpleLayout(type, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts)?.SizeBytes;
    }

    private static ConcreteTypeLayout? TryGetSimpleLayout(
        StarkTypeSymbol type,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts)
    {
        return LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
            type,
            targetInfo,
            namedTypes,
            enumLayouts,
            publishedConcreteLayouts);
    }

    private static bool IsLayoutControlledAggregate(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        return type.Kind switch
        {
            StarkTypeKind.Named when type.NamedType is { } name
                                     && type.TypeArguments is not { Count: > 0 }
                                     && namedTypes.TryGetValue(name, out var namedType)
                                     && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                     && namedType.Layout?.Kind is StructLayoutKind.C or StructLayoutKind.Explicit
                => true,
            _ => false
        };
    }

    private static bool HasMisalignedStorage(
        StarkTypeSymbol type,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
        => HasMisalignedStorage(type, targetInfo, namedTypes, EmptyEnumLayouts, EmptyPublishedLayouts);

    private static bool HasMisalignedStorage(
        StarkTypeSymbol type,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts)
    {
        var normalizedType = NormalizeType(type);
        var layout = TryGetSimpleLayout(normalizedType, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts);
        if (layout?.Fields.Any(static field => field.IsMisaligned) == true)
        {
            return true;
        }

        return normalizedType.Kind switch
        {
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null
                => HasMisalignedStorage(normalizedType.ElementType, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts),
            StarkTypeKind.Named when normalizedType.NamedType is { } name
                                     && namedTypes.TryGetValue(name, out var namedType)
                => namedType.OrderedFields.Any(field => HasMisalignedStorage(field.Type, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts)),
            _ => false
        };
    }

    private static CAbiFamily ResolveCAbiFamily(StarkFfiAbi? abi, LlvmTargetInfo? targetInfo)
    {
        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(targetInfo);
        if (architecture == StarkAsmArchitecture.AArch64)
        {
            return abi is StarkFfiAbi.C or StarkFfiAbi.Aapcs64 or null
                ? CAbiFamily.AArch64Aapcs64
                : CAbiFamily.Unsupported;
        }

        if (architecture != StarkAsmArchitecture.X86_64)
        {
            return CAbiFamily.Unsupported;
        }

        return abi switch
        {
            StarkFfiAbi.SysV => CAbiFamily.X86_64SysV,
            StarkFfiAbi.Win64 => CAbiFamily.X86_64Win64,
            StarkFfiAbi.C or StarkFfiAbi.CDecl or null => IsWindowsTarget(targetInfo)
                ? CAbiFamily.X86_64Win64
                : CAbiFamily.X86_64SysV,
            _ => CAbiFamily.Unsupported
        };
    }

    private static bool IsWindowsTarget(LlvmTargetInfo? targetInfo)
    {
        var triple = targetInfo?.Triple;
        return !string.IsNullOrWhiteSpace(triple)
            && (triple.Contains("-windows-", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("-win32", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("-msvc", StringComparison.OrdinalIgnoreCase)
                || triple.EndsWith("-windows", StringComparison.OrdinalIgnoreCase));
    }

    private static StarkTypeSymbol NormalizeType(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    private enum CAbiFamily
    {
        Unsupported,
        X86_64SysV,
        X86_64Win64,
        AArch64Aapcs64
    }

    private enum CAbiStorageKind
    {
        Integer,
        Floating
    }

    private readonly record struct CAbiStorageUnit(
        int OffsetBytes,
        int SizeBytes,
        CAbiStorageKind Kind,
        StarkTypeSymbol Type)
    {
        public int EndOffsetBytes => OffsetBytes + SizeBytes;
    }

    private static readonly IReadOnlyDictionary<string, ConcreteTypeLayout> EmptyPublishedLayouts =
        new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, EnumLayoutSymbol> EmptyEnumLayouts =
        new Dictionary<string, EnumLayoutSymbol>(StringComparer.Ordinal);
}
