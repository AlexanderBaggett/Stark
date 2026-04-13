namespace Stark.Compiler.LlvmIrEmission;

internal sealed record AggregateScalarLeaf(IReadOnlyList<int> Indices, StarkTypeSymbol Type);

internal static class LlvmAggregateEmissionSupport
{
    public static int? TryGetGlobalAlignmentBytes(
        StarkTypeSymbol type,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts)
    {
        return TryGetTargetAwareTypeLayout(type, targetInfo, namedTypes, enumLayouts, new HashSet<string>(StringComparer.Ordinal))?.AlignmentBytes
            ?? TryGetConcreteTypeLayout(type, targetInfo, namedTypes, enumLayouts, publishedConcreteLayouts)?.AlignmentBytes;
    }

    public static ConcreteTypeLayout? TryGetConcreteTypeLayout(
        StarkTypeSymbol type,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts)
    {
        var normalizedType = NormalizeTypeForLayout(type);

        if (normalizedType.Kind == StarkTypeKind.Named
            && normalizedType.NamedType is { } namedType
            && normalizedType.TypeArguments is not { Count: > 0 }
            && publishedConcreteLayouts.TryGetValue(namedType, out var publishedLayout))
        {
            return publishedLayout;
        }

        if (TryGetTargetAwareTypeLayout(normalizedType, targetInfo, namedTypes, enumLayouts, new HashSet<string>(StringComparer.Ordinal)) is { } targetAwareLayout)
        {
            return targetAwareLayout;
        }

        return ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(type, namedTypes, enumLayouts);
    }

    public static int? TryGetReadonlyVectorizationFriendlyAlignmentBytes(
        StarkTypeSymbol type,
        ConcreteTypeLayout? layout)
    {
        var normalizedType = NormalizeTypeForLayout(type);
        if (normalizedType.Kind != StarkTypeKind.FixedArray
            || normalizedType.ElementType is null
            || normalizedType.FixedLength is not int fixedLength
            || fixedLength <= 0
            || !IsScalarNumericArrayData(normalizedType.ElementType))
        {
            return null;
        }

        if (layout is null
            || layout.SizeBytes < 16
            || layout.AlignmentBytes >= 16)
        {
            return null;
        }

        return 16;
    }

    public static NamedTypeSymbol? ResolveNamedTypeSymbol(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        return type.NamedType is not null && namedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
    }

    public static bool TryGetScalarizableNamedAggregateFields(
        NamedTypeSymbol namedType,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        out IReadOnlyList<FieldSymbol> orderedFields)
    {
        if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            orderedFields = namedType.OrderedFields;
            return true;
        }

        if (namedType.Kind == DeclarationKind.Enum
            && enumLayouts.TryGetValue(namedType.Name, out var enumLayout))
        {
            orderedFields = enumLayout.OrderedFields;
            return true;
        }

