using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SemanticValidator
{
    private readonly CompilerPassContext _context;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly ModuleGraph _moduleGraph;
    private readonly LoadedModuleSet _loadedModules;
    private readonly FunctionEffectModel _effectModel;
    private readonly TypeCheckModel _typeModel;
    private readonly EnumLayoutModel _enumLayoutModel;
    private readonly StarkTypeResolver _typeResolver;
    private readonly Dictionary<string, TopLevelDeclarationModel> _syntaxDeclarations;
    private readonly Dictionary<string, DeclaredFunctionSyntax> _functionDeclarations;
    private readonly IReadOnlyDictionary<ObjectCreationKey, TypedConstructorShape?> _objectCreationConstructors;
    private readonly Dictionary<string, FunctionValidationBuilder> _summaries = new(StringComparer.Ordinal);

    public SemanticValidator(
        CompilerPassContext context,
        ParseResult parseResult,
        SyntaxModel syntaxModel,
        ModuleGraph moduleGraph,
        LoadedModuleSet loadedModules,
        FunctionEffectModel effectModel,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel)
    {
        _context = context;
        _parseResult = parseResult;
        _syntaxModel = syntaxModel;
        _moduleGraph = moduleGraph;
        _loadedModules = loadedModules;
        _effectModel = effectModel;
        _typeModel = typeModel;
        _enumLayoutModel = enumLayoutModel;
        _typeResolver = new StarkTypeResolver(context, "semantic-validate", moduleGraph, typeModel.NamedTypes);
        _syntaxDeclarations = syntaxModel.Declarations.ToDictionary(
            declaration => declaration.Function is null
                ? declaration.Name
                : FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration),
            StringComparer.Ordinal);
        _functionDeclarations = DeclaredFunctionSyntaxCollector.Collect(parseResult, syntaxModel)
            .ToDictionary(static declaration => declaration.Name, StringComparer.Ordinal);
        _objectCreationConstructors = typeModel.ObjectCreations
            .GroupBy(static record => new ObjectCreationKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last().Constructor);
    }

    public SemanticValidationModel Validate()
    {
        ValidateGlobalDeclarations();
        ValidateDestructorDeclarations();

        foreach (var function in _functionDeclarations.Values)
        {
            ValidateFunction(function);
        }

        FinalizeMemoryEffectsAndValidateCalls();
        InferEffectiveFunctionKindsAndValidateDeclaredContracts();

        return new SemanticValidationModel(
            _syntaxModel.ModuleName,
            _summaries.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Build(),
                StringComparer.Ordinal));
    }

    private void ValidateGlobalDeclarations()
    {
        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalConstantDeclaration() is { } constantDeclaration)
            {
                var declaredType = _typeResolver.ResolveType(constantDeclaration.type_());
                ValidateTypeUsage(declaredType, TypeUsage.Global, constantDeclaration.type_(), isFfiBoundary: false);

                foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                {
                    ValidateConstGlobal(declarator.Identifier().GetText(), declaredType, declarator.variableInitializer());
                }

                continue;
            }

            if (declaration.globalVariableDeclaration() is { } variableDeclaration)
            {
                var declaredType = _typeResolver.ResolveType(variableDeclaration.type_());
                ValidateTypeUsage(declaredType, TypeUsage.Global, variableDeclaration.type_(), isFfiBoundary: false);
            }
        }
    }

    private void ValidateDestructorDeclarations()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            var destructors = DeclaredDestructorSyntaxCollector.Collect(module)
                .GroupBy(static destructor => destructor.QualifiedTypeName, StringComparer.Ordinal);

            foreach (var group in destructors)
            {
                ValidateDestructorDeclarations(group.Key, group.ToArray());
            }
        }
    }

    private void ValidateDestructorDeclarations(
        string qualifiedTypeName,
        IReadOnlyList<DeclaredDestructorSyntax> destructors)
    {
        if (destructors.Count == 0)
        {
            return;
        }

        if (destructors.Count > 1)
        {
            foreach (var duplicate in destructors.Skip(1))
            {
                _context.Diagnostics.Error(
                    "STK4012",
                    $"Type '{qualifiedTypeName}' declares more than one destructor block. Stark currently allows at most one 'drop' or 'mut drop' block per type.",
                    "semantic-validate",
                    Location(duplicate.Declaration.Start));
            }
        }

        var destructor = destructors[0];
        var mutatesSelf = BlockMutatesSelf(destructor.Body);

        if (!destructor.IsMutable && mutatesSelf)
        {
            _context.Diagnostics.Error(
                "STK4011",
                $"Read-only destructor 'drop' on '{qualifiedTypeName}' may not mutate 'self'. Use 'mut drop' if destructor state rewrites are required.",
                "semantic-validate",
                Location(destructor.Declaration.Start));
        }

        if (destructor.IsMutable && !mutatesSelf)
        {
            _context.Diagnostics.Warning(
                "STK4010",
                $"Destructor 'mut drop' on '{qualifiedTypeName}' does not mutate 'self'. Use 'drop' instead.",
                "semantic-validate",
                Location(destructor.Declaration.Start));
        }

        if (ContainsReturnStatement(destructor.Body))
        {
            _context.Diagnostics.Error(
                "STK4014",
                $"Destructor block on '{qualifiedTypeName}' may not use 'return' because destructors are not ordinary functions.",
                "semantic-validate",
                Location(destructor.Declaration.Start));
        }
    }

    private static bool BlockMutatesSelf(StarkParser.BlockContext block)
    {
        return ContainsSelfMutation(block);
    }

    private static bool ContainsSelfMutation(ParserRuleContext context)
    {
        if (context is StarkParser.AssignmentExpressionContext assignment
            && assignment.unaryExpression() is { } target
            && IsSelfMutationTarget(target))
        {
            return true;
        }

        for (var index = 0; index < context.ChildCount; index++)
        {
            if (context.GetChild(index) is ParserRuleContext child && ContainsSelfMutation(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsReturnStatement(ParserRuleContext context)
    {
        if (context is StarkParser.ReturnStatementContext)
        {
            return true;
        }

        for (var index = 0; index < context.ChildCount; index++)
        {
            if (context.GetChild(index) is ParserRuleContext child && ContainsReturnStatement(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSelfMutationTarget(StarkParser.UnaryExpressionContext target)
    {
        var postfix = target.powerExpression()?.postfixExpression();
        if (postfix is null)
        {
            return false;
        }

        var primary = postfix.primaryExpression();
        return primary?.Identifier() is { } identifier
            && string.Equals(identifier.GetText(), "self", StringComparison.Ordinal);
    }

    private void ValidateConstGlobal(
        string name,
        StarkTypeSymbol declaredType,
        StarkParser.VariableInitializerContext initializer)
    {
        if (declaredType.Kind == StarkTypeKind.Error)
        {
            return;
        }

        if (IsExternalConstRawPointerPlaceholder(declaredType, initializer))
        {
            return;
        }

        if (!TryValidateFrozenConstGlobalType(
                declaredType,
                name,
                new HashSet<string>(StringComparer.Ordinal),
                out var failingPath,
                out var failureReason))
        {
            _context.Diagnostics.Error(
                "STK4007",
                $"Const global '{name}' must be a fully frozen object graph. Reachable path '{failingPath}' {failureReason}.",
                "semantic-validate",
                Location(initializer.Start));
            return;
        }

        if (!CanMaterializeFrozenConstInitializer(initializer, declaredType))
        {
            _context.Diagnostics.Error(
                "STK4008",
                $"Const global '{name}' must use a frozen initializer that can be materialized as static data.",
                "semantic-validate",
                Location(initializer.Start));
        }
    }

    private void ValidateFunction(DeclaredFunctionSyntax functionDeclaration)
    {
        var name = functionDeclaration.Name;
        if (!_typeModel.Functions.TryGetValue(name, out var signature)
            || !_effectModel.Functions.TryGetValue(name, out var effects)
            || !_syntaxDeclarations.TryGetValue(name, out var syntaxDeclaration)
            || syntaxDeclaration.Function is null)
        {
            return;
        }

        var summary = GetOrCreateSummary(name);
        summary.Configure(signature.ReturnType, syntaxDeclaration.Function.HasBody, syntaxDeclaration.Function.Kind);
        summary.SetParameters(signature.Parameters, _typeModel.NamedTypes, _enumLayoutModel.Layouts);
        ValidateFunctionSignature(functionDeclaration, syntaxDeclaration.Function, signature, effects, summary);

        if (functionDeclaration.Body.block() is not { } block)
        {
            return;
        }

        var scope = ValidationScope.CreateRoot();
        for (var index = 0; index < signature.Parameters.Count; index++)
        {
            var parameter = signature.Parameters[index];
            scope.Declare(new VariableSymbol(
                parameter.Name,
                parameter.Type,
                SymbolOrigin.Parameter,
                LocalStorageClass.None,
                IsMutable: false,
                IsConstant: false));
        }

        CheckBlock(block, scope, syntaxDeclaration.Function, effects, summary);
    }

    private void ValidateFunctionSignature(
        DeclaredFunctionSyntax functionDeclaration,
        FunctionDeclarationModel declaration,
        TypedFunctionSignature signature,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        ValidateFunctionModifiers(functionDeclaration, summary);
        ValidateTypeUsage(signature.ReturnType, TypeUsage.Return, functionDeclaration.ReturnType, declaration.Modifiers.IsFfi);

        if (signature.ReturnType.BorrowKind == StarkBorrowKind.Borrow)
        {
            BorrowError(
                summary,
                "STK4000",
                $"Function '{signature.Name}' cannot return a plain 'borrow' value. Use 'retborrow' or 'storeborrow'.",
                functionDeclaration.ReturnType);
        }

        if (signature.ReturnType.InitializationKind != StarkInitializationKind.None)
        {
            summary.DisqualifyLaw();

            if (effects.IsPure)
            {
                EffectError(summary, "STK4100", $"Law '{signature.Name}' cannot return an 'out' or 'init' type.", functionDeclaration.ReturnType);
            }
        }

        for (var index = 0; index < functionDeclaration.ParameterList.parameter().Length; index++)
        {
            var parameterContext = functionDeclaration.ParameterList.parameter(index);
            var parameter = signature.Parameters[index];

            ValidateTypeUsage(parameter.Type, TypeUsage.Parameter, parameterContext.type_(), declaration.Modifiers.IsFfi);

            if (parameter.Type.InitializationKind != StarkInitializationKind.None)
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(
                        summary,
                        "STK4101",
                        $"Law '{signature.Name}' cannot declare 'out' or 'init' parameters.",
                        parameterContext.type_());
                }
            }
        }
    }

    private void ValidateFunctionModifiers(DeclaredFunctionSyntax functionDeclaration, FunctionValidationBuilder summary)
    {
        var inlineModifiers = functionDeclaration.Modifiers
            .Where(static modifier =>
            {
                var text = modifier.GetText();
                return text is "inline" or "noinline" or "inlinehint";
            })
            .Select(static modifier => modifier.GetText())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (inlineModifiers.Length > 1)
        {
            EffectError(
                summary,
                "STK4109",
                $"Function '{functionDeclaration.DisplaySourceName}' may use at most one of 'inline', 'noinline', or 'inlinehint'. Found: {string.Join(", ", inlineModifiers.Select(static modifier => $"'{modifier}'"))}.",
                functionDeclaration.DeclarationContext);
        }

        var hasHot = functionDeclaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "hot", StringComparison.Ordinal));
        var hasCold = functionDeclaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "cold", StringComparison.Ordinal));

        if (hasHot && hasCold)
        {
            EffectError(
                summary,
                "STK4110",
                $"Function '{functionDeclaration.DisplaySourceName}' may not combine 'hot' and 'cold'.",
                functionDeclaration.DeclarationContext);
        }
    }

    private void CheckBlock(
        StarkParser.BlockContext block,
        ValidationScope parentScope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        var scope = new ValidationScope(parentScope);
        foreach (var statement in block.statement())
        {
            CheckStatement(statement, scope, function, effects, summary);
        }
    }

    private void CheckStatement(
        StarkParser.StatementContext statement,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        if (statement.block() is { } nestedBlock)
        {
            CheckBlock(nestedBlock, scope, function, effects, summary);
            return;
        }

        if (statement.localConstantDeclaration() is { } constantDeclaration)
        {
            var declaredType = _typeResolver.ResolveType(constantDeclaration.type_());
            ValidateTypeUsage(declaredType, TypeUsage.Local, constantDeclaration.type_(), isFfiBoundary: false);

            foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
            {
                if (declarator.variableInitializer() is { } initializer)
                {
                    CheckVariableInitializer(initializer, scope, function, effects, summary);
                }

                scope.Declare(new VariableSymbol(
                    declarator.Identifier().GetText(),
                    declaredType,
                    SymbolOrigin.Local,
                    LocalStorageClass.None,
                    IsMutable: false,
                    IsConstant: true));
            }

            return;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            var storageClass = ParseStorageClass(localVariable.storageClass());
            var declaredType = _typeResolver.ResolveType(localVariable.type_());
            ValidateTypeUsage(declaredType, TypeUsage.Local, localVariable.type_(), isFfiBoundary: false);

            if (storageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static)
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(summary, "STK4102", $"Law '{function.Name}' cannot allocate or publish local '{storageClass.ToString().ToLowerInvariant()}' storage.", localVariable.storageClass());
                }
            }

            foreach (var declarator in localVariable.variableDeclarators().variableDeclarator())
            {
                if (declarator.variableInitializer() is { } initializer)
                {
                    CheckVariableInitializer(initializer, scope, function, effects, summary);
                }

                scope.Declare(new VariableSymbol(
                    declarator.Identifier().GetText(),
                    declaredType,
                    SymbolOrigin.Local,
                    storageClass,
                    IsMutable: localVariable.MUT() is not null,
                    IsConstant: false));
            }

            return;
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            EvaluateExpression(ifStatement.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            CheckStatement(ifStatement.statement(0), new ValidationScope(scope), function, effects, summary);
            if (ifStatement.statement().Length > 1)
            {
                CheckStatement(ifStatement.statement(1), new ValidationScope(scope), function, effects, summary);
            }

            return;
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            var switchValue = EvaluateExpression(switchStatement.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);

            foreach (var section in switchStatement.switchSection())
            {
                var sectionScope = new ValidationScope(scope);
                foreach (var label in section.switchLabel())
                {
                    if (label.pattern() is { } pattern)
                    {
                        BindSwitchPattern(pattern, switchValue.Type, sectionScope);
                    }

                    if (label.whenClause() is { } whenClause)
                    {
                        EvaluateExpression(whenClause.expression(), sectionScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
                    }
                }

                foreach (var nestedStatement in section.statement())
                {
                    CheckStatement(nestedStatement, sectionScope, function, effects, summary);
                }
            }

            return;
        }

        if (statement.whileStatement() is { } whileStatement)
        {
            ValidateLoopContract(function.Name, whileStatement.loopBehavior().GetText(), whileStatement.expression(), whileStatement.statement(), whileStatement.loopBehavior(), summary);

            if (whileStatement.loopBehavior().GetText() != "willexit")
            {
                summary.DisqualifyFinite();

                if (effects.WillReturn)
                {
                    EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", whileStatement.loopBehavior());
                }
            }

            EvaluateExpression(whileStatement.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            CheckStatement(whileStatement.statement(), new ValidationScope(scope), function, effects, summary);
            return;
        }

        if (statement.forStatement() is { } forStatement)
        {
            ValidateLoopContract(function.Name, forStatement.loopBehavior().GetText(), forStatement.forCondition()?.expression(), forStatement.statement(), forStatement.loopBehavior(), summary);

            if (forStatement.loopBehavior().GetText() != "willexit")
            {
                summary.DisqualifyFinite();

                if (effects.WillReturn)
                {
                    EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", forStatement.loopBehavior());
                }
            }

            var loopScope = new ValidationScope(scope);

            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForDeclaration)
            {
                var storageClass = ParseStorageClass(localForDeclaration.storageClass());
                var declaredType = _typeResolver.ResolveType(localForDeclaration.type_());
                ValidateTypeUsage(declaredType, TypeUsage.Local, localForDeclaration.type_(), isFfiBoundary: false);

                if (storageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static)
                {
                    summary.DisqualifyLaw();

                    if (effects.IsPure)
                    {
                        EffectError(summary, "STK4102", $"Law '{function.Name}' cannot allocate or publish local '{storageClass.ToString().ToLowerInvariant()}' storage.", localForDeclaration.storageClass());
                    }
                }

                foreach (var declarator in localForDeclaration.variableDeclarators().variableDeclarator())
                {
                    if (declarator.variableInitializer() is { } initializer)
                    {
                        CheckVariableInitializer(initializer, loopScope, function, effects, summary);
                    }

                    loopScope.Declare(new VariableSymbol(
                        declarator.Identifier().GetText(),
                        declaredType,
                        SymbolOrigin.Local,
                        storageClass,
                        IsMutable: localForDeclaration.MUT() is not null,
                        IsConstant: false));
                }
            }
            else if (forStatement.forInitializer()?.expressionList() is { } initializerExpressions)
            {
                foreach (var expression in initializerExpressions.expression())
                {
                    EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
                }
            }

            if (forStatement.forCondition() is { } condition)
            {
                EvaluateExpression(condition.expression(), loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }

            if (forStatement.forIterator() is { } iterator)
            {
                foreach (var expression in iterator.expressionList().expression())
                {
                    EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
                }
            }

            CheckStatement(forStatement.statement(), loopScope, function, effects, summary);
            return;
        }

        if (statement.returnStatement() is { } returnStatement)
        {
            if (returnStatement.expression() is not null)
            {
                var returnedValue = EvaluateExpression(
                    returnStatement.expression(),
                    scope,
                    function,
                    effects,
                    summary,
                    allowFunctionReference: false,
                    ExpressionObservation.Read);
                RecordReturnCapture(returnedValue, function, summary);
            }

            return;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            EvaluateExpression(
                expressionStatement.expression(),
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read);
        }
    }

    private void CheckVariableInitializer(
        StarkParser.VariableInitializerContext initializer,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        if (initializer.expression() is { } expression)
        {
            EvaluateExpression(expression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            return;
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            foreach (var memberInitializer in objectInitializer.memberInitializer())
            {
                CheckVariableInitializer(memberInitializer.variableInitializer(), scope, function, effects, summary);
            }

            return;
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            foreach (var item in arrayInitializer.variableInitializer())
            {
                CheckVariableInitializer(item, scope, function, effects, summary);
            }
        }
    }

    private ValidationValue EvaluateExpression(
        StarkParser.ExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateAssignmentExpression(expression.assignmentExpression(), scope, function, effects, summary, allowFunctionReference, observation);
    }

    private ValidationValue EvaluateAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        if (expression.conditionalExpression() is { } conditionalExpression)
        {
            return EvaluateConditionalExpression(conditionalExpression, scope, function, effects, summary, allowFunctionReference, observation);
        }

        var left = EvaluateUnaryExpression(
            expression.unaryExpression(),
            scope,
            function,
            effects,
            summary,
            allowFunctionReference: true,
            ExpressionObservation.WriteTarget);
        var right = EvaluateAssignmentExpression(
            expression.assignmentExpression(),
            scope,
            function,
            effects,
            summary,
            allowFunctionReference: false,
            ExpressionObservation.Read);

        RecordObservedMemoryWrite(left, summary);

        if (IsVisibleMemoryWrite(left))
        {
            summary.DisqualifyLaw();

            if (effects.IsPure)
            {
                EffectError(summary, "STK4104", $"Law '{function.Name}' cannot perform externally visible writes.", expression.unaryExpression());
            }
        }

        return new ValidationValue(left.Type);
    }

    private ValidationValue EvaluateConditionalExpression(
        StarkParser.ConditionalExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var condition = EvaluateLogicalOrExpression(expression.logicalOrExpression(), scope, function, effects, summary, allowFunctionReference, ExpressionObservation.Read);
        if (expression.expression().Length == 0)
        {
            return condition;
        }

        var whenTrue = EvaluateExpression(expression.expression(0), scope, function, effects, summary, allowFunctionReference: false, observation);
        var whenFalse = EvaluateExpression(expression.expression(1), scope, function, effects, summary, allowFunctionReference: false, observation);
        return new ValidationValue(FindCommonType(whenTrue.Type, whenFalse.Type));
    }

    private ValidationValue EvaluateLogicalOrExpression(
        StarkParser.LogicalOrExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.logicalAndExpression()
            .Select(item => EvaluateLogicalAndExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateLogicalAndExpression(
        StarkParser.LogicalAndExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.bitwiseOrExpression()
            .Select(item => EvaluateBitwiseOrExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateBitwiseOrExpression(
        StarkParser.BitwiseOrExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.bitwiseXorExpression(),
            item => EvaluateBitwiseXorExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateBitwiseXorExpression(
        StarkParser.BitwiseXorExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.bitwiseAndExpression(),
            item => EvaluateBitwiseAndExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateBitwiseAndExpression(
        StarkParser.BitwiseAndExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.equalityExpression(),
            item => EvaluateEqualityExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateEqualityExpression(
        StarkParser.EqualityExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.relationalExpression()
            .Select(item => EvaluateRelationalExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateRelationalExpression(
        StarkParser.RelationalExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var operands = expression.shiftExpression()
            .Select(item => EvaluateShiftExpression(item, scope, function, effects, summary, allowFunctionReference, observation))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateShiftExpression(
        StarkParser.ShiftExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.additiveExpression(),
            item => EvaluateAdditiveExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateAdditiveExpression(
        StarkParser.AdditiveExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.multiplicativeExpression(),
            item => EvaluateMultiplicativeExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateMultiplicativeExpression(
        StarkParser.MultiplicativeExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        return EvaluateBinaryChain(
            expression.unaryExpression(),
            item => EvaluateUnaryExpression(item, scope, function, effects, summary, allowFunctionReference, observation));
    }

    private ValidationValue EvaluateUnaryExpression(
        StarkParser.UnaryExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        if (expression.powerExpression() is { } powerExpression)
        {
            return EvaluatePowerExpression(powerExpression, scope, function, effects, summary, allowFunctionReference, observation);
        }

        if (expression.conversionType() is { } conversionType)
        {
            var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            var targetType = _typeResolver.ResolveConversionType(conversionType);
            return CreateConvertedValidationValue(targetType, operand);
        }

        var op = expression.unaryOperator()?.GetText() ?? expression.GetChild(0).GetText();

        if (op == "&")
        {
            var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.WriteTarget);
            return CreateAddressOfValidationValue(operand);
        }

        if (op == "*")
        {
            var operand = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            var result = CreateDereferenceValidationValue(operand);
            if (observation == ExpressionObservation.Read)
            {
                RecordObservedMemoryRead(result, summary);
            }

            return result;
        }

        return EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false, observation);
    }

    private ValidationValue EvaluatePowerExpression(
        StarkParser.PowerExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var left = EvaluatePostfixExpression(expression.postfixExpression(), scope, function, effects, summary, allowFunctionReference, observation);
        if (expression.unaryExpression() is not { } rightExpression)
        {
            return left;
        }

        var right = EvaluateUnaryExpression(rightExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
        return new ValidationValue(FindCommonType(left.Type, right.Type));
    }

    private ValidationValue EvaluatePostfixExpression(
        StarkParser.PostfixExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        var requiresCallableTarget = expression.postfixPart().Any(static part => part.argumentList() is not null);
        var binding = EvaluatePrimaryExpression(expression.primaryExpression(), scope, function, effects, summary, allowFunctionReference || requiresCallableTarget, observation);

        foreach (var postfixPart in expression.postfixPart())
        {
            if (postfixPart.argumentList() is { } argumentList)
            {
                binding = InvokeCall(binding, argumentList, scope, function, effects, summary);
                continue;
            }

            if (postfixPart.expressionList() is { } expressionList)
            {
                binding = ApplyIndex(binding, expressionList, scope, function, effects, summary);
                continue;
            }

            binding = ApplyMemberAccess(binding, postfixPart.Identifier().GetText());
        }

        if (observation == ExpressionObservation.Read)
        {
            RecordObservedMemoryRead(binding, summary);
        }

        return binding;
    }

    private ValidationValue EvaluatePrimaryExpression(
        StarkParser.PrimaryExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation)
    {
        if (expression.literal() is { } literal)
        {
            return new ValidationValue(EvaluateLiteralType(literal));
        }

        if (expression.Identifier() is { } identifier)
        {
            return ResolveValue(identifier.GetText(), scope, function, effects, summary, allowFunctionReference, observation, identifier.Symbol);
        }

        if (expression.enumConstructorExpression() is { } enumConstructorExpression)
        {
            return EvaluateEnumConstructorExpression(enumConstructorExpression, scope, function, effects, summary);
        }

        if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
        {
            return ResolveValue(genericEnumCaseReference.GetText(), scope, function, effects, summary, allowFunctionReference, observation, genericEnumCaseReference.Start);
        }

        if (expression.qualifiedName() is { } qualifiedName)
        {
            return ResolveValue(qualifiedName.GetText(), scope, function, effects, summary, allowFunctionReference, observation, qualifiedName.Start);
        }

        if (expression.objectCreationExpression() is { } objectCreationExpression)
        {
            return EvaluateObjectCreation(objectCreationExpression, scope, function, effects, summary);
        }

        return EvaluateExpression(expression.expression(), scope, function, effects, summary, allowFunctionReference: false, observation);
    }

    private ValidationValue EvaluateObjectCreation(
        StarkParser.ObjectCreationExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        var createdType = _typeResolver.ResolveType(expression.type_());

        if (expression.argumentList() is { } argumentList)
        {
            foreach (var argument in argumentList.argument())
            {
                EvaluateExpression(argument.expression(), scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }
        }

        if (expression.objectInitializer() is { } objectInitializer)
        {
            foreach (var memberInitializer in objectInitializer.memberInitializer())
            {
                CheckVariableInitializer(memberInitializer.variableInitializer(), scope, function, effects, summary);
            }
        }

        return new ValidationValue(createdType, NamedType: ResolveNamedTypeSymbol(createdType));
    }

    private ValidationValue EvaluateEnumConstructorExpression(
        StarkParser.EnumConstructorExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        var constructorName = expression.enumCaseTarget().GetText();
        if (!TryResolveEnumCaseReference(constructorName, out var enumType, out var enumTypeSymbol, out var variant))
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        foreach (var member in expression.enumConstructorInitializer().enumConstructorMember())
        {
            EvaluateExpression(
                member.expression(),
                scope,
                function,
                effects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read);
        }

        return variant.UsesNamedFields
            ? new ValidationValue(enumTypeSymbol, NamedType: enumType)
            : new ValidationValue(StarkTypeSymbols.Error);
    }

    private ValidationValue ResolveValue(
        string name,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        ExpressionObservation observation,
        IToken token)
    {
        if (scope.TryLookup(name, out var local))
        {
            return new ValidationValue(local.Type, IsAssignable: local.IsMutable && !local.IsConstant, RootSymbol: local, NamedType: ResolveNamedTypeSymbol(local.Type));
        }

        if (_typeModel.Globals.TryGetValue(name, out var globalType))
        {
            if (observation == ExpressionObservation.Read)
            {
                summary.DisqualifyLaw();

                if (effects.IsPure)
                {
                    EffectError(summary, "STK4105", $"Law '{function.Name}' cannot read global state.", token);
                }
            }

            var isMutable = globalType.IsMutable;

            return new ValidationValue(
                globalType.Type,
                IsAssignable: isMutable,
                RootSymbol: new VariableSymbol(name, globalType.Type, SymbolOrigin.Global, LocalStorageClass.Static, isMutable, IsConstant: !isMutable),
                NamedType: ResolveNamedTypeSymbol(globalType.Type));
        }

        if (TryGetFunctionOverloads(name, out var targetFunctions))
        {
            if (!allowFunctionReference)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            if (targetFunctions.Count == 1)
            {
                return new ValidationValue(targetFunctions[0].ReturnType, Function: targetFunctions[0]);
            }

            return new ValidationValue(StarkTypeSymbols.Error, OverloadSourceName: name);
        }

        if (TryResolveNamedTypeBySourceName(name, out var namedType))
        {
            if (namedType.Kind == DeclarationKind.Doctrine && allowFunctionReference)
            {
                return new ValidationValue(StarkTypeSymbols.Named(namedType.Name), NamedType: namedType);
            }

            if (namedType.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }
        }

        if (TryResolveNamedTypeBySourceName(name, out namedType) && namedType.Kind == DeclarationKind.Enum)
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: name);
        }

        if (_moduleGraph.HasModule(name))
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: name);
        }

        if (_moduleGraph.HasModuleNamespace(name))
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: name);
        }

        if (TryResolveEnumCaseReference(name, out var enumType, out var enumTypeSymbol, out var variant))
        {
            if (variant.IsUnit)
            {
                return new ValidationValue(enumTypeSymbol, NamedType: enumType);
            }

            if (!variant.UsesNamedFields && allowFunctionReference)
            {
                return new ValidationValue(
                    enumTypeSymbol,
                    NamedType: enumType,
                    EnumConstructor: new EnumConstructorBinding(name, variant));
            }

            return new ValidationValue(StarkTypeSymbols.Error);
        }

        return new ValidationValue(StarkTypeSymbols.Error);
    }

    private void BindSwitchPattern(StarkParser.PatternContext pattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        if (pattern.VAR() is not null && pattern.Identifier() is { } capture)
        {
            if (switchType.Kind != StarkTypeKind.Named)
            {
                scope.Declare(new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            }

            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, switchType, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, switchType, scope);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, switchType, scope))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, switchType, scope);
        }
    }

    private void BindAggregateSwitchPattern(StarkParser.AggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        var patternType = ResolvePatternSimpleType(aggregatePattern.simpleType());
        if (switchType.Kind != StarkTypeKind.Named
            || patternType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || patternType.NamedType is null
            || !string.Equals(switchType.NamedType, patternType.NamedType, StringComparison.Ordinal)
            || !_typeModel.NamedTypes.TryGetValue(switchType.NamedType, out var namedType))
        {
            return;
        }

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null || suffix.Identifier() is not null)
        {
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope);
        }
    }

    private bool TryBindEnumAggregateSwitchPattern(StarkParser.AggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.simpleType().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchType,
            scope,
            out var matched)
            && matched;
    }

    private bool TryBindEnumAggregateSwitchPattern(StarkParser.GenericEnumAggregatePatternContext aggregatePattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.genericEnumCaseReference().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchType,
            scope,
            out var matched)
            && matched;
    }

    private bool TryBindResolvedEnumAggregateSwitchPattern(
        string caseName,
        StarkParser.AggregatePatternSuffixContext? suffix,
        StarkTypeSymbol switchType,
        ValidationScope scope,
        out bool matched)
    {
        matched = false;
        if (!TryResolveEnumCaseReference(caseName, out var enumType, out _, out var variant))
        {
            return false;
        }

        matched = true;
        if (switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal)
            || variant.UsesNamedFields)
        {
            return true;
        }

        if (variant.IsUnit || suffix is null || suffix.Identifier() is not null)
        {
            return true;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != variant.Fields.Count)
        {
            return true;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindEnumVariantFieldPattern(fieldPatterns[index], variant.Fields[index], scope);
        }

        return true;
    }

    private void BindEnumNamedFieldPattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        StarkTypeSymbol switchType,
        ValidationScope scope)
    {
        if (!TryResolveEnumCaseReference(enumNamedFieldPattern.enumCaseTarget().GetText(), out var enumType, out _, out var variant)
            || switchType.Kind != StarkTypeKind.Named
            || switchType.NamedType is null
            || !string.Equals(switchType.NamedType, enumType.Name, StringComparison.Ordinal)
            || !variant.UsesNamedFields)
        {
            return;
        }

        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember())
        {
            var memberName = member.Identifier().GetText();
            var field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
            if (field is null || !seenMembers.Add(memberName))
            {
                continue;
            }

            BindEnumVariantFieldPattern(member.pattern(), field, scope);
        }
    }

    private void BindEnumVariantFieldPattern(StarkParser.PatternContext pattern, EnumVariantFieldSymbol field, ValidationScope scope)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture)
        {
            scope.Declare(new VariableSymbol(capture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, field.Type, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, field.Type, scope);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, field.Type, scope))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, field.Type, scope);
        }
    }

    private void BindAggregateFieldPattern(StarkParser.PatternContext pattern, FieldSymbol field, ValidationScope scope)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture
            && SupportsAggregateFieldSubpattern(field.Type))
        {
            scope.Declare(new VariableSymbol(capture.GetText(), field.Type, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, field.Type, scope);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, field.Type, scope))
        {
            return;
        }

        if (pattern.aggregatePattern() is { } enumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(enumAggregatePattern, field.Type, scope))
        {
            return;
        }

        if (pattern.aggregatePattern() is not { } aggregatePattern
            || field.Type.Kind != StarkTypeKind.Named
            || field.Type.NamedType is null
            || !_typeModel.NamedTypes.TryGetValue(field.Type.NamedType, out var namedType))
        {
            return;
        }

        var patternType = ResolvePatternSimpleType(aggregatePattern.simpleType());
        if (patternType.Kind != StarkTypeKind.Named
            || patternType.NamedType is null
            || !string.Equals(field.Type.NamedType, patternType.NamedType, StringComparison.Ordinal))
        {
            return;
        }

        var suffix = aggregatePattern.aggregatePatternSuffix();
        if (suffix is null || suffix.Identifier() is not null)
        {
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], scope);
        }
    }

    private StarkTypeSymbol ResolvePatternSimpleType(StarkParser.SimpleTypeContext simpleType)
    {
        return _typeResolver.ResolveSimpleType(simpleType, currentModuleName: _syntaxModel.ModuleName);
    }

    private static bool SupportsAggregateFieldSubpattern(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer;
    }

    private ValidationValue InvokeCall(
        ValidationValue target,
        StarkParser.ArgumentListContext arguments,
        ValidationScope scope,
        FunctionDeclarationModel currentFunction,
        FunctionEffectProfile currentEffects,
        FunctionValidationBuilder summary)
    {
        if (target.EnumConstructor is not null)
        {
            foreach (var argument in arguments.argument())
            {
                EvaluateExpression(
                    argument.expression(),
                    scope,
                    currentFunction,
                    currentEffects,
                    summary,
                    allowFunctionReference: false,
                    ExpressionObservation.Read);
            }

            return new ValidationValue(target.Type, NamedType: target.NamedType);
        }

        var argumentValues = arguments.argument()
            .Select(argument => EvaluateExpression(
                argument.expression(),
                scope,
                currentFunction,
                currentEffects,
                summary,
                allowFunctionReference: false,
                ExpressionObservation.Read))
            .ToArray();

        if (target.OverloadSourceName is { } overloadSourceName)
        {
            if (!TryGetFunctionOverloads(overloadSourceName, out var overloads))
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                target.Receiver?.Type,
                argumentValues.Select(static argument => argument.Type).ToArray(),
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            target = target with
            {
                Function = resolution.Match,
                OverloadSourceName = null,
                Type = resolution.Match!.ReturnType,
                NamedType = ResolveNamedTypeSymbol(resolution.Match.ReturnType)
            };
        }

        if (target.Function is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var receiverOffset = target.Receiver is null ? 0 : 1;
        var explicitParameterCount = Math.Max(0, target.Function.Parameters.Count - receiverOffset);

        if (_effectModel.Functions.TryGetValue(target.Function.Name, out var calleeEffects))
        {
            summary.CalledFunctions.Add(target.Function.Name);

            if (calleeEffects.IsFfi)
            {
                if (target.Receiver is not null
                    && target.Function.Parameters.Count != 0
                    && target.Receiver.Type.BorrowKind != StarkBorrowKind.None)
                {
                    BorrowError(summary, "STK4001", $"Safe borrows may not cross an 'ffi' boundary. Argument 1 to '{target.Function.DisplaySourceName}' must use a raw pointer form instead.", arguments);
                }

                for (var index = 0; index < Math.Min(explicitParameterCount, argumentValues.Length); index++)
                {
                    var argumentValue = argumentValues[index];
                    if (argumentValue.Type.BorrowKind != StarkBorrowKind.None)
                    {
                        BorrowError(summary, "STK4001", $"Safe borrows may not cross an 'ffi' boundary. Argument {index + receiverOffset + 1} to '{target.Function.DisplaySourceName}' must use a raw pointer form instead.", arguments.argument(index));
                    }
                }

                return new ValidationValue(target.Function.ReturnType, NamedType: ResolveNamedTypeSymbol(target.Function.ReturnType));
            }
        }

        var pendingArguments = new List<PendingCallArgument>();

        if (target.Receiver is not null && target.Function.Parameters.Count != 0)
        {
            var receiverParameter = target.Function.Parameters[0];
            ValidateBorrowArgumentFlow(target.Receiver.Type, receiverParameter.Type, target.Function.DisplaySourceName, 0, summary, arguments);
            pendingArguments.Add(CreatePendingCallArgument(0, target.Receiver, receiverParameter, target.Function.ReturnType));
        }

        for (var index = 0; index < Math.Min(explicitParameterCount, argumentValues.Length); index++)
        {
            var parameter = target.Function.Parameters[index + receiverOffset];
            var argumentValue = argumentValues[index];
            ValidateBorrowArgumentFlow(argumentValue.Type, parameter.Type, target.Function.DisplaySourceName, index + receiverOffset, summary, arguments.argument(index));
            pendingArguments.Add(CreatePendingCallArgument(index + receiverOffset, argumentValue, parameter, target.Function.ReturnType));
        }

        summary.PendingCalls.Add(new PendingCall(target.Function.Name, pendingArguments, arguments.Start));

        return new ValidationValue(target.Function.ReturnType, NamedType: ResolveNamedTypeSymbol(target.Function.ReturnType));
    }

    private void ValidateBorrowArgumentFlow(
        StarkTypeSymbol argumentType,
        StarkTypeSymbol parameterType,
        string calleeName,
        int argumentIndex,
        FunctionValidationBuilder summary,
        ParserRuleContext context)
    {
        if (argumentType.BorrowKind == StarkBorrowKind.Borrow
            && parameterType.BorrowKind != StarkBorrowKind.Borrow)
        {
            BorrowError(summary, "STK4002", $"Non-escaping borrows may only be passed to 'borrow' parameters. Argument {argumentIndex + 1} to '{calleeName}' would escape too far.", context);
        }

        if (argumentType.BorrowKind == StarkBorrowKind.RetBorrow
            && parameterType.BorrowKind != StarkBorrowKind.Borrow)
        {
            BorrowError(summary, "STK4003", $"A 'retborrow' value may only be passed to a non-escaping 'borrow' parameter unless it is returned directly.", context);
        }

        if (parameterType.Kind == StarkTypeKind.RawPointer && argumentType.BorrowKind != StarkBorrowKind.None)
        {
            BorrowError(summary, "STK4001", $"Safe borrows may not be converted implicitly to raw pointers when calling '{calleeName}'.", context);
        }
    }

    private ValidationValue ApplyIndex(
        ValidationValue target,
        StarkParser.ExpressionListContext indexes,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        if (target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
        {
            foreach (var indexExpression in indexes.expression())
            {
                EvaluateExpression(indexExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            }

            return new ValidationValue(
                target.Type,
                NamedType: ResolveNamedTypeSymbol(target.Type));
        }

        var currentType = target.Type;
        var currentIsAssignable = target.IsAssignable;
        foreach (var indexExpression in indexes.expression())
        {
            EvaluateExpression(indexExpression, scope, function, effects, summary, allowFunctionReference: false, ExpressionObservation.Read);
            if (currentType.ElementType is null)
            {
                currentType = StarkTypeSymbols.Error;
                continue;
            }

            currentIsAssignable &= currentType.AccessKind != StarkAccessKind.Frozen;
            currentType = ProjectFrozenView(currentType, currentType.ElementType);
        }

        return new ValidationValue(
            currentType,
            IsAssignable: currentIsAssignable,
            RootSymbol: target.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(currentType),
            IsIndirectStorageAccess: true);
    }

    private ValidationValue ApplyMemberAccess(ValidationValue target, string memberName)
    {
        if (target.NamespaceName is not null)
        {
            var qualifiedName = $"{target.NamespaceName}.{memberName}";
            if (_moduleGraph.HasModule(qualifiedName))
            {
                return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
            }

            if (_moduleGraph.HasModuleNamespace(qualifiedName))
            {
                return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
            }

            if (_typeModel.Globals.TryGetValue(qualifiedName, out var globalType))
            {
                var isMutable = globalType.IsMutable;
                return new ValidationValue(
                    globalType.Type,
                    IsAssignable: isMutable,
                    RootSymbol: new VariableSymbol(qualifiedName, globalType.Type, SymbolOrigin.Global, LocalStorageClass.Static, isMutable, IsConstant: !isMutable),
                    NamedType: ResolveNamedTypeSymbol(globalType.Type));
            }

            if (TryGetFunctionOverloads(qualifiedName, out var namespaceFunctions))
            {
                if (namespaceFunctions.Count == 1)
                {
                    return new ValidationValue(namespaceFunctions[0].ReturnType, Function: namespaceFunctions[0]);
                }

                return new ValidationValue(StarkTypeSymbols.Error, OverloadSourceName: qualifiedName);
            }

            if (TryResolveNamedTypeBySourceName(qualifiedName, out var qualifiedType))
            {
                if (qualifiedType.Kind == DeclarationKind.Enum)
                {
                    return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
                }

                if (qualifiedType.Kind == DeclarationKind.Doctrine)
                {
                    return new ValidationValue(StarkTypeSymbols.Named(qualifiedType.Name), NamedType: qualifiedType);
                }

                if (qualifiedType.Kind == DeclarationKind.Trait)
                {
                    return new ValidationValue(StarkTypeSymbols.Error);
                }
            }

            if (TryResolveEnumCaseReference(qualifiedName, out var enumType, out var enumTypeSymbol, out var variant))
            {
                if (variant.IsUnit)
                {
                    return new ValidationValue(enumTypeSymbol, NamedType: enumType);
                }

                return new ValidationValue(
                    enumTypeSymbol,
                    NamedType: enumType,
                    EnumConstructor: new EnumConstructorBinding(qualifiedName, variant));
            }
        }

        var namedType = target.NamedType ?? ResolveNamedTypeSymbol(target.Type);
        if (namedType is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        if (namedType.Fields.TryGetValue(memberName, out var field))
        {
            var projectedType = ProjectFrozenView(target.Type, field.Type);
            return new ValidationValue(
                projectedType,
                IsAssignable: target.IsAssignable && target.Type.AccessKind != StarkAccessKind.Frozen,
                RootSymbol: target.RootSymbol,
                NamedType: ResolveNamedTypeSymbol(projectedType),
                IsIndirectStorageAccess: true);
        }

        var methodSourceName = $"{namedType.Name}.{memberName}";
        if (namedType.Kind == DeclarationKind.Doctrine
            && TryGetFunctionOverloads(methodSourceName, out var doctrineMethods))
        {
            return doctrineMethods.Count == 1
                ? new ValidationValue(
                    doctrineMethods[0].ReturnType,
                    Function: doctrineMethods[0],
                    NamedType: ResolveNamedTypeSymbol(doctrineMethods[0].ReturnType))
                : new ValidationValue(StarkTypeSymbols.Error, OverloadSourceName: methodSourceName);
        }

        if (TryGetFunctionOverloads(methodSourceName, out var methods))
        {
            if (methods.Count == 1 && methods[0].Parameters.Count != 0)
            {
                return new ValidationValue(
                    methods[0].ReturnType,
                    Function: methods[0],
                    NamedType: ResolveNamedTypeSymbol(methods[0].ReturnType),
                    Receiver: target);
            }

            return new ValidationValue(StarkTypeSymbols.Error, Receiver: target, OverloadSourceName: methodSourceName);
        }

        return new ValidationValue(
            StarkTypeSymbols.Error);
    }

    private ValidationValue CreateConvertedValidationValue(StarkTypeSymbol targetType, ValidationValue operand)
    {
        return PreservesStorageView(targetType, operand.Type)
            ? new ValidationValue(
                targetType,
                RootSymbol: operand.RootSymbol,
                NamedType: ResolveNamedTypeSymbol(targetType),
                IsIndirectStorageAccess: operand.IsIndirectStorageAccess)
            : new ValidationValue(targetType, NamedType: ResolveNamedTypeSymbol(targetType));
    }

    private bool TryGetFunctionOverloads(string sourceName, out IReadOnlyList<TypedFunctionSignature> overloads)
    {
        if (_typeModel.Overloads.TryGetValue(sourceName, out overloads!))
        {
            return true;
        }

        overloads = [];
        return false;
    }

    private bool TryResolveEnumCaseReference(
        string name,
        out NamedTypeSymbol enumType,
        out StarkTypeSymbol enumTypeSymbol,
        out EnumVariantSymbol variant)
    {
        enumType = null!;
        enumTypeSymbol = StarkTypeSymbols.Error;
        variant = null!;

        var separator = name.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var enumTypeName = name[..separator];
        var variantName = name[(separator + 1)..];
        if (!TryResolveNamedTypeBySourceName(enumTypeName, out enumType)
            || enumType.Kind != DeclarationKind.Enum
            || !enumType.TryGetVariant(variantName, out variant, out _))
        {
            enumType = null!;
            variant = null!;
            return false;
        }

        enumTypeSymbol = StarkTypeSymbols.Named(enumType.Name);
        return true;
    }

    private bool TryResolveNamedTypeBySourceName(string typeName, out NamedTypeSymbol namedType)
    {
        if (_typeModel.NamedTypes.TryGetValue(typeName, out namedType!))
        {
            return true;
        }

        if (!typeName.Contains('.', StringComparison.Ordinal)
            && _typeModel.NamedTypes.TryGetValue($"{_syntaxModel.ModuleName}.{typeName}", out namedType!))
        {
            return true;
        }

        namedType = null!;
        return false;
    }

    private ValidationValue CreateAddressOfValidationValue(ValidationValue operand)
    {
        if (operand.RootSymbol is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var pointerType = StarkTypeSymbols.RawPointer(operand.Type, operand.IsAssignable);
        return new ValidationValue(
            pointerType,
            RootSymbol: operand.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(pointerType),
            IsIndirectStorageAccess: true);
    }

    private ValidationValue CreateDereferenceValidationValue(ValidationValue operand)
    {
        if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var pointeeType = operand.Type.ElementType;
        return new ValidationValue(
            pointeeType,
            IsAssignable: operand.Type.IsMutablePointer && pointeeType.AccessKind != StarkAccessKind.Frozen,
            RootSymbol: operand.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(pointeeType),
            IsIndirectStorageAccess: true);
    }

    private PendingCallArgument CreatePendingCallArgument(
        int argumentIndex,
        ValidationValue argumentValue,
        TypedParameterSymbol calleeParameter,
        StarkTypeSymbol calleeReturnType)
    {
        var aliasing = CanAliasCalleeParameterMemory(calleeParameter.Type);
        var callerParameterName = aliasing && argumentValue.RootSymbol?.Origin == SymbolOrigin.Parameter
            ? argumentValue.RootSymbol.Name
            : null;
        var fallbackEffects = DeriveFallbackArgumentEffects(calleeParameter, calleeReturnType, hasBody: false);
        return new PendingCallArgument(argumentIndex, callerParameterName, calleeParameter.Name, aliasing, argumentValue.RootSymbol, fallbackEffects);
    }

    private void RecordObservedMemoryRead(ValidationValue value, FunctionValidationBuilder summary)
    {
        if (value.RootSymbol?.Origin != SymbolOrigin.Parameter || !value.IsIndirectStorageAccess)
        {
            return;
        }

        summary.MarkParameterRead(value.RootSymbol.Name);
    }

    private void RecordObservedMemoryWrite(ValidationValue value, FunctionValidationBuilder summary)
    {
        if (value.RootSymbol?.Origin != SymbolOrigin.Parameter || !value.IsIndirectStorageAccess)
        {
            return;
        }

        summary.MarkParameterWrite(value.RootSymbol.Name);
    }

    private void RecordReturnCapture(ValidationValue value, FunctionDeclarationModel function, FunctionValidationBuilder summary)
    {
        if (value.RootSymbol?.Origin != SymbolOrigin.Parameter)
        {
            return;
        }

        var captureKind = function.ReturnType switch
        {
            var returnType when returnType.Contains("storeborrow", StringComparison.Ordinal) => ParameterCaptureKind.Escape,
            var returnType when returnType.Contains("retborrow", StringComparison.Ordinal) => ParameterCaptureKind.Return,
            _ => ParameterCaptureKind.None
        };

        if (captureKind != ParameterCaptureKind.None)
        {
            summary.MarkParameterCapture(value.RootSymbol.Name, captureKind);
        }
    }

    private void FinalizeMemoryEffectsAndValidateCalls()
    {
        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var summary in _summaries.Values)
            {
                foreach (var pendingCall in summary.PendingCalls)
                {
                    foreach (var argument in pendingCall.Arguments)
                    {
                        if (!argument.AliasesCalleeMemory || argument.CallerParameterName is null)
                        {
                            continue;
                        }

                        var propagated = GetCallArgumentEffects(pendingCall.CalleeName, argument.CalleeParameterName, argument.FallbackEffects);
                        changed |= summary.ApplyArgumentEffects(argument.CallerParameterName, propagated);
                    }
                }
            }
        }

        foreach (var summary in _summaries.Values)
        {
            var declaredLaw = FunctionKindFacts.IsLaw(summary.DeclaredKind);

            foreach (var pendingCall in summary.PendingCalls)
            {
                foreach (var argument in pendingCall.Arguments)
                {
                    if (argument.RootSymbol is null || !IsExternallyVisibleMemory(argument.RootSymbol))
                    {
                        continue;
                    }

                    var propagated = GetCallArgumentEffects(pendingCall.CalleeName, argument.CalleeParameterName, argument.FallbackEffects);
                    if (!propagated.Writes && propagated.CaptureKind == ParameterCaptureKind.None)
                    {
                        continue;
                    }

                    summary.DisqualifyLaw();

                    if (!declaredLaw)
                    {
                        continue;
                    }

                    var operation = propagated.Writes && propagated.CaptureKind != ParameterCaptureKind.None
                        ? "perform externally visible writes or captures"
                        : propagated.Writes
                            ? "perform externally visible writes"
                            : "capture externally visible memory";
                    EffectError(
                        summary,
                        "STK4104",
                        $"Law '{summary.Name}' cannot {operation} through call '{pendingCall.CalleeName}'.",
                        pendingCall.Location);
                }
            }

            summary.BuildResolvedCalls(call =>
                BuildResolvedCallSummary(call));
        }
    }

    private ArgumentEffects GetCallArgumentEffects(string calleeName, string calleeParameterName, ArgumentEffects fallback)
    {
        if (_summaries.TryGetValue(calleeName, out var summary)
            && summary.TryGetParameter(calleeParameterName, out var parameter))
        {
            return parameter.GetEffectiveEffects(summary.HasBody);
        }

        return fallback;
    }

    private CallMemoryEffectSummary BuildResolvedCallSummary(PendingCall call)
    {
        var argumentSummaries = call.Arguments
            .OrderBy(static argument => argument.ArgumentIndex)
            .Select(argument =>
            {
                var effects = GetCallArgumentEffects(call.CalleeName, argument.CalleeParameterName, argument.FallbackEffects);
                return new CallArgumentMemoryEffectSummary(
                    argument.ArgumentIndex,
                    argument.CallerParameterName,
                    argument.CalleeParameterName,
                    effects.Reads,
                    effects.Writes,
                    effects.CaptureKind);
            })
            .ToArray();

        return new CallMemoryEffectSummary(
            call.CalleeName,
            new FunctionMemoryEffectSummary(
                argumentSummaries.Any(static argument => argument.Reads),
                argumentSummaries.Any(static argument => argument.Writes),
                argumentSummaries.Any(static argument => argument.CaptureKind != ParameterCaptureKind.None)),
            argumentSummaries);
    }

    private void InferEffectiveFunctionKindsAndValidateDeclaredContracts()
    {
        var effectiveLaw = _summaries.Values.ToDictionary(
            static summary => summary.Name,
            static summary => summary.DirectLawCompatible,
            StringComparer.Ordinal);
        var effectiveFinite = _summaries.Values.ToDictionary(
            static summary => summary.Name,
            static summary => summary.DirectFiniteCompatible,
            StringComparer.Ordinal);

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var summary in _summaries.Values.Where(static summary => summary.HasBody))
            {
                if (effectiveLaw[summary.Name]
                    && summary.CalledFunctions.Any(callee => !IsEffectiveLaw(callee, effectiveLaw)))
                {
                    effectiveLaw[summary.Name] = false;
                    changed = true;
                }

                if (effectiveFinite[summary.Name]
                    && summary.CalledFunctions.Any(callee => !IsEffectiveFinite(callee, effectiveFinite)))
                {
                    effectiveFinite[summary.Name] = false;
                    changed = true;
                }
            }
        }

        var finiteCycles = FindFiniteCycles(effectiveFinite);

        foreach (var function in finiteCycles)
        {
            effectiveFinite[function] = false;
        }

        foreach (var summary in _summaries.Values)
        {
            summary.SetEffectiveKind(
                summary.HasBody
                    ? FunctionKindFacts.Combine(effectiveLaw[summary.Name], effectiveFinite[summary.Name])
                    : summary.DeclaredKind);
        }

        foreach (var summary in _summaries.Values)
        {
            var declaredLaw = FunctionKindFacts.IsLaw(summary.DeclaredKind);
            var declaredFinite = FunctionKindFacts.IsFinite(summary.DeclaredKind);

            foreach (var pendingCall in summary.PendingCalls)
            {
                if (declaredLaw && !IsEffectiveLaw(pendingCall.CalleeName, effectiveLaw))
                {
                    EffectError(summary, "STK4106", $"Law '{summary.Name}' may only call other laws.", pendingCall.Location);
                }

                if (declaredFinite
                    && !IsEffectiveFinite(pendingCall.CalleeName, effectiveFinite)
                    && !(finiteCycles.Contains(summary.Name) && finiteCycles.Contains(pendingCall.CalleeName)))
                {
                    EffectError(summary, "STK4107", $"Finite function '{summary.Name}' may only call finite functions.", pendingCall.Location);
                }
            }
        }

        foreach (var function in finiteCycles)
        {
            if (_functionDeclarations.TryGetValue(function, out var declaration)
                && _summaries.TryGetValue(function, out var summary)
                && FunctionKindFacts.IsFinite(summary.DeclaredKind))
            {
                EffectError(summary, "STK4108", $"Finite function '{function}' participates in a recursive call cycle and cannot be proven finite.", declaration.NameToken);
            }
        }
    }

    private bool IsEffectiveLaw(string functionName, IReadOnlyDictionary<string, bool> effectiveLaw)
    {
        if (_summaries.TryGetValue(functionName, out var summary))
        {
            return summary.HasBody
                ? effectiveLaw[functionName]
                : FunctionKindFacts.IsLaw(summary.DeclaredKind);
        }

        return _effectModel.Functions.TryGetValue(functionName, out var effects) && effects.IsPure;
    }

    private bool IsEffectiveFinite(string functionName, IReadOnlyDictionary<string, bool> effectiveFinite)
    {
        if (_summaries.TryGetValue(functionName, out var summary))
        {
            return summary.HasBody
                ? effectiveFinite[functionName]
                : FunctionKindFacts.IsFinite(summary.DeclaredKind);
        }

        return _effectModel.Functions.TryGetValue(functionName, out var effects) && effects.WillReturn && effects.MustProgress;
    }

    private HashSet<string> FindFiniteCycles(IReadOnlyDictionary<string, bool> effectiveFinite)
    {
        var visited = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in effectiveFinite.Where(static pair => pair.Value).Select(static pair => pair.Key))
        {
            Visit(function);
        }

        return cyclic;

        void Visit(string function)
        {
            if (visited.TryGetValue(function, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    var cycleStart = stack.LastIndexOf(function);
                    if (cycleStart >= 0)
                    {
                        foreach (var item in stack.Skip(cycleStart))
                        {
                            cyclic.Add(item);
                        }
                    }
                }

                return;
            }

            visited[function] = VisitState.Visiting;
            stack.Add(function);

            if (_summaries.TryGetValue(function, out var summary) && summary.HasBody)
            {
                foreach (var callee in summary.CalledFunctions.Where(callee => IsEffectiveFinite(callee, effectiveFinite)))
                {
                    Visit(callee);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visited[function] = VisitState.Visited;
        }
    }

    private void ValidateTypeUsage(StarkTypeSymbol type, TypeUsage usage, ParserRuleContext context, bool isFfiBoundary)
    {
        if ((usage is TypeUsage.Global or TypeUsage.Local) && type.InitializationKind != StarkInitializationKind.None)
        {
            _context.Diagnostics.Error(
                "STK4004",
                $"'{type.InitializationKind.ToString().ToLowerInvariant()}' types are only valid on function parameters.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Return && type.InitializationKind != StarkInitializationKind.None)
        {
            _context.Diagnostics.Error(
                "STK4004",
                "Function return types may not use 'out' or 'init'.",
                "semantic-validate",
                Location(context.Start));
        }

        if (usage == TypeUsage.Global && (type.BorrowKind is StarkBorrowKind.Borrow or StarkBorrowKind.RetBorrow))
        {
            _context.Diagnostics.Error(
                "STK4005",
                $"Global declarations may not use '{type.BorrowKind.ToString().ToLowerInvariant()}' because those borrows are not allowed to escape globally.",
                "semantic-validate",
                Location(context.Start));
        }

        if (ContainsNestedRawPointer(type) && (!isFfiBoundary || usage is not (TypeUsage.Parameter or TypeUsage.Return)))
        {
            _context.Diagnostics.Error(
                "STK4006",
                "Pointers to pointers are only permitted on 'ffi' function boundaries through raw pointer types.",
                "semantic-validate",
                Location(context.Start));
        }

        if (TryFindCompileTimeOnlyTypeDependency(type, out var dependencyName, out var dependencyKind))
        {
            _context.Diagnostics.Error(
                "STK4009",
                $"Type '{type.DisplayName}' depends on compile-time-only {DescribeCompileTimeOnlyKind(dependencyKind)} '{dependencyName}', which is not allowed in {DescribeTypeUsage(usage)}. {DescribeNoDynamicDispatchPolicy()}",
                "semantic-validate",
                Location(context.Start));
        }
    }

    private bool TryFindCompileTimeOnlyTypeDependency(
        StarkTypeSymbol type,
        out string dependencyName,
        out DeclarationKind dependencyKind)
    {
        return TryFindCompileTimeOnlyTypeDependency(type, out dependencyName, out dependencyKind, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool TryFindCompileTimeOnlyTypeDependency(
        StarkTypeSymbol type,
        out string dependencyName,
        out DeclarationKind dependencyKind,
        ISet<string> activeNamedTypes)
    {
        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            && namedType.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
        {
            dependencyName = namedType.Name;
            dependencyKind = namedType.Kind;
            return true;
        }

        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var aggregateType))
        {
            if (!activeNamedTypes.Add(aggregateType.Name))
            {
                dependencyName = string.Empty;
                dependencyKind = default;
                return false;
            }

            try
            {
                foreach (var field in aggregateType.OrderedFields)
                {
                    if (TryFindCompileTimeOnlyTypeDependency(field.Type, out dependencyName, out dependencyKind, activeNamedTypes))
                    {
                        return true;
                    }
                }

                foreach (var variant in aggregateType.Variants)
                {
                    foreach (var field in variant.Fields)
                    {
                        if (TryFindCompileTimeOnlyTypeDependency(field.Type, out dependencyName, out dependencyKind, activeNamedTypes))
                        {
                            return true;
                        }
                    }
                }
            }
            finally
            {
                activeNamedTypes.Remove(aggregateType.Name);
            }
        }

        if (type.ElementType is not null)
        {
            return TryFindCompileTimeOnlyTypeDependency(type.ElementType, out dependencyName, out dependencyKind, activeNamedTypes);
        }

        dependencyName = string.Empty;
        dependencyKind = default;
        return false;
    }

    private static string DescribeCompileTimeOnlyKind(DeclarationKind kind)
    {
        return kind switch
        {
            DeclarationKind.Doctrine => "doctrine",
            DeclarationKind.Trait => "trait",
            _ => "type"
        };
    }

    private static string DescribeNoDynamicDispatchPolicy()
    {
        return "Stark v1.x has no runtime dispatch values for traits or doctrines.";
    }

    private static string DescribeTypeUsage(TypeUsage usage)
    {
        return usage switch
        {
            TypeUsage.Global => "global declarations",
            TypeUsage.Parameter => "function parameters",
            TypeUsage.Return => "function return types",
            TypeUsage.Local => "local declarations",
            _ => "runtime type positions"
        };
    }

    private bool TryValidateFrozenConstGlobalType(
        StarkTypeSymbol type,
        string path,
        ISet<string> visitingNamedTypes,
        out string failingPath,
        out string failureReason)
    {
        failingPath = path;
        failureReason = string.Empty;

        if (type.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (type.BorrowKind != StarkBorrowKind.None)
        {
            failureReason = $"uses borrow-qualified type '{type.DisplayName}'";
            return false;
        }

        if (type.AccessKind == StarkAccessKind.Shared)
        {
            failureReason = $"uses shared access type '{type.DisplayName}'";
            return false;
        }

        if (type.InitializationKind != StarkInitializationKind.None)
        {
            failureReason = $"uses initialization-qualified type '{type.DisplayName}'";
            return false;
        }

        if (type.IsMutableView)
        {
            failureReason = $"uses mutable-view type '{type.DisplayName}'";
            return false;
        }

        switch (type.Kind)
        {
            case StarkTypeKind.RawPointer:
                failureReason = $"uses raw pointer type '{type.DisplayName}'";
                return false;
            case StarkTypeKind.FixedArray:
            case StarkTypeKind.Slice:
                if (type.ElementType is null)
                {
                    return true;
                }

                return TryValidateFrozenConstGlobalType(type.ElementType, $"{path}[]", visitingNamedTypes, out failingPath, out failureReason);
            case StarkTypeKind.Named:
                if (type.NamedType is null || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType))
                {
                    return true;
                }

                if (!visitingNamedTypes.Add(namedType.Name))
                {
                    return true;
                }

                foreach (var field in namedType.OrderedFields)
                {
                    if (!TryValidateFrozenConstGlobalType(field.Type, $"{path}.{field.Name}", visitingNamedTypes, out failingPath, out failureReason))
                    {
                        visitingNamedTypes.Remove(namedType.Name);
                        return false;
                    }
                }

                visitingNamedTypes.Remove(namedType.Name);
                return true;
            default:
                return true;
        }
    }

    private bool CanMaterializeFrozenConstInitializer(
        StarkParser.VariableInitializerContext initializer,
        StarkTypeSymbol targetType)
    {
        if (targetType.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (initializer.expression() is { } expression)
        {
            return CanMaterializeFrozenConstExpression(expression, targetType);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            return CanMaterializeFrozenConstObjectInitializer(objectInitializer, targetType);
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            return CanMaterializeFrozenConstArrayInitializer(arrayInitializer, targetType);
        }

        return false;
    }

    private bool CanMaterializeFrozenConstExpression(
        StarkParser.ExpressionContext expression,
        StarkTypeSymbol targetType)
    {
        if (!TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression))
        {
            return false;
        }

        if (primaryExpression.literal() is not null)
        {
            return true;
        }

        if (primaryExpression.objectCreationExpression() is { } objectCreation)
        {
            return CanMaterializeFrozenConstObjectCreation(objectCreation, targetType);
        }

        if (primaryExpression.expression() is { } groupedExpression)
        {
            return CanMaterializeFrozenConstExpression(groupedExpression, targetType);
        }

        return false;
    }

    private bool CanMaterializeFrozenConstObjectCreation(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        StarkTypeSymbol targetType)
    {
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        var arguments = objectCreation.argumentList()?.argument() ?? [];
        if (arguments.Length != 0)
        {
            if (!TryGetObjectCreationConstructor(objectCreation, out var constructor)
                || constructor is null
                || !constructor.IsPrimaryShape
                || arguments.Length != constructor.Parameters.Count)
            {
                return false;
            }

            for (var index = 0; index < arguments.Length; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out _)
                    || !CanMaterializeFrozenConstExpression(arguments[index].expression(), field.Type))
                {
                    return false;
                }
            }
        }

        if (objectCreation.objectInitializer() is { } objectInitializer
            && !CanMaterializeFrozenConstObjectInitializer(objectInitializer, targetType))
        {
            return false;
        }

        return true;
    }

    private bool TryGetObjectCreationConstructor(
        StarkParser.ObjectCreationExpressionContext objectCreation,
        out TypedConstructorShape? constructor)
    {
        return _objectCreationConstructors.TryGetValue(
            new ObjectCreationKey(
                objectCreation.GetText(),
                objectCreation.Start.Line,
                objectCreation.Start.Column + 1),
            out constructor);
    }

    private bool CanMaterializeFrozenConstObjectInitializer(
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType)
    {
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType is null)
        {
            return false;
        }

        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberName = memberInitializer.Identifier().GetText();
            if (!namedType.Fields.TryGetValue(memberName, out var field))
            {
                return false;
            }

            if (!CanMaterializeFrozenConstInitializer(memberInitializer.variableInitializer(), field.Type))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanMaterializeFrozenConstArrayInitializer(
        StarkParser.ArrayInitializerContext arrayInitializer,
        StarkTypeSymbol targetType)
    {
        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is null)
        {
            return false;
        }

        if (targetType.FixedLength is int fixedLength
            && fixedLength != arrayInitializer.variableInitializer().Length)
        {
            return false;
        }

        foreach (var item in arrayInitializer.variableInitializer())
        {
            if (!CanMaterializeFrozenConstInitializer(item, targetType.ElementType))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExternalConstRawPointerPlaceholder(
        StarkTypeSymbol type,
        StarkParser.VariableInitializerContext initializer)
    {
        return type.Kind == StarkTypeKind.RawPointer
            && !type.IsMutablePointer
            && type.BorrowKind == StarkBorrowKind.None
            && type.AccessKind == StarkAccessKind.None
            && type.InitializationKind == StarkInitializationKind.None
            && !type.IsMutableView
            && initializer.expression() is { } expression
            && TryUnwrapSimplePrimaryExpression(expression, out var primaryExpression)
            && primaryExpression.literal()?.NULL() is not null;
    }

    private static bool TryUnwrapSimplePrimaryExpression(StarkParser.ExpressionContext expression, out StarkParser.PrimaryExpressionContext primaryExpression)
    {
        primaryExpression = null!;

        if (expression.assignmentExpression().conditionalExpression() is not { } conditionalExpression
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

    private static bool ContainsNestedRawPointer(StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.RawPointer)
        {
            if (type.ElementType?.Kind == StarkTypeKind.RawPointer)
            {
                return true;
            }

            return type.ElementType is not null && ContainsNestedRawPointer(type.ElementType);
        }

        return type.ElementType is not null && ContainsNestedRawPointer(type.ElementType);
    }

    private static bool IsVisibleMemoryWrite(ValidationValue target)
    {
        if (target.RootSymbol is null)
        {
            return false;
        }

        if (target.RootSymbol.Origin == SymbolOrigin.Global)
        {
            return true;
        }

        return target.IsIndirectStorageAccess && IsExternallyVisibleMemory(target.RootSymbol);
    }

    private static bool PreservesStorageView(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.RawPointer)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.FixedArray)
        {
            return true;
        }

        return false;
    }

    private static bool IsExternallyVisibleMemory(VariableSymbol symbol)
    {
        if (symbol.Origin == SymbolOrigin.Global)
        {
            return true;
        }

        return symbol.Type.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice
            || symbol.Type.BorrowKind != StarkBorrowKind.None
            || symbol.Type.InitializationKind != StarkInitializationKind.None
            || symbol.StorageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static;
    }

    private StarkTypeSymbol EvaluateLiteralType(StarkParser.LiteralContext literal)
    {
        if (literal.signedIntegerLiteral() is { } integerLiteral)
        {
            return InferIntegerLiteralType(ParseSignedIntegerLiteral(integerLiteral));
        }

        if (literal.FloatLiteral() is not null)
        {
            return StarkTypeSymbols.Float(32);
        }

        if (literal.StringLiteral() is { } stringLiteral)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(stringLiteral.GetText(), TextLiteralKind.String)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        if (literal.CharacterLiteral() is { } characterLiteral)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(characterLiteral.GetText(), TextLiteralKind.Character)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        if (literal.TRUE() is not null || literal.FALSE() is not null)
        {
            return StarkTypeSymbols.Bool;
        }

        return StarkTypeSymbols.Null;
    }

    private static ValidationValue EvaluateBinaryChain<TContext>(
        IEnumerable<TContext> operands,
        Func<TContext, ValidationValue> evaluate)
        where TContext : ParserRuleContext
    {
        ValidationValue? current = null;

        foreach (var operand in operands)
        {
            var binding = evaluate(operand);
            current = current is null
                ? binding
                : new ValidationValue(FindCommonType(current.Type, binding.Type));
        }

        return current ?? new ValidationValue(StarkTypeSymbols.Error);
    }

    private static StarkTypeSymbol FindCommonType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (left.Kind == StarkTypeKind.Error || right.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        if (Equals(left, right))
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Integer)
        {
            return StarkTypeSymbols.Integer(Math.Max(left.BitWidth ?? 0, right.BitWidth ?? 0));
        }

        if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Float)
        {
            return StarkTypeSymbols.Float(Math.Max(left.BitWidth ?? 32, right.BitWidth ?? 32));
        }

        if (left.Kind == StarkTypeKind.Float && right.Kind == StarkTypeKind.Integer)
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Integer && right.Kind == StarkTypeKind.Float)
        {
            return right;
        }

        return StarkTypeSymbols.Error;
    }

    private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
    {
        var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
        return literal.MINUS() is null ? value : -value;
    }

    private static StarkTypeSymbol InferIntegerLiteralType(BigInteger value)
    {
        var widths = new[] { 8, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024 };
        foreach (var width in widths)
        {
            var min = -(BigInteger.One << (width - 1));
            var max = (BigInteger.One << (width - 1)) - BigInteger.One;
            if (value >= min && value <= max)
            {
                return StarkTypeSymbols.Integer(width, value, value);
            }
        }

        return StarkTypeSymbols.Integer(widths[^1], value, value);
    }

    private static StarkTypeSymbol ProjectFrozenView(StarkTypeSymbol sourceType, StarkTypeSymbol projectedType)
    {
        return sourceType.AccessKind == StarkAccessKind.Frozen
            ? StarkTypeSymbols.FreezeReachableView(projectedType)
            : projectedType;
    }

    private static bool IsMemoryBackedType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.RawPointer => true,
            StarkTypeKind.FixedArray => true,
            StarkTypeKind.Slice => true,
            StarkTypeKind.Ascii => true,
            StarkTypeKind.Unicode => true,
            StarkTypeKind.Named => true,
            _ => type.BorrowKind != StarkBorrowKind.None || type.InitializationKind != StarkInitializationKind.None
        };
    }

    private static bool DeriveGuaranteedNonNull(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None || type.InitializationKind != StarkInitializationKind.None;
    }

    private static bool DeriveGuaranteedReadOnly(StarkTypeSymbol type)
    {
        if (type.InitializationKind != StarkInitializationKind.None)
        {
            return false;
        }

        return (type.Kind == StarkTypeKind.RawPointer && !type.IsMutablePointer)
            || type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
            || (type.BorrowKind != StarkBorrowKind.None && !type.IsMutableView)
            || type.AccessKind is StarkAccessKind.Shared or StarkAccessKind.Frozen;
    }

    private static bool DeriveGuaranteedNoAlias(StarkTypeSymbol type)
    {
        return type.InitializationKind != StarkInitializationKind.None;
    }

    private static bool CanAliasCalleeParameterMemory(StarkTypeSymbol parameterType)
    {
        return parameterType.BorrowKind != StarkBorrowKind.None
            || parameterType.InitializationKind != StarkInitializationKind.None
            || parameterType.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static ArgumentEffects DeriveFallbackArgumentEffects(
        TypedParameterSymbol parameter,
        StarkTypeSymbol calleeReturnType,
        bool hasBody)
    {
        var isAliasing = CanAliasCalleeParameterMemory(parameter.Type);
        var writes = parameter.Type.InitializationKind != StarkInitializationKind.None;
        var reads = isAliasing && !writes;
        var captureKind = ParameterCaptureKind.None;

        if (parameter.Type.BorrowKind == StarkBorrowKind.StoreBorrow)
        {
            captureKind = ParameterCaptureKind.Escape;
        }
        else if (parameter.Type.BorrowKind == StarkBorrowKind.RetBorrow
                 && calleeReturnType.BorrowKind != StarkBorrowKind.None)
        {
            captureKind = calleeReturnType.BorrowKind == StarkBorrowKind.StoreBorrow
                ? ParameterCaptureKind.Escape
                : ParameterCaptureKind.Return;
        }

        if (hasBody)
        {
            reads = false;
            writes = false;
            captureKind = ParameterCaptureKind.None;
        }

        return new ArgumentEffects(reads, writes, captureKind);
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return type.NamedType is not null && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
    }

    private bool TryIsMutableGlobal(string name)
    {
        return _typeModel.Globals.TryGetValue(name, out var global) && global.IsMutable;
    }

    private FunctionValidationBuilder GetOrCreateSummary(string functionName)
    {
        if (_summaries.TryGetValue(functionName, out var existing))
        {
            return existing;
        }

        var created = new FunctionValidationBuilder(functionName);
        _summaries[functionName] = created;
        return created;
    }

    private void BorrowError(FunctionValidationBuilder summary, string code, string message, ParserRuleContext context)
    {
        summary.BorrowingValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(context.Start));
    }

    private void BorrowError(FunctionValidationBuilder summary, string code, string message, IToken token)
    {
        summary.BorrowingValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(token));
    }

    private void EffectError(FunctionValidationBuilder summary, string code, string message, ParserRuleContext context)
    {
        summary.EffectsValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(context.Start));
    }

    private void EffectError(FunctionValidationBuilder summary, string code, string message, IToken token)
    {
        summary.EffectsValid = false;
        _context.Diagnostics.Error(code, message, "semantic-validate", Location(token));
    }

    private void ValidateLoopContract(
        string functionName,
        string loopBehavior,
        StarkParser.ExpressionContext? condition,
        StarkParser.StatementContext body,
        ParserRuleContext loopBehaviorContext,
        FunctionValidationBuilder summary)
    {
        switch (loopBehavior)
        {
            case "infinite":
                if (!IsStaticallyUnconditionalLoopCondition(condition))
                {
                    EffectError(
                        summary,
                        "STK4111",
                        $"Loop in function '{functionName}' is marked 'infinite' and must use a statically unconditional condition ('true' for 'while' or an omitted condition for 'for').",
                        loopBehaviorContext);
                }

                if (ContainsForbiddenInfiniteLoopExit(body))
                {
                    EffectError(
                        summary,
                        "STK4111",
                        $"Loop in function '{functionName}' is marked 'infinite' and may not contain a structural exit from the current loop or function.",
                        loopBehaviorContext);
                }

                break;

            case "willexit":
                if (IsStaticallyUnconditionalLoopCondition(condition)
                    && !ContainsStructuralLoopExit(body))
                {
                    EffectError(
                        summary,
                        "STK4112",
                        $"Loop in function '{functionName}' is marked 'willexit' with an unconditional condition and must contain at least one structural 'break' or 'return' in its body.",
                        loopBehaviorContext);
                }

                break;
        }
    }

    private static bool IsStaticallyUnconditionalLoopCondition(StarkParser.ExpressionContext? condition)
    {
        return condition is null || string.Equals(condition.GetText(), "true", StringComparison.Ordinal);
    }

    private static bool ContainsStructuralLoopExit(StarkParser.StatementContext statement, int nestedLoopDepth = 0)
    {
        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (nestedLoopDepth == 0 && statement.breakStatement() is not null)
        {
            return true;
        }

        if (statement.block() is { } block)
        {
            return block.statement().Any(child => ContainsStructuralLoopExit(child, nestedLoopDepth));
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return ifStatement.statement().Any(child => ContainsStructuralLoopExit(child, nestedLoopDepth));
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            return switchStatement.switchSection()
                .SelectMany(static section => section.statement())
                .Any(child => ContainsStructuralLoopExit(child, nestedLoopDepth));
        }

        if (statement.whileStatement() is { } nestedWhile)
        {
            return ContainsStructuralLoopExit(nestedWhile.statement(), nestedLoopDepth + 1);
        }

        if (statement.forStatement() is { } nestedFor)
        {
            return ContainsStructuralLoopExit(nestedFor.statement(), nestedLoopDepth + 1);
        }

        return false;
    }

    private static bool ContainsForbiddenInfiniteLoopExit(StarkParser.StatementContext statement, int nestedLoopDepth = 0)
    {
        if (statement.returnStatement() is not null)
        {
            return true;
        }

        if (nestedLoopDepth == 0 && statement.breakStatement() is not null)
        {
            return true;
        }

        if (statement.block() is { } block)
        {
            return block.statement().Any(child => ContainsForbiddenInfiniteLoopExit(child, nestedLoopDepth));
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            return ifStatement.statement().Any(child => ContainsForbiddenInfiniteLoopExit(child, nestedLoopDepth));
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            return switchStatement.switchSection()
                .SelectMany(static section => section.statement())
                .Any(child => ContainsForbiddenInfiniteLoopExit(child, nestedLoopDepth));
        }

        if (statement.whileStatement() is { } nestedWhile)
        {
            return ContainsForbiddenInfiniteLoopExit(nestedWhile.statement(), nestedLoopDepth + 1);
        }

        if (statement.forStatement() is { } nestedFor)
        {
            return ContainsForbiddenInfiniteLoopExit(nestedFor.statement(), nestedLoopDepth + 1);
        }

        return false;
    }

    private SourceLocation Location(IToken token) => new(_context.Input.FilePath, token.Line, token.Column + 1);

    private static LocalStorageClass ParseStorageClass(StarkParser.StorageClassContext context)
    {
        return context.GetText() switch
        {
            "stack" => LocalStorageClass.Stack,
            "heap" => LocalStorageClass.Heap,
            "register" => LocalStorageClass.Register,
            "static" => LocalStorageClass.Static,
            "arena" => LocalStorageClass.Arena,
            _ => LocalStorageClass.None
        };
    }

    private enum TypeUsage
    {
        Global,
        Parameter,
        Return,
        Local
    }

    private enum ExpressionObservation
    {
        Read,
        WriteTarget
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }

    private enum SymbolOrigin
    {
        Local,
        Parameter,
        Global
    }

    private enum LocalStorageClass
    {
        None,
        Stack,
        Heap,
        Register,
        Static,
        Arena
    }

    private sealed record VariableSymbol(
        string Name,
        StarkTypeSymbol Type,
        SymbolOrigin Origin,
        LocalStorageClass StorageClass,
        bool IsMutable,
        bool IsConstant);

    private sealed record ValidationValue(
        StarkTypeSymbol Type,
        bool IsAssignable = false,
        VariableSymbol? RootSymbol = null,
        TypedFunctionSignature? Function = null,
        string? OverloadSourceName = null,
        NamedTypeSymbol? NamedType = null,
        bool IsIndirectStorageAccess = false,
        string? NamespaceName = null,
        ValidationValue? Receiver = null,
        EnumConstructorBinding? EnumConstructor = null);

    private sealed record EnumConstructorBinding(
        string Name,
        EnumVariantSymbol Variant);

    private readonly record struct ObjectCreationKey(string Text, int Line, int Column);

    private sealed class ValidationScope
    {
        private readonly Dictionary<string, VariableSymbol> _locals = new(StringComparer.Ordinal);

        public ValidationScope(ValidationScope parent)
        {
            Parent = parent;
        }

        private ValidationScope()
        {
        }

        public ValidationScope? Parent { get; }

        public static ValidationScope CreateRoot() => new();

        public void Declare(VariableSymbol symbol)
        {
            _locals[symbol.Name] = symbol;
        }

        public bool TryLookup(string name, out VariableSymbol symbol)
        {
            if (_locals.TryGetValue(name, out symbol!))
            {
                return true;
            }

            if (Parent is not null)
            {
                return Parent.TryLookup(name, out symbol);
            }

            symbol = default!;
            return false;
        }
    }

    private sealed record ArgumentEffects(
        bool Reads,
        bool Writes,
        ParameterCaptureKind CaptureKind);

    private sealed record PendingCallArgument(
        int ArgumentIndex,
        string? CallerParameterName,
        string CalleeParameterName,
        bool AliasesCalleeMemory,
        VariableSymbol? RootSymbol,
        ArgumentEffects FallbackEffects);

    private sealed record PendingCall(
        string CalleeName,
        IReadOnlyList<PendingCallArgument> Arguments,
        IToken Location);

    private sealed class ParameterSummaryBuilder
    {
        public ParameterSummaryBuilder(
            TypedParameterSymbol parameter,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            Name = parameter.Name;
            Type = parameter.Type;
            IsMemoryBacked = IsMemoryBackedType(parameter.Type);
            GuaranteedNonNull = DeriveGuaranteedNonNull(parameter.Type);
            GuaranteedReadOnly = DeriveGuaranteedReadOnly(parameter.Type);
            GuaranteedWriteOnly = parameter.Type.InitializationKind != StarkInitializationKind.None;
            GuaranteedNoAlias = DeriveGuaranteedNoAlias(parameter.Type);
            var concreteLayout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(parameter.Type, namedTypes, enumLayouts);
            DereferenceableBytes = GuaranteedNonNull && concreteLayout is not null ? concreteLayout.SizeBytes : null;
            AlignmentBytes = GuaranteedNonNull && concreteLayout is not null ? concreteLayout.AlignmentBytes : null;
        }

        public string Name { get; }

        public StarkTypeSymbol Type { get; }

        public bool IsMemoryBacked { get; }

        public bool GuaranteedNonNull { get; }

        public bool GuaranteedReadOnly { get; }

        public bool GuaranteedWriteOnly { get; }

        public bool GuaranteedNoAlias { get; }

        public int? DereferenceableBytes { get; }

        public int? AlignmentBytes { get; }

        public bool Reads { get; set; }

        public bool Writes { get; set; }

        public ParameterCaptureKind CaptureKind { get; set; }

        public ArgumentEffects GetEffectiveEffects(bool hasBody)
        {
            var reads = Reads || (!hasBody && IsMemoryBacked && !GuaranteedWriteOnly);
            var writes = Writes
                || (!hasBody && GuaranteedWriteOnly)
                || (!hasBody && IsMemoryBacked && !GuaranteedReadOnly);
            var captureKind = CaptureKind;

            if (!hasBody && captureKind == ParameterCaptureKind.None)
            {
                captureKind = Type.BorrowKind switch
                {
                    StarkBorrowKind.StoreBorrow => ParameterCaptureKind.Escape,
                    StarkBorrowKind.RetBorrow => ParameterCaptureKind.Return,
                    _ => ParameterCaptureKind.None
                };
            }

            return new ArgumentEffects(reads, writes, captureKind);
        }

        public ParameterMemoryEffectSummary Build(bool hasBody)
        {
            var effects = GetEffectiveEffects(hasBody);
            return new ParameterMemoryEffectSummary(
                Name,
                Type.DisplayName,
                IsMemoryBacked,
                GuaranteedNonNull,
                GuaranteedReadOnly,
                GuaranteedWriteOnly,
                GuaranteedNoAlias,
                DereferenceableBytes,
                AlignmentBytes,
                effects.Reads,
                effects.Writes,
                effects.CaptureKind);
        }

        public bool Apply(ArgumentEffects effects)
        {
            var changed = false;
            if (effects.Reads && !Reads)
            {
                Reads = true;
                changed = true;
            }

            if (effects.Writes && !Writes)
            {
                Writes = true;
                changed = true;
            }

            if ((int)effects.CaptureKind > (int)CaptureKind)
            {
                CaptureKind = effects.CaptureKind;
                changed = true;
            }

            return changed;
        }
    }

    private sealed class FunctionValidationBuilder
    {
        public FunctionValidationBuilder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public StarkTypeSymbol ReturnType { get; private set; } = StarkTypeSymbols.Error;

        public StarkFunctionKind DeclaredKind { get; private set; } = StarkFunctionKind.Fn;

        public StarkFunctionKind EffectiveKind { get; private set; } = StarkFunctionKind.Fn;

        public bool HasBody { get; private set; }

        public bool DirectLawCompatible { get; private set; }

        public bool DirectFiniteCompatible { get; private set; }

        public bool EffectsValid { get; set; } = true;

        public bool BorrowingValid { get; set; } = true;

        public HashSet<string> CalledFunctions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ParameterSummaryBuilder> Parameters { get; } = new(StringComparer.Ordinal);

        public List<PendingCall> PendingCalls { get; } = [];

        public List<CallMemoryEffectSummary> ResolvedCalls { get; } = [];

        public void Configure(StarkTypeSymbol returnType, bool hasBody, StarkFunctionKind declaredKind)
        {
            ReturnType = returnType;
            DeclaredKind = declaredKind;
            HasBody = hasBody;
            DirectLawCompatible = hasBody;
            DirectFiniteCompatible = hasBody;
            EffectiveKind = hasBody ? StarkFunctionKind.Fn : declaredKind;
        }

        public void SetParameters(
            IReadOnlyList<TypedParameterSymbol> parameters,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            foreach (var parameter in parameters)
            {
                Parameters[parameter.Name] = new ParameterSummaryBuilder(parameter, namedTypes, enumLayouts);
            }
        }

        public void MarkParameterRead(string name)
        {
            if (Parameters.TryGetValue(name, out var parameter))
            {
                parameter.Reads = true;
            }
        }

        public void MarkParameterWrite(string name)
        {
            if (Parameters.TryGetValue(name, out var parameter))
            {
                parameter.Writes = true;
            }
        }

        public void MarkParameterCapture(string name, ParameterCaptureKind captureKind)
        {
            if (Parameters.TryGetValue(name, out var parameter) && (int)captureKind > (int)parameter.CaptureKind)
            {
                parameter.CaptureKind = captureKind;
            }
        }

        public bool ApplyArgumentEffects(string parameterName, ArgumentEffects effects)
        {
            return Parameters.TryGetValue(parameterName, out var parameter) && parameter.Apply(effects);
        }

        public bool TryGetParameter(string name, out ParameterSummaryBuilder parameter)
        {
            return Parameters.TryGetValue(name, out parameter!);
        }

        public void BuildResolvedCalls(Func<PendingCall, CallMemoryEffectSummary> projector)
        {
            ResolvedCalls.Clear();
            ResolvedCalls.AddRange(PendingCalls.Select(projector));
        }

        public void DisqualifyLaw()
        {
            DirectLawCompatible = false;
        }

        public void DisqualifyFinite()
        {
            DirectFiniteCompatible = false;
        }

        public void SetEffectiveKind(StarkFunctionKind kind)
        {
            EffectiveKind = kind;
        }

        public FunctionValidationSummary Build()
        {
            var parameterSummaries = Parameters.Values
                .OrderBy(static parameter => parameter.Name, StringComparer.Ordinal)
                .Select(parameter => parameter.Build(HasBody))
                .ToArray();
            var memoryEffects = new FunctionMemoryEffectSummary(
                parameterSummaries.Any(static parameter => parameter.Reads),
                parameterSummaries.Any(static parameter => parameter.Writes),
                parameterSummaries.Any(static parameter => parameter.CaptureKind != ParameterCaptureKind.None));

            return new FunctionValidationSummary(
                Name,
                DeclaredKind,
                EffectiveKind,
                EffectsValid,
                BorrowingValid,
                CalledFunctions.OrderBy(static item => item, StringComparer.Ordinal).ToArray(),
                memoryEffects,
                parameterSummaries,
                ResolvedCalls.ToArray());
        }
    }
}
