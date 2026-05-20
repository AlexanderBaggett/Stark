using System.Globalization;
using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal enum CompileTimeConstantKind
{
    Integer,
    Float,
    Bool,
    Text,
    Null
}

internal readonly record struct CompileTimeConstant
{
    private CompileTimeConstant(
        CompileTimeConstantKind kind,
        StarkTypeSymbol type,
        BigInteger integerValue,
        double floatValue,
        bool boolValue,
        string? textLiteral)
    {
        Kind = kind;
        Type = type;
        IntegerValue = integerValue;
        FloatValue = floatValue;
        BoolValue = boolValue;
        TextLiteral = textLiteral;
    }

    public CompileTimeConstantKind Kind { get; }
    public StarkTypeSymbol Type { get; }
    public BigInteger IntegerValue { get; }
    public double FloatValue { get; }
    public bool BoolValue { get; }
    public string? TextLiteral { get; }

    public static CompileTimeConstant Integer(BigInteger value, StarkTypeSymbol type) =>
        new(CompileTimeConstantKind.Integer, type, value, default, default, null);

    public static CompileTimeConstant Float(double value, StarkTypeSymbol type) =>
        new(CompileTimeConstantKind.Float, type, default, value, default, null);

    public static CompileTimeConstant Bool(bool value) =>
        new(CompileTimeConstantKind.Bool, StarkTypeSymbols.Bool, default, default, value, null);

    public static CompileTimeConstant Text(string literalText, StarkTypeSymbol type) =>
        new(CompileTimeConstantKind.Text, type, default, default, default, literalText);

    public static CompileTimeConstant Null(StarkTypeSymbol type) =>
        new(CompileTimeConstantKind.Null, type, default, default, default, null);
}

internal delegate bool TryResolveCompileTimeIdentifier(string name, out CompileTimeConstant constant);

internal delegate bool TryEvaluateCompileTimePostfixExpression(
    StarkParser.PostfixExpressionContext expression,
    CompileTimeEvaluationServices services,
    out CompileTimeConstant constant);

internal readonly record struct CompileTimeEvaluationServices(
    TryResolveCompileTimeIdentifier? TryResolveIdentifier = null,
    TryEvaluateCompileTimePostfixExpression? TryEvaluatePostfixExpression = null);

internal static class CompileTimeExpressionEvaluator
{
    private const int MaximumCompileTimeIntegerPowerExponent = 1024;
    private static readonly int[] SupportedIntegerLiteralWidths = [8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024];

    public static bool TryEvaluate(
        StarkParser.ExpressionContext expression,
        out CompileTimeConstant constant,
        CompileTimeEvaluationServices services = default)
    {
        return TryEvaluateAssignmentExpression(expression.assignmentExpression(), services, out constant);
    }

    public static bool TryEvaluate(
        StarkParser.AssignmentExpressionContext expression,
        out CompileTimeConstant constant,
        CompileTimeEvaluationServices services = default)
    {
        return TryEvaluateAssignmentExpression(expression, services, out constant);
    }

    public static bool TryEvaluate(
        ParserRuleContext expression,
        out CompileTimeConstant constant,
        CompileTimeEvaluationServices services = default)
    {
        return expression switch
        {
            StarkParser.ExpressionContext typed => TryEvaluate(typed, out constant, services),
            StarkParser.AssignmentExpressionContext typed => TryEvaluateAssignmentExpression(typed, services, out constant),
            StarkParser.ConditionalExpressionContext typed => TryEvaluateConditionalExpression(typed, services, out constant),
            StarkParser.LogicalOrExpressionContext typed => TryEvaluateLogicalOrExpression(typed, services, out constant),
            StarkParser.LogicalAndExpressionContext typed => TryEvaluateLogicalAndExpression(typed, services, out constant),
            StarkParser.BitwiseOrExpressionContext typed => TryEvaluateBitwiseOrExpression(typed, services, out constant),
            StarkParser.BitwiseXorExpressionContext typed => TryEvaluateBitwiseXorExpression(typed, services, out constant),
            StarkParser.BitwiseAndExpressionContext typed => TryEvaluateBitwiseAndExpression(typed, services, out constant),
            StarkParser.EqualityExpressionContext typed => TryEvaluateEqualityExpression(typed, services, out constant),
            StarkParser.RelationalExpressionContext typed => TryEvaluateRelationalExpression(typed, services, out constant),
            StarkParser.ShiftExpressionContext typed => TryEvaluateShiftExpression(typed, services, out constant),
            StarkParser.AdditiveExpressionContext typed => TryEvaluateAdditiveExpression(typed, services, out constant),
            StarkParser.MultiplicativeExpressionContext typed => TryEvaluateMultiplicativeExpression(typed, services, out constant),
            StarkParser.UnaryExpressionContext typed => TryEvaluateUnaryExpression(typed, services, out constant),
            StarkParser.PowerExpressionContext typed => TryEvaluatePowerExpression(typed, services, out constant),
            StarkParser.PostfixExpressionContext typed => TryEvaluatePostfixExpression(typed, services, out constant),
            StarkParser.PrimaryExpressionContext typed => TryEvaluatePrimaryExpression(typed, services, out constant),
            StarkParser.LiteralContext typed => TryEvaluateLiteral(typed, services, out constant),
            _ => Fail(out constant)
        };

        static bool Fail(out CompileTimeConstant constant)
        {
            constant = default;
            return false;
        }
    }