        orderedFields = Array.Empty<FieldSymbol>();
        return false;
    }

    private static ConcreteTypeLayout? TryGetTargetAwareTypeLayout(
        StarkTypeSymbol type,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        ISet<string> activeNamedTypes)
    {
        var normalizedType = NormalizeTypeForLayout(type);

        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => new ConcreteTypeLayout(1, 1),
            StarkTypeKind.Integer when normalizedType.BitWidth is int bitWidth
                => TryGetTargetAwareScalarLayout(bitWidth, isFloat: false, targetInfo),
            StarkTypeKind.Float when normalizedType.BitWidth is int bitWidth
                => TryGetTargetAwareScalarLayout(bitWidth, isFloat: true, targetInfo),
            StarkTypeKind.RawPointer or StarkTypeKind.Null => TryGetTargetAwarePointerLayout(targetInfo),
            StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.Slice => TryGetTargetAwareViewLayout(targetInfo),
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null && normalizedType.FixedLength is int fixedLength
                => TryGetTargetAwareFixedArrayLayout(normalizedType.ElementType, fixedLength, targetInfo, namedTypes, enumLayouts, activeNamedTypes),
            StarkTypeKind.Named when normalizedType.NamedType is not null
                                     && namedTypes.TryGetValue(normalizedType.NamedType, out var namedType)
                                     && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                => TryGetTargetAwareNamedTypeLayout(namedType, targetInfo, namedTypes, enumLayouts, activeNamedTypes),
            StarkTypeKind.Named when normalizedType.NamedType is not null
                                     && namedTypes.TryGetValue(normalizedType.NamedType, out var enumType)
                                     && enumType.Kind == DeclarationKind.Enum
                                     && enumLayouts.TryGetValue(normalizedType.NamedType, out var enumLayout)
                => TryGetTargetAwareEnumTypeLayout(enumLayout, targetInfo, namedTypes, enumLayouts, activeNamedTypes),
            StarkTypeKind.Named => TryGetTargetAwarePointerLayout(targetInfo),
            _ => null
        };
    }

    private static ConcreteTypeLayout? TryGetTargetAwareScalarLayout(int bitWidth, bool isFloat, LlvmTargetInfo? targetInfo)
    {
        if (bitWidth <= 0)
        {
            return new ConcreteTypeLayout(0, 1);
        }

        var sizeBytes = checked((bitWidth + 7) / 8);
        var alignmentBytes = TryGetTargetAwareScalarAlignmentBytes(bitWidth, isFloat, targetInfo);
        return alignmentBytes is null
            ? null
            : new ConcreteTypeLayout(sizeBytes, alignmentBytes.Value);
    }

    private static ConcreteTypeLayout? TryGetTargetAwarePointerLayout(LlvmTargetInfo? targetInfo)
    {
        var pointerSizeBytes = TryGetTargetPointerSizeBytes(targetInfo);
        var pointerAlignmentBytes = TryGetTargetPointerAlignmentBytes(targetInfo);
        if (pointerSizeBytes is null || pointerAlignmentBytes is null)
        {
            return null;
        }

        return new ConcreteTypeLayout(pointerSizeBytes.Value, pointerAlignmentBytes.Value);
    }

    private static ConcreteTypeLayout? TryGetTargetAwareViewLayout(LlvmTargetInfo? targetInfo)
    {
        var pointerLayout = TryGetTargetAwarePointerLayout(targetInfo);
        var lengthLayout = TryGetTargetAwareScalarLayout(64, isFloat: false, targetInfo);
        if (pointerLayout is null || lengthLayout is null)
        {
            return null;
        }

        var alignmentBytes = Math.Max(pointerLayout.AlignmentBytes, lengthLayout.AlignmentBytes);
        var sizeBytes = AlignTo(pointerLayout.SizeBytes, lengthLayout.AlignmentBytes);
        sizeBytes = checked(sizeBytes + lengthLayout.SizeBytes);
        sizeBytes = AlignTo(sizeBytes, alignmentBytes);
        return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
    }

    private static ConcreteTypeLayout? TryGetTargetAwareFixedArrayLayout(
        StarkTypeSymbol elementType,
        int fixedLength,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        ISet<string> activeNamedTypes)
    {
        var elementLayout = TryGetTargetAwareTypeLayout(elementType, targetInfo, namedTypes, enumLayouts, activeNamedTypes);
        if (elementLayout is null)
        {
            return null;
        }

        try
        {
            return new ConcreteTypeLayout(
                checked(elementLayout.SizeBytes * fixedLength),
                fixedLength == 0 ? 1 : elementLayout.AlignmentBytes);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static ConcreteTypeLayout? TryGetTargetAwareNamedTypeLayout(
        NamedTypeSymbol namedType,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        ISet<string> activeNamedTypes)
    {
        if (!activeNamedTypes.Add(namedType.Name))
        {
            return null;
        }

        try
        {
            return TryGetTargetAwareAggregateLayout(
                namedType.OrderedFields.Select(static field => field.Type),
                targetInfo,
                namedTypes,
                enumLayouts,
                activeNamedTypes);
        }
        finally
        {
            activeNamedTypes.Remove(namedType.Name);
        }
    }

    private static ConcreteTypeLayout? TryGetTargetAwareEnumTypeLayout(
        EnumLayoutSymbol enumLayout,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        ISet<string> activeNamedTypes)
    {
        if (!activeNamedTypes.Add(enumLayout.EnumName))
        {
            return null;
        }

        try
        {
            return TryGetTargetAwareAggregateLayout(
                enumLayout.OrderedFields.Select(static field => field.Type),
                targetInfo,
                namedTypes,
                enumLayouts,
                activeNamedTypes);
        }
        finally
        {
            activeNamedTypes.Remove(enumLayout.EnumName);
        }
    }

    private static ConcreteTypeLayout? TryGetTargetAwareAggregateLayout(
        IEnumerable<StarkTypeSymbol> fieldTypes,
        LlvmTargetInfo? targetInfo,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        ISet<string> activeNamedTypes)
    {
        try
        {
            var sizeBytes = 0;
            var alignmentBytes = 1;

            foreach (var fieldType in fieldTypes)
            {
                var fieldLayout = TryGetTargetAwareTypeLayout(fieldType, targetInfo, namedTypes, enumLayouts, activeNamedTypes);
                if (fieldLayout is null)
                {
                    return null;
                }

                sizeBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                sizeBytes = checked(sizeBytes + fieldLayout.SizeBytes);
                alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
            }

            sizeBytes = AlignTo(sizeBytes, alignmentBytes);
            return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static int? TryGetTargetAwareScalarAlignmentBytes(int bitWidth, bool isFloat, LlvmTargetInfo? targetInfo)
    {
        if (TryGetScalarAlignmentBytesFromDataLayout(bitWidth, isFloat, targetInfo) is { } fromLayout)
        {
            return fromLayout;
        }

        return TryGetTripleArchitecture(targetInfo) switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64
                => bitWidth switch
                {
                    <= 8 => 1,
                    <= 16 => 2,
                    <= 32 => 4,
                    <= 64 => 8,
                    <= 128 => 16,
                    _ => 1
                },
            StarkAsmArchitecture.X86
                => bitWidth switch
                {
                    <= 8 => 1,
                    <= 16 => 2,
                    <= 32 => 4,
                    64 when isFloat => 4,
                    <= 64 => 4,
                    <= 128 => 16,
                    _ => 1
                },
            StarkAsmArchitecture.Arm32
                => bitWidth switch
                {
                    <= 8 => 1,
                    <= 16 => 2,
                    <= 32 => 4,
                    <= 64 => 8,
                    <= 128 => 16,
                    _ => 1
                },
            _ => null
        };
    }

    private static int? TryGetTargetPointerSizeBytes(LlvmTargetInfo? targetInfo)
    {
        if (TryGetPointerLayoutFromDataLayout(targetInfo, out var sizeBytes, out _))
        {
            return sizeBytes;
        }

        return TryGetTripleArchitecture(targetInfo) switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64 => 8,
            StarkAsmArchitecture.X86 or StarkAsmArchitecture.Arm32 => 4,
            _ => null
        };
    }

    private static int? TryGetTargetPointerAlignmentBytes(LlvmTargetInfo? targetInfo)
    {
        if (TryGetPointerLayoutFromDataLayout(targetInfo, out _, out var alignmentBytes))
        {
            return alignmentBytes;
        }

        return TryGetTripleArchitecture(targetInfo) switch
        {
            StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.AArch64 or StarkAsmArchitecture.RiscV64 => 8,
            StarkAsmArchitecture.X86 or StarkAsmArchitecture.Arm32 => 4,
            _ => null
        };
    }

    private static bool TryGetPointerLayoutFromDataLayout(LlvmTargetInfo? targetInfo, out int sizeBytes, out int alignmentBytes)
    {
        sizeBytes = 0;
        alignmentBytes = 0;

        var dataLayout = targetInfo?.DataLayout;
        if (string.IsNullOrWhiteSpace(dataLayout))
        {
            return false;
        }

        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith("p:", StringComparison.Ordinal)
                && !token.StartsWith("p0:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3
                || !int.TryParse(parts[1], out var sizeBits)
                || !int.TryParse(parts[2], out var alignBits))
            {
                continue;
            }

            sizeBytes = BitsToBytes(sizeBits);
            alignmentBytes = BitsToBytes(alignBits);
            return sizeBytes > 0 && alignmentBytes > 0;
        }

        return false;
    }

    private static int? TryGetScalarAlignmentBytesFromDataLayout(int bitWidth, bool isFloat, LlvmTargetInfo? targetInfo)
    {
        var dataLayout = targetInfo?.DataLayout;
        if (string.IsNullOrWhiteSpace(dataLayout))
        {
            return null;
        }

        var prefix = $"{(isFloat ? 'f' : 'i')}{bitWidth}:";
        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var alignBits))
            {
                continue;
            }

            return BitsToBytes(alignBits);
        }

        return null;
    }

    private static StarkAsmArchitecture TryGetTripleArchitecture(LlvmTargetInfo? targetInfo)
    {
        var triple = targetInfo?.Triple;
        if (string.IsNullOrWhiteSpace(triple))
        {
            return StarkAsmArchitecture.Unknown;
        }

        var architecture = triple.Split('-', 2, StringSplitOptions.TrimEntries)[0];
        if (architecture.StartsWith("x86_64", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("amd64", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.X86_64;
        }

        if (architecture.StartsWith("i386", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i486", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i586", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i686", StringComparison.OrdinalIgnoreCase)
            || string.Equals(architecture, "x86", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.X86;
        }

        if (architecture.StartsWith("aarch64", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.AArch64;
        }

        if (architecture.StartsWith("riscv64", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.RiscV64;
        }

        if (architecture.StartsWith("arm", StringComparison.OrdinalIgnoreCase))
        {
            return StarkAsmArchitecture.Arm32;
        }

        return StarkAsmArchitecture.Unknown;
    }

    private static StarkTypeSymbol NormalizeTypeForLayout(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
    }

    private static int BitsToBytes(int bitCount)
    {
        return bitCount <= 0 ? 0 : (bitCount + 7) / 8;
    }

    private static int AlignTo(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static bool IsScalarNumericArrayData(StarkTypeSymbol type)
    {
        var normalizedType = NormalizeTypeForLayout(type);

        return normalizedType.Kind switch
        {
            StarkTypeKind.Bool => true,
            StarkTypeKind.Integer when normalizedType.BitWidth is not null => true,
            StarkTypeKind.Float when normalizedType.BitWidth is not null => true,
            StarkTypeKind.FixedArray when normalizedType.ElementType is not null
                                          && normalizedType.FixedLength is int fixedLength
                                          && fixedLength > 0
                => IsScalarNumericArrayData(normalizedType.ElementType),
            _ => false
        };
    }
}
