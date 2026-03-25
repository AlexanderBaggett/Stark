using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class StarkTypeResolver
{
    private readonly CompilerPassContext _context;
    private readonly string _stage;
    private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;

    public StarkTypeResolver(
        CompilerPassContext context,
        string stage,
        ModuleGraph moduleGraph,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        _context = context;
        _stage = stage;
        _namedTypes = namedTypes;
    }

    public StarkTypeSymbol ResolveReturnType(StarkParser.ReturnTypeContext returnType, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        return returnType.VOID() is not null
            ? StarkTypeSymbols.Void
            : ResolveType(returnType.type_(), genericParameters, currentModuleName);
    }

    public StarkTypeSymbol ResolveType(StarkParser.Type_Context type, ISet<string>? genericParameters = null, string? currentModuleName = null)
    {
        var result = ResolveNonArrayType(type.nonArrayType(), genericParameters, currentModuleName);

        foreach (var suffix in type.arraySuffix())
        {
            if (suffix.expression() is null)
            {
                result = StarkTypeSymbols.Slice(result);
                continue;
            }

            var length = TryEvaluateConstantInteger(suffix.expression());
            if (length is null)
            {
                ReportError("STK3014", "Fixed array lengths must currently be constant integer expressions.", suffix.expression());
                result = StarkTypeSymbols.FixedArray(result, fixedLength: null);
                continue;
            }

            if (length < 0 || length > int.MaxValue)
            {
                ReportError("STK3014", $"Fixed array length '{length}' is out of range.", suffix.expression());
                result = StarkTypeSymbols.FixedArray(result, fixedLength: null);
                continue;
            }

            result = StarkTypeSymbols.FixedArray(result, (int)length.Value);
        }

        return ApplyQualifiers(result, type.typeQualifier());
    }

    public HashSet<string>? GetGenericParameterNames(StarkParser.TypeParameterListContext? typeParameterList)
    {
        if (typeParameterList is null)
        {
            return null;
        }

        return typeParameterList.typeParameter()
            .Select(static parameter => parameter.GetText())
            .ToHashSet(StringComparer.Ordinal);
    }

    public StarkTypeSymbol ResolveQualifiedType(string qualifiedName, ISet<string>? genericParameters, IToken token, string? currentModuleName = null)
    {
        if (genericParameters?.Contains(qualifiedName) == true)
        {
            return StarkTypeSymbols.Named(qualifiedName);
        }

        if (_namedTypes.ContainsKey(qualifiedName))
        {
            return StarkTypeSymbols.Named(qualifiedName);
        }

        if (!string.IsNullOrWhiteSpace(currentModuleName)
            && !qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            var moduleQualifiedName = $"{currentModuleName}.{qualifiedName}";
            if (_namedTypes.ContainsKey(moduleQualifiedName))
            {
                return StarkTypeSymbols.Named(moduleQualifiedName);
            }
        }

        if (!qualifiedName.Contains('.', StringComparison.Ordinal))
        {
            ReportError("STK3004", $"Unknown type '{qualifiedName}'.", token);
            return StarkTypeSymbols.Error;
        }

        ReportError("STK3004", $"Unknown type '{qualifiedName}'.", token);
        return StarkTypeSymbols.Error;
    }

    private StarkTypeSymbol ResolveNonArrayType(StarkParser.NonArrayTypeContext type, ISet<string>? genericParameters, string? currentModuleName)
    {
        if (type.rawPointerType() is { } rawPointerType)
        {
            var elementType = ResolveType(rawPointerType.type_(), genericParameters, currentModuleName);
            return StarkTypeSymbols.RawPointer(elementType, rawPointerType.RAWMUTPTR() is not null);
        }

        var simpleType = ResolveSimpleType(type.simpleType(), genericParameters, currentModuleName);
        if (type.rangeConstraint() is { } rangeConstraint && simpleType.Kind == StarkTypeKind.Integer)
        {
            var min = ParseSignedIntegerLiteral(rangeConstraint.signedIntegerLiteral(0));
            var max = ParseSignedIntegerLiteral(rangeConstraint.signedIntegerLiteral(1));
            simpleType = StarkTypeSymbols.Integer(simpleType.BitWidth!.Value, min, max);
        }

        return simpleType;
    }

    private StarkTypeSymbol ResolveSimpleType(StarkParser.SimpleTypeContext simpleType, ISet<string>? genericParameters, string? currentModuleName)
    {
        if (simpleType.builtinType() is { } builtinType)
        {
            var text = builtinType.GetText();
            return text switch
            {
                "void" => StarkTypeSymbols.Void,
                "bool" => StarkTypeSymbols.Bool,
                "ascii" => StarkTypeSymbols.Ascii,
                "unicode" => StarkTypeSymbols.Unicode,
                _ when text.StartsWith("i", StringComparison.Ordinal) => StarkTypeSymbols.Integer(int.Parse(text[1..])),
                _ when text.StartsWith("f", StringComparison.Ordinal) => StarkTypeSymbols.Float(int.Parse(text[1..])),
                _ => StarkTypeSymbols.Error
            };
        }

        return ResolveQualifiedType(simpleType.qualifiedName().GetText(), genericParameters, simpleType.Start, currentModuleName);
    }

    private StarkTypeSymbol ApplyQualifiers(StarkTypeSymbol type, IReadOnlyList<StarkParser.TypeQualifierContext> qualifiers)
    {
        if (qualifiers.Count == 0 || type.Kind == StarkTypeKind.Error)
        {
            return type;
        }

        StarkBorrowKind borrowKind = StarkBorrowKind.None;
        StarkAccessKind accessKind = StarkAccessKind.None;
        StarkInitializationKind initializationKind = StarkInitializationKind.None;
        var isMutableView = false;

        foreach (var qualifier in qualifiers)
        {
            var text = qualifier.GetText();
            switch (text)
            {
                case "borrow":
                    borrowKind = ApplyBorrowQualifier(borrowKind, StarkBorrowKind.Borrow, qualifier);
                    break;
                case "retborrow":
                    borrowKind = ApplyBorrowQualifier(borrowKind, StarkBorrowKind.RetBorrow, qualifier);
                    break;
                case "storeborrow":
                    borrowKind = ApplyBorrowQualifier(borrowKind, StarkBorrowKind.StoreBorrow, qualifier);
                    break;
                case "shared":
                    accessKind = ApplyAccessQualifier(accessKind, StarkAccessKind.Shared, qualifier);
                    break;
                case "frozen":
                    accessKind = ApplyAccessQualifier(accessKind, StarkAccessKind.Frozen, qualifier);
                    break;
                case "out":
                    initializationKind = ApplyInitializationQualifier(initializationKind, StarkInitializationKind.Out, qualifier);
                    break;
                case "init":
                    initializationKind = ApplyInitializationQualifier(initializationKind, StarkInitializationKind.Init, qualifier);
                    break;
                case "mut":
                    isMutableView = true;
                    break;
            }
        }

        if (type.Kind == StarkTypeKind.RawPointer
            && (borrowKind != StarkBorrowKind.None || accessKind != StarkAccessKind.None || initializationKind != StarkInitializationKind.None))
        {
            ReportError("STK3018", "Raw pointers cannot be wrapped in safe borrow, access, or initialization qualifiers.", qualifiers[0]);
        }

        return StarkTypeSymbols.ApplyQualifiers(type, borrowKind, accessKind, initializationKind, isMutableView);
    }

    private StarkBorrowKind ApplyBorrowQualifier(
        StarkBorrowKind current,
        StarkBorrowKind next,
        ParserRuleContext context)
    {
        if (current != StarkBorrowKind.None && current != next)
        {
            ReportError("STK3015", "A type may not combine multiple borrow escape qualifiers.", context);
            return current;
        }

        return next;
    }

    private StarkAccessKind ApplyAccessQualifier(
        StarkAccessKind current,
        StarkAccessKind next,
        ParserRuleContext context)
    {
        if (current != StarkAccessKind.None && current != next)
        {
            ReportError("STK3016", "A type may not combine both 'shared' and 'frozen'.", context);
            return current;
        }

        return next;
    }

    private StarkInitializationKind ApplyInitializationQualifier(
        StarkInitializationKind current,
        StarkInitializationKind next,
        ParserRuleContext context)
    {
        if (current != StarkInitializationKind.None && current != next)
        {
            ReportError("STK3017", "A type may not combine both 'out' and 'init'.", context);
            return current;
        }

        return next;
    }

    private void ReportError(string code, string message, ParserRuleContext context)
    {
        _context.Diagnostics.Error(code, message, _stage, Location(context.Start));
    }

    private void ReportError(string code, string message, IToken token)
    {
        _context.Diagnostics.Error(code, message, _stage, Location(token));
    }

    private SourceLocation Location(IToken token) => new(_context.Input.FilePath, token.Line, token.Column + 1);

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
        return literal.MINUS() is null ? value : -value;
    }

    private static BigInteger? TryEvaluateConstantInteger(StarkParser.ExpressionContext expression)
    {
        var postfixExpression = expression.assignmentExpression()
            .conditionalExpression()
            ?.logicalOrExpression()
            ?.logicalAndExpression(0)
            ?.bitwiseOrExpression(0)
            ?.bitwiseXorExpression(0)
            ?.bitwiseAndExpression(0)
            ?.equalityExpression(0)
            ?.relationalExpression(0)
            ?.shiftExpression(0)
            ?.additiveExpression(0)
            ?.multiplicativeExpression(0)
            ?.unaryExpression(0)
            ?.powerExpression()
            ?.postfixExpression();

        if (postfixExpression is not null && postfixExpression.postfixPart().Length == 0)
        {
            var literal = postfixExpression.primaryExpression()
                ?.literal()
                ?.signedIntegerLiteral();

            if (literal is not null)
            {
                return ParseSignedIntegerLiteral(literal);
            }
        }

        return null;
    }
}
