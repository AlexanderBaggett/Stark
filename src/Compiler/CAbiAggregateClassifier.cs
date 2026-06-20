using Stark.Compiler.LlvmIrEmission;

namespace Stark.Compiler;

internal enum CAbiAggregatePassKind
{
    Direct,
    Indirect
}

internal sealed record CAbiAggregateClassification(
    CAbiAggregatePassKind PassKind,
    StarkTypeSymbol LlvmType);

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
            CAbiFamily.X86_64SysV => TryClassifyX86_64SysV(normalizedType, layout, namedTypes, out classification),
            CAbiFamily.X86_64Win64 => TryClassifyX86_64Win64(layout, out classification),
            _ => false
        };
    }

    public static bool IsCarrierType(StarkTypeSymbol sourceType, StarkTypeSymbol llvmType)
    {
        var normalizedSource = NormalizeType(sourceType);
        var normalizedLlvm = NormalizeType(llvmType);
        return normalizedSource != normalizedLlvm
            && normalizedSource.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray
            && normalizedLlvm.Kind is StarkTypeKind.Integer or StarkTypeKind.FixedArray;
    }

    private static bool TryClassifyX86_64SysV(
        StarkTypeSymbol type,
        ConcreteTypeLayout layout,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        out CAbiAggregateClassification classification)
    {
        classification = null!;

        if (layout.SizeBytes > 16 || layout.Fields.Any(static field => field.IsMisaligned))
        {
            classification = new CAbiAggregateClassification(CAbiAggregatePassKind.Indirect, StarkTypeSymbols.RawPointer(type, isMutable: false));
            return true;
        }

        if (ContainsFloatingPointStorage(type, namedTypes))
        {
            return false;
        }

        classification = new CAbiAggregateClassification(
            CAbiAggregatePassKind.Direct,
            BuildIntegerEightbyteCarrier(layout.SizeBytes));
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
                StarkTypeSymbols.Integer(layout.SizeBytes * 8, isUnsigned: true));
            return true;
        }

        return false;
    }

    private static StarkTypeSymbol BuildIntegerEightbyteCarrier(int sizeBytes)
    {
        return sizeBytes switch
        {
            <= 1 => StarkTypeSymbols.Integer(8, isUnsigned: true),
            <= 2 => StarkTypeSymbols.Integer(16, isUnsigned: true),
            <= 4 => StarkTypeSymbols.Integer(32, isUnsigned: true),
            <= 8 => StarkTypeSymbols.Integer(64, isUnsigned: true),
            _ => StarkTypeSymbols.FixedArray(StarkTypeSymbols.Integer(64, isUnsigned: true), 2)
        };
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

    private static bool ContainsFloatingPointStorage(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        var normalizedType = NormalizeType(type);
        return normalizedType.Kind switch
        {
            StarkTypeKind.Float => true,
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null
                => ContainsFloatingPointStorage(normalizedType.ElementType, namedTypes),
            StarkTypeKind.Named when normalizedType.NamedType is { } name
                                     && namedTypes.TryGetValue(name, out var namedType)
                => namedType.OrderedFields.Any(field => ContainsFloatingPointStorage(field.Type, namedTypes)),
            _ => false
        };
    }

    private static CAbiFamily ResolveCAbiFamily(StarkFfiAbi? abi, LlvmTargetInfo? targetInfo)
    {
        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(targetInfo);
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
        X86_64Win64
    }

    private static readonly IReadOnlyDictionary<string, ConcreteTypeLayout> EmptyPublishedLayouts =
        new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);
}
