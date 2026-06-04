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
            rendered = CompileTimeExpressionEvaluator.StripFloatSuffix(floatLiteral.GetText());
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
        var constructor = _context.ResolveObjectCreationConstructor(objectCreation);

        if (constructor is not null && arguments.Length != constructor.Parameters.Count)
        {
            return false;
        }

        if (constructor is { IsPrimaryShape: true })
        {
            // Record-style primary constructors: parameters are the fields themselves.
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
        else if (constructor is not null
            && !TryPlanExplicitConstructorInitializer(constructor, arguments, namedType, fieldValues))
        {
            // Explicit (bodied) constructors are traced at compile time; a constructor
            // that cannot be evaluated this way cannot initialize a static.
            return false;
        }
        else if (constructor is null && arguments.Length != 0)
        {
            return false;
        }

        if (objectCreation.objectInitializer() is { } objectInitializer
            && !TryCollectObjectInitializerMembers(objectInitializer, namedType, isFrozen, fieldValues, preludeDefinitions))
        {
            return false;
        }

        plan = new LlvmGlobalInitializerPlan(FormatNamedAggregateInitializer(namedType, fieldValues), preludeDefinitions);
        return true;
    }

    /// <summary>
    /// Plans a static initializer that invokes an explicit (bodied) constructor by
    /// tracing the constructor body at compile time. Static initializers are comptime
    /// contexts (doc 13): the call's arguments must fold to compile-time constants and
    /// the body must consist of `self.Field = <comptime expression>;` assignments
    /// (if/else is followed when its condition folds to a constant; a bare `return;`
    /// finishes construction). Anything else cannot initialize a static.
    /// </summary>
    private bool TryPlanExplicitConstructorInitializer(
        TypedConstructorShape constructor,
        StarkParser.ArgumentContext[] arguments,
        NamedTypeSymbol namedType,
        IDictionary<string, string> fieldValues)
    {
        if (constructor.BodyKey is null
            || TryFindConstructorBody(constructor.BodyKey) is not { } constructorBody)
        {
            return false;
        }

        // Evaluate every call-site argument to a compile-time constant and bind it to
        // its parameter name; the body trace resolves parameter references through
        // these bindings.
        var parameterConstants = new Dictionary<string, CompileTimeConstant>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var parameter = constructor.Parameters[index];
            if (!CompileTimeExpressionEvaluator.TryEvaluate(arguments[index].expression(), out var argumentConstant)
                || !CompileTimeExpressionEvaluator.TryCoerce(argumentConstant, parameter.Type, out var coercedArgument))
            {
                return false;
            }

            parameterConstants[parameter.Name] = coercedArgument;
        }

        var services = new CompileTimeEvaluationServices(
            TryResolveIdentifier: (string name, out CompileTimeConstant constant) =>
                parameterConstants.TryGetValue(name, out constant));

        var constructionComplete = false;
        return TryTraceConstructorStatements(constructorBody.statement(), services, namedType, fieldValues, ref constructionComplete);
    }

    /// <summary>
    /// Finds the parsed body of the constructor identified by a constructor body key
    /// ("QualifiedTypeName@line:column") across all loaded modules.
    /// </summary>
    private StarkParser.BlockContext? TryFindConstructorBody(string bodyKey)
    {
        foreach (var module in _context.LoadedModules.Modules.Values)
        {
            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                IEnumerable<StarkParser.ConstructorDeclarationContext?> constructorDeclarations;
                string localTypeName;

                if (declaration.structDeclaration() is { } structDeclaration)
                {
                    localTypeName = structDeclaration.Identifier().GetText();
                    constructorDeclarations = structDeclaration.structBody().structMember()
                        .Select(static member => member.constructorDeclaration());
                }
                else if (declaration.recordDeclaration() is { } recordDeclaration)
                {
                    localTypeName = recordDeclaration.Identifier().GetText();
                    constructorDeclarations = recordDeclaration.recordBody().recordMember()
                        .Select(static member => member.constructorDeclaration());
                }
                else
                {
                    continue;
                }

                var qualifiedTypeName = module.Reference.IsRoot
                    ? localTypeName
                    : $"{module.SyntaxModel.ModuleName}.{localTypeName}";

                foreach (var constructorDeclaration in constructorDeclarations)
                {
                    if (constructorDeclaration is null)
                    {
                        continue;
                    }

                    var candidateKey = $"{qualifiedTypeName}@{constructorDeclaration.Start.Line}:{constructorDeclaration.Start.Column + 1}";
                    if (string.Equals(candidateKey, bodyKey, StringComparison.Ordinal))
                    {
                        return constructorDeclaration.block();
                    }
                }
            }
        }

        return null;
    }

    private bool TryTraceConstructorStatements(
        IEnumerable<StarkParser.StatementContext> statements,
        CompileTimeEvaluationServices services,
        NamedTypeSymbol namedType,
        IDictionary<string, string> fieldValues,
        ref bool constructionComplete)
    {
        foreach (var statement in statements)
        {
            // Statements after a `return;` never execute.
            if (constructionComplete)
            {
                return true;
            }

            if (!TryTraceConstructorStatement(statement, services, namedType, fieldValues, ref constructionComplete))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryTraceConstructorStatement(
        StarkParser.StatementContext statement,
        CompileTimeEvaluationServices services,
        NamedTypeSymbol namedType,
        IDictionary<string, string> fieldValues,
        ref bool constructionComplete)
    {
        if (statement.block() is { } block)
        {
            return TryTraceConstructorStatements(block.statement(), services, namedType, fieldValues, ref constructionComplete);
        }

        if (statement.emptyStatement() is not null)
        {
            return true;
        }

        // Constructors never return values; a bare `return;` just finishes construction.
        if (statement.returnStatement() is { } returnStatement)
        {
            if (returnStatement.expression() is not null)
            {
                return false;
            }

            constructionComplete = true;
            return true;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            return TryTraceConstructorFieldAssignment(expressionStatement.expression(), services, namedType, fieldValues);
        }

        // if/else is only traceable when the condition folds to a compile-time constant
        // (e.g. it tests a parameter that was bound to a constant argument).
        if (statement.ifStatement() is { } ifStatement)
        {
            if (ifStatement.IS() is not null
                || ifStatement.disjointRuntimeCondition() is not null
                || ifStatement.expression() is not { } condition
                || !CompileTimeExpressionEvaluator.TryEvaluate(condition, out var conditionConstant, services)
                || conditionConstant.Kind != CompileTimeConstantKind.Bool)
            {
                return false;
            }

            var branches = ifStatement.statement();
            if (conditionConstant.BoolValue)
            {
                return TryTraceConstructorStatement(branches[0], services, namedType, fieldValues, ref constructionComplete);
            }

            return branches.Length < 2
                || TryTraceConstructorStatement(branches[1], services, namedType, fieldValues, ref constructionComplete);
        }

        return false;
    }

    private bool TryTraceConstructorFieldAssignment(
        StarkParser.ExpressionContext expression,
        CompileTimeEvaluationServices services,
        NamedTypeSymbol namedType,
        IDictionary<string, string> fieldValues)
    {
        // Must be a plain `self.<Field> = <expression>` assignment (no `init`, no
        // compound operators).
        var assignment = expression.assignmentExpression();
        if (assignment.INIT() is not null
            || assignment.unaryExpression() is not { } assignmentTarget
            || assignment.assignmentOperator() is not { } assignmentOperator
            || assignmentOperator.ASSIGN() is null
            || assignment.assignmentExpression() is not { } assignedValue)
        {
            return false;
        }

        if (TryGetSelfFieldName(assignmentTarget) is not { } fieldName
            || !namedType.TryGetField(fieldName, out var field, out _))
        {
            return false;
        }

        // The assigned value must fold to a compile-time constant; parameter references
        // resolve through the bound call-site arguments.
        if (!CompileTimeExpressionEvaluator.TryEvaluate(assignedValue, out var valueConstant, services)
            || !CompileTimeExpressionEvaluator.TryCoerce(valueConstant, field.Type, out var coercedValue)
            || !TryPlanCompileTimeConstant(coercedValue, field.Type, out var valuePlan))
        {
            return false;
        }

        fieldValues[fieldName] = valuePlan.Rendered;
        return true;
    }

    /// <summary>
    /// Returns the field name when the assignment target is exactly `self.<Field>`:
    /// the bare `self` identifier with a single member-access postfix part.
    /// </summary>
    private static string? TryGetSelfFieldName(StarkParser.UnaryExpressionContext assignmentTarget)
    {
        if (assignmentTarget.powerExpression() is not { } power
            || power.unaryExpression() is not null
            || assignmentTarget.unaryOperator() is not null
            || assignmentTarget.conversionType() is not null
            || assignmentTarget.INIT() is not null
            || assignmentTarget.TRY() is not null)
        {
            return null;
        }

        var postfix = power.postfixExpression();
        var postfixParts = postfix.postfixPart();
        if (postfix.primaryExpression().Identifier() is not { } selfIdentifier
            || !string.Equals(selfIdentifier.GetText(), "self", StringComparison.Ordinal)
            || postfixParts.Length != 1
            || postfixParts[0].DOT() is null
            || postfixParts[0].Identifier() is not { } fieldIdentifier)
        {
            return null;
        }

        return fieldIdentifier.GetText();
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
        if (LlvmLayoutControlledAggregateFacts.RequiresPhysicalLayout(namedType)
            && _context.TryGetConcreteTypeLayout(StarkTypeSymbols.Named(namedType.Name)) is { } layout)
        {
            if (!LlvmLayoutControlledAggregateFacts.TryBuildPhysicalElements(
                    namedType,
                    layout,
                    out var physicalElements,
                    out var hasOverlappingFields)
                || hasOverlappingFields)
            {
                return "zeroinitializer";
            }

            var physicalInitializers = physicalElements
                .Where(static element => element.SizeBytes > 0)
                .Select(element =>
                {
                    if (element.SourceFieldName is { } fieldName
                        && element.FieldType is { } fieldType)
                    {
                        return $"{_context.MapType(fieldType)} {(fieldValues.TryGetValue(fieldName, out var value) ? value : FormatZeroInitializer(fieldType))}";
                    }

                    return $"[{element.SizeBytes} x i8] zeroinitializer";
                });
            return $"{{ {string.Join(", ", physicalInitializers)} }}";
        }

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
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer => "null",
            StarkTypeKind.Ascii or StarkTypeKind.Unicode or StarkTypeKind.FixedArray or StarkTypeKind.Slice or StarkTypeKind.Dynamic or StarkTypeKind.Named => "zeroinitializer",
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
