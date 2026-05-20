using System.Numerics;

namespace Stark.Compiler;

internal enum IntegerRangeStorageViolationKind
{
    None,
    NonNegativeSigned,
    WiderThanNeeded
}

internal static class IntegerRangeStorageFacts
{
    private static readonly int[] SupportedIntegerWidths =
    [
        8,
        16,
        24,
        32,
        48,
        64,
        96,
        128,
        192,
        256,
        384,
        512,
        768,
        1024
    ];

    public static void GetIntegerTypeBounds(
        int bitWidth,
        bool isUnsigned,
        out BigInteger min,
        out BigInteger max)
    {
        if (isUnsigned)
        {
            min = BigInteger.Zero;
            max = (BigInteger.One << bitWidth) - BigInteger.One;
            return;
        }

        min = -(BigInteger.One << (bitWidth - 1));
        max = (BigInteger.One << (bitWidth - 1)) - BigInteger.One;
    }

    public static bool TryGetSmallestTypeForRange(
        BigInteger lower,
        BigInteger upper,
        out StarkTypeSymbol type)
    {
        if (lower >= BigInteger.Zero)
        {
            foreach (var width in SupportedIntegerWidths)
            {
                var max = (BigInteger.One << width) - BigInteger.One;
                if (upper <= max)
                {
                    type = StarkTypeSymbols.Integer(width, lower, upper, isUnsigned: true);
                    return true;
                }
            }
        }
        else
        {
            foreach (var width in SupportedIntegerWidths)
            {
                var min = -(BigInteger.One << (width - 1));
                var max = (BigInteger.One << (width - 1)) - BigInteger.One;
                if (lower >= min && upper <= max)
                {
                    type = StarkTypeSymbols.Integer(width, lower, upper);
                    return true;
                }
            }
        }

        type = StarkTypeSymbols.Error;
        return false;
    }

    public static bool TryGetStorageViolation(
        StarkTypeSymbol type,
        out StarkTypeSymbol suggestedType,
        out IntegerRangeStorageViolationKind violationKind)
    {
        suggestedType = StarkTypeSymbols.Error;
        violationKind = IntegerRangeStorageViolationKind.None;

        if (type.Kind != StarkTypeKind.Integer
            || type.BitWidth is not int bitWidth
            || type.RangeMin is not { } lower
            || type.RangeMax is not { } upper
            || !TryGetSmallestTypeForRange(lower, upper, out suggestedType))
        {
            return false;
        }

        if (suggestedType.BitWidth == bitWidth && suggestedType.IsUnsigned == type.IsUnsigned)
        {
            return false;
        }

        violationKind = !type.IsUnsigned && lower >= BigInteger.Zero
            ? IntegerRangeStorageViolationKind.NonNegativeSigned
            : IntegerRangeStorageViolationKind.WiderThanNeeded;
        return true;
    }
}
