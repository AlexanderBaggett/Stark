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
    private readonly FunctionEffectModel _effectModel;
    private readonly TypeCheckModel _typeModel;
    private readonly StarkTypeResolver _typeResolver;
    private readonly Dictionary<string, TopLevelDeclarationModel> _syntaxDeclarations;
    private readonly Dictionary<string, StarkParser.FunctionDeclarationContext> _functionDeclarations;
    private readonly Dictionary<string, FunctionValidationBuilder> _summaries = new(StringComparer.Ordinal);

    public SemanticValidator(
        CompilerPassContext context,
        ParseResult parseResult,
        SyntaxModel syntaxModel,
        ModuleGraph moduleGraph,
        FunctionEffectModel effectModel,
        TypeCheckModel typeModel)
    {
        _context = context;
        _parseResult = parseResult;
        _syntaxModel = syntaxModel;
        _moduleGraph = moduleGraph;
        _effectModel = effectModel;
        _typeModel = typeModel;
        _typeResolver = new StarkTypeResolver(context, "semantic-validate", moduleGraph, typeModel.NamedTypes);
        _syntaxDeclarations = syntaxModel.Declarations.ToDictionary(static declaration => declaration.Name, StringComparer.Ordinal);
        _functionDeclarations = parseResult.Root.topLevelDeclaration()
            .Select(static declaration => declaration.functionDeclaration())
            .Where(static declaration => declaration is not null)!
            .ToDictionary(static declaration => declaration.Identifier().GetText(), StringComparer.Ordinal);
    }

    public SemanticValidationModel Validate()
    {
        ValidateGlobalDeclarations();

        foreach (var function in _functionDeclarations.Values)
        {
            ValidateFunction(function);
        }

        ValidateFiniteCallCycles();

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
                continue;
            }

            if (declaration.globalVariableDeclaration() is { } variableDeclaration)
            {
                var declaredType = _typeResolver.ResolveType(variableDeclaration.type_());
                ValidateTypeUsage(declaredType, TypeUsage.Global, variableDeclaration.type_(), isFfiBoundary: false);
            }
        }
    }

    private void ValidateFunction(StarkParser.FunctionDeclarationContext functionDeclaration)
    {
        var name = functionDeclaration.Identifier().GetText();
        if (!_typeModel.Functions.TryGetValue(name, out var signature)
            || !_effectModel.Functions.TryGetValue(name, out var effects)
            || !_syntaxDeclarations.TryGetValue(name, out var syntaxDeclaration)
            || syntaxDeclaration.Function is null)
        {
            return;
        }

        var summary = GetOrCreateSummary(name);
        ValidateFunctionSignature(functionDeclaration, syntaxDeclaration.Function, signature, effects, summary);

        if (functionDeclaration.functionBody().block() is not { } block)
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
        StarkParser.FunctionDeclarationContext functionDeclaration,
        FunctionDeclarationModel declaration,
        TypedFunctionSignature signature,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary)
    {
        ValidateTypeUsage(signature.ReturnType, TypeUsage.Return, functionDeclaration.returnType(), declaration.Modifiers.IsFfi);

        if (signature.ReturnType.BorrowKind == StarkBorrowKind.Borrow)
        {
            BorrowError(
                summary,
                "STK4000",
                $"Function '{signature.Name}' cannot return a plain 'borrow' value. Use 'retborrow' or 'storeborrow'.",
                functionDeclaration.returnType());
        }

        if (effects.IsPure && signature.ReturnType.InitializationKind != StarkInitializationKind.None)
        {
            EffectError(summary, "STK4100", $"Law '{signature.Name}' cannot return an 'out' or 'init' type.", functionDeclaration.returnType());
        }

        for (var index = 0; index < functionDeclaration.parameterList().parameter().Length; index++)
        {
            var parameterContext = functionDeclaration.parameterList().parameter(index);
            var parameter = signature.Parameters[index];

            ValidateTypeUsage(parameter.Type, TypeUsage.Parameter, parameterContext.type_(), declaration.Modifiers.IsFfi);

            if (effects.IsPure && parameter.Type.InitializationKind != StarkInitializationKind.None)
            {
                EffectError(
                    summary,
                    "STK4101",
                    $"Law '{signature.Name}' cannot declare 'out' or 'init' parameters.",
                    parameterContext.type_());
            }
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
                if (declarator.expression() is { } initializer)
                {
                    EvaluateExpression(initializer, scope, function, effects, summary, allowFunctionReference: false);
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

            if (effects.IsPure && storageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static)
            {
                EffectError(summary, "STK4102", $"Law '{function.Name}' cannot allocate or publish local '{storageClass.ToString().ToLowerInvariant()}' storage.", localVariable.storageClass());
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
            EvaluateExpression(ifStatement.expression(), scope, function, effects, summary, allowFunctionReference: false);
            CheckStatement(ifStatement.statement(0), new ValidationScope(scope), function, effects, summary);
            if (ifStatement.statement().Length > 1)
            {
                CheckStatement(ifStatement.statement(1), new ValidationScope(scope), function, effects, summary);
            }

            return;
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            var switchValue = EvaluateExpression(switchStatement.expression(), scope, function, effects, summary, allowFunctionReference: false);

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
                        EvaluateExpression(whenClause.expression(), sectionScope, function, effects, summary, allowFunctionReference: false);
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
            if (effects.WillReturn && whileStatement.loopBehavior().GetText() != "willexit")
            {
                EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", whileStatement.loopBehavior());
            }

            EvaluateExpression(whileStatement.expression(), scope, function, effects, summary, allowFunctionReference: false);
            CheckStatement(whileStatement.statement(), new ValidationScope(scope), function, effects, summary);
            return;
        }

        if (statement.forStatement() is { } forStatement)
        {
            if (effects.WillReturn && forStatement.loopBehavior().GetText() != "willexit")
            {
                EffectError(summary, "STK4103", $"Finite function '{function.Name}' may only use 'willexit' loops.", forStatement.loopBehavior());
            }

            var loopScope = new ValidationScope(scope);

            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForDeclaration)
            {
                var storageClass = ParseStorageClass(localForDeclaration.storageClass());
                var declaredType = _typeResolver.ResolveType(localForDeclaration.type_());
                ValidateTypeUsage(declaredType, TypeUsage.Local, localForDeclaration.type_(), isFfiBoundary: false);

                if (effects.IsPure && storageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static)
                {
                    EffectError(summary, "STK4102", $"Law '{function.Name}' cannot allocate or publish local '{storageClass.ToString().ToLowerInvariant()}' storage.", localForDeclaration.storageClass());
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
                    EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false);
                }
            }

            if (forStatement.forCondition() is { } condition)
            {
                EvaluateExpression(condition.expression(), loopScope, function, effects, summary, allowFunctionReference: false);
            }

            if (forStatement.forIterator() is { } iterator)
            {
                foreach (var expression in iterator.expressionList().expression())
                {
                    EvaluateExpression(expression, loopScope, function, effects, summary, allowFunctionReference: false);
                }
            }

            CheckStatement(forStatement.statement(), loopScope, function, effects, summary);
            return;
        }

        if (statement.returnStatement() is { } returnStatement)
        {
            if (returnStatement.expression() is not null)
            {
                EvaluateExpression(returnStatement.expression(), scope, function, effects, summary, allowFunctionReference: false);
            }

            return;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            EvaluateExpression(expressionStatement.expression(), scope, function, effects, summary, allowFunctionReference: false);
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
            EvaluateExpression(expression, scope, function, effects, summary, allowFunctionReference: false);
            return;
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            foreach (var memberInitializer in objectInitializer.memberInitializer())
            {
                EvaluateExpression(memberInitializer.expression(), scope, function, effects, summary, allowFunctionReference: false);
            }

            return;
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            foreach (var item in arrayInitializer.expression())
            {
                EvaluateExpression(item, scope, function, effects, summary, allowFunctionReference: false);
            }
        }
    }

    private ValidationValue EvaluateExpression(
        StarkParser.ExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        return EvaluateAssignmentExpression(expression.assignmentExpression(), scope, function, effects, summary, allowFunctionReference);
    }

    private ValidationValue EvaluateAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        if (expression.conditionalExpression() is { } conditionalExpression)
        {
            return EvaluateConditionalExpression(conditionalExpression, scope, function, effects, summary, allowFunctionReference);
        }

        var left = EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: true);
        var right = EvaluateAssignmentExpression(expression.assignmentExpression(), scope, function, effects, summary, allowFunctionReference: false);

        if (effects.IsPure && IsVisibleMemoryWrite(left))
        {
            EffectError(summary, "STK4104", $"Law '{function.Name}' cannot perform externally visible writes.", expression.unaryExpression());
        }

        return new ValidationValue(left.Type);
    }

    private ValidationValue EvaluateConditionalExpression(
        StarkParser.ConditionalExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        var condition = EvaluateLogicalOrExpression(expression.logicalOrExpression(), scope, function, effects, summary, allowFunctionReference);
        if (expression.expression().Length == 0)
        {
            return condition;
        }

        var whenTrue = EvaluateExpression(expression.expression(0), scope, function, effects, summary, allowFunctionReference: false);
        var whenFalse = EvaluateExpression(expression.expression(1), scope, function, effects, summary, allowFunctionReference: false);
        return new ValidationValue(FindCommonType(whenTrue.Type, whenFalse.Type));
    }

    private ValidationValue EvaluateLogicalOrExpression(
        StarkParser.LogicalOrExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        var operands = expression.logicalAndExpression()
            .Select(item => EvaluateLogicalAndExpression(item, scope, function, effects, summary, allowFunctionReference))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateLogicalAndExpression(
        StarkParser.LogicalAndExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        var operands = expression.bitwiseOrExpression()
            .Select(item => EvaluateBitwiseOrExpression(item, scope, function, effects, summary, allowFunctionReference))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateBitwiseOrExpression(
        StarkParser.BitwiseOrExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.bitwiseXorExpression(),
            item => EvaluateBitwiseXorExpression(item, scope, function, effects, summary, allowFunctionReference));
    }

    private ValidationValue EvaluateBitwiseXorExpression(
        StarkParser.BitwiseXorExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.bitwiseAndExpression(),
            item => EvaluateBitwiseAndExpression(item, scope, function, effects, summary, allowFunctionReference));
    }

    private ValidationValue EvaluateBitwiseAndExpression(
        StarkParser.BitwiseAndExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.equalityExpression(),
            item => EvaluateEqualityExpression(item, scope, function, effects, summary, allowFunctionReference));
    }

    private ValidationValue EvaluateEqualityExpression(
        StarkParser.EqualityExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        var operands = expression.relationalExpression()
            .Select(item => EvaluateRelationalExpression(item, scope, function, effects, summary, allowFunctionReference))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateRelationalExpression(
        StarkParser.RelationalExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        var operands = expression.shiftExpression()
            .Select(item => EvaluateShiftExpression(item, scope, function, effects, summary, allowFunctionReference))
            .ToArray();

        return operands.Length == 1 ? operands[0] : new ValidationValue(StarkTypeSymbols.Bool);
    }

    private ValidationValue EvaluateShiftExpression(
        StarkParser.ShiftExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.additiveExpression(),
            item => EvaluateAdditiveExpression(item, scope, function, effects, summary, allowFunctionReference));
    }

    private ValidationValue EvaluateAdditiveExpression(
        StarkParser.AdditiveExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.multiplicativeExpression(),
            item => EvaluateMultiplicativeExpression(item, scope, function, effects, summary, allowFunctionReference));
    }

    private ValidationValue EvaluateMultiplicativeExpression(
        StarkParser.MultiplicativeExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.unaryExpression(),
            item => EvaluateUnaryExpression(item, scope, function, effects, summary, allowFunctionReference));
    }

    private ValidationValue EvaluateUnaryExpression(
        StarkParser.UnaryExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        if (expression.powerExpression() is { } powerExpression)
        {
            return EvaluatePowerExpression(powerExpression, scope, function, effects, summary, allowFunctionReference);
        }

        return EvaluateUnaryExpression(expression.unaryExpression(), scope, function, effects, summary, allowFunctionReference: false);
    }

    private ValidationValue EvaluatePowerExpression(
        StarkParser.PowerExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        var left = EvaluatePostfixExpression(expression.postfixExpression(), scope, function, effects, summary, allowFunctionReference);
        if (expression.unaryExpression() is not { } rightExpression)
        {
            return left;
        }

        var right = EvaluateUnaryExpression(rightExpression, scope, function, effects, summary, allowFunctionReference: false);
        return new ValidationValue(FindCommonType(left.Type, right.Type));
    }

    private ValidationValue EvaluatePostfixExpression(
        StarkParser.PostfixExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        var requiresCallableTarget = expression.postfixPart().Any(static part => part.argumentList() is not null);
        var binding = EvaluatePrimaryExpression(expression.primaryExpression(), scope, function, effects, summary, allowFunctionReference || requiresCallableTarget);

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

        return binding;
    }

    private ValidationValue EvaluatePrimaryExpression(
        StarkParser.PrimaryExpressionContext expression,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference)
    {
        if (expression.literal() is { } literal)
        {
            return new ValidationValue(EvaluateLiteralType(literal));
        }

        if (expression.Identifier() is { } identifier)
        {
            return ResolveValue(identifier.GetText(), scope, function, effects, summary, allowFunctionReference, identifier.Symbol);
        }

        if (expression.qualifiedName() is { } qualifiedName)
        {
            return ResolveValue(qualifiedName.GetText(), scope, function, effects, summary, allowFunctionReference, qualifiedName.Start);
        }

        if (expression.objectCreationExpression() is { } objectCreationExpression)
        {
            return EvaluateObjectCreation(objectCreationExpression, scope, function, effects, summary);
        }

        return EvaluateExpression(expression.expression(), scope, function, effects, summary, allowFunctionReference: false);
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
                EvaluateExpression(argument.expression(), scope, function, effects, summary, allowFunctionReference: false);
            }
        }

        if (expression.objectInitializer() is { } objectInitializer)
        {
            foreach (var memberInitializer in objectInitializer.memberInitializer())
            {
                EvaluateExpression(memberInitializer.expression(), scope, function, effects, summary, allowFunctionReference: false);
            }
        }

        return new ValidationValue(createdType, NamedType: ResolveNamedTypeSymbol(createdType));
    }

    private ValidationValue ResolveValue(
        string name,
        ValidationScope scope,
        FunctionDeclarationModel function,
        FunctionEffectProfile effects,
        FunctionValidationBuilder summary,
        bool allowFunctionReference,
        IToken token)
    {
        if (scope.TryLookup(name, out var local))
        {
            return new ValidationValue(local.Type, IsAssignable: local.IsMutable && !local.IsConstant, RootSymbol: local, NamedType: ResolveNamedTypeSymbol(local.Type));
        }

        if (_typeModel.Globals.TryGetValue(name, out var globalType))
        {
            if (effects.IsPure)
            {
                EffectError(summary, "STK4105", $"Law '{function.Name}' cannot read global state.", token);
            }

            return new ValidationValue(
                globalType,
                IsAssignable: TryIsMutableGlobal(name),
                RootSymbol: new VariableSymbol(name, globalType, SymbolOrigin.Global, LocalStorageClass.Static, TryIsMutableGlobal(name), IsConstant: !TryIsMutableGlobal(name)),
                NamedType: ResolveNamedTypeSymbol(globalType));
        }

        if (_typeModel.Functions.TryGetValue(name, out var targetFunction))
        {
            if (!allowFunctionReference)
            {
                return new ValidationValue(StarkTypeSymbols.Error);
            }

            return new ValidationValue(targetFunction.ReturnType, Function: targetFunction);
        }

        if (_moduleGraph.HasModule(name))
        {
            return new ValidationValue(StarkTypeSymbols.Error, NamespaceName: name);
        }

        return new ValidationValue(StarkTypeSymbols.Error);
    }

    private void BindSwitchPattern(StarkParser.PatternContext pattern, StarkTypeSymbol switchType, ValidationScope scope)
    {
        if (pattern.VAR() is not null && pattern.Identifier() is { } capture)
        {
            scope.Declare(new VariableSymbol(capture.GetText(), switchType, SymbolOrigin.Local, LocalStorageClass.None, IsMutable: false, IsConstant: false));
        }
    }

    private ValidationValue InvokeCall(
        ValidationValue target,
        StarkParser.ArgumentListContext arguments,
        ValidationScope scope,
        FunctionDeclarationModel currentFunction,
        FunctionEffectProfile currentEffects,
        FunctionValidationBuilder summary)
    {
        if (target.Function is null)
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        if (_effectModel.Functions.TryGetValue(target.Function.Name, out var calleeEffects))
        {
            summary.CalledFunctions.Add(target.Function.Name);

            if (currentEffects.IsPure && !calleeEffects.IsPure)
            {
                EffectError(summary, "STK4106", $"Law '{currentFunction.Name}' may only call other laws.", arguments);
            }

            if (currentEffects.WillReturn && (!calleeEffects.WillReturn || !calleeEffects.MustProgress))
            {
                EffectError(summary, "STK4107", $"Finite function '{currentFunction.Name}' may only call finite functions.", arguments);
            }

            if (calleeEffects.IsFfi)
            {
                for (var index = 0; index < Math.Min(target.Function.Parameters.Count, arguments.argument().Length); index++)
                {
                    var argumentValue = EvaluateExpression(arguments.argument(index).expression(), scope, currentFunction, currentEffects, summary, allowFunctionReference: false);
                    if (argumentValue.Type.BorrowKind != StarkBorrowKind.None)
                    {
                        BorrowError(summary, "STK4001", $"Safe borrows may not cross an 'ffi' boundary. Argument {index + 1} to '{target.Function.Name}' must use a raw pointer form instead.", arguments.argument(index));
                    }
                }

                return new ValidationValue(target.Function.ReturnType, NamedType: ResolveNamedTypeSymbol(target.Function.ReturnType));
            }
        }

        for (var index = 0; index < Math.Min(target.Function.Parameters.Count, arguments.argument().Length); index++)
        {
            var parameter = target.Function.Parameters[index];
            var argumentValue = EvaluateExpression(arguments.argument(index).expression(), scope, currentFunction, currentEffects, summary, allowFunctionReference: false);
            ValidateBorrowArgumentFlow(argumentValue.Type, parameter.Type, target.Function.Name, index, summary, arguments.argument(index));
        }

        for (var index = target.Function.Parameters.Count; index < arguments.argument().Length; index++)
        {
            EvaluateExpression(arguments.argument(index).expression(), scope, currentFunction, currentEffects, summary, allowFunctionReference: false);
        }

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
        var currentType = target.Type;
        foreach (var indexExpression in indexes.expression())
        {
            EvaluateExpression(indexExpression, scope, function, effects, summary, allowFunctionReference: false);
            currentType = currentType.ElementType ?? StarkTypeSymbols.Error;
        }

        return new ValidationValue(
            currentType,
            IsAssignable: target.IsAssignable,
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

            if (_typeModel.Globals.TryGetValue(qualifiedName, out var globalType))
            {
                return new ValidationValue(
                    globalType,
                    IsAssignable: TryIsMutableGlobal(qualifiedName),
                    RootSymbol: new VariableSymbol(qualifiedName, globalType, SymbolOrigin.Global, LocalStorageClass.Static, TryIsMutableGlobal(qualifiedName), IsConstant: !TryIsMutableGlobal(qualifiedName)),
                    NamedType: ResolveNamedTypeSymbol(globalType));
            }

            if (_typeModel.Functions.TryGetValue(qualifiedName, out var function))
            {
                return new ValidationValue(function.ReturnType, Function: function);
            }

            return new ValidationValue(StarkTypeSymbols.Error);
        }

        var namedType = target.NamedType ?? ResolveNamedTypeSymbol(target.Type);
        if (namedType is null || !namedType.Fields.TryGetValue(memberName, out var field))
        {
            return new ValidationValue(StarkTypeSymbols.Error);
        }

        return new ValidationValue(
            field.Type,
            IsAssignable: target.IsAssignable,
            RootSymbol: target.RootSymbol,
            NamedType: ResolveNamedTypeSymbol(field.Type),
            IsIndirectStorageAccess: true);
    }

    private void ValidateFiniteCallCycles()
    {
        var finiteFunctions = _summaries
            .Where(pair => _effectModel.Functions.TryGetValue(pair.Key, out var effects) && effects.WillReturn && effects.MustProgress)
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        var visited = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in finiteFunctions)
        {
            Visit(function);
        }

        foreach (var function in cyclic)
        {
            if (_functionDeclarations.TryGetValue(function, out var declaration))
            {
                var summary = GetOrCreateSummary(function);
                EffectError(summary, "STK4108", $"Finite function '{function}' participates in a recursive call cycle and cannot be proven finite.", declaration.Identifier().Symbol);
            }
        }

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

            if (_summaries.TryGetValue(function, out var summary))
            {
                foreach (var callee in summary.CalledFunctions.Where(finiteFunctions.Contains))
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

        if (!target.IsIndirectStorageAccess)
        {
            return false;
        }

        return target.RootSymbol.Type.Kind == StarkTypeKind.RawPointer
            || target.RootSymbol.Type.BorrowKind != StarkBorrowKind.None
            || target.RootSymbol.Type.InitializationKind != StarkInitializationKind.None
            || target.RootSymbol.StorageClass is LocalStorageClass.Heap or LocalStorageClass.Arena or LocalStorageClass.Static;
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
            return IsAsciiLiteral(stringLiteral.GetText()) ? StarkTypeSymbols.Ascii : StarkTypeSymbols.Unicode;
        }

        if (literal.CharacterLiteral() is { } characterLiteral)
        {
            return IsAsciiLiteral(characterLiteral.GetText()) ? StarkTypeSymbols.Ascii : StarkTypeSymbols.Unicode;
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

        if (left.Kind == StarkTypeKind.Unicode && right.Kind == StarkTypeKind.Ascii)
        {
            return left;
        }

        if (left.Kind == StarkTypeKind.Ascii && right.Kind == StarkTypeKind.Unicode)
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

    private static bool IsAsciiLiteral(string text)
    {
        var content = text.Length >= 2 ? text[1..^1] : text;
        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (ch == '\\' && index + 1 < content.Length)
            {
                if (content[index + 1] == 'u' && index + 5 < content.Length)
                {
                    var hex = content.Substring(index + 2, 4);
                    var value = Convert.ToInt32(hex, 16);
                    if (value > 0x7F)
                    {
                        return false;
                    }

                    index += 5;
                    continue;
                }

                index++;
                continue;
            }

            if (ch > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return type.NamedType is not null && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            ? namedType
            : null;
    }

    private bool TryIsMutableGlobal(string name)
    {
        foreach (var declaration in _parseResult.Root.topLevelDeclaration())
        {
            if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
            {
                continue;
            }

            if (variableDeclaration.variableDeclarators().variableDeclarator().Any(declarator => declarator.Identifier().GetText() == name))
            {
                return variableDeclaration.MUT() is not null;
            }
        }

        return false;
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
        NamedTypeSymbol? NamedType = null,
        bool IsIndirectStorageAccess = false,
        string? NamespaceName = null);

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

    private sealed class FunctionValidationBuilder
    {
        public FunctionValidationBuilder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool EffectsValid { get; set; } = true;

        public bool BorrowingValid { get; set; } = true;

        public HashSet<string> CalledFunctions { get; } = new(StringComparer.Ordinal);

        public FunctionValidationSummary Build()
        {
            return new FunctionValidationSummary(Name, EffectsValid, BorrowingValid, CalledFunctions.OrderBy(static item => item, StringComparer.Ordinal).ToArray());
        }
    }
}
