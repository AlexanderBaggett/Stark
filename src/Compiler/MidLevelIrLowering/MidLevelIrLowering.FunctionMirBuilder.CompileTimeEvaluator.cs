using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder
    {
        private sealed class CompileTimeEvaluator
        {
            private readonly FunctionMirBuilder _builder;
            private readonly int _maximumLoopIterations;

            public CompileTimeEvaluator(FunctionMirBuilder builder)
            {
                _builder = builder;
                _maximumLoopIterations = builder._maximumCompileTimeLoopIterations;
            }

            public string? LastFailure { get; private set; }

            public void ClearFailure()
            {
                LastFailure = null;
            }

            public bool TryEvaluateExpression(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string>? activeCalls,
                out CompileTimeConstant constant)
            {
                activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
                var services = CreateServices(moduleName, state, activeCalls);
                return CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant, services);
            }

            public bool TryEvaluateExpressionNode(
                ParserRuleContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string>? activeCalls,
                out CompileTimeConstant constant)
            {
                activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
                var services = CreateServices(moduleName, state, activeCalls);
                return CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant, services);
            }

            public bool TryEvaluateBlock(
                StarkParser.BlockContext block,
                string moduleName,
                StarkTypeSymbol? expectedType,
                out CompileTimeConstant constant)
            {
                return TryEvaluateBlockValue(
                    block,
                    moduleName,
                    _builder._compileTimeConstantState,
                    new HashSet<string>(StringComparer.Ordinal),
                    expectedType ?? StarkTypeSymbols.Error,
                    out constant);
            }

            public bool TryEvaluateInteger(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string>? activeCalls,
                out BigInteger value)
            {
                activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
                var services = CreateServices(moduleName, state, activeCalls);
                return CompileTimeExpressionEvaluator.TryEvaluateInteger(expression, out value, services);
            }

            private CompileTimeEvaluationServices CreateServices(
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls)
            {
                TryResolveCompileTimeIdentifier? nameResolver = state is null
                    ? _builder.TryResolveCompileTimeConstantValue
                    : (string name, out CompileTimeConstant constant) =>
                        state.TryResolve(name, out constant)
                        || _builder.TryResolveCompileTimeConstantValue(name, out constant);
                TryEvaluateCompileTimePostfixExpression postfixResolver =
                    (StarkParser.PostfixExpressionContext postfix, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                        TryEvaluateCompileTimeCall(postfix, moduleName, state, activeCalls, out value)
                        || TryResolvePostfixConstant(postfix, moduleName, state, activeCalls, out value);
                TryEvaluateCompileTimeBlockExpression blockResolver =
                    (StarkParser.BlockContext block, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                    {
                        value = default;
                        return state is not null
                            && TryEvaluateBlockValue(
                                block,
                                moduleName,
                                state,
                                activeCalls,
                                StarkTypeSymbols.Error,
                                out value);
                    };
                TryEvaluateCompileTimeObjectCreationExpression objectCreationResolver =
                    (StarkParser.ObjectCreationExpressionContext objectCreation, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                        TryEvaluateObjectCreation(objectCreation, moduleName, state, activeCalls, out value);
                TryEvaluateCompileTimeEnumConstructorExpression enumConstructorResolver =
                    (StarkParser.EnumConstructorExpressionContext enumConstructor, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                        TryEvaluateEnumConstructor(enumConstructor, moduleName, state, activeCalls, out value);
                TryEvaluateCompileTimeEnumValueExpression enumValueResolver =
                    (ParserRuleContext expression, string caseName, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                        TryEvaluateEnumValue(expression, caseName, out value);
                TryEvaluateCompileTimeTypeLayoutExpression typeLayoutResolver =
                    (StarkParser.PrimaryExpressionContext expression, out CompileTimeConstant value) =>
                        TryEvaluateTypeLayout(expression, moduleName, state, out value);
                TryResolveCompileTimeConversionType conversionTypeResolver =
                    (StarkParser.ConversionTypeContext type, out StarkTypeSymbol resolved) =>
                    {
                        resolved = ResolveLocalConversionType(type, moduleName, state);
                        return resolved.Kind != StarkTypeKind.Error;
                    };
                TryEvaluateCompileTimeTryExpression tryResolver =
                    (StarkParser.UnaryExpressionContext expression, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                        TryEvaluateTryPropagation(expression, moduleName, state, activeCalls, out value);
                return new CompileTimeEvaluationServices(
                    TryResolveIdentifier: nameResolver,
                    TryEvaluatePostfixExpression: postfixResolver,
                    TryEvaluateBlockExpression: blockResolver,
                    TryEvaluateObjectCreationExpression: objectCreationResolver,
                    TryEvaluateEnumConstructorExpression: enumConstructorResolver,
                    TryEvaluateEnumValueExpression: enumValueResolver,
                    TryEvaluateTypeLayoutExpression: typeLayoutResolver,
                    TryResolveConversionType: conversionTypeResolver,
                    TryEvaluateTryExpression: tryResolver);
            }

            private bool TryEvaluateTypeLayout(
                StarkParser.PrimaryExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                out CompileTimeConstant constant)
            {
                constant = default;
                var hasBoundLayout = _builder.TryResolveBoundLayoutQuery(expression, out var boundLayout);
                var targetType = hasBoundLayout
                    ? _builder.ApplyGenericSubstitution(boundLayout.TargetType)
                    : ResolveLocalType(expression.type_(), moduleName, state);
                if (targetType.Kind == StarkTypeKind.Error
                    || ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                        targetType,
                        _builder._typeModel.NamedTypes,
                        _builder._enumLayoutModel.Layouts) is not { } layout)
                {
                    return false;
                }

                var queryKind = hasBoundLayout
                    ? boundLayout.QueryKind
                    : expression.ALIGNOF() is not null
                        ? BoundLayoutQueryKind.AlignOf
                        : BoundLayoutQueryKind.SizeOf;
                var resultType = hasBoundLayout
                    ? _builder.ApplyGenericSubstitution(boundLayout.ResultType)
                    : TypeLayoutQueryFacts.GetResultType(queryKind);
                constant = CompileTimeConstant.Integer(
                    TypeLayoutQueryFacts.GetResultValue(queryKind, layout),
                    resultType);
                return true;
            }

            private StarkTypeSymbol ResolveLocalType(
                StarkParser.Type_Context type,
                string moduleName,
                CompileTimeEvaluationState? state)
            {
                var resolved = _builder._typeResolver.ResolveType(
                    type,
                    state?.GenericParameterNames ?? _builder.ActiveGenericParameterNames(),
                    moduleName,
                    state?.ComptimeGenericParameters ?? _builder.ActiveComptimeGenericParameters());
                resolved = _builder.ApplyGenericSubstitution(resolved);
                return state is null
                    ? resolved
                    : state.SubstituteType(resolved);
            }

            private StarkTypeSymbol ResolveLocalConversionType(
                StarkParser.ConversionTypeContext type,
                string moduleName,
                CompileTimeEvaluationState? state)
            {
                var resolved = _builder._typeResolver.ResolveConversionType(
                    type,
                    state?.GenericParameterNames ?? _builder.ActiveGenericParameterNames(),
                    moduleName,
                    state?.ComptimeGenericParameters ?? _builder.ActiveComptimeGenericParameters());
                resolved = _builder.ApplyGenericSubstitution(resolved);
                return state is null
                    ? resolved
                    : state.SubstituteType(resolved);
            }

            private bool TryResolvePostfixConstant(
                StarkParser.PostfixExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (state is null)
                {
                    return false;
                }

                var parts = new List<string>(expression.postfixPart().Length + 1);
                CompileTimeConstant? current = null;
                if (expression.primaryExpression().Identifier()?.GetText() is { } identifier)
                {
                    parts.Add(identifier);
                }
                else if (expression.primaryExpression().qualifiedName()?.GetText() is { } qualifiedName)
                {
                    parts.Add(qualifiedName);
                }
                else
                {
                    return false;
                }

                foreach (var postfixPart in expression.postfixPart())
                {
                    if (postfixPart.DOT() is not null)
                    {
                        if (postfixPart.Identifier()?.GetText() is not { } memberName)
                        {
                            return false;
                        }

                        if (current is not null)
                        {
                            if (!TryGetNamedAggregateField(current.Value, memberName, out var fieldValue))
                            {
                                return false;
                            }

                            current = fieldValue;
                            parts.Clear();
                            continue;
                        }

                        if (TryResolveNamedConstant(parts, state, out var resolved)
                            && TryGetNamedAggregateField(resolved, memberName, out var projected))
                        {
                            current = projected;
                            parts.Clear();
                            continue;
                        }

                        parts.Add(memberName);
                        continue;
                    }

                    if (postfixPart.expressionList() is { } indexList)
                    {
                        if (current is null)
                        {
                            if (!state.TryResolve(string.Join(".", parts), out var resolved))
                            {
                                return false;
                            }

                            current = resolved;
                        }

                        if (indexList.expression().Length != 1
                            || current.Value.Kind != CompileTimeConstantKind.FixedArray
                            || !TryEvaluateInteger(indexList.expression(0), moduleName, state, activeCalls, out var index)
                            || index < 0
                            || index > int.MaxValue
                            || index >= current.Value.Elements.Count)
                        {
                            return false;
                        }

                        current = current.Value.Elements[(int)index];
                        parts.Clear();
                        continue;
                    }

                    if (postfixPart.argumentList() is not null)
                    {
                        return false;
                    }
                }

                if (current is not null)
                {
                    constant = current.Value;
                    return true;
                }

                var name = string.Join(".", parts);
                return TryEvaluateEnumValue(expression, name, out constant)
                    || TryResolveNamedConstant(parts, state, out constant);
            }

            private bool TryGetNamedAggregateField(
                CompileTimeConstant aggregate,
                string fieldName,
                out CompileTimeConstant fieldValue)
            {
                fieldValue = default;
                if (aggregate.Kind != CompileTimeConstantKind.NamedAggregate
                    || !_builder.TryResolveCompileTimeNamedType(aggregate.Type, out var namedType)
                    || !namedType.TryGetField(fieldName, out _, out var fieldIndex)
                    || fieldIndex < 0
                    || fieldIndex >= aggregate.Elements.Count)
                {
                    return false;
                }

                fieldValue = aggregate.Elements[fieldIndex];
                return true;
            }

            private bool TryResolveNamedConstant(
                IReadOnlyList<string> parts,
                CompileTimeEvaluationState? state,
                out CompileTimeConstant constant)
            {
                var name = string.Join(".", parts);
                return state is not null && state.TryResolve(name, out constant)
                    || _builder.TryResolveCompileTimeConstantValue(name, out constant);
            }

            private bool TryEvaluateCompileTimeCall(
                StarkParser.PostfixExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;

                if (expression.postfixPart().Length == 0
                    || expression.postfixPart()[^1].argumentList() is not { } finalArguments)
                {
                    return false;
                }

                var explicitGenericCall = expression.primaryExpression().genericQualifiedName();
                string? currentName = expression.primaryExpression().Identifier()?.GetText()
                    ?? expression.primaryExpression().genericEnumCaseReference()?.GetText()
                    ?? explicitGenericCall?.qualifiedName().GetText()
                    ?? expression.primaryExpression().qualifiedName()?.GetText();
                if (currentName is null)
                {
                    return false;
                }

                for (var index = 0; index < expression.postfixPart().Length; index++)
                {
                    var postfixPart = expression.postfixPart()[index];
                    if (postfixPart.argumentList() is { } arguments)
                    {
                        if (index != expression.postfixPart().Length - 1)
                        {
                            currentName = null;
                            continue;
                        }

                        return ReferenceEquals(arguments, finalArguments)
                            && ((currentName is not null
                                    && (TryEvaluateStructuralFactCall(
                                            explicitGenericCall,
                                            arguments,
                                            moduleName,
                                            state,
                                            activeCalls,
                                            out constant)
                                        || TryEvaluateEnumCall(
                                            expression,
                                            currentName,
                                            arguments,
                                            moduleName,
                                            state,
                                            activeCalls,
                                            out constant)
                                        || TryEvaluateCallByName(
                                            currentName,
                                            moduleName,
                                            arguments,
                                            state,
                                            activeCalls,
                                            explicitGenericCall,
                                            out constant)))
                                || TryEvaluateReceiverMemberCall(
                                    expression,
                                    index,
                                    arguments,
                                    moduleName,
                                    state,
                                    activeCalls,
                                    out constant));
                    }

                    if (postfixPart.expressionList() is not null)
                    {
                        currentName = null;
                        continue;
                    }

                    var memberName = postfixPart.Identifier()?.GetText();
                    if (memberName is null)
                    {
                        return false;
                    }

                    if (currentName is not null)
                    {
                        currentName = $"{currentName}.{memberName}";
                    }
                }

                return false;
            }

            private bool TryEvaluateReceiverMemberCall(
                StarkParser.PostfixExpressionContext expression,
                int callPartIndex,
                StarkParser.ArgumentListContext arguments,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (callPartIndex <= 0)
                {
                    return false;
                }

                var memberPart = expression.postfixPart()[callPartIndex - 1];
                if (memberPart.DOT() is null
                    || memberPart.Identifier()?.GetText() is not { } memberName
                    || !TryEvaluateReceiverPrefix(
                        expression,
                        callPartIndex - 1,
                        moduleName,
                        state,
                        activeCalls,
                        out var receiver))
                {
                    return false;
                }

                return TryEvaluateMemberCallByReceiver(
                    receiver,
                    memberName,
                    moduleName,
                    arguments,
                    state,
                    activeCalls,
                    out constant);
            }

            private bool TryEvaluateReceiverPrefix(
                StarkParser.PostfixExpressionContext expression,
                int postfixPartCount,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                var services = CreateServices(moduleName, state, activeCalls);
                CompileTimeConstant? current = null;
                var parts = new List<string>();
                if (CompileTimeExpressionEvaluator.TryEvaluate(expression.primaryExpression(), out var primaryConstant, services))
                {
                    current = primaryConstant;
                }
                else if (expression.primaryExpression().Identifier()?.GetText() is { } identifier)
                {
                    parts.Add(identifier);
                }
                else if (expression.primaryExpression().genericEnumCaseReference()?.GetText() is { } genericEnumCase)
                {
                    parts.Add(genericEnumCase);
                }
                else if (expression.primaryExpression().genericQualifiedName()?.qualifiedName().GetText() is { } genericQualifiedName)
                {
                    parts.Add(genericQualifiedName);
                }
                else if (expression.primaryExpression().qualifiedName()?.GetText() is { } qualifiedName)
                {
                    parts.Add(qualifiedName);
                }
                else
                {
                    return false;
                }

                string? pendingMemberCall = null;
                for (var index = 0; index < postfixPartCount; index++)
                {
                    var postfixPart = expression.postfixPart()[index];
                    if (postfixPart.argumentList() is { } arguments)
                    {
                        if (pendingMemberCall is not null)
                        {
                            if (current is not { } receiver
                                || !TryEvaluateMemberCallByReceiver(
                                    receiver,
                                    pendingMemberCall,
                                    moduleName,
                                    arguments,
                                    state,
                                    activeCalls,
                                    out var memberCallValue))
                            {
                                return false;
                            }

                            current = memberCallValue;
                            pendingMemberCall = null;
                            parts.Clear();
                            continue;
                        }

                        if (current is not null || parts.Count == 0)
                        {
                            return false;
                        }

                        var callName = string.Join(".", parts);
                        var explicitGenericCall = parts.Count == 1
                            ? expression.primaryExpression().genericQualifiedName()
                            : null;
                        if (!(TryEvaluateStructuralFactCall(
                                    explicitGenericCall,
                                    arguments,
                                    moduleName,
                                    state,
                                    activeCalls,
                                    out var callValue)
                                || TryEvaluateEnumCall(
                                    expression,
                                    callName,
                                    arguments,
                                    moduleName,
                                    state,
                                    activeCalls,
                                    out callValue)
                                || TryEvaluateCallByName(
                                    callName,
                                    moduleName,
                                    arguments,
                                    state,
                                    activeCalls,
                                    explicitGenericCall,
                                    out callValue)))
                        {
                            return false;
                        }

                        current = callValue;
                        parts.Clear();
                        continue;
                    }

                    if (postfixPart.expressionList() is { } indexList)
                    {
                        if (pendingMemberCall is not null)
                        {
                            return false;
                        }

                        if (current is null)
                        {
                            if (!TryResolveNamedConstant(parts, state, out var resolved))
                            {
                                return false;
                            }

                            current = resolved;
                            parts.Clear();
                        }

                        if (indexList.expression().Length != 1
                            || current.Value.Kind != CompileTimeConstantKind.FixedArray
                            || !TryEvaluateInteger(indexList.expression(0), moduleName, state, activeCalls, out var elementIndex)
                            || elementIndex < 0
                            || elementIndex > int.MaxValue
                            || elementIndex >= current.Value.Elements.Count)
                        {
                            return false;
                        }

                        current = current.Value.Elements[(int)elementIndex];
                        continue;
                    }

                    if (postfixPart.DOT() is null
                        || postfixPart.Identifier()?.GetText() is not { } memberName)
                    {
                        return false;
                    }

                    var nextIsCall = index + 1 < postfixPartCount
                        && expression.postfixPart()[index + 1].argumentList() is not null;
                    if (current is not null)
                    {
                        if (nextIsCall)
                        {
                            pendingMemberCall = memberName;
                            continue;
                        }

                        if (!TryGetNamedAggregateField(current.Value, memberName, out var fieldValue))
                        {
                            return false;
                        }

                        current = fieldValue;
                        continue;
                    }

                    if (parts.Count == 0)
                    {
                        return false;
                    }

                    if (!nextIsCall
                        && TryResolveNamedConstant(parts, state, out var resolvedConstant)
                        && TryGetNamedAggregateField(resolvedConstant, memberName, out var projected))
                    {
                        current = projected;
                        parts.Clear();
                        continue;
                    }

                    parts.Add(memberName);
                }

                if (pendingMemberCall is not null)
                {
                    return false;
                }

                if (current is { } value)
                {
                    constant = value;
                    return true;
                }

                return parts.Count > 0
                    && TryResolveNamedConstant(parts, state, out constant);
            }

            private bool TryEvaluateStructuralFactCall(
                StarkParser.GenericQualifiedNameContext? genericQualifiedName,
                StarkParser.ArgumentListContext arguments,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (genericQualifiedName is null
                    || arguments.argument().Length != 0
                    || !CompileTimeStructuralFacts.TryGetFactKind(
                        genericQualifiedName.qualifiedName().GetText(),
                        out var factKind)
                    || !CompileTimeStructuralFacts.TryResolveArguments(
                        genericQualifiedName.qualifiedName().GetText(),
                        genericQualifiedName,
                        typeArgument => ResolveLocalType(typeArgument, moduleName, state),
                        static (_, _, _) => { },
                        CreateServices(moduleName, state, activeCalls),
                        state?.ComptimeGenericParameters ?? _builder.ActiveComptimeGenericParameters(),
                        state?.ComptimeValueSubstitution ?? _builder._activeGenericValueSubstitution,
                        out var structuralArguments))
                {
                    return false;
                }

                if (structuralArguments.TargetType.Kind == StarkTypeKind.Error
                    || structuralArguments.AdditionalTypeArguments.Any(static argument => argument.Kind == StarkTypeKind.Error)
                    || !ValidateStructuralFactTargetForEvaluation(factKind, structuralArguments, genericQualifiedName.qualifiedName().GetText()))
                {
                    return false;
                }

                if (!CompileTimeStructuralFacts.TryEvaluate(
                        factKind,
                        structuralArguments,
                        type => _builder.TryResolveCompileTimeNamedType(type, out var namedType)
                            ? namedType
                            : null,
                        type => ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                            type,
                            _builder._typeModel.NamedTypes,
                            _builder._enumLayoutModel.Layouts),
                        (target, trait) => _builder.TryResolveCompileTimeTraitConformance(
                            target,
                            trait,
                            moduleName,
                            out var implements)
                                ? implements
                                : null,
                        type => _builder.ResolveCompileTimeMethodSignatures(type, moduleName),
                        out constant))
                {
                    return IsOpenStructuralFactArgument(structuralArguments)
                        && CompileTimeStructuralFacts.TryCreateDefaultConstant(factKind, out constant);
                }

                return true;
            }

            private bool ValidateStructuralFactTargetForEvaluation(
                CompileTimeStructuralFactKind factKind,
                CompileTimeStructuralFactArguments arguments,
                string factName)
            {
                if (!CompileTimeStructuralFacts.RequiresIntegerTarget(factKind)
                    && !CompileTimeStructuralFacts.RequiresFloatTarget(factKind)
                    && !CompileTimeStructuralFacts.RequiresRawPointerTarget(factKind)
                    && !CompileTimeStructuralFacts.RequiresElementTypeTarget(factKind)
                    && !CompileTimeStructuralFacts.RequiresFixedArrayTarget(factKind))
                {
                    return true;
                }

                var targetType = StarkTypeSymbols.WithQualifiers(
                    arguments.TargetType,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
                var requiresInteger = CompileTimeStructuralFacts.RequiresIntegerTarget(factKind);
                var requiresFloat = CompileTimeStructuralFacts.RequiresFloatTarget(factKind);
                var requiresRawPointer = CompileTimeStructuralFacts.RequiresRawPointerTarget(factKind);
                var requiresElementType = CompileTimeStructuralFacts.RequiresElementTypeTarget(factKind);
                var expectedTargetDescription = requiresInteger
                    ? "an integer"
                    : requiresFloat
                        ? "a float"
                        : requiresRawPointer
                            ? "a raw pointer"
                            : requiresElementType
                                ? "an element-bearing"
                                : "a fixed-array";
                var hasExpectedTarget = requiresInteger
                    ? targetType.Kind == StarkTypeKind.Integer && targetType.BitWidth is not null
                    : requiresFloat
                        ? targetType.Kind == StarkTypeKind.Float && targetType.BitWidth is not null
                        : requiresRawPointer
                            ? targetType.Kind == StarkTypeKind.RawPointer
                            : requiresElementType
                                ? targetType.ElementType is not null
                                : targetType.Kind == StarkTypeKind.FixedArray;
                if (hasExpectedTarget || IsOpenNamedStructuralFactTypeArgument(targetType))
                {
                    return true;
                }

                SetFailure($"Compile-time structural fact '{factName}' requires {expectedTargetDescription} type, but found '{arguments.TargetType.DisplayName}'.");
                return false;
            }

            private bool IsOpenStructuralFactArgument(CompileTimeStructuralFactArguments arguments)
            {
                if (arguments.ComptimeValueArguments.Any(static argument => argument.IsSymbolic))
                {
                    return true;
                }

                return IsOpenStructuralFactTypeArgument(arguments.TargetType)
                    || arguments.AdditionalTypeArguments.Any(IsOpenStructuralFactTypeArgument);
            }

            private bool IsOpenStructuralFactTypeArgument(StarkTypeSymbol type)
            {
                var coreType = StarkTypeSymbols.WithQualifiers(
                    type,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);

                if (coreType.Kind == StarkTypeKind.Named
                    && !_builder.TryResolveCompileTimeNamedType(coreType, out _))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(coreType.FixedLengthParameterName))
                {
                    return true;
                }

                if (coreType.TypeArguments is { Count: > 0 }
                    && coreType.TypeArguments.Any(IsOpenStructuralFactTypeArgument))
                {
                    return true;
                }

                if (coreType.ComptimeValueArguments is { Count: > 0 }
                    && coreType.ComptimeValueArguments.Any(static argument => argument.IsSymbolic))
                {
                    return true;
                }

                if (coreType.ElementType is not null
                    && IsOpenStructuralFactTypeArgument(coreType.ElementType))
                {
                    return true;
                }

                if (coreType.AssociatedTypeOwner is not null
                    && IsOpenStructuralFactTypeArgument(coreType.AssociatedTypeOwner))
                {
                    return true;
                }

                if (coreType.Kind == StarkTypeKind.FunctionPointer)
                {
                    return coreType.FunctionPointerReturnType is not null
                            && IsOpenStructuralFactTypeArgument(coreType.FunctionPointerReturnType)
                        || coreType.FunctionPointerParameterTypes is { Count: > 0 }
                            && coreType.FunctionPointerParameterTypes.Any(IsOpenStructuralFactTypeArgument);
                }

                if (coreType.Kind == StarkTypeKind.Closure)
                {
                    return coreType.ClosureReturnType is not null
                            && IsOpenStructuralFactTypeArgument(coreType.ClosureReturnType)
                        || coreType.ClosureParameterTypes is { Count: > 0 }
                            && coreType.ClosureParameterTypes.Any(IsOpenStructuralFactTypeArgument);
                }

                return false;
            }

            private bool IsOpenNamedStructuralFactTypeArgument(StarkTypeSymbol type)
            {
                return IsOpenStructuralFactTypeArgument(type);
            }

            private bool TryEvaluateEnumCall(
                StarkParser.PostfixExpressionContext expression,
                string caseName,
                StarkParser.ArgumentListContext arguments,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (!TryResolveEnumCall(expression, caseName, arguments, out var enumType, out var variant))
                {
                    return false;
                }

                if (variant.UsesNamedFields || variant.Fields.Count != arguments.argument().Length)
                {
                    return false;
                }

                var elements = new CompileTimeConstant[variant.Fields.Count];
                var enumDefinition = _builder.TryResolveCompileTimeNamedType(enumType, out var resolvedEnumDefinition)
                    ? resolvedEnumDefinition
                    : null;
                for (var index = 0; index < variant.Fields.Count; index++)
                {
                    var fieldType = enumDefinition is not null
                        ? SubstituteEnumFieldType(variant.Fields[index].Type, enumDefinition, enumType, state)
                        : _builder.ApplyGenericSubstitution(variant.Fields[index].Type);
                    if (!TryEvaluateExpression(
                            arguments.argument(index).expression(),
                            moduleName,
                            state,
                            activeCalls,
                            out var argumentValue)
                        || !CompileTimeExpressionEvaluator.TryCoerce(argumentValue, fieldType, out elements[index]))
                    {
                        return false;
                    }
                }

                constant = CompileTimeConstant.EnumAggregate(variant.Name, elements, enumType);
                return true;
            }

            private bool TryEvaluateEnumConstructor(
                StarkParser.EnumConstructorExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (!TryResolveEnumConstructor(expression, out var enumType, out var variant, out var boundMembers, out var publishedMembers)
                    || !variant.UsesNamedFields)
                {
                    return false;
                }

                var elements = new CompileTimeConstant[variant.Fields.Count];
                var initializedFields = new HashSet<int>();
                var members = expression.enumConstructorInitializer().enumConstructorMember();
                var enumDefinition = _builder.TryResolveCompileTimeNamedType(enumType, out var resolvedEnumDefinition)
                    ? resolvedEnumDefinition
                    : null;
                for (var memberOrdinal = 0; memberOrdinal < members.Length; memberOrdinal++)
                {
                    var member = members[memberOrdinal];
                    var fieldIndex = -1;
                    var fieldType = StarkTypeSymbols.Error;
                    if (boundMembers is { Count: > 0 } && memberOrdinal < boundMembers.Count)
                    {
                        var boundMember = boundMembers[memberOrdinal];
                        fieldIndex = boundMember.FieldIndex;
                        fieldType = enumDefinition is not null
                            ? SubstituteEnumFieldType(boundMember.FieldType, enumDefinition, enumType, state)
                            : _builder.ApplyGenericSubstitution(boundMember.FieldType);
                    }
                    else if (publishedMembers is { Count: > 0 } && memberOrdinal < publishedMembers.Count)
                    {
                        var publishedMember = publishedMembers[memberOrdinal];
                        fieldIndex = publishedMember.FieldIndex;
                        fieldType = enumDefinition is not null
                            ? SubstituteEnumFieldType(publishedMember.FieldType, enumDefinition, enumType, state)
                            : _builder.ApplyGenericSubstitution(publishedMember.FieldType);
                    }
                    else
                    {
                        var memberName = member.Identifier().GetText();
                        for (var candidate = 0; candidate < variant.Fields.Count; candidate++)
                        {
                            if (!string.Equals(variant.Fields[candidate].Name, memberName, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            fieldIndex = candidate;
                            fieldType = enumDefinition is not null
                                ? SubstituteEnumFieldType(variant.Fields[candidate].Type, enumDefinition, enumType, state)
                                : _builder.ApplyGenericSubstitution(variant.Fields[candidate].Type);
                            break;
                        }
                    }

                    if (fieldIndex < 0
                        || fieldIndex >= elements.Length
                        || !initializedFields.Add(fieldIndex)
                        || !TryEvaluateExpression(member.expression(), moduleName, state, activeCalls, out var memberValue)
                        || !CompileTimeExpressionEvaluator.TryCoerce(memberValue, fieldType, out elements[fieldIndex]))
                    {
                        return false;
                    }
                }

                if (initializedFields.Count != elements.Length)
                {
                    return false;
                }

                constant = CompileTimeConstant.EnumAggregate(variant.Name, elements, enumType);
                return true;
            }

            private StarkTypeSymbol SubstituteEnumFieldType(
                StarkTypeSymbol fieldType,
                NamedTypeSymbol enumDefinition,
                StarkTypeSymbol enumType,
                CompileTimeEvaluationState? state)
            {
                var substituted = SubstitutePropagationPayloadType(fieldType, enumDefinition, enumType);
                substituted = _builder.ApplyGenericSubstitution(substituted);
                return state?.SubstituteType(substituted) ?? substituted;
            }

            private bool TryEvaluateEnumValue(
                ParserRuleContext expression,
                string caseName,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (!TryResolveEnumValue(expression, caseName, out var enumType, out var variant)
                    || variant.Fields.Count != 0)
                {
                    return false;
                }

                constant = CompileTimeConstant.EnumAggregate(variant.Name, [], enumType);
                return true;
            }

            private bool TryResolveEnumCall(
                StarkParser.PostfixExpressionContext expression,
                string caseName,
                StarkParser.ArgumentListContext arguments,
                out StarkTypeSymbol enumType,
                out EnumVariantSymbol variant)
            {
                enumType = StarkTypeSymbols.Error;
                variant = null!;

                if (_builder.TryResolveCompileTimeBoundEnumCall(arguments, out var boundEnumCall))
                {
                    enumType = _builder.ApplyGenericSubstitution(boundEnumCall.EnumType);
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(boundEnumCall.VariantName, out variant, out _);
                }

                if (_builder.TryResolvePublishedEnumCallSummary(arguments, out var publishedEnumCall))
                {
                    enumType = _builder.ApplyGenericSubstitution(publishedEnumCall.EnumType);
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(publishedEnumCall.VariantName, out variant, out _);
                }

                if (expression.primaryExpression().genericEnumCaseReference() is { } genericEnumCaseReference
                    && _builder.TryResolveEnumCaseReference(genericEnumCaseReference, out enumType, out _, out var layoutVariant))
                {
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(layoutVariant.Name, out variant, out _);
                }

                if (_builder.TryResolveEnumCaseReference(caseName, out enumType, out _, out var resolvedLayoutVariant))
                {
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(resolvedLayoutVariant.Name, out variant, out _);
                }

                return false;
            }

            private bool TryResolveEnumConstructor(
                StarkParser.EnumConstructorExpressionContext expression,
                out StarkTypeSymbol enumType,
                out EnumVariantSymbol variant,
                out IReadOnlyList<EnumConstructorMemberTypingRecord>? boundMembers,
                out IReadOnlyList<ImportedTemplateEnumConstructorMemberSummary>? publishedMembers)
            {
                enumType = StarkTypeSymbols.Error;
                variant = null!;
                boundMembers = null;
                publishedMembers = null;

                if (_builder.TryResolveCompileTimeBoundEnumConstruction(expression, out var boundEnumConstruction))
                {
                    enumType = _builder.ApplyGenericSubstitution(boundEnumConstruction.EnumType);
                    boundMembers = boundEnumConstruction.Members;
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(boundEnumConstruction.VariantName, out variant, out _);
                }

                if (_builder.TryGetPublishedEnumConstructorSummary(expression, out var publishedEnumConstructor)
                    && publishedEnumConstructor is not null)
                {
                    enumType = _builder.ApplyGenericSubstitution(publishedEnumConstructor.EnumType);
                    publishedMembers = publishedEnumConstructor.Members;
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(publishedEnumConstructor.VariantName, out variant, out _);
                }

                if (_builder.TryResolveEnumCaseTarget(expression.enumCaseTarget(), out _, out enumType, out _, out var layoutVariant))
                {
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(layoutVariant.Name, out variant, out _);
                }

                return false;
            }

            private bool TryResolveEnumValue(
                ParserRuleContext expression,
                string caseName,
                out StarkTypeSymbol enumType,
                out EnumVariantSymbol variant)
            {
                enumType = StarkTypeSymbols.Error;
                variant = null!;

                if (expression is StarkParser.PrimaryExpressionContext primaryExpression
                    && _builder.TryResolveCompileTimeBoundEnumValue(primaryExpression, out var boundEnumValue))
                {
                    enumType = _builder.ApplyGenericSubstitution(boundEnumValue.EnumType);
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(boundEnumValue.VariantName, out variant, out _);
                }

                if (expression is StarkParser.PrimaryExpressionContext publishedPrimary
                    && _builder.TryResolvePublishedEnumValueSummary(publishedPrimary, out var publishedEnumValue))
                {
                    enumType = _builder.ApplyGenericSubstitution(publishedEnumValue.EnumType);
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(publishedEnumValue.VariantName, out variant, out _);
                }

                if (expression is StarkParser.GenericEnumCaseReferenceContext genericEnumCaseReference
                    && _builder.TryResolveEnumCaseReference(genericEnumCaseReference, out enumType, out _, out var genericLayoutVariant))
                {
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(genericLayoutVariant.Name, out variant, out _);
                }

                if (_builder.TryResolveEnumCaseReference(caseName, out enumType, out _, out var layoutVariant))
                {
                    return _builder.TryResolveCompileTimeNamedType(enumType, out var namedType)
                        && namedType.TryGetVariant(layoutVariant.Name, out variant, out _);
                }

                return false;
            }

            private bool TryEvaluateTryPropagation(
                StarkParser.UnaryExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (state is null
                    || state.CurrentReturnType is not { } returnType
                    || returnType.Kind == StarkTypeKind.Error
                    || !TryEvaluateExpressionNode(
                        expression.unaryExpression(),
                        moduleName,
                        state,
                        activeCalls,
                        out var operand)
                    || operand.Kind != CompileTimeConstantKind.EnumAggregate
                    || operand.VariantName is not { } operandVariantName
                    || !TryResolvePropagationRoles(operand.Type, state, out var operandRoles)
                    || !TryResolvePropagationRoles(returnType, state, out var enclosingRoles))
                {
                    return false;
                }

                if (string.Equals(operandVariantName, operandRoles.OkVariantName, StringComparison.Ordinal))
                {
                    if (operandRoles.SuccessPayloadType is null)
                    {
                        constant = CompileTimeConstant.Void();
                        return true;
                    }

                    if (operand.Elements.Count != 1)
                    {
                        return false;
                    }

                    return CompileTimeExpressionEvaluator.TryCoerce(
                        operand.Elements[0],
                        operandRoles.SuccessPayloadType,
                        out constant);
                }

                if (!string.Equals(operandVariantName, operandRoles.ErrVariantName, StringComparison.Ordinal)
                    || !TryBuildTryErrorReturn(operand, operandRoles, enclosingRoles, returnType, state, out var errorReturn))
                {
                    return false;
                }

                state.SetPendingReturn(errorReturn);
                return false;
            }

            private bool TryBuildTryErrorReturn(
                CompileTimeConstant operand,
                CompileTimePropagationRoles operandRoles,
                CompileTimePropagationRoles enclosingRoles,
                StarkTypeSymbol enclosingReturnType,
                CompileTimeEvaluationState state,
                out CompileTimeConstant errorReturn)
            {
                errorReturn = default;
                if (operandRoles.FailurePayloadType is null && enclosingRoles.FailurePayloadType is null)
                {
                    if (operand.Elements.Count != 0)
                    {
                        return false;
                    }

                    errorReturn = CompileTimeConstant.EnumAggregate(enclosingRoles.ErrVariantName, [], enclosingReturnType);
                    return true;
                }

                if (operandRoles.FailurePayloadType is null
                    || enclosingRoles.FailurePayloadType is null
                    || operand.Elements.Count != 1)
                {
                    return false;
                }

                var operandFailure = operand.Elements[0];
                CompileTimeConstant enclosingFailure;
                if (SameCompileTimeErrorType(operandRoles.FailurePayloadType, enclosingRoles.FailurePayloadType))
                {
                    if (!CompileTimeExpressionEvaluator.TryCoerce(
                            operandFailure,
                            enclosingRoles.FailurePayloadType,
                            out enclosingFailure))
                    {
                        return false;
                    }
                }
                else if (!TryBuildErrorFunnelValue(
                             operandFailure,
                             operandRoles.FailurePayloadType,
                             enclosingRoles.FailurePayloadType,
                             state,
                             out enclosingFailure))
                {
                    return false;
                }

                errorReturn = CompileTimeConstant.EnumAggregate(
                    enclosingRoles.ErrVariantName,
                    [enclosingFailure],
                    enclosingReturnType);
                return true;
            }

            private bool TryBuildErrorFunnelValue(
                CompileTimeConstant operandFailure,
                StarkTypeSymbol operandFailureType,
                StarkTypeSymbol enclosingFailureType,
                CompileTimeEvaluationState state,
                out CompileTimeConstant funnelValue)
            {
                funnelValue = default;
                if (!_builder.TryResolveCompileTimeNamedType(enclosingFailureType, out var enclosingErrorType)
                    || enclosingErrorType.Kind != DeclarationKind.Enum)
                {
                    return false;
                }

                foreach (var variant in enclosingErrorType.Variants)
                {
                    var absorbedType = variant.AbsorbsErrorType is { } absorbed
                        ? state.SubstituteType(_builder.ApplyGenericSubstitution(absorbed))
                        : null;
                    if (absorbedType is null
                        || !SameCompileTimeErrorType(absorbedType, operandFailureType)
                        || variant.Fields.Count != 1)
                    {
                        continue;
                    }

                    var fieldType = state.SubstituteType(_builder.ApplyGenericSubstitution(variant.Fields[0].Type));
                    if (!CompileTimeExpressionEvaluator.TryCoerce(operandFailure, fieldType, out var coercedFailure))
                    {
                        continue;
                    }

                    funnelValue = CompileTimeConstant.EnumAggregate(
                        variant.Name,
                        [coercedFailure],
                        enclosingFailureType);
                    return true;
                }

                return false;
            }

            private bool TryResolvePropagationRoles(
                StarkTypeSymbol type,
                CompileTimeEvaluationState state,
                out CompileTimePropagationRoles roles)
            {
                roles = default;
                if (type.Kind != StarkTypeKind.Named
                    || !_builder.TryResolveCompileTimeNamedType(type, out var namedType)
                    || namedType.Kind != DeclarationKind.Enum
                    || namedType.Variants.Count != 2)
                {
                    return false;
                }

                EnumVariantSymbol? okVariant = null;
                EnumVariantSymbol? errVariant = null;
                foreach (var variant in namedType.Variants)
                {
                    if (variant.Role == EnumVariantRole.Ok)
                    {
                        okVariant = variant;
                    }
                    else if (variant.Role == EnumVariantRole.Err)
                    {
                        errVariant = variant;
                    }
                }

                if (okVariant is null
                    || errVariant is null
                    || okVariant.Fields.Count > 1
                    || errVariant.Fields.Count > 1)
                {
                    return false;
                }

                roles = new CompileTimePropagationRoles(
                    okVariant.Name,
                    errVariant.Name,
                    okVariant.Fields.Count == 1
                        ? state.SubstituteType(_builder.ApplyGenericSubstitution(
                            SubstitutePropagationPayloadType(okVariant.Fields[0].Type, namedType, type)))
                        : null,
                    errVariant.Fields.Count == 1
                        ? state.SubstituteType(_builder.ApplyGenericSubstitution(
                            SubstitutePropagationPayloadType(errVariant.Fields[0].Type, namedType, type)))
                        : null);
                return true;
            }

            private static StarkTypeSymbol SubstitutePropagationPayloadType(
                StarkTypeSymbol payloadType,
                NamedTypeSymbol enumDefinition,
                StarkTypeSymbol instantiatedType)
            {
                if (!enumDefinition.IsGeneric || instantiatedType.TypeArguments is not { Count: > 0 } typeArguments)
                {
                    return payloadType;
                }

                var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
                var genericParameters = enumDefinition.GenericParams;
                for (var index = 0; index < genericParameters.Count && index < typeArguments.Count; index++)
                {
                    substitution[genericParameters[index]] = typeArguments[index];
                }

                return FunctionOverloadFacts.SubstituteType(payloadType, substitution);
            }

            private static bool SameCompileTimeErrorType(StarkTypeSymbol left, StarkTypeSymbol right)
            {
                return string.Equals(
                    left.NamedType ?? left.DisplayName,
                    right.NamedType ?? right.DisplayName,
                    StringComparison.Ordinal);
            }

            private bool TryEvaluateCallByName(
                string functionName,
                string moduleName,
                StarkParser.ArgumentListContext arguments,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                StarkParser.GenericQualifiedNameContext? explicitGenericCall,
                out CompileTimeConstant constant)
            {
                constant = default;

                var argumentConstants = new List<CompileTimeConstant>(arguments.argument().Length);
                foreach (var argument in arguments.argument())
                {
                    if (!TryEvaluateExpression(argument.expression(), moduleName, state, activeCalls, out var argumentConstant))
                    {
                        SetFailure($"Compile-time call to '{functionName}' could not evaluate one of its arguments during MIR lowering.");
                        return false;
                    }

                    argumentConstants.Add(argumentConstant);
                }

                TypedFunctionSignature signature;
                if (explicitGenericCall is not null)
                {
                    if (!TryResolveExplicitGenericCallSignature(
                            functionName,
                            moduleName,
                            explicitGenericCall,
                            argumentConstants,
                            state,
                            activeCalls,
                            out signature))
                    {
                        return false;
                    }
                }
                else if (_builder.TryGetFunctionOverloads(functionName, moduleName, out var overloads))
                {
                    var resolution = FunctionOverloadFacts.Resolve(
                        overloads,
                        receiverType: null,
                        argumentConstants.Select(static argument => argument.Type).ToArray(),
                        TypeCompatibilityFacts.CanAssign);
                    if (!resolution.Succeeded)
                    {
                        return false;
                    }

                    signature = resolution.Match!;
                }
                else if (!_builder.TryResolveFunctionSignature(functionName, moduleName, out signature))
                {
                    return false;
                }

                if (!TryEvaluateResolvedCall(
                    signature,
                    receiver: null,
                    argumentConstants,
                    activeCalls,
                    out constant))
                {
                    SetFailure($"Compile-time call to '{signature.DisplaySourceName}' could not evaluate during MIR lowering.");
                    return false;
                }

                return true;
            }

            private bool TryEvaluateMemberCallByReceiver(
                CompileTimeConstant receiver,
                string memberName,
                string moduleName,
                StarkParser.ArgumentListContext arguments,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;

                var receiverType = StarkTypeSymbols.WithQualifiers(
                    receiver.Type,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
                receiverType = _builder.ApplyGenericSubstitution(receiverType);
                if (receiverType.Kind != StarkTypeKind.Named
                    || receiverType.NamedType is not { } receiverTypeName)
                {
                    return false;
                }

                var argumentConstants = new List<CompileTimeConstant>(arguments.argument().Length);
                foreach (var argument in arguments.argument())
                {
                    if (!TryEvaluateExpression(argument.expression(), moduleName, state, activeCalls, out var argumentConstant))
                    {
                        return false;
                    }

                    argumentConstants.Add(argumentConstant);
                }

                if (!TryResolveConcreteMemberCallSignature(
                        receiverTypeName,
                        memberName,
                        moduleName,
                        receiverType,
                        argumentConstants,
                        out var signature)
                    && !TryResolveTraitDefaultMemberCallSignature(
                        receiverType,
                        memberName,
                        moduleName,
                        argumentConstants,
                        out signature))
                {
                    return false;
                }

                return TryEvaluateResolvedCall(
                    signature,
                    receiver,
                    argumentConstants,
                    activeCalls,
                    out constant);
            }

            private bool TryResolveConcreteMemberCallSignature(
                string receiverTypeName,
                string memberName,
                string moduleName,
                StarkTypeSymbol receiverType,
                IReadOnlyList<CompileTimeConstant> argumentConstants,
                out TypedFunctionSignature signature)
            {
                signature = null!;
                var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(receiverTypeName)}.{memberName}";
                if (!_builder.TryGetFunctionOverloads(methodSourceName, moduleName, out var overloads))
                {
                    return false;
                }

                return TryResolveMemberCallSignature(overloads, receiverType, argumentConstants, requireBody: false, out signature);
            }

            private bool TryResolveTraitDefaultMemberCallSignature(
                StarkTypeSymbol receiverType,
                string memberName,
                string moduleName,
                IReadOnlyList<CompileTimeConstant> argumentConstants,
                out TypedFunctionSignature signature)
            {
                signature = null!;
                if (!_builder.TryResolveCompileTimeNamedTypeInModule(receiverType, moduleName, out var namedType))
                {
                    return false;
                }

                foreach (var traitName in namedType.ImplementedTraits)
                {
                    var methodSourceName = $"{StarkTypeSymbols.GetGenericBaseName(traitName)}.{memberName}";
                    if (_builder.TryGetFunctionOverloads(methodSourceName, moduleName, out var overloads)
                        && TryResolveMemberCallSignature(overloads, receiverType, argumentConstants, requireBody: true, out signature))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool TryResolveMemberCallSignature(
                IReadOnlyList<TypedFunctionSignature> overloads,
                StarkTypeSymbol receiverType,
                IReadOnlyList<CompileTimeConstant> argumentConstants,
                bool requireBody,
                out TypedFunctionSignature signature)
            {
                signature = null!;
                var candidates = overloads
                    .Where(static overload => !overload.IsStatic)
                    .Where(overload => !requireBody || overload.HasBody)
                    .ToArray();
                if (candidates.Length == 0)
                {
                    return false;
                }

                var resolution = FunctionOverloadFacts.Resolve(
                    candidates,
                    receiverType,
                    argumentConstants.Select(static argument => argument.Type).ToArray(),
                    TypeCompatibilityFacts.CanAssign);
                if (!resolution.Succeeded)
                {
                    return false;
                }

                signature = resolution.Match!;
                return true;
            }

            private bool TryEvaluateResolvedCall(
                TypedFunctionSignature signature,
                CompileTimeConstant? receiver,
                IReadOnlyList<CompileTimeConstant> argumentConstants,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (signature.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
                    || !_builder._functionsByName.TryGetValue(signature.Name, out var functionContext)
                    || functionContext.ParsedDeclaration is not { } parsedFunction
                    || !parsedFunction.HasBody
                    || parsedFunction.Body.block() is not { } body
                    || !FunctionKindFacts.IsCompileTimeCallable(parsedFunction.DeclaredKind))
                {
                    return false;
                }

                var receiverOffset = receiver is null ? 0 : 1;
                if (argumentConstants.Count + receiverOffset != signature.Parameters.Count)
                {
                    return false;
                }

                var coercedArguments = new List<CompileTimeConstant>(argumentConstants.Count + receiverOffset);
                if (receiver is { } receiverValue)
                {
                    if (!TryCoerceCompileTimeArgument(receiverValue, signature.Parameters[0].Type, out var coercedReceiver))
                    {
                        return false;
                    }

                    coercedArguments.Add(coercedReceiver);
                }

                for (var index = 0; index < argumentConstants.Count; index++)
                {
                    if (!TryCoerceCompileTimeArgument(argumentConstants[index], signature.Parameters[index + receiverOffset].Type, out var coerced))
                    {
                        return false;
                    }

                    coercedArguments.Add(coerced);
                }

                return TryExecuteFunction(signature, functionContext, body, coercedArguments, activeCalls, out constant);
            }

            private static bool TryCoerceCompileTimeArgument(
                CompileTimeConstant argument,
                StarkTypeSymbol parameterType,
                out CompileTimeConstant coerced)
            {
                if (CompileTimeExpressionEvaluator.TryCoerce(argument, parameterType, out coerced))
                {
                    return true;
                }

                var unqualifiedParameterType = StarkTypeSymbols.WithQualifiers(
                    parameterType,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
                if (unqualifiedParameterType == parameterType
                    || !CompileTimeExpressionEvaluator.TryCoerce(argument, unqualifiedParameterType, out var unqualifiedCoerced))
                {
                    return false;
                }

                return TryRetypeCompileTimeConstant(unqualifiedCoerced, parameterType, out coerced);
            }

            private static bool TryRetypeCompileTimeConstant(
                CompileTimeConstant constant,
                StarkTypeSymbol targetType,
                out CompileTimeConstant retyped)
            {
                switch (constant.Kind)
                {
                    case CompileTimeConstantKind.Integer:
                        retyped = CompileTimeConstant.Integer(constant.IntegerValue, targetType);
                        return true;
                    case CompileTimeConstantKind.Float:
                        retyped = CompileTimeConstant.Float(constant.FloatValue, targetType);
                        return true;
                    case CompileTimeConstantKind.Bool:
                        retyped = CompileTimeConstant.Bool(constant.BoolValue);
                        return true;
                    case CompileTimeConstantKind.Text when constant.TextLiteral is { } text:
                        retyped = CompileTimeConstant.Text(text, targetType);
                        return true;
                    case CompileTimeConstantKind.Null:
                        retyped = CompileTimeConstant.Null(targetType);
                        return true;
                    case CompileTimeConstantKind.FixedArray:
                        retyped = CompileTimeConstant.FixedArray(constant.Elements, targetType);
                        return true;
                    case CompileTimeConstantKind.NamedAggregate:
                        retyped = CompileTimeConstant.NamedAggregate(constant.Elements, targetType);
                        return true;
                    case CompileTimeConstantKind.EnumAggregate when constant.VariantName is { } variantName:
                        retyped = CompileTimeConstant.EnumAggregate(variantName, constant.Elements, targetType);
                        return true;
                    default:
                        retyped = default;
                        return false;
                }
            }

            private bool TryResolveExplicitGenericCallSignature(
                string functionName,
                string moduleName,
                StarkParser.GenericQualifiedNameContext genericQualifiedName,
                IReadOnlyList<CompileTimeConstant> argumentConstants,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out TypedFunctionSignature signature)
            {
                signature = null!;
                IReadOnlyList<TypedFunctionSignature> overloads;
                if (_builder.TryGetFunctionOverloads(functionName, moduleName, out var resolvedOverloads))
                {
                    overloads = resolvedOverloads;
                }
                else if (_builder.TryResolveFunctionSignature(functionName, moduleName, out var singleSignature))
                {
                    overloads = [singleSignature];
                }
                else
                {
                    return false;
                }

                var syntaxArgumentCount = genericQualifiedName.typeArgumentList().genericArgument().Length;
                var instantiatedCandidates = new List<TypedFunctionSignature>();
                foreach (var candidate in overloads)
                {
                    if (candidate.GenericParams.Count + candidate.ComptimeGenericParams.Count != syntaxArgumentCount)
                    {
                        continue;
                    }

                    var genericArguments = GenericArgumentSyntaxFacts.Resolve(
                        genericQualifiedName.typeArgumentList(),
                        candidate.GenericParams,
                        candidate.ComptimeGenericParams,
                        typeArgument => ResolveLocalType(typeArgument, moduleName, state),
                        static (_, _, _) => { },
                        CreateServices(moduleName, state, activeCalls),
                        state?.ComptimeGenericParameters ?? _builder.ActiveComptimeGenericParameters());
                    if (genericArguments.TypeArguments.Count != candidate.GenericParams.Count
                        || genericArguments.TypeArguments.Any(static type => type.Kind == StarkTypeKind.Error)
                        || genericArguments.ComptimeValueArguments.Count != candidate.ComptimeGenericParams.Count)
                    {
                        continue;
                    }

                    var valueArguments = FunctionOverloadFacts.SubstituteComptimeValues(
                        genericArguments.ComptimeValueArguments,
                        state?.ComptimeValueSubstitution ?? _builder._activeGenericValueSubstitution);
                    instantiatedCandidates.Add(FunctionOverloadFacts.InstantiateSignature(
                        candidate,
                        genericArguments.TypeArguments,
                        candidate.Name,
                        associatedTypeResolver: null,
                        valueArguments));
                }

                if (instantiatedCandidates.Count == 0)
                {
                    return false;
                }

                var resolution = FunctionOverloadFacts.Resolve(
                    instantiatedCandidates,
                    receiverType: null,
                    argumentConstants.Select(static argument => argument.Type).ToArray(),
                    TypeCompatibilityFacts.CanAssign);
                if (!resolution.Succeeded)
                {
                    return false;
                }

                signature = resolution.Match!;
                return true;
            }

            private bool TryExecuteFunction(
                TypedFunctionSignature signature,
                FunctionLoweringContext functionContext,
                StarkParser.BlockContext body,
                IReadOnlyList<CompileTimeConstant> arguments,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;

                if (!activeCalls.Add(signature.Name))
                {
                    return false;
                }

                var state = new CompileTimeEvaluationState();
                InitializeGenericContext(signature, functionContext, state);
                state.SetCurrentReturnType(signature.ReturnType);
                state.PushScope();
                try
                {
                    DeclareComptimeGenericValues(signature, state);
                    for (var index = 0; index < signature.Parameters.Count; index++)
                    {
                        state.Declare(signature.Parameters[index].Name, arguments[index], isMutable: false);
                    }

                    if (!TryExecuteBlock(body, functionContext.ModuleName, state, activeCalls, signature.ReturnType, out var flow, out var returnValue))
                    {
                        SetFailure($"Compile-time evaluation could not execute every required statement in '{signature.DisplaySourceName}' during MIR lowering.");
                        return false;
                    }

                    if (flow != CompileTimeStatementFlow.Return || signature.ReturnType.Kind == StarkTypeKind.Void)
                    {
                        SetFailure($"Compile-time evaluation of '{signature.DisplaySourceName}' did not produce a return value during MIR lowering.");
                        return false;
                    }

                    if (!CompileTimeExpressionEvaluator.TryCoerce(returnValue, signature.ReturnType, out constant))
                    {
                        SetFailure($"Compile-time evaluation of '{signature.DisplaySourceName}' produced '{returnValue.Type.DisplayName}', which cannot be used as '{signature.ReturnType.DisplayName}' during MIR lowering.");
                        return false;
                    }

                    return true;
                }
                finally
                {
                    state.PopScope();
                    activeCalls.Remove(signature.Name);
                }
            }

            private void SetFailure(string message)
            {
                LastFailure ??= message;
            }

            private void InitializeGenericContext(
                TypedFunctionSignature signature,
                FunctionLoweringContext functionContext,
                CompileTimeEvaluationState state)
            {
                if (signature.TypeArguments is not { Count: > 0 }
                    && signature.ComptimeValueArguments is not { Count: > 0 })
                {
                    return;
                }

                var template = signature.TemplateName is { } templateName
                    && _builder.TryResolveFunctionSignature(templateName, functionContext.ModuleName, out var templateSignature)
                        ? templateSignature
                        : signature;

                var genericNames = template.GenericParams;
                IReadOnlyDictionary<string, StarkTypeSymbol>? typeSubstitution = null;
                if (genericNames.Count > 0 && signature.TypeArguments is { Count: > 0 } typeArguments)
                {
                    var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
                    for (var index = 0; index < genericNames.Count && index < typeArguments.Count; index++)
                    {
                        substitution[genericNames[index]] = typeArguments[index];
                    }

                    typeSubstitution = substitution;
                }

                var comptimeParameters = template.ComptimeGenericParams;
                IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeParameterMap = null;
                IReadOnlyDictionary<string, BigInteger>? valueSubstitution = null;
                if (comptimeParameters.Count > 0)
                {
                    comptimeParameterMap = comptimeParameters.ToDictionary(
                        static parameter => parameter.Name,
                        StringComparer.Ordinal);
                    if (signature.ComptimeValueArguments is { Count: > 0 } valueArguments)
                    {
                        var values = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
                        for (var index = 0; index < comptimeParameters.Count && index < valueArguments.Count; index++)
                        {
                            if (!valueArguments[index].IsSymbolic)
                            {
                                values[comptimeParameters[index].Name] = valueArguments[index].IntegerValue;
                            }
                        }

                        valueSubstitution = values.Count == 0 ? null : values;
                    }
                }

                state.SetGenericContext(
                    genericNames.Count == 0 ? null : genericNames.ToHashSet(StringComparer.Ordinal),
                    typeSubstitution,
                    comptimeParameterMap,
                    valueSubstitution);
            }

            private static void DeclareComptimeGenericValues(
                TypedFunctionSignature signature,
                CompileTimeEvaluationState state)
            {
                if (state.ComptimeGenericParameters is not { Count: > 0 }
                    || (state.ComptimeValueSubstitution is not { Count: > 0 }
                        && !signature.ComptimeValues.Any(static value => value.IsSymbolic)))
                {
                    return;
                }

                foreach (var parameter in state.ComptimeGenericParameters.Values)
                {
                    if (state.ComptimeValueSubstitution?.TryGetValue(parameter.Name, out var value) != true)
                    {
                        var symbolicValue = signature.ComptimeValues.FirstOrDefault(value =>
                            value.IsSymbolic
                            && string.Equals(value.ParameterName, parameter.Name, StringComparison.Ordinal));
                        if (symbolicValue is not null)
                        {
                            state.Declare(
                                parameter.Name,
                                CompileTimeConstant.SymbolicInteger(state.SubstituteType(parameter.Type)),
                                isMutable: false);
                        }

                        continue;
                    }

                    state.Declare(
                        parameter.Name,
                        CompileTimeConstant.Integer(value, state.SubstituteType(parameter.Type)),
                        isMutable: false);
                }
            }

            private bool TryEvaluateBlockValue(
                StarkParser.BlockContext block,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeConstant constant)
            {
                constant = default;
                var previousReturnType = returnType.Kind == StarkTypeKind.Error
                    ? state.CurrentReturnType
                    : state.SetCurrentReturnType(returnType);
                try
                {
                    if (!TryExecuteBlock(block, moduleName, state, activeCalls, returnType, out var flow, out var returnValue)
                        || flow != CompileTimeStatementFlow.Return
                        || returnValue.Type.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
                    {
                        return false;
                    }

                    if (returnType.Kind != StarkTypeKind.Error)
                    {
                        return CompileTimeExpressionEvaluator.TryCoerce(returnValue, returnType, out constant);
                    }

                    constant = returnValue;
                    return true;
                }
                finally
                {
                    if (returnType.Kind != StarkTypeKind.Error)
                    {
                        state.SetCurrentReturnType(previousReturnType);
                    }
                }
            }

            private bool TryExecuteBlock(
                StarkParser.BlockContext block,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;
                state.PushScope();
                try
                {
                    foreach (var statement in block.statement())
                    {
                        if (!TryExecuteStatement(statement, moduleName, state, activeCalls, returnType, out flow, out returnValue))
                        {
                            return false;
                        }

                        if (flow != CompileTimeStatementFlow.None)
                        {
                            return true;
                        }
                    }

                    return true;
                }
                finally
                {
                    state.PopScope();
                }
            }

            private bool TryExecuteScopedStatement(
                StarkParser.StatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;
                state.PushScope();
                try
                {
                    return TryExecuteStatement(statement, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }
                finally
                {
                    state.PopScope();
                }
            }

            private bool TryExecutePatternScopedStatement(
                StarkParser.PatternContext pattern,
                CompileTimeConstant value,
                StarkParser.StatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out bool matched,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                matched = false;
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                state.PushScope();
                try
                {
                    if (!TryMatchPattern(pattern, value, moduleName, state, activeCalls, out matched))
                    {
                        return false;
                    }

                    return !matched
                        || TryExecuteStatement(statement, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }
                finally
                {
                    state.PopScope();
                }
            }

            private bool TryExecuteIfStatement(
                StarkParser.IfStatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                if (statement.expression() is not { } conditionExpression)
                {
                    return false;
                }

                if (statement.pattern() is { } pattern)
                {
                    if (!TryEvaluateExpression(conditionExpression, moduleName, state, activeCalls, out var scrutinee)
                        || !TryExecutePatternScopedStatement(
                            pattern,
                            scrutinee,
                            statement.statement(0),
                            moduleName,
                            state,
                            activeCalls,
                            returnType,
                            out var matched,
                            out flow,
                            out returnValue))
                    {
                        return false;
                    }

                    if (matched)
                    {
                        return true;
                    }

                    return statement.statement().Length < 2
                        || TryExecuteScopedStatement(statement.statement(1), moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                if (!TryEvaluateExpression(conditionExpression, moduleName, state, activeCalls, out var condition)
                    || condition.Kind != CompileTimeConstantKind.Bool)
                {
                    return false;
                }

                if (!condition.BoolValue)
                {
                    return statement.statement().Length < 2
                        || TryExecuteScopedStatement(statement.statement(1), moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                return TryExecuteScopedStatement(statement.statement(0), moduleName, state, activeCalls, returnType, out flow, out returnValue);
            }

            private static bool TryConsumePendingReturn(
                CompileTimeEvaluationState state,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                if (state.TryConsumePendingReturn(out returnValue))
                {
                    flow = CompileTimeStatementFlow.Return;
                    return true;
                }

                flow = CompileTimeStatementFlow.None;
                return false;
            }

            private bool TryExecuteStatement(
                StarkParser.StatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                if (statement.block() is { } block)
                {
                    return TryExecuteBlock(block, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                if (statement.unsafeStatement() is { } unsafeStatement)
                {
                    if (unsafeStatement.block() is { } unsafeBlock)
                    {
                        return TryExecuteBlock(unsafeBlock, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                    }

                    if (unsafeStatement.assumeStatement()?.statement() is { } unsafeAssumedStatement)
                    {
                        return TryExecuteScopedStatement(unsafeAssumedStatement, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                    }

                    return false;
                }

                if (statement.assumeStatement() is { } assumeStatement)
                {
                    return TryExecuteScopedStatement(assumeStatement.statement(), moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                if (statement.localConstantDeclaration() is { } localConstant)
                {
                    var declaredType = _builder.TryResolveLocalDeclarationType(
                        TemplateLocalDeclarationFacts.ConstantKind,
                        localConstant,
                        out var typedLocalType)
                        ? typedLocalType
                    : localConstant.type_() is { } typeContext
                            ? ResolveLocalType(typeContext, moduleName, state)
                            : StarkTypeSymbols.Error;
                    foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
                    {
                        if (declarator.variableInitializer() is not { } initializerContext
                            || !TryEvaluateVariableInitializer(initializerContext, declaredType, moduleName, state, activeCalls, out var initializer)
                            || !CompileTimeExpressionEvaluator.TryCoerce(initializer, declaredType, out var coerced))
                        {
                            if (TryConsumePendingReturn(state, out flow, out returnValue))
                            {
                                return true;
                            }

                            return false;
                        }

                        state.Declare(declarator.Identifier().GetText(), coerced, isMutable: false);
                    }

                    return true;
                }

                if (statement.localVariableDeclaration() is { } localVariable)
                {
                    var declaredType = ResolveLocalType(localVariable.type_(), moduleName, state);
                    foreach (var declarator in localVariable.variableDeclarators().variableDeclarator())
                    {
                        CompileTimeConstant coerced;
                        if (declarator.variableInitializer() is { } initializerContext)
                        {
                            if (!TryEvaluateVariableInitializer(initializerContext, declaredType, moduleName, state, activeCalls, out var initializer)
                                || !CompileTimeExpressionEvaluator.TryCoerce(initializer, declaredType, out coerced))
                            {
                                if (TryConsumePendingReturn(state, out flow, out returnValue))
                                {
                                    return true;
                                }

                                return false;
                            }
                        }
                        else if (!TryCreateZeroConstant(declaredType, out coerced))
                        {
                            return false;
                        }

                        state.Declare(declarator.Identifier().GetText(), coerced, isMutable: localVariable.MUT() is not null);
                    }

                    return true;
                }

                if (statement.ifStatement() is { } ifStatement)
                {
                    return TryExecuteIfStatement(ifStatement, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                if (statement.switchStatement() is { } switchStatement)
                {
                    return TryExecuteSwitchStatement(switchStatement, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                if (statement.whileStatement() is { } whileStatement)
                {
                    return TryExecuteWhileStatement(whileStatement, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                if (statement.forStatement() is { } forStatement)
                {
                    return TryExecuteForStatement(forStatement, moduleName, state, activeCalls, returnType, out flow, out returnValue);
                }

                if (statement.breakStatement() is not null)
                {
                    flow = CompileTimeStatementFlow.Break;
                    return true;
                }

                if (statement.continueStatement() is not null)
                {
                    flow = CompileTimeStatementFlow.Continue;
                    return true;
                }

                if (statement.returnStatement() is { } returnStatement)
                {
                    flow = CompileTimeStatementFlow.Return;
                    if (returnStatement.expression() is null)
                    {
                        return returnType.Kind == StarkTypeKind.Void;
                    }

                    if (!TryEvaluateExpression(returnStatement.expression(), moduleName, state, activeCalls, out var computed))
                    {
                        if (TryConsumePendingReturn(state, out flow, out returnValue))
                        {
                            return true;
                        }

                        SetFailure($"Compile-time evaluation could not evaluate return expression '{returnStatement.expression().GetText()}' during MIR lowering. Use literals, const values, comptime generic values, or finite/law calls whose bodies stay within the supported compile-time subset.");
                        flow = CompileTimeStatementFlow.None;
                        return false;
                    }

                    if (returnType.Kind != StarkTypeKind.Error
                        && !CompileTimeExpressionEvaluator.TryCoerce(computed, returnType, out returnValue))
                    {
                        SetFailure($"Compile-time evaluation return expression '{returnStatement.expression().GetText()}' produced '{computed.Type.DisplayName}', which cannot be used as '{returnType.DisplayName}' during MIR lowering.");
                        flow = CompileTimeStatementFlow.None;
                        return false;
                    }

                    if (returnType.Kind == StarkTypeKind.Error)
                    {
                        returnValue = computed;
                    }

                    return true;
                }

                if (statement.expressionStatement() is { } expressionStatement)
                {
                    return TryExecuteExpressionStatement(expressionStatement.expression(), moduleName, state, activeCalls, out flow, out returnValue);
                }

                return false;
            }

            private bool TryExecuteSwitchStatement(
                StarkParser.SwitchStatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                if (!TryEvaluateExpression(statement.expression(), moduleName, state, activeCalls, out var switchValue))
                {
                    return false;
                }

                foreach (var section in statement.switchSection())
                {
                    foreach (var label in section.switchLabel())
                    {
                        state.PushScope();
                        try
                        {
                            if (!TryMatchSwitchLabel(
                                    label,
                                    switchValue,
                                    moduleName,
                                    state,
                                    activeCalls,
                                    out var matched))
                            {
                                return false;
                            }

                            if (!matched)
                            {
                                continue;
                            }

                            if (!TryExecuteSwitchSection(
                                    section,
                                    moduleName,
                                    state,
                                    activeCalls,
                                    returnType,
                                    out flow,
                                    out returnValue))
                            {
                                return false;
                            }

                            if (flow == CompileTimeStatementFlow.Break)
                            {
                                flow = CompileTimeStatementFlow.None;
                            }

                            return true;
                        }
                        finally
                        {
                            state.PopScope();
                        }
                    }
                }

                return true;
            }

            private bool TryExecuteSwitchSection(
                StarkParser.SwitchSectionContext section,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                foreach (var statement in section.statement())
                {
                    if (!TryExecuteStatement(statement, moduleName, state, activeCalls, returnType, out flow, out returnValue))
                    {
                        return false;
                    }

                    if (flow != CompileTimeStatementFlow.None)
                    {
                        return true;
                    }
                }

                return true;
            }

            private bool TryMatchSwitchLabel(
                StarkParser.SwitchLabelContext label,
                CompileTimeConstant switchValue,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;
                if (label.DEFAULT() is not null)
                {
                    matched = true;
                    return true;
                }

                foreach (var pattern in label.pattern())
                {
                    if (!TryMatchPattern(pattern, switchValue, moduleName, state, activeCalls, out var patternMatched))
                    {
                        return false;
                    }

                    if (!patternMatched)
                    {
                        state.PopScope();
                        state.PushScope();
                        continue;
                    }

                    if (label.whenClause()?.expression() is { } guardExpression)
                    {
                        if (!TryEvaluateExpression(guardExpression, moduleName, state, activeCalls, out var guardValue)
                            || guardValue.Kind != CompileTimeConstantKind.Bool)
                        {
                            return false;
                        }

                        if (!guardValue.BoolValue)
                        {
                            state.PopScope();
                            state.PushScope();
                            continue;
                        }
                    }

                    matched = true;
                    return true;
                }

                return true;
            }

            private bool TryMatchPattern(
                StarkParser.PatternContext pattern,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;

                if (pattern.DISCARD() is not null)
                {
                    matched = true;
                    return true;
                }

                if (pattern.VAR() is not null)
                {
                    state.Declare(pattern.Identifier().GetText(), value, isMutable: false);
                    matched = true;
                    return true;
                }

                if (pattern.literal() is { } literal)
                {
                    return TryMatchLiteralPattern(literal, value, moduleName, state, activeCalls, out matched);
                }

                if (pattern.rangePattern() is { } rangePattern)
                {
                    matched = value.Kind == CompileTimeConstantKind.Integer
                        && TryGetRangePatternBounds(rangePattern, out var min, out var max)
                        && value.IntegerValue >= min
                        && value.IntegerValue <= max;
                    return true;
                }

                if (pattern.listPattern() is { } listPattern)
                {
                    return TryMatchListPattern(listPattern, value, moduleName, state, activeCalls, out matched);
                }

                if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
                {
                    return TryMatchEnumNamedFieldPattern(enumNamedFieldPattern, value, moduleName, state, activeCalls, out matched);
                }

                if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
                {
                    return TryMatchAggregatePattern(
                        genericEnumAggregatePattern.genericEnumCaseReference().GetText(),
                        genericEnumAggregatePattern.aggregatePatternSuffix(),
                        value,
                        moduleName,
                        state,
                        activeCalls,
                        out matched);
                }

                if (pattern.aggregatePattern() is { } aggregatePattern)
                {
                    return TryMatchAggregatePattern(
                        aggregatePattern.simpleType().GetText(),
                        aggregatePattern.aggregatePatternSuffix(),
                        value,
                        moduleName,
                        state,
                        activeCalls,
                        out matched);
                }

                return false;
            }

            private bool TryMatchLiteralPattern(
                StarkParser.LiteralContext literal,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;
                if (!TryEvaluateExpressionNode(literal, moduleName, state, activeCalls, out var literalValue)
                    || !CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                        "==",
                        value,
                        literalValue,
                        requireInteger: false,
                        out var equality)
                    || equality.Kind != CompileTimeConstantKind.Bool)
                {
                    return false;
                }

                matched = equality.BoolValue;
                return true;
            }

            private bool TryMatchListPattern(
                StarkParser.ListPatternContext listPattern,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;
                var elementPatterns = listPattern.pattern();
                if (value.Kind != CompileTimeConstantKind.FixedArray
                    || value.Elements.Count != elementPatterns.Length)
                {
                    return true;
                }

                for (var index = 0; index < elementPatterns.Length; index++)
                {
                    if (!TryMatchPattern(elementPatterns[index], value.Elements[index], moduleName, state, activeCalls, out var elementMatched))
                    {
                        return false;
                    }

                    if (!elementMatched)
                    {
                        return true;
                    }
                }

                matched = true;
                return true;
            }

            private bool TryMatchEnumNamedFieldPattern(
                StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                var targetName = enumNamedFieldPattern.enumCaseTarget().GetText();
                var payload = enumNamedFieldPattern.namedPatternPayload();
                if (value.Kind == CompileTimeConstantKind.EnumAggregate)
                {
                    return TryMatchEnumAggregateValue(
                        targetName,
                        suffix: null,
                        payload,
                        value,
                        moduleName,
                        state,
                        activeCalls,
                        out matched);
                }

                if (value.Kind == CompileTimeConstantKind.NamedAggregate)
                {
                    return TryMatchNamedAggregateValue(
                        targetName,
                        suffix: null,
                        payload,
                        value,
                        moduleName,
                        state,
                        activeCalls,
                        out matched);
                }

                matched = false;
                return false;
            }

            private bool TryMatchAggregatePattern(
                string targetName,
                StarkParser.AggregatePatternSuffixContext? suffix,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                if (value.Kind == CompileTimeConstantKind.EnumAggregate)
                {
                    return TryMatchEnumAggregateValue(
                        targetName,
                        suffix,
                        payload: null,
                        value,
                        moduleName,
                        state,
                        activeCalls,
                        out matched);
                }

                if (value.Kind == CompileTimeConstantKind.NamedAggregate)
                {
                    return TryMatchNamedAggregateValue(
                        targetName,
                        suffix,
                        payload: null,
                        value,
                        moduleName,
                        state,
                        activeCalls,
                        out matched);
                }

                matched = false;
                return false;
            }

            private bool TryMatchNamedAggregateValue(
                string targetName,
                StarkParser.AggregatePatternSuffixContext? suffix,
                StarkParser.NamedPatternPayloadContext? payload,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;
                if (value.Kind != CompileTimeConstantKind.NamedAggregate
                    || !TypeNameMatches(value.Type, targetName))
                {
                    return true;
                }

                if (!TryResolveConstantNamedType(value.Type, out var namedType)
                    || namedType.Kind == DeclarationKind.Enum)
                {
                    return false;
                }

                if (payload is not null)
                {
                    return TryMatchNamedAggregatePayload(payload, namedType, value, moduleName, state, activeCalls, out matched);
                }

                if (suffix is null)
                {
                    matched = true;
                    return true;
                }

                if (suffix.Identifier() is { } wholeCapture)
                {
                    state.Declare(wholeCapture.GetText(), value, isMutable: false);
                    matched = true;
                    return true;
                }

                if (suffix.namedPatternPayload() is { } namedPayload)
                {
                    return TryMatchNamedAggregatePayload(namedPayload, namedType, value, moduleName, state, activeCalls, out matched);
                }

                var fieldPatterns = suffix.pattern();
                if (fieldPatterns.Length != namedType.OrderedFields.Count
                    || value.Elements.Count != namedType.OrderedFields.Count)
                {
                    return true;
                }

                for (var index = 0; index < fieldPatterns.Length; index++)
                {
                    if (!TryMatchPattern(fieldPatterns[index], value.Elements[index], moduleName, state, activeCalls, out var fieldMatched))
                    {
                        return false;
                    }

                    if (!fieldMatched)
                    {
                        return true;
                    }
                }

                matched = true;
                return true;
            }

            private bool TryMatchNamedAggregatePayload(
                StarkParser.NamedPatternPayloadContext payload,
                NamedTypeSymbol namedType,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;
                var members = payload.namedPatternMember();
                if (members.Length != namedType.OrderedFields.Count
                    || value.Elements.Count != namedType.OrderedFields.Count)
                {
                    return true;
                }

                var seen = new HashSet<int>();
                foreach (var member in members)
                {
                    if (!namedType.TryGetField(member.Identifier().GetText(), out _, out var fieldIndex)
                        || fieldIndex < 0
                        || fieldIndex >= value.Elements.Count
                        || !seen.Add(fieldIndex))
                    {
                        return false;
                    }

                    if (!TryMatchPattern(
                            member.pattern(),
                            value.Elements[fieldIndex],
                            moduleName,
                            state,
                            activeCalls,
                            out var fieldMatched))
                    {
                        return false;
                    }

                    if (!fieldMatched)
                    {
                        return true;
                    }
                }

                matched = seen.Count == namedType.OrderedFields.Count;
                return true;
            }

            private bool TryMatchEnumAggregateValue(
                string targetName,
                StarkParser.AggregatePatternSuffixContext? suffix,
                StarkParser.NamedPatternPayloadContext? payload,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;
                if (value.Kind != CompileTimeConstantKind.EnumAggregate
                    || value.VariantName is not { } variantName
                    || !EnumTargetMatches(value.Type, variantName, targetName))
                {
                    return true;
                }

                if (!TryResolveConstantNamedType(value.Type, out var namedType)
                    || !namedType.TryGetVariant(variantName, out var variant, out _))
                {
                    return false;
                }

                if (payload is not null)
                {
                    return TryMatchEnumNamedPayload(payload, variant, value, moduleName, state, activeCalls, out matched);
                }

                if (suffix is null)
                {
                    matched = value.Elements.Count == 0;
                    return true;
                }

                if (suffix.Identifier() is { } wholeCapture)
                {
                    state.Declare(wholeCapture.GetText(), value, isMutable: false);
                    matched = true;
                    return true;
                }

                if (suffix.namedPatternPayload() is { } namedPayload)
                {
                    return TryMatchEnumNamedPayload(namedPayload, variant, value, moduleName, state, activeCalls, out matched);
                }

                var fieldPatterns = suffix.pattern();
                if (variant.UsesNamedFields
                    || fieldPatterns.Length != variant.Fields.Count
                    || value.Elements.Count != variant.Fields.Count)
                {
                    return true;
                }

                for (var index = 0; index < fieldPatterns.Length; index++)
                {
                    if (!TryMatchPattern(fieldPatterns[index], value.Elements[index], moduleName, state, activeCalls, out var fieldMatched))
                    {
                        return false;
                    }

                    if (!fieldMatched)
                    {
                        return true;
                    }
                }

                matched = true;
                return true;
            }

            private bool TryMatchEnumNamedPayload(
                StarkParser.NamedPatternPayloadContext payload,
                EnumVariantSymbol variant,
                CompileTimeConstant value,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out bool matched)
            {
                matched = false;
                var members = payload.namedPatternMember();
                if (!variant.UsesNamedFields
                    || members.Length != variant.Fields.Count
                    || value.Elements.Count != variant.Fields.Count)
                {
                    return true;
                }

                var seen = new HashSet<int>();
                foreach (var member in members)
                {
                    var field = variant.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, member.Identifier().GetText(), StringComparison.Ordinal));
                    if (field is null
                        || field.Position < 0
                        || field.Position >= value.Elements.Count
                        || !seen.Add(field.Position))
                    {
                        return false;
                    }

                    if (!TryMatchPattern(
                            member.pattern(),
                            value.Elements[field.Position],
                            moduleName,
                            state,
                            activeCalls,
                            out var fieldMatched))
                    {
                        return false;
                    }

                    if (!fieldMatched)
                    {
                        return true;
                    }
                }

                matched = seen.Count == variant.Fields.Count;
                return true;
            }

            private bool TryResolveConstantNamedType(StarkTypeSymbol type, out NamedTypeSymbol namedType)
            {
                namedType = null!;
                var coreType = StarkTypeSymbols.WithQualifiers(
                    _builder.ApplyGenericSubstitution(type),
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
                return coreType.Kind == StarkTypeKind.Named
                    && _builder.TryResolveCompileTimeNamedType(coreType, out namedType);
            }

            private static bool TypeNameMatches(StarkTypeSymbol type, string patternText)
            {
                var coreType = StarkTypeSymbols.WithQualifiers(
                    type,
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false);
                if (coreType.Kind != StarkTypeKind.Named || coreType.NamedType is not { } namedType)
                {
                    return false;
                }

                var actualBaseName = StarkTypeSymbols.GetGenericBaseName(namedType);
                var patternBaseName = StarkTypeSymbols.GetGenericBaseName(patternText);
                return NameMatches(namedType, patternText)
                    || NameMatches(actualBaseName, patternBaseName);
            }

            private static bool EnumTargetMatches(StarkTypeSymbol enumType, string variantName, string targetText)
            {
                if (!NameMatches(variantName, LastDottedSegment(targetText)))
                {
                    return false;
                }

                var enumTarget = StripEnumTargetVariant(targetText);
                return enumTarget.Length == 0 || TypeNameMatches(enumType, enumTarget);
            }

            private static string StripEnumTargetVariant(string targetText)
            {
                var separator = targetText.LastIndexOf('.');
                return separator <= 0 ? string.Empty : targetText[..separator];
            }

            private static string LastDottedSegment(string text)
            {
                var separator = text.LastIndexOf('.');
                return separator >= 0 ? text[(separator + 1)..] : text;
            }

            private static bool NameMatches(string actual, string expected)
            {
                if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
                {
                    return false;
                }

                return string.Equals(actual, expected, StringComparison.Ordinal)
                    || actual.EndsWith($".{expected}", StringComparison.Ordinal)
                    || expected.EndsWith($".{actual}", StringComparison.Ordinal);
            }

            private static bool TryGetRangePatternBounds(
                StarkParser.RangePatternContext rangePattern,
                out BigInteger min,
                out BigInteger max)
            {
                var endpoints = rangePattern.signedIntegerLiteral();
                if (endpoints.Length != 2)
                {
                    min = BigInteger.Zero;
                    max = BigInteger.Zero;
                    return false;
                }

                min = ParseSignedIntegerLiteral(endpoints[0]);
                max = ParseSignedIntegerLiteral(endpoints[1]);
                return min <= max;
            }

            private static BigInteger ParseSignedIntegerLiteral(StarkParser.SignedIntegerLiteralContext literal)
            {
                var value = BigInteger.Parse(literal.IntegerLiteral().GetText());
                return literal.MINUS() is null ? value : -value;
            }

            private bool TryExecuteWhileStatement(
                StarkParser.WhileStatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                if (statement.loopBehavior().WILLEXIT() is null)
                {
                    return false;
                }

                var iterations = 0;
                if (statement.pattern() is { } pattern)
                {
                    while (true)
                    {
                        if (!TryEvaluateExpression(statement.expression(), moduleName, state, activeCalls, out var scrutinee))
                        {
                            return false;
                        }

                        state.PushScope();
                        try
                        {
                            if (!TryMatchPattern(pattern, scrutinee, moduleName, state, activeCalls, out var matched))
                            {
                                return false;
                            }

                            if (!matched)
                            {
                                return true;
                            }

                            if (iterations++ >= _maximumLoopIterations)
                            {
                                return false;
                            }

                            if (!TryExecuteStatement(statement.statement(), moduleName, state, activeCalls, returnType, out flow, out returnValue))
                            {
                                return false;
                            }

                            if (flow == CompileTimeStatementFlow.Return)
                            {
                                return true;
                            }

                            if (flow == CompileTimeStatementFlow.Break)
                            {
                                flow = CompileTimeStatementFlow.None;
                                return true;
                            }

                            if (flow == CompileTimeStatementFlow.Continue)
                            {
                                flow = CompileTimeStatementFlow.None;
                            }
                        }
                        finally
                        {
                            state.PopScope();
                        }
                    }
                }

                while (true)
                {
                    if (!TryEvaluateExpression(statement.expression(), moduleName, state, activeCalls, out var condition)
                        || condition.Kind != CompileTimeConstantKind.Bool)
                    {
                        return false;
                    }

                    if (!condition.BoolValue)
                    {
                        return true;
                    }

                    if (iterations++ >= _maximumLoopIterations)
                    {
                        return false;
                    }

                    if (!TryExecuteScopedStatement(statement.statement(), moduleName, state, activeCalls, returnType, out flow, out returnValue))
                    {
                        return false;
                    }

                    if (flow == CompileTimeStatementFlow.Return)
                    {
                        return true;
                    }

                    if (flow == CompileTimeStatementFlow.Break)
                    {
                        flow = CompileTimeStatementFlow.None;
                        return true;
                    }

                    if (flow == CompileTimeStatementFlow.Continue)
                    {
                        flow = CompileTimeStatementFlow.None;
                    }
                }
            }

            private bool TryExecuteForStatement(
                StarkParser.ForStatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                if (statement.loopBehavior().WILLEXIT() is null)
                {
                    return false;
                }

                if (statement.forTraversal() is { } traversal)
                {
                    return TryExecuteForTraversalStatement(
                        statement,
                        traversal,
                        moduleName,
                        state,
                        activeCalls,
                        returnType,
                        out flow,
                        out returnValue);
                }

                state.PushScope();
                try
                {
                    if (!TryExecuteForInitializer(statement.forInitializer(), moduleName, state, activeCalls))
                    {
                        if (TryConsumePendingReturn(state, out flow, out returnValue))
                        {
                            return true;
                        }

                        return false;
                    }

                    var iterations = 0;
                    while (true)
                    {
                        if (statement.forCondition()?.expression() is { } conditionExpression)
                        {
                            if (!TryEvaluateExpression(conditionExpression, moduleName, state, activeCalls, out var condition)
                                || condition.Kind != CompileTimeConstantKind.Bool)
                            {
                                return false;
                            }

                            if (!condition.BoolValue)
                            {
                                return true;
                            }
                        }

                        if (iterations++ >= _maximumLoopIterations)
                        {
                            return false;
                        }

                        if (!TryExecuteScopedStatement(statement.statement(), moduleName, state, activeCalls, returnType, out flow, out returnValue))
                        {
                            return false;
                        }

                        if (flow == CompileTimeStatementFlow.Return)
                        {
                            return true;
                        }

                        if (flow == CompileTimeStatementFlow.Break)
                        {
                            flow = CompileTimeStatementFlow.None;
                            return true;
                        }

                        if (flow == CompileTimeStatementFlow.Continue)
                        {
                            flow = CompileTimeStatementFlow.None;
                        }

                        if (!TryExecuteForIterator(statement.forIterator(), moduleName, state, activeCalls))
                        {
                            if (TryConsumePendingReturn(state, out flow, out returnValue))
                            {
                                return true;
                            }

                            return false;
                        }
                    }
                }
                finally
                {
                    state.PopScope();
                }
            }

            private bool TryExecuteForTraversalStatement(
                StarkParser.ForStatementContext statement,
                StarkParser.ForTraversalContext traversal,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;

                var sourceTarget = TryResolvePlaceTarget(traversal.expression(), out var resolvedSourceTarget)
                    ? resolvedSourceTarget
                    : null;
                if (!TryEvaluateTraversalSource(
                        traversal.expression(),
                        sourceTarget,
                        moduleName,
                        state,
                        activeCalls,
                        out var initialSource)
                    || initialSource.Kind != CompileTimeConstantKind.FixedArray
                    || initialSource.Type.ElementType is null)
                {
                    return false;
                }

                var elementBinding = traversal.traversalElementBinding();
                var elementBindingType = ResolveLocalType(elementBinding.type_(), moduleName, state);
                if (elementBindingType.BorrowKind != StarkBorrowKind.Borrow
                    || elementBindingType.InitializationKind != StarkInitializationKind.None)
                {
                    return false;
                }

                var elementValueType = StarkTypeSymbols.BorrowReturnValueType(elementBindingType);
                if (elementBindingType.IsMutableView && sourceTarget is null)
                {
                    return false;
                }

                var indexBinding = traversal.traversalIndexBinding();
                StarkTypeSymbol? indexType = null;
                string? indexName = null;
                if (indexBinding is not null)
                {
                    indexType = ResolveLocalType(indexBinding.type_(), moduleName, state);
                    if (indexType.Kind != StarkTypeKind.Integer)
                    {
                        return false;
                    }

                    indexName = indexBinding.Identifier().GetText();
                }

                var iterations = 0;
                for (var elementIndex = 0; elementIndex < initialSource.Elements.Count; elementIndex++)
                {
                    if (iterations++ >= _maximumLoopIterations)
                    {
                        return false;
                    }

                    var currentSource = initialSource;
                    if (sourceTarget is not null
                        && (!TryEvaluatePlaceTarget(sourceTarget, moduleName, state, activeCalls, out currentSource)
                            || currentSource.Kind != CompileTimeConstantKind.FixedArray
                            || elementIndex >= currentSource.Elements.Count))
                    {
                        return false;
                    }

                    var elementValue = currentSource.Elements[elementIndex];
                    if (!CompileTimeExpressionEvaluator.TryCoerce(elementValue, elementValueType, out var coercedElement))
                    {
                        return false;
                    }

                    state.PushScope();
                    try
                    {
                        if (indexBinding is not null)
                        {
                            var indexConstant = CompileTimeConstant.Integer(new BigInteger(elementIndex), indexType!);
                            if (!CompileTimeExpressionEvaluator.TryCoerce(indexConstant, indexType!, out var coercedIndex))
                            {
                                return false;
                            }

                            state.Declare(indexName!, coercedIndex, isMutable: false);
                        }

                        state.Declare(
                            elementBinding.Identifier().GetText(),
                            coercedElement,
                            isMutable: elementBindingType.IsMutableView);

                        if (!TryExecuteStatement(statement.statement(), moduleName, state, activeCalls, returnType, out flow, out returnValue))
                        {
                            return false;
                        }

                        if (elementBindingType.IsMutableView
                            && sourceTarget is not null
                            && !TryWriteBackTraversalElement(
                                sourceTarget,
                                elementBinding.Identifier().GetText(),
                                elementIndex,
                                elementValueType,
                                moduleName,
                                state,
                                activeCalls))
                        {
                            return false;
                        }

                        if (flow == CompileTimeStatementFlow.Return)
                        {
                            return true;
                        }

                        if (flow == CompileTimeStatementFlow.Break)
                        {
                            flow = CompileTimeStatementFlow.None;
                            return true;
                        }

                        if (flow == CompileTimeStatementFlow.Continue)
                        {
                            flow = CompileTimeStatementFlow.None;
                        }
                    }
                    finally
                    {
                        state.PopScope();
                    }
                }

                return true;
            }

            private bool TryEvaluateTraversalSource(
                StarkParser.ExpressionContext expression,
                CompileTimeAssignmentTarget? sourceTarget,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant source)
            {
                return sourceTarget is not null
                    ? TryEvaluatePlaceTarget(sourceTarget, moduleName, state, activeCalls, out source)
                    : TryEvaluateExpression(expression, moduleName, state, activeCalls, out source);
            }

            private bool TryWriteBackTraversalElement(
                CompileTimeAssignmentTarget sourceTarget,
                string elementName,
                int elementIndex,
                StarkTypeSymbol elementValueType,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                if (!state.TryResolve(elementName, out var updatedElement)
                    || !CompileTimeExpressionEvaluator.TryCoerce(updatedElement, elementValueType, out var coercedElement)
                    || !TryEvaluatePlaceTarget(sourceTarget, moduleName, state, activeCalls, out var sourceValue)
                    || sourceValue.Kind != CompileTimeConstantKind.FixedArray
                    || elementIndex < 0
                    || elementIndex >= sourceValue.Elements.Count)
                {
                    return false;
                }

                var updatedElements = sourceValue.Elements.ToArray();
                updatedElements[elementIndex] = coercedElement;
                var updatedSource = CompileTimeConstant.FixedArray(updatedElements, sourceValue.Type);
                return TryAssignPlaceTarget(sourceTarget, "=", updatedSource, moduleName, state, activeCalls);
            }

            private bool TryExecuteForInitializer(
                StarkParser.ForInitializerContext? initializer,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                if (initializer is null)
                {
                    return true;
                }

                if (initializer.localForVariableDeclaration() is { } localForVariable)
                {
                    var declaredType = ResolveLocalType(localForVariable.type_(), moduleName, state);
                    foreach (var declarator in localForVariable.variableDeclarators().variableDeclarator())
                    {
                        CompileTimeConstant value;
                        if (declarator.variableInitializer() is { } initializerContext)
                        {
                            if (!TryEvaluateVariableInitializer(initializerContext, declaredType, moduleName, state, activeCalls, out var initialized)
                                || !CompileTimeExpressionEvaluator.TryCoerce(initialized, declaredType, out value))
                            {
                                return false;
                            }
                        }
                        else if (!TryCreateZeroConstant(declaredType, out value))
                        {
                            return false;
                        }

                        state.Declare(declarator.Identifier().GetText(), value, isMutable: localForVariable.MUT() is not null);
                    }

                    return true;
                }

                return initializer.expressionList() is { } expressions
                    && TryExecuteExpressionList(expressions, moduleName, state, activeCalls);
            }

            private bool TryExecuteForIterator(
                StarkParser.ForIteratorContext? iterator,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                return iterator is null
                    || TryExecuteExpressionList(iterator.expressionList(), moduleName, state, activeCalls);
            }

            private bool TryExecuteExpressionList(
                StarkParser.ExpressionListContext expressions,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                foreach (var expression in expressions.expression())
                {
                    if (!TryExecuteExpressionStatement(expression, moduleName, state, activeCalls))
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool TryExecuteExpressionStatement(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                return TryHandleAssignmentStatement(expression, moduleName, state, activeCalls)
                    || TryEvaluateExpression(expression, moduleName, state, activeCalls, out _);
            }

            private bool TryExecuteExpressionStatement(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeStatementFlow flow,
                out CompileTimeConstant returnValue)
            {
                flow = CompileTimeStatementFlow.None;
                returnValue = default;
                if (TryHandleAssignmentStatement(expression, moduleName, state, activeCalls))
                {
                    return true;
                }

                if (TryConsumePendingReturn(state, out flow, out returnValue))
                {
                    return true;
                }

                if (TryEvaluateExpression(expression, moduleName, state, activeCalls, out _))
                {
                    return true;
                }

                return TryConsumePendingReturn(state, out flow, out returnValue);
            }

            private bool TryEvaluateVariableInitializer(
                StarkParser.VariableInitializerContext initializer,
                StarkTypeSymbol targetType,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                if (initializer.expression() is { } expression)
                {
                    return TryEvaluateExpression(expression, moduleName, state, activeCalls, out constant);
                }

                if (initializer.arrayInitializer() is { } arrayInitializer)
                {
                    return TryEvaluateArrayInitializer(arrayInitializer, targetType, moduleName, state, activeCalls, out constant);
                }

                if (initializer.objectInitializer() is { } objectInitializer)
                {
                    return TryEvaluateObjectInitializer(
                        objectInitializer,
                        targetType,
                        moduleName,
                        state,
                        activeCalls,
                        preinitializedFields: null,
                        out constant);
                }

                constant = default;
                return false;
            }

            private bool TryEvaluateObjectCreation(
                StarkParser.ObjectCreationExpressionContext objectCreation,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                var hasBoundObjectCreation = _builder.TryResolveCompileTimeBoundObjectCreation(objectCreation, out var boundObjectCreation);
                _builder.TryGetPublishedObjectCreationSummary(objectCreation, out var publishedObjectCreation);
                var targetType = hasBoundObjectCreation
                    ? _builder.ApplyGenericSubstitution(boundObjectCreation.CreatedType)
                    : publishedObjectCreation is not null
                        ? _builder.ApplyGenericSubstitution(publishedObjectCreation.CreatedType)
                        : objectCreation.type_() is { } explicitType
                            ? ResolveLocalType(explicitType, moduleName, state)
                            : StarkTypeSymbols.Error;
                if (targetType.Kind != StarkTypeKind.Named
                    || !_builder.TryResolveCompileTimeNamedType(targetType, out var namedType)
                    || !TryCreateZeroConstant(targetType, out var current))
                {
                    return false;
                }

                var arguments = objectCreation.argumentList()?.argument() ?? [];
                var constructor = hasBoundObjectCreation
                    ? boundObjectCreation.Constructor
                    : publishedObjectCreation?.Constructor;
                var preinitializedFields = new HashSet<string>(StringComparer.Ordinal);
                if (constructor is not null)
                {
                    constructor = _builder.ApplyGenericSubstitution(constructor);
                    if (!constructor.IsPrimaryShape || arguments.Length != constructor.Parameters.Count)
                    {
                        return false;
                    }

                    for (var index = 0; index < arguments.Length; index++)
                    {
                        var parameter = constructor.Parameters[index];
                        if (!namedType.TryGetField(parameter.Name, out var field, out var fieldIndex)
                            || !TryEvaluateExpression(arguments[index].expression(), moduleName, state, activeCalls, out var argumentValue)
                            || !CompileTimeExpressionEvaluator.TryCoerce(argumentValue, _builder.ApplyGenericSubstitution(field.Type), out var coercedArgument)
                            || !TryWithNamedAggregateField(current, fieldIndex, coercedArgument, out current))
                        {
                            return false;
                        }

                        preinitializedFields.Add(field.Name);
                    }
                }
                else if (arguments.Length != 0)
                {
                    return false;
                }

                if (objectCreation.objectInitializer() is { } objectInitializer
                    && !TryEvaluateObjectInitializer(
                        objectInitializer,
                        targetType,
                        moduleName,
                        state ?? _builder._compileTimeConstantState,
                        activeCalls,
                        hasBoundObjectCreation ? boundObjectCreation.Members : null,
                        publishedObjectCreation?.InitializerMembers,
                        preinitializedFields,
                        current,
                        out current))
                {
                    return false;
                }

                constant = current;
                return true;
            }

            private bool TryEvaluateObjectInitializer(
                StarkParser.ObjectInitializerContext objectInitializer,
                StarkTypeSymbol targetType,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                ISet<string>? preinitializedFields,
                out CompileTimeConstant constant)
            {
                constant = default;
                return TryCreateZeroConstant(targetType, out var seed)
                    && TryEvaluateObjectInitializer(
                        objectInitializer,
                        targetType,
                        moduleName,
                        state,
                        activeCalls,
                        boundInitializerMembers: null,
                        publishedInitializerMembers: null,
                        preinitializedFields,
                        seed,
                        out constant);
            }

            private bool TryEvaluateObjectInitializer(
                StarkParser.ObjectInitializerContext objectInitializer,
                StarkTypeSymbol targetType,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                IReadOnlyList<ObjectInitializerMemberTypingRecord>? boundInitializerMembers,
                IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? publishedInitializerMembers,
                ISet<string>? preinitializedFields,
                CompileTimeConstant seed,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (targetType.Kind != StarkTypeKind.Named
                    || !_builder.TryResolveCompileTimeNamedType(targetType, out var namedType)
                    || seed.Kind != CompileTimeConstantKind.NamedAggregate)
                {
                    return false;
                }

                var current = seed;
                var initializedFields = preinitializedFields is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(preinitializedFields, StringComparer.Ordinal);
                for (var index = 0; index < objectInitializer.memberInitializer().Length; index++)
                {
                    var initializer = objectInitializer.memberInitializer(index);
                    var fieldName = initializer.Identifier().GetText();
                    var fieldIndex = -1;
                    var fieldType = StarkTypeSymbols.Error;
                    if (boundInitializerMembers is { Count: > 0 } && index < boundInitializerMembers.Count)
                    {
                        var boundMember = boundInitializerMembers[index];
                        fieldName = boundMember.FieldName;
                        fieldIndex = boundMember.FieldIndex;
                        fieldType = _builder.ApplyGenericSubstitution(boundMember.FieldType);
                    }
                    else if (publishedInitializerMembers is { Count: > 0 } && index < publishedInitializerMembers.Count)
                    {
                        var publishedMember = publishedInitializerMembers[index];
                        fieldName = publishedMember.FieldName;
                        fieldIndex = publishedMember.FieldIndex;
                        fieldType = _builder.ApplyGenericSubstitution(publishedMember.FieldType);
                    }
                    else if (namedType.TryGetField(fieldName, out var field, out var resolvedFieldIndex))
                    {
                        fieldIndex = resolvedFieldIndex;
                        fieldType = _builder.ApplyGenericSubstitution(field.Type);
                    }
                    else
                    {
                        return false;
                    }

                    if (!initializedFields.Add(fieldName)
                        || !TryEvaluateVariableInitializer(initializer.variableInitializer(), fieldType, moduleName, state, activeCalls, out var memberValue)
                        || !CompileTimeExpressionEvaluator.TryCoerce(memberValue, fieldType, out var coercedMember)
                        || !TryWithNamedAggregateField(current, fieldIndex, coercedMember, out current))
                    {
                        return false;
                    }
                }

                constant = current;
                return true;
            }

            private bool TryEvaluateArrayInitializer(
                StarkParser.ArrayInitializerContext arrayInitializer,
                StarkTypeSymbol targetType,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;
                if (targetType.Kind != StarkTypeKind.FixedArray
                    || targetType.ElementType is not { } elementType
                    || targetType.FixedLength is not int fixedLength
                    || arrayInitializer.variableInitializer().Length > fixedLength)
                {
                    return false;
                }

                var elements = new CompileTimeConstant[fixedLength];
                var initializedCount = arrayInitializer.variableInitializer().Length;
                for (var index = 0; index < initializedCount; index++)
                {
                    if (!TryEvaluateVariableInitializer(
                            arrayInitializer.variableInitializer(index),
                            elementType,
                            moduleName,
                            state,
                            activeCalls,
                            out var element)
                        || !CompileTimeExpressionEvaluator.TryCoerce(element, elementType, out elements[index]))
                    {
                        return false;
                    }
                }

                for (var index = initializedCount; index < fixedLength; index++)
                {
                    if (!TryCreateZeroConstant(elementType, out elements[index]))
                    {
                        return false;
                    }
                }

                constant = CompileTimeConstant.FixedArray(elements, targetType);
                return true;
            }

            private bool TryCreateZeroConstant(StarkTypeSymbol type, out CompileTimeConstant constant)
            {
                constant = default;
                switch (type.Kind)
                {
                    case StarkTypeKind.Integer:
                        constant = CompileTimeConstant.Integer(BigInteger.Zero, type);
                        return true;
                    case StarkTypeKind.Float:
                        constant = CompileTimeConstant.Float(0, type);
                        return true;
                    case StarkTypeKind.Bool:
                        constant = CompileTimeConstant.Bool(false);
                        return true;
                    case StarkTypeKind.RawPointer:
                        constant = CompileTimeConstant.Null(type);
                        return true;
                    case StarkTypeKind.FixedArray when type.ElementType is { } elementType && type.FixedLength is int fixedLength:
                        var elements = new CompileTimeConstant[fixedLength];
                        for (var index = 0; index < fixedLength; index++)
                        {
                            if (!TryCreateZeroConstant(elementType, out elements[index]))
                            {
                                return false;
                            }
                        }

                        constant = CompileTimeConstant.FixedArray(elements, type);
                        return true;
                    case StarkTypeKind.Named when _builder.TryResolveCompileTimeNamedType(type, out var namedType):
                        var fieldValues = new CompileTimeConstant[namedType.OrderedFields.Count];
                        for (var index = 0; index < namedType.OrderedFields.Count; index++)
                        {
                            if (!TryCreateZeroConstant(_builder.ApplyGenericSubstitution(namedType.OrderedFields[index].Type), out fieldValues[index]))
                            {
                                return false;
                            }
                        }

                        constant = CompileTimeConstant.NamedAggregate(fieldValues, type);
                        return true;
                    default:
                        return false;
                }
            }

            private static bool TryWithNamedAggregateField(
                CompileTimeConstant aggregate,
                int fieldIndex,
                CompileTimeConstant fieldValue,
                out CompileTimeConstant updated)
            {
                updated = default;
                if (aggregate.Kind != CompileTimeConstantKind.NamedAggregate
                    || fieldIndex < 0
                    || fieldIndex >= aggregate.Elements.Count)
                {
                    return false;
                }

                var elements = aggregate.Elements.ToArray();
                elements[fieldIndex] = fieldValue;
                updated = CompileTimeConstant.NamedAggregate(elements, aggregate.Type);
                return true;
            }

            private bool TryWithNamedAggregateField(
                CompileTimeConstant aggregate,
                string fieldName,
                CompileTimeConstant fieldValue,
                out CompileTimeConstant updated)
            {
                updated = default;
                if (aggregate.Kind != CompileTimeConstantKind.NamedAggregate
                    || !_builder.TryResolveCompileTimeNamedType(aggregate.Type, out var namedType)
                    || !namedType.TryGetField(fieldName, out _, out var fieldIndex))
                {
                    return false;
                }

                return TryWithNamedAggregateField(aggregate, fieldIndex, fieldValue, out updated);
            }

            private bool TryHandleAssignmentStatement(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                var assignment = expression.assignmentExpression();
                if (assignment.assignmentOperator() is not { } assignmentOperator
                    || assignment.unaryExpression() is not { } unaryExpression
                    || assignment.assignmentExpression() is not { } rightExpression
                    || !TryResolveAssignmentTarget(unaryExpression, out var target)
                    || !TryEvaluateAssignmentExpression(rightExpression, moduleName, state, activeCalls, out var assignedValue))
                {
                    return false;
                }

                return TryAssignPlaceTarget(target, assignmentOperator.GetText(), assignedValue, moduleName, state, activeCalls);
            }

            private static bool TryAssignLocalTarget(
                string targetName,
                string assignmentOperator,
                CompileTimeConstant assignedValue,
                CompileTimeEvaluationState state)
            {
                if (!state.TryResolve(targetName, out var targetValue)
                    || !TryEvaluateAssignedValue(assignmentOperator, targetValue, assignedValue, targetValue.Type, out var coerced))
                {
                    return false;
                }

                return state.TryAssign(targetName, coerced);
            }

            private bool TryAssignPlaceTarget(
                CompileTimeAssignmentTarget target,
                string assignmentOperator,
                CompileTimeConstant assignedValue,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                if (target.Segments.Count == 0)
                {
                    return TryAssignLocalTarget(target.Name, assignmentOperator, assignedValue, state);
                }

                if (!state.TryResolve(target.Name, out var rootValue)
                    || !TryUpdatePlaceValue(
                        rootValue,
                        target.Segments,
                        segmentIndex: 0,
                        assignmentOperator,
                        assignedValue,
                        moduleName,
                        state,
                        activeCalls,
                        out var updatedRoot))
                {
                    return false;
                }

                return state.TryAssign(target.Name, updatedRoot);
            }

            private bool TryEvaluatePlaceTarget(
                CompileTimeAssignmentTarget target,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant value)
            {
                value = default;
                if (!state.TryResolve(target.Name, out var current))
                {
                    return false;
                }

                foreach (var segment in target.Segments)
                {
                    if (!TryGetPlaceSegmentValue(segment, current, moduleName, state, activeCalls, out current))
                    {
                        return false;
                    }
                }

                value = current;
                return true;
            }

            private bool TryUpdatePlaceValue(
                CompileTimeConstant current,
                IReadOnlyList<CompileTimePlaceSegment> segments,
                int segmentIndex,
                string assignmentOperator,
                CompileTimeConstant assignedValue,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant updated)
            {
                updated = default;
                if (segmentIndex >= segments.Count)
                {
                    return TryEvaluateAssignedValue(assignmentOperator, current, assignedValue, current.Type, out updated);
                }

                var segment = segments[segmentIndex];
                if (!TryGetPlaceSegmentValue(segment, current, moduleName, state, activeCalls, out var child)
                    || !TryUpdatePlaceValue(
                        child,
                        segments,
                        segmentIndex + 1,
                        assignmentOperator,
                        assignedValue,
                        moduleName,
                        state,
                        activeCalls,
                        out var updatedChild))
                {
                    return false;
                }

                return segment.Kind switch
                {
                    CompileTimePlaceSegmentKind.Index => TryWithFixedArrayElement(
                        current,
                        segment.IndexExpression,
                        updatedChild,
                        moduleName,
                        state,
                        activeCalls,
                        out updated),
                    CompileTimePlaceSegmentKind.Field => TryWithNamedAggregateField(
                        current,
                        segment.FieldName!,
                        updatedChild,
                        out updated),
                    _ => false
                };
            }

            private bool TryGetPlaceSegmentValue(
                CompileTimePlaceSegment segment,
                CompileTimeConstant current,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant value)
            {
                value = default;
                return segment.Kind switch
                {
                    CompileTimePlaceSegmentKind.Index => TryGetFixedArrayElement(
                        current,
                        segment.IndexExpression,
                        moduleName,
                        state,
                        activeCalls,
                        out value),
                    CompileTimePlaceSegmentKind.Field => TryGetNamedAggregateField(current, segment.FieldName!, out value),
                    _ => false
                };
            }

            private bool TryGetFixedArrayElement(
                CompileTimeConstant arrayValue,
                StarkParser.ExpressionContext? indexExpression,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant value)
            {
                value = default;
                if (arrayValue.Kind != CompileTimeConstantKind.FixedArray
                    || indexExpression is null
                    || !TryEvaluateInteger(indexExpression, moduleName, state, activeCalls, out var indexValue)
                    || indexValue < 0
                    || indexValue > int.MaxValue
                    || indexValue >= arrayValue.Elements.Count)
                {
                    return false;
                }

                value = arrayValue.Elements[(int)indexValue];
                return true;
            }

            private bool TryWithFixedArrayElement(
                CompileTimeConstant arrayValue,
                StarkParser.ExpressionContext? indexExpression,
                CompileTimeConstant elementValue,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                out CompileTimeConstant updated)
            {
                updated = default;
                if (arrayValue.Kind != CompileTimeConstantKind.FixedArray
                    || arrayValue.Type.ElementType is not { } elementType
                    || indexExpression is null
                    || !TryEvaluateInteger(indexExpression, moduleName, state, activeCalls, out var indexValue)
                    || indexValue < 0
                    || indexValue > int.MaxValue
                    || indexValue >= arrayValue.Elements.Count
                    || !CompileTimeExpressionEvaluator.TryCoerce(elementValue, elementType, out var coerced))
                {
                    return false;
                }

                var updatedElements = arrayValue.Elements.ToArray();
                updatedElements[(int)indexValue] = coerced;
                updated = CompileTimeConstant.FixedArray(updatedElements, arrayValue.Type);
                return true;
            }

            private static bool TryEvaluateAssignedValue(
                string assignmentOperator,
                CompileTimeConstant targetValue,
                CompileTimeConstant assignedValue,
                StarkTypeSymbol targetType,
                out CompileTimeConstant coerced)
            {
                coerced = default;
                if (assignmentOperator == "=")
                {
                    return CompileTimeExpressionEvaluator.TryCoerce(assignedValue, targetType, out coerced);
                }

                if (!TryGetBinaryOperatorForAssignment(assignmentOperator, out var binaryOperator, out var requireInteger)
                    || !CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                        binaryOperator,
                        targetValue,
                        assignedValue,
                        requireInteger,
                        out var computed))
                {
                    return false;
                }

                return CompileTimeExpressionEvaluator.TryCoerce(computed, targetType, out coerced);
            }

            private static bool TryGetBinaryOperatorForAssignment(
                string assignmentOperator,
                out string binaryOperator,
                out bool requireInteger)
            {
                binaryOperator = assignmentOperator switch
                {
                    "+=" => "+",
                    "-=" => "-",
                    "*=" => "*",
                    "+%=" => "+%",
                    "-%=" => "-%",
                    "*%=" => "*%",
                    "+|=" => "+|",
                    "-|=" => "-|",
                    "*|=" => "*|",
                    "/=" => "/",
                    "%=" => "%",
                    "&=" => "&",
                    "|=" => "|",
                    "^=" => "^",
                    _ => string.Empty
                };
                requireInteger = assignmentOperator is "+%=" or "-%=" or "*%=" or "+|=" or "-|=" or "*|=" or "&=" or "|=" or "^=";
                return binaryOperator.Length > 0;
            }

            private bool TryEvaluateAssignmentExpression(
                StarkParser.AssignmentExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string>? activeCalls,
                out CompileTimeConstant constant)
            {
                activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
                var services = CreateServices(moduleName, state, activeCalls);
                return CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant, services);
            }

            private static bool TryResolvePlaceTarget(
                ParserRuleContext expression,
                out CompileTimeAssignmentTarget target)
            {
                target = default!;

                if (TryGetSimplePostfixExpression(expression) is not { } postfix
                    || postfix.primaryExpression().Identifier() is not { } identifier)
                {
                    return false;
                }

                var targetName = identifier.GetText();
                var segments = new List<CompileTimePlaceSegment>(postfix.postfixPart().Length);
                foreach (var postfixPart in postfix.postfixPart())
                {
                    if (postfixPart.DOT() is not null && postfixPart.Identifier() is { } fieldName)
                    {
                        segments.Add(CompileTimePlaceSegment.Field(fieldName.GetText()));
                        continue;
                    }

                    if (postfixPart.expressionList()?.expression() is [var indexExpression])
                    {
                        segments.Add(CompileTimePlaceSegment.Index(indexExpression));
                        continue;
                    }

                    return false;
                }

                target = new CompileTimeAssignmentTarget(targetName, segments);
                return true;
            }

            private static bool TryResolveAssignmentTarget(
                StarkParser.UnaryExpressionContext expression,
                out CompileTimeAssignmentTarget target)
            {
                return TryResolvePlaceTarget(expression, out target);
            }

            private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(ParserRuleContext context)
            {
                if (context is StarkParser.PostfixExpressionContext postfix)
                {
                    return postfix;
                }

                return context.ChildCount == 1 && context.GetChild(0) is ParserRuleContext child
                    ? TryGetSimplePostfixExpression(child)
                    : null;
            }

            public sealed class CompileTimeEvaluationState
            {
                private readonly Dictionary<string, CompileTimeBinding> _bindings = new(StringComparer.Ordinal);
                private readonly Stack<List<CompileTimeScopeEntry>> _scopes = new();

                public ISet<string>? GenericParameterNames { get; private set; }
                public IReadOnlyDictionary<string, StarkTypeSymbol>? TypeSubstitution { get; private set; }
                public IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? ComptimeGenericParameters { get; private set; }
                public IReadOnlyDictionary<string, BigInteger>? ComptimeValueSubstitution { get; private set; }
                public StarkTypeSymbol? CurrentReturnType { get; private set; }

                private CompileTimeConstant? PendingReturnValue { get; set; }

                public void SetGenericContext(
                    ISet<string>? genericParameterNames,
                    IReadOnlyDictionary<string, StarkTypeSymbol>? typeSubstitution,
                    IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? comptimeGenericParameters,
                    IReadOnlyDictionary<string, BigInteger>? comptimeValueSubstitution)
                {
                    GenericParameterNames = genericParameterNames;
                    TypeSubstitution = typeSubstitution;
                    ComptimeGenericParameters = comptimeGenericParameters;
                    ComptimeValueSubstitution = comptimeValueSubstitution;
                }

                public StarkTypeSymbol? SetCurrentReturnType(StarkTypeSymbol? returnType)
                {
                    var previous = CurrentReturnType;
                    CurrentReturnType = returnType;
                    return previous;
                }

                public void SetPendingReturn(CompileTimeConstant value)
                {
                    PendingReturnValue = value;
                }

                public bool TryConsumePendingReturn(out CompileTimeConstant value)
                {
                    if (PendingReturnValue is { } pending)
                    {
                        value = pending;
                        PendingReturnValue = null;
                        return true;
                    }

                    value = default;
                    return false;
                }

                public StarkTypeSymbol SubstituteType(StarkTypeSymbol type)
                {
                    return TypeSubstitution is { Count: > 0 } || ComptimeValueSubstitution is { Count: > 0 }
                        ? FunctionOverloadFacts.SubstituteType(
                            type,
                            TypeSubstitution ?? new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal),
                            comptimeValueSubstitution: ComptimeValueSubstitution)
                        : type;
                }

                public void PushScope()
                {
                    _scopes.Push([]);
                }

                public void PopScope()
                {
                    if (_scopes.Count == 0)
                    {
                        return;
                    }

                    foreach (var entry in _scopes.Pop().AsEnumerable().Reverse())
                    {
                        if (entry.HadPreviousBinding && entry.PreviousBinding is not null)
                        {
                            _bindings[entry.Name] = entry.PreviousBinding;
                        }
                        else
                        {
                            _bindings.Remove(entry.Name);
                        }
                    }
                }

                public void Declare(string name, CompileTimeConstant value, bool isMutable)
                {
                    if (_scopes.Count == 0)
                    {
                        PushScope();
                    }

                    var hadPreviousBinding = _bindings.TryGetValue(name, out var previousBinding);
                    _scopes.Peek().Add(new CompileTimeScopeEntry(name, hadPreviousBinding, previousBinding));
                    _bindings[name] = new CompileTimeBinding(value, isMutable);
                }

                public bool TryResolve(string name, out CompileTimeConstant value)
                {
                    if (_bindings.TryGetValue(name, out var binding))
                    {
                        value = binding.Value;
                        return true;
                    }

                    value = default;
                    return false;
                }

                public bool TryAssign(string name, CompileTimeConstant value)
                {
                    if (!_bindings.TryGetValue(name, out var binding) || !binding.IsMutable)
                    {
                        return false;
                    }

                    _bindings[name] = binding with { Value = value };
                    return true;
                }
            }

            private sealed record CompileTimeBinding(CompileTimeConstant Value, bool IsMutable);

            private sealed record CompileTimeScopeEntry(
                string Name,
                bool HadPreviousBinding,
                CompileTimeBinding? PreviousBinding);
        }
    }
}
