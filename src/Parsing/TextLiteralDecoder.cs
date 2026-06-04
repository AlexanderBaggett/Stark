using System.Globalization;
using System.Text;

namespace Stark.Parsing;

internal enum TextLiteralKind
{
    String,
    Character
}

internal readonly record struct TextLiteralDiagnostic(int Offset, string Message);

internal readonly record struct DecodedTextLiteral(string Value)
{
    public bool IsAscii
    {
        get
        {
            foreach (var ch in Value)
            {
                if (ch > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public byte[] Utf8Bytes => Encoding.UTF8.GetBytes(Value);

    public int[] Utf32CodeUnits
    {
        get
        {
            var units = new int[Value.Length];
            for (var index = 0; index < Value.Length; index++)
            {
                units[index] = Value[index];
            }

            return units;
        }
    }
}

internal static class TextLiteralDecoder
{
    private static readonly UTF8Encoding StrictUtf8Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryDecode(
        string literalText,
        TextLiteralKind kind,
        out DecodedTextLiteral decoded,
        out TextLiteralDiagnostic diagnostic)
    {
        var content = GetContent(literalText);
        if (kind == TextLiteralKind.String && IsRawStringLiteral(literalText))
        {
            decoded = new DecodedTextLiteral(content);
            diagnostic = default;
            return true;
        }

        var builder = new StringBuilder(content.Length);

        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (ch != '\\')
            {
                builder.Append(ch);
                continue;
            }

            var escapeOffset = index + 1;
            if (index + 1 >= content.Length)
            {
                decoded = default;
                diagnostic = new TextLiteralDiagnostic(
                    escapeOffset,
                    $"Unterminated escape sequence in {Describe(kind)}.");
                return false;
            }

            var escape = content[index + 1];
            switch (escape)
            {
                case '\\':
                    builder.Append('\\');
                    index++;
                    break;
                case '"':
                    builder.Append('"');
                    index++;
                    break;
                case '\'':
                    builder.Append('\'');
                    index++;
                    break;
                case '0':
                    builder.Append('\0');
                    index++;
                    break;
                case 'b':
                    builder.Append('\b');
                    index++;
                    break;
                case 't':
                    builder.Append('\t');
                    index++;
                    break;
                case 'n':
                    builder.Append('\n');
                    index++;
                    break;
                case 'f':
                    builder.Append('\f');
                    index++;
                    break;
                case 'r':
                    builder.Append('\r');
                    index++;
                    break;
                case 'x':
                    if (!TryDecodeHexEscape(content, index + 2, 2, out var hexValue))
                    {
                        decoded = default;
                        diagnostic = new TextLiteralDiagnostic(
                            escapeOffset,
                            $"Hex escape '\\x' in {Describe(kind)} must use exactly two hex digits.");
                        return false;
                    }

                    builder.Append((char)hexValue);
                    index += 3;
                    break;
                case 'u':
                    if (!TryDecodeHexEscape(content, index + 2, 4, out var unicodeValue))
                    {
                        decoded = default;
                        diagnostic = new TextLiteralDiagnostic(
                            escapeOffset,
                            $"Unicode escape '\\u' in {Describe(kind)} must use exactly four hex digits.");
                        return false;
                    }

                    builder.Append((char)unicodeValue);
                    index += 5;
                    break;
                default:
                    decoded = default;
                    diagnostic = new TextLiteralDiagnostic(
                        escapeOffset,
                        $"Invalid escape sequence '\\{escape}' in {Describe(kind)}.");
                    return false;
            }
        }

        var value = builder.ToString();
        if (kind == TextLiteralKind.Character && value.Length != 1)
        {
            decoded = default;
            diagnostic = new TextLiteralDiagnostic(
                1,
                "Character literals must decode to exactly one character.");
            return false;
        }

        decoded = new DecodedTextLiteral(value);
        diagnostic = default;
        return true;
    }

    public static bool IsAsciiLiteral(string literalText, TextLiteralKind kind)
    {
        return TryDecode(literalText, kind, out var decoded, out _) && decoded.IsAscii;
    }

    public static bool CanUseUtf8Storage(string literalText, TextLiteralKind kind)
    {
        if (!TryDecode(literalText, kind, out var decoded, out _))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8Encoding.GetBytes(decoded.Value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public static bool TryConcatenateAsStringLiteral(
        string leftLiteralText,
        TextLiteralKind leftKind,
        string rightLiteralText,
        TextLiteralKind rightKind,
        out string literalText)
    {
        literalText = string.Empty;
        if (!TryDecode(leftLiteralText, leftKind, out var left, out _)
            || !TryDecode(rightLiteralText, rightKind, out var right, out _))
        {
            return false;
        }

        literalText = EncodeStringLiteral(left.Value + right.Value);
        return true;
    }

    public static string EncodeStringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var ch in value)
        {
            AppendEscapedStringCharacter(builder, ch);
        }

        builder.Append('"');
        return builder.ToString();
    }

    public static byte[] DecodeUtf8BytesOrFallback(string literalText, TextLiteralKind kind)
    {
        if (TryDecode(literalText, kind, out var decoded, out _))
        {
            return decoded.Utf8Bytes;
        }

        return Encoding.UTF8.GetBytes(GetContent(literalText));
    }

    public static int[] DecodeUtf32CodeUnitsOrFallback(string literalText, TextLiteralKind kind)
    {
        if (TryDecode(literalText, kind, out var decoded, out _))
        {
            return decoded.Utf32CodeUnits;
        }

        var content = GetContent(literalText);
        var units = new int[content.Length];
        for (var index = 0; index < content.Length; index++)
        {
            units[index] = content[index];
        }

        return units;
    }

    public static string GetContent(string literalText)
    {
        if (IsRawMultilineStringLiteral(literalText))
        {
            return literalText.Length >= 9 ? literalText[6..^3] : literalText;
        }

        if (IsRawStringLiteral(literalText))
        {
            return literalText.Length >= 5 ? literalText[4..^1] : literalText;
        }

        return literalText.Length >= 2 ? literalText[1..^1] : literalText;
    }

    public static bool IsRawStringLiteral(string literalText)
    {
        return literalText.StartsWith("raw\"", StringComparison.Ordinal);
    }

    public static bool IsRawMultilineStringLiteral(string literalText)
    {
        return literalText.StartsWith("raw\"\"\"", StringComparison.Ordinal)
            && literalText.EndsWith("\"\"\"", StringComparison.Ordinal);
    }

    private static string Describe(TextLiteralKind kind)
    {
        return kind == TextLiteralKind.String ? "string literal" : "character literal";
    }

    private static void AppendEscapedStringCharacter(StringBuilder builder, char ch)
    {
        switch (ch)
        {
            case '\0':
                builder.Append("\\0");
                return;
            case '\b':
                builder.Append("\\b");
                return;
            case '\t':
                builder.Append("\\t");
                return;
            case '\n':
                builder.Append("\\n");
                return;
            case '\f':
                builder.Append("\\f");
                return;
            case '\r':
                builder.Append("\\r");
                return;
            case '\\':
                builder.Append("\\\\");
                return;
            case '"':
                builder.Append("\\\"");
                return;
        }

        if (ch < ' ' || ch > '~')
        {
            builder.Append("\\u");
            builder.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            return;
        }

        builder.Append(ch);
    }

    private static bool TryDecodeHexEscape(string content, int start, int length, out int value)
    {
        value = 0;
        if (start + length > content.Length)
        {
            return false;
        }

        for (var index = 0; index < length; index++)
        {
            var ch = content[start + index];
            if (!TryDecodeHexDigit(ch, out var digit))
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }

    private static bool TryDecodeHexDigit(char ch, out int value)
    {
        if (ch is >= '0' and <= '9')
        {
            value = ch - '0';
            return true;
        }

        if (ch is >= 'a' and <= 'f')
        {
            value = ch - 'a' + 10;
            return true;
        }

        if (ch is >= 'A' and <= 'F')
        {
            value = ch - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }
}