    public static bool TryEvaluateInteger(
        StarkParser.ExpressionContext expression,
        out BigInteger value,
        CompileTimeEvaluationServices services = default)
    {
        value = BigInteger.Zero;

        if (!TryEvaluate(expression, out var constant, services)
            || constant.Kind != CompileTimeConstantKind.Integer)
        {
            return false;
        }

        value = constant.IntegerValue;
        return true;
    }

    public static bool TryCoerce(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType,
        out CompileTimeConstant coerced)
    {
        coerced = constant;

        if (constant.Type == targetType)
        {
            return true;
        }

        if (constant.Kind == CompileTimeConstantKind.Null && targetType.Kind == StarkTypeKind.RawPointer)
        {
            coerced = CompileTimeConstant.Null(targetType);
            return true;
        }

        if (constant.Kind == CompileTimeConstantKind.Text
            && TryCoerceText(constant, targetType, out coerced))
        {
            return true;
        }

        if (constant.Kind == CompileTimeConstantKind.Bool && targetType.Kind == StarkTypeKind.Bool)
        {
            coerced = CompileTimeConstant.Bool(constant.BoolValue);
            return true;
        }

        if (constant.Kind == CompileTimeConstantKind.Integer
            && targetType.Kind == StarkTypeKind.Integer)
        {
            if (StarkTypeSymbols.IsCompileTimeInteger(targetType))
            {
                coerced = CompileTimeConstant.Integer(constant.IntegerValue, targetType);
                return true;
            }

            if (TryFoldSignedInteger(targetType, constant.IntegerValue, out var integerCoerced))
            {
                coerced = integerCoerced;
                return true;
            }
        }

        if (constant.Kind == CompileTimeConstantKind.Float && targetType.Kind == StarkTypeKind.Float)
        {
            coerced = CompileTimeConstant.Float(constant.FloatValue, targetType);
            return true;
        }

        return false;
    }

    public static string FormatFloatLiteral(CompileTimeConstant constant)
    {
        return FormatFloat(constant.FloatValue);
    }

    public static bool HasFloat32Suffix(string text)
    {
        return text.Length > 0 && text[^1] is 'f' or 'F';
    }

    public static string StripFloatSuffix(string text)
    {
        return HasFloat32Suffix(text) ? text[..^1] : text;
    }

    private static bool TryEvaluateAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (expression.conditionalExpression() is not { } conditionalExpression)
        {
            return false;
        }

