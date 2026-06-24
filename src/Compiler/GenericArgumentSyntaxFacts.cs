using System.Globalization;
using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed record ResolvedGenericArgumentList(
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    IReadOnlyList<ComptimeValueArgumentSymbol> ComptimeValueArguments);

internal static class GenericArgumentSyntaxFacts
{
    public static ResolvedGenericArgumentList Resolve(
        StarkParser.TypeArgumentListContext argumentList,
        IReadOnlyList<string> expectedTypeParameters,
        IReadOnlyList<ComptimeGenericParameterSymbol> expectedComptimeParameters,
        Func<StarkParser.Type_Context, StarkTypeSymbol> resolveType,
        Action<string, string, ParserRuleContext> reportError,
        CompileTimeEvaluationServices compileTimeServices = default,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? visibleComptimeParameters = null)
    {
        var syntaxArguments = argumentList.genericArgument();
        var expectedCount = expectedTypeParameters.Count + expectedComptimeParameters.Count;
        if (syntaxArguments.Length != expectedCount)
        {
            reportError(
                "STK3019",
                $"Generic argument list expects {expectedTypeParameters.Count} type argument(s) and {expectedComptimeParameters.Count} comptime value argument(s), but received {syntaxArguments.Length}.",
                argumentList);
        }

        var typeArguments = new List<StarkTypeSymbol>(expectedTypeParameters.Count);
        var valueArguments = new List<ComptimeValueArgumentSymbol>(expectedComptimeParameters.Count);

        foreach (var argument in syntaxArguments)
        {
            if (argument.type_() is { } typeArgument)
            {
                if (typeArguments.Count >= expectedTypeParameters.Count)
                {
                    reportError(
                        "STK3019",
                        $"Generic argument '{argument.GetText()}' is a type argument, but this position expects a comptime value argument.",
                        argument);
                    continue;
                }

                typeArguments.Add(resolveType(typeArgument));
                continue;
            }

            if (typeArguments.Count < expectedTypeParameters.Count)
            {
                reportError(
                    "STK3019",
                    $"Generic argument '{argument.GetText()}' is a comptime value argument, but this position expects type argument '{expectedTypeParameters[typeArguments.Count]}'.",
                    argument);
                continue;
            }

            var valueIndex = valueArguments.Count;
            if (valueIndex >= expectedComptimeParameters.Count)
            {
                reportError(
                    "STK3019",
                    $"Generic argument '{argument.GetText()}' is a comptime value argument, but no comptime value parameter remains.",
                    argument);
                continue;
            }

            var parameter = expectedComptimeParameters[valueIndex];
            if (!TryResolveComptimeValueArgument(
                    argument,
                    parameter,
                    compileTimeServices,
                    visibleComptimeParameters,
                    out var valueArgument))
            {
                reportError(
                    "STK3050",
                    $"Generic argument '{argument.GetText()}' must be a compile-time integer value that fits '{parameter.Type.DisplayName}'.",
                    argument);
                valueArgument = new ComptimeValueArgumentSymbol(parameter.Name, BigInteger.Zero, parameter.Type);
            }

            valueArguments.Add(valueArgument);
        }

        return new ResolvedGenericArgumentList(typeArguments, valueArguments);
    }

    private static bool TryResolveComptimeValueArgument(
        StarkParser.GenericArgumentContext argument,
        ComptimeGenericParameterSymbol parameter,
        CompileTimeEvaluationServices compileTimeServices,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? visibleComptimeParameters,
        out ComptimeValueArgumentSymbol valueArgument)
    {
        if (argument.signedIntegerLiteral() is { } literal)
        {
            var value = ParseSignedIntegerLiteral(literal);
            return TryCreateConcreteValueArgument(parameter, value, out valueArgument);
        }

        if (argument.expression() is not { } expression)
        {
            valueArgument = default!;
            return false;
        }

        if (TryGetSymbolicComptimeArgumentName(argument, expression, visibleComptimeParameters, out var identifier)
            && visibleComptimeParameters?.ContainsKey(identifier) == true)
        {
            valueArgument = new ComptimeValueArgumentSymbol(
                parameter.Name,
                BigInteger.Zero,
                parameter.Type,
                IsSymbolic: true,
                SymbolicSourceName: identifier);
            return true;
        }

        if (!CompileTimeExpressionEvaluator.TryEvaluateInteger(expression, out var computed, compileTimeServices))
        {
            valueArgument = default!;
            return false;
        }

        return TryCreateConcreteValueArgument(parameter, computed, out valueArgument);
    }

    private static bool TryGetSymbolicComptimeArgumentName(
        StarkParser.GenericArgumentContext argument,
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? visibleComptimeParameters,
        out string identifier)
    {
        if (TryGetSingleIdentifierExpression(expression, out identifier))
        {
            if (visibleComptimeParameters?.ContainsKey(identifier) == true)
            {
                return true;
            }
        }

        if (argument.COMPTIME() is null
            || visibleComptimeParameters is not { Count: > 0 })
        {
            identifier = string.Empty;
            return false;
        }

        var text = expression.GetText();
        const string prefix = "comptime";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)
            || text.Length <= prefix.Length)
        {
            identifier = string.Empty;
            return false;
        }

        var candidate = text[prefix.Length..];
        if (!IsIdentifierText(candidate)
            || !visibleComptimeParameters.ContainsKey(candidate))
        {
            identifier = string.Empty;
            return false;
        }

        identifier = candidate;
        return true;
    }

    private static bool TryCreateConcreteValueArgument(
        ComptimeGenericParameterSymbol parameter,
        BigInteger value,
        out ComptimeValueArgumentSymbol valueArgument)
    {
        if (parameter.Type.Kind != StarkTypeKind.Integer
            || !StarkTypeSymbols.IntegerValueFitsEffectiveRange(value, parameter.Type))
        {
            valueArgument = default!;
            return false;
        }

        valueArgument = new ComptimeValueArgumentSymbol(parameter.Name, value, parameter.Type);
        return true;
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText(), CultureInfo.InvariantCulture);
        return literal.MINUS() is null ? value : -value;
    }

    private static bool TryGetSingleIdentifierExpression(StarkParser.ExpressionContext expression, out string identifier)
    {
        var text = expression.GetText();
        if (!IsIdentifierText(text))
        {
            identifier = string.Empty;
            return false;
        }

        identifier = text;
        return true;
    }

    private static bool IsIdentifierText(string text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && (char.IsLetter(text[0]) || text[0] == '_')
            && text.All(static character => char.IsLetterOrDigit(character) || character == '_');
    }
}
