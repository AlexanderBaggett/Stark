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
        out LlvmGlobalInitializerPlan plan,
        CompileTimeEvaluationServices services = default,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? initializerBindings = null)
    {
        plan = null!;

        if (initializer.expression() is { } expression)
        {
            return TryPlanGlobalExpression(expression, targetType, isFrozen, out plan, services, initializerBindings);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            return TryPlanObjectInitializer(objectInitializer, targetType, isFrozen, out plan, services, initializerBindings);
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            return TryPlanArrayInitializer(arrayInitializer, targetType, isFrozen, out plan, services, initializerBindings);
        }

        return false;
    }

    public bool TryPlanTypedConstantInitializer(
        TypedConstantInitializer initializer,
        StarkTypeSymbol targetType,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        switch (initializer.Kind)
        {
            case TypedConstantInitializerKind.Integer when initializer.IntegerValue is { } integerValue:
                plan = new LlvmGlobalInitializerPlan(integerValue.ToString(), []);
                return true;
            case TypedConstantInitializerKind.Float when initializer.FloatLiteralText is { } floatLiteralText:
                plan = new LlvmGlobalInitializerPlan(RenderFloatLiteral(floatLiteralText, targetType), []);
                return true;
            case TypedConstantInitializerKind.Bool when initializer.BoolValue is { } boolValue:
                plan = new LlvmGlobalInitializerPlan(boolValue ? "true" : "false", []);
                return true;
            case TypedConstantInitializerKind.Text when initializer.TextLiteralText is { } textLiteralText:
                plan = new LlvmGlobalInitializerPlan(FormatGlobalStringConstantValue(textLiteralText, targetType), []);
                return true;
            case TypedConstantInitializerKind.Null:
                plan = new LlvmGlobalInitializerPlan("null", []);
                return true;
            case TypedConstantInitializerKind.FixedArray:
                return TryPlanTypedFixedArrayInitializer(initializer, targetType, out plan);
            case TypedConstantInitializerKind.NamedAggregate:
                return TryPlanTypedNamedAggregateInitializer(initializer, targetType, out plan);
            case TypedConstantInitializerKind.EnumAggregate:
                return TryPlanTypedEnumAggregateInitializer(initializer, targetType, out plan);
            default:
                return false;
        }
    }

    private bool TryPlanTypedFixedArrayInitializer(
        TypedConstantInitializer initializer,
        StarkTypeSymbol targetType,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is not { } elementType
            || targetType.FixedLength is not int fixedLength
            || initializer.Elements is not { } elements
            || elements.Count != fixedLength)
        {
            return false;
        }

        var preludeDefinitionsForArray = new List<string>();
        var renderedElements = new List<string>(fixedLength);
        foreach (var element in elements)
        {
            if (!TryPlanTypedConstantInitializer(element, elementType, out var elementPlan))
            {
                return false;
            }

            preludeDefinitionsForArray.AddRange(elementPlan.PreludeDefinitions);
            renderedElements.Add($"{_context.MapType(elementType)} {elementPlan.Rendered}");
        }

        plan = new LlvmGlobalInitializerPlan($"[{string.Join(", ", renderedElements)}]", preludeDefinitionsForArray);
        return true;
    }

    private bool TryPlanTypedNamedAggregateInitializer(
        TypedConstantInitializer initializer,
        StarkTypeSymbol targetType,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        var namedType = _context.ResolveNamedTypeSymbol(targetType);
        if (namedType is null
            || initializer.Elements is not { } elements
            || elements.Count != namedType.OrderedFields.Count)
        {
            return false;
        }

        var preludeDefinitions = new List<string>();
        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < namedType.OrderedFields.Count; index++)
        {
            var field = namedType.OrderedFields[index];
            if (!TryPlanTypedConstantInitializer(elements[index], field.Type, out var fieldPlan))
            {
                return false;
            }

            preludeDefinitions.AddRange(fieldPlan.PreludeDefinitions);
            fieldValues[field.Name] = fieldPlan.Rendered;
        }

        plan = new LlvmGlobalInitializerPlan(FormatNamedAggregateInitializer(namedType, fieldValues), preludeDefinitions);
        return true;
    }

    private bool TryPlanTypedEnumAggregateInitializer(
        TypedConstantInitializer initializer,
        StarkTypeSymbol targetType,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        if (_context.ResolveNamedTypeSymbol(targetType) is not { Kind: DeclarationKind.Enum } namedType
            || !_context.EnumLayouts.TryGetValue(namedType.Name, out var layout)
            || initializer.VariantName is not { } variantName
            || !layout.TryGetVariant(variantName, out var variant)
            || initializer.Elements is not { } elements
            || elements.Count != variant.Fields.Count)
        {
            return false;
        }

        var preludeDefinitions = new List<string>();
        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [layout.TagField.Name] = variant.TagValue.ToString(CultureInfo.InvariantCulture)
        };

        for (var index = 0; index < variant.Fields.Count; index++)
        {
            var field = variant.Fields[index];
            if (!TryPlanTypedConstantInitializer(elements[index], field.Type, out var fieldPlan))
            {
                return false;
            }

            preludeDefinitions.AddRange(fieldPlan.PreludeDefinitions);
            fieldValues[field.StorageFieldName] = fieldPlan.Rendered;
        }

        plan = new LlvmGlobalInitializerPlan(FormatEnumAggregateInitializer(layout, fieldValues), preludeDefinitions);
        return true;
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
        out LlvmGlobalInitializerPlan plan,
        CompileTimeEvaluationServices services = default,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? initializerBindings = null)
    {
        plan = null!;

        if (CompileTimeExpressionEvaluator.TryEvaluate(expression, out var constant, services)
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

        if (TryPlanBoundInitializer(primaryExpression, initializerBindings, out plan))
        {
            return true;
        }

        if (primaryExpression.objectCreationExpression() is { } objectCreation)
        {
            return TryPlanObjectCreationInitializer(
                objectCreation,
                targetType,
                isFrozen,
                out plan,
                services,
                initializerBindings);
        }

        if (primaryExpression.expression() is { } groupedExpression)
        {
            return TryPlanGlobalExpression(
                groupedExpression,
                targetType,
                isFrozen,
                out plan,
                services,
                initializerBindings);
        }

        return false;
    }

    private static bool TryPlanBoundInitializer(
        StarkParser.PrimaryExpressionContext primaryExpression,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? initializerBindings,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        if (initializerBindings is null)
        {
            return false;
        }

        if (primaryExpression.Identifier() is { } identifier
            && initializerBindings.TryGetValue(identifier.GetText(), out plan!))
        {
            return true;
        }

        if (primaryExpression.qualifiedName() is { } qualifiedName
            && initializerBindings.TryGetValue(qualifiedName.GetText(), out plan!))
        {
            return true;
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
                rendered = RenderFloatLiteral(
                    CompileTimeExpressionEvaluator.FormatFloatLiteral(constant),
                    constant.Type.Kind == StarkTypeKind.Float ? constant.Type : targetType);
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
            rendered = RenderFloatLiteral(
                CompileTimeExpressionEvaluator.StripFloatSuffix(floatLiteral.GetText()),
                targetType);
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
        out LlvmGlobalInitializerPlan plan,
        CompileTimeEvaluationServices services = default,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? initializerBindings = null)
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

        // Creations inside reconstructed package-module constructor bodies have no
        // object-creation typing record (package bodies are not re-type-checked), so
        // fall back to the type model's constructor shapes for the created type.
        var constructor = _context.ResolveObjectCreationConstructor(objectCreation)
            ?? TryResolveConstructorShapeByArity(namedType.Name, arguments.Length);

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

                if (!TryPlanGlobalExpression(
                        arguments[index].expression(),
                        field.Type,
                        isFrozen,
                        out var argumentPlan,
                        services,
                        initializerBindings))
                {
                    return false;
                }

                preludeDefinitions.AddRange(argumentPlan.PreludeDefinitions);
                fieldValues[field.Name] = argumentPlan.Rendered;
            }
        }
        else if (constructor is not null
            && !TryPlanExplicitConstructorInitializer(
                constructor,
                arguments,
                namedType,
                isFrozen,
                fieldValues,
                preludeDefinitions,
                services,
                initializerBindings))
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
            && !TryCollectObjectInitializerMembers(
                objectInitializer,
                namedType,
                isFrozen,
                fieldValues,
                preludeDefinitions,
                services,
                initializerBindings))
        {
            return false;
        }

        plan = new LlvmGlobalInitializerPlan(FormatNamedAggregateInitializer(namedType, fieldValues), preludeDefinitions);
        return true;
    }

    private TypedConstructorShape? TryResolveConstructorShapeByArity(string typeName, int argumentCount)
    {
        if (!_context.TypeModel.ConstructorShapes.TryGetValue(typeName, out var shapes))
        {
            return null;
        }

        TypedConstructorShape? match = null;
        foreach (var shape in shapes)
        {
            if (shape.Parameters.Count != argumentCount)
            {
                continue;
            }

            if (match is not null)
            {
                // Ambiguous arity; without a typing record the overload cannot be
                // chosen safely.
                return null;
            }

            match = shape;
        }

        return match;
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
        bool isFrozen,
        IDictionary<string, string> fieldValues,
        ICollection<string> preludeDefinitions,
        CompileTimeEvaluationServices outerServices,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? outerInitializerBindings)
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
        var parameterInitializers = outerInitializerBindings is null
            ? new Dictionary<string, LlvmGlobalInitializerPlan>(StringComparer.Ordinal)
            : new Dictionary<string, LlvmGlobalInitializerPlan>(outerInitializerBindings, StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var parameter = constructor.Parameters[index];
            if (!TryPlanGlobalExpression(
                    arguments[index].expression(),
                    parameter.Type,
                    isFrozen,
                    out var argumentPlan,
                    outerServices,
                    outerInitializerBindings))
            {
                return false;
            }

            parameterInitializers[parameter.Name] = argumentPlan;
            foreach (var prelude in argumentPlan.PreludeDefinitions)
            {
                preludeDefinitions.Add(prelude);
            }

            if (CompileTimeExpressionEvaluator.TryEvaluate(arguments[index].expression(), out var argumentConstant, outerServices)
                && CompileTimeExpressionEvaluator.TryCoerce(argumentConstant, parameter.Type, out var coercedArgument))
            {
                parameterConstants[parameter.Name] = coercedArgument;
            }
        }

        var services = new CompileTimeEvaluationServices(
            TryResolveIdentifier: (string name, out CompileTimeConstant constant) =>
            {
                if (parameterConstants.TryGetValue(name, out constant))
                {
                    return true;
                }

                if (outerServices.TryResolveIdentifier is not null
                    && outerServices.TryResolveIdentifier(name, out constant))
                {
                    return true;
                }

                constant = default;
                return false;
            });

        var constructionComplete = false;
        return TryTraceConstructorStatements(
            constructorBody.statement(),
            services,
            parameterInitializers,
            namedType,
            isFrozen,
            fieldValues,
            preludeDefinitions,
            ref constructionComplete);
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
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan> initializerBindings,
        NamedTypeSymbol namedType,
        bool isFrozen,
        IDictionary<string, string> fieldValues,
        ICollection<string> preludeDefinitions,
        ref bool constructionComplete)
    {
        foreach (var statement in statements)
        {
            // Statements after a `return;` never execute.
            if (constructionComplete)
            {
                return true;
            }

            if (!TryTraceConstructorStatement(
                    statement,
                    services,
                    initializerBindings,
                    namedType,
                    isFrozen,
                    fieldValues,
                    preludeDefinitions,
                    ref constructionComplete))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryTraceConstructorStatement(
        StarkParser.StatementContext statement,
        CompileTimeEvaluationServices services,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan> initializerBindings,
        NamedTypeSymbol namedType,
        bool isFrozen,
        IDictionary<string, string> fieldValues,
        ICollection<string> preludeDefinitions,
        ref bool constructionComplete)
    {
        if (statement.block() is { } block)
        {
            return TryTraceConstructorStatements(
                block.statement(),
                services,
                initializerBindings,
                namedType,
                isFrozen,
                fieldValues,
                preludeDefinitions,
                ref constructionComplete);
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
            return TryTraceConstructorFieldAssignment(
                expressionStatement.expression(),
                services,
                initializerBindings,
                namedType,
                isFrozen,
                fieldValues,
                preludeDefinitions);
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
                return TryTraceConstructorStatement(
                    branches[0],
                    services,
                    initializerBindings,
                    namedType,
                    isFrozen,
                    fieldValues,
                    preludeDefinitions,
                    ref constructionComplete);
            }

            return branches.Length < 2
                || TryTraceConstructorStatement(
                    branches[1],
                    services,
                    initializerBindings,
                    namedType,
                    isFrozen,
                    fieldValues,
                    preludeDefinitions,
                    ref constructionComplete);
        }

        return false;
    }

    private bool TryTraceConstructorFieldAssignment(
        StarkParser.ExpressionContext expression,
        CompileTimeEvaluationServices services,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan> initializerBindings,
        NamedTypeSymbol namedType,
        bool isFrozen,
        IDictionary<string, string> fieldValues,
        ICollection<string> preludeDefinitions)
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

        // The assigned value must be statically plannable. Scalar values fold through
        // CTFE; aggregate constructor parameters carry their rendered initializer plans.
        if (!TryPlanConstructorAssignedValue(
                assignedValue,
                field.Type,
                isFrozen,
                services,
                initializerBindings,
                out var valuePlan))
        {
            return false;
        }

        foreach (var prelude in valuePlan.PreludeDefinitions)
        {
            preludeDefinitions.Add(prelude);
        }

        fieldValues[fieldName] = valuePlan.Rendered;
        return true;
    }

    private bool TryPlanConstructorAssignedValue(
        StarkParser.AssignmentExpressionContext assignedValue,
        StarkTypeSymbol targetType,
        bool isFrozen,
        CompileTimeEvaluationServices services,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan> initializerBindings,
        out LlvmGlobalInitializerPlan plan)
    {
        plan = null!;
        if (CompileTimeExpressionEvaluator.TryEvaluate(assignedValue, out var valueConstant, services)
            && CompileTimeExpressionEvaluator.TryCoerce(valueConstant, targetType, out var coercedValue)
            && TryPlanCompileTimeConstant(coercedValue, targetType, out plan))
        {
            return true;
        }

        if (!TryUnwrapSimplePrimaryExpression(assignedValue, out var primaryExpression))
        {
            return false;
        }

        if (primaryExpression.literal() is { } literal)
        {
            return TryPlanLiteralInitializer(literal, targetType, out plan);
        }

        if (TryPlanBoundInitializer(primaryExpression, initializerBindings, out plan))
        {
            return true;
        }

        if (primaryExpression.objectCreationExpression() is { } objectCreation)
        {
            return TryPlanObjectCreationInitializer(
                objectCreation,
                targetType,
                isFrozen,
                out plan,
                services,
                initializerBindings);
        }

        if (primaryExpression.expression() is { } groupedExpression)
        {
            return TryPlanGlobalExpression(
                groupedExpression,
                targetType,
                isFrozen,
                out plan,
                services,
                initializerBindings);
        }

        return false;
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
        out LlvmGlobalInitializerPlan plan,
        CompileTimeEvaluationServices services = default,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? initializerBindings = null)
    {
        plan = null!;

        var namedType = _context.ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        var preludeDefinitions = new List<string>();
        var fieldValues = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryCollectObjectInitializerMembers(
                objectInitializer,
                namedType,
                isFrozen,
                fieldValues,
                preludeDefinitions,
                services,
                initializerBindings))
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
        ICollection<string> preludeDefinitions,
        CompileTimeEvaluationServices services = default,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? initializerBindings = null)
    {
        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberName = memberInitializer.Identifier().GetText();
            if (!namedType.Fields.TryGetValue(memberName, out var field))
            {
                return false;
            }

            if (!TryPlanVariableInitializer(
                    memberInitializer.variableInitializer(),
                    field.Type,
                    isFrozen,
                    out var memberPlan,
                    services,
                    initializerBindings))
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

    private string FormatEnumAggregateInitializer(
        EnumLayoutSymbol layout,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        var fieldInitializers = layout.OrderedFields
            .Select(field => $"{_context.MapType(field.Type)} {(fieldValues.TryGetValue(field.Name, out var value) ? value : FormatZeroInitializer(field.Type))}");
        return $"{{ {string.Join(", ", fieldInitializers)} }}";
    }

    private bool TryPlanArrayInitializer(
        StarkParser.ArrayInitializerContext arrayInitializer,
        StarkTypeSymbol targetType,
        bool isFrozen,
        out LlvmGlobalInitializerPlan plan,
        CompileTimeEvaluationServices services = default,
        IReadOnlyDictionary<string, LlvmGlobalInitializerPlan>? initializerBindings = null)
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
            if (!TryPlanVariableInitializer(
                    initializer,
                    targetType.ElementType,
                    isFrozen,
                    out var elementPlan,
                    services,
                    initializerBindings))
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
        if (StarkTypeSymbols.IsPointerBackedBorrowType(type))
        {
            return "null";
        }

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

    /// <summary>
    /// Re-renders a decimal float literal (e.g. <c>1</c>, <c>10</c>,
    /// <c>1E+17</c>) as an LLVM-valid hex float for the given float type.
    /// Integral and scientific decimal forms are rejected by LLVM's IR
    /// parser; the hex float round-trips every value exactly. Falls back to
    /// the raw text when the type is not a float or the text is unparseable
    /// (neither should happen for a well-typed float initializer).
    /// </summary>
    private static string RenderFloatLiteral(string literalText, StarkTypeSymbol targetType)
    {
        if (targetType.Kind == StarkTypeKind.Float
            && LlvmFloatLiteral.TryRenderLiteralText(literalText, targetType.BitWidth ?? 64, out var rendered))
        {
            return rendered;
        }

        return literalText;
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

    private static bool TryUnwrapSimplePrimaryExpression(
        StarkParser.AssignmentExpressionContext expression,
        out StarkParser.PrimaryExpressionContext primaryExpression)
    {
        primaryExpression = null!;

        if (expression.conditionalExpression() is not { } conditionalExpression
            || conditionalExpression.QUESTION() is not null)
        {
            return false;
        }

        var logicalOr = conditionalExpression.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return false;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return false;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return false;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return false;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return false;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return false;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return false;
        }

        var shift = relational.shiftExpression(0);
        if (shift.additiveExpression().Length != 1)
        {
            return false;
        }

        var additive = shift.additiveExpression(0);
        if (additive.multiplicativeExpression().Length != 1)
        {
            return false;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return false;
        }

        var unary = multiplicative.unaryExpression(0);
        if (unary.powerExpression() is not { } powerExpression
            || powerExpression.unaryExpression() is not null
            || powerExpression.postfixExpression() is not { } postfixExpression
            || postfixExpression.postfixPart().Length != 0
            || postfixExpression.primaryExpression() is not { } primary)
        {
            return false;
        }

        primaryExpression = primary;
        return true;
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