        return TryEvaluateConditionalExpression(conditionalExpression, services, out constant);
    }

    private static bool TryEvaluateConditionalExpression(
        StarkParser.ConditionalExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (!TryEvaluateLogicalOrExpression(expression.logicalOrExpression(), services, out var condition))
        {
            return false;
        }

        if (expression.expression().Length == 0)
        {
            constant = condition;
            return true;
        }

        if (condition.Kind != CompileTimeConstantKind.Bool)
        {
            return false;
        }

        return TryEvaluate(
            condition.BoolValue ? expression.expression(0) : expression.expression(1),
            out constant,
            services);
    }

    private static bool TryEvaluateLogicalOrExpression(
        StarkParser.LogicalOrExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;
        var operands = expression.logicalAndExpression();
        if (!TryEvaluateLogicalAndExpression(operands[0], services, out var current))
        {
            return false;
        }

        if (operands.Length == 1)
        {
            constant = current;
            return true;
        }

        if (current.Kind != CompileTimeConstantKind.Bool)
        {
            return false;
        }

        for (var index = 1; index < operands.Length; index++)
        {
            if (current.BoolValue)
            {
                constant = CompileTimeConstant.Bool(true);
                return true;
            }

            if (!TryEvaluateLogicalAndExpression(operands[index], services, out current)
                || current.Kind != CompileTimeConstantKind.Bool)
            {
                return false;
            }
        }

        constant = current;
        return true;
    }

    private static bool TryEvaluateLogicalAndExpression(
        StarkParser.LogicalAndExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;
        var operands = expression.bitwiseOrExpression();
        if (!TryEvaluateBitwiseOrExpression(operands[0], services, out var current))
        {
            return false;
        }

        if (operands.Length == 1)
        {
            constant = current;
            return true;
        }

        if (current.Kind != CompileTimeConstantKind.Bool)
        {
            return false;
        }

        for (var index = 1; index < operands.Length; index++)
        {
            if (!current.BoolValue)
            {
                constant = CompileTimeConstant.Bool(false);
                return true;
            }

            if (!TryEvaluateBitwiseOrExpression(operands[index], services, out current)
                || current.Kind != CompileTimeConstantKind.Bool)
            {
                return false;
            }
        }

        constant = current;
        return true;
    }

    private static bool TryEvaluateBitwiseOrExpression(
        StarkParser.BitwiseOrExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateBinaryChain(
            expression.bitwiseXorExpression(),
            ExtractOperators<StarkParser.BitwiseXorExpressionContext>(expression),
            (StarkParser.BitwiseXorExpressionContext operand, out CompileTimeConstant value) => TryEvaluateBitwiseXorExpression(operand, services, out value),
            requireInteger: true,
            out constant);
    }

    private static bool TryEvaluateBitwiseXorExpression(
        StarkParser.BitwiseXorExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateBinaryChain(
            expression.bitwiseAndExpression(),
            ExtractOperators<StarkParser.BitwiseAndExpressionContext>(expression),
            (StarkParser.BitwiseAndExpressionContext operand, out CompileTimeConstant value) => TryEvaluateBitwiseAndExpression(operand, services, out value),
            requireInteger: true,
            out constant);
    }

    private static bool TryEvaluateBitwiseAndExpression(
        StarkParser.BitwiseAndExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateBinaryChain(
            expression.equalityExpression(),
            ExtractOperators<StarkParser.EqualityExpressionContext>(expression),
            (StarkParser.EqualityExpressionContext operand, out CompileTimeConstant value) => TryEvaluateEqualityExpression(operand, services, out value),
            requireInteger: true,
            out constant);
    }

    private static bool TryEvaluateEqualityExpression(
        StarkParser.EqualityExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateComparisonChain(
            expression.relationalExpression(),
            ExtractOperators<StarkParser.RelationalExpressionContext>(expression),
            (StarkParser.RelationalExpressionContext operand, out CompileTimeConstant value) => TryEvaluateRelationalExpression(operand, services, out value),
            out constant);
    }

    private static bool TryEvaluateRelationalExpression(
        StarkParser.RelationalExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateComparisonChain(
            expression.shiftExpression(),
            ExtractOperators<StarkParser.ShiftExpressionContext>(expression),
            (StarkParser.ShiftExpressionContext operand, out CompileTimeConstant value) => TryEvaluateShiftExpression(operand, services, out value),
            out constant);
    }

    private static bool TryEvaluateShiftExpression(
        StarkParser.ShiftExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateBinaryChain(
            expression.additiveExpression(),
            ExtractOperators<StarkParser.AdditiveExpressionContext>(expression),
            (StarkParser.AdditiveExpressionContext operand, out CompileTimeConstant value) => TryEvaluateAdditiveExpression(operand, services, out value),
            requireInteger: true,
            out constant);
    }

    private static bool TryEvaluateAdditiveExpression(
        StarkParser.AdditiveExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateBinaryChain(
            expression.multiplicativeExpression(),
            ExtractOperators<StarkParser.MultiplicativeExpressionContext>(expression),
            (StarkParser.MultiplicativeExpressionContext operand, out CompileTimeConstant value) => TryEvaluateMultiplicativeExpression(operand, services, out value),
            requireInteger: false,
            out constant);
    }

    private static bool TryEvaluateMultiplicativeExpression(
        StarkParser.MultiplicativeExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        return TryEvaluateBinaryChain(
            expression.unaryExpression(),
            ExtractOperators<StarkParser.UnaryExpressionContext>(expression),
            (StarkParser.UnaryExpressionContext operand, out CompileTimeConstant value) => TryEvaluateUnaryExpression(operand, services, out value),
            requireInteger: false,
            out constant);
    }

    private static bool TryEvaluateUnaryExpression(
        StarkParser.UnaryExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (expression.powerExpression() is { } powerExpression)
        {
            return TryEvaluatePowerExpression(powerExpression, services, out constant);
        }

        if (expression.conversionType() is not null)
        {
            return false;
        }

        var op = expression.unaryOperator()?.GetText();
        if (op is null
            || !TryEvaluateUnaryExpression(expression.unaryExpression(), services, out var operand))
        {
            return false;
        }

        return op switch
        {
            "+" => TryCopyUnaryValue(operand, out constant),
            "-" when operand.Kind == CompileTimeConstantKind.Integer => TryFoldIntegerNegate(operand, out constant),
            "-" when operand.Kind == CompileTimeConstantKind.Float => TryFoldFloatNegate(operand, out constant),
            "-%" when operand.Kind == CompileTimeConstantKind.Integer => TryFoldWrappingNegate(operand, out constant),
            "!" when operand.Kind == CompileTimeConstantKind.Bool => TryFoldLogicalNot(operand, out constant),
            "~" when operand.Kind == CompileTimeConstantKind.Integer => TryFoldBitwiseNot(operand, out constant),
            _ => false
        };
    }

    private static bool TryEvaluatePowerExpression(
        StarkParser.PowerExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (!TryEvaluatePostfixExpression(expression.postfixExpression(), services, out var left))
        {
            return false;
        }

        if (expression.unaryExpression() is not { } rightExpression)
        {
            constant = left;
            return true;
        }

        if (!TryEvaluateUnaryExpression(rightExpression, services, out var right))
        {
            return false;
        }

        return TryEvaluateBinary("**", left, right, requireInteger: false, out constant);
    }

    private static bool TryEvaluatePostfixExpression(
        StarkParser.PostfixExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (expression.postfixPart().Length != 0)
        {
            return services.TryEvaluatePostfixExpression is not null
                && services.TryEvaluatePostfixExpression(expression, services, out constant);
        }

        return TryEvaluatePrimaryExpression(expression.primaryExpression(), services, out constant);
    }

    private static bool TryEvaluatePrimaryExpression(
        StarkParser.PrimaryExpressionContext expression,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (expression.literal() is { } literal)
        {
            return TryEvaluateLiteral(literal, services, out constant);
        }

        if (expression.Identifier() is { } identifier)
        {
            return services.TryResolveIdentifier is not null
                && services.TryResolveIdentifier(identifier.GetText(), out constant);
        }

        if (expression.qualifiedName() is { } qualifiedName)
        {
            return services.TryResolveIdentifier is not null
                && services.TryResolveIdentifier(qualifiedName.GetText(), out constant);
        }

        if (expression.expression() is { } groupedExpression)
        {
            return TryEvaluate(groupedExpression, out constant, services);
        }

        return false;
    }

    private static bool TryEvaluateLiteral(
        StarkParser.LiteralContext literal,
        CompileTimeEvaluationServices services,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (literal.signedIntegerLiteral() is { } integerLiteral)
        {
            var value = ParseSignedIntegerLiteral(integerLiteral);
            constant = CompileTimeConstant.Integer(value, InferIntegerLiteralType(value));
            return true;
        }

        if (literal.FloatLiteral() is { } floatLiteral
            && double.TryParse(
                StripFloatSuffix(floatLiteral.GetText()),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var floatValue))
        {
            constant = CompileTimeConstant.Float(
                floatValue,
                HasFloat32Suffix(floatLiteral.GetText())
                    ? StarkTypeSymbols.Float(32)
                    : StarkTypeSymbols.Float(64));
            return true;
        }

        if (literal.TRUE() is not null)
        {
            constant = CompileTimeConstant.Bool(true);
            return true;
        }

        if (literal.FALSE() is not null)
        {
            constant = CompileTimeConstant.Bool(false);
            return true;
        }

        if (literal.NULL() is not null)
        {
            constant = CompileTimeConstant.Null(StarkTypeSymbols.Null);
            return true;
        }

        if (literal.DOLLAR() is not null && literal.StringLiteral() is { } interpolatedStringLiteral)
        {
            if (!InterpolatedText.TryFold(
                    interpolatedStringLiteral.GetText(),
                    services,
                    out var literalText,
                    out _))
            {
                return false;
            }

            constant = CompileTimeConstant.Text(
                literalText,
                TextLiteralDecoder.CanUseUtf8Storage(literalText, TextLiteralKind.String)
                    ? StarkTypeSymbols.Ascii
                    : StarkTypeSymbols.Unicode);
            return true;
        }

        if (literal.StringLiteral() is { } stringLiteral)
        {
            constant = CompileTimeConstant.Text(
                stringLiteral.GetText(),
                TextLiteralDecoder.CanUseUtf8Storage(stringLiteral.GetText(), TextLiteralKind.String)
                    ? StarkTypeSymbols.Ascii
                    : StarkTypeSymbols.Unicode);
            return true;
        }

        if (literal.CharacterLiteral() is { } characterLiteral)
        {
            constant = CompileTimeConstant.Text(
                characterLiteral.GetText(),
                TextLiteralDecoder.CanUseUtf8Storage(characterLiteral.GetText(), TextLiteralKind.Character)
                    ? StarkTypeSymbols.Ascii
                    : StarkTypeSymbols.Unicode);
            return true;
        }

        return false;
    }

    private static bool TryEvaluateBinaryChain<TOperand>(
        IReadOnlyList<TOperand> operands,
        IReadOnlyList<string> operators,
        TryEvaluateOperand<TOperand> evaluateOperand,
        bool requireInteger,
        out CompileTimeConstant constant)
        where TOperand : ParserRuleContext
    {
        constant = default;

        if (!evaluateOperand(operands[0], out var current))
        {
            return false;
        }

        if (operators.Count == 0)
        {
            constant = current;
            return true;
        }

        for (var index = 1; index < operands.Count; index++)
        {
            if (!evaluateOperand(operands[index], out var next)
                || !TryEvaluateBinary(operators[index - 1], current, next, requireInteger, out current))
            {
                return false;
            }
        }

        constant = current;
        return true;
    }

    private static bool TryEvaluateComparisonChain<TOperand>(
        IReadOnlyList<TOperand> operands,
        IReadOnlyList<string> operators,
        TryEvaluateOperand<TOperand> evaluateOperand,
        out CompileTimeConstant constant)
        where TOperand : ParserRuleContext
    {
        constant = default;

        if (!evaluateOperand(operands[0], out var left))
        {
            return false;
        }

        if (operators.Count == 0)
        {
            constant = left;
            return true;
        }

        for (var index = 0; index < operators.Count; index++)
        {
            if (!evaluateOperand(operands[index + 1], out var right)
                || !TryEvaluatePairComparison(operators[index], left, right, out var comparison))
            {
                return false;
            }

            if (!comparison.BoolValue)
            {
                constant = comparison;
                return true;
            }

            left = right;
        }

        constant = CompileTimeConstant.Bool(true);
        return true;
    }

    private static bool TryEvaluateBinary(
        string operatorText,
        CompileTimeConstant left,
        CompileTimeConstant right,
        bool requireInteger,
        out CompileTimeConstant constant)
    {
        constant = default;
        var resultType = FindCommonType(left.Type, right.Type);
        if (resultType.Kind == StarkTypeKind.Error)
        {
            return false;
        }

        if (requireInteger && resultType.Kind != StarkTypeKind.Integer)
        {
            return false;
        }

        if (!TryCoerce(left, resultType, out var coercedLeft)
            || !TryCoerce(right, resultType, out var coercedRight))
        {
            return false;
        }

        if (operatorText == "+"
            && coercedLeft.Kind == CompileTimeConstantKind.Text
            && coercedRight.Kind == CompileTimeConstantKind.Text)
        {
            return TryFoldTextConcatenation(coercedLeft, coercedRight, out constant);
        }

        if (coercedLeft.Kind == CompileTimeConstantKind.Integer && coercedRight.Kind == CompileTimeConstantKind.Integer)
        {
            return TryFoldIntegerBinary(operatorText, coercedLeft, coercedRight, out constant);
        }

        if (coercedLeft.Kind == CompileTimeConstantKind.Float && coercedRight.Kind == CompileTimeConstantKind.Float)
        {
            return TryFoldFloatBinary(operatorText, coercedLeft, coercedRight, out constant);
        }

        return false;
    }

    private static bool TryEvaluatePairComparison(
        string operatorText,
        CompileTimeConstant left,
        CompileTimeConstant right,
        out CompileTimeConstant constant)
    {
        constant = default;

        if (left.Kind == CompileTimeConstantKind.Bool
            && right.Kind == CompileTimeConstantKind.Bool
            && operatorText is "==" or "!=")
        {
            constant = CompileTimeConstant.Bool(
                operatorText == "=="
                    ? left.BoolValue == right.BoolValue
                    : left.BoolValue != right.BoolValue);
            return true;
        }

        if (left.Kind == CompileTimeConstantKind.Null
            && right.Kind == CompileTimeConstantKind.Null
            && operatorText is "==" or "!=")
        {
            constant = CompileTimeConstant.Bool(operatorText == "==");
            return true;
        }

        return TryEvaluateBinary(operatorText, left, right, requireInteger: false, out constant);
    }

    private static bool TryFoldIntegerBinary(
        string operatorText,
        CompileTimeConstant left,
        CompileTimeConstant right,
        out CompileTimeConstant constant)
    {
        constant = default;
        var targetType = left.Type;
        var bitWidth = targetType.BitWidth ?? 0;
        if (bitWidth <= 0)
        {
            return StarkTypeSymbols.IsCompileTimeInteger(targetType)
                && TryFoldCompileTimeIntegerBinary(operatorText, left.IntegerValue, right.IntegerValue, out constant);
        }

        return operatorText switch
        {
            "+" => TryFoldSignedInteger(targetType, left.IntegerValue + right.IntegerValue, out constant),
            "-" => TryFoldSignedInteger(targetType, left.IntegerValue - right.IntegerValue, out constant),
            "*" => TryFoldSignedInteger(targetType, left.IntegerValue * right.IntegerValue, out constant),
            "**" when TryGetValidPowerExponent(right.IntegerValue, out var exponent) => TryFoldIntegerPower(left.IntegerValue, exponent, out constant),
            "+%" => TryWrapSignedInteger(targetType, left.IntegerValue + right.IntegerValue, out constant),
            "-%" => TryWrapSignedInteger(targetType, left.IntegerValue - right.IntegerValue, out constant),
            "*%" => TryWrapSignedInteger(targetType, left.IntegerValue * right.IntegerValue, out constant),
            "+|" => TryClampSignedInteger(targetType, left.IntegerValue + right.IntegerValue, out constant),
            "-|" => TryClampSignedInteger(targetType, left.IntegerValue - right.IntegerValue, out constant),
            "*|" => TryClampSignedInteger(targetType, left.IntegerValue * right.IntegerValue, out constant),
            "/" when !right.IntegerValue.IsZero => TryFoldSignedInteger(targetType, left.IntegerValue / right.IntegerValue, out constant),
            "%" when !right.IntegerValue.IsZero => TryFoldSignedInteger(targetType, left.IntegerValue % right.IntegerValue, out constant),
            "&" => TryFoldSignedInteger(targetType, left.IntegerValue & right.IntegerValue, out constant),
            "^" => TryFoldSignedInteger(targetType, left.IntegerValue ^ right.IntegerValue, out constant),
            "|" => TryFoldSignedInteger(targetType, left.IntegerValue | right.IntegerValue, out constant),
            "<<" when TryGetValidShiftAmount(right.IntegerValue, bitWidth, out var leftShift) => TryFoldSignedInteger(targetType, left.IntegerValue << leftShift, out constant),
            ">>" when TryGetValidShiftAmount(right.IntegerValue, bitWidth, out var rightShift) => TryFoldSignedInteger(targetType, left.IntegerValue >> rightShift, out constant),
            "==" => TryBoolConstant(left.IntegerValue == right.IntegerValue, out constant),
            "!=" => TryBoolConstant(left.IntegerValue != right.IntegerValue, out constant),
            "<" => TryBoolConstant(left.IntegerValue < right.IntegerValue, out constant),
            "<=" => TryBoolConstant(left.IntegerValue <= right.IntegerValue, out constant),
            ">" => TryBoolConstant(left.IntegerValue > right.IntegerValue, out constant),
            ">=" => TryBoolConstant(left.IntegerValue >= right.IntegerValue, out constant),
            _ => false
        };
    }

    private static bool TryFoldIntegerPower(BigInteger baseValue, int exponent, out CompileTimeConstant constant)
    {
        var value = BigInteger.Pow(baseValue, exponent);
        constant = CompileTimeConstant.Integer(value, InferIntegerLiteralType(value));
        return true;
    }

    private static bool TryFoldCompileTimeIntegerBinary(
        string operatorText,
        BigInteger left,
        BigInteger right,
        out CompileTimeConstant constant)
    {
        constant = default;
        return operatorText switch
        {
            "+" => TryIntegerLiteralConstant(left + right, out constant),
            "-" => TryIntegerLiteralConstant(left - right, out constant),
            "*" => TryIntegerLiteralConstant(left * right, out constant),
            "**" when TryGetValidPowerExponent(right, out var exponent) => TryFoldIntegerPower(left, exponent, out constant),
            "/" when !right.IsZero => TryIntegerLiteralConstant(left / right, out constant),
            "%" when !right.IsZero => TryIntegerLiteralConstant(left % right, out constant),
            "&" => TryIntegerLiteralConstant(left & right, out constant),
            "^" => TryIntegerLiteralConstant(left ^ right, out constant),
            "|" => TryIntegerLiteralConstant(left | right, out constant),
            "<<" when TryGetValidCompileTimeShiftAmount(right, out var leftShift) => TryIntegerLiteralConstant(left << leftShift, out constant),
            ">>" when TryGetValidCompileTimeShiftAmount(right, out var rightShift) => TryIntegerLiteralConstant(left >> rightShift, out constant),
            "==" => TryBoolConstant(left == right, out constant),
            "!=" => TryBoolConstant(left != right, out constant),
            "<" => TryBoolConstant(left < right, out constant),
            "<=" => TryBoolConstant(left <= right, out constant),
            ">" => TryBoolConstant(left > right, out constant),
            ">=" => TryBoolConstant(left >= right, out constant),
            _ => false
        };
    }

    private static bool TryGetValidPowerExponent(BigInteger value, out int exponent)
    {
        if (value < BigInteger.Zero || value > MaximumCompileTimeIntegerPowerExponent)
        {
            exponent = 0;
            return false;
        }

        exponent = (int)value;
        return true;
    }

    private static bool TryFoldFloatBinary(
        string operatorText,
        CompileTimeConstant left,
        CompileTimeConstant right,
        out CompileTimeConstant constant)
    {
        constant = default;
        return operatorText switch
        {
            "+" => TryFloatConstant(left.FloatValue + right.FloatValue, left.Type, out constant),
            "-" => TryFloatConstant(left.FloatValue - right.FloatValue, left.Type, out constant),
            "*" => TryFloatConstant(left.FloatValue * right.FloatValue, left.Type, out constant),
            "/" => TryFloatConstant(left.FloatValue / right.FloatValue, left.Type, out constant),
            "**" => TryFloatConstant(Math.Pow(left.FloatValue, right.FloatValue), left.Type, out constant),
            "==" => TryBoolConstant(left.FloatValue == right.FloatValue, out constant),
            "!=" => TryBoolConstant(left.FloatValue != right.FloatValue, out constant),
            "<" => TryBoolConstant(left.FloatValue < right.FloatValue, out constant),
            "<=" => TryBoolConstant(left.FloatValue <= right.FloatValue, out constant),
            ">" => TryBoolConstant(left.FloatValue > right.FloatValue, out constant),
            ">=" => TryBoolConstant(left.FloatValue >= right.FloatValue, out constant),
            _ => false
        };
    }

    private static bool TryFoldTextConcatenation(
        CompileTimeConstant left,
        CompileTimeConstant right,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (left.TextLiteral is null || right.TextLiteral is null)
        {
            return false;
        }

        if (!TextLiteralDecoder.TryConcatenateAsStringLiteral(
                left.TextLiteral,
                GetTextLiteralKind(left.TextLiteral),
                right.TextLiteral,
                GetTextLiteralKind(right.TextLiteral),
                out var literalText))
        {
            return false;
        }

        constant = CompileTimeConstant.Text(literalText, left.Type);
        return true;
    }

    private static bool TryCopyUnaryValue(CompileTimeConstant operand, out CompileTimeConstant constant)
    {
        constant = operand;
        return operand.Kind is CompileTimeConstantKind.Integer or CompileTimeConstantKind.Float;
    }

    private static bool TryFoldIntegerNegate(CompileTimeConstant operand, out CompileTimeConstant constant)
    {
        var value = -operand.IntegerValue;
        if (StarkTypeSymbols.IntegerValueFitsEffectiveRange(value, operand.Type))
        {
            constant = CompileTimeConstant.Integer(value, operand.Type);
            return true;
        }

        return TryIntegerLiteralConstant(value, out constant);
    }

    private static bool TryFoldFloatNegate(CompileTimeConstant operand, out CompileTimeConstant constant)
    {
        return TryFloatConstant(-operand.FloatValue, operand.Type, out constant);
    }

    private static bool TryFoldWrappingNegate(CompileTimeConstant operand, out CompileTimeConstant constant)
    {
        return TryWrapSignedInteger(operand.Type, -operand.IntegerValue, out constant);
    }

    private static bool TryFoldLogicalNot(CompileTimeConstant operand, out CompileTimeConstant constant)
    {
        constant = CompileTimeConstant.Bool(!operand.BoolValue);
        return true;
    }

    private static bool TryFoldBitwiseNot(CompileTimeConstant operand, out CompileTimeConstant constant)
    {
        constant = default;
        var bitWidth = operand.Type.BitWidth ?? 0;
        if (bitWidth <= 0)
        {
            return false;
        }

        var mask = (BigInteger.One << bitWidth) - 1;
        var twosComplement = operand.IntegerValue & mask;
        var inverted = (~twosComplement) & mask;
        var folded = operand.Type.IsUnsigned ? inverted : FromTwosComplement(inverted, bitWidth);
        if (StarkTypeSymbols.IntegerValueFitsEffectiveRange(folded, operand.Type))
        {
            constant = CompileTimeConstant.Integer(folded, operand.Type);
            return true;
        }

        return TryIntegerLiteralConstant(folded, out constant);
    }

    private static bool TryBoolConstant(bool value, out CompileTimeConstant constant)
    {
        constant = CompileTimeConstant.Bool(value);
        return true;
    }

    private static bool TryIntegerLiteralConstant(BigInteger value, out CompileTimeConstant constant)
    {
        constant = CompileTimeConstant.Integer(value, InferIntegerLiteralType(value));
        return true;
    }

    private static bool TryFloatConstant(double value, StarkTypeSymbol type, out CompileTimeConstant constant)
    {
        constant = CompileTimeConstant.Float(value, type);
        return true;
    }

    private static bool TryFoldSignedInteger(StarkTypeSymbol type, BigInteger value, out CompileTimeConstant constant)
    {
        if (TryFitInteger(value, type, out var fitted))
        {
            constant = CompileTimeConstant.Integer(fitted, type);
            return true;
        }

        constant = default;
        return false;
    }

    private static bool TryWrapSignedInteger(StarkTypeSymbol type, BigInteger value, out CompileTimeConstant constant)
    {
        constant = default;

        if (type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            return false;
        }

        var modulus = BigInteger.One << bitWidth;
        var normalized = ((value % modulus) + modulus) % modulus;
        var wrapped = type.IsUnsigned ? normalized : FromTwosComplement(normalized, bitWidth);
        constant = CompileTimeConstant.Integer(wrapped, type);
        return true;
    }

    private static bool TryClampSignedInteger(StarkTypeSymbol type, BigInteger value, out CompileTimeConstant constant)
    {
        constant = default;

        if (!TryGetIntegerBounds(type, out var min, out var max))
        {
            return false;
        }

        var clamped = value < min ? min : value > max ? max : value;
        constant = CompileTimeConstant.Integer(clamped, type);
        return true;
    }

    private static bool TryCoerceText(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType,
        out CompileTimeConstant coerced)
    {
        coerced = constant;

        if (constant.Kind != CompileTimeConstantKind.Text || constant.TextLiteral is null)
        {
            return false;
        }

        if (constant.Type.Kind == targetType.Kind
            && constant.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
        {
            coerced = CompileTimeConstant.Text(constant.TextLiteral, targetType);
            return true;
        }

        if (constant.Type.Kind == StarkTypeKind.Ascii && targetType.Kind == StarkTypeKind.Unicode)
        {
            coerced = CompileTimeConstant.Text(constant.TextLiteral, targetType);
            return true;
        }

        var kind = constant.TextLiteral.StartsWith('\'')
            ? TextLiteralKind.Character
            : TextLiteralKind.String;

        if (constant.Type.Kind == StarkTypeKind.Unicode
            && targetType.Kind == StarkTypeKind.Ascii
            && TextLiteralDecoder.CanUseUtf8Storage(constant.TextLiteral, kind))
        {
            coerced = CompileTimeConstant.Text(constant.TextLiteral, targetType);
            return true;
        }

        return false;
    }

    private static bool TryFitInteger(BigInteger value, StarkTypeSymbol type, out BigInteger fitted)
    {
        fitted = value;
        if (!TryGetIntegerBounds(type, out var min, out var max))
        {
            return false;
        }

        return value >= min && value <= max;
    }

    private static bool TryGetIntegerBounds(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        return StarkTypeSymbols.TryGetEffectiveIntegerBounds(type, out min, out max);
    }

    private static bool TryGetValidShiftAmount(BigInteger value, int bitWidth, out int shift)
    {
        shift = 0;
        if (value < 0 || value >= bitWidth || value > int.MaxValue)
        {
            return false;
        }

        shift = (int)value;
        return true;
    }

    private static bool TryGetValidCompileTimeShiftAmount(BigInteger value, out int shift)
    {
        shift = 0;
        if (value < 0 || value > MaximumCompileTimeIntegerPowerExponent)
        {
            return false;
        }

        shift = (int)value;
        return true;
    }

    private static BigInteger FromTwosComplement(BigInteger value, int bitWidth)
    {
        var signBit = BigInteger.One << (bitWidth - 1);
        return (value & signBit) != 0
            ? value - (BigInteger.One << bitWidth)
            : value;
    }

    private static string FormatFloat(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
        return literal.MINUS() is null ? value : -value;
    }

    private static StarkTypeSymbol InferIntegerLiteralType(BigInteger value)
    {
        foreach (var width in SupportedIntegerLiteralWidths)
        {
            var min = -(BigInteger.One << (width - 1));
            var max = (BigInteger.One << (width - 1)) - 1;
            if (value >= min && value <= max)
            {
                return StarkTypeSymbols.Integer(width, value, value);
            }
        }

        return StarkTypeSymbols.CompileTimeInteger;
    }

    private static StarkTypeSymbol FindCommonType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (left.Kind == StarkTypeKind.Error || right.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Integer)
        {
            if (StarkTypeSymbols.IsCompileTimeInteger(left)
                || StarkTypeSymbols.IsCompileTimeInteger(right))
            {
                return StarkTypeSymbols.CompileTimeInteger;
            }

            return StarkTypeSymbols.Integer(
                Math.Max(left.BitWidth ?? 0, right.BitWidth ?? 0),
                isUnsigned: left.IsUnsigned && right.IsUnsigned);
        }

        if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Float)
        {
            return StarkTypeSymbols.Float(Math.Max(left.BitWidth ?? 32, right.BitWidth ?? 32));
        }

        if (IsTextType(left) && IsTextType(right))
        {
            return left.Kind == StarkTypeKind.Unicode || right.Kind == StarkTypeKind.Unicode
                ? StarkTypeSymbols.Unicode
                : StarkTypeSymbols.Ascii;
        }

        if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Integer)
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Float)
        {
            return right;
        }

        if (left.Kind == StarkTypeKind.Bool && right.Kind == StarkTypeKind.Bool)
        {
            return StarkTypeSymbols.Bool;
        }

        if (left.Kind == StarkTypeKind.RawPointer && right.Kind == StarkTypeKind.Null)
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Null && right.Kind == StarkTypeKind.RawPointer)
        {
            return right;
        }

        return left.DisplayName == right.DisplayName
            ? left
            : StarkTypeSymbols.Error;
    }

    private static bool IsTextType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static TextLiteralKind GetTextLiteralKind(string literalText)
    {
        return literalText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String;
    }

    private static IReadOnlyList<string> ExtractOperators<TOperand>(ParserRuleContext context)
        where TOperand : ParserRuleContext
    {
        var operators = new List<string>();
        var builder = new System.Text.StringBuilder();

        for (var index = 0; index < context.ChildCount; index++)
        {
            var child = context.GetChild(index);
            if (child is TOperand)
            {
                if (builder.Length > 0)
                {
                    operators.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(child.GetText());
        }

        return operators;
    }

    private delegate bool TryEvaluateOperand<in TOperand>(
        TOperand operand,
        out CompileTimeConstant constant)
        where TOperand : ParserRuleContext;
}
