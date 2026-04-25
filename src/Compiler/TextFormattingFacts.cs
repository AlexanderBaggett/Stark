using System.Globalization;
using System.Numerics;

namespace Stark.Compiler;

internal readonly record struct FixedTextFormatInfo(
    string FunctionName,
    StarkTypeSymbol ParameterType,
    int Capacity);

internal static class TextFormattingFacts
{
    private static readonly HashSet<int> SupportedIntegerWidths =
    [
        8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024
    ];

    public static bool TryGetFixedBufferFormatInfo(
        StarkTypeSymbol destination,
        StarkTypeSymbol valueType,
        out FixedTextFormatInfo info)
    {
        var suffix = destination.NamedType == StarkTypeSymbols.OwnedUnicodeName
            ? "Unicode"
            : destination.NamedType == StarkTypeSymbols.OwnedAsciiName
                ? "Ascii"
                : null;
        if (suffix is null)
        {
            info = default;
            return false;
        }

        switch (valueType.Kind)
        {
            case StarkTypeKind.Bool:
                info = new FixedTextFormatInfo($"TryFormatBool{suffix}", StarkTypeSymbols.Bool, 5);
                return true;
            case StarkTypeKind.Integer when valueType.BitWidth is int width && SupportedIntegerWidths.Contains(width):
                var useUnsigned = RequiresUnsignedFormatting(valueType, width);
                var prefix = useUnsigned ? "U" : "I";
                var parameterType = useUnsigned
                    ? UnsignedIntegerType(width)
                    : SignedIntegerType(width);
                var capacity = useUnsigned
                    ? DecimalDigitCount((BigInteger.One << width) - 1)
                    : DecimalDigitCount(-(BigInteger.One << (width - 1)));
                info = new FixedTextFormatInfo($"TryFormat{prefix}{width}{suffix}", parameterType, capacity);
                return true;
            case StarkTypeKind.Float when valueType.BitWidth is 32 or 64:
                info = new FixedTextFormatInfo($"TryFormatF{valueType.BitWidth}{suffix}", valueType, 32);
                return true;
            default:
                info = default;
                return false;
        }
    }

    private static bool RequiresUnsignedFormatting(StarkTypeSymbol type, int width)
    {
        if (type.IsUnsigned)
        {
            return true;
        }

        var min = type.RangeMin ?? -(BigInteger.One << (width - 1));
        if (min < BigInteger.Zero)
        {
            return false;
        }

        var signedMax = (BigInteger.One << (width - 1)) - 1;
        var max = type.RangeMax ?? signedMax;
        return max > signedMax;
    }

    private static StarkTypeSymbol SignedIntegerType(int width) =>
        StarkTypeSymbols.Integer(width, -(BigInteger.One << (width - 1)), (BigInteger.One << (width - 1)) - 1);

    private static StarkTypeSymbol UnsignedIntegerType(int width) =>
        StarkTypeSymbols.Integer(width, BigInteger.Zero, (BigInteger.One << width) - 1, isUnsigned: true);

    private static int DecimalDigitCount(BigInteger value) =>
        value.ToString(CultureInfo.InvariantCulture).Length;
}
