using System.Globalization;

namespace Stark.Compiler.LlvmIrEmission;

internal static class LlvmStaticArrayConstant
{
    public static (string ArrayType, string Initializer, int AlignmentBytes) Describe(SsaStaticArrayAddressValue value)
    {
        var width = value.Type.ElementType!.BitWidth!.Value;
        var elementType = $"i{width}";
        return (
            $"[{value.Elements.Count} x {elementType}]",
            $"[{string.Join(", ", value.Elements.Select(element => $"{elementType} {element.ToString(CultureInfo.InvariantCulture)}"))}]",
            Math.Max((width + 7) / 8, value.Elements.Count * ((width + 7) / 8) >= 16 ? 16 : 1));
    }
}
