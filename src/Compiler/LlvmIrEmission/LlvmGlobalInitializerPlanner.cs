using System.Globalization;
using System.Numerics;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class LlvmGlobalInitializerPlanner
{
    private readonly LlvmEmissionContext _context;

    public LlvmGlobalInitializerPlanner(LlvmEmissionContext context)
    {
        _context = context;
    }

    public bool TryPlanVariableInitializer(
        StarkParser.VariableInitializerContext initializer,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;

        if (initializer.expression() is { } expression)
        {
            return TryPlanGlobalExpression(expression, targetType, isFrozen, out plan);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            return TryPlanObjectInitializer(objectInitializer, targetType, isFrozen, out plan);
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            return TryPlanArrayInitializer(arrayInitializer, targetType, isFrozen, out plan);
        }

        return false;
    }

    public bool ShouldEmitExternalConstPlaceholder(
        TypedGlobalSymbol global,
        StarkParser.VariableInitializerContext initializer)
    {
        return global.IsConst
            && global.Type.Kind == StarkTypeKind.RawPointer
            && initializer.expression() is { } expression
            && _context.TryUnwrapSimplePrimaryExpression(expression) is { } primaryExpression
            && primaryExpression.literal()?.NULL() is not null;
    }

    private bool TryPlanGlobalExpression(
        StarkParser.ExpressionContext expression,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;

        if (CompileTimeExpressionEvaluator.TryEvaluate(expression, out var constant)
            && CompileTimeExpressionEvaluator.TryCoerce(constant, targetType, out var coerced)
            && TryPlanCompileTimeConstant(coerced, targetType, out plan))
        {
            return true;
        }

        var primaryExpression = _context.TryUnwrapSimplePrimaryExpression(expression);
        if (primaryExpression is null)
        {
            return false;
        }

        if (primaryExpression.literal() is { } literal)
        {
            return TryPlanLiteralInitializer(literal, targetType, out plan);
        }

        if (primaryExpression.objectCreationExpression() is { } objectCreation)
        {
            return TryPlanObjectCreationInitializer(objectCreation, targetType, isFrozen, out plan);
        }

        if (primaryExpression.expression() is { } groupedExpression)
        {
            return TryPlanGlobalExpression(groupedExpression, targetType, isFrozen, out plan);
        }

        return false;
    }

    private bool TryPlanCompileTimeConstant(
        CompileTimeConstant constant,
        StarkTypeSymbol targetType,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        string rendered;

        switch (constant.Kind)
        {
            case CompileTimeConstantKind.Integer:
                rendered = constant.IntegerValue.ToString();
                break;
            case CompileTimeConstantKind.Float:
                rendered = CompileTimeExpressionEvaluator.FormatFloatLiteral(constant);
                break;
            case CompileTimeConstantKind.Bool:
                rendered = constant.BoolValue ? "true" : "false";
                break;
            case CompileTimeConstantKind.Null:
                rendered = "null";
                break;
            case CompileTimeConstantKind.Text when constant.TextLiteral is not null:
                rendered = FormatGlobalStringConstantValue(constant.TextLiteral, targetType);
                break;
            default:
                return false;
        }

        plan = new LlvmGlobalInitializerPlan(rendered, []);
        return true;
    }

    private bool TryPlanLiteralInitializer(
        StarkParser.LiteralContext literal,
        StarkTypeSymbol targetType,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        var rendered = string.Empty;

        if (literal.signedIntegerLiteral() is { } integerLiteral)
        {
            rendered = ParseSignedIntegerLiteral(integerLiteral).ToString();
        }
        else if (literal.FloatLiteral() is { } floatLiteral)
        {
            rendered = floatLiteral.GetText();
        }
        else if (literal.TRUE() is not null)
        {
            rendered = "true";
        }
        else if (literal.FALSE() is not null)
        {
            rendered = "false";
        }
        else if (literal.NULL() is not null)
        {
            rendered = "null";
        }
        else if (literal.StringLiteral() is { } stringLiteral)
        {
            rendered = FormatGlobalStringConstantValue(stringLiteral.GetText(), targetType);
        }
        else if (literal.CharacterLiteral() is { } characterLiteral)
        {
            rendered = FormatGlobalStringConstantValue(characterLiteral.GetText(), targetType);
        }
        else
        {
            return false;
        }

        plan = new LlvmGlobalInitializerPlan(rendered, []);
        return true;
    }

    private bool TryPlanObjectCreationInitializer(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;

        var namedType = _context.ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        var preludeDefinitions = new List<string>();
        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var arguments = objectCreation.argumentList()?.argument() ?? [];

        if (arguments.Length != 0)
        {
            var constructor = _context.ResolveObjectCreationConstructor(objectCreation);
            if (constructor is null
                || !constructor.IsPrimaryShape
                || arguments.Length != constructor.Parameters.Count)
            {
                return false;
            }

            for (var index = 0; index < arguments.Length; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out _))
                {
                    return false;
                }

                if (!TryPlanGlobalExpression(arguments[index].expression(), field.Type, isFrozen, out var argumentPlan))
                {
                    return false;
                }

                preludeDefinitions.AddRange(argumentPlan.PreludeDefinitions);
                fieldValues[field.Name] = argumentPlan.Rendered;
            }
        }

        if (objectCreation.objectInitializer() is { } objectInitializer
            && !TryCollectObjectInitializerMembers(objectInitializer, namedType, isFrozen, fieldValues, preludeDefinitions))
        {
            return false;
        }

        plan = new LlvmGlobalInitializerPlan(FormatNamedAggregateInitializer(namedType, fieldValues), preludeDefinitions);
        return true;
    }

    private bool TryPlanObjectInitializer(
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;

        var namedType = _context.ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        var preludeDefinitions = new List<string>();
        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryCollectObjectInitializerMembers(objectInitializer, namedType, isFrozen, fieldValues, preludeDefinitions))
        {
            return false;
        }

        plan = new LlvmGlobalInitializerPlan(FormatNamedAggregateInitializer(namedType, fieldValues), preludeDefinitions);
        return true;
    }

    private bool TryCollectObjectInitializerMembers(
        StarkParser.ObjectInitializerContext objectInitializer,
        NamedTypeSymbol namedType,
        bool isFrozen,
        IDictionary<string, string> fieldValues,
        ICollection<string> preludeDefinitions)
    {
        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberName = memberInitializer.Identifier().GetText();
            if (!namedType.Fields.TryGetValue(memberName, out var field))
            {
                return false;
            }

            if (!TryPlanVariableInitializer(memberInitializer.variableInitializer(), field.Type, isFrozen, out var memberPlan))
            {
                return false;
            }

            foreach (var prelude in memberPlan.PreludeDefinitions)
            {
                preludeDefinitions.Add(prelude);
            }

            fieldValues[memberName] = memberPlan.Rendered;
        }

        return true;
    }

    private string FormatNamedAggregateInitializer(
        NamedTypeSymbol namedType,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        var fieldInitializers = namedType.OrderedFields
            .Select(field => $"{_context.MapType(field.Type)} {(fieldValues.TryGetValue(field.Name, out var value) ? value : FormatZeroInitializer(field.Type))}");
        return $"{{ {string.Join(", ", fieldInitializers)} }}";
    }

    private bool TryPlanArrayInitializer(
        StarkParser.ArrayInitializerContext arrayInitializer,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;

        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is null
            || targetType.FixedLength is not int fixedLength
            || arrayInitializer.variableInitializer().Length != fixedLength)
        {
            return false;
        }

        var preludeDefinitionsForArray = new List<string>();
        var elements = new List<string>(fixedLength);
        foreach (var initializer in arrayInitializer.variableInitializer())
        {
            if (!TryPlanVariableInitializer(initializer, targetType.ElementType, isFrozen, out var elementPlan))
            {
                return false;
            }

            preludeDefinitionsForArray.AddRange(elementPlan.PreludeDefinitions);
            elements.Add($"{_context.MapType(targetType.ElementType)} {elementPlan.Rendered}");
        }

        plan = new LlvmGlobalInitializerPlan($"[{string.Join(", ", elements)}]", preludeDefinitionsForArray);
        return true;
    }

    private static string FormatZeroInitializer(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Integer => "0",
            StarkTypeKind.Float => "0.0",
            StarkTypeKind.Bool => "false",
            StarkTypeKind.RawPointer => "null",
            StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.FixedArray or StarkTypeKind.Slice or StarkTypeKind.Named => "zeroinitializer",
            _ => "zeroinitializer"
        };
    }

    private string FormatGlobalStringConstantValue(string literalText, StarkTypeSymbol targetType)
    {
        var constant = _context.ResolveStringConstant(literalText, targetType);
        var pointer = FormatStringDataPointer(constant);
        return $"{{ ptr {pointer}, i64 {constant.DataLength} }}";
    }

    private static string FormatStringDataPointer(EmittedStringConstant constant)
    {
        return $"getelementptr inbounds nuw ({constant.ArrayType}, ptr @{constant.SymbolName}, i32 0, i32 0)";
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var text = literal.GetText().Replace("_", string.Empty, StringComparison.Ordinal);
        return BigInteger.Parse(text, CultureInfo.InvariantCulture);
    }
}

internal sealed record LlvmGlobalInitializerPlan(
    string Rendered,
    IReadOnlyList<string> PreludeDefinitions);
