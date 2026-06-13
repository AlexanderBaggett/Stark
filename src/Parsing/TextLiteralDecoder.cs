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
            if (IsRawMultilineStringLiteral(literalText) && content.Contains('\n', StringComparison.Ordinal))
            {
                return TryDecodeRawMultiline(content, out decoded, out diagnostic);
            }

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

    private static bool TryDecodeRawMultiline(
        string content,
        out DecodedTextLiteral decoded,
        out TextLiteralDiagnostic diagnostic)
    {
        if (!TryNormalizeRawMultilineContent(content, out var normalized, out diagnostic))
        {
            decoded = default;
            return false;
        }

        decoded = new DecodedTextLiteral(normalized);
        return true;
    }

    // Multiline raw strings follow C# raw-string-literal rules: content
    // starts on the line after the opening quotes, the closing quotes stand
    // alone on their line, and the whitespace before the closing quotes is
    // stripped from every content line. Shared with interpolated raw
    // literals, which normalize before hole splitting.
    internal static bool TryNormalizeRawMultilineContent(
        string content,
        out string normalized,
        out TextLiteralDiagnostic diagnostic)
    {
        // Offsets in diagnostics are relative to the literal text; the
        // content starts after the six characters of 'raw"""'.
        const int contentBaseOffset = 6;

        normalized = string.Empty;
        var lines = SplitLinesKeepingBreaks(content);
        var openingLine = lines[0].Text;
        for (var index = 0; index < openingLine.Length; index++)
        {
            if (!IsRawIndentationCharacter(openingLine[index]))
            {
                diagnostic = new TextLiteralDiagnostic(
                    contentBaseOffset + index,
                    "Multiline raw string literals must start their content on the line after the opening quotes.");
                return false;
            }
        }

        var closingLine = lines[^1];
        for (var index = 0; index < closingLine.Text.Length; index++)
        {
            if (!IsRawIndentationCharacter(closingLine.Text[index]))
            {
                diagnostic = new TextLiteralDiagnostic(
                    contentBaseOffset + closingLine.Start + index,
                    "The closing quotes of a multiline raw string literal must be on their own line.");
                return false;
            }
        }

        var indentation = closingLine.Text;
        var builder = new StringBuilder(content.Length);
        for (var index = 1; index < lines.Count - 1; index++)
        {
            var line = lines[index];
            if (index > 1)
            {
                builder.Append(lines[index - 1].Break);
            }

            if (line.Text.StartsWith(indentation, StringComparison.Ordinal))
            {
                builder.Append(line.Text, indentation.Length, line.Text.Length - indentation.Length);
                continue;
            }

            if (IsWhitespaceOnly(line.Text))
            {
                continue;
            }

            diagnostic = new TextLiteralDiagnostic(
                contentBaseOffset + line.Start,
                "Lines inside a multiline raw string literal must start with the same whitespace as the closing quotes.");
            return false;
        }

        normalized = builder.ToString();
        diagnostic = default;
        return true;
    }

    private static List<(string Text, string Break, int Start)> SplitLinesKeepingBreaks(string content)
    {
        var lines = new List<(string Text, string Break, int Start)>();
        var lineStart = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != '\n')
            {
                continue;
            }

            var breakStart = index > lineStart && content[index - 1] == '\r' ? index - 1 : index;
            lines.Add((content[lineStart..breakStart], content[breakStart..(index + 1)], lineStart));
            lineStart = index + 1;
        }

        lines.Add((content[lineStart..], string.Empty, lineStart));
        return lines;
    }

    private static bool IsRawIndentationCharacter(char ch)
    {
        return ch is ' ' or '\t';
    }

    private static bool IsWhitespaceOnly(string text)
    {
        foreach (var ch in text)
        {
            if (!IsRawIndentationCharacter(ch))
            {
                return false;
            }
        }

        return true;
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
