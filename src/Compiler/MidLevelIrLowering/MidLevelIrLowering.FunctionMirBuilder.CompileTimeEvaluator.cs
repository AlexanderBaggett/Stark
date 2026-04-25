using System.Numerics;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder
    {
        private sealed class CompileTimeEvaluator
        {
            private readonly FunctionMirBuilder _builder;

            public CompileTimeEvaluator(FunctionMirBuilder builder)
            {
                _builder = builder;
            }

            public bool TryEvaluateExpression(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string>? activeCalls,
                out CompileTimeConstant constant)
            {
                activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
                TryResolveCompileTimeIdentifier? nameResolver = state is null
                    ? null
                    : new TryResolveCompileTimeIdentifier(state.TryResolve);
                TryEvaluateCompileTimePostfixExpression postfixResolver =
                    (StarkParser.PostfixExpressionContext postfix, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                        TryEvaluateLawCall(postfix, moduleName, state, activeCalls, out value);
                var services = new CompileTimeEvaluationServices(
                    TryResolveIdentifier: nameResolver,
                    TryEvaluatePostfixExpression: postfixResolver);
                return CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant, services);
            }

            public bool TryEvaluateInteger(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string>? activeCalls,
                out BigInteger value)
            {
                activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
                TryResolveCompileTimeIdentifier? nameResolver = state is null
                    ? null
                    : new TryResolveCompileTimeIdentifier(state.TryResolve);
                TryEvaluateCompileTimePostfixExpression postfixResolver =
                    (StarkParser.PostfixExpressionContext postfix, CompileTimeEvaluationServices _, out CompileTimeConstant constant) =>
                        TryEvaluateLawCall(postfix, moduleName, state, activeCalls, out constant);
                var services = new CompileTimeEvaluationServices(
                    TryResolveIdentifier: nameResolver,
                    TryEvaluatePostfixExpression: postfixResolver);
                return CompileTimeExpressionEvaluator.TryEvaluateInteger(expression, out value, services);
            }

            private bool TryEvaluateLawCall(
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

                string? currentName = expression.primaryExpression().Identifier()?.GetText()
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
                        return index == expression.postfixPart().Length - 1
                            && currentName is not null
                            && ReferenceEquals(arguments, finalArguments)
                            && TryEvaluateCallByName(currentName, moduleName, arguments, state, activeCalls, out constant);
                    }

                    if (postfixPart.expressionList() is not null)
                    {
                        return false;
                    }

                    var memberName = postfixPart.Identifier()?.GetText();
                    if (memberName is null)
                    {
                        return false;
                    }

                    currentName = $"{currentName}.{memberName}";
                }

                return false;
            }

            private bool TryEvaluateCallByName(
                string functionName,
                string moduleName,
                StarkParser.ArgumentListContext arguments,
                CompileTimeEvaluationState? state,
                HashSet<string> activeCalls,
                out CompileTimeConstant constant)
            {
                constant = default;

                var argumentConstants = new List<CompileTimeConstant>(arguments.argument().Length);
                foreach (var argument in arguments.argument())
                {
                    if (!TryEvaluateExpression(argument.expression(), moduleName, state, activeCalls, out var argumentConstant))
                    {
                        return false;
                    }

                    argumentConstants.Add(argumentConstant);
                }

                TypedFunctionSignature signature;
                if (_builder.TryGetFunctionOverloads(functionName, moduleName, out var overloads))
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

                if (arguments.argument().Length != signature.Parameters.Count
                    || !_builder._functionsByName.TryGetValue(signature.Name, out var functionContext)
                    || functionContext.ParsedDeclaration is not { } parsedFunction
                    || !parsedFunction.HasBody
                    || parsedFunction.Body.block() is not { } body
                    || !FunctionKindFacts.IsLaw(parsedFunction.DeclaredKind)
                    || parsedFunction.TypeParameters is not null)
                {
                    return false;
                }

                var coercedArguments = new List<CompileTimeConstant>(argumentConstants.Count);
                for (var index = 0; index < argumentConstants.Count; index++)
                {
                    if (!CompileTimeExpressionEvaluator.TryCoerce(argumentConstants[index], signature.Parameters[index].Type, out var coerced))
                    {
                        return false;
                    }

                    coercedArguments.Add(coerced);
                }

                return TryExecuteFunction(signature, functionContext, body, coercedArguments, activeCalls, out constant);
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
                state.PushScope();
                try
                {
                    for (var index = 0; index < signature.Parameters.Count; index++)
                    {
                        state.Declare(signature.Parameters[index].Name, arguments[index], isMutable: false);
                    }

                    if (!TryExecuteBlock(body, functionContext.ModuleName, state, activeCalls, signature.ReturnType, out var returned, out var returnValue)
                        || !returned
                        || signature.ReturnType.Kind == StarkTypeKind.Void)
                    {
                        return false;
                    }

                    if (!CompileTimeExpressionEvaluator.TryCoerce(returnValue, signature.ReturnType, out constant))
                    {
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

            private bool TryExecuteBlock(
                StarkParser.BlockContext block,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out bool returned,
                out CompileTimeConstant returnValue)
            {
                returned = false;
                returnValue = default;
                state.PushScope();
                try
                {
                    foreach (var statement in block.statement())
                    {
                        if (!TryExecuteStatement(statement, moduleName, state, activeCalls, returnType, out returned, out returnValue))
                        {
                            return false;
                        }

                        if (returned)
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
                out bool returned,
                out CompileTimeConstant returnValue)
            {
                returned = false;
                returnValue = default;
                state.PushScope();
                try
                {
                    return TryExecuteStatement(statement, moduleName, state, activeCalls, returnType, out returned, out returnValue);
                }
                finally
                {
                    state.PopScope();
                }
            }

            private bool TryExecuteStatement(
                StarkParser.StatementContext statement,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls,
                StarkTypeSymbol returnType,
                out bool returned,
                out CompileTimeConstant returnValue)
            {
                returned = false;
                returnValue = default;

                if (statement.block() is { } block)
                {
                    return TryExecuteBlock(block, moduleName, state, activeCalls, returnType, out returned, out returnValue);
                }

                if (statement.localConstantDeclaration() is { } localConstant)
                {
                    var declaredType = _builder.TryResolveLocalDeclarationType(
                        TemplateLocalDeclarationFacts.ConstantKind,
                        localConstant,
                        out var typedLocalType)
                        ? typedLocalType
                        : localConstant.type_() is { } typeContext
                            ? _builder.ResolveTypeWithGenericSubstitution(typeContext, moduleName)
                            : StarkTypeSymbols.Error;
                    foreach (var declarator in localConstant.constantDeclarators().constantDeclarator())
                    {
                        if (declarator.variableInitializer()?.expression() is not { } initializerExpression
                            || !TryEvaluateExpression(initializerExpression, moduleName, state, activeCalls, out var initializer)
                            || !CompileTimeExpressionEvaluator.TryCoerce(initializer, declaredType, out var coerced))
                        {
                            return false;
                        }

                        state.Declare(declarator.Identifier().GetText(), coerced, isMutable: false);
                    }

                    return true;
                }

                if (statement.localVariableDeclaration() is { } localVariable)
                {
                    var declaredType = _builder.ResolveTypeWithGenericSubstitution(localVariable.type_(), moduleName);
                    foreach (var declarator in localVariable.variableDeclarators().variableDeclarator())
                    {
                        if (declarator.variableInitializer()?.expression() is not { } initializerExpression
                            || !TryEvaluateExpression(initializerExpression, moduleName, state, activeCalls, out var initializer)
                            || !CompileTimeExpressionEvaluator.TryCoerce(initializer, declaredType, out var coerced))
                        {
                            return false;
                        }

                        state.Declare(declarator.Identifier().GetText(), coerced, isMutable: localVariable.MUT() is not null);
                    }

                    return true;
                }

                if (statement.ifStatement() is { } ifStatement)
                {
                    if (!TryEvaluateExpression(ifStatement.expression(), moduleName, state, activeCalls, out var condition)
                        || condition.Kind != CompileTimeConstantKind.Bool)
                    {
                        return false;
                    }

                    if (!condition.BoolValue)
                    {
                        return ifStatement.statement().Length < 2
                            || TryExecuteScopedStatement(ifStatement.statement(1), moduleName, state, activeCalls, returnType, out returned, out returnValue);
                    }

                    return TryExecuteScopedStatement(ifStatement.statement(0), moduleName, state, activeCalls, returnType, out returned, out returnValue);
                }

                if (statement.returnStatement() is { } returnStatement)
                {
                    returned = true;
                    if (returnStatement.expression() is null)
                    {
                        return returnType.Kind == StarkTypeKind.Void;
                    }

                    if (!TryEvaluateExpression(returnStatement.expression(), moduleName, state, activeCalls, out var computed)
                        || !CompileTimeExpressionEvaluator.TryCoerce(computed, returnType, out returnValue))
                    {
                        returned = false;
                        return false;
                    }

                    return true;
                }

                if (statement.expressionStatement() is { } expressionStatement)
                {
                    return TryHandleAssignmentStatement(expressionStatement.expression(), moduleName, state, activeCalls)
                        || TryEvaluateExpression(expressionStatement.expression(), moduleName, state, activeCalls, out _);
                }

                return false;
            }

            private bool TryHandleAssignmentStatement(
                StarkParser.ExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState state,
                HashSet<string> activeCalls)
            {
                var assignment = expression.assignmentExpression();
                if (assignment.assignmentOperator() is null
                    || assignment.assignmentOperator().GetText() != "="
                    || assignment.unaryExpression() is not { } unaryExpression
                    || !TryResolveAssignmentTarget(unaryExpression, out var targetName)
                    || !state.TryResolve(targetName, out var targetValue)
                    || !TryEvaluateAssignmentExpression(assignment.assignmentExpression(), moduleName, state, activeCalls, out var assignedValue)
                    || !CompileTimeExpressionEvaluator.TryCoerce(assignedValue, targetValue.Type, out var coerced))
                {
                    return false;
                }

                return state.TryAssign(targetName, coerced);
            }

            private bool TryEvaluateAssignmentExpression(
                StarkParser.AssignmentExpressionContext expression,
                string moduleName,
                CompileTimeEvaluationState? state,
                HashSet<string>? activeCalls,
                out CompileTimeConstant constant)
            {
                activeCalls ??= new HashSet<string>(StringComparer.Ordinal);
                TryResolveCompileTimeIdentifier? nameResolver = state is null
                    ? null
                    : new TryResolveCompileTimeIdentifier(state.TryResolve);
                TryEvaluateCompileTimePostfixExpression postfixResolver =
                    (StarkParser.PostfixExpressionContext postfix, CompileTimeEvaluationServices _, out CompileTimeConstant value) =>
                        TryEvaluateLawCall(postfix, moduleName, state, activeCalls, out value);
                var services = new CompileTimeEvaluationServices(
                    TryResolveIdentifier: nameResolver,
                    TryEvaluatePostfixExpression: postfixResolver);
                return CompileTimeExpressionEvaluator.TryEvaluate(expression, out constant, services);
            }

            private static bool TryResolveAssignmentTarget(
                StarkParser.UnaryExpressionContext expression,
                out string name)
            {
                name = string.Empty;

                if (FunctionMirBuilder.TryGetSimplePostfixExpression(expression) is not { } postfix
                    || postfix.postfixPart().Length != 0
                    || postfix.primaryExpression().Identifier() is not { } identifier)
                {
                    return false;
                }

                name = identifier.GetText();
                return true;
            }

            public sealed class CompileTimeEvaluationState
            {
                private readonly Dictionary<string, CompileTimeBinding> _bindings = new(StringComparer.Ordinal);
                private readonly Stack<List<CompileTimeScopeEntry>> _scopes = new();

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
