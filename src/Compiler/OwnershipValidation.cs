using System.Numerics;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class OwnershipValidator
{
    private static readonly StarkTypeSymbol NonNegativeI64Type = StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 63) - 1);

    private readonly CompilerPassContext _context;
    private readonly ParseResult _parseResult;
    private readonly SyntaxModel _syntaxModel;
    private readonly ModuleGraph _moduleGraph;
    private readonly TypeCheckModel _typeModel;
    private readonly StarkTypeResolver _typeResolver;
    private readonly Dictionary<string, DeclaredFunctionSyntax> _functionDeclarations;
    private readonly Dictionary<string, TypedFunctionSignature> _signatures;
    private readonly Dictionary<string, bool> _mutableGlobals = new(StringComparer.Ordinal);
    private readonly List<DynamicInitSliceLoopContext> _dynamicInitSliceLoopContexts = [];
    private ISet<string>? _currentFunctionGenericParameters;
    private IReadOnlyDictionary<string, ClosureWriteContract>? _activeClosureWriteContracts;
    private int _unsafeDepth;

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

        foreach (var (name, summary) in ValidateLambdaFunctions())
        {
            summaries[name] = summary;
        }

        return new OwnershipValidationModel(_syntaxModel.ModuleName, summaries);
    }

    private FunctionOwnershipSummary ValidateFunction(
        DeclaredFunctionSyntax functionDeclaration,
        TypedFunctionSignature signature)
    {
        var summary = new FunctionOwnershipBuilder(signature.Name, _typeModel.NamedTypes);
        if (signature.IsGeneric && functionDeclaration.Body.block() is not null)
        {
            // Open generic templates can depend on ownership and drop behavior of
            // unknown type parameters. Validate concrete instantiations instead
            // of rejecting the package template before T has a real layout.
            return summary.Build();
        }

        var state = new FlowState(_typeModel.NamedTypes);
        var functionScope = state.EnterScope();
        var parameterDeclarations = functionDeclaration.ParameterList.parameter();
        var previousGenericParameters = _currentFunctionGenericParameters;
        var previousUnsafeDepth = _unsafeDepth;
        var previousDynamicInitSliceLoopContextCount = _dynamicInitSliceLoopContexts.Count;
        _currentFunctionGenericParameters = signature.IsGeneric
            ? signature.GenericParams.ToHashSet(StringComparer.Ordinal)
            : null;

        try
        {
            if (signature.IsUnsafe)
            {
                _unsafeDepth++;
            }

            for (var index = 0; index < signature.Parameters.Count; index++)
            {
                var parameter = signature.Parameters[index];
                var declarationLocation = index < parameterDeclarations.Length
                    ? Location(parameterDeclarations[index].Identifier().Symbol)
                    : null;
                var parameterVariable = state.Declare(new VariableInfo(
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
                summary.DeclareRoot(
                    parameterVariable,
                    requiresDrop: IsAutomaticallyDropped(parameterVariable.Type, parameterVariable.StorageClass));
                if (parameter.Type.Kind == StarkTypeKind.Dynamic)
                {
                    state.SetDynamicStoragePrefix(parameter.Name, DynamicStoragePrefixState.Unknown);
                }
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
            _unsafeDepth = previousUnsafeDepth;
            if (_dynamicInitSliceLoopContexts.Count > previousDynamicInitSliceLoopContextCount)
            {
                _dynamicInitSliceLoopContexts.RemoveRange(
                    previousDynamicInitSliceLoopContextCount,
                    _dynamicInitSliceLoopContexts.Count - previousDynamicInitSliceLoopContextCount);
            }
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

        if (statement.unsafeStatement() is { } unsafeStatement)
        {
            _unsafeDepth++;
            try
            {
                if (unsafeStatement.block() is { } unsafeBlock)
                {
                    CheckBlock(unsafeBlock, state, signature, summary, openScope: true);
                }
                else if (unsafeStatement.assumeStatement() is { } unsafeAssumeStatement)
                {
                    CheckAssumeStatement(unsafeAssumeStatement, state, signature, summary);
                }
            }
            finally
            {
                _unsafeDepth--;
            }

            return;
        }

        if (statement.assumeStatement() is { } assumeStatement)
        {
            CheckAssumeStatement(assumeStatement, state, signature, summary);
            return;
        }

        if (statement.localConstantDeclaration() is { } localConstant)
        {
            var declaredType = ResolveLocalDeclarationType(
                TemplateLocalDeclarationFacts.ConstantKind,
                localConstant,
                localConstant.type_(),
                signature.Name);
            CheckLocalDeclaration(
                declaredType,
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
            if (ifStatement.expression() is { } condition)
            {
                EvaluateExpression(condition, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
            }
            else if (ifStatement.disjointRuntimeCondition() is { } disjointCondition)
            {
                foreach (var expression in disjointCondition.expressionList().expression())
                {
                    if (TryEvaluateRawPointerRegionExpression(expression, state, signature, summary))
                    {
                        continue;
                    }

                    EvaluateExpression(expression, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
                }
            }

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

            var dynamicInitSliceLoopContext = TryCreateDynamicInitSliceLoopContext(forStatement);
            if (dynamicInitSliceLoopContext is not null)
            {
                _dynamicInitSliceLoopContexts.Add(dynamicInitSliceLoopContext);
            }

            try
            {
                CheckStatement(forStatement.statement(), loopState, signature, summary);
            }
            finally
            {
                if (dynamicInitSliceLoopContext is not null)
                {
                    _dynamicInitSliceLoopContexts.RemoveAt(_dynamicInitSliceLoopContexts.Count - 1);
                }
            }

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

            ValidateActiveClosureWriteContracts(state, summary, returnStatement);
            return;
        }

        if (statement.expressionStatement() is { } expressionStatement)
        {
            EvaluateExpression(expressionStatement.expression(), state, signature, summary, ValueUse.ConsumeTemporary, allowFunctionReference: false);
        }
    }

    private void CheckAssumeStatement(
        StarkParser.AssumeStatementContext assumeStatement,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        foreach (var expression in assumeStatement.disjointRuntimeCondition().expressionList().expression())
        {
            if (TryEvaluateRawPointerRegionExpression(expression, state, signature, summary))
            {
                continue;
            }

            EvaluateExpression(expression, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        }

        var assumeScope = state.EnterScope();
        try
        {
            CheckStatement(assumeStatement.statement(), state, signature, summary);
        }
        finally
        {
            state.ExitScope(assumeScope, summary, ValidateScopeExitState, RecordImplicitDrops);
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

    private static DynamicInitSliceLoopContext? TryCreateDynamicInitSliceLoopContext(
        StarkParser.ForStatementContext forStatement)
    {
        if (!forStatement.loopContract().Any(static contract => contract.INDEPENDENT() is not null)
            || forStatement.forInitializer()?.localForVariableDeclaration() is not { } declaration
            || declaration.MUT() is null
            || declaration.variableDeclarators().variableDeclarator() is not [var declarator]
            || declarator.variableInitializer()?.expression() is not { } initializerExpression
            || !IsZeroExpression(initializerExpression)
            || declarator.Identifier()?.GetText() is not { Length: > 0 } inductionName
            || forStatement.forCondition()?.expression() is not { } conditionExpression
            || !IsCanonicalExclusiveUpperBoundCondition(conditionExpression, inductionName)
            || forStatement.forIterator()?.expressionList().expression() is not [var iteratorExpression]
            || !IsUnitIncrementExpression(iteratorExpression, inductionName))
        {
            return null;
        }

        return new DynamicInitSliceLoopContext(inductionName);
    }

    private static bool IsCanonicalExclusiveUpperBoundCondition(
        StarkParser.ExpressionContext expression,
        string inductionName)
    {
        if (!TryGetSingleRelationalExpression(expression, out var relational)
            || relational.shiftExpression() is not [var left, var right]
            || ExtractOperators<StarkParser.ShiftExpressionContext>(relational) is not [var op])
        {
            return false;
        }

        return (op == "<" && IsSimpleIdentifierText(left.GetText(), inductionName))
            || (op == ">" && IsSimpleIdentifierText(right.GetText(), inductionName));
    }

    private static bool IsUnitIncrementExpression(
        StarkParser.ExpressionContext expression,
        string inductionName)
    {
        var assignment = expression.assignmentExpression();
        return assignment.assignmentOperator()?.GetText() == "+="
            && TryGetDirectAssignmentTargetName(assignment.unaryExpression(), out var targetName)
            && string.Equals(targetName, inductionName, StringComparison.Ordinal)
            && IsOneExpression(assignment.assignmentExpression());
    }

    private static bool TryGetDirectAssignmentTargetName(
        StarkParser.UnaryExpressionContext target,
        out string name)
    {
        name = string.Empty;
        if (target.unaryOperator() is not null
            || target.powerExpression()?.postfixExpression() is not { } postfix
            || postfix.postfixPart().Length != 0
            || postfix.primaryExpression().Identifier()?.GetText() is not { } identifier)
        {
            return false;
        }

        name = identifier;
        return true;
    }

    private static bool IsZeroExpression(StarkParser.ExpressionContext expression) =>
        string.Equals(NormalizeSimpleExpressionText(expression.GetText()), "0", StringComparison.Ordinal);

    private static bool IsOneExpression(StarkParser.AssignmentExpressionContext expression) =>
        string.Equals(NormalizeSimpleExpressionText(expression.GetText()), "1", StringComparison.Ordinal);

    private static bool IsSimpleIdentifierText(string text, string identifier) =>
        string.Equals(NormalizeSimpleExpressionText(text), identifier, StringComparison.Ordinal);

    private static string NormalizeSimpleExpressionText(string text)
    {
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')' && HasSingleOuterParentheses(text))
        {
            text = text[1..^1];
        }

        return text;
    }

    private static bool HasSingleOuterParentheses(string text)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0 && index != text.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static bool TryGetSingleRelationalExpression(
        StarkParser.ExpressionContext expression,
        out StarkParser.RelationalExpressionContext relational)
    {
        relational = null!;
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return false;
        }

        if (conditional.expression().Length != 0)
        {
            return false;
        }

        var logicalOr = conditional.logicalOrExpression();
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
        if (ExtractOperators<StarkParser.RelationalExpressionContext>(equality).Count != 0
            || equality.relationalExpression().Length != 1)
        {
            return false;
        }

        relational = equality.relationalExpression(0);
        return true;
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
        CheckLocalDeclaration(
            ResolveType(typeContext),
            declarators,
            storageClass,
            isMutable,
            isConstant,
            state,
            signature,
            summary);
    }

    private void CheckLocalDeclaration(
        StarkTypeSymbol declaredType,
        IReadOnlyList<(ITerminalNode Identifier, StarkParser.ExpressionContext? ConstantExpression, StarkParser.VariableInitializerContext? Initializer)> declarators,
        StorageClass storageClass,
        bool isMutable,
        bool isConstant,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {

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
                var variable = state.Declare(new VariableInfo(
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
                summary.DeclareRoot(variable, requiresDrop: IsAutomaticallyDropped(variable.Type, variable.StorageClass));
                RecordDeclaredDynamicStorageState(declarator.Identifier.GetText(), declaredType, value, state, initializer: null);
            }
            else if (declarator.Initializer is { } initializer)
            {
                var value = EvaluateVariableInitializer(initializer, state, signature, summary, declaredType);
                borrowLifetime = InferLifetimeForAssignment(declaredType, value, summary, initializer);
                var variable = state.Declare(new VariableInfo(
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
                summary.DeclareRoot(variable, requiresDrop: IsAutomaticallyDropped(variable.Type, variable.StorageClass));
                RecordDeclaredDynamicStorageState(declarator.Identifier.GetText(), declaredType, value, state, initializer);
                TryRecordDynamicInitSliceState(declarator.Identifier.GetText(), declaredType, initializer, state, summary);
            }
            else
            {
                var variable = state.Declare(new VariableInfo(
                    declarator.Identifier.GetText(),
                    declaredType,
                    storageClass,
                    VariableOrigin.Local,
                    isMutable,
                    isConstant,
                    borrowLifetime,
                    DeclarationLocation: Location(declarator.Identifier.Symbol)),
                    isInitialized: false);
                summary.DeclareRoot(variable, requiresDrop: IsAutomaticallyDropped(variable.Type, variable.StorageClass));
            }
        }
    }

    private static void RecordDeclaredDynamicStorageState(
        string name,
        StarkTypeSymbol declaredType,
        ExpressionInfo value,
        FlowState state,
        StarkParser.VariableInitializerContext? initializer)
    {
        if (declaredType.Kind != StarkTypeKind.Dynamic)
        {
            return;
        }

        state.SetDynamicStoragePrefix(
            name,
            value.DynamicInitializedPrefix
                ?? (IsDynamicObjectCreationInitializer(initializer) ? DynamicStoragePrefixState.Empty : DynamicStoragePrefixState.Unknown));
    }

    private static bool IsDynamicObjectCreationInitializer(StarkParser.VariableInitializerContext? initializer)
    {
        return initializer?.expression() is { } expression
            && TryGetSimpleUnaryExpression(expression) is { } unary
            && unary.powerExpression()?.postfixExpression()?.primaryExpression().objectCreationExpression() is not null;
    }

    private void TryRecordDynamicInitSliceState(
        string name,
        StarkTypeSymbol declaredType,
        StarkParser.VariableInitializerContext initializer,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        if (declaredType.Kind != StarkTypeKind.Slice
            || declaredType.InitializationKind != StarkInitializationKind.Init
            || initializer.expression() is not { } expression
            || TryGetSimpleUnaryExpression(expression) is not { } initUnary
            || initUnary.INIT() is null
            || initUnary.unaryOperator() is not null
            || TryGetSimplePostfixExpression(initUnary.unaryExpression()) is not { } postfix
            || postfix.postfixPart() is not { Length: > 0 } postfixParts
            || postfixParts[^1].expressionList()?.expression() is not [var startExpression, _])
        {
            return;
        }

        if (!TryResolveDynamicStorageRoot(postfix, postfixParts.Length - 1, state, out var root))
        {
            return;
        }

        BigInteger? startOffset = null;
        if (IsDynamicLengthExpression(startExpression, root.RootKey))
        {
            if (state.TryGetDynamicStoragePrefix(root.RootKey, out var existing)
                && existing.InitializedPrefix is { } prefix)
            {
                startOffset = prefix;
            }
        }
        else if (TryEvaluateNonNegativeIntegerLiteral(startExpression, out var start)
            && state.TryGetDynamicStoragePrefix(root.RootKey, out var existing)
            && existing.InitializedPrefix is { } prefix
            && start == prefix)
        {
            startOffset = start;
        }
        else
        {
            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: init slice from dynamic storage '{root.RootKey}[{startExpression.GetText()}, ...]' must start at the current dense initialized prefix. Use '{root.RootKey}.Length' for the spare range start, or use an explicit sparse initialized-slot proof.",
                Location(startExpression.Start));
            return;
        }

        if (state.TryLookup(name, out var local))
        {
            state.SetDynamicInitSliceState(
                local.Id,
                new DynamicInitSliceState(root.RootKey, startOffset, InitializedCount: BigInteger.Zero));
        }
    }

    private bool TryResolveDynamicStorageRoot(
        StarkParser.PostfixExpressionContext postfix,
        int postfixPartCount,
        FlowState state,
        out DynamicStorageRoot root)
    {
        root = default!;
        if (postfix.primaryExpression().Identifier()?.GetText() is not { } rootName
            || !state.TryLookup(rootName, out var variable))
        {
            return false;
        }

        var rootKey = variable.Name;
        var currentType = variable.Type;
        for (var index = 0; index < postfixPartCount; index++)
        {
            var part = postfix.postfixPart(index);
            if (part.Identifier()?.GetText() is not { } memberName
                || !TryResolveField(currentType, memberName, out var field))
            {
                return false;
            }

            rootKey = $"{rootKey}.{memberName}";
            currentType = field.Type;
        }

        if (currentType.Kind != StarkTypeKind.Dynamic)
        {
            return false;
        }

        root = new DynamicStorageRoot(rootKey, currentType);
        return true;
    }

    private bool TryResolveField(StarkTypeSymbol type, string memberName, out FieldSymbol field)
    {
        field = default!;
        if (type.Kind != StarkTypeKind.Named
            || type.NamedType is null
            || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
            || !namedType.Fields.TryGetValue(memberName, out var resolvedField)
            || resolvedField is null)
        {
            return false;
        }

        field = resolvedField;
        return true;
    }

    private StarkTypeSymbol ResolveLocalDeclarationType(
        string declarationKind,
        ParserRuleContext declarationContext,
        StarkParser.Type_Context? typeContext,
        string functionName)
    {
        var key = TemplateLocalDeclarationFacts.BuildLookupKey(
            declarationKind,
            declarationContext.Start.Line,
            declarationContext.Start.Column + 1);
        var typedDeclaration = _typeModel.LocalDeclarations.LastOrDefault(record =>
            string.Equals(record.EnclosingFunctionName, functionName, StringComparison.Ordinal)
            && TemplateLocalDeclarationFacts.BuildLookupKey(record.Kind, record.Location) == key);
        if (typedDeclaration is not null)
        {
            return typedDeclaration.Type;
        }

        return typeContext is not null
            ? ResolveType(typeContext)
            : StarkTypeSymbols.Error;
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
            if (IsTextBufferType(declaredType)
                && TryGetStandaloneInterpolatedTextLiteral(expression) is { } interpolatedLiteral)
            {
                EvaluateFixedTextStorageInterpolation(interpolatedLiteral, state, signature, summary);
                return new ExpressionInfo(declaredType);
            }

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

    private void EvaluateFixedTextStorageInterpolation(
        StarkParser.LiteralContext literal,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        if (literal.StringLiteral() is not { } interpolatedString
            || !InterpolatedText.TryParse(interpolatedString.GetText(), out var segments, out _))
        {
            return;
        }

        foreach (var hole in segments.OfType<InterpolatedTextHoleSegment>())
        {
            EvaluateExpression(hole.Expression, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        }
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

    private bool TryEvaluateRawPointerRegionExpression(
        StarkParser.ExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        if (!TryGetRawPointerRegionExpression(expression, out _, out var startExpression, out var lengthExpression))
        {
            return false;
        }

        EvaluateExpression(startExpression, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        EvaluateExpression(lengthExpression, state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        return true;
    }

    private static bool TryGetRawPointerRegionExpression(
        StarkParser.ExpressionContext expression,
        out string rootName,
        out StarkParser.ExpressionContext startExpression,
        out StarkParser.ExpressionContext lengthExpression)
    {
        rootName = string.Empty;
        startExpression = null!;
        lengthExpression = null!;

        if (TryGetSimplePostfixExpression(expression) is not { } postfix
            || postfix.primaryExpression().Identifier()?.GetText() is not { } identifier
            || postfix.postfixPart() is not [var indexPart]
            || indexPart.LBRACK() is null
            || indexPart.expressionList()?.expression() is not [var start, var length])
        {
            return false;
        }

        rootName = identifier;
        startExpression = start;
        lengthExpression = length;
        return true;
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.ExpressionContext expression)
    {
        return TryGetSimpleUnaryExpression(expression) is { } unary
            ? TryGetSimplePostfixExpression(unary)
            : null;
    }

    private static StarkParser.UnaryExpressionContext? TryGetSimpleUnaryExpression(StarkParser.ExpressionContext expression)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return null;
        }

        if (conditional.expression().Length != 0)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return null;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return null;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return null;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return null;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return null;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return null;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return null;
        }

        var shift = relational.shiftExpression(0);
        if (shift.additiveExpression().Length != 1)
        {
            return null;
        }

        var additive = shift.additiveExpression(0);
        if (additive.multiplicativeExpression().Length != 1)
        {
            return null;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return null;
        }

        return multiplicative.unaryExpression(0);
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.UnaryExpressionContext expression)
    {
        if (expression.unaryOperator() is not null
            || expression.conversionType() is not null
            || expression.unaryExpression() is not null
            || expression.powerExpression() is not { } powerExpression
            || powerExpression.unaryExpression() is not null)
        {
            return null;
        }

        return powerExpression.postfixExpression();
    }

    private ExpressionInfo EvaluateAssignmentExpression(
        StarkParser.AssignmentExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        if (expression.INIT() is not null
            && expression.ASSIGN() is not null
            && expression.assignmentOperator() is null)
        {
            var initTarget = EvaluateUnaryExpression(
                expression.unaryExpression(),
                state,
                signature,
                summary,
                ValueUse.Place,
                allowFunctionReference: true);
            var storageType = StarkTypeSymbols.WithQualifiers(initTarget.Type, initializationKind: StarkInitializationKind.None);
            var initValue = EvaluateAssignmentExpression(
                expression.assignmentExpression(),
                state,
                signature,
                summary,
                ValueUse.ForAssignment(storageType),
                allowFunctionReference: false);

            ApplyAssignment(initTarget, initValue, state, summary, expression.unaryExpression(), isInitializationAssignment: true);
            return initTarget with { BorrowLifetime = initValue.BorrowLifetime, AggregateState = initValue.AggregateState };
        }

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
            ApplyAssignment(left, right, state, summary, expression.unaryExpression(), isInitializationAssignment: false);
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
        ParserRuleContext context,
        bool isInitializationAssignment)
    {
        if (!left.IsPlace)
        {
            return;
        }

        if (left.DynamicStorageAccess is { } dynamicAccess)
        {
            if (isInitializationAssignment)
            {
                MarkDynamicSlotInitialized(dynamicAccess, state, summary);
            }
            else
            {
                EnsureDynamicSlotInitialized(dynamicAccess, state, summary, forReplacement: true);
            }
        }

        if (left.Variable is { } variable)
        {
            if (variable.Origin == VariableOrigin.Global)
            {
                if (IsMoveOnly(left.Type))
                {
                    summary.RecordAssignmentDrop(variable, variable.Name, Location(context.Start));
                }

                return;
            }

            if (left.ProjectionPath is null
                && state.TryGetState(variable.Id, out var variableState)
                && variableState.MayBeInitialized
                && IsAutomaticallyDropped(left.Type, variable.StorageClass))
            {
                RecordAssignmentDrops(variable, variableState, summary, Location(context.Start));
            }

            var borrowLifetime = left.Type.BorrowKind == StarkBorrowKind.None
                ? BorrowLifetime.None
                : right.BorrowLifetime;
            if (left.Type.BorrowKind != StarkBorrowKind.None)
            {
                if (left.ProjectionPath is { Length: > 0 } storedBorrowProjectionPath)
                {
                    ValidateStoredBorrowLifetime(
                        left.Type,
                        right,
                        summary,
                        context,
                        $"stored field '{string.Join(".", storedBorrowProjectionPath)}'");
                }

                ValidateAssignedBorrowLifetime(left, right, state, summary, context);
            }

            if (left.ProjectionPath is { Length: > 0 } projectionPath)
            {
                state.MarkFieldInitialized(variable.Id, projectionPath[0]);
            }
            else
            {
                if (left.ProjectionPath is null
                    && state.TryGetState(variable.Id, out var previousState)
                    && IsReinitializationState(previousState))
                {
                    summary.RecordReinitialization(variable, left.Type, Location(context.Start));
                }

                state.SetInitialized(variable.Id, borrowLifetime, right.AggregateState);
            }

            if (left.Type.Kind == StarkTypeKind.Dynamic)
            {
                if (BuildDynamicRootKey(left) is { } rootKey)
                {
                    state.SetDynamicStoragePrefix(rootKey, right.DynamicInitializedPrefix ?? DynamicStoragePrefixState.Unknown);
                }
            }
        }
    }

    private void MarkDynamicSlotInitialized(
        DynamicStorageIndexAccess access,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        if (access.InitSliceVariableId is { } initSliceVariableId)
        {
            MarkDynamicInitSliceSlotInitialized(access, initSliceVariableId, state, summary);
            return;
        }

        if (IsDynamicLengthExpression(access.IndexExpression, access.RootKey))
        {
            if (state.TryGetDynamicStoragePrefix(access.RootKey, out var current)
                && current.InitializedPrefix is { } prefix)
            {
                state.SetDynamicStoragePrefix(access.RootKey, new DynamicStoragePrefixState(prefix + BigInteger.One));
            }
            else
            {
                state.SetDynamicStoragePrefix(access.RootKey, DynamicStoragePrefixState.Unknown);
            }

            return;
        }

        if (TryEvaluateNonNegativeIntegerLiteral(access.IndexExpression, out var index)
            && state.TryGetDynamicStoragePrefix(access.RootKey, out var stateValue)
            && stateValue.InitializedPrefix is { } initializedPrefix
            && index == initializedPrefix)
        {
            state.SetDynamicStoragePrefix(access.RootKey, new DynamicStoragePrefixState(initializedPrefix + BigInteger.One));
            return;
        }

        if (TryAcceptUnsafeSparseDynamicInitializationProof(access, state))
        {
            return;
        }

        OwnershipError(
            summary,
            "STK4205",
            $"Initialization error: init assignment to dynamic storage '{access.RootKey}[{access.IndexExpression.GetText()}]' must target the next spare slot in the dense initialized prefix. Use '{access.RootKey}.Length' for append, initialize earlier slots first, or use an explicit sparse initialized-slot proof.",
            access.Location);
    }

    private void MarkDynamicInitSliceSlotInitialized(
        DynamicStorageIndexAccess access,
        int initSliceVariableId,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        if (!state.TryGetDynamicInitSliceState(initSliceVariableId, out var initSlice))
        {
            if (TryAcceptUnsafeSparseDynamicInitializationProof(access, state))
            {
                return;
            }

            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: init slice assignment '{access.IndexExpression.GetText()}' has no dynamic storage provenance.",
                access.Location);
            return;
        }

        if (!TryEvaluateNonNegativeIntegerLiteral(access.IndexExpression, out var index))
        {
            if (TryAcceptDynamicInitSliceInductionProof(access, initSliceVariableId, initSlice, state, summary))
            {
                return;
            }

            if (TryAcceptUnsafeSparseDynamicInitializationProof(access, state))
            {
                return;
            }

            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: init slice assignments backed by dynamic storage must be proven in ascending slot order; index '{access.IndexExpression.GetText()}' is not a compile-time slot proof.",
                access.Location);
            return;
        }

        if (initSlice.InitializedCount is not { } initializedCount)
        {
            if (TryAcceptUnsafeSparseDynamicInitializationProof(access, state))
            {
                return;
            }

            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: init slice assignment to '{access.RootKey}' no longer has a compile-time dense slot proof after dynamic loop initialization. Use a fresh initialized slice view for later writes, or use an explicit sparse initialized-slot proof.",
                access.Location);
            return;
        }

        if (index != initializedCount)
        {
            if (TryAcceptUnsafeSparseDynamicInitializationProof(access, state))
            {
                return;
            }

            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: init slice assignment to '{access.RootKey}' expected slot {initializedCount} but found slot {index}. Initialize dynamic spare slots in ascending order, or use an explicit sparse initialized-slot proof.",
                access.Location);
            return;
        }

        var nextCount = initializedCount + BigInteger.One;
        state.SetDynamicInitSliceState(initSliceVariableId, initSlice with { InitializedCount = nextCount });

        if (initSlice.StartOffset is { } startOffset)
        {
            state.SetDynamicStoragePrefix(access.RootKey, new DynamicStoragePrefixState(startOffset + nextCount));
        }
        else
        {
            state.SetDynamicStoragePrefix(access.RootKey, DynamicStoragePrefixState.Unknown);
        }
    }

    private bool TryAcceptDynamicInitSliceInductionProof(
        DynamicStorageIndexAccess access,
        int initSliceVariableId,
        DynamicInitSliceState initSlice,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        var indexText = NormalizeSimpleExpressionText(access.IndexExpression.GetText());
        for (var contextIndex = _dynamicInitSliceLoopContexts.Count - 1; contextIndex >= 0; contextIndex--)
        {
            var context = _dynamicInitSliceLoopContexts[contextIndex];
            if (!string.Equals(context.InductionName, indexText, StringComparison.Ordinal))
            {
                continue;
            }

            if (!context.TryMarkInitialized(initSliceVariableId))
            {
                OwnershipError(
                    summary,
                    "STK4205",
                    $"Initialization error: init slice assignment to '{access.RootKey}[{access.IndexExpression.GetText()}]' repeats the same dynamic loop slot proof. Initialize each dynamic spare slot at most once per canonical independent loop iteration.",
                    access.Location);
                return true;
            }

            state.SetDynamicInitSliceState(initSliceVariableId, initSlice with { InitializedCount = null });
            state.SetDynamicStoragePrefix(access.RootKey, DynamicStoragePrefixState.Unknown);
            return true;
        }

        return false;
    }

    private bool TryAcceptUnsafeSparseDynamicInitializationProof(
        DynamicStorageIndexAccess access,
        FlowState state)
    {
        if (_unsafeDepth == 0)
        {
            return false;
        }

        state.SetDynamicStoragePrefix(access.RootKey, DynamicStoragePrefixState.Unknown);
        return true;
    }

    private bool EnsureDynamicSlotInitialized(
        DynamicStorageIndexAccess access,
        FlowState state,
        FunctionOwnershipBuilder summary,
        bool forReplacement)
    {
        if (access.InitSliceVariableId is not null)
        {
            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: init slice '{access.RootKey}' is write-only; initialized values must be read through an ordinary initialized slice view.",
                access.Location);
            return false;
        }

        if (TryEvaluateNonNegativeIntegerLiteral(access.IndexExpression, out var index)
            && state.TryGetDynamicStoragePrefix(access.RootKey, out var stateValue)
            && stateValue.InitializedPrefix is { } initializedPrefix
            && index < initializedPrefix)
        {
            return true;
        }

        if (_unsafeDepth != 0)
        {
            return true;
        }

        var verb = forReplacement ? "assign to" : "read";
        OwnershipError(
            summary,
            "STK4205",
            $"Initialization error: cannot {verb} dynamic storage slot '{access.RootKey}[{access.IndexExpression.GetText()}]' without a proof that the slot is initialized. Use an initialized slice view for ranges or an explicit sparse initialized-slot proof for sparse data structures.",
            access.Location);
        return false;
    }

    private static bool TryEvaluateNonNegativeIntegerLiteral(
        StarkParser.ExpressionContext expression,
        out BigInteger value)
    {
        value = BigInteger.Zero;
        var text = expression.GetText();
        return text.Length != 0
            && text.All(static ch => ch is >= '0' and <= '9')
            && BigInteger.TryParse(text, out value);
    }

    private static bool IsDynamicLengthExpression(StarkParser.ExpressionContext expression, string rootKey) =>
        string.Equals(expression.GetText(), $"{rootKey}.Length", StringComparison.Ordinal);

    private static string? BuildDynamicRootKey(ExpressionInfo value)
    {
        if (value.Variable is not { } variable
            || value.HasIndexProjection)
        {
            return null;
        }

        return value.ProjectionPath is { Length: > 0 } projectionPath
            ? $"{variable.Name}.{string.Join(".", projectionPath)}"
            : variable.Name;
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

    private void ValidateStoredBorrowLifetime(
        StarkTypeSymbol targetType,
        ExpressionInfo value,
        FunctionOwnershipBuilder summary,
        ParserRuleContext context,
        string destinationDescription)
    {
        if (targetType.BorrowKind != StarkBorrowKind.StoreBorrow)
        {
            return;
        }

        var sourceLifetime = TryInferClosureLambdaBorrowLifetime(targetType, context, out var closureLambdaLifetime)
            ? closureLambdaLifetime
            : value.BorrowLifetime.Kind == BorrowLifetimeKind.None
                ? InferBorrowLifetimeFromValue(value, context.Start)
                : value.BorrowLifetime;
        if (sourceLifetime.Kind == BorrowLifetimeKind.External)
        {
            return;
        }

        var reason = sourceLifetime.Kind switch
        {
            BorrowLifetimeKind.LocalScope => "because it is tied to local scope.",
            BorrowLifetimeKind.Temporary => "because it is tied to a temporary value.",
            BorrowLifetimeKind.Unknown => "because its source lifetime could not be proven.",
            _ => "because its source lifetime does not outlive stored borrow storage."
        };

        OwnershipError(
            summary,
            "STK4202",
            $"Lifetime error: cannot store {DescribeBorrowSource(value with { BorrowLifetime = sourceLifetime })} in {destinationDescription} {reason}",
            context);
        ReportBorrowSourceNote(summary, sourceLifetime);
    }

    private bool TryInferClosureLambdaBorrowLifetime(
        StarkTypeSymbol targetType,
        ParserRuleContext context,
        out BorrowLifetime lifetime)
    {
        if (targetType.Kind == StarkTypeKind.Closure
            && TryFindLambdaExpression(context, out var lambdaExpression))
        {
            lifetime = lambdaExpression.captureClause() is null
                ? BorrowLifetime.ExternalAt(Location(lambdaExpression.Start), "noncapturing closure target")
                : BorrowLifetime.TemporaryAt(Location(lambdaExpression.Start), "capturing closure environment");
            return true;
        }

        lifetime = BorrowLifetime.None;
        return false;
    }

    private static bool TryFindLambdaExpression(IParseTree tree, out StarkParser.LambdaExpressionContext lambdaExpression)
    {
        if (tree is StarkParser.LambdaExpressionContext lambda)
        {
            lambdaExpression = lambda;
            return true;
        }

        for (var index = 0; index < tree.ChildCount; index++)
        {
            if (TryFindLambdaExpression(tree.GetChild(index), out lambdaExpression))
            {
                return true;
            }
        }

        lambdaExpression = null!;
        return false;
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
        var operands = expression.multiplicativeExpression();
        var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(expression);
        if (operators.Count == 0)
        {
            return EvaluateBinaryChain(
                operands,
                item => EvaluateMultiplicativeExpression(item, state, signature, summary, ValueUse.Read, allowFunctionReference),
                state,
                summary,
                use,
                expression);
        }

        var current = EvaluateMultiplicativeExpression(operands[0], state, signature, summary, ValueUse.Read, allowFunctionReference);
        for (var index = 1; index < operands.Length; index++)
        {
            var next = EvaluateMultiplicativeExpression(operands[index], state, signature, summary, ValueUse.Read, allowFunctionReference);
            if (operators[index - 1] == "+"
                && TryApplyRuntimeTextConcatenation(current, next, state, summary, expression, out var runtimeConcat))
            {
                current = runtimeConcat;
                continue;
            }

            current = IsTextType(current.Type) && IsTextType(next.Type) && operators[index - 1] == "+"
                ? new ExpressionInfo(FindCommonTextType(current.Type, next.Type))
                : new ExpressionInfo(FindCommonType(current.Type, next.Type));
        }

        return ApplyUse(current, state, summary, use, expression);
    }

    private bool TryApplyRuntimeTextConcatenation(
        ExpressionInfo left,
        ExpressionInfo right,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ParserRuleContext context,
        out ExpressionInfo result)
    {
        result = default!;

        if ((IsTextBufferType(left.Type) || IsTextBufferType(right.Type))
            && IsTextLikeForConcatenation(left.Type)
            && IsTextLikeForConcatenation(right.Type))
        {
            result = new ExpressionInfo(IsUnicodeConcatSource(left.Type) || IsUnicodeConcatSource(right.Type)
                ? StarkTypeSymbols.Unicode
                : StarkTypeSymbols.Ascii);
            return true;
        }

        if (!IsTextType(left.Type))
        {
            return false;
        }

        var sourceName = left.Type.Kind == StarkTypeKind.Unicode
            ? "System.Text.ConcatUnicode"
            : "System.Text.ConcatAscii";
        if (!TryGetFunctionOverloads(sourceName, out var overloads))
        {
            return false;
        }

        var resolution = FunctionOverloadFacts.Resolve(
            overloads,
            receiverType: null,
            [left.Type, NonNegativeI64Type, right.Type],
            TypeCompatibilityFacts.CanAssign);
        if (!resolution.Succeeded)
        {
            return false;
        }

        var signature = resolution.Match!;
        if (signature.Parameters.Count >= 1)
        {
            ApplyUse(left, state, summary, ValueUse.ForCallArgument(signature.Parameters[0].Type), context);
        }

        if (signature.Parameters.Count >= 3)
        {
            ApplyUse(right, state, summary, ValueUse.ForCallArgument(signature.Parameters[2].Type), context);
        }

        result = new ExpressionInfo(signature.ReturnType);
        return true;
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

    private static StarkParser.LiteralContext? TryGetStandaloneInterpolatedTextLiteral(StarkParser.ExpressionContext expression)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return null;
        }

        if (conditional.expression().Length != 0)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return null;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return null;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return null;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return null;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return null;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return null;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return null;
        }

        var shift = relational.shiftExpression(0);
        if (shift.additiveExpression().Length != 1)
        {
            return null;
        }

        var additive = shift.additiveExpression(0);
        if (additive.multiplicativeExpression().Length != 1)
        {
            return null;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return null;
        }

        var unary = multiplicative.unaryExpression(0);
        if (unary.powerExpression() is not { } power
            || power.unaryExpression() is not null
            || power.postfixExpression() is not { } postfix
            || postfix.postfixPart().Length != 0)
        {
            return null;
        }

        var literal = postfix.primaryExpression().literal();
        return literal?.DOLLAR() is not null && literal.StringLiteral() is not null
            ? literal
            : null;
    }

    private static bool IsTextType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static bool IsTextBufferType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Named
            && type.NamedType is StarkTypeSymbols.OwnedAsciiName or StarkTypeSymbols.OwnedUnicodeName;
    }

    private static bool IsTextLikeForConcatenation(StarkTypeSymbol type)
    {
        return IsTextType(type) || IsTextBufferType(type);
    }

    private static bool IsUnicodeConcatSource(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Unicode
            || type.Kind == StarkTypeKind.Named
                && string.Equals(type.NamedType, StarkTypeSymbols.OwnedUnicodeName, StringComparison.Ordinal);
    }

    private static StarkTypeSymbol FindCommonTextType(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        return left.Kind == StarkTypeKind.Unicode || right.Kind == StarkTypeKind.Unicode
            ? StarkTypeSymbols.Unicode
            : StarkTypeSymbols.Ascii;
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
            summary.RecordAddressTaken(addressOperand, Location(expression.Start));
            var pointerType = StarkTypeSymbols.RawPointer(addressOperand.Type, addressOperand.IsPlace);
            var pointerLifetime = addressOperand.BorrowLifetime.Kind != BorrowLifetimeKind.None
                ? addressOperand.BorrowLifetime
                : InferBorrowLifetimeFromValue(addressOperand, expression.Start);
            return ApplyUse(new ExpressionInfo(pointerType, BorrowLifetime: pointerLifetime), state, summary, use, expression);
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

        if (op == "init")
        {
            return EvaluateUnaryExpression(
                expression.unaryExpression(),
                state,
                signature,
                summary,
                ValueUse.Place,
                allowFunctionReference: false);
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
        var firstUnhandledPostfixIndex = 0;
        ExpressionInfo binding;
        if (TryEvaluateRawSliceConstructionPrefix(expression, state, signature, summary, out var rawSliceBinding, out firstUnhandledPostfixIndex))
        {
            binding = rawSliceBinding;
        }
        else
        {
            var requiresCallableTarget = expression.postfixPart().Any(static part => part.argumentList() is not null);
            var primaryUse = expression.postfixPart().Length == 0
                ? use.Kind == ValueUseKind.Place ? ValueUse.Place : ValueUse.Read
                : ValueUse.ProjectBase;
            binding = EvaluatePrimaryExpression(expression.primaryExpression(), state, signature, summary, primaryUse, allowFunctionReference || requiresCallableTarget);
        }

        var postfixParts = expression.postfixPart();
        for (var index = firstUnhandledPostfixIndex; index < postfixParts.Length; index++)
        {
            var postfixPart = postfixParts[index];
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

            if (index + 1 < postfixParts.Length
                && postfixParts[index + 1].argumentList() is { } memberArguments
                && TryApplyDynamicStorageMemberCall(binding, postfixPart.Identifier().GetText(), memberArguments, state, signature, summary, out var dynamicMemberCall))
            {
                binding = dynamicMemberCall;
                use = ValueUse.Read;
                index++;
                continue;
            }

            binding = ApplyMemberAccess(binding, postfixPart.Identifier().GetText(), summary, postfixPart);
        }

        return ApplyUse(binding, state, summary, use, expression);
    }

    private bool TryEvaluateRawSliceConstructionPrefix(
        StarkParser.PostfixExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        out ExpressionInfo binding,
        out int firstUnhandledPostfixIndex)
    {
        binding = null!;
        firstUnhandledPostfixIndex = 0;
        if (!string.Equals(expression.primaryExpression().Identifier()?.GetText(), "slice", StringComparison.Ordinal)
            || expression.postfixPart().Length == 0
            || expression.postfixPart()[0] is not { } callPart
            || callPart.argumentList() is not { } arguments)
        {
            return false;
        }

        firstUnhandledPostfixIndex = 1;
        var argumentList = arguments.argument();
        if (argumentList.Length != 2)
        {
            binding = new ExpressionInfo(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        var pointer = EvaluateExpression(argumentList[0].expression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);
        EvaluateExpression(argumentList[1].expression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);

        if (pointer.Type.Kind != StarkTypeKind.RawPointer || pointer.Type.ElementType is not { } elementType)
        {
            binding = new ExpressionInfo(StarkTypeSymbols.Error);
            firstUnhandledPostfixIndex = expression.postfixPart().Length;
            return true;
        }

        var sliceType = StarkTypeSymbols.ApplyQualifiers(
            StarkTypeSymbols.Slice(elementType),
            isMutableView: pointer.Type.IsMutablePointer);
        var borrowLifetime = pointer.BorrowLifetime.Kind != BorrowLifetimeKind.None
            ? pointer.BorrowLifetime
            : InferBorrowLifetimeFromValue(pointer, arguments.Start);
        binding = new ExpressionInfo(sliceType, BorrowLifetime: borrowLifetime);
        return true;
    }

    private bool TryApplyDynamicStorageMemberCall(
        ExpressionInfo receiver,
        string memberName,
        StarkParser.ArgumentListContext arguments,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        out ExpressionInfo result)
    {
        result = null!;
        if (receiver.Type.Kind != StarkTypeKind.Dynamic)
        {
            return false;
        }

        if (string.Equals(memberName, "Reserve", StringComparison.Ordinal)
            || string.Equals(memberName, "TryReserve", StringComparison.Ordinal)
            || string.Equals(memberName, "TryReserveCapacity", StringComparison.Ordinal))
        {
            TryEnsureValueAvailable(receiver, state, summary, ValueUse.Read, arguments.Start);
            foreach (var argument in arguments.argument())
            {
                EvaluateExpression(argument.expression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);
            }

            result = new ExpressionInfo(
                string.Equals(memberName, "Reserve", StringComparison.Ordinal)
                    ? StarkTypeSymbols.Void
                    : StarkTypeSymbols.Bool);
            return true;
        }

        if (string.Equals(memberName, "MoveLast", StringComparison.Ordinal)
            || string.Equals(memberName, "MoveAt", StringComparison.Ordinal))
        {
            TryEnsureValueAvailable(receiver, state, summary, ValueUse.Read, arguments.Start);
            foreach (var argument in arguments.argument())
            {
                EvaluateExpression(argument.expression(), state, signature, summary, ValueUse.Read, allowFunctionReference: false);
            }

            if (BuildDynamicRootKey(receiver) is { } rootKey)
            {
                if (state.TryGetDynamicStoragePrefix(rootKey, out var prefixState)
                    && prefixState.InitializedPrefix is { } prefix
                    && prefix > BigInteger.Zero)
                {
                    state.SetDynamicStoragePrefix(rootKey, new DynamicStoragePrefixState(prefix - BigInteger.One));
                }
                else
                {
                    state.SetDynamicStoragePrefix(rootKey, DynamicStoragePrefixState.Unknown);
                }
            }

            result = new ExpressionInfo(receiver.Type.ElementType ?? StarkTypeSymbols.Error);
            return true;
        }

        return false;
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

        if (expression.SIZEOF() is not null || expression.ALIGNOF() is not null)
        {
            _ = ResolveType(expression.type_());
            var resultType = expression.ALIGNOF() is not null
                ? StarkTypeSymbols.Integer(64, BigInteger.One, new BigInteger(long.MaxValue))
                : StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue));
            return ApplyUse(new ExpressionInfo(resultType), state, summary, use, expression);
        }

        if (expression.Identifier() is { } identifier)
        {
            return ResolveValue(identifier.GetText(), identifier.Symbol, state, summary, use, allowFunctionReference);
        }

        if (expression.lambdaExpression() is { } lambdaExpression)
        {
            return EvaluateLambdaExpression(lambdaExpression, state, signature, summary, use);
        }

        if (expression.enumConstructorExpression() is { } enumConstructorExpression)
        {
            var created = EvaluateEnumConstructorExpression(enumConstructorExpression, state, signature, summary);
            return ApplyUse(created, state, summary, use, enumConstructorExpression);
        }

        if (expression.genericEnumCaseReference() is { } genericEnumCaseReference)
        {
            return ResolveGenericMemberReference(genericEnumCaseReference, state, summary, use, allowFunctionReference);
        }

        if (expression.qualifiedName() is { } qualifiedName)
        {
            return ResolveValue(qualifiedName.GetText(), qualifiedName.Start, state, summary, use, allowFunctionReference);
        }

        if (expression.objectCreationExpression() is { } objectCreationExpression)
        {
            var created = EvaluateObjectCreation(objectCreationExpression, state, signature, summary, use);
            return ApplyUse(created, state, summary, use, objectCreationExpression);
        }

        return EvaluateExpression(expression.expression(), state, signature, summary, use, allowFunctionReference: false);
    }

    private ExpressionInfo EvaluateLambdaExpression(
        StarkParser.LambdaExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use)
    {
        EvaluateLambdaCaptureUses(expression, state, summary);

        var targetType = use.TargetType ?? StarkTypeSymbols.Error;
        var borrowLifetime = targetType.Kind == StarkTypeKind.Closure
            && targetType.BorrowKind != StarkBorrowKind.None
            ? expression.captureClause() is null
                ? BorrowLifetime.ExternalAt(Location(expression.Start), "noncapturing closure target")
                : BorrowLifetime.TemporaryAt(Location(expression.Start), "capturing closure environment")
            : BorrowLifetime.None;
        return ApplyUse(new ExpressionInfo(targetType, BorrowLifetime: borrowLifetime), state, summary, use, expression);
    }

    private void EvaluateLambdaCaptureUses(
        StarkParser.LambdaExpressionContext expression,
        FlowState state,
        FunctionOwnershipBuilder summary)
    {
        if (expression.captureClause() is not { } captureClause)
        {
            return;
        }

        foreach (var capture in captureClause.captureBinding())
        {
            var mode = capture.captureMode().GetText();
            var token = capture.Identifier().Symbol;
            var use = mode switch
            {
                "move" => ValueUse.Read,
                "out" or "init" or "addr" => ValueUse.Place,
                _ => ValueUse.Read
            };

            var value = ResolveValue(capture.Identifier().GetText(), token, state, summary, use, allowFunctionReference: false);
            if (string.Equals(mode, "move", StringComparison.Ordinal))
            {
                MarkMoveCapture(value, state, summary, token);
            }
        }
    }

    private void MarkMoveCapture(
        ExpressionInfo value,
        FlowState state,
        FunctionOwnershipBuilder summary,
        IToken token)
    {
        if (value.Variable is null)
        {
            return;
        }

        if (value.Variable.Origin == VariableOrigin.Global)
        {
            OwnershipError(summary, "STK4204", $"Cannot move-capture global or static storage '{value.Variable.Name}'.", token);
            return;
        }

        if (!state.TryGetState(value.Variable.Id, out var stateValue))
        {
            OwnershipError(summary, "STK4200", $"Value '{value.Variable.Name}' is not available in the current flow state.", token);
            return;
        }

        if (!stateValue.IsDefinitelyInitialized)
        {
            return;
        }

        state.SetMoved(value.Variable.Id, value.BorrowLifetime, Location(token));
        summary.RecordMove(value.Variable, value.Type, Location(token));
    }

    private Dictionary<string, FunctionOwnershipSummary> ValidateLambdaFunctions()
    {
        var summaries = new Dictionary<string, FunctionOwnershipSummary>(StringComparer.Ordinal);
        if (_typeModel.Lambdas.Count == 0 && _typeModel.ClosureLambdas.Count == 0)
        {
            return summaries;
        }

        var lambdaContexts = CollectLambdaExpressionsByFunctionName();
        foreach (var lambda in _typeModel.Lambdas)
        {
            if (!lambdaContexts.TryGetValue(lambda.FunctionName, out var expression))
            {
                continue;
            }

            var signature = _signatures.TryGetValue(lambda.FunctionName, out var typedSignature)
                ? typedSignature
                : CallableValueFacts.BuildLambdaSignature(lambda);
            var summary = new FunctionOwnershipBuilder(signature.Name, _typeModel.NamedTypes);
            var state = new FlowState(_typeModel.NamedTypes);
            var functionScope = state.EnterScope();

            for (var index = 0; index < signature.Parameters.Count; index++)
            {
                var parameter = signature.Parameters[index];
                var declarationParameter = index < expression.lambdaParameterList().parameter().Length
                    ? expression.lambdaParameterList().parameter(index)
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
                    DeclarationLocation: declarationParameter is null ? null : Location(declarationParameter.Identifier().Symbol)),
                    isInitialized: true,
                    summary: summary);
                if (parameter.Type.Kind == StarkTypeKind.Dynamic)
                {
                    state.SetDynamicStoragePrefix(parameter.Name, DynamicStoragePrefixState.Unknown);
                }
            }

            if (expression.expression() is { } bodyExpression)
            {
                var value = EvaluateExpression(
                    bodyExpression,
                    state,
                    signature,
                    summary,
                    ValueUse.ForReturn(signature.ReturnType),
                    allowFunctionReference: false);

                if (signature.ReturnType.BorrowKind != StarkBorrowKind.None)
                {
                    ValidateReturnedBorrowLifetime(value, summary, bodyExpression);
                }
            }
            else if (expression.block() is { } block)
            {
                CheckBlock(block, state, signature, summary, openScope: true);
            }

            state.ExitScope(functionScope, summary, ValidateScopeExitState, RecordImplicitDrops);
            summaries[signature.Name] = summary.Build();
        }

        foreach (var lambda in _typeModel.ClosureLambdas)
        {
            if (!lambdaContexts.TryGetValue(lambda.FunctionName, out var expression))
            {
                continue;
            }

            var signature = _signatures.TryGetValue(lambda.FunctionName, out var typedSignature)
                ? typedSignature
                : CallableValueFacts.BuildClosureLambdaSignature(lambda);
            var summary = new FunctionOwnershipBuilder(signature.Name, _typeModel.NamedTypes);
            var state = new FlowState(_typeModel.NamedTypes);
            var functionScope = state.EnterScope();
            var previousClosureWriteContracts = _activeClosureWriteContracts;
            _activeClosureWriteContracts = GetClosureWriteContracts(lambda);

            try
            {
                DeclareLambdaCaptures(state, summary, lambda.Location, lambda.EnclosingFunctionName);
                for (var index = 0; index < signature.Parameters.Count; index++)
                {
                    var parameter = signature.Parameters[index];
                    var sourceParameterIndex = string.Equals(parameter.Name, CallableValueFacts.ClosureEnvironmentParameterName, StringComparison.Ordinal)
                        ? -1
                        : index - 1;
                    var declarationParameter = sourceParameterIndex >= 0
                                               && sourceParameterIndex < expression.lambdaParameterList().parameter().Length
                        ? expression.lambdaParameterList().parameter(sourceParameterIndex)
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
                        DeclarationLocation: declarationParameter is null ? null : Location(declarationParameter.Identifier().Symbol)),
                        isInitialized: true,
                        summary: summary);
                    if (parameter.Type.Kind == StarkTypeKind.Dynamic)
                    {
                        state.SetDynamicStoragePrefix(parameter.Name, DynamicStoragePrefixState.Unknown);
                    }
                }

                if (expression.expression() is { } bodyExpression)
                {
                    var value = EvaluateExpression(
                        bodyExpression,
                        state,
                        signature,
                        summary,
                        ValueUse.ForReturn(signature.ReturnType),
                        allowFunctionReference: false);

                    if (signature.ReturnType.BorrowKind != StarkBorrowKind.None)
                    {
                        ValidateReturnedBorrowLifetime(value, summary, bodyExpression);
                    }

                    ValidateActiveClosureWriteContracts(state, summary, bodyExpression);
                }
                else if (expression.block() is { } block)
                {
                    CheckBlock(block, state, signature, summary, openScope: true);
                    ValidateActiveClosureWriteContracts(state, summary, block);
                }
            }
            finally
            {
                _activeClosureWriteContracts = previousClosureWriteContracts;
            }

            state.ExitScope(functionScope, summary, ValidateScopeExitState, RecordImplicitDrops);
            summaries[signature.Name] = summary.Build();
        }

        return summaries;
    }

    private void DeclareLambdaCaptures(
        FlowState state,
        FunctionOwnershipBuilder summary,
        SourceLocation lambdaLocation,
        string? enclosingFunctionName)
    {
        foreach (var capture in _typeModel.LambdaCaptures.Where(capture =>
                     SameLocation(capture.LambdaLocation, lambdaLocation)
                     && string.Equals(capture.EnclosingFunctionName, enclosingFunctionName, StringComparison.Ordinal)))
        {
            state.Declare(new VariableInfo(
                capture.Name,
                CallableValueFacts.GetLambdaCaptureBodyType(capture.Type, capture.Mode),
                StorageClass.None,
                VariableOrigin.Local,
                IsMutable: CallableValueFacts.LambdaCaptureModeExposesWritableBinding(capture.Mode),
                IsConstant: false,
                BorrowLifetime: capture.Type.BorrowKind == StarkBorrowKind.None
                    ? BorrowLifetime.None
                    : BorrowLifetime.External,
                DeclarationLocation: capture.Location),
                isInitialized: !IsClosureWriteContractCaptureMode(capture.Mode),
                summary: summary);
        }
    }

    private IReadOnlyDictionary<string, ClosureWriteContract>? GetClosureWriteContracts(ClosureLambdaTypingRecord lambda)
    {
        Dictionary<string, ClosureWriteContract>? contracts = null;
        foreach (var capture in _typeModel.LambdaCaptures.Where(capture =>
                     SameLocation(capture.LambdaLocation, lambda.Location)
                     && string.Equals(capture.EnclosingFunctionName, lambda.EnclosingFunctionName, StringComparison.Ordinal)
                     && IsClosureWriteContractCaptureMode(capture.Mode)))
        {
            contracts ??= new Dictionary<string, ClosureWriteContract>(StringComparer.Ordinal);
            contracts[capture.Name] = new ClosureWriteContract(capture.Mode, capture.Location);
        }

        return contracts;
    }

    private void ValidateActiveClosureWriteContracts(
        FlowState state,
        FunctionOwnershipBuilder summary,
        ParserRuleContext context)
    {
        if (_activeClosureWriteContracts is not { Count: > 0 } contracts)
        {
            return;
        }

        foreach (var (name, contract) in contracts)
        {
            if (state.TryLookup(name, out var variable)
                && state.TryGetState(variable.Id, out var variableState)
                && variableState.IsDefinitelyInitialized)
            {
                continue;
            }

            OwnershipError(
                summary,
                "STK4205",
                $"Initialization error: closure capture '{name}' with mode '{contract.Mode}' must be assigned on every successful return path.",
                context);
            OwnershipNote(
                summary,
                "STK4205",
                $"Closure capture '{name}' was declared here.",
                contract.Location);
        }
    }

    private static bool IsClosureWriteContractCaptureMode(string mode)
    {
        return string.Equals(mode, "out", StringComparison.Ordinal)
            || string.Equals(mode, "init", StringComparison.Ordinal);
    }

    private static bool SameLocation(SourceLocation left, SourceLocation right)
    {
        return left.Line == right.Line
            && left.Column == right.Column
            && string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal);
    }

    private Dictionary<string, StarkParser.LambdaExpressionContext> CollectLambdaExpressionsByFunctionName()
    {
        var lambdasByLocation = _typeModel.Lambdas
            .Select(static lambda => (lambda.FunctionName, lambda.Location))
            .Concat(_typeModel.ClosureLambdas.Select(static lambda => (lambda.FunctionName, lambda.Location)))
            .GroupBy(static lambda => $"{lambda.Location.Line}:{lambda.Location.Column}")
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var contexts = new Dictionary<string, StarkParser.LambdaExpressionContext>(StringComparer.Ordinal);

        Collect(_parseResult.Root);
        return contexts;

        void Collect(IParseTree current)
        {
            if (current is StarkParser.LambdaExpressionContext lambdaExpression)
            {
                var key = $"{lambdaExpression.Start.Line}:{lambdaExpression.Start.Column + 1}";
                if (lambdasByLocation.TryGetValue(key, out var matchingLambdas)
                    && matchingLambdas.Length == 1)
                {
                    contexts[matchingLambdas[0].FunctionName] = lambdaExpression;
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index));
            }
        }
    }

    private ExpressionInfo EvaluateObjectCreation(
        StarkParser.ObjectCreationExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary,
        ValueUse use)
    {
        var type = expression.type_() is { } explicitType
            ? ResolveType(explicitType)
            : use.TargetType ?? StarkTypeSymbols.Error;

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

        return new ExpressionInfo(
            type,
            BorrowLifetime: BorrowLifetime.None,
            AggregateState: CreateInitializedAggregateState(type),
            DynamicInitializedPrefix: type.Kind == StarkTypeKind.Dynamic
                ? DynamicStoragePrefixState.Empty
                : null);
    }

    private ExpressionInfo EvaluateEnumConstructorExpression(
        StarkParser.EnumConstructorExpressionContext expression,
        FlowState state,
        TypedFunctionSignature signature,
        FunctionOwnershipBuilder summary)
    {
        if (!TryResolveEnumCaseTarget(expression.enumCaseTarget(), out var enumType, out var enumTypeSymbol, out var variant)
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
            var memberValue = EvaluateVariableInitializer(memberInitializer.variableInitializer(), state, signature, summary, memberType);
            ValidateStoredBorrowLifetime(
                memberType,
                memberValue,
                summary,
                memberInitializer,
                $"stored field '{memberInitializer.Identifier().GetText()}'");
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

        if (TryResolveGlobalBySourceName(name, out var globalType))
        {
            var isMutable = globalType.IsMutable;
            var binding = new ExpressionInfo(
                globalType.Type,
                Variable: new VariableInfo(
                    globalType.Name,
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
                OwnershipError(summary, "STK4204", $"Cannot move out of global or static storage '{globalType.Name}'.", token);
            }

            return binding;
        }

        if (TryGetFunctionOverloads(name, out var functions))
        {
            functions = FilterDirectCallableTypeMemberFunctions(name, functions);
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
            if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
            {
                return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: namedType.Name);
            }

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
            return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: namedType.Name);
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

    private ExpressionInfo ResolveGenericMemberReference(
        StarkParser.GenericEnumCaseReferenceContext genericMemberReference,
        FlowState state,
        FunctionOwnershipBuilder summary,
        ValueUse use,
        bool allowFunctionReference)
    {
        if (TryResolveEnumCaseReference(genericMemberReference, out var enumType, out var enumTypeSymbol, out var variant))
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
                    EnumConstructor: new EnumConstructorBinding(genericMemberReference.GetText(), variant));
            }

            return new ExpressionInfo(StarkTypeSymbols.Error);
        }

        var targetType = ResolveGenericQualifiedName(genericMemberReference.genericQualifiedName());
        var namedType = ResolveNamedTypeSymbol(targetType);
        if (namedType?.Kind is DeclarationKind.Doctrine or DeclarationKind.Trait)
        {
            return ApplyMemberAccess(
                new ExpressionInfo(targetType),
                genericMemberReference.Identifier().GetText(),
                summary,
                genericMemberReference);
        }

        if (namedType is not null && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
        {
            return ApplyMemberAccess(
                new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: targetType.NamedType),
                genericMemberReference.Identifier().GetText(),
                summary,
                genericMemberReference);
        }

        return ResolveValue(
            genericMemberReference.GetText(),
            genericMemberReference.Start,
            state,
            summary,
            use,
            allowFunctionReference);
    }

    private bool TryGetFunctionOverloads(string sourceName, out IReadOnlyList<TypedFunctionSignature> overloads)
    {
        if (_typeModel.Overloads.TryGetValue(sourceName, out overloads!))
        {
            return true;
        }

        if (TryResolveTypeQualifiedMemberSourceName(sourceName, out var resolvedMemberSourceName)
            && _typeModel.Overloads.TryGetValue(resolvedMemberSourceName, out overloads!))
        {
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal)
            && _typeModel.Overloads.TryGetValue($"{_syntaxModel.ModuleName}.{sourceName}", out overloads!))
        {
            return true;
        }

        if (!sourceName.Contains('.', StringComparison.Ordinal))
        {
            var importedCandidates = new List<TypedFunctionSignature>();
            foreach (var candidateName in _moduleGraph.EnumerateAccessibleModuleQualifiedNames(_syntaxModel.ModuleName, sourceName))
            {
                if (_typeModel.Overloads.TryGetValue(candidateName, out var candidates))
                {
                    importedCandidates.AddRange(candidates);
                }
            }

            if (importedCandidates.Count > 0)
            {
                overloads = importedCandidates;
                return true;
            }
        }

        overloads = [];
        return false;
    }

    private bool TryResolveTypeQualifiedMemberSourceName(string sourceName, out string resolvedSourceName)
    {
        resolvedSourceName = string.Empty;
        var separator = sourceName.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var qualifier = sourceName[..separator];
        if (!TryResolveNamedTypeBySourceName(qualifier, out var namedType))
        {
            return false;
        }

        resolvedSourceName = $"{StarkTypeSymbols.GetGenericBaseName(namedType.Name)}.{sourceName[(separator + 1)..]}";
        return !string.Equals(resolvedSourceName, sourceName, StringComparison.Ordinal);
    }

    private IReadOnlyList<TypedFunctionSignature> FilterDirectCallableTypeMemberFunctions(
        string sourceName,
        IReadOnlyList<TypedFunctionSignature> functions)
    {
        return IsStructOrRecordMemberFunctionSourceName(sourceName)
            ? functions.Where(static function => function.IsStatic).ToArray()
            : functions;
    }

    private bool IsStructOrRecordMemberFunctionSourceName(string sourceName)
    {
        var separator = sourceName.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var typeName = sourceName[..separator];
        return TryResolveNamedTypeBySourceName(typeName, out var namedType)
            && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record;
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
                    isInitialized: true,
                    summary: summary);
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
                isInitialized: true,
                summary: summary);
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
        return TryBindResolvedGenericEnumAggregateSwitchPattern(
            aggregatePattern.genericEnumCaseReference(),
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
        if (!IsValueOfEnumType(switchValue.Type, enumType)
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
                isInitialized: true,
                summary: summary);
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

    private bool TryBindResolvedGenericEnumAggregateSwitchPattern(
        StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
        StarkParser.AggregatePatternSuffixContext? suffix,
        ExpressionInfo switchValue,
        FlowState state,
        FunctionOwnershipBuilder summary,
        out bool matched)
    {
        matched = false;
        if (!TryResolveEnumCaseReference(genericEnumCaseReference, out var enumType, out _, out var variant))
        {
            return false;
        }

        matched = true;
        if (!IsValueOfEnumType(switchValue.Type, enumType)
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
                isInitialized: true,
                summary: summary);
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
        if (!TryResolveEnumCaseTarget(enumNamedFieldPattern.enumCaseTarget(), out var enumType, out _, out var variant)
            || !IsValueOfEnumType(switchValue.Type, enumType)
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
                isInitialized: true,
                summary: summary);
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
                isInitialized: true,
                summary: summary);
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
            && ResolveNamedTypeSymbol(switchType) is { } namedType
            && namedType.Kind == DeclarationKind.Enum;
    }

    private static bool IsValueOfEnumType(StarkTypeSymbol type, NamedTypeSymbol enumType)
    {
        return type.Kind == StarkTypeKind.Named
            && type.NamedType is not null
            && string.Equals(StarkTypeSymbols.GetGenericBaseName(type.NamedType), enumType.Name, StringComparison.Ordinal);
    }

    private StarkTypeSymbol ResolveType(StarkParser.Type_Context type)
    {
        return _typeResolver.ResolveType(type, _currentFunctionGenericParameters, _syntaxModel.ModuleName);
    }

    private StarkTypeSymbol ResolveConversionType(StarkParser.ConversionTypeContext type)
    {
        return _typeResolver.ResolveConversionType(type, _currentFunctionGenericParameters, _syntaxModel.ModuleName);
    }

    private static bool SupportsAggregateFieldSubpattern(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.FunctionPointer;
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
                ValueUse.Read,
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
            if (target.Type.Kind == StarkTypeKind.FunctionPointer)
            {
                var parameterTypes = target.Type.FunctionPointerParameterTypes ?? [];
                for (var index = 0; index < argumentValues.Length; index++)
                {
                    var parameterType = index < parameterTypes.Count
                        ? parameterTypes[index]
                        : argumentValues[index].Type;
                    ApplyUse(
                        argumentValues[index],
                        state,
                        summary,
                        ValueUse.ForCallArgument(parameterType),
                        arguments.argument(index));
                }

                return ApplyUse(
                    new ExpressionInfo(target.Type.FunctionPointerReturnType ?? StarkTypeSymbols.Error),
                    state,
                    summary,
                    use,
                    arguments);
            }

            if (target.Type.Kind == StarkTypeKind.Closure)
            {
                var parameterTypes = target.Type.ClosureParameterTypes ?? [];
                for (var index = 0; index < argumentValues.Length; index++)
                {
                    var parameterType = index < parameterTypes.Count
                        ? parameterTypes[index]
                        : argumentValues[index].Type;
                    ApplyUse(
                        argumentValues[index],
                        state,
                        summary,
                        ValueUse.ForCallArgument(parameterType),
                        arguments.argument(index));
                }

                var closureUse = target.Type.ClosureCallCapability == StarkClosureCallCapability.Once
                    ? ValueUse.ConsumeClosure
                    : ValueUse.Read;
                ApplyUse(target, state, summary, closureUse, arguments);

                return ApplyUse(
                    new ExpressionInfo(target.Type.ClosureReturnType ?? StarkTypeSymbols.Error),
                    state,
                    summary,
                    use,
                    arguments);
            }

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
                : target.Function.IsVarargs
                    ? argumentValues[index].Type
                    : StarkTypeSymbols.Error;
            var argumentValue = argumentValues[index];
            var usedArgument = ApplyUse(
                argumentValue,
                state,
                summary,
                ValueUse.ForCallArgument(parameterType),
                arguments.argument(index));

            if (parameterType.BorrowKind != StarkBorrowKind.None)
            {
                borrowArguments.Add(usedArgument.BorrowLifetime);
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
        DynamicStorageIndexAccess? dynamicAccess = null;
        var indexExpressions = expressionList.expression();
        if (indexExpressions.Length == 1)
        {
            if (target.Type.Kind == StarkTypeKind.Dynamic
                && BuildDynamicRootKey(target) is { } dynamicRootKey)
            {
                dynamicAccess = new DynamicStorageIndexAccess(
                    dynamicRootKey,
                    target.Type,
                    indexExpressions[0],
                    Location(expressionList.Start));
            }
            else if (target.Type.Kind == StarkTypeKind.Slice
                && target.Type.InitializationKind == StarkInitializationKind.Init
                && target.Variable is { } initSliceVariable
                && state.TryGetDynamicInitSliceState(initSliceVariable.Id, out var initSlice))
            {
                dynamicAccess = new DynamicStorageIndexAccess(
                    initSlice.RootKey,
                    StarkTypeSymbols.Dynamic(elementType),
                    indexExpressions[0],
                    Location(expressionList.Start),
                    InitSliceVariableId: initSliceVariable.Id);
            }
        }

        return new ExpressionInfo(
            elementType,
            Variable: target.Variable,
            BorrowLifetime: target.BorrowLifetime,
            IsPlace: target.IsPlace,
            IsIndirectPlace: true,
            ProjectionPath: target.ProjectionPath,
            HasIndexProjection: true,
            DynamicStorageAccess: dynamicAccess);
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
                namespaceFunctions = FilterDirectCallableTypeMemberFunctions(qualifiedName, namespaceFunctions);
                return namespaceFunctions.Count == 1 && !namespaceFunctions[0].IsGeneric
                    ? new ExpressionInfo(namespaceFunctions[0].ReturnType, Function: namespaceFunctions[0])
                    : new ExpressionInfo(StarkTypeSymbols.Error, OverloadSourceName: qualifiedName);
            }

            if (TryResolveNamedTypeBySourceName(qualifiedName, out var qualifiedType))
            {
                if (qualifiedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
                {
                    return new ExpressionInfo(StarkTypeSymbols.Error, NamespaceName: qualifiedName);
                }

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

        if (TryApplyValueTextConversionMemberAccess(target, memberName, out var valueTextConversion))
        {
            return valueTextConversion;
        }

        if (target.Type.Kind == StarkTypeKind.Dynamic
            && (string.Equals(memberName, "Length", StringComparison.Ordinal)
                || string.Equals(memberName, "Capacity", StringComparison.Ordinal)))
        {
            return new ExpressionInfo(NonNegativeI64Type, BorrowLifetime: BorrowLifetime.None);
        }

        var namedType = ResolveNamedTypeSymbol(target.Type);

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

        var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(namedType.Name)}.{memberName}";
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
            var instanceMethods = methods.Where(static method => !method.IsStatic).ToArray();
            if (instanceMethods.Length == 1 && !instanceMethods[0].IsGeneric && instanceMethods[0].Parameters.Count != 0)
            {
                return new ExpressionInfo(
                    instanceMethods[0].ReturnType,
                    Function: instanceMethods[0],
                    BorrowLifetime: BorrowLifetime.None,
                    Receiver: target);
            }

            return instanceMethods.Length == 0
                ? new ExpressionInfo(StarkTypeSymbols.Error)
                : new ExpressionInfo(StarkTypeSymbols.Error, OverloadSourceName: methodSourceName, Receiver: target);
        }

        return new ExpressionInfo(
            StarkTypeSymbols.Error);
    }

    private bool TryApplyValueTextConversionMemberAccess(
        ExpressionInfo target,
        string memberName,
        out ExpressionInfo value)
    {
        value = default!;

        if (!TryGetValueTextConversionSourceName(memberName, out var sourceName)
            || !TryGetFunctionOverloads(sourceName, out var overloads))
        {
            return false;
        }

        var candidates = overloads
            .Where(static overload => !overload.IsStatic)
            .Where(overload => overload.Parameters.Count != 0
                && FunctionOverloadFacts.CanBindReceiver(overload.Parameters[0].Type, target.Type, TypeCompatibilityFacts.CanAssign))
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        if (candidates.Length == 1 && !candidates[0].IsGeneric)
        {
            value = new ExpressionInfo(
                candidates[0].ReturnType,
                Function: candidates[0],
                BorrowLifetime: BorrowLifetime.None,
                Receiver: target);
            return true;
        }

        value = new ExpressionInfo(StarkTypeSymbols.Error, OverloadSourceName: sourceName, Receiver: target);
        return true;
    }

    private static bool TryGetValueTextConversionSourceName(string memberName, out string sourceName)
    {
        sourceName = memberName switch
        {
            "ToAscii" => "System.Text.ToAscii",
            "ToUnicode" => "System.Text.ToUnicode",
            _ => string.Empty
        };

        return sourceName.Length != 0;
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

        if (value.DynamicStorageAccess is { } dynamicAccess
            && use.Kind != ValueUseKind.Place
            && !EnsureDynamicSlotInitialized(dynamicAccess, state, summary, forReplacement: false))
        {
            return value;
        }

        if (use.Kind != ValueUseKind.Consume
            || !use.ForceConsume && !IsMoveOnly(value.Type))
        {
            return value;
        }

        if (value.IsIndirectPlace)
        {
            if (value.DynamicStorageAccess is { } dynamicMoveAccess)
            {
                if (_unsafeDepth != 0)
                {
                    state.SetDynamicStoragePrefix(dynamicMoveAccess.RootKey, DynamicStoragePrefixState.Unknown);
                    return value;
                }

                OwnershipError(
                    summary,
                    "STK4203",
                    $"Cannot move a non-tail dynamic storage slot of type '{value.Type.DisplayName}' without an explicit sparse initialized-slot proof. Use MoveLast() for dense-prefix tail moves.",
                    token);
                return value;
            }

            if (value.Variable is { } projectedVariable
                && value.ProjectionPath is { Length: 1 } projectionPath
                && !value.HasIndexProjection)
            {
                state.MarkFieldMoved(projectedVariable.Id, projectionPath[0], value.BorrowLifetime, Location(token));
                summary.RecordMove(projectedVariable, projectionPath[0], value.Type, Location(token));
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
        summary.RecordMove(value, Location(token));
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
            StarkTypeKind.Ascii => false,
            StarkTypeKind.Unicode => false,
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
            summary.RecordImplicitDrop(variable, target);
        }
    }

    private void RecordAssignmentDrops(
        VariableInfo variable,
        VariableState state,
        FunctionOwnershipBuilder summary,
        SourceLocation? location)
    {
        foreach (var target in GetImplicitDropTargets(variable, state))
        {
            summary.RecordAssignmentDrop(variable, target, location);
        }
    }

    private static bool IsReinitializationState(VariableState state)
    {
        return (!state.IsDefinitelyInitialized
                && state.UnavailableKind is UnavailableValueKind.Moved
                    or UnavailableValueKind.PartiallyInitialized
                    or UnavailableValueKind.ControlFlow)
               || (state.AggregateState is { MayHaveAnyAvailableFields: true }
                   && (state.AggregateState.HasDefinitelyUnavailableMovedFields
                       || state.AggregateState.HasDefinitelyUnavailableUninitializedFields));
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

    private bool TryResolveEnumCaseTarget(
        StarkParser.EnumCaseTargetContext enumCaseTarget,
        out NamedTypeSymbol enumType,
        out StarkTypeSymbol enumTypeSymbol,
        out EnumVariantSymbol variant)
    {
        if (enumCaseTarget.genericEnumCaseReference() is { } genericEnumCaseReference)
        {
            return TryResolveEnumCaseReference(genericEnumCaseReference, out enumType, out enumTypeSymbol, out variant);
        }

        return TryResolveEnumCaseReference(enumCaseTarget.dottedName().GetText(), out enumType, out enumTypeSymbol, out variant);
    }

    private bool TryResolveEnumCaseReference(
        StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference,
        out NamedTypeSymbol enumType,
        out StarkTypeSymbol enumTypeSymbol,
        out EnumVariantSymbol variant)
    {
        enumType = null!;
        enumTypeSymbol = StarkTypeSymbols.Error;
        variant = null!;

        enumTypeSymbol = ResolveGenericQualifiedName(genericEnumCaseReference.genericQualifiedName());
        if (ResolveNamedTypeSymbol(enumTypeSymbol) is not { } resolvedEnumType
            || resolvedEnumType.Kind != DeclarationKind.Enum
            || !resolvedEnumType.TryGetVariant(genericEnumCaseReference.Identifier().GetText(), out var resolvedVariant, out _))
        {
            enumTypeSymbol = StarkTypeSymbols.Error;
            return false;
        }

        enumType = resolvedEnumType;
        variant = resolvedVariant;
        return true;
    }

    private StarkTypeSymbol ResolveGenericQualifiedName(StarkParser.GenericQualifiedNameContext genericQualifiedName)
    {
        var baseName = genericQualifiedName.qualifiedName().GetText();
        var baseType = _typeResolver.ResolveQualifiedType(
            baseName,
            _currentFunctionGenericParameters,
            genericQualifiedName.qualifiedName().Start,
            _syntaxModel.ModuleName);
        if (baseType.Kind == StarkTypeKind.Error)
        {
            return StarkTypeSymbols.Error;
        }

        var typeArguments = genericQualifiedName.typeArgumentList().type_()
            .Select(ResolveType)
            .ToArray();
        if (typeArguments.Any(static type => type.Kind == StarkTypeKind.Error))
        {
            return StarkTypeSymbols.Error;
        }

        return StarkTypeSymbols.GenericInstantiation(baseType.NamedType ?? baseName, typeArguments);
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        if (type.NamedType is not { } namedTypeName)
        {
            return null;
        }

        if (_typeModel.NamedTypes.TryGetValue(namedTypeName, out var exact))
        {
            return exact;
        }

        var genericBaseName = StarkTypeSymbols.GetGenericBaseName(namedTypeName);
        return _typeModel.NamedTypes.TryGetValue(genericBaseName, out var genericBase)
            ? genericBase
            : null;
    }

    private bool TryResolveGlobalBySourceName(string name, out TypedGlobalSymbol global)
    {
        if (_typeModel.Globals.TryGetValue(name, out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal)
            && _typeModel.Globals.TryGetValue($"{_syntaxModel.ModuleName}.{name}", out global!))
        {
            return true;
        }

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(_syntaxModel.ModuleName, name)
                .Where(_typeModel.Globals.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                global = _typeModel.Globals[importedMatches[0]];
                return true;
            }
        }

        global = null!;
        return false;
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

        if (!typeName.Contains('.', StringComparison.Ordinal))
        {
            var importedMatches = _moduleGraph.EnumerateAccessibleModuleQualifiedNames(_syntaxModel.ModuleName, typeName)
                .Where(_typeModel.NamedTypes.ContainsKey)
                .ToArray();
            if (importedMatches.Length == 1)
            {
                namedType = _typeModel.NamedTypes[importedMatches[0]];
                return true;
            }
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
        summary.RecordMove(switchValue.Variable, switchValue.Type, Location(token));
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

    private sealed record ClosureWriteContract(string Mode, SourceLocation Location);

    private readonly record struct ValueUse(
        ValueUseKind Kind,
        bool CaptureBorrowLifetime = false,
        StarkTypeSymbol? TargetType = null,
        bool ForceConsume = false)
    {
        public static readonly ValueUse Read = new(ValueUseKind.Read);
        public static readonly ValueUse ConsumeTemporary = new(ValueUseKind.Consume);
        public static readonly ValueUse ConsumeClosure = new(ValueUseKind.Consume, ForceConsume: true);
        public static readonly ValueUse Place = new(ValueUseKind.Place);
        public static readonly ValueUse ProjectBase = new(ValueUseKind.ProjectBase);

        public static ValueUse ForAssignment(StarkTypeSymbol targetType) =>
            targetType.BorrowKind != StarkBorrowKind.None
                ? new(ValueUseKind.Read, CaptureBorrowLifetime: true, TargetType: targetType)
                : targetType.Kind == StarkTypeKind.Slice
                ? new(ValueUseKind.Read, TargetType: targetType)
                : IsMoveOnly(targetType) ? new(ValueUseKind.Consume, TargetType: targetType) : new(ValueUseKind.Read, TargetType: targetType);

        public static ValueUse ForCallArgument(StarkTypeSymbol parameterType) =>
            parameterType.InitializationKind is StarkInitializationKind.Init or StarkInitializationKind.Out
                ? Place
                :
            parameterType.BorrowKind != StarkBorrowKind.None
                ? new(ValueUseKind.Read, CaptureBorrowLifetime: true, TargetType: parameterType)
                : parameterType.Kind is StarkTypeKind.RawPointer or StarkTypeKind.Slice || !IsMoveOnly(parameterType)
                ? new(ValueUseKind.Read, TargetType: parameterType)
                : new(ValueUseKind.Consume, TargetType: parameterType);

        public static ValueUse ForReturn(StarkTypeSymbol returnType) =>
            returnType.BorrowKind != StarkBorrowKind.None
                ? new(ValueUseKind.Read, CaptureBorrowLifetime: true, TargetType: returnType)
                : !IsMoveOnly(returnType)
                ? new(ValueUseKind.Read, TargetType: returnType)
                : new(ValueUseKind.Consume, TargetType: returnType);

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

    private sealed record DynamicStorageIndexAccess(
        string RootKey,
        StarkTypeSymbol StorageType,
        StarkParser.ExpressionContext IndexExpression,
        SourceLocation? Location,
        int? InitSliceVariableId = null);

    private sealed record DynamicInitSliceState(
        string RootKey,
        BigInteger? StartOffset,
        BigInteger? InitializedCount);

    private sealed class DynamicInitSliceLoopContext(string inductionName)
    {
        private readonly HashSet<int> _initializedInitSlices = [];

        public string InductionName { get; } = inductionName;

        public bool TryMarkInitialized(int initSliceVariableId) =>
            _initializedInitSlices.Add(initSliceVariableId);
    }

    private sealed record DynamicStorageRoot(
        string RootKey,
        StarkTypeSymbol Type);

    private sealed record DynamicStoragePrefixState(BigInteger? InitializedPrefix)
    {
        public static readonly DynamicStoragePrefixState Empty = new(BigInteger.Zero);
        public static readonly DynamicStoragePrefixState Unknown = new((BigInteger?)null);

        public bool IsKnown => InitializedPrefix is not null;
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
        AggregateFieldState? AggregateState = null,
        DynamicStorageIndexAccess? DynamicStorageAccess = null,
        DynamicStoragePrefixState? DynamicInitializedPrefix = null)
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
        private readonly Dictionary<string, DynamicStoragePrefixState> _dynamicStorageStates;
        private readonly Dictionary<int, DynamicInitSliceState> _dynamicInitSliceStates;
        private readonly Dictionary<int, ScopeFrame> _scopes;
        private int _nextVariableId;
        private int _nextScopeId;

        public FlowState(IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
        {
            _namedTypes = namedTypes;
            _variables = new Dictionary<int, VariableInfo>();
            _states = new Dictionary<int, VariableState>();
            _dynamicStorageStates = new Dictionary<string, DynamicStoragePrefixState>(StringComparer.Ordinal);
            _dynamicInitSliceStates = new Dictionary<int, DynamicInitSliceState>();
            _scopes = new Dictionary<int, ScopeFrame>();
            CurrentScope = new ScopeFrame(0, null);
            _scopes[0] = CurrentScope;
            _nextScopeId = 1;
        }

        private FlowState(
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            Dictionary<int, VariableInfo> variables,
            Dictionary<int, VariableState> states,
            Dictionary<string, DynamicStoragePrefixState> dynamicStorageStates,
            Dictionary<int, DynamicInitSliceState> dynamicInitSliceStates,
            Dictionary<int, ScopeFrame> scopes,
            ScopeFrame currentScope,
            int nextVariableId,
            int nextScopeId)
        {
            _namedTypes = namedTypes;
            _variables = variables;
            _states = states;
            _dynamicStorageStates = dynamicStorageStates;
            _dynamicInitSliceStates = dynamicInitSliceStates;
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

                summary.ObserveRootState(
                    variable,
                    state,
                    requiresDrop: IsAutomaticallyDropped(variable.Type, variable.StorageClass));
                validateScopeExitState(variable, state, summary);

                if (state.MayBeInitialized && IsAutomaticallyDropped(variable.Type, variable.StorageClass))
                {
                    recordImplicitDrops(variable, state, summary);
                }

                _states.Remove(variableId);
                _dynamicInitSliceStates.Remove(variableId);
                RemoveDynamicStorageStatesForRoot(variable.Name);
                _variables.Remove(variableId);
            }

            CurrentScope = scope.Parent ?? scope;
            _scopes.Remove(scope.Id);
        }

        public VariableInfo Declare(
            VariableInfo variable,
            bool isInitialized,
            AggregateFieldState? aggregateState = null,
            FunctionOwnershipBuilder? summary = null)
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
            summary?.DeclareRoot(bound, requiresDrop: IsAutomaticallyDropped(bound.Type, bound.StorageClass));
            return bound;
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

        public bool TryGetDynamicStoragePrefix(string rootKey, out DynamicStoragePrefixState state) =>
            _dynamicStorageStates.TryGetValue(rootKey, out state!);

        public void SetDynamicStoragePrefix(string rootKey, DynamicStoragePrefixState state)
        {
            _dynamicStorageStates[rootKey] = state;
        }

        public bool TryGetDynamicInitSliceState(int variableId, out DynamicInitSliceState state) =>
            _dynamicInitSliceStates.TryGetValue(variableId, out state!);

        public void SetDynamicInitSliceState(int variableId, DynamicInitSliceState state)
        {
            _dynamicInitSliceStates[variableId] = state;
        }

        private void RemoveDynamicStorageStatesForRoot(string rootName)
        {
            var prefix = $"{rootName}.";
            foreach (var key in _dynamicStorageStates.Keys
                         .Where(key => string.Equals(key, rootName, StringComparison.Ordinal)
                             || key.StartsWith(prefix, StringComparison.Ordinal))
                         .ToArray())
            {
                _dynamicStorageStates.Remove(key);
            }
        }

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
                _dynamicStorageStates.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                _dynamicInitSliceStates.ToDictionary(static pair => pair.Key, static pair => pair.Value),
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

            MergeDynamicStorageStates(thenState, elseState ?? this);
            MergeDynamicInitSliceStates(thenState, elseState ?? this);
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

            MergeDynamicStorageStates(branchList);
            MergeDynamicInitSliceStates(branchList);
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

            MergeDynamicStorageStates(this, loopState);
            MergeDynamicInitSliceStates(this, loopState);
        }

        private void MergeDynamicStorageStates(params FlowState[] branches)
        {
            var snapshots = branches
                .Select(static branch => branch._dynamicStorageStates.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal))
                .ToArray();
            var keys = snapshots.SelectMany(static snapshot => snapshot.Keys).ToHashSet(StringComparer.Ordinal);
            _dynamicStorageStates.Clear();
            foreach (var key in keys)
            {
                DynamicStoragePrefixState? merged = null;
                var first = true;
                foreach (var snapshot in snapshots)
                {
                    var state = snapshot.TryGetValue(key, out var value)
                        ? value
                        : DynamicStoragePrefixState.Unknown;
                    merged = first ? state : MergeDynamicStoragePrefix(merged!, state);
                    first = false;
                }

                if (merged is not null)
                {
                    _dynamicStorageStates[key] = merged;
                }
            }
        }

        private void MergeDynamicInitSliceStates(params FlowState[] branches)
        {
            var snapshots = branches
                .Select(static branch => branch._dynamicInitSliceStates.ToDictionary(static pair => pair.Key, static pair => pair.Value))
                .ToArray();
            var ids = snapshots.SelectMany(static snapshot => snapshot.Keys).ToHashSet();
            _dynamicInitSliceStates.Clear();
            foreach (var id in ids)
            {
                DynamicInitSliceState? merged = null;
                var first = true;
                var compatible = true;
                foreach (var snapshot in snapshots)
                {
                    if (!snapshot.TryGetValue(id, out var state))
                    {
                        compatible = false;
                        break;
                    }

                    if (first)
                    {
                        merged = state;
                        first = false;
                        continue;
                    }

                    if (!string.Equals(merged!.RootKey, state.RootKey, StringComparison.Ordinal)
                        || merged.StartOffset != state.StartOffset)
                    {
                        compatible = false;
                        break;
                    }

                    if (merged.InitializedCount != state.InitializedCount)
                    {
                        merged = merged with { InitializedCount = null };
                    }
                }

                if (compatible && merged is not null)
                {
                    _dynamicInitSliceStates[id] = merged;
                }
            }
        }

        private static DynamicStoragePrefixState MergeDynamicStoragePrefix(
            DynamicStoragePrefixState left,
            DynamicStoragePrefixState right)
        {
            return left.InitializedPrefix is { } leftPrefix
                && right.InitializedPrefix is { } rightPrefix
                && leftPrefix == rightPrefix
                    ? new DynamicStoragePrefixState(leftPrefix)
                    : DynamicStoragePrefixState.Unknown;
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
        private readonly Dictionary<string, OwnershipRootBuilder> _ownershipRoots = new(StringComparer.Ordinal);

        private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;

        public FunctionOwnershipBuilder(string name, IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
        {
            Name = name;
            _namedTypes = namedTypes;
        }

        public string Name { get; }

        public bool OwnershipValid { get; set; } = true;

        public List<string> ImplicitDrops { get; } = [];

        public List<string> Moves { get; } = [];

        public List<OwnershipEventSummary> OwnershipEvents { get; } = [];

        public void DeclareRoot(VariableInfo variable, bool requiresDrop)
        {
            var root = GetOrAddRoot(variable);
            root.RequiresDrop |= requiresDrop;
        }

        public void ObserveRootState(VariableInfo variable, VariableState state, bool requiresDrop)
        {
            var root = GetOrAddRoot(variable);
            root.RequiresDrop |= requiresDrop;
            root.FinalAvailability = ToOwnershipAvailability(state);
            if (state.AggregateState?.HasDefinitelyUnavailableMovedFields == true)
            {
                root.HasPartialMove = true;
            }
        }

        public void RecordMove(ExpressionInfo value, SourceLocation? location)
        {
            if (value.Variable is not { } variable)
            {
                return;
            }

            Moves.Add(value.ProjectionPath is { Length: > 0 } projectionPath
                ? $"{variable.Name}.{projectionPath[0]}"
                : variable.Name);
            var root = GetOrAddRoot(variable);
            root.HasMove = true;
            if (value.ProjectionPath is { Length: > 0 })
            {
                root.HasPartialMove = true;
            }

            OwnershipEvents.Add(new OwnershipEventSummary(
                value.ProjectionPath is { Length: > 0 } ? OwnershipEventKind.FieldMove : OwnershipEventKind.Move,
                BuildPlace(variable, value.Type, value.ProjectionPath, value.HasIndexProjection),
                location));
        }

        public void RecordMove(VariableInfo variable, StarkTypeSymbol type, SourceLocation? location)
        {
            Moves.Add(variable.Name);
            var root = GetOrAddRoot(variable);
            root.HasMove = true;
            OwnershipEvents.Add(new OwnershipEventSummary(
                OwnershipEventKind.Move,
                BuildPlace(variable, type, projectionPath: null, hasIndexProjection: false),
                location));
        }

        public void RecordMove(VariableInfo variable, string fieldName, StarkTypeSymbol type, SourceLocation? location)
        {
            Moves.Add($"{variable.Name}.{fieldName}");
            var root = GetOrAddRoot(variable);
            root.HasMove = true;
            root.HasPartialMove = true;
            OwnershipEvents.Add(new OwnershipEventSummary(
                OwnershipEventKind.FieldMove,
                BuildPlace(variable, type, [fieldName], hasIndexProjection: false),
                location));
        }

        public void RecordImplicitDrop(VariableInfo variable, string target, SourceLocation? location = null)
        {
            ImplicitDrops.Add(target);
            var root = GetOrAddRoot(variable);
            root.HasImplicitDrop = true;
            OwnershipEvents.Add(new OwnershipEventSummary(
                OwnershipEventKind.ImplicitDrop,
                BuildPlace(variable, variable.Type, ParseProjectionPath(variable.Name, target), hasIndexProjection: false),
                location));
        }

        public void RecordAssignmentDrop(VariableInfo variable, string target, SourceLocation? location = null)
        {
            ImplicitDrops.Add(target);
            var root = GetOrAddRoot(variable);
            root.HasImplicitDrop = true;
            root.HasAssignmentDrop = true;
            OwnershipEvents.Add(new OwnershipEventSummary(
                OwnershipEventKind.AssignmentDrop,
                BuildPlace(variable, variable.Type, ParseProjectionPath(variable.Name, target), hasIndexProjection: false),
                location));
        }

        public void RecordReinitialization(VariableInfo variable, StarkTypeSymbol type, SourceLocation? location)
        {
            var root = GetOrAddRoot(variable);
            root.HasReinitialization = true;
            OwnershipEvents.Add(new OwnershipEventSummary(
                OwnershipEventKind.Reinitialize,
                BuildPlace(variable, type, projectionPath: null, hasIndexProjection: false),
                location));
        }

        public void RecordAddressTaken(ExpressionInfo value, SourceLocation? location)
        {
            if (value.Variable is not { } variable)
            {
                return;
            }

            var root = GetOrAddRoot(variable);
            root.IsAddressTaken = true;
            root.HasRawPointerEscape = true;
            OwnershipEvents.Add(new OwnershipEventSummary(
                OwnershipEventKind.AddressTaken,
                BuildPlace(variable, value.Type, value.ProjectionPath, value.HasIndexProjection),
                location));
        }

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
                Moves.ToArray(),
                OwnershipEvents.ToArray(),
                _ownershipRoots.Values
                    .OrderBy(static root => root.Name, StringComparer.Ordinal)
                    .Select(static root => root.Build())
                    .ToArray());
        }

        private OwnershipRootBuilder GetOrAddRoot(VariableInfo variable)
        {
            if (!_ownershipRoots.TryGetValue(variable.Name, out var root))
            {
                root = new OwnershipRootBuilder(variable);
                _ownershipRoots[variable.Name] = root;
            }

            return root;
        }

        private OwnershipPlaceSummary BuildPlace(
            VariableInfo variable,
            StarkTypeSymbol type,
            IReadOnlyList<string>? projectionPath,
            bool hasIndexProjection)
        {
            IReadOnlyList<string> normalizedProjectionPath = projectionPath is null
                ? []
                : projectionPath.ToArray();
            return new OwnershipPlaceSummary(
                variable.Name,
                ResolveProjectedType(variable.Type, normalizedProjectionPath) ?? type,
                normalizedProjectionPath,
                hasIndexProjection);
        }

        private StarkTypeSymbol? ResolveProjectedType(StarkTypeSymbol rootType, IReadOnlyList<string> projectionPath)
        {
            if (projectionPath.Count == 0)
            {
                return rootType;
            }

            var current = rootType;
            foreach (var segment in projectionPath)
            {
                if (current.NamedType is null
                    || !_namedTypes.TryGetValue(current.NamedType, out var namedType))
                {
                    return null;
                }

                if (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record)
                {
                    if (!namedType.Fields.TryGetValue(segment, out var field))
                    {
                        return null;
                    }

                    current = field.Type;
                    continue;
                }

                if (namedType.Kind == DeclarationKind.Enum
                    && namedType.TryGetVariant(segment, out var variant, out _)
                    && variant.Fields.Count == 1)
                {
                    current = variant.Fields[0].Type;
                    continue;
                }

                return null;
            }

            return current;
        }

        private static IReadOnlyList<string> ParseProjectionPath(string rootName, string target)
        {
            if (!target.StartsWith(rootName, StringComparison.Ordinal)
                || target.Length == rootName.Length)
            {
                return [];
            }

            if (target[rootName.Length] != '.')
            {
                return [];
            }

            return target[(rootName.Length + 1)..]
                .Split('.', StringSplitOptions.RemoveEmptyEntries);
        }

        private static OwnershipRootAvailabilityKind ToOwnershipAvailability(VariableState state)
        {
            if (state.IsDefinitelyInitialized)
            {
                return OwnershipRootAvailabilityKind.Initialized;
            }

            if (state.AggregateState is not null && state.AggregateState.MayHaveAnyAvailableFields)
            {
                return OwnershipRootAvailabilityKind.PartiallyInitialized;
            }

            return state.UnavailableKind switch
            {
                UnavailableValueKind.NeverInitialized => OwnershipRootAvailabilityKind.Uninitialized,
                UnavailableValueKind.PartiallyInitialized => OwnershipRootAvailabilityKind.PartiallyInitialized,
                UnavailableValueKind.Moved => OwnershipRootAvailabilityKind.Moved,
                UnavailableValueKind.ControlFlow => OwnershipRootAvailabilityKind.ControlFlow,
                _ => OwnershipRootAvailabilityKind.Unknown
            };
        }
    }

    private sealed class OwnershipRootBuilder
    {
        public OwnershipRootBuilder(VariableInfo variable)
        {
            Name = variable.Name;
            Type = variable.Type;
            RootKind = variable.Origin switch
            {
                VariableOrigin.Parameter => OwnershipStorageRootKind.Parameter,
                VariableOrigin.Global => OwnershipStorageRootKind.Global,
                _ => OwnershipStorageRootKind.Local
            };
            IsMutable = variable.IsMutable;
            IsConstant = variable.IsConstant;
        }

        public string Name { get; }

        public StarkTypeSymbol Type { get; }

        public OwnershipStorageRootKind RootKind { get; }

        public bool IsMutable { get; }

        public bool IsConstant { get; }

        public bool IsAddressTaken { get; set; }

        public bool HasRawPointerEscape { get; set; }

        public bool HasMove { get; set; }

        public bool HasPartialMove { get; set; }

        public bool HasImplicitDrop { get; set; }

        public bool HasAssignmentDrop { get; set; }

        public bool HasReinitialization { get; set; }

        public bool RequiresDrop { get; set; }

        public OwnershipRootAvailabilityKind FinalAvailability { get; set; } = OwnershipRootAvailabilityKind.Unknown;

        public OwnershipRootSummary Build()
        {
            return new OwnershipRootSummary(
                Name,
                Type,
                RootKind,
                IsMutable,
                IsConstant,
                IsAddressTaken,
                HasRawPointerEscape,
                HasMove,
                HasPartialMove,
                HasImplicitDrop,
                HasAssignmentDrop,
                HasReinitialization,
                RequiresDrop,
                FinalAvailability);
        }
    }

    private readonly record struct EmittedOwnershipDiagnosticKey(
        DiagnosticSeverity Severity,
        string Code,
        string Message,
        SourceLocation? Location);
}
