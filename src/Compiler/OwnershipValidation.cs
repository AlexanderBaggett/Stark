using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class OwnershipValidator
{
    private readonly CompilerPassContext _context;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly ModuleGraph _moduleGraph;
    private readonly TypeCheckModel _typeModel;
    private readonly StarkTypeResolver _typeResolver;
    private readonly Dictionary<string, DeclaredFunctionSyntax> _functionDeclarations;
    private readonly Dictionary<string, TypedFunctionSignature> _signatures;
    private readonly Dictionary<string, bool> _mutableGlobals = new(StringComparer.Ordinal);
    private ISet<string>? _currentFunctionGenericParameters;

    public OwnershipValidator(
        CompilerPassContext context,
        ParseResult parseResult,
        SyntaxModel syntaxModel,
        ModuleGraph moduleGraph,
        TypeCheckModel typeModel)
    {
        _context = context;
        _parseResult = parseResult;
        _syntaxModel = syntaxModel;
        _moduleGraph = moduleGraph;
        _typeModel = typeModel;
        _typeResolver = new StarkTypeResolver(context, "ownership-validate", moduleGraph, typeModel.NamedTypes, typeModel.TypeAliases);
        _functionDeclarations = DeclaredFunctionSyntaxCollector.Collect(parseResult, syntaxModel)
            .ToDictionary(static declaration => declaration.Name, StringComparer.Ordinal);
        _signatures = new Dictionary<string, TypedFunctionSignature>(typeModel.Functions, StringComparer.Ordinal);

        SeedMutableGlobals();
    }

    public OwnershipValidationModel Validate()
    {
        var summaries = new Dictionary<string, FunctionOwnershipSummary>(StringComparer.Ordinal);

        foreach (var functionDeclaration in _functionDeclarations.Values)
        {
            var name = functionDeclaration.Name;
            if (!_signatures.TryGetValue(name, out var signature))
            {
                continue;
            }

            var summary = ValidateFunction(functionDeclaration, signature);
            summaries[name] = summary;
        }

        return new OwnershipValidationModel(_syntaxModel.ModuleName, summaries);
    }

    private FunctionOwnershipSummary ValidateFunction(
        DeclaredFunctionSyntax functionDeclaration,
        TypedFunctionSignature signature)
    {
        var summary = new FunctionOwnershipBuilder(signature.Name);
        var state = new FlowState(_typeModel.NamedTypes);
        var functionScope = state.EnterScope();
        var parameterDeclarations = functionDeclaration.ParameterList.parameter();
        var previousGenericParameters = _currentFunctionGenericParameters;
        _currentFunctionGenericParameters = signature.IsGeneric
            ? signature.GenericParams.ToHashSet(StringComparer.Ordinal)
            : null;

        try
        {
            for (var index = 0; index < signature.Parameters.Count; index++)
            {
                var parameter = signature.Parameters[index];
                var declarationLocation = index < parameterDeclarations.Length
                    ? Location(parameterDeclarations[index].Identifier().Symbol)
                    : null;
                state.Declare(new VariableInfo(
                    parameter.Name,
                    parameter.Type,
                    StorageClass.None,
                    VariableOrigin.Parameter,
                    IsMutable: false,
                    IsConstant: false,
                    BorrowLifetime: parameter.Type.BorrowKind == StarkBorrowKind.None
                        ? BorrowLifetime.None
                        : BorrowLifetime.External,
                    DeclarationLocation: declarationLocation),
                    isInitialized: true);
            }

            if (functionDeclaration.Body.block() is { } body)
            {
                CheckBlock(body, state, signature, summary, openScope: true);
            }

            state.ExitScope(functionScope, summary, ValidateScopeExitState, RecordImplicitDrops);
            return summary.Build();
        }
        finally
        {
            _currentFunctionGenericParameters = previousGenericParameters;
        }
    }

    private void CheckBlock(
        StarkParser.BlockContext block,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        bool openScope)
    {
        var scope = openScope ? state.EnterScope() : state.CurrentScope;

        foreach (var statement in block.statement())
        {
            CheckStatement(statement, state, signature, summary);
        }

        if (openScope)
        {
            state.ExitScope(scope!, summary, ValidateScopeExitState, RecordImplicitDrops);
        }
    }

    private void CheckStatement(
        StarkParser.StatementContext statement,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        if (statement.block() is { } block)
        {
            CheckBlock(block, state, signature, summary, openScope: true);
            return;
        }

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            CheckLocalDeclaration(
                localConstant.type_(),
                localConstant.constantDeclarators().constantDeclarator()
                    .Select(static declarator => (
                        Identifier: (ITerminalNode)declarator.Identifier(),
                        ConstantExpression: (StarkParser.ExpressionContext?)null,
                        Initializer: (StarkParser.VariableInitializerContext?)declarator.variableInitializer()))
                    .ToArray(),
                StorageClass.None,
                isMutable: false,
                isConstant: true,
                state,
                signature,
                summary);
            return;
        }

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            CheckLocalDeclaration(
                localVariable.type_(),
                localVariable.variableDeclarators().variableDeclarator()
                    .Select(static declarator => (
                        Identifier: (ITerminalNode)declarator.Identifier(),
                        ConstantExpression: (StarkParser.ExpressionContext?)null,
                        Initializer: (StarkParser.VariableInitializerContext?)declarator.variableInitializer()))
                    .ToArray(),
                ParseStorageClass(localVariable.storageClass()),
                isMutable: localVariable.MUT() is not null,
                isConstant: false,
                state,
                signature,
                summary);
            return;
        }

        if (statement.ifStatement() is { } ifStatement)
        {
            EvaluateExpression(ifStatement.expression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);

            var thenState = state.Clone();
            CheckStatement(ifStatement.statement(0), thenState, signature, summary);

            FlowState? elseState = null;
            if (ifStatement.statement().Length > 1)
            {
                elseState = state.Clone();
                CheckStatement(ifStatement.statement(1), elseState, signature, summary);
            }

            state.MergeBranches(thenState, elseState);
            return;
        }

        if (statement.switchStatement() is { } switchStatement)
        {
            var switchValue = EvaluateExpression(switchStatement.expression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);

            var sectionStates = new List<FlowState>();
            foreach (var section in switchStatement.switchSection())
            {
                var sectionState = state.Clone();
                var sectionScope = sectionState.EnterScope();
                foreach (var label in section.switchLabel())
                {
                    if (label.pattern() is { } pattern)
                    {
                        BindSwitchPattern(pattern, switchValue, sectionState, summary);
                    }

                    if (label.whenClause() is { } whenClause)
                    {
                        EvaluateExpression(whenClause.expression(), sectionState, signature, summary, ValueUse.Read, allowFunctionReference: false);
                    }
                }

                foreach (var nestedStatement in section.statement())
                {
                    CheckStatement(nestedStatement, sectionState, signature, summary);
                }

                sectionState.ExitScope(sectionScope, summary, ValidateScopeExitState, RecordImplicitDrops);
                sectionStates.Add(sectionState);
            }

            state.MergeBranches(sectionStates);
            return;
        }

        if (statement.whileStatement() is { } whileStatement)
        {
            EvaluateExpression(whileStatement.expression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);
            var loopState = state.Clone();
            CheckStatement(whileStatement.statement(), loopState, signature, summary);
            state.MergeLoop(loopState);
            return;
        }

        if (statement.forStatement() is { } forStatement)
        {
            var loopState = state.Clone();
            var loopScope = loopState.EnterScope();

            if (forStatement.forInitializer()?.localForVariableDeclaration() is { } localForVariableDeclaration)
            {
                CheckForVariableDeclaration(localForVariableDeclaration, loopState, signature, summary);
            }
            else if (forStatement.forInitializer()?.expressionList() is { } initializerExpressions)
            {
                foreach (var expression in initializerExpressions.expression())
                {
                    EvaluateExpression(expression, loopState, signature, summary, ValueUse.ConsumeTemporary, allowFunctionReference: false);
                }
            }

            if (forStatement.forCondition() is { } condition)
            {
                EvaluateExpression(condition.expression(), loopState, signature, summary, ValueUse.Read, allowFunctionReference: false);
            }

            CheckStatement(forStatement.statement(), loopState, signature, summary);

            if (forStatement.forIterator() is { } iterator)
            {
                foreach (var expression in iterator.expressionList().expression())
                {
                    EvaluateExpression(expression, loopState, signature, summary, ValueUse.ConsumeTemporary, allowFunctionReference: false);
                }
            }

            loopState.ExitScope(loopScope, summary, ValidateScopeExitState, RecordImplicitDrops);
            state.MergeLoop(loopState);
            return;
        }

        if (statement.returnStatement() is { } returnStatement)
        {
            if (returnStatement.expression() is { } expression)
            {
                var value = EvaluateExpression(
                    expression,
                    state,
                    signature,
                    summary,
                    ValueUse.ForReturn(signature.ReturnType),
                    allowFunctionReference: false);

                if (signature.ReturnType.BorrowKind != StarkBorrowKind.None)
                {
                    ValidateReturnedBorrowLifetime(value, summary, expression);
                }
            }

            return;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            EvaluateExpression(expressionStatement.expression(), state, signature, summary, ValueUse.ConsumeTemporary, allowFunctionReference: false);
        }
    }

    private void CheckForVariableDeclaration(
        StarkParser.LocalForVariableDeclarationContext declaration,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        CheckLocalDeclaration(
            declaration.type_(),
            declaration.variableDeclarators().variableDeclarator()
                .Select(static declarator => (
                    Identifier: (ITerminalNode)declarator.Identifier(),
                    ConstantExpression: (StarkParser.ExpressionContext?)null,
                    Initializer: (StarkParser.VariableInitializerContext?)declarator.variableInitializer()))
                .ToArray(),
            ParseStorageClass(declaration.storageClass()),
            isMutable: declaration.MUT() is not null,
            isConstant: false,
            state,
            signature,
            summary);
    }

    private void CheckLocalDeclaration(
        StarkParser.Type_Context typeContext,
        IReadOnlyList<(ITerminalNode Identifier, StarkParser.ExpressionContext? ConstantExpression, StarkParser.VariableInitializerContext? Initializer)> declarators,
        StorageClass storageClass,
        bool isMutable,
        bool isConstant,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        var declaredType = ResolveType(typeContext);

        foreach (var declarator in declarators)
        {
            BorrowLifetime borrowLifetime = declaredType.BorrowKind == StarkBorrowKind.None
                ? BorrowLifetime.None
                : BorrowLifetime.Unknown;

            if (declarator.ConstantExpression is { } constantExpression)
            {
                var value = EvaluateExpression(
                    WrapExpression(constantExpression),
                    state,
                    signature,
                    summary,
                    ValueUse.ForAssignment(declaredType),
                    allowFunctionReference: false);
                borrowLifetime = InferLifetimeForAssignment(declaredType, value, summary, constantExpression);
                state.Declare(new VariableInfo(
                    declarator.Identifier.GetText(),
                    declaredType,
                    storageClass,
                    VariableOrigin.Local,
                    isMutable,
                    isConstant,
                    borrowLifetime,
                    DeclarationLocation: Location(declarator.Identifier.Symbol)),
                    isInitialized: true,
                    aggregateState: value.AggregateState);
            }
            else if (declarator.Initializer is { } initializer)
            {
                var value = EvaluateVariableInitializer(initializer, state, signature, summary, declaredType);
                borrowLifetime = InferLifetimeForAssignment(declaredType, value, summary, initializer);
                state.Declare(new VariableInfo(
                    declarator.Identifier.GetText(),
                    declaredType,
                    storageClass,
                    VariableOrigin.Local,
                    isMutable,
                    isConstant,
                    borrowLifetime,
                    DeclarationLocation: Location(declarator.Identifier.Symbol)),
                    isInitialized: true,
                    aggregateState: value.AggregateState);
            }
            else
            {
                state.Declare(new VariableInfo(
                    declarator.Identifier.GetText(),
                    declaredType,
                    storageClass,
                    VariableOrigin.Local,
                    isMutable,
                    isConstant,
                    borrowLifetime,
                    DeclarationLocation: Location(declarator.Identifier.Symbol)),
                    isInitialized: false);
            }
        }
    }

    private ExpressionInfo EvaluateVariableInitializer(
        StarkParser.VariableInitializerContext initializer,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        StarkTypeSymbol declaredType)
    {
        if (initializer.expression() is { } expression)
        {
            return EvaluateExpression(expression, state, signature, summary, ValueUse.ForAssignment(declaredType), allowFunctionReference: false);
        }

        if (initializer.objectInitializer() is { } objectInitializer)
        {
            EvaluateObjectInitializerMembers(objectInitializer, declaredType, state, signature, summary);

            return new ExpressionInfo(declaredType);
        }

        if (initializer.arrayInitializer() is { } arrayInitializer)
        {
            EvaluateArrayInitializerItems(arrayInitializer, declaredType, state, signature, summary);
        }

        return new ExpressionInfo(declaredType);
    }

    private ExpressionInfo EvaluateExpression(
        StarkParser.ExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateAssignmentExpression(expression.assignmentExpression(), state, signature, summary, use, allowFunctionReference);
    }

    private ExpressionInfo EvaluateAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        if (expression.conditionalExpression() is { } conditionalExpression)
        {
            return EvaluateConditionalExpression(conditionalExpression, state, signature, summary, use, allowFunctionReference);
        }

        var isSimpleAssignment = expression.assignmentOperator().GetText() == "=";
        var left = EvaluateUnaryExpression(
            expression.unaryExpression(),
            state,
            signature,
            summary,
            isSimpleAssignment ? ValueUse.Place : ValueUse.Read,
            allowFunctionReference: true);
        var rightUse = isSimpleAssignment
            ? ValueUse.ForAssignment(left.Type)
            : ValueUse.Read;
        var right = EvaluateAssignmentExpression(expression.assignmentExpression(), state, signature, summary, rightUse, allowFunctionReference: false);

        if (isSimpleAssignment)
        {
            ApplyAssignment(left, right, state, summary, expression.unaryExpression());
            return left with { BorrowLifetime = right.BorrowLifetime, AggregateState = right.AggregateState };
        }

        if (IsMoveOnly(left.Type) && left.IsIndirectPlace)
        {
            OwnershipError(summary, "STK4203", $"Cannot move out of field or indexed place of type '{left.Type.DisplayName}'.", expression.unaryExpression());
        }

        return left;
    }

    private void ApplyAssignment(
        ExpressionInfo left,
        ExpressionInfo right,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ParserRuleContext context)
    {
        if (!left.IsPlace)
        {
            return;
        }

        if (left.Variable is { } variable)
        {
            if (variable.Origin == VariableOrigin.Global)
            {
                if (IsMoveOnly(left.Type))
                {
                    summary.ImplicitDrops.Add(variable.Name);
                }

                return;
            }

            if (left.ProjectionPath is null
                && state.TryGetState(variable.Id, out var variableState)
                && variableState.MayBeInitialized
                && IsAutomaticallyDropped(left.Type, variable.StorageClass))
            {
                RecordImplicitDrops(variable, variableState, summary);
            }

            var borrowLifetime = left.Type.BorrowKind == StarkBorrowKind.None
                ? BorrowLifetime.None
                : right.BorrowLifetime;
            if (left.Type.BorrowKind != StarkBorrowKind.None)
            {
                ValidateAssignedBorrowLifetime(left, right, state, summary, context);
            }

            if (left.ProjectionPath is { Length: > 0 } projectionPath)
            {
                state.MarkFieldInitialized(variable.Id, projectionPath[0]);
            }
            else
            {
                state.SetInitialized(variable.Id, borrowLifetime, right.AggregateState);
            }
        }
    }

    private void ValidateAssignedBorrowLifetime(
        ExpressionInfo left,
        ExpressionInfo right,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ParserRuleContext context)
    {
        if (left.Variable is null)
        {
            return;
        }

        var sourceLifetime = right.BorrowLifetime;
        if (!DoesLifetimeOutliveScope(sourceLifetime, left.Variable.DeclarationScopeId, state))
        {
            var reason = sourceLifetime.Kind switch
            {
                BorrowLifetimeKind.LocalScope => "because it is tied to local scope and would escape the destination scope.",
                BorrowLifetimeKind.Temporary => "because it is tied to a temporary value that ends before the destination scope.",
                BorrowLifetimeKind.Unknown => "because its source lifetime could not be proven for this destination scope.",
                _ => "because the source lifetime ends before the destination scope."
            };

            OwnershipError(
                summary,
                "STK4202",
                $"Lifetime error: cannot assign {DescribeBorrowSource(right)} to '{left.Variable.Name}' {reason}",
                context);
            ReportBorrowSourceNote(summary, sourceLifetime);
        }
    }

    private ExpressionInfo EvaluateConditionalExpression(
        StarkParser.ConditionalExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        var condition = EvaluateLogicalOrExpression(expression.logicalOrExpression(), state, signature, summary, ValueUse.Read, allowFunctionReference);
        if (expression.expression().Length == 0)
        {
            return ApplyUse(condition, state, summary, use, expression);
        }

        var thenState = state.Clone();
        var whenTrue = EvaluateExpression(expression.expression(0), thenState, signature, summary, use, allowFunctionReference: false);

        var elseState = state.Clone();
        var whenFalse = EvaluateExpression(expression.expression(1), elseState, signature, summary, use, allowFunctionReference: false);

        state.MergeBranches(thenState, elseState);

        var resultType = FindCommonType(whenTrue.Type, whenFalse.Type);
        var borrowLifetime = BorrowLifetime.Merge(whenTrue.BorrowLifetime, whenFalse.BorrowLifetime);
        var aggregateState = resultType.Kind == StarkTypeKind.Named
            ? AggregateFieldState.Merge(whenTrue.AggregateState, whenFalse.AggregateState)
            : null;
        return new ExpressionInfo(resultType, BorrowLifetime: borrowLifetime, AggregateState: aggregateState);
    }

    private ExpressionInfo EvaluateLogicalOrExpression(
        StarkParser.LogicalOrExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        var operands = expression.logicalAndExpression()
            .Select(item => EvaluateLogicalAndExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference))
            .ToArray();

        var result = operands.Length == 1
            ? operands[0]
            : new ExpressionInfo(StarkTypeSymbols.Bool);
        return ApplyUse(result, state, summary, use, expression);
    }

    private ExpressionInfo EvaluateLogicalAndExpression(
        StarkParser.LogicalAndExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        var operands = expression.bitwiseOrExpression()
            .Select(item => EvaluateBitwiseOrExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference))
            .ToArray();

        var result = operands.Length == 1
            ? operands[0]
            : new ExpressionInfo(StarkTypeSymbols.Bool);
        return ApplyUse(result, state, summary, use, expression);
    }

    private ExpressionInfo EvaluateBitwiseOrExpression(
        StarkParser.BitwiseOrExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.bitwiseXorExpression(),
            item => EvaluateBitwiseXorExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression);
    }

    private ExpressionInfo EvaluateBitwiseXorExpression(
        StarkParser.BitwiseXorExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.bitwiseAndExpression(),
            item => EvaluateBitwiseAndExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression);
    }

    private ExpressionInfo EvaluateBitwiseAndExpression(
        StarkParser.BitwiseAndExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.equalityExpression(),
            item => EvaluateEqualityExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression);
    }

    private ExpressionInfo EvaluateEqualityExpression(
        StarkParser.EqualityExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.relationalExpression(),
            item => EvaluateRelationalExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression,
            forceResultType: StarkTypeSymbols.Bool);
    }

    private ExpressionInfo EvaluateRelationalExpression(
        StarkParser.RelationalExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.shiftExpression(),
            item => EvaluateShiftExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression,
            forceResultType: StarkTypeSymbols.Bool);
    }

    private ExpressionInfo EvaluateShiftExpression(
        StarkParser.ShiftExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.additiveExpression(),
            item => EvaluateAdditiveExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression);
    }

    private ExpressionInfo EvaluateAdditiveExpression(
        StarkParser.AdditiveExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.multiplicativeExpression(),
            item => EvaluateMultiplicativeExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression);
    }

    private ExpressionInfo EvaluateMultiplicativeExpression(
        StarkParser.MultiplicativeExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        return EvaluateBinaryChain(
            expression.unaryExpression(),
            item => EvaluateUnaryExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
            state,
            summary,
            use,
            expression);
    }

    private ExpressionInfo EvaluateBinaryChain<TContext>(
        IEnumerable<TContext> operands,
        Func<TContext, ExpressionInfo> evaluateOperand,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        ParserRuleContext context,
        StarkTypeSymbol? forceResultType = null)
        where TContext : ParserRuleContext
    {
        ExpressionInfo? current = null;

        foreach (var operand in operands)
        {
            var value = evaluateOperand(operand);
            current = current is null
                ? value
                : new ExpressionInfo(forceResultType ?? FindCommonType(current.Type, value.Type));
        }

        return ApplyUse(current ?? new ExpressionInfo(StarkTypeSymbols.Error), state, summary, use, context);
    }

    private ExpressionInfo EvaluateUnaryExpression(
        StarkParser.UnaryExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        if (expression.powerExpression() is { } powerExpression)
        {
            return EvaluatePowerExpression(powerExpression, state, signature, summary, use, allowFunctionReference);
        }

        if (expression.conversionType() is { } conversionType)
        {
            var convertedOperand = EvaluateUnaryExpression(expression.unaryExpression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);
            var targetType = ResolveConversionType(conversionType);
            return ApplyUse(new ExpressionInfo(targetType), state, summary, use, expression);
        }

        var op = expression.unaryOperator()?.GetText() ?? expression.GetChild(0).GetText();
        if (op == "&")
        {
            var addressOperand = EvaluateUnaryExpression(expression.unaryExpression(), state, signature, summary, ValueUse.Place, allowFunctionReference: false);
            var pointerType = StarkTypeSymbols.RawPointer(addressOperand.Type, addressOperand.IsPlace);
            return ApplyUse(new ExpressionInfo(pointerType), state, summary, use, expression);
        }

        if (op == "*")
        {
            var dereferenceOperand = EvaluateUnaryExpression(expression.unaryExpression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);
            var pointeeType = dereferenceOperand.Type.Kind == StarkTypeKind.RawPointer && dereferenceOperand.Type.ElementType is not null
                ? dereferenceOperand.Type.ElementType
                : StarkTypeSymbols.Error;
            return ApplyUse(
                new ExpressionInfo(
                    pointeeType,
                    BorrowLifetime: BorrowLifetime.None,
                    IsPlace: true,
                    IsIndirectPlace: true),
                state,
                summary,
                use,
                expression);
        }

        var operand = EvaluateUnaryExpression(expression.unaryExpression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        return ApplyUse(operand with { Variable = null, IsPlace = false, IsIndirectPlace = false }, state, summary, use, expression);
    }

    private ExpressionInfo EvaluatePowerExpression(
        StarkParser.PowerExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        var postfixUse = expression.unaryExpression() is null ? use : ValueUse.Read;
        var left = EvaluatePostfixExpression(expression.postfixExpression(), state, signature, summary, postfixUse, allowFunctionReference);
        if (expression.unaryExpression() is not { } rightExpression)
        {
            return left;
        }

        var right = EvaluateUnaryExpression(rightExpression, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        return ApplyUse(new ExpressionInfo(FindCommonType(left.Type, right.Type)), state, summary, use, expression);
    }

    private ExpressionInfo EvaluatePostfixExpression(
        StarkParser.PostfixExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        var requiresCallableTarget = expression.postfixPart().Any(static part => part.argumentList() is not null);
        var primaryUse = expression.postfixPart().Length == 0
            ? use.Kind == ValueUseKind.Place ? ValueUse.Place : ValueUse.Read
            : ValueUse.ProjectBase;
        var binding = EvaluatePrimaryExpression(expression.primaryExpression(), state, signature, summary, primaryUse, allowFunctionReference || requiresCallableTarget);

        foreach (var postfixPart in expression.postfixPart())
        {
            if (postfixPart.argumentList() is { } argumentList)
            {
                binding = InvokeCall(binding, argumentList, state, summary, use);
                use = ValueUse.Read;
                continue;
            }

            if (postfixPart.GetChild(0).GetText() == "[")
            {
                if (postfixPart.expressionList() is { } expressionList)
                {
                    binding = ApplyIndex(binding, expressionList, state, signature, summary);
                }

                continue;
            }

            binding = ApplyMemberAccess(binding, postfixPart.Identifier().GetText(), summary, postfixPart);
        }

        return ApplyUse(binding, state, summary, use, expression);
    }

    private ExpressionInfo EvaluatePrimaryExpression(
        StarkParser.PrimaryExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        if (expression.literal() is { } literal)
        {
            return ApplyUse(new ExpressionInfo(EvaluateLiteralType(literal)), state, summary, use, literal);
        }

        if (expression.Identifier() is { } identifier)
        {
            return ResolveValue(identifier.GetText(), identifier.Symbol, state, summary, use, allowFunctionReference);
        }

        if (expression.enumConstructorExpression() is { } enumConstructorExpression)
        {
            var created = EvaluateEnumConstructorExpression(enumConstructorExpression, state, signature, summary);
            return ApplyUse(created, state, summary, use, enumConstructorExpression);
        }

        if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
        {
            return ResolveValue(genericEnumCaseReference.GetText(), genericEnumCaseReference.Start, state, summary, use, allowFunctionReference);
        }

        if (expression.qualifiedName() is { } qualifiedName)
        {
            return ResolveValue(qualifiedName.GetText(), qualifiedName.Start, state, summary, use, allowFunctionReference);
        }

        if (expression.objectCreationExpression() is { } objectCreationExpression)
        {
            var created = EvaluateObjectCreation(objectCreationExpression, state, signature, summary);
            return ApplyUse(created, state, summary, use, objectCreationExpression);
        }

        return EvaluateExpression(expression.expression(), state, signature, summary, use, allowFunctionReference: false);
    }

    private ExpressionInfo EvaluateObjectCreation(
        StarkParser.ObjectCreationExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        var type = ResolveType(expression.type_());

        if (expression.argumentList() is { } argumentList)
        {
            foreach (var argument in argumentList.argument())
            {
                EvaluateExpression(argument.expression(), state, signature, summary, ValueUse.ConsumeTemporary, allowFunctionReference: false);
            }
        }

        if (expression.objectInitializer() is { } objectInitializer)
        {
            EvaluateObjectInitializerMembers(objectInitializer, type, state, signature, summary);
        }

        return new ExpressionInfo(type, BorrowLifetime: BorrowLifetime.None, AggregateState: CreateInitializedAggregateState(type));
    }

    private ExpressionInfo EvaluateEnumConstructorExpression(
        StarkParser.EnumConstructorExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        var constructorName = expression.enumCaseTarget().GetText();
        if (!TryResolveEnumCaseReference(constructorName, out var enumType, out var enumTypeSymbol, out var variant)
            || !variant.UsesNamedFields)
        {
            return new ExpressionInfo(StarkTypeSymbols.Error);
        }

        foreach (var member in expression.enumConstructorInitializer().enumConstructorMember())
        {
            EvaluateExpression(
                member.expression(),
                state,
                signature,
                summary,
                ValueUse.ConsumeTemporary,
                allowFunctionReference: false);
        }

        return new ExpressionInfo(
            enumTypeSymbol,
            BorrowLifetime: BorrowLifetime.None,
            AggregateState: CreateEnumAggregateState(enumType, variant));
    }

    private void EvaluateObjectInitializerMembers(
        StarkParser.ObjectInitializerContext objectInitializer,
        StarkTypeSymbol targetType,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        NamedTypeSymbol? namedType = null;
        if (targetType.NamedType is not null)
        {
            _typeModel.NamedTypes.TryGetValue(targetType.NamedType, out namedType);
        }

        foreach (var memberInitializer in objectInitializer.memberInitializer())
        {
            var memberType = namedType is not null && namedType.Fields.TryGetValue(memberInitializer.Identifier().GetText(), out var field)
                ? field.Type
                : StarkTypeSymbols.Error;
            EvaluateVariableInitializer(memberInitializer.variableInitializer(), state, signature, summary, memberType);
        }
    }

    private void EvaluateArrayInitializerItems(
        StarkParser.ArrayInitializerContext arrayInitializer,
        StarkTypeSymbol targetType,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        var elementType = targetType.ElementType ?? StarkTypeSymbols.Error;
        foreach (var item in arrayInitializer.variableInitializer())
        {
            EvaluateVariableInitializer(item, state, signature, summary, elementType);
        }
    }

    private ExpressionInfo ResolveValue(
        string name,
        IToken token,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        if (state.TryLookup(name, out var variable))
        {
            if (!state.TryGetState(variable.Id, out var variableState))
            {
                OwnershipError(summary, "STK4200", $"Value '{name}' is not available in the current flow state.", token);
                return new ExpressionInfo(variable.Type);
            }

            if (!variableState.IsDefinitelyInitialized && use.Kind is not (ValueUseKind.Place or ValueUseKind.ProjectBase))
            {
                ReportUnavailableValue(variable, variableState, summary, token);
                return new ExpressionInfo(variable.Type);
            }

            var binding = new ExpressionInfo(
                variable.Type,
                Variable: variable,
                BorrowLifetime: variableState.BorrowLifetime,
                IsPlace: true,
                IsDirectVariable: true,
                AggregateState: variableState.AggregateState);

            return ApplyUse(binding, state, summary, use, token);
        }

        if (_typeModel.Globals.TryGetValue(name, out var globalType))
        {
            var isMutable = globalType.IsMutable;
            var binding = new ExpressionInfo(
                globalType.Type,
                Variable: new VariableInfo(
                    name,
                    globalType.Type,
                    StorageClass.Static,
                    VariableOrigin.Global,
                    IsMutable: isMutable,
                    IsConstant: !isMutable,
                    BorrowLifetime: globalType.Type.BorrowKind == StarkBorrowKind.None ? BorrowLifetime.None : BorrowLifetime.External,
                    DeclarationLocation: null),
                BorrowLifetime: globalType.Type.BorrowKind == StarkBorrowKind.None ? BorrowLifetime.None : BorrowLifetime.External,
                IsPlace: true,
                IsDirectVariable: true);

            if (use.Kind == ValueUseKind.Consume && IsMoveOnly(globalType.Type))
            {
                OwnershipError(summary, "STK4204", $"Cannot move out of global or static storage '{name}'.", token);
            }

            return binding;
        }

        if (TryGetFunctionOverloads(name, out var functions))
        {
            if (!allowFunctionReference)
            {
                return new ExpressionInfo(StarkTypeSymbols.Error);
            }

            return functions.Count == 1 && !functions[0].IsGeneric
                ? new ExpressionInfo(functions[0].ReturnType, Function: functions[0])
                : new ExpressionInfo(StarkTypeSymbols.Error, OverloadSourceName: name);
        }

        if (TryResolveNamedTypeBySourceName(name, out var namedType))
        {
            if (namedType.Kind == DeclarationKind.Doctrine && allowFunctionReference)
            {
                return new ExpressionInfo(StarkTypeSymbols.Named(namedType.Name));
            }

            if (namedType.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
            {
                return new ExpressionInfo(StarkTypeSymbols.Error);
            }
        }

        if (TryResolveNamedTypeBySourceName(name, out namedType) && namedType.Kind == DeclarationKind.Enum)
        {
            return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: name);
        }

            if (TryResolveEnumCaseReference(name, out var enumType, out var enumTypeSymbol, out var variant))
            {
                if (variant.IsUnit)
                {
                    return new ExpressionInfo(
                        enumTypeSymbol,
                        BorrowLifetime: BorrowLifetime.None,
                        AggregateState: CreateEnumAggregateState(enumType, variant));
                }

            if (!variant.UsesNamedFields && allowFunctionReference)
            {
                return new ExpressionInfo(
                    enumTypeSymbol,
                    BorrowLifetime: BorrowLifetime.None,
                    EnumConstructor: new EnumConstructorBinding(name, variant));
            }
        }

        if (_moduleGraph.CanAccessModule(_syntaxModel.ModuleName, name))
        {
            return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: name);
        }

        if (_moduleGraph.CanAccessModuleNamespace(_syntaxModel.ModuleName, name))
        {
            return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: name);
        }

        return new ExpressionInfo(StarkTypeSymbols.Error);
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

    private void BindSwitchPattern(StarkParser.PatternContext pattern, ExpressionInfo switchValue, FlowState state, FunctionOwnershipBuilder summary)
    {
        if (pattern.VAR() is not null && pattern.Identifier() is { } capture)
        {
            if (!IsEnumSwitchType(switchValue.Type))
            {
                state.Declare(new VariableInfo(
                    capture.GetText(),
                    switchValue.Type,
                    StorageClass.None,
                    VariableOrigin.Local,
                    IsMutable: false,
                    IsConstant: false,
                    switchValue.BorrowLifetime,
                    DeclarationLocation: Location(capture.Symbol)),
                    isInitialized: true);
            }

            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, switchValue, state, summary);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, switchValue, state, summary);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, switchValue, state, summary))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, switchValue.Type, state, summary);
        }
    }

    private void BindAggregateSwitchPattern(
        StarkParser.AggregatePatternContext aggregatePattern,
        StarkTypeSymbol switchType,
        FlowState state,
        FunctionOwnershipBuilder summary)
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
        if (suffix is null)
        {
            return;
        }

        if (suffix.Identifier() is { } capture)
        {
            state.Declare(new VariableInfo(
                capture.GetText(),
                switchType,
                StorageClass.None,
                VariableOrigin.Local,
                IsMutable: false,
                IsConstant: false,
                BorrowLifetime.None,
                DeclarationLocation: Location(capture.Symbol)),
                isInitialized: true);
            return;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != namedType.OrderedFields.Count)
        {
            return;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindAggregateFieldPattern(fieldPatterns[index], namedType.OrderedFields[index], state, summary);
        }
    }

    private bool TryBindEnumAggregateSwitchPattern(
        StarkParser.AggregatePatternContext aggregatePattern,
        ExpressionInfo switchValue,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.simpleType().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchValue,
            state,
            summary,
            out var matched)
            && matched;
    }

    private bool TryBindEnumAggregateSwitchPattern(
        StarkParser.GenericEnumAggregatePatternContext aggregatePattern,
        ExpressionInfo switchValue,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        return TryBindResolvedEnumAggregateSwitchPattern(
            aggregatePattern.genericEnumCaseReference().GetText(),
            aggregatePattern.aggregatePatternSuffix(),
            switchValue,
            state,
            summary,
            out var matched)
            && matched;
    }

    private bool TryBindResolvedEnumAggregateSwitchPattern(
        string caseName,
        StarkParser.AggregatePatternSuffixContext? suffix,
        ExpressionInfo switchValue,
        FlowState state,
        FunctionOwnershipBuilder summary,
        out bool matched)
    {
        matched = false;
        if (!TryResolveEnumCaseReference(caseName, out var enumType, out _, out var variant))
        {
            return false;
        }

        matched = true;
        if (switchValue.Type.Kind != StarkTypeKind.Named
            || switchValue.Type.NamedType is null
            || !string.Equals(switchValue.Type.NamedType, enumType.Name, StringComparison.Ordinal)
            || variant.UsesNamedFields)
        {
            return true;
        }

        NarrowSwitchValueToEnumCase(switchValue, state, enumType, variant);

        if (variant.IsUnit || suffix is null)
        {
            return true;
        }

        if (suffix.Identifier() is { } capture)
        {
            state.Declare(new VariableInfo(
                capture.GetText(),
                switchValue.Type,
                StorageClass.None,
                VariableOrigin.Local,
                IsMutable: false,
                IsConstant: false,
                switchValue.BorrowLifetime,
                DeclarationLocation: Location(capture.Symbol)),
                isInitialized: true);
            return true;
        }

        var fieldPatterns = suffix.pattern();
        if (fieldPatterns.Length != variant.Fields.Count)
        {
            return true;
        }

        for (var index = 0; index < fieldPatterns.Length; index++)
        {
            BindEnumVariantFieldPattern(fieldPatterns[index], variant.Fields[index], switchValue, state, summary);
        }

        return true;
    }

    private void BindEnumNamedFieldPattern(
        StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
        ExpressionInfo switchValue,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        if (!TryResolveEnumCaseReference(enumNamedFieldPattern.enumCaseTarget().GetText(), out var enumType, out _, out var variant)
            || switchValue.Type.Kind != StarkTypeKind.Named
            || switchValue.Type.NamedType is null
            || !string.Equals(switchValue.Type.NamedType, enumType.Name, StringComparison.Ordinal)
            || !variant.UsesNamedFields)
        {
            return;
        }

        NarrowSwitchValueToEnumCase(switchValue, state, enumType, variant);

        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember())
        {
            var memberName = member.Identifier().GetText();
            var field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.Ordinal));
            if (field is null || !seenMembers.Add(memberName))
            {
                continue;
            }

            BindEnumVariantFieldPattern(member.pattern(), field, switchValue, state, summary);
        }
    }

    private void BindEnumVariantFieldPattern(
        StarkParser.PatternContext pattern,
        EnumVariantFieldSymbol field,
        ExpressionInfo switchValue,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture)
        {
            if (IsMoveOnly(field.Type))
            {
                ConsumeSwitchValueForOwnedEnumCapture(switchValue, state, summary, capture.Symbol);
            }

            state.Declare(new VariableInfo(
                capture.GetText(),
                field.Type,
                StorageClass.None,
                VariableOrigin.Local,
                IsMutable: false,
                IsConstant: false,
                BorrowLifetime.None,
                DeclarationLocation: Location(capture.Symbol)),
                isInitialized: true);
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, switchValue with { Type = field.Type }, state, summary);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, switchValue with { Type = field.Type }, state, summary);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            if (TryBindEnumAggregateSwitchPattern(aggregatePattern, switchValue with { Type = field.Type }, state, summary))
            {
                return;
            }

            BindAggregateSwitchPattern(aggregatePattern, field.Type, state, summary);
        }
    }

    private void BindAggregateFieldPattern(StarkParser.PatternContext pattern, FieldSymbol field, FlowState state, FunctionOwnershipBuilder summary)
    {
        if (pattern.VAR() is not null
            && pattern.Identifier() is { } capture
            && SupportsAggregateFieldSubpattern(field.Type))
        {
            state.Declare(new VariableInfo(
                capture.GetText(),
                field.Type,
                StorageClass.None,
                VariableOrigin.Local,
                IsMutable: false,
                IsConstant: false,
                BorrowLifetime.None,
                DeclarationLocation: Location(capture.Symbol)),
                isInitialized: true);
            return;
        }

        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            BindEnumNamedFieldPattern(enumNamedFieldPattern, new ExpressionInfo(field.Type), state, summary);
            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(genericEnumAggregatePattern, new ExpressionInfo(field.Type), state, summary))
        {
            return;
        }

        if (pattern.aggregatePattern() is { } enumAggregatePattern
            && TryBindEnumAggregateSwitchPattern(enumAggregatePattern, new ExpressionInfo(field.Type), state, summary))
        {
            return;
        }
    }

    private StarkTypeSymbol ResolvePatternSimpleType(StarkParser.SimpleTypeContext simpleType)
    {
        return _typeResolver.ResolveSimpleType(simpleType, _currentFunctionGenericParameters, _syntaxModel.ModuleName);
    }

    private bool IsEnumSwitchType(StarkTypeSymbol switchType)
    {
        return switchType.Kind == StarkTypeKind.Named
            && switchType.NamedType is not null
            && _typeModel.NamedTypes.TryGetValue(switchType.NamedType, out var namedType)
            && namedType.Kind == DeclarationKind.Enum;
    }

    private StarkTypeSymbol ResolveType(StarkParser.Type_Context type)
    {
        return _typeResolver.ResolveType(type, _currentFunctionGenericParameters);
    }

    private StarkTypeSymbol ResolveConversionType(StarkParser.ConversionTypeContext type)
    {
        return _typeResolver.ResolveConversionType(type, _currentFunctionGenericParameters);
    }

    private static bool SupportsAggregateFieldSubpattern(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer;
    }

    private ExpressionInfo InvokeCall(
        ExpressionInfo target,
        StarkParser.ArgumentListContext arguments,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ValueUse use)
    {
        if (target.EnumConstructor is not null)
        {
            foreach (var argument in arguments.argument())
            {
                EvaluateExpression(
                    argument.expression(),
                    state,
                    _signatures[summary.Name],
                    summary,
                    ValueUse.ConsumeTemporary,
                    allowFunctionReference: false);
            }

            var aggregateState = target.Type.NamedType is not null
                && _typeModel.NamedTypes.TryGetValue(target.Type.NamedType, out var enumType)
                && enumType.Kind == DeclarationKind.Enum
                ? CreateEnumAggregateState(enumType, target.EnumConstructor.Variant)
                : null;

            return ApplyUse(
                new ExpressionInfo(target.Type, BorrowLifetime: BorrowLifetime.None, AggregateState: aggregateState),
                state,
                summary,
                use,
                arguments);
        }

        var argumentValues = arguments.argument()
            .Select(argument => EvaluateExpression(
                argument.expression(),
                state,
                _signatures[summary.Name],
                summary,
                ValueUse.ConsumeTemporary,
                allowFunctionReference: false))
            .ToArray();

        if (target.OverloadSourceName is { } overloadSourceName)
        {
            if (!TryGetFunctionOverloads(overloadSourceName, out var overloads))
            {
                return new ExpressionInfo(StarkTypeSymbols.Error);
            }

            var resolution = FunctionOverloadFacts.Resolve(
                overloads,
                target.Receiver?.Type,
                argumentValues.Select(static argument => argument.Type).ToArray(),
                TypeCompatibilityFacts.CanAssign);
            if (!resolution.Succeeded)
            {
                return new ExpressionInfo(StarkTypeSymbols.Error);
            }

            target = target with
            {
                Function = resolution.Match,
                OverloadSourceName = null,
                Type = resolution.Match!.ReturnType
            };
        }

        if (target.Function is null)
        {
            return new ExpressionInfo(StarkTypeSymbols.Error);
        }

        var borrowArguments = new List<BorrowLifetime>();
        var receiverOffset = target.Receiver is null ? 0 : 1;
        var explicitParameterCount = Math.Max(0, target.Function.Parameters.Count - receiverOffset);

        if (target.Receiver is not null && target.Function.Parameters.Count != 0)
        {
            var receiverParameterType = target.Function.Parameters[0].Type;
            var receiverValue = ApplyUse(target.Receiver, state, summary, ValueUse.ForCallArgument(receiverParameterType), arguments);
            if (receiverParameterType.BorrowKind != StarkBorrowKind.None)
            {
                borrowArguments.Add(receiverValue.BorrowLifetime);
            }
        }

        for (var index = 0; index < argumentValues.Length; index++)
        {
            var parameterType = index < explicitParameterCount
                ? target.Function.Parameters[index + receiverOffset].Type
                : StarkTypeSymbols.Error;
            var argumentValue = argumentValues[index];

            if (parameterType.BorrowKind != StarkBorrowKind.None)
            {
                borrowArguments.Add(argumentValue.BorrowLifetime);
            }
        }

        var borrowLifetime = target.Function.ReturnType.BorrowKind == StarkBorrowKind.None
            ? BorrowLifetime.None
            : BorrowLifetime.InferFromCall(
                borrowArguments,
                Location(arguments.Start),
                $"borrow source for call '{target.Function.DisplaySourceName}'");

        return ApplyUse(
            new ExpressionInfo(target.Function.ReturnType, BorrowLifetime: borrowLifetime),
            state,
            summary,
            use,
            arguments);
    }

    private ExpressionInfo ApplyIndex(
        ExpressionInfo target,
        StarkParser.ExpressionListContext expressionList,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        if (target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
        {
            foreach (var index in expressionList.expression())
            {
                EvaluateExpression(index, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
            }

            var borrowLifetime = target.BorrowLifetime.Kind != BorrowLifetimeKind.None
                ? target.BorrowLifetime
                : InferBorrowLifetimeFromValue(target, expressionList.Start);

            return new ExpressionInfo(target.Type, BorrowLifetime: borrowLifetime);
        }

        foreach (var index in expressionList.expression())
        {
            EvaluateExpression(index, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        }

        var elementType = target.Type.ElementType ?? StarkTypeSymbols.Error;
        return new ExpressionInfo(
            elementType,
            Variable: target.Variable,
            BorrowLifetime: target.BorrowLifetime,
            IsPlace: target.IsPlace,
            IsIndirectPlace: true,
            ProjectionPath: target.ProjectionPath,
            HasIndexProjection: true);
    }

    private ExpressionInfo ApplyMemberAccess(
        ExpressionInfo target,
        string memberName,
        FunctionOwnershipBuilder summary,
        ParserRuleContext context)
    {
        if (target.NamespaceName is not null)
        {
            var qualifiedName = $"{target.NamespaceName}.{memberName}";
            if (_moduleGraph.CanAccessModule(_syntaxModel.ModuleName, qualifiedName))
            {
                return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
            }

            if (_moduleGraph.CanAccessModuleNamespace(_syntaxModel.ModuleName, qualifiedName))
            {
                return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
            }

            if (_typeModel.Globals.TryGetValue(qualifiedName, out var globalType))
            {
                var isMutable = globalType.IsMutable;
                return new ExpressionInfo(
                    globalType.Type,
                    Variable: new VariableInfo(
                        qualifiedName,
                        globalType.Type,
                        StorageClass.Static,
                        VariableOrigin.Global,
                        IsMutable: isMutable,
                        IsConstant: !isMutable,
                        BorrowLifetime: globalType.Type.BorrowKind == StarkBorrowKind.None ? BorrowLifetime.None : BorrowLifetime.External,
                        DeclarationLocation: null),
                    BorrowLifetime: globalType.Type.BorrowKind == StarkBorrowKind.None ? BorrowLifetime.None : BorrowLifetime.External,
                    IsPlace: true,
                    IsDirectVariable: true);
            }

            if (TryGetFunctionOverloads(qualifiedName, out var namespaceFunctions))
            {
                return namespaceFunctions.Count == 1 && !namespaceFunctions[0].IsGeneric
                    ? new ExpressionInfo(namespaceFunctions[0].ReturnType, Function: namespaceFunctions[0])
                    : new ExpressionInfo(StarkTypeSymbols.Error, OverloadSourceName: qualifiedName);
            }

            if (TryResolveNamedTypeBySourceName(qualifiedName, out var qualifiedType))
            {
                if (qualifiedType.Kind == DeclarationKind.Enum)
                {
                    return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
                }

                if (qualifiedType.Kind == DeclarationKind.Doctrine)
                {
                    return new ExpressionInfo(StarkTypeSymbols.Named(qualifiedType.Name));
                }

                if (qualifiedType.Kind == DeclarationKind.Trait)
                {
                    return new ExpressionInfo(StarkTypeSymbols.Error);
                }
            }

            if (TryResolveEnumCaseReference(qualifiedName, out var enumType, out var enumTypeSymbol, out var variant))
            {
                if (variant.IsUnit)
                {
                    return new ExpressionInfo(
                        enumTypeSymbol,
                        BorrowLifetime: BorrowLifetime.None,
                        AggregateState: CreateEnumAggregateState(enumType, variant));
                }

                return new ExpressionInfo(
                    enumTypeSymbol,
                    BorrowLifetime: BorrowLifetime.None,
                    EnumConstructor: new EnumConstructorBinding(qualifiedName, variant));
            }

            return new ExpressionInfo(StarkTypeSymbols.Error);
        }

        var namedType = target.Type.NamedType is not null && _typeModel.NamedTypes.TryGetValue(target.Type.NamedType, out var resolved)
            ? resolved
            : null;

        if (namedType is null)
        {
            return new ExpressionInfo(StarkTypeSymbols.Error);
        }

        if (namedType.Fields.TryGetValue(memberName, out var field))
        {
            return new ExpressionInfo(
                field.Type,
                Variable: target.Variable,
                BorrowLifetime: target.BorrowLifetime,
                IsPlace: target.IsPlace,
                IsIndirectPlace: true,
            ProjectionPath: target.Variable is null
                ? target.ProjectionPath
                : AppendProjection(target.ProjectionPath, memberName),
            HasIndexProjection: target.HasIndexProjection);
        }

        var methodSourceName = $"{namedType.Name}.{memberName}";
        if (namedType.Kind == DeclarationKind.Doctrine
            && TryGetFunctionOverloads(methodSourceName, out var doctrineMethods))
        {
            return doctrineMethods.Count == 1 && !doctrineMethods[0].IsGeneric
                ? new ExpressionInfo(
                    doctrineMethods[0].ReturnType,
                    Function: doctrineMethods[0],
                    BorrowLifetime: BorrowLifetime.None)
                : new ExpressionInfo(StarkTypeSymbols.Error, OverloadSourceName: methodSourceName);
        }

        if (TryGetFunctionOverloads(methodSourceName, out var methods))
        {
            if (methods.Count == 1 && !methods[0].IsGeneric && methods[0].Parameters.Count != 0)
            {
                return new ExpressionInfo(
                    methods[0].ReturnType,
                    Function: methods[0],
                    BorrowLifetime: BorrowLifetime.None,
                    Receiver: target);
            }

            return new ExpressionInfo(StarkTypeSymbols.Error, OverloadSourceName: methodSourceName, Receiver: target);
        }

        return new ExpressionInfo(
            StarkTypeSymbols.Error);
    }

    private ExpressionInfo ApplyUse(
        ExpressionInfo value,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        ParserRuleContext context)
    {
        return ApplyUse(value, state, summary, use, context.Start);
    }

    private ExpressionInfo ApplyUse(
        ExpressionInfo value,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        IToken token)
    {
        if (use.CaptureBorrowLifetime && value.BorrowLifetime.Kind == BorrowLifetimeKind.None)
        {
            value = value with { BorrowLifetime = InferBorrowLifetimeFromValue(value, token) };
        }

        if (use.Kind == ValueUseKind.ProjectBase)
        {
            return value;
        }

        if (!TryEnsureValueAvailable(value, state, summary, use, token))
        {
            return value;
        }

        if (use.Kind != ValueUseKind.Consume || !IsMoveOnly(value.Type))
        {
            return value;
        }

        if (value.IsIndirectPlace)
        {
            if (value.Variable is { } projectedVariable
                && value.ProjectionPath is { Length: 1 } projectionPath
                && !value.HasIndexProjection)
            {
                state.MarkFieldMoved(projectedVariable.Id, projectionPath[0], value.BorrowLifetime, Location(token));
                summary.Moves.Add($"{projectedVariable.Name}.{projectionPath[0]}");
                return value;
            }

            OwnershipError(summary, "STK4203", $"Cannot move out of field or indexed place of type '{value.Type.DisplayName}'.", token);
            return value;
        }

        if (value.Variable is null)
        {
            return value;
        }

        if (value.Variable.Origin == VariableOrigin.Global)
        {
            OwnershipError(summary, "STK4204", $"Cannot move out of global or static storage '{value.Variable.Name}'.", token);
            return value;
        }

        if (!state.TryGetState(value.Variable.Id, out var stateValue))
        {
            OwnershipError(summary, "STK4200", $"Value '{value.Variable.Name}' is not available in the current flow state.", token);
            return value;
        }

        state.SetMoved(value.Variable.Id, value.BorrowLifetime, Location(token));
        summary.Moves.Add(value.Variable.Name);
        return value;
    }

    private BorrowLifetime InferLifetimeForAssignment(
        StarkTypeSymbol declaredType,
        ExpressionInfo value,
        FunctionOwnershipBuilder summary,
        ParserRuleContext context)
    {
        if (declaredType.BorrowKind == StarkBorrowKind.None)
        {
            return BorrowLifetime.None;
        }

        if (value.BorrowLifetime.Kind != BorrowLifetimeKind.None)
        {
            return value.BorrowLifetime;
        }

        return InferBorrowLifetimeFromValue(value, context.Start);
    }

    private void ValidateReturnedBorrowLifetime(ExpressionInfo value, FunctionOwnershipBuilder summary, ParserRuleContext context)
    {
        if (value.BorrowLifetime.Kind == BorrowLifetimeKind.LocalScope)
        {
            OwnershipError(summary, "STK4202", $"Lifetime error: cannot return {DescribeBorrowSource(value)} because it is tied to local scope and would escape the current function.", context);
            ReportBorrowSourceNote(summary, value.BorrowLifetime);
            return;
        }

        if (value.BorrowLifetime.Kind == BorrowLifetimeKind.Temporary)
        {
            OwnershipError(summary, "STK4202", $"Lifetime error: cannot return {DescribeBorrowSource(value)} because it is tied to a temporary value.", context);
            ReportBorrowSourceNote(summary, value.BorrowLifetime);
            return;
        }

        if (value.BorrowLifetime.Kind == BorrowLifetimeKind.Unknown)
        {
            OwnershipError(summary, "STK4202", $"Lifetime error: cannot return {DescribeBorrowSource(value)} because its source lifetime could not be proven for this return path.", context);
            ReportBorrowSourceNote(summary, value.BorrowLifetime);
        }
    }

    private bool DoesLifetimeOutliveScope(BorrowLifetime lifetime, int targetScopeId, FlowState state)
    {
        return lifetime.Kind switch
        {
            BorrowLifetimeKind.None => true,
            BorrowLifetimeKind.External => true,
            BorrowLifetimeKind.Temporary => false,
            BorrowLifetimeKind.Unknown => false,
            BorrowLifetimeKind.LocalScope => state.ScopeContains(lifetime.ScopeId!.Value, targetScopeId),
            _ => false
        };
    }

    private void SeedMutableGlobals()
    {
        foreach (var global in _typeModel.Globals)
        {
            if (!global.Value.IsMutable)
            {
                continue;
            }

            _mutableGlobals[global.Key] = true;
        }
    }

    private static StorageClass ParseStorageClass(StarkParser.StorageClassContext context)
    {
        return context.GetText() switch
        {
            "stack" => StorageClass.Stack,
            "heap" => StorageClass.Heap,
            "register" => StorageClass.Register,
            "static" => StorageClass.Static,
            "arena" => StorageClass.Arena,
            _ => StorageClass.None
        };
    }

    private static bool IsMoveOnly(StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.Error || type.Kind == StarkTypeKind.Void)
        {
            return false;
        }

        if (type.BorrowKind != StarkBorrowKind.None)
        {
            return type.IsMutableView;
        }

        return type.Kind switch
        {
            StarkTypeKind.Bool => false,
            StarkTypeKind.Integer => false,
            StarkTypeKind.Float => false,
            StarkTypeKind.RawPointer => false,
            StarkTypeKind.Null => false,
            _ => true
        };
    }

    private static bool IsAutomaticallyDropped(StarkTypeSymbol type, StorageClass storageClass)
    {
        return IsMoveOnly(type) && storageClass != StorageClass.Static;
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

    private static StarkTypeSymbol EvaluateLiteralType(StarkParser.LiteralContext literal)
    {
        if (literal.signedIntegerLiteral() is not null)
        {
            return StarkTypeSymbols.Integer(8);
        }

        if (literal.FloatLiteral() is not null)
        {
            return StarkTypeSymbols.Float(32);
        }

        if (literal.StringLiteral() is not null)
        {
            return StarkTypeSymbols.Ascii;
        }

        if (literal.CharacterLiteral() is not null)
        {
            return StarkTypeSymbols.Ascii;
        }

        if (literal.TRUE() is not null || literal.FALSE() is not null)
        {
            return StarkTypeSymbols.Bool;
        }

        return StarkTypeSymbols.Null;
    }

    private SourceLocation Location(IToken token)
    {
        var tokenText = token.Text;
        if (string.IsNullOrEmpty(tokenText))
        {
            return new SourceLocation(_context.Input.FilePath, token.Line, token.Column + 1);
        }

        var normalizedText = tokenText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedText.Split('\n');
        if (lines.Length == 1)
        {
            return new SourceLocation(
                _context.Input.FilePath,
                token.Line,
                token.Column + 1,
                token.Line,
                token.Column + Math.Max(lines[0].Length, 1));
        }

        return new SourceLocation(
            _context.Input.FilePath,
            token.Line,
            token.Column + 1,
            token.Line + lines.Length - 1,
            Math.Max(lines[^1].Length, 1));
    }

    private void OwnershipError(FunctionOwnershipBuilder summary, string code, string message, ParserRuleContext context)
    {
        OwnershipError(summary, code, message, context.Start);
    }

    private void OwnershipError(FunctionOwnershipBuilder summary, string code, string message, IToken token)
    {
        summary.OwnershipValid = false;
        var location = Location(token);
        if (!summary.TryRecordDiagnostic(DiagnosticSeverity.Error, code, message, location))
        {
            return;
        }

        _context.Diagnostics.Error(code, message, "ownership-validate", location);
    }

    private void OwnershipError(FunctionOwnershipBuilder summary, string code, string message, SourceLocation? location)
    {
        summary.OwnershipValid = false;
        if (!summary.TryRecordDiagnostic(DiagnosticSeverity.Error, code, message, location))
        {
            return;
        }

        _context.Diagnostics.Error(code, message, "ownership-validate", location);
    }

    private void OwnershipNote(FunctionOwnershipBuilder summary, string code, string message, SourceLocation location)
    {
        if (!summary.TryRecordDiagnostic(DiagnosticSeverity.Info, code, message, location))
        {
            return;
        }

        _context.Diagnostics.Info(code, message, "ownership-validate", location);
    }

    private void RecordImplicitDrops(VariableInfo variable, VariableState state, FunctionOwnershipBuilder summary)
    {
        foreach (var target in GetImplicitDropTargets(variable, state))
        {
            summary.ImplicitDrops.Add(target);
        }
    }

    private void ValidateScopeExitState(VariableInfo variable, VariableState state, FunctionOwnershipBuilder summary)
    {
        var implicitDropTargets = GetImplicitDropTargets(variable, state);
        if (IsEnumType(variable.Type)
            && IsAutomaticallyDropped(variable.Type, variable.StorageClass)
            && state.MayBeInitialized
            && !state.IsDefinitelyInitialized)
        {
            if (implicitDropTargets.Count == 0)
            {
                return;
            }

            OwnershipError(
                summary,
                "STK4205",
                $"Drop error: cannot drop '{variable.Name}' because enum values must be initialized on every path before scope exit.",
                variable.DeclarationLocation);
            return;
        }

        if (!IsAutomaticallyDropped(variable.Type, variable.StorageClass)
            || implicitDropTargets.Count == 0
            || state.AggregateState is null
            || state.IsDefinitelyInitialized
            || !state.AggregateState.MayHaveAnyAvailableFields)
        {
            return;
        }

        if (!state.AggregateState.HasDefinitelyUnavailableUninitializedFields)
        {
            return;
        }

        OwnershipError(
            summary,
            "STK4205",
            $"Drop error: cannot drop '{variable.Name}' because it is not fully initialized. Missing fields: {DescribeDefinitelyUnavailableFields(state.AggregateState)}.",
            variable.DeclarationLocation);
    }

    private bool TryEnsureValueAvailable(
        ExpressionInfo value,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        IToken token)
    {
        if (value.Variable is not { } variable)
        {
            return true;
        }

        if (variable.Origin == VariableOrigin.Global)
        {
            return true;
        }

        if (!state.TryGetState(variable.Id, out var variableState))
        {
            OwnershipError(summary, "STK4200", $"Value '{variable.Name}' is not available in the current flow state.", token);
            return false;
        }

        if (value.ProjectionPath is { Length: > 0 } projectionPath)
        {
            return TryEnsureProjectedValueAvailable(variable, variableState, projectionPath, value.HasIndexProjection, summary, use, token);
        }

        if (variableState.IsDefinitelyInitialized || use.Kind == ValueUseKind.Place)
        {
            return true;
        }

        ReportUnavailableValue(variable, variableState, summary, token);
        return false;
    }

    private bool TryEnsureProjectedValueAvailable(
        VariableInfo variable,
        VariableState state,
        IReadOnlyList<string> projectionPath,
        bool hasIndexProjection,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        IToken token)
    {
        if (projectionPath.Count == 0)
        {
            return true;
        }

        if (state.UnavailableKind == UnavailableValueKind.Moved)
        {
            ReportUnavailableValue(variable, state, summary, token);
            return false;
        }

        if (!TryGetNamedAggregate(variable.Type, out var namedType))
        {
            if (!state.IsDefinitelyInitialized && use.Kind != ValueUseKind.Place)
            {
                ReportUnavailableValue(variable, state, summary, token);
                return false;
            }

            return true;
        }

        var aggregateState = state.AggregateState ?? AggregateFieldState.Empty;
        var topLevelField = projectionPath[0];
        var fieldState = aggregateState.GetFieldState(topLevelField);

        if (use.Kind == ValueUseKind.Place && projectionPath.Count == 1 && !hasIndexProjection)
        {
            return true;
        }

        if (fieldState.IsDefinitelyAvailable)
        {
            return true;
        }

        if (fieldState.UnavailableKind == UnavailableValueKind.Moved)
        {
            OwnershipError(
                summary,
                "STK4200",
                $"Move error: field '{topLevelField}' of '{variable.Name}' was moved and must be reinitialized before it can be read.",
                token);
            if (fieldState.UnavailableLocation is not null)
            {
                OwnershipNote(summary, "STK4200", $"Field '{topLevelField}' of '{variable.Name}' was moved here.", fieldState.UnavailableLocation);
            }

            return false;
        }

        if (fieldState.UnavailableKind == UnavailableValueKind.ControlFlow || fieldState.MayBeAvailable)
        {
            OwnershipError(
                summary,
                "STK4200",
                $"Control-flow error: field '{topLevelField}' of '{variable.Name}' is not available on every path.",
                token);
            return false;
        }

        OwnershipError(
            summary,
            "STK4205",
            projectionPath.Count == 1 && !hasIndexProjection
                ? $"Initialization error: field '{topLevelField}' of '{variable.Name}' is not initialized yet."
                : $"Initialization error: cannot access '{FormatProjection(variable.Name, projectionPath, hasIndexProjection)}' because field '{topLevelField}' of '{variable.Name}' is not initialized yet.",
            token);
        if (variable.DeclarationLocation is not null)
        {
            OwnershipNote(summary, "STK4205", $"Aggregate '{variable.Name}' was declared here.", variable.DeclarationLocation);
        }

        return false;
    }

    private void ReportUnavailableValue(
        VariableInfo variable,
        VariableState state,
        FunctionOwnershipBuilder summary,
        IToken token)
    {
        if (state.AggregateState is not null
            && state.AggregateState.MayHaveAnyAvailableFields
            && state.UnavailableKind is not UnavailableValueKind.Moved)
        {
            if (state.AggregateState.HasDefinitelyUnavailableMovedFields
                && !state.AggregateState.HasDefinitelyUnavailableUninitializedFields)
            {
                OwnershipError(
                    summary,
                    "STK4200",
                    $"Move error: value '{variable.Name}' is partially moved. Unavailable fields: {DescribeDefinitelyUnavailableFields(state.AggregateState)}.",
                    token);

                foreach (var movedField in state.AggregateState.GetDefinitelyUnavailableFields(UnavailableValueKind.Moved))
                {
                    if (movedField.UnavailableLocation is not null)
                    {
                        OwnershipNote(summary, "STK4200", $"Field '{movedField.Name}' of '{variable.Name}' was moved here.", movedField.UnavailableLocation);
                    }
                }

                return;
            }

            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: value '{variable.Name}' is not fully initialized. Missing fields: {DescribeDefinitelyUnavailableFields(state.AggregateState)}.",
                token);
            if (variable.DeclarationLocation is not null)
            {
                OwnershipNote(summary, "STK4205", $"Aggregate '{variable.Name}' was declared here.", variable.DeclarationLocation);
            }

            return;
        }

        OwnershipError(summary, "STK4200", DescribeUnavailableValue(variable.Name, state), token);
        ReportUnavailableValueNote(summary, variable, state);
    }

    private void ReportUnavailableValueNote(FunctionOwnershipBuilder summary, VariableInfo variable, VariableState state)
    {
        if (state.UnavailableKind == UnavailableValueKind.Moved && state.UnavailableLocation is not null)
        {
            OwnershipNote(summary, "STK4200", $"Value '{variable.Name}' was moved here.", state.UnavailableLocation);
            return;
        }

        if (state.UnavailableKind == UnavailableValueKind.NeverInitialized && variable.DeclarationLocation is not null)
        {
            OwnershipNote(summary, "STK4200", $"Value '{variable.Name}' was declared here without an initializer.", variable.DeclarationLocation);
        }
    }

    private void ReportBorrowSourceNote(FunctionOwnershipBuilder summary, BorrowLifetime lifetime)
    {
        if (lifetime.OriginLocation is null)
        {
            return;
        }

        OwnershipNote(summary, "STK4202", lifetime.OriginDescription is null ? "Borrow source is here." : $"{lifetime.OriginDescription} is here.", lifetime.OriginLocation);
    }

    private bool TryGetNamedAggregate(StarkTypeSymbol type, out NamedTypeSymbol namedType)
    {
        namedType = null!;
        if (type.NamedType is null || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var candidate))
        {
            return false;
        }

        namedType = candidate;
        return namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record;
    }

    private static string[] AppendProjection(IReadOnlyList<string>? existing, string memberName)
    {
        if (existing is null || existing.Count == 0)
        {
            return [memberName];
        }

        var result = new string[existing.Count + 1];
        for (var index = 0; index < existing.Count; index++)
        {
            result[index] = existing[index];
        }

        result[^1] = memberName;
        return result;
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

    private static string DescribeDefinitelyUnavailableFields(AggregateFieldState aggregateState) =>
        string.Join(
            ", ",
            aggregateState.GetDefinitelyUnavailableFields()
                .Select(field => field.UnavailableKind == UnavailableValueKind.Moved ? $"{field.Name} (moved)" : field.Name)
                .DefaultIfEmpty("none"));

    private static string FormatProjection(string variableName, IReadOnlyList<string> projectionPath, bool hasIndexProjection)
    {
        var projection = string.Join(".", projectionPath);
        return hasIndexProjection ? $"{variableName}.{projection}[...]" : $"{variableName}.{projection}";
    }

    private static string DescribeUnavailableValue(string name, VariableState state)
    {
        return state.UnavailableKind switch
        {
            UnavailableValueKind.Moved => $"Move error: value '{name}' was moved and must be reinitialized before it can be read.",
            UnavailableValueKind.NeverInitialized => $"Initialization error: value '{name}' is not initialized yet.",
            UnavailableValueKind.PartiallyInitialized => $"Initialization error: value '{name}' is not fully initialized yet.",
            UnavailableValueKind.ControlFlow => $"Control-flow error: value '{name}' is not available on every path; it may have been moved or may not have been initialized.",
            _ => $"Value '{name}' is not available in the current flow state."
        };
    }

    private bool IsEnumType(StarkTypeSymbol type)
    {
        return type.NamedType is not null
            && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            && namedType.Kind == DeclarationKind.Enum;
    }

    private IReadOnlyList<string> GetImplicitDropTargets(VariableInfo variable, VariableState state)
    {
        if (!state.MayBeInitialized || !IsAutomaticallyDropped(variable.Type, variable.StorageClass))
        {
            return [];
        }

        if (!IsEnumType(variable.Type))
        {
            return [variable.Name];
        }

        if (variable.Type.NamedType is null
            || !_typeModel.NamedTypes.TryGetValue(variable.Type.NamedType, out var namedType)
            || namedType.Kind != DeclarationKind.Enum)
        {
            return [variable.Name];
        }

        var dropVariants = namedType.Variants
            .Where(static variant => VariantRequiresImplicitDrop(variant))
            .ToArray();
        if (dropVariants.Length == 0)
        {
            return [];
        }

        if (state.AggregateState is null)
        {
            return dropVariants.Select(variant => $"{variable.Name}.{variant.Name}").ToArray();
        }

        var targets = new List<string>();
        foreach (var variant in dropVariants)
        {
            if (state.AggregateState.GetFieldState(GetEnumCaseMarkerName(variant.Name)).MayBeAvailable)
            {
                targets.Add($"{variable.Name}.{variant.Name}");
            }
        }

        return targets;
    }

    private static bool VariantRequiresImplicitDrop(EnumVariantSymbol variant) =>
        variant.Fields.Any(static field => IsMoveOnly(field.Type));

    private void ConsumeSwitchValueForOwnedEnumCapture(
        ExpressionInfo switchValue,
        FlowState state,
        FunctionOwnershipBuilder summary,
        IToken token)
    {
        if (switchValue.IsIndirectPlace)
        {
            OwnershipError(summary, "STK4203", $"Cannot move out of field or indexed place of type '{switchValue.Type.DisplayName}'.", token);
            return;
        }

        if (switchValue.Variable is null)
        {
            return;
        }

        if (switchValue.Variable.Origin == VariableOrigin.Global)
        {
            OwnershipError(summary, "STK4204", $"Cannot move out of global or static storage '{switchValue.Variable.Name}'.", token);
            return;
        }

        if (!state.TryGetState(switchValue.Variable.Id, out var stateValue))
        {
            OwnershipError(summary, "STK4200", $"Value '{switchValue.Variable.Name}' is not available in the current flow state.", token);
            return;
        }

        if (stateValue.UnavailableKind == UnavailableValueKind.Moved)
        {
            return;
        }

        if (!stateValue.IsDefinitelyInitialized)
        {
            ReportUnavailableValue(switchValue.Variable, stateValue, summary, token);
            return;
        }

        state.SetMoved(switchValue.Variable.Id, switchValue.BorrowLifetime, Location(token));
        summary.Moves.Add(switchValue.Variable.Name);
    }

    private void NarrowSwitchValueToEnumCase(
        ExpressionInfo switchValue,
        FlowState state,
        NamedTypeSymbol enumType,
        EnumVariantSymbol variant)
    {
        if (switchValue.Variable is not { } variable
            || !switchValue.IsDirectVariable
            || switchValue.HasIndexProjection
            || switchValue.ProjectionPath is not null
            || !state.TryGetState(variable.Id, out var variableState))
        {
            return;
        }

        state.SetInitialized(variable.Id, variableState.BorrowLifetime, CreateEnumAggregateState(enumType, variant));
    }

    private AggregateFieldState? CreateInitializedAggregateState(StarkTypeSymbol type)
    {
        if (type.NamedType is null || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType))
        {
            return null;
        }

        if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            return AggregateFieldState.Full(namedType);
        }

        return null;
    }

    private static AggregateFieldState CreateEnumAggregateState(NamedTypeSymbol enumType, EnumVariantSymbol variant)
    {
        var fields = new Dictionary<string, AggregateFieldAvailability>(StringComparer.Ordinal)
        {
            [GetEnumCaseMarkerName(variant.Name)] = AggregateFieldAvailability.Initialized()
        };

        foreach (var field in variant.Fields)
        {
            var fieldName = field.Name ?? $"#{field.Position}";
            fields[$"{variant.Name}.{fieldName}"] = AggregateFieldAvailability.Initialized();
        }

        return new AggregateFieldState(fields);
    }

    private static string GetEnumCaseMarkerName(string variantName) => $"$case:{variantName}";

    private static string DescribeBorrowSource(ExpressionInfo value)
    {
        if (value.Variable is { } variable)
        {
            return $"borrow '{variable.Name}'";
        }

        return value.BorrowLifetime.Kind switch
        {
            BorrowLifetimeKind.Temporary => "a temporary borrow",
            BorrowLifetimeKind.LocalScope => "a local-scope borrow",
            BorrowLifetimeKind.Unknown => "a borrow with an unknown source lifetime",
            _ => "this borrow"
        };
    }

    private static StarkParser.ExpressionContext WrapExpression(StarkParser.ExpressionContext expression) => expression;

    private enum VariableOrigin
    {
        Local,
        Parameter,
        Global
    }

    private enum StorageClass
    {
        None,
        Stack,
        Heap,
        Register,
        Static,
        Arena
    }

    private enum BorrowLifetimeKind
    {
        None,
        External,
        LocalScope,
        Temporary,
        Unknown
    }

    private enum ValueUseKind
    {
        Read,
        Consume,
        Place,
        ProjectBase
    }

    private enum UnavailableValueKind
    {
        None,
        NeverInitialized,
        PartiallyInitialized,
        Moved,
        ControlFlow
    }

    private readonly record struct ValueUse(ValueUseKind Kind, bool CaptureBorrowLifetime = false)
    {
        public static readonly ValueUse Read = new(ValueUseKind.Read);
        public static readonly ValueUse ConsumeTemporary = new(ValueUseKind.Consume);
        public static readonly ValueUse Place = new(ValueUseKind.Place);
        public static readonly ValueUse ProjectBase = new(ValueUseKind.ProjectBase);

        public static ValueUse ForAssignment(StarkTypeSymbol targetType) =>
            targetType.BorrowKind != StarkBorrowKind.None
                ? new(ValueUseKind.Read, CaptureBorrowLifetime: true)
                : IsMoveOnly(targetType) ? new(ValueUseKind.Consume) : Read;

        public static ValueUse ForCallArgument(StarkTypeSymbol parameterType) =>
            parameterType.BorrowKind != StarkBorrowKind.None
                ? new(ValueUseKind.Read, CaptureBorrowLifetime: true)
                : parameterType.Kind == StarkTypeKind.RawPointer || !IsMoveOnly(parameterType)
                ? Read
                : new(ValueUseKind.Consume);

        public static ValueUse ForReturn(StarkTypeSymbol returnType) =>
            returnType.BorrowKind != StarkBorrowKind.None
                ? new(ValueUseKind.Read, CaptureBorrowLifetime: true)
                : !IsMoveOnly(returnType)
                ? Read
                : new(ValueUseKind.Consume);

        public static ValueUse ForAssignment(StarkParser.ExpressionContext _) => ConsumeTemporary;
    }

    private sealed record BorrowLifetime(BorrowLifetimeKind Kind, int? ScopeId = null)
    {
        public static readonly BorrowLifetime None = new(BorrowLifetimeKind.None);
        public static readonly BorrowLifetime External = new(BorrowLifetimeKind.External);
        public static readonly BorrowLifetime Temporary = new(BorrowLifetimeKind.Temporary);
        public static readonly BorrowLifetime Unknown = new(BorrowLifetimeKind.Unknown);

        public SourceLocation? OriginLocation { get; init; }

        public string? OriginDescription { get; init; }

        public static BorrowLifetime Local(int scopeId, SourceLocation? originLocation = null, string? originDescription = null) =>
            new(BorrowLifetimeKind.LocalScope, scopeId) { OriginLocation = originLocation, OriginDescription = originDescription };

        public static BorrowLifetime ExternalAt(SourceLocation? originLocation, string? originDescription = null) =>
            originLocation is null ? External : new(BorrowLifetimeKind.External) { OriginLocation = originLocation, OriginDescription = originDescription };

        public static BorrowLifetime TemporaryAt(SourceLocation originLocation, string? originDescription = null) =>
            new(BorrowLifetimeKind.Temporary) { OriginLocation = originLocation, OriginDescription = originDescription };

        public static BorrowLifetime UnknownAt(SourceLocation originLocation, string? originDescription = null) =>
            new(BorrowLifetimeKind.Unknown) { OriginLocation = originLocation, OriginDescription = originDescription };

        public static BorrowLifetime Merge(BorrowLifetime left, BorrowLifetime right)
        {
            if (left == right)
            {
                return left;
            }

            if (left.Kind == BorrowLifetimeKind.None)
            {
                return right;
            }

            if (right.Kind == BorrowLifetimeKind.None)
            {
                return left;
            }

            return Unknown;
        }

        public static BorrowLifetime InferFromCall(IReadOnlyList<BorrowLifetime> arguments, SourceLocation? originLocation = null, string? originDescription = null)
        {
            if (arguments.Count == 0)
            {
                return originLocation is null ? Unknown : UnknownAt(originLocation, originDescription);
            }

            var distinct = arguments.Distinct().ToArray();
            return distinct.Length == 1
                ? distinct[0]
                : originLocation is null ? Unknown : UnknownAt(originLocation, originDescription);
        }
    }

    private sealed record VariableInfo(
        string Name,
        StarkTypeSymbol Type,
        StorageClass StorageClass,
        VariableOrigin Origin,
        bool IsMutable,
        bool IsConstant,
        BorrowLifetime BorrowLifetime,
        SourceLocation? DeclarationLocation)
    {
        public int Id { get; init; }

        public int DeclarationScopeId { get; init; }
    }

    private sealed record VariableState(
        bool IsDefinitelyInitialized,
        bool MayBeInitialized,
        BorrowLifetime BorrowLifetime,
        UnavailableValueKind UnavailableKind,
        AggregateFieldState? AggregateState = null,
        SourceLocation? UnavailableLocation = null)
    {
        public static VariableState Initialized(BorrowLifetime borrowLifetime, AggregateFieldState? aggregateState) =>
            new(true, true, borrowLifetime.Kind == BorrowLifetimeKind.None ? BorrowLifetime.None : borrowLifetime, UnavailableValueKind.None, aggregateState);

        public static VariableState Uninitialized(BorrowLifetime borrowLifetime, AggregateFieldState? aggregateState) =>
            new(false, false, borrowLifetime.Kind == BorrowLifetimeKind.None ? BorrowLifetime.None : borrowLifetime, UnavailableValueKind.NeverInitialized, aggregateState);

        public static VariableState Moved(BorrowLifetime borrowLifetime, AggregateFieldState? aggregateState, SourceLocation? unavailableLocation) =>
            new(false, false, borrowLifetime.Kind == BorrowLifetimeKind.None ? BorrowLifetime.None : borrowLifetime, UnavailableValueKind.Moved, aggregateState, unavailableLocation);

        public static VariableState PartiallyInitialized(BorrowLifetime borrowLifetime, AggregateFieldState aggregateState) =>
            new(false, aggregateState.MayHaveAnyAvailableFields, borrowLifetime.Kind == BorrowLifetimeKind.None ? BorrowLifetime.None : borrowLifetime, UnavailableValueKind.PartiallyInitialized, aggregateState);

        public static VariableState Merge(VariableState left, VariableState right, BorrowLifetime borrowLifetime)
        {
            var isDefinitelyInitialized = left.IsDefinitelyInitialized && right.IsDefinitelyInitialized;
            var mayBeInitialized = left.MayBeInitialized || right.MayBeInitialized;
            var aggregateState = AggregateFieldState.Merge(left.AggregateState, right.AggregateState);

            if (isDefinitelyInitialized)
            {
                return Initialized(borrowLifetime, aggregateState);
            }

            if (mayBeInitialized || left.UnavailableKind != right.UnavailableKind)
            {
                return new VariableState(false, mayBeInitialized, borrowLifetime, UnavailableValueKind.ControlFlow, aggregateState);
            }

            return new VariableState(false, false, borrowLifetime, left.UnavailableKind, aggregateState, left.UnavailableLocation ?? right.UnavailableLocation);
        }
    }

    private BorrowLifetime InferBorrowLifetimeFromValue(ExpressionInfo value, IToken token)
    {
        if (value.Variable is { } variable && value.IsPlace)
        {
            return variable.Origin == VariableOrigin.Global
                ? BorrowLifetime.ExternalAt(variable.DeclarationLocation, $"borrow source for '{variable.Name}'")
                : BorrowLifetime.Local(variable.DeclarationScopeId, variable.DeclarationLocation, $"borrow source for '{variable.Name}'");
        }

        return value.Type.Kind == StarkTypeKind.Error
            ? BorrowLifetime.UnknownAt(Location(token), "borrow source")
            : BorrowLifetime.TemporaryAt(Location(token), "temporary borrow");
    }

    private sealed record ExpressionInfo(
        StarkTypeSymbol Type,
        VariableInfo? Variable = null,
        TypedFunctionSignature? Function = null,
        string? OverloadSourceName = null,
        BorrowLifetime BorrowLifetime = null!,
        bool IsPlace = false,
        bool IsDirectVariable = false,
        bool IsIndirectPlace = false,
        string? NamespaceName = null,
        string[]? ProjectionPath = null,
        bool HasIndexProjection = false,
        ExpressionInfo? Receiver = null,
        EnumConstructorBinding? EnumConstructor = null,
        AggregateFieldState? AggregateState = null)
    {
        public ExpressionInfo(StarkTypeSymbol type)
            : this(type, BorrowLifetime: BorrowLifetime.None)
        {
        }
    }

    private sealed record EnumConstructorBinding(
        string Name,
        EnumVariantSymbol Variant);

    private sealed class ScopeFrame
    {
        public ScopeFrame(int id, ScopeFrame? parent)
        {
            Id = id;
            Parent = parent;
        }

        public int Id { get; }

        public ScopeFrame? Parent { get; }

        public Dictionary<string, int> Symbols { get; } = new(StringComparer.Ordinal);

        public List<int> DeclaredVariables { get; } = [];
    }

    private sealed class FlowState
    {
        private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;
        private readonly Dictionary<int, VariableInfo> _variables;
        private readonly Dictionary<int, VariableState> _states;
        private readonly Dictionary<int, ScopeFrame> _scopes;
        private int _nextVariableId;
        private int _nextScopeId;

        public FlowState(IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
        {
            _namedTypes = namedTypes;
            _variables = new Dictionary<int, VariableInfo>();
            _states = new Dictionary<int, VariableState>();
            _scopes = new Dictionary<int, ScopeFrame>();
            CurrentScope = new ScopeFrame(0, null);
            _scopes[0] = CurrentScope;
            _nextScopeId = 1;
        }

        private FlowState(
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            Dictionary<int, VariableInfo> variables,
            Dictionary<int, VariableState> states,
            Dictionary<int, ScopeFrame> scopes,
            ScopeFrame currentScope,
            int nextVariableId,
            int nextScopeId)
        {
            _namedTypes = namedTypes;
            _variables = variables;
            _states = states;
            _scopes = scopes;
            CurrentScope = currentScope;
            _nextVariableId = nextVariableId;
            _nextScopeId = nextScopeId;
        }

        public ScopeFrame CurrentScope { get; private set; }

        public ScopeFrame EnterScope()
        {
            var frame = new ScopeFrame(_nextScopeId++, CurrentScope);
            _scopes[frame.Id] = frame;
            CurrentScope = frame;
            return frame;
        }

        public void ExitScope(
            ScopeFrame scope,
            FunctionOwnershipBuilder summary,
            Action<VariableInfo, VariableState, FunctionOwnershipBuilder> validateScopeExitState,
            Action<VariableInfo, VariableState, FunctionOwnershipBuilder> recordImplicitDrops)
        {
            foreach (var variableId in scope.DeclaredVariables)
            {
                if (!_variables.TryGetValue(variableId, out var variable)
                    || !_states.TryGetValue(variableId, out var state))
                {
                    continue;
                }

                validateScopeExitState(variable, state, summary);

                if (state.MayBeInitialized && IsAutomaticallyDropped(variable.Type, variable.StorageClass))
                {
                    recordImplicitDrops(variable, state, summary);
                }

                _states.Remove(variableId);
                _variables.Remove(variableId);
            }

            CurrentScope = scope.Parent ?? scope;
            _scopes.Remove(scope.Id);
        }

        public void Declare(VariableInfo variable, bool isInitialized, AggregateFieldState? aggregateState = null)
        {
            var id = _nextVariableId++;
            var bound = variable with { Id = id, DeclarationScopeId = CurrentScope.Id };
            CurrentScope.Symbols[bound.Name] = id;
            CurrentScope.DeclaredVariables.Add(id);
            _variables[id] = bound;
            aggregateState ??= CreateAggregateState(bound.Type, isInitialized);
            _states[id] = isInitialized
                ? VariableState.Initialized(bound.BorrowLifetime, aggregateState)
                : VariableState.Uninitialized(bound.BorrowLifetime, aggregateState);
        }

        public bool TryLookup(string name, out VariableInfo variable)
        {
            var scope = CurrentScope;
            while (scope is not null)
            {
                if (scope.Symbols.TryGetValue(name, out var id) && _variables.TryGetValue(id, out variable!))
                {
                    return true;
                }

                scope = scope.Parent;
            }

            variable = default!;
            return false;
        }

        public bool TryGetState(int variableId, out VariableState state) => _states.TryGetValue(variableId, out state!);

        public void SetInitialized(int variableId, BorrowLifetime borrowLifetime, AggregateFieldState? aggregateState = null)
        {
            if (_states.ContainsKey(variableId) && _variables.TryGetValue(variableId, out var variable))
            {
                aggregateState ??= CreateAggregateState(variable.Type, isInitialized: true);
                _states[variableId] = VariableState.Initialized(borrowLifetime, aggregateState);
            }
        }

        public void MarkFieldInitialized(int variableId, string fieldName)
        {
            if (!_states.TryGetValue(variableId, out var currentState)
                || !_variables.TryGetValue(variableId, out var variable)
                || !TryGetNamedAggregate(variable.Type, out var namedType))
            {
                return;
            }

            var aggregateState = (currentState.AggregateState ?? AggregateFieldState.Empty).MarkInitialized(fieldName);
            var isFullyInitialized = aggregateState.IsComplete(namedType);
            _states[variableId] = isFullyInitialized
                ? VariableState.Initialized(currentState.BorrowLifetime, aggregateState)
                : VariableState.PartiallyInitialized(currentState.BorrowLifetime, aggregateState);
        }

        public void MarkFieldMoved(int variableId, string fieldName, BorrowLifetime borrowLifetime, SourceLocation unavailableLocation)
        {
            if (!_states.TryGetValue(variableId, out var currentState)
                || !_variables.TryGetValue(variableId, out var variable)
                || !TryGetNamedAggregate(variable.Type, out var namedType))
            {
                return;
            }

            var aggregateState = (currentState.AggregateState ?? AggregateFieldState.Empty).MarkMoved(fieldName, unavailableLocation);
            var isFullyInitialized = aggregateState.IsComplete(namedType);
            _states[variableId] = isFullyInitialized
                ? VariableState.Initialized(borrowLifetime, aggregateState)
                : VariableState.PartiallyInitialized(borrowLifetime, aggregateState);
        }

        public void SetMoved(int variableId, BorrowLifetime borrowLifetime, SourceLocation unavailableLocation)
        {
            if (_states.TryGetValue(variableId, out var currentState) && _variables.TryGetValue(variableId, out var variable))
            {
                var aggregateState = currentState.AggregateState is not null
                    ? MarkAllAggregateFieldsMoved(currentState.AggregateState, unavailableLocation)
                    : CreateAggregateState(variable.Type, isInitialized: false);
                _states[variableId] = VariableState.Moved(
                    borrowLifetime,
                    aggregateState,
                    unavailableLocation);
            }
        }

        public FlowState Clone()
        {
            var scopeMap = new Dictionary<int, ScopeFrame>();
            ScopeFrame CloneScope(ScopeFrame source)
            {
                if (scopeMap.TryGetValue(source.Id, out var existing))
                {
                    return existing;
                }

                var parent = source.Parent is null ? null : CloneScope(source.Parent);
                var clone = new ScopeFrame(source.Id, parent);
                foreach (var symbol in source.Symbols)
                {
                    clone.Symbols[symbol.Key] = symbol.Value;
                }

                clone.DeclaredVariables.AddRange(source.DeclaredVariables);
                scopeMap[source.Id] = clone;
                return clone;
            }

            var currentScope = CloneScope(CurrentScope);
            var scopes = scopeMap.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            return new FlowState(
                _namedTypes,
                _variables.ToDictionary(static pair => pair.Key, static pair => pair.Value),
                _states.ToDictionary(static pair => pair.Key, static pair => pair.Value),
                scopes,
                currentScope,
                _nextVariableId,
                _nextScopeId);
        }

        public void MergeBranches(FlowState thenState, FlowState? elseState)
        {
            var visibleIds = GetVisibleVariableIds();
            foreach (var id in visibleIds)
            {
                var left = thenState._states.TryGetValue(id, out var thenVar)
                    ? thenVar
                    : VariableState.Uninitialized(BorrowLifetime.None, aggregateState: null);
                var right = elseState is null
                    ? _states.TryGetValue(id, out var original) ? original : VariableState.Uninitialized(BorrowLifetime.None, aggregateState: null)
                    : elseState._states.TryGetValue(id, out var elseVar) ? elseVar : VariableState.Uninitialized(BorrowLifetime.None, aggregateState: null);

                _states[id] = VariableState.Merge(left, right, BorrowLifetime.Merge(left.BorrowLifetime, right.BorrowLifetime));
            }
        }

        public void MergeBranches(IEnumerable<FlowState> branches)
        {
            var branchList = branches.ToArray();
            if (branchList.Length == 0)
            {
                return;
            }

            var visibleIds = GetVisibleVariableIds();
            foreach (var id in visibleIds)
            {
                var initialized = true;
                var mayBeInitialized = false;
                BorrowLifetime lifetime = BorrowLifetime.None;
                var unavailableKind = UnavailableValueKind.None;
                var first = true;

                foreach (var branch in branchList)
                {
                    var state = branch._states.TryGetValue(id, out var stateValue)
                        ? stateValue
                        : VariableState.Uninitialized(BorrowLifetime.None, aggregateState: null);
                    initialized &= state.IsDefinitelyInitialized;
                    lifetime = first ? state.BorrowLifetime : BorrowLifetime.Merge(lifetime, state.BorrowLifetime);
                    mayBeInitialized |= state.MayBeInitialized;
                    unavailableKind = first ? state.UnavailableKind : unavailableKind == state.UnavailableKind ? unavailableKind : UnavailableValueKind.ControlFlow;
                    first = false;
                }

                var aggregateState = branchList
                    .Select(branch => branch._states.TryGetValue(id, out var stateValue) ? stateValue.AggregateState : null)
                    .Aggregate(AggregateFieldState.Merge);
                _states[id] = initialized
                    ? VariableState.Initialized(lifetime, aggregateState)
                    : new VariableState(false, mayBeInitialized, lifetime, mayBeInitialized ? UnavailableValueKind.ControlFlow : unavailableKind, aggregateState);
            }
        }

        public void MergeLoop(FlowState loopState)
        {
            var visibleIds = GetVisibleVariableIds();
            foreach (var id in visibleIds)
            {
                var before = _states.TryGetValue(id, out var beforeState) ? beforeState : VariableState.Uninitialized(BorrowLifetime.None, aggregateState: null);
                var after = loopState._states.TryGetValue(id, out var afterState) ? afterState : VariableState.Uninitialized(BorrowLifetime.None, aggregateState: null);
                _states[id] = VariableState.Merge(before, after, BorrowLifetime.Merge(before.BorrowLifetime, after.BorrowLifetime));
            }
        }

        public bool ScopeContains(int ownerScopeId, int targetScopeId)
        {
            if (!_scopes.TryGetValue(targetScopeId, out var target))
            {
                return false;
            }

            var current = target;
            while (current is not null)
            {
                if (current.Id == ownerScopeId)
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private HashSet<int> GetVisibleVariableIds()
        {
            var ids = new HashSet<int>();
            var scope = CurrentScope;
            while (scope is not null)
            {
                foreach (var id in scope.Symbols.Values)
                {
                    ids.Add(id);
                }

                scope = scope.Parent;
            }

            return ids;
        }

        private AggregateFieldState? CreateAggregateState(StarkTypeSymbol type, bool isInitialized)
        {
            if (!TryGetNamedAggregate(type, out var namedType))
            {
                return null;
            }

            return isInitialized
                ? AggregateFieldState.Full(namedType)
                : AggregateFieldState.EmptyFor(namedType);
        }

        private bool TryGetNamedAggregate(StarkTypeSymbol type, out NamedTypeSymbol namedType)
        {
            namedType = null!;
            if (type.NamedType is null || !_namedTypes.TryGetValue(type.NamedType, out var candidate))
            {
                return false;
            }

            namedType = candidate;
            return namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record;
        }

        private static AggregateFieldState MarkAllAggregateFieldsMoved(AggregateFieldState state, SourceLocation unavailableLocation)
        {
            var fields = state.Fields.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value.IsDefinitelyAvailable
                    ? AggregateFieldAvailability.Moved(unavailableLocation)
                    : pair.Value,
                StringComparer.Ordinal);
            return new AggregateFieldState(fields);
        }
    }

    private sealed record AggregateFieldState(IReadOnlyDictionary<string, AggregateFieldAvailability> Fields)
    {
        public static readonly AggregateFieldState Empty = new(new Dictionary<string, AggregateFieldAvailability>(StringComparer.Ordinal));

        public bool MayHaveAnyAvailableFields => Fields.Values.Any(static state => state.MayBeAvailable);

        public bool HasDefinitelyUnavailableUninitializedFields =>
            Fields.Values.Any(static state => !state.IsDefinitelyAvailable && state.UnavailableKind == UnavailableValueKind.NeverInitialized);

        public bool HasDefinitelyUnavailableMovedFields =>
            Fields.Values.Any(static state => !state.IsDefinitelyAvailable && state.UnavailableKind == UnavailableValueKind.Moved);

        public AggregateFieldAvailability GetFieldState(string fieldName) =>
            Fields.TryGetValue(fieldName, out var state)
                ? state
                : AggregateFieldAvailability.Uninitialized();

        public bool IsComplete(NamedTypeSymbol namedType) =>
            namedType.OrderedFields.All(field => GetFieldState(field.Name).IsDefinitelyAvailable);

        public AggregateFieldState MarkInitialized(string fieldName)
        {
            var fields = CloneFields();
            fields[fieldName] = AggregateFieldAvailability.Initialized();
            return new AggregateFieldState(fields);
        }

        public AggregateFieldState MarkMoved(string fieldName, SourceLocation unavailableLocation)
        {
            var fields = CloneFields();
            fields[fieldName] = AggregateFieldAvailability.Moved(unavailableLocation);
            return new AggregateFieldState(fields);
        }

        public IEnumerable<(string Name, UnavailableValueKind UnavailableKind, SourceLocation? UnavailableLocation)> GetDefinitelyUnavailableFields()
        {
            return Fields
                .Where(static pair => !pair.Value.IsDefinitelyAvailable)
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => (pair.Key, pair.Value.UnavailableKind, pair.Value.UnavailableLocation));
        }

        public IEnumerable<(string Name, UnavailableValueKind UnavailableKind, SourceLocation? UnavailableLocation)> GetDefinitelyUnavailableFields(UnavailableValueKind kind)
        {
            return GetDefinitelyUnavailableFields().Where(field => field.UnavailableKind == kind);
        }

        public static AggregateFieldState Full(NamedTypeSymbol namedType)
        {
            var fields = new Dictionary<string, AggregateFieldAvailability>(StringComparer.Ordinal);
            foreach (var field in namedType.OrderedFields)
            {
                fields[field.Name] = AggregateFieldAvailability.Initialized();
            }

            return new AggregateFieldState(fields);
        }

        public static AggregateFieldState EmptyFor(NamedTypeSymbol namedType)
        {
            var fields = new Dictionary<string, AggregateFieldAvailability>(StringComparer.Ordinal);
            foreach (var field in namedType.OrderedFields)
            {
                fields[field.Name] = AggregateFieldAvailability.Uninitialized();
            }

            return new AggregateFieldState(fields);
        }

        public static AggregateFieldState? Merge(AggregateFieldState? left, AggregateFieldState? right)
        {
            if (left is null)
            {
                return right;
            }

            if (right is null)
            {
                return left;
            }

            var merged = new Dictionary<string, AggregateFieldAvailability>(StringComparer.Ordinal);
            foreach (var fieldName in left.Fields.Keys.Concat(right.Fields.Keys).Distinct(StringComparer.Ordinal))
            {
                merged[fieldName] = AggregateFieldAvailability.Merge(left.GetFieldState(fieldName), right.GetFieldState(fieldName));
            }

            return new AggregateFieldState(merged);
        }

        private Dictionary<string, AggregateFieldAvailability> CloneFields()
        {
            return Fields.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        }
    }

    private sealed record AggregateFieldAvailability(
        bool IsDefinitelyAvailable,
        bool MayBeAvailable,
        UnavailableValueKind UnavailableKind,
        SourceLocation? UnavailableLocation = null)
    {
        public static AggregateFieldAvailability Initialized() => new(true, true, UnavailableValueKind.None);

        public static AggregateFieldAvailability Uninitialized() => new(false, false, UnavailableValueKind.NeverInitialized);

        public static AggregateFieldAvailability Moved(SourceLocation unavailableLocation) => new(false, false, UnavailableValueKind.Moved, unavailableLocation);

        public static AggregateFieldAvailability Merge(AggregateFieldAvailability left, AggregateFieldAvailability right)
        {
            var isDefinitelyAvailable = left.IsDefinitelyAvailable && right.IsDefinitelyAvailable;
            var mayBeAvailable = left.MayBeAvailable || right.MayBeAvailable;

            if (isDefinitelyAvailable)
            {
                return Initialized();
            }

            if (mayBeAvailable || left.UnavailableKind != right.UnavailableKind)
            {
                return new AggregateFieldAvailability(false, mayBeAvailable, UnavailableValueKind.ControlFlow);
            }

            return new AggregateFieldAvailability(false, false, left.UnavailableKind, left.UnavailableLocation ?? right.UnavailableLocation);
        }
    }

    private sealed class FunctionOwnershipBuilder
    {
        private readonly HashSet<EmittedOwnershipDiagnosticKey> _emittedDiagnostics = [];

        public FunctionOwnershipBuilder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool OwnershipValid { get; set; } = true;

        public List<string> ImplicitDrops { get; } = [];

        public List<string> Moves { get; } = [];

        public bool TryRecordDiagnostic(DiagnosticSeverity severity, string code, string message, SourceLocation? location)
        {
            return _emittedDiagnostics.Add(new EmittedOwnershipDiagnosticKey(severity, code, message, location));
        }

        public FunctionOwnershipSummary Build()
        {
            return new FunctionOwnershipSummary(
                Name,
                OwnershipValid,
                ImplicitDrops.ToArray(),
                Moves.ToArray());
        }
    }

    private readonly record struct EmittedOwnershipDiagnosticKey(
        DiagnosticSeverity Severity,
        string Code,
        string Message,
        SourceLocation? Location);
}
