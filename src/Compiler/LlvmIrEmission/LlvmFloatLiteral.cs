using System.Globalization;

namespace Stark.Compiler.LlvmIrEmission;

/// <summary>
/// Renders f64/f32 constant values as LLVM-valid float literals.
///
/// LLVM's textual IR rejects a float constant that lacks a fractional point
/// (<c>double 1</c>, <c>double 10</c>) or that uses bare scientific notation
/// (<c>double 1E+17</c>) with "integer constant must have integer type". The
/// only form that round-trips every value exactly — integral, scientific,
/// subnormal, inf, nan — is the hex float (<c>0xH…</c>), which is the bit
/// pattern LLVM itself recommends. For f64 it is the 16 uppercase hex digits
/// of the IEEE-754 double bit pattern; for f32 it is the bit pattern of the
/// double the float rounds to (LLVM requires the low 29 mantissa bits to be
/// zero, which <c>(double)(float)value</c> guarantees).
/// </summary>
internal static class LlvmFloatLiteral
{
    /// <summary>
    /// Renders the LLVM literal for an already-parsed numeric value.
    /// <paramref name="bitWidth"/> selects f32 (32) vs f64 (any other width).
    /// </summary>
    public static string Render(double value, int bitWidth)
    {
        var bits = bitWidth == 32
            ? BitConverter.DoubleToUInt64Bits((double)(float)value)
            : BitConverter.DoubleToUInt64Bits(value);
        return $"0x{bits:X16}";
    }

    /// <summary>
    /// Renders the LLVM literal from raw decimal literal text (any
    /// representation produced by the constant evaluators, e.g. <c>1</c>,
    /// <c>10</c>, <c>1E+17</c>, <c>1.5</c>). The text must already have any
    /// f32 suffix stripped. Returns <c>false</c> when the text is not a
    /// parseable finite/non-finite float literal.
    /// </summary>
    public static bool TryRenderLiteralText(string literalText, int bitWidth, out string rendered)
    {
        if (double.TryParse(
                literalText,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var value))
        {
            rendered = Render(value, bitWidth);
            return true;
        }

        rendered = string.Empty;
        return false;
    }
}
