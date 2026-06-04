using System.Globalization;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal readonly record struct InterpolatedTextDiagnostic(int Offset, string Message);

internal abstract record InterpolatedTextSegment;

internal sealed record InterpolatedTextRawSegment(string Value) : InterpolatedTextSegment;

internal sealed record InterpolatedTextHoleSegment(
    string SourceText,
    StarkParser.ExpressionContext Expression) : InterpolatedTextSegment;

internal static class InterpolatedText
{
    public static bool TryParse(
        string stringLiteralText,
        out IReadOnlyList<InterpolatedTextSegment> segments,
        out IReadOnlyList<InterpolatedTextDiagnostic> diagnostics)
    {
        var parsedSegments = new List<InterpolatedTextSegment>();
        var reportedDiagnostics = new List<InterpolatedTextDiagnostic>();
        var isRawLiteral = TextLiteralDecoder.IsRawStringLiteral(stringLiteralText);
        var content = TextLiteralDecoder.GetContent(stringLiteralText);
        var raw = new StringBuilder();

        for (var index = 0; index < content.Length;)
        {
            var ch = content[index];
            if (!isRawLiteral && ch == '\\')
            {
                raw.Append(ch);
                if (index + 1 < content.Length)
                {
                    raw.Append(content[index + 1]);
                    index += 2;
                    continue;
                }

                index++;
                continue;
            }

            if (ch == '{')
            {
                if (index + 1 < content.Length && content[index + 1] == '{')
                {
                    raw.Append('{');
                    index += 2;
                    continue;
                }

                FlushRaw(raw, isRawLiteral, parsedSegments, reportedDiagnostics, index + 1);
                if (!TryReadHole(content, index, isRawLiteral, out var holeSource, out var closeIndex))
                {
                    reportedDiagnostics.Add(new InterpolatedTextDiagnostic(
                        index + 1,
                        "Interpolated text is missing a closing '}'. Add '}' after the expression, or write '{{' for a literal '{'."));
                    break;
                }

                if (string.IsNullOrWhiteSpace(holeSource))
                {
                    reportedDiagnostics.Add(new InterpolatedTextDiagnostic(
                        index + 1,
                        "Interpolated text needs an expression between '{' and '}'."));
                    index = closeIndex + 1;
                    continue;
                }

                var expression = StarkSyntax.ParseExpression(holeSource);
                if (!expression.Succeeded)
                {
                    var first = expression.Diagnostics[0];
                    reportedDiagnostics.Add(new InterpolatedTextDiagnostic(
                        index + 1,
                        $"Could not read the expression inside '{{...}}': {first.Message}"));
                    index = closeIndex + 1;
                    continue;
                }

                parsedSegments.Add(new InterpolatedTextHoleSegment(holeSource, expression.Root));
                index = closeIndex + 1;
                continue;
            }

            if (ch == '}')
            {
                if (index + 1 < content.Length && content[index + 1] == '}')
                {
                    raw.Append('}');
                    index += 2;
                    continue;
                }

                reportedDiagnostics.Add(new InterpolatedTextDiagnostic(
                    index + 1,
                    "Interpolated text found a '}' without a matching '{'. Write '}}' for a literal '}'."));
                index++;
                continue;
            }

            raw.Append(ch);
            index++;
        }

        FlushRaw(raw, isRawLiteral, parsedSegments, reportedDiagnostics, content.Length + 1);
        segments = parsedSegments;
        diagnostics = reportedDiagnostics;
        return diagnostics.Count == 0;
    }

    public static bool TryFold(
        IReadOnlyList<InterpolatedTextSegment> segments,
        CompileTimeEvaluationServices services,
        out string literalText,
        out InterpolatedTextDiagnostic diagnostic)
    {
        var value = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment is InterpolatedTextRawSegment raw)
            {
                value.Append(raw.Value);
                continue;
            }

