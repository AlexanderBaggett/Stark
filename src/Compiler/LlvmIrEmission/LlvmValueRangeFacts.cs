using System.Globalization;
using System.Numerics;

namespace Stark.Compiler.LlvmIrEmission;

internal static class LlvmValueRangeFacts
{
    public static bool TryBuildRangeMetadataBody(StarkTypeSymbol type, out string metadataBody)
    {
        metadataBody = string.Empty;

        if (!TryGetNonFullValueRange(type, out var bitWidth, out var min, out var max))
        {
            return false;
        }

        var llvmType = $"i{bitWidth}";
        var lower = FormatTwosComplementInteger(min, bitWidth);
        var upperExclusive = FormatTwosComplementInteger(max + BigInteger.One, bitWidth);
        metadataBody = $"!{{{llvmType} {lower}, {llvmType} {upperExclusive}}}";
        return true;
    }

    public static bool TryBuildRangeMetadataBody(
        StarkTypeSymbol type,
        SsaIntegerRangeFact range,
        out string metadataBody)
    {
        metadataBody = string.Empty;

        if (!TryGetNonFullValueRange(type, range, out var bitWidth, out var min, out var max))
        {
            return false;
        }

        var llvmType = $"i{bitWidth}";
        var lower = FormatTwosComplementInteger(min, bitWidth);
        var upperExclusive = FormatTwosComplementInteger(max + BigInteger.One, bitWidth);
        metadataBody = $"!{{{llvmType} {lower}, {llvmType} {upperExclusive}}}";
        return true;
    }

    public static bool TryBuildRangeAttribute(StarkTypeSymbol type, out string attribute)
    {
        attribute = string.Empty;

        if (!TryGetNonFullValueRange(type, out var bitWidth, out var min, out var max))
        {
            return false;
        }

        var llvmType = $"i{bitWidth}";
        var lower = FormatTwosComplementInteger(min, bitWidth);
        var upperExclusive = FormatTwosComplementInteger(max + BigInteger.One, bitWidth);
        attribute = $"range({llvmType} {lower}, {upperExclusive})";
        return true;
    }

    public static bool TryBuildRangeAttribute(StarkTypeSymbol type, SsaIntegerRangeFact range, out string attribute)
    {
        attribute = string.Empty;

        if (!TryGetNonFullValueRange(type, range, out var bitWidth, out var min, out var max))
        {
            return false;
        }

        var llvmType = $"i{bitWidth}";
        var lower = FormatTwosComplementInteger(min, bitWidth);
        var upperExclusive = FormatTwosComplementInteger(max + BigInteger.One, bitWidth);
        attribute = $"range({llvmType} {lower}, {upperExclusive})";
        return true;
    }

    private static bool TryGetNonFullValueRange(
        StarkTypeSymbol type,
        out int bitWidth,
        out BigInteger min,
        out BigInteger max)
    {
        if (!TryGetValueRange(type, out bitWidth, out min, out max))
        {
            return false;
        }

        var valueCount = max - min + BigInteger.One;
        var domainSize = BigInteger.One << bitWidth;
        return valueCount > BigInteger.Zero && valueCount < domainSize;
    }

    private static bool TryGetNonFullValueRange(
        StarkTypeSymbol type,
        SsaIntegerRangeFact range,
        out int bitWidth,
        out BigInteger min,
        out BigInteger max)
    {
        if (!TryGetValueRange(type, out bitWidth, out var typeMin, out var typeMax))
        {
            min = default;
            max = default;
            return false;
        }

        min = BigInteger.Max(range.Min, typeMin);
        max = BigInteger.Min(range.Max, typeMax);
        var valueCount = max - min + BigInteger.One;
        var domainSize = BigInteger.One << bitWidth;
        return valueCount > BigInteger.Zero && valueCount < domainSize;
    }

    private static bool TryGetValueRange(
        StarkTypeSymbol type,
        out int bitWidth,
        out BigInteger min,
        out BigInteger max)
    {
        var normalizedType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        if (normalizedType.Kind == StarkTypeKind.Bool)
        {
            bitWidth = 1;
            min = BigInteger.Zero;
            max = BigInteger.One;
            return true;
        }

        if (normalizedType.Kind != StarkTypeKind.Integer || normalizedType.BitWidth is not int width || width <= 0)
        {
            bitWidth = default;
            min = default;
            max = default;
            return false;
        }

        bitWidth = width;
        if (normalizedType.RangeMin is not null && normalizedType.RangeMax is not null)
        {
            min = normalizedType.RangeMin.Value;
            max = normalizedType.RangeMax.Value;
            return true;
        }

        min = -(BigInteger.One << (width - 1));
        max = (BigInteger.One << (width - 1)) - BigInteger.One;
        return true;
    }

    private static string FormatTwosComplementInteger(BigInteger value, int bitWidth)
    {
        var domainSize = BigInteger.One << bitWidth;
        var normalized = value % domainSize;
        if (normalized < BigInteger.Zero)
        {
            normalized += domainSize;
        }

        var signedThreshold = BigInteger.One << (bitWidth - 1);
        if (normalized >= signedThreshold)
        {
            normalized -= domainSize;
        }

        return normalized.ToString(CultureInfo.InvariantCulture);
    }
}