            var hole = (InterpolatedTextHoleSegment)segment;
            if (!CompileTimeExpressionEvaluator.TryEvaluate(hole.Expression, out var constant, services))
            {
                literalText = string.Empty;
                diagnostic = new InterpolatedTextDiagnostic(
                    0,
                    $"Interpolated text with runtime value '{hole.SourceText.Trim()}' needs caller-owned storage. Write a fixed-capacity buffer such as `stack Ascii text[64] = $\"Score: {{score}}\";`, or use System.Text formatting APIs directly.");
                return false;
            }

            if (!TryAppendConstant(value, constant))
            {
                literalText = string.Empty;
                diagnostic = new InterpolatedTextDiagnostic(
                    0,
                    $"Interpolated text does not know how to format '{hole.SourceText.Trim()}' yet.");
                return false;
            }
        }

        literalText = TextLiteralDecoder.EncodeStringLiteral(value.ToString());
        diagnostic = default;
        return true;
    }

    public static bool TryFold(
        string stringLiteralText,
        CompileTimeEvaluationServices services,
        out string literalText,
        out InterpolatedTextDiagnostic diagnostic)
    {
        literalText = string.Empty;
        if (!TryParse(stringLiteralText, out var segments, out var diagnostics))
        {
            diagnostic = diagnostics.Count > 0
                ? diagnostics[0]
                : new InterpolatedTextDiagnostic(0, "Interpolated text could not be parsed.");
            return false;
        }

        return TryFold(segments, services, out literalText, out diagnostic);
    }

    private static bool TryAppendConstant(StringBuilder builder, CompileTimeConstant constant)
    {
        switch (constant.Kind)
        {
            case CompileTimeConstantKind.Integer:
                builder.Append(constant.IntegerValue.ToString(CultureInfo.InvariantCulture));
                return true;
            case CompileTimeConstantKind.Float:
                builder.Append(CompileTimeExpressionEvaluator.FormatFloatLiteral(constant));
                return true;
            case CompileTimeConstantKind.Bool:
                builder.Append(constant.BoolValue ? "true" : "false");
                return true;
            case CompileTimeConstantKind.Text when constant.TextLiteral is not null:
                return TryAppendTextLiteral(builder, constant.TextLiteral);
            default:
                return false;
        }
    }

    private static bool TryAppendTextLiteral(StringBuilder builder, string literalText)
    {
        var kind = literalText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String;
        if (!TextLiteralDecoder.TryDecode(literalText, kind, out var decoded, out _))
        {
            return false;
        }

        builder.Append(decoded.Value);
        return true;
    }

    private static void FlushRaw(
        StringBuilder raw,
        bool isRawLiteral,
        List<InterpolatedTextSegment> segments,
        List<InterpolatedTextDiagnostic> diagnostics,
        int offset)
    {
        if (raw.Length == 0)
        {
            return;
        }

        if (isRawLiteral)
        {
            segments.Add(new InterpolatedTextRawSegment(raw.ToString()));
            raw.Clear();
            return;
        }

        var literalText = $"\"{raw}\"";
        if (TextLiteralDecoder.TryDecode(literalText, TextLiteralKind.String, out var decoded, out var diagnostic))
        {
            segments.Add(new InterpolatedTextRawSegment(decoded.Value));
        }
        else
        {
            diagnostics.Add(new InterpolatedTextDiagnostic(
                Math.Max(1, offset - raw.Length + diagnostic.Offset),
                diagnostic.Message));
        }

        raw.Clear();
    }

    private static bool TryReadHole(
        string content,
        int openIndex,
        bool isRawLiteral,
        out string sourceText,
        out int closeIndex)
    {
        var depth = 0;
        for (var index = openIndex + 1; index < content.Length; index++)
        {
            var ch = content[index];
            if (!isRawLiteral && ch == '\\')
            {
                index++;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch != '}')
            {
                continue;
            }

            if (depth == 0)
            {
                sourceText = content[(openIndex + 1)..index];
                closeIndex = index;
                return true;
            }

            depth--;
        }

        sourceText = string.Empty;
        closeIndex = content.Length;
        return false;
    }
}
