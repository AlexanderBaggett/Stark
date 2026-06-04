using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class LoweringContractValidator
{
    private const string Stage = "validate-lowering-contract";

    private readonly CompilerPassContext _context;
    private readonly LoadedModuleSet _loadedModules;
    private readonly TypeCheckModel _typeModel;
    private readonly IReadOnlyDictionary<string, EnumLayoutSymbol> _enumLayouts;
    private readonly Dictionary<OperationKey, DirectCallTypingRecord> _directCalls;
    private readonly Dictionary<OperationKey, MemberCallTypingRecord> _memberCalls;
    private readonly Dictionary<OperationKey, IndirectCallTypingRecord> _indirectCalls;
    private readonly Dictionary<OperationKey, ClosureCallTypingRecord> _closureCalls;
    private readonly Dictionary<OperationKey, EnumCallTypingRecord> _enumCalls;
    private readonly Dictionary<OperationKey, IndexAccessTypingRecord> _indexAccesses;
    private readonly Dictionary<OperationKey, DynamicStorageOperationTypingRecord> _dynamicStorageOperations;
    private readonly Dictionary<OperationKey, SwitchTypingRecord> _switches;
    private readonly Dictionary<OperationKey, EnumPatternTypingRecord> _enumPatterns;
    private readonly Dictionary<OperationKey, AggregatePatternTypingRecord> _aggregatePatterns;
    private readonly Dictionary<OperationKey, ObjectCreationTypingRecord> _objectCreations;
    private readonly Dictionary<OperationKey, EnumConstructorTypingRecord> _enumConstructors;
    private readonly Dictionary<OperationKey, TypeLayoutExpressionTypingRecord> _typeLayoutExpressions;
    private readonly Dictionary<OperationKey, LocalDeclarationTypingRecord> _localDeclarations;
    private readonly Dictionary<OperationKey, IReadOnlyList<BoundOperation>> _boundOperations;
    private readonly Dictionary<OperationKey, LambdaTypingRecord> _lambdas;
    private readonly Dictionary<OperationKey, ClosureLambdaTypingRecord> _closureLambdas;

    private int _checkedFunctionCount;
    private int _checkedCallCount;
    private int _checkedIndexAccessCount;
    private int _checkedObjectCreationCount;
    private int _checkedEnumConstructorCount;
    private int _checkedLambdaCount;
    private int _checkedTypeLayoutExpressionCount;
    private int _checkedDynamicStorageOperationCount;
    private int _checkedSwitchCount;

    public LoweringContractValidator(
        CompilerPassContext context,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        EnumLayoutModel? enumLayoutModel = null)
    {
        _context = context;
        _loadedModules = loadedModules;
        _typeModel = typeModel;
        _enumLayouts = enumLayoutModel?.Layouts ?? new Dictionary<string, EnumLayoutSymbol>(StringComparer.Ordinal);
        _directCalls = BuildOperationMap(typeModel.DirectCalls, static record => Key(record.EnclosingFunctionName, record.Location));
        _memberCalls = BuildOperationMap(typeModel.MemberCalls, static record => Key(record.EnclosingFunctionName, record.Location));
        _indirectCalls = BuildOperationMap(typeModel.IndirectCalls, static record => Key(record.EnclosingFunctionName, record.Location));
        _closureCalls = BuildOperationMap(typeModel.ClosureCalls, static record => Key(record.EnclosingFunctionName, record.Location));
        _enumCalls = BuildOperationMap(typeModel.EnumCalls, static record => Key(record.EnclosingFunctionName, record.Location));
        _indexAccesses = BuildOperationMap(typeModel.IndexAccesses, static record => Key(record.EnclosingFunctionName, record.Location));
        _dynamicStorageOperations = BuildOperationMap(typeModel.DynamicStorageOperations, static record => Key(record.EnclosingFunctionName, record.Location));
        _switches = BuildOperationMap(typeModel.Switches, static record => Key(record.EnclosingFunctionName, record.Location));
        _enumPatterns = BuildOperationMap(typeModel.EnumPatterns, static record => Key(record.EnclosingFunctionName, record.Location));
        _aggregatePatterns = BuildOperationMap(typeModel.AggregatePatterns, static record => Key(record.EnclosingFunctionName, record.Location));
        _objectCreations = BuildOperationMap(typeModel.ObjectCreations, static record => Key(record.EnclosingFunctionName, record.Location));
        _enumConstructors = BuildOperationMap(typeModel.EnumConstructors, static record => Key(record.EnclosingFunctionName, record.Location));
        _typeLayoutExpressions = BuildOperationMap(typeModel.TypeLayoutExpressions, static record => Key(record.EnclosingFunctionName, record.Location));
        _localDeclarations = BuildOperationMap(typeModel.LocalDeclarations, static record => Key(record.EnclosingFunctionName, record.Location));
        _boundOperations = typeModel.BoundOperations
            .GroupBy(static operation => Key(operation.EnclosingFunctionName, operation.Location))
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<BoundOperation>)group.ToArray());
        _lambdas = typeModel.Lambdas
            .GroupBy(static record => Key(record.EnclosingFunctionName, record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last());
        _closureLambdas = typeModel.ClosureLambdas
            .GroupBy(static record => Key(record.EnclosingFunctionName, record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last());
    }

    public LoweringContractValidationModel Validate()
    {
        foreach (var module in _loadedModules.Modules.Values)
        {
            if (module.IsPackageImageImport)
            {
                continue;
            }

            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var functionName = QualifyName(module, declaration.Name);
                if (!_typeModel.Functions.TryGetValue(functionName, out var signature))
                {
                    continue;
                }

                if (!module.Reference.IsRoot && !signature.IsGeneric)
                {
                    continue;
                }

                if (declaration.Body.block() is not { } body)
                {
                    continue;
                }

                _checkedFunctionCount++;
                ValidateTree(body, functionName, module.Reference.FilePath);
            }
        }

        return new LoweringContractValidationModel(
            _loadedModules.RootModuleName,
            _checkedFunctionCount,
            _checkedCallCount,
            _checkedIndexAccessCount,
            _checkedObjectCreationCount,
            _checkedEnumConstructorCount,
            _checkedLambdaCount,
            _checkedTypeLayoutExpressionCount,
            _checkedDynamicStorageOperationCount,
            _checkedSwitchCount);
    }

    private void ValidateTree(IParseTree current, string functionName, string? filePath)
    {
        switch (current)
        {
            case StarkParser.LambdaExpressionContext lambda:
                ValidateLambda(lambda, functionName, filePath);
                return;

            case StarkParser.PostfixExpressionContext postfix:
                ValidatePostfix(postfix, functionName, filePath);
                break;

            case StarkParser.ObjectCreationExpressionContext objectCreation:
                ValidateObjectCreation(objectCreation, functionName, filePath);
                break;

            case StarkParser.EnumConstructorExpressionContext enumConstructor:
                ValidateEnumConstructor(enumConstructor, functionName, filePath);
                break;

            case StarkParser.PrimaryExpressionContext primary when primary.SIZEOF() is not null || primary.ALIGNOF() is not null:
                ValidateTypeLayoutExpression(primary, functionName, filePath);
                break;

            case StarkParser.LiteralContext literal when literal.DOLLAR() is not null && literal.StringLiteral() is not null:
                ValidateTextInterpolation(literal, functionName, filePath);
                break;

            case StarkParser.LocalVariableDeclarationContext localVariable:
                ValidateFixedTextStorageInitializers(localVariable, functionName, filePath);
                break;

            case StarkParser.AdditiveExpressionContext additive:
                ValidateOptionalTextBuild(additive, functionName, filePath);
                break;

            case StarkParser.SwitchStatementContext switchStatement:
                ValidateSwitch(switchStatement, functionName, filePath);
                break;
        }

        for (var index = 0; index < current.ChildCount; index++)
        {
            ValidateTree(current.GetChild(index), functionName, filePath);
        }
    }

    private void ValidatePostfix(StarkParser.PostfixExpressionContext postfix, string functionName, string? filePath)
    {
        var parts = postfix.postfixPart();
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.argumentList() is { } arguments)
            {
                if (IsUnsafeRawSliceConstructionPrefix(postfix, index))
                {
                    continue;
                }

                if (TryGetRecord(_dynamicStorageOperations, arguments, functionName, out var dynamicStorageOperation))
                {
                    ValidateBoundDynamicStorageOperation(dynamicStorageOperation, arguments, functionName, filePath);
                    ValidateDynamicStorageOperation(postfix, index, dynamicStorageOperation, arguments, filePath);
                    _checkedDynamicStorageOperationCount++;
                    continue;
                }

                if (TryGetRecord(_directCalls, arguments, functionName, out var directCall))
                {
                    ValidateBoundDirectCallOperation(directCall, arguments, functionName, filePath);
                    ValidateFunctionCallArity(
                        "direct-call",
                        directCall.Signature.DisplaySourceName,
                        directCall.Signature.Parameters.Count,
                        directCall.Signature.IsVarargs,
                        arguments.argument().Length,
                        arguments,
                        filePath);
                    ValidateCallArgumentFacts(
                        "direct-call",
                        directCall.Signature.DisplaySourceName,
                        directCall.Signature.Parameters,
                        receiverOffset: 0,
                        directCall.Arguments,
                        arguments,
                        filePath);
                    _checkedCallCount++;
                    continue;
                }

                if (TryGetRecord(_memberCalls, arguments, functionName, out var memberCall))
                {
                    ValidateBoundMemberCallOperation(memberCall, arguments, functionName, filePath);
                    if (memberCall.Signature.Parameters.Count == 0)
                    {
                        ReportInvalid(
                            arguments,
                            filePath,
                            $"Typed member-call fact for '{memberCall.Signature.DisplaySourceName}' does not include a receiver parameter. MIR lowering requires receiver binding before explicit arguments.");
                    }
                    else
                    {
                        ValidateFunctionCallArity(
                            "member-call",
                            memberCall.Signature.DisplaySourceName,
                            memberCall.Signature.Parameters.Count - 1,
                            memberCall.Signature.IsVarargs,
                            arguments.argument().Length,
                            arguments,
                            filePath);
                        ValidateCallArgumentFacts(
                            "member-call",
                            memberCall.Signature.DisplaySourceName,
                            memberCall.Signature.Parameters,
                            receiverOffset: 1,
                            memberCall.Arguments,
                            arguments,
                            filePath);
                    }

                    _checkedCallCount++;
                    continue;
                }

                if (TryGetRecord(_indirectCalls, arguments, functionName, out var indirectCall))
                {
                    ValidateBoundFunctionPointerCallOperation(indirectCall, arguments, functionName, filePath);
                    ValidateIndirectCall(indirectCall, arguments, filePath);
                    _checkedCallCount++;
                    continue;
                }

                if (TryGetRecord(_closureCalls, arguments, functionName, out var closureCall))
                {
                    ValidateBoundClosureCallOperation(closureCall, arguments, functionName, filePath);
                    ValidateClosureCall(closureCall, arguments, filePath);
                    _checkedCallCount++;
                    continue;
                }

                if (TryGetRecord(_enumCalls, arguments, functionName, out var enumCall))
                {
                    ValidateBoundEnumCallOperation(enumCall, arguments, functionName, filePath);
                    ValidateEnumCall(enumCall, arguments, filePath);
                    _checkedCallCount++;
                    continue;
                }

                ReportMissing(
                    arguments,
                    filePath,
                    "Lowering contract is missing typed call facts for this call expression. Type checking must record a direct, member, indirect, closure, enum-constructor, or dynamic-storage operation before MIR lowering.");
                continue;
            }

            if (part.expressionList() is { } indexes)
            {
                if (TryGetRecord(_indexAccesses, indexes, functionName, out var indexAccess))
                {
                    ValidateBoundIndexAccessOperation(indexAccess, indexes, functionName, filePath);
                    ValidateIndexAccess(indexAccess, indexes, filePath);
                    _checkedIndexAccessCount++;
                    continue;
                }

                ReportMissing(
                    indexes,
                    filePath,
                    "Lowering contract is missing typed indexing facts for this index or slice expression. Type checking must record the operation family, arity, source type, and result type before MIR lowering.");
            }
        }
    }

    private void ValidateObjectCreation(
        StarkParser.ObjectCreationExpressionContext expression,
        string functionName,
        string? filePath)
    {
        if (!ShouldTrackObjectCreation(expression))
        {
            return;
        }

        if (TryGetRecord(_objectCreations, expression, functionName, out var objectCreation))
        {
            ValidateBoundObjectCreationOperation(objectCreation, expression, functionName, filePath);
            ValidateObjectCreationFact(objectCreation, expression, filePath);
            _checkedObjectCreationCount++;
            return;
        }

        ReportMissing(
            expression,
            filePath,
            "Lowering contract is missing typed object-creation facts. Type checking must record the created type, constructor shape, and initializer field mapping before MIR lowering.");
    }

    private void ValidateEnumConstructor(
        StarkParser.EnumConstructorExpressionContext expression,
        string functionName,
        string? filePath)
    {
        if (TryGetRecord(_enumConstructors, expression, functionName, out var enumConstructor))
        {
            ValidateBoundEnumConstructionOperation(enumConstructor, expression, functionName, filePath);
            ValidateEnumConstructorFact(enumConstructor, expression, filePath);
            _checkedEnumConstructorCount++;
            return;
        }

        ReportMissing(
            expression,
            filePath,
            "Lowering contract is missing typed named-field enum-constructor facts. Type checking must record the enum type, variant, and payload field mapping before MIR lowering.");
    }

    private void ValidateTypeLayoutExpression(
        StarkParser.PrimaryExpressionContext expression,
        string functionName,
        string? filePath)
    {
        if (TryGetRecord(_typeLayoutExpressions, expression, functionName, out var layoutExpression))
        {
            ValidateBoundLayoutQueryOperation(layoutExpression, expression, functionName, filePath);
            ValidateTypeLayoutFact(layoutExpression, expression, functionName, filePath);
            _checkedTypeLayoutExpressionCount++;
            return;
        }

        ReportMissing(
            expression,
            filePath,
            "Lowering contract is missing typed layout-query facts. Type checking must record the concrete target type for sizeof/alignof before MIR lowering.");
    }

    private void ValidateLambda(StarkParser.LambdaExpressionContext expression, string functionName, string? filePath)
    {
        var key = Key(functionName, expression);
        if (!_lambdas.TryGetValue(key, out var lambda)
            && !_lambdas.TryGetValue(key with { FunctionName = null }, out lambda))
        {
            if (!_closureLambdas.TryGetValue(key, out var closureLambda)
                && !_closureLambdas.TryGetValue(key with { FunctionName = null }, out closureLambda))
            {
                ReportMissing(
                    expression,
                    filePath,
                    "Lowering contract is missing typed lambda facts. Type checking must record the generated function name, callable target type, and parameter names before MIR lowering.");
                return;
            }

            _checkedLambdaCount++;
            ValidateClosureLambdaFact(closureLambda, expression, filePath);
            if (expression.expression() is { } closureBodyExpression)
            {
                ValidateTree(closureBodyExpression, closureLambda.FunctionName, filePath);
            }
            else if (expression.block() is { } closureBlock)
            {
                ValidateTree(closureBlock, closureLambda.FunctionName, filePath);
            }

            return;
        }

        _checkedLambdaCount++;
        ValidateLambdaFact(lambda, expression, filePath);
        if (expression.expression() is { } bodyExpression)
        {
            ValidateTree(bodyExpression, lambda.FunctionName, filePath);
        }
        else if (expression.block() is { } block)
        {
            ValidateTree(block, lambda.FunctionName, filePath);
        }
    }

    private void ValidateSwitch(StarkParser.SwitchStatementContext switchStatement, string functionName, string? filePath)
    {
        if (TryGetRecord(_switches, switchStatement, functionName, out var switchRecord))
        {
            ValidateBoundSwitchDispatchOperation(switchRecord, switchStatement, functionName, filePath);
            ValidateSwitchFact(switchRecord, switchStatement, filePath);
            ValidateSwitchPatternFacts(switchStatement, functionName, filePath);
            _checkedSwitchCount++;
            return;
        }

        ReportMissing(
            switchStatement,
            filePath,
            "Lowering contract is missing typed switch-lowering facts. Type checking must record the switch domain, dispatch family, label counts, and structured pattern facts before MIR lowering.");
    }

    private void ValidateTextInterpolation(StarkParser.LiteralContext literal, string functionName, string? filePath)
    {
        if (!TryGetBoundOperation(literal, functionName, out BoundTextInterpolationOperation operation))
        {
            ReportMissingBoundOperation(literal, filePath, "text-interpolation");
            return;
        }

        if (!InterpolatedText.TryParse(literal.StringLiteral()!.GetText(), out var segments, out _))
        {
            return;
        }

        var holeCount = segments.OfType<InterpolatedTextHoleSegment>().Count();
        if (operation.SegmentCount != segments.Count
            || operation.HoleCount != holeCount
            || operation.ResultType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            ReportInvalid(
                literal,
                filePath,
                "Bound text-interpolation operation does not match the source interpolation shape or result type.");
        }
    }

    private void ValidateOptionalTextBuild(StarkParser.AdditiveExpressionContext additive, string functionName, string? filePath)
    {
        if (!TryGetBoundOperation(additive, functionName, out BoundTextBuildOperation operation))
        {
            if (TryGetRecord(_directCalls, additive, functionName, out var directCall)
                && IsRuntimeTextBuildCall(directCall.Signature))
            {
                ReportMissingBoundOperation(additive, filePath, "text-build");
            }

            return;
        }

        var operandCount = additive.multiplicativeExpression().Length;
        if (operation.OperandCount != operandCount
            || operation.ResultType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            ReportInvalid(
                additive,
                filePath,
                $"Bound text-build operation records {operation.OperandCount} operand(s), but the source has {operandCount}.");
        }
    }

    private void ValidateRequiredTextBuild(StarkParser.AdditiveExpressionContext additive, string functionName, string? filePath)
    {
        if (!TryGetBoundOperation(additive, functionName, out BoundTextBuildOperation operation))
        {
            ReportMissingBoundOperation(additive, filePath, "text-build");
            return;
        }

        var operands = additive.multiplicativeExpression();
        var operators = ExtractOperators<StarkParser.MultiplicativeExpressionContext>(additive);
        if (operands.Length < 2
            || operators.Any(static item => item != "+")
            || !string.Equals(operation.BuildKind, "concat", StringComparison.Ordinal)
            || !operation.UsesFixedStorage
            || operation.OperandCount != operands.Length
            || operation.ResultType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            ReportInvalid(
                additive,
                filePath,
                "Bound fixed text-build operation does not match the source concatenation shape or result type.");
        }
    }

    private void ValidateFixedTextStorageInitializers(
        StarkParser.LocalVariableDeclarationContext declaration,
        string functionName,
        string? filePath)
    {
        if (!TryGetRecord(_localDeclarations, declaration, functionName, out var localDeclaration)
            || !IsTextBufferType(localDeclaration.Type))
        {
            return;
        }

        foreach (var declarator in declaration.variableDeclarators().variableDeclarator())
        {
            if (declarator.variableStorageCapacity() is null
                || declarator.variableInitializer()?.expression() is not { } initializer)
            {
                continue;
            }

            if (TryGetStandaloneInterpolatedTextLiteral(initializer) is { } interpolatedLiteral)
            {
                ValidateTextInterpolation(interpolatedLiteral, functionName, filePath);
                continue;
            }

            if (TryGetStandaloneAdditiveExpression(initializer) is { } additive)
            {
                ValidateRequiredTextBuild(additive, functionName, filePath);
            }
        }
    }

    private static bool IsRuntimeTextBuildCall(TypedFunctionSignature signature)
    {
        return signature.DisplaySourceName is "System.Text.ConcatAscii" or "System.Text.ConcatUnicode"
            || signature.Name is "System.Text.ConcatAscii" or "System.Text.ConcatUnicode"
            || signature.TemplateName is "System.Text.ConcatAscii" or "System.Text.ConcatUnicode";
    }

    private void ValidateBoundDirectCallOperation(
        DirectCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(arguments, functionName, out BoundDirectCallOperation operation))
        {
            ReportMissingBoundOperation(arguments, filePath, "direct-call");
            return;
        }

        if (!Equals(operation.Signature, record.Signature))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Bound direct-call operation records '{operation.Signature.DisplaySourceName}', but typed call facts record '{record.Signature.DisplaySourceName}'.");
        }

        ValidateBoundCallArgumentRecords("direct-call", operation.Arguments, record.Arguments, arguments, filePath);
    }

    private void ValidateBoundMemberCallOperation(
        MemberCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(arguments, functionName, out BoundMemberCallOperation operation))
        {
            ReportMissingBoundOperation(arguments, filePath, "member-call");
            return;
        }

        if (!Equals(operation.Signature, record.Signature))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Bound member-call operation records '{operation.Signature.DisplaySourceName}', but typed member-call facts record '{record.Signature.DisplaySourceName}'.");
        }

        var receiver = record.Arguments.FirstOrDefault(static argument => argument.IsReceiver);
        if (receiver is not null)
        {
            if (!Equals(operation.ReceiverType, receiver.ArgumentType)
                || operation.ReceiverIsAddressable != receiver.ArgumentIsAddressable
                || operation.ReceiverIsMutable != receiver.ArgumentIsMutable)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Bound member-call operation for '{record.Signature.DisplaySourceName}' does not match the receiver type/addressability facts.");
            }
        }

        ValidateBoundCallArgumentRecords("member-call", operation.Arguments, record.Arguments, arguments, filePath);
    }

    private void ValidateBoundFunctionPointerCallOperation(
        IndirectCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(arguments, functionName, out BoundFunctionPointerCallOperation operation))
        {
            ReportMissingBoundOperation(arguments, filePath, "function-pointer-call");
            return;
        }

        if (!Equals(operation.FunctionPointerType, record.FunctionPointerType))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Bound function-pointer call operation records target type '{operation.FunctionPointerType.DisplayName}', but typed indirect-call facts record '{record.FunctionPointerType.DisplayName}'.");
        }

        ValidateBoundCallArgumentRecords("function-pointer-call", operation.Arguments, record.Arguments, arguments, filePath);
    }

    private void ValidateBoundClosureCallOperation(
        ClosureCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(arguments, functionName, out BoundClosureCallOperation operation))
        {
            ReportMissingBoundOperation(arguments, filePath, "closure-call");
            return;
        }

        if (!Equals(operation.ClosureType, record.ClosureType))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Bound closure-call operation records target type '{operation.ClosureType.DisplayName}', but typed closure-call facts record '{record.ClosureType.DisplayName}'.");
        }

        ValidateBoundCallArgumentRecords("closure-call", operation.Arguments, record.Arguments, arguments, filePath);
    }

    private void ValidateBoundEnumCallOperation(
        EnumCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(arguments, functionName, out BoundEnumCallOperation operation))
        {
            ReportMissingBoundOperation(arguments, filePath, "enum-call");
            return;
        }

        if (!Equals(operation.EnumType, record.EnumType)
            || !string.Equals(operation.VariantName, record.VariantName, StringComparison.Ordinal))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Bound enum-call operation records '{operation.EnumType.DisplayName}.{operation.VariantName}', but typed enum-call facts record '{record.EnumType.DisplayName}.{record.VariantName}'.");
        }
    }

    private void ValidateBoundIndexAccessOperation(
        IndexAccessTypingRecord record,
        StarkParser.ExpressionListContext indexes,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(indexes, functionName, out BoundIndexAccessOperation operation))
        {
            ReportMissingBoundOperation(indexes, filePath, "index-or-slice");
            return;
        }

        if (!string.Equals(operation.SourceKind, record.Kind, StringComparison.Ordinal)
            || operation.IndexCount != record.IndexCount
            || !Equals(operation.SourceType, record.SourceType)
            || !Equals(operation.ResultType, record.ResultType))
        {
            ReportInvalid(
                indexes,
                filePath,
                $"Bound index operation for '{operation.SourceKind}' does not match typed index facts for '{record.Kind}'.");
        }
    }

    private void ValidateBoundDynamicStorageOperation(
        DynamicStorageOperationTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(arguments, functionName, out BoundDynamicStorageOperation operation))
        {
            ReportMissingBoundOperation(arguments, filePath, "dynamic-storage");
            return;
        }

        if (!string.Equals(operation.OperationName, record.OperationName, StringComparison.Ordinal)
            || operation.ArgumentCount != record.ArgumentCount
            || operation.ReceiverIsAddressable != record.ReceiverIsAddressable
            || operation.ReceiverIsMutable != record.ReceiverIsMutable
            || !Equals(operation.ReceiverType, record.ReceiverType)
            || !Equals(operation.ResultType, record.ResultType))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Bound dynamic-storage operation for '{operation.OperationName}' does not match typed dynamic-storage facts for '{record.OperationName}'.");
        }
    }

    private void ValidateBoundObjectCreationOperation(
        ObjectCreationTypingRecord record,
        StarkParser.ObjectCreationExpressionContext expression,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(expression, functionName, out BoundObjectCreationOperation operation))
        {
            ReportMissingBoundOperation(expression, filePath, "object-creation");
            return;
        }

        if (!Equals(operation.CreatedType, record.CreatedType)
            || !Equals(operation.Constructor, record.Constructor)
            || operation.Members.Count != record.Members.Count)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Bound object-creation operation for '{operation.CreatedType.DisplayName}' does not match typed object-creation facts for '{record.CreatedType.DisplayName}'.");
        }
    }

    private void ValidateBoundEnumConstructionOperation(
        EnumConstructorTypingRecord record,
        StarkParser.EnumConstructorExpressionContext expression,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(expression, functionName, out BoundEnumConstructionOperation operation))
        {
            ReportMissingBoundOperation(expression, filePath, "enum-construction");
            return;
        }

        if (!Equals(operation.EnumType, record.EnumType)
            || !string.Equals(operation.VariantName, record.VariantName, StringComparison.Ordinal)
            || operation.Members.Count != record.Members.Count)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Bound enum-construction operation for '{operation.EnumType.DisplayName}.{operation.VariantName}' does not match typed enum-constructor facts for '{record.EnumType.DisplayName}.{record.VariantName}'.");
        }
    }

    private void ValidateBoundLayoutQueryOperation(
        TypeLayoutExpressionTypingRecord record,
        StarkParser.PrimaryExpressionContext expression,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(expression, functionName, out BoundLayoutQueryOperation operation))
        {
            ReportMissingBoundOperation(expression, filePath, "layout-query");
            return;
        }

        var expectedKind = string.Equals(record.Kind, "alignof", StringComparison.Ordinal)
            ? BoundLayoutQueryKind.AlignOf
            : BoundLayoutQueryKind.SizeOf;
        if (operation.QueryKind != expectedKind
            || !Equals(operation.TargetType, record.TargetType))
        {
            ReportInvalid(
                expression,
                filePath,
                $"Bound layout-query operation records '{operation.QueryKind}' for '{operation.TargetType.DisplayName}', but typed layout facts record '{record.Kind}' for '{record.TargetType.DisplayName}'.");
        }
    }

    private void ValidateBoundSwitchDispatchOperation(
        SwitchTypingRecord record,
        StarkParser.SwitchStatementContext switchStatement,
        string functionName,
        string? filePath)
    {
        if (!TryGetBoundOperation(switchStatement, functionName, out BoundSwitchDispatchOperation operation))
        {
            ReportMissingBoundOperation(switchStatement, filePath, "switch-dispatch");
            return;
        }

        if (!string.Equals(operation.Family, record.Family, StringComparison.Ordinal)
            || !Equals(operation.SwitchType, record.SwitchType)
            || operation.SectionCount != record.SectionCount
            || operation.LabelCount != record.LabelCount
            || operation.ExplicitDefaultLabelCount != record.ExplicitDefaultLabelCount
            || operation.LoweredDefaultLabelCount != record.LoweredDefaultLabelCount
            || operation.LiteralLabelCount != record.LiteralLabelCount
            || operation.MatchAllLabelCount != record.MatchAllLabelCount
            || operation.CaptureLabelCount != record.CaptureLabelCount
            || operation.StructuredPatternLabelCount != record.StructuredPatternLabelCount
            || operation.GuardedLabelCount != record.GuardedLabelCount)
        {
            ReportInvalid(
                switchStatement,
                filePath,
                $"Bound switch-dispatch operation for '{operation.SwitchType.DisplayName}' does not match typed switch facts for '{record.SwitchType.DisplayName}'.");
        }
    }

    private void ValidateBoundCallArgumentRecords(
        string factKind,
        IReadOnlyList<CallArgumentTypingRecord> boundArguments,
        IReadOnlyList<CallArgumentTypingRecord> typedArguments,
        ParserRuleContext context,
        string? filePath)
    {
        if (boundArguments.Count != typedArguments.Count
            || !boundArguments.SequenceEqual(typedArguments))
        {
            ReportInvalid(
                context,
                filePath,
                $"Bound {factKind} operation argument facts do not match typed {factKind} facts.");
        }
    }

    private void ReportMissingBoundOperation(ParserRuleContext context, string? filePath, string operationFamily)
    {
        ReportMissing(
            context,
            filePath,
            $"Lowering contract is missing a bound {operationFamily} operation for this executable expression. Type checking must publish a closed BoundOperation before MIR lowering.");
    }

    private void ValidateSwitchFact(
        SwitchTypingRecord record,
        StarkParser.SwitchStatementContext switchStatement,
        string? filePath)
    {
        var shape = InspectSwitchShape(switchStatement);
        if (!SwitchLoweringFamilies.IsKnown(record.Family))
        {
            ReportInvalid(
                switchStatement,
                filePath,
                $"Typed switch fact records unknown lowering family '{record.Family}'.");
        }

        if (record.SwitchType.Kind is StarkTypeKind.Error or StarkTypeKind.Void
            || !CanLowerImplementedSwitchType(record.SwitchType))
        {
            ReportInvalid(
                switchStatement,
                filePath,
                $"Typed switch fact must carry a supported concrete switch domain, but found '{record.SwitchType.DisplayName}'.");
        }

        ValidateSwitchCount(
            "section",
            record.SectionCount,
            shape.SectionCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "label",
            record.LabelCount,
            shape.LabelCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "explicit-default label",
            record.ExplicitDefaultLabelCount,
            shape.ExplicitDefaultLabelCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "lowered-default label",
            record.LoweredDefaultLabelCount,
            shape.LoweredDefaultLabelCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "literal label",
            record.LiteralLabelCount,
            shape.LiteralLabelCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "match-all label",
            record.MatchAllLabelCount,
            shape.MatchAllLabelCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "capture label",
            record.CaptureLabelCount,
            shape.CaptureLabelCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "structured-pattern label",
            record.StructuredPatternLabelCount,
            shape.StructuredPatternLabelCount,
            switchStatement,
            filePath);
        ValidateSwitchCount(
            "guarded label",
            record.GuardedLabelCount,
            shape.GuardedLabelCount,
            switchStatement,
            filePath);

        var expectedFamily = ClassifySwitchFamily(record.SwitchType, shape);
        if (!string.Equals(record.Family, expectedFamily, StringComparison.Ordinal))
        {
            ReportInvalid(
                switchStatement,
                filePath,
                $"Typed switch fact records lowering family '{record.Family}', but the source switch shape requires '{expectedFamily}'.");
        }
    }

    private void ValidateSwitchPatternFacts(StarkParser.SwitchStatementContext switchStatement, string functionName, string? filePath)
    {
        foreach (var section in switchStatement.switchSection())
        {
            foreach (var label in section.switchLabel())
            {
                foreach (var pattern in label.pattern())
                {
                    ValidatePatternFacts(pattern, functionName, filePath);
                }
            }
        }
    }

    private void ValidatePatternFacts(StarkParser.PatternContext pattern, string functionName, string? filePath)
    {
        if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
        {
            ValidateEnumPatternContext(enumNamedFieldPattern, functionName, filePath);
            foreach (var member in enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember())
            {
                ValidatePatternFacts(member.pattern(), functionName, filePath);
            }

            return;
        }

        if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
        {
            ValidateEnumPatternContext(genericEnumAggregatePattern, functionName, filePath);
            ValidateAggregateSuffixPatternFacts(genericEnumAggregatePattern.aggregatePatternSuffix(), functionName, filePath);
            return;
        }

        if (pattern.aggregatePattern() is { } aggregatePattern)
        {
            ValidateAggregateOrEnumPatternContext(aggregatePattern, functionName, filePath);
            ValidateAggregateSuffixPatternFacts(aggregatePattern.aggregatePatternSuffix(), functionName, filePath);
        }
    }

    private void ValidateAggregateSuffixPatternFacts(
        StarkParser.AggregatePatternSuffixContext? suffix,
        string functionName,
        string? filePath)
    {
        if (suffix is null || suffix.Identifier() is not null)
        {
            return;
        }

        foreach (var nestedPattern in suffix.pattern())
        {
            ValidatePatternFacts(nestedPattern, functionName, filePath);
        }
    }

    private void ValidateEnumPatternContext(ParserRuleContext context, string functionName, string? filePath)
    {
        if (TryGetRecord(_enumPatterns, context, functionName, out var enumPattern))
        {
            ValidateEnumPatternFact(enumPattern, context, filePath);
            return;
        }

        ReportMissing(
            context,
            filePath,
            "Lowering contract is missing typed enum-pattern facts. Type checking must record the enum type, variant, and payload field mapping before switch MIR lowering.");
    }

    private void ValidateAggregateOrEnumPatternContext(
        StarkParser.AggregatePatternContext context,
        string functionName,
        string? filePath)
    {
        if (TryGetRecord(_enumPatterns, context, functionName, out var enumPattern))
        {
            ValidateEnumPatternFact(enumPattern, context, filePath);
            return;
        }

        if (TryGetRecord(_aggregatePatterns, context, functionName, out var aggregatePattern))
        {
            ValidateAggregatePatternFact(aggregatePattern, context, filePath);
            return;
        }

        ReportMissing(
            context,
            filePath,
            "Lowering contract is missing typed enum or aggregate-pattern facts. Type checking must record whether this switch pattern is a named aggregate or enum case before MIR lowering.");
    }

    private void ValidateEnumPatternFact(
        EnumPatternTypingRecord record,
        ParserRuleContext context,
        string? filePath)
    {
        if (!TryGetEnumVariant(record.EnumType, record.VariantName, out _, out var variant))
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed enum-pattern fact references unknown enum variant '{record.EnumType.DisplayName}.{record.VariantName}'.");
            return;
        }

        if (context is StarkParser.EnumNamedFieldPatternContext namedFieldPattern)
        {
            if (!variant.UsesNamedFields)
            {
                ReportInvalid(
                    context,
                    filePath,
                    $"Typed enum-pattern fact for '{record.EnumType.DisplayName}.{record.VariantName}' was attached to a named-field pattern, but the variant is not named-field shaped.");
            }

            var sourceMemberCount = namedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember().Length;
            if (record.Members.Count != sourceMemberCount)
            {
                ReportInvalid(
                    context,
                    filePath,
                    $"Typed enum-pattern fact for '{record.EnumType.DisplayName}.{record.VariantName}' has a member-count mismatch: recorded {record.Members.Count}, but the source pattern has {sourceMemberCount}.");
            }

            foreach (var member in record.Members)
            {
                if (member.FieldIndex < 0
                    || member.FieldIndex >= variant.Fields.Count
                    || !string.Equals(variant.Fields[member.FieldIndex].Name, member.FieldName, StringComparison.Ordinal)
                    || !Equals(variant.Fields[member.FieldIndex].Type, member.FieldType))
                {
                    ReportInvalid(
                        context,
                        filePath,
                        $"Typed enum-pattern fact for '{record.EnumType.DisplayName}.{record.VariantName}.{member.FieldName}' does not match the enum variant payload layout.");
                }
            }

            return;
        }

        if (variant.UsesNamedFields)
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed enum-pattern fact for '{record.EnumType.DisplayName}.{record.VariantName}' was attached to a positional pattern, but the variant requires named-field payload matching.");
        }

        if (record.Members.Count != 0)
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed positional enum-pattern fact for '{record.EnumType.DisplayName}.{record.VariantName}' unexpectedly carries named-field member records.");
        }

        var suffix = context switch
        {
            StarkParser.AggregatePatternContext aggregatePattern => aggregatePattern.aggregatePatternSuffix(),
            StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern => genericEnumAggregatePattern.aggregatePatternSuffix(),
            _ => null
        };
        if (variant.IsUnit)
        {
            if (suffix is not null)
            {
                ReportInvalid(
                    context,
                    filePath,
                    $"Typed enum-pattern fact for unit variant '{record.EnumType.DisplayName}.{record.VariantName}' is attached to a source pattern with a payload suffix.");
            }

            return;
        }

        if (suffix is null)
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed enum-pattern fact for '{record.EnumType.DisplayName}.{record.VariantName}' is missing the source payload suffix required by the variant.");
            return;
        }

        if (suffix.Identifier() is null && suffix.pattern().Length != variant.Fields.Count)
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed enum-pattern fact for '{record.EnumType.DisplayName}.{record.VariantName}' has a positional payload-count mismatch: the variant has {variant.Fields.Count} field(s), but the source pattern has {suffix.pattern().Length}.");
        }
    }

    private void ValidateAggregatePatternFact(
        AggregatePatternTypingRecord record,
        StarkParser.AggregatePatternContext context,
        string? filePath)
    {
        if (record.Type.Kind != StarkTypeKind.Named
            || record.Type.NamedType is null
            || !_typeModel.NamedTypes.TryGetValue(record.Type.NamedType, out var namedType))
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed aggregate-pattern fact must carry a known named type, but found '{record.Type.DisplayName}'.");
            return;
        }

        if (namedType.Kind == DeclarationKind.Enum)
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed aggregate-pattern fact for '{record.Type.DisplayName}' references an enum. Enum case patterns must use enum-pattern facts.");
            return;
        }

        var suffix = context.aggregatePatternSuffix();
        if (suffix is null || suffix.Identifier() is not null)
        {
            return;
        }

        if (suffix.pattern().Length != namedType.OrderedFields.Count)
        {
            ReportInvalid(
                context,
                filePath,
                $"Typed aggregate-pattern fact for '{record.Type.DisplayName}' has a field-count mismatch: the named type has {namedType.OrderedFields.Count} field(s), but the source pattern has {suffix.pattern().Length}.");
        }
    }

    private void ValidateSwitchCount(
        string countName,
        int recorded,
        int actual,
        ParserRuleContext context,
        string? filePath)
    {
        if (recorded == actual)
        {
            return;
        }

        ReportInvalid(
            context,
            filePath,
            $"Typed switch fact has a {countName}-count mismatch: recorded {recorded}, but the source switch has {actual}.");
    }

    private void ValidateCallArgumentFacts(
        string factKind,
        string calleeName,
        IReadOnlyList<TypedParameterSymbol> parameters,
        int receiverOffset,
        IReadOnlyList<CallArgumentTypingRecord> records,
        StarkParser.ArgumentListContext arguments,
        string? filePath)
    {
        var actualArgumentCount = arguments.argument().Length;
        var expectedRecordCount = Math.Min(Math.Max(0, parameters.Count - receiverOffset), actualArgumentCount)
            + Math.Min(receiverOffset, parameters.Count);
        if (records.Count != expectedRecordCount)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed {factKind} fact for '{calleeName}' has an argument fact-count mismatch: recorded {records.Count}, but lowering expects {expectedRecordCount} formal argument fact(s).");
        }

        foreach (var record in records)
        {
            if (record.ParameterIndex < 0 || record.ParameterIndex >= parameters.Count)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' references parameter index {record.ParameterIndex}, but the signature has {parameters.Count} parameter(s).");
                continue;
            }

            var parameter = parameters[record.ParameterIndex];
            var expectedSourceArgumentIndex = record.IsReceiver ? -1 : record.ParameterIndex - receiverOffset;
            if (record.SourceArgumentIndex != expectedSourceArgumentIndex)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' maps parameter {record.ParameterIndex} to source argument {record.SourceArgumentIndex}, but lowering expects {expectedSourceArgumentIndex}.");
            }

            if (record.IsReceiver != (record.ParameterIndex < receiverOffset))
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' has an invalid receiver marker for parameter {record.ParameterIndex}.");
            }

            if (!record.IsReceiver
                && (record.SourceArgumentIndex < 0 || record.SourceArgumentIndex >= actualArgumentCount))
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' references source argument {record.SourceArgumentIndex}, but the source call has {actualArgumentCount} explicit argument(s).");
            }

            if (!Equals(record.ParameterType, parameter.Type))
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' records parameter type '{record.ParameterType.DisplayName}', but signature parameter {record.ParameterIndex} is '{parameter.Type.DisplayName}'.");
            }

            if (record.ArgumentType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' must carry a runtime argument type, but found '{record.ArgumentType.DisplayName}'.");
            }

            var requiresAddressable = RequiresAddressableCallArgument(parameter, record.IsReceiver);
            if (record.RequiresAddressable != requiresAddressable)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' records RequiresAddressable={record.RequiresAddressable}, but parameter {record.ParameterIndex} requires {requiresAddressable}.");
            }

            var requiresMutable = RequiresMutableCallArgument(parameter, record.IsReceiver);
            if (record.RequiresMutable != requiresMutable)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' records RequiresMutable={record.RequiresMutable}, but parameter {record.ParameterIndex} requires {requiresMutable}.");
            }

            if (record.RequiresConstProvenance != parameter.IsConst)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' records RequiresConstProvenance={record.RequiresConstProvenance}, but parameter {record.ParameterIndex} requires {parameter.IsConst}.");
            }

            if (requiresAddressable && !record.ArgumentIsAddressable)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' requires an addressable argument for parameter {record.ParameterIndex}, but the recorded source argument is not addressable.");
            }

            if (requiresMutable && !record.ArgumentIsMutable)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' requires a mutable argument for parameter {record.ParameterIndex}, but the recorded source argument is not mutable.");
            }

            if (parameter.IsConst && !record.ArgumentHasConstProvenance)
            {
                ReportInvalid(
                    arguments,
                    filePath,
                    $"Typed {factKind} argument fact for '{calleeName}' requires const provenance for parameter {record.ParameterIndex}, but the recorded source argument does not have const provenance.");
            }
        }
    }

    private void ValidateFunctionCallArity(
        string factKind,
        string calleeName,
        int expectedArgumentCount,
        bool isVarargs,
        int actualArgumentCount,
        ParserRuleContext context,
        string? filePath)
    {
        if (isVarargs
            ? actualArgumentCount >= expectedArgumentCount
            : actualArgumentCount == expectedArgumentCount)
        {
            return;
        }

        var expectation = isVarargs
            ? $"at least {expectedArgumentCount}"
            : expectedArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ReportInvalid(
            context,
            filePath,
            $"Typed {factKind} fact for '{calleeName}' has an arity mismatch: expected {expectation} explicit argument(s), but the source call has {actualArgumentCount}.");
    }

    private void ValidateIndirectCall(
        IndirectCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string? filePath)
    {
        if (record.FunctionPointerType.Kind != StarkTypeKind.FunctionPointer
            || record.FunctionPointerType.FunctionPointerParameterTypes is not { } parameterTypes
            || record.FunctionPointerType.FunctionPointerReturnType is null)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed indirect-call fact must carry a concrete function-pointer ABI type, but found '{record.FunctionPointerType.DisplayName}'.");
            return;
        }

        ValidateFunctionCallArity(
            "indirect-call",
            record.FunctionPointerType.DisplayName,
            parameterTypes.Count,
            isVarargs: false,
            arguments.argument().Length,
            arguments,
            filePath);
        var syntheticParameters = parameterTypes
            .Select(static (parameterType, index) => new TypedParameterSymbol($"arg{index}", parameterType))
            .ToArray();
        ValidateCallArgumentFacts(
            "indirect-call",
            record.FunctionPointerType.DisplayName,
            syntheticParameters,
            receiverOffset: 0,
            record.Arguments,
            arguments,
            filePath);
    }

    private void ValidateClosureCall(
        ClosureCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string? filePath)
    {
        if (record.ClosureType.Kind != StarkTypeKind.Closure
            || record.ClosureType.ClosureParameterTypes is not { } parameterTypes
            || record.ClosureType.ClosureReturnType is null)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed closure-call fact must carry a concrete closure ABI type, but found '{record.ClosureType.DisplayName}'.");
            return;
        }

        ValidateFunctionCallArity(
            "closure-call",
            record.ClosureType.DisplayName,
            parameterTypes.Count,
            isVarargs: false,
            arguments.argument().Length,
            arguments,
            filePath);
        var syntheticParameters = parameterTypes
            .Select((parameterType, index) => new TypedParameterSymbol(
                $"arg{index}",
                parameterType,
                RawPointerElementCountExpression: StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(
                    record.ClosureType,
                    index)))
            .ToArray();
        ValidateCallArgumentFacts(
            "closure-call",
            record.ClosureType.DisplayName,
            syntheticParameters,
            receiverOffset: 0,
            record.Arguments,
            arguments,
            filePath);
    }

    private void ValidateEnumCall(
        EnumCallTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string? filePath)
    {
        if (!TryGetEnumVariant(record.EnumType, record.VariantName, out _, out var variant))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed enum-constructor call fact references unknown enum variant '{record.EnumType.DisplayName}.{record.VariantName}'.");
            return;
        }

        ValidateFunctionCallArity(
            "enum-constructor-call",
            $"{record.EnumType.DisplayName}.{record.VariantName}",
            variant.Fields.Count,
            isVarargs: false,
            arguments.argument().Length,
            arguments,
            filePath);
    }

    private void ValidateDynamicStorageOperation(
        StarkParser.PostfixExpressionContext postfix,
        int callPartIndex,
        DynamicStorageOperationTypingRecord record,
        StarkParser.ArgumentListContext arguments,
        string? filePath)
    {
        var sourceMemberName = callPartIndex > 0
            ? postfix.postfixPart()[callPartIndex - 1].Identifier()?.GetText()
            : null;
        if (!string.Equals(record.OperationName, sourceMemberName, StringComparison.Ordinal))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact names '{record.OperationName}', but the source call is '{sourceMemberName ?? "<unknown>"}'.");
        }

        if (record.ReceiverType.Kind != StarkTypeKind.Dynamic)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact for '{record.OperationName}' requires a dynamic receiver, but found '{record.ReceiverType.DisplayName}'.");
        }

        if (!record.ReceiverIsAddressable)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact for '{record.OperationName}' requires an addressable dynamic receiver, but the recorded receiver is not addressable.");
        }

        if (!record.ReceiverIsMutable)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact for '{record.OperationName}' requires a mutable dynamic receiver, but the recorded receiver is not mutable.");
        }

        if (record.ArgumentCount != arguments.argument().Length)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact for '{record.OperationName}' has an arity mismatch: recorded {record.ArgumentCount} argument(s), but the source call has {arguments.argument().Length}.");
        }

        var expectedArgumentCount = record.OperationName switch
        {
            "MoveLast" => 0,
            "MoveAt" or "Reserve" or "TryReserve" or "TryReserveCapacity" => 1,
            _ => -1
        };
        if (expectedArgumentCount < 0)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact uses unknown operation '{record.OperationName}'.");
        }
        else if (record.ArgumentCount != expectedArgumentCount)
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact for '{record.OperationName}' records {record.ArgumentCount} argument(s), but the operation requires {expectedArgumentCount}.");
        }

        var expectedResultType = record.OperationName switch
        {
            "Reserve" => StarkTypeSymbols.Void,
            "TryReserve" or "TryReserveCapacity" => StarkTypeSymbols.Bool,
            "MoveLast" or "MoveAt" => record.ReceiverType.ElementType ?? StarkTypeSymbols.Error,
            _ => StarkTypeSymbols.Error
        };
        if (expectedResultType.Kind != StarkTypeKind.Error
            && !Equals(record.ResultType, expectedResultType))
        {
            ReportInvalid(
                arguments,
                filePath,
                $"Typed dynamic-storage operation fact for '{record.OperationName}' records result '{record.ResultType.DisplayName}', but lowering expects '{expectedResultType.DisplayName}'.");
        }
    }

    private void ValidateIndexAccess(
        IndexAccessTypingRecord record,
        StarkParser.ExpressionListContext indexes,
        string? filePath)
    {
        var actualIndexCount = indexes.expression().Length;
        if (record.IndexCount != actualIndexCount)
        {
            ReportInvalid(
                indexes,
                filePath,
                $"Typed index fact for '{record.Kind}' has an arity mismatch: recorded {record.IndexCount} index operand(s), but the source index has {actualIndexCount}.");
        }

        if (record.SourceType.Kind is StarkTypeKind.Error or StarkTypeKind.Void
            || record.ResultType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            ReportInvalid(
                indexes,
                filePath,
                $"Typed index fact for '{record.Kind}' must carry runtime source/result types, but found '{record.SourceType.DisplayName}' -> '{record.ResultType.DisplayName}'.");
        }

        var validShape = record.Kind switch
        {
            "element" => actualIndexCount >= 1
                && record.SourceType.Kind is StarkTypeKind.FixedArray or StarkTypeKind.Slice or StarkTypeKind.RawPointer,
            "text-element" => actualIndexCount == 1
                && record.SourceType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode,
            "text-slice" => actualIndexCount is 0 or 2
                && record.SourceType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode,
            "dynamic-element" => actualIndexCount == 1
                && record.SourceType.Kind == StarkTypeKind.Dynamic,
            "dynamic-slice" => actualIndexCount == 2
                && record.SourceType.Kind == StarkTypeKind.Dynamic,
            "raw-pointer-region" => actualIndexCount == 2
                && record.SourceType.Kind == StarkTypeKind.RawPointer,
            _ => false
        };

        if (!validShape)
        {
            ReportInvalid(
                indexes,
                filePath,
                $"Typed index fact '{record.Kind}' is not compatible with source type '{record.SourceType.DisplayName}' and {actualIndexCount} index operand(s).");
        }
    }

    private void ValidateObjectCreationFact(
        ObjectCreationTypingRecord record,
        StarkParser.ObjectCreationExpressionContext expression,
        string? filePath)
    {
        if (record.CreatedType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed object-creation fact must carry a runtime created type, but found '{record.CreatedType.DisplayName}'.");
        }

        var actualArgumentCount = expression.argumentList()?.argument().Length ?? 0;
        if (record.Constructor is { } constructor
            && constructor.Parameters.Count != actualArgumentCount)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed object-creation constructor fact for '{record.CreatedType.DisplayName}' has an arity mismatch: recorded {constructor.Parameters.Count} constructor parameter(s), but the source creation has {actualArgumentCount} argument(s).");
        }

        var initializerMemberCount = expression.objectInitializer()?.memberInitializer().Length ?? 0;
        if (record.Members.Count != initializerMemberCount)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed object-creation initializer fact for '{record.CreatedType.DisplayName}' has a member-count mismatch: recorded {record.Members.Count}, but the source initializer has {initializerMemberCount}.");
        }

        if (initializerMemberCount == 0
            || record.CreatedType.Kind != StarkTypeKind.Named
            || record.CreatedType.NamedType is null
            || !_typeModel.NamedTypes.TryGetValue(record.CreatedType.NamedType, out var namedType))
        {
            return;
        }

        foreach (var member in record.Members)
        {
            if (member.FieldIndex < 0
                || member.FieldIndex >= namedType.OrderedFields.Count
                || !string.Equals(namedType.OrderedFields[member.FieldIndex].Name, member.FieldName, StringComparison.Ordinal)
                || !Equals(namedType.OrderedFields[member.FieldIndex].Type, member.FieldType))
            {
                ReportInvalid(
                    expression,
                    filePath,
                    $"Typed object-creation initializer fact for '{record.CreatedType.DisplayName}.{member.FieldName}' does not match the named type field layout.");
            }
        }
    }

    private void ValidateEnumConstructorFact(
        EnumConstructorTypingRecord record,
        StarkParser.EnumConstructorExpressionContext expression,
        string? filePath)
    {
        if (!TryGetEnumVariant(record.EnumType, record.VariantName, out _, out var variant))
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed named-field enum-constructor fact references unknown enum variant '{record.EnumType.DisplayName}.{record.VariantName}'.");
            return;
        }

        var sourceMemberCount = expression.enumConstructorInitializer().enumConstructorMember().Length;
        if (record.Members.Count != sourceMemberCount)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed named-field enum-constructor fact for '{record.EnumType.DisplayName}.{record.VariantName}' has a member-count mismatch: recorded {record.Members.Count}, but the source constructor has {sourceMemberCount}.");
        }

        foreach (var member in record.Members)
        {
            if (member.FieldIndex < 0
                || member.FieldIndex >= variant.Fields.Count
                || !string.Equals(variant.Fields[member.FieldIndex].Name, member.FieldName, StringComparison.Ordinal)
                || !Equals(variant.Fields[member.FieldIndex].Type, member.FieldType))
            {
                ReportInvalid(
                    expression,
                    filePath,
                    $"Typed named-field enum-constructor fact for '{record.EnumType.DisplayName}.{record.VariantName}.{member.FieldName}' does not match the enum variant payload layout.");
            }
        }
    }

    private void ValidateTypeLayoutFact(
        TypeLayoutExpressionTypingRecord record,
        StarkParser.PrimaryExpressionContext expression,
        string functionName,
        string? filePath)
    {
        var sourceKind = expression.SIZEOF() is not null ? "sizeof" : "alignof";
        if (!string.Equals(record.Kind, sourceKind, StringComparison.Ordinal))
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed layout-query fact records '{record.Kind}', but the source expression is '{sourceKind}'.");
        }

        if (record.TargetType.Kind is StarkTypeKind.Error or StarkTypeKind.Void)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed layout-query fact for '{sourceKind}' must carry a concrete runtime type, but found '{record.TargetType.DisplayName}'.");
        }

        if (IsOpenGenericLayoutTarget(record.TargetType, functionName))
        {
            return;
        }

        if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(record.TargetType, _typeModel.NamedTypes, _enumLayouts) is null)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed layout-query fact for '{sourceKind}' references '{record.TargetType.DisplayName}', but no concrete layout is available before MIR lowering.");
        }
    }

    private void ValidateLambdaFact(
        LambdaTypingRecord record,
        StarkParser.LambdaExpressionContext expression,
        string? filePath)
    {
        var parameterCount = expression.lambdaParameterList().parameter().Length;
        if (record.FunctionPointerType.Kind != StarkTypeKind.FunctionPointer
            || record.FunctionPointerType.FunctionPointerParameterTypes is not { } parameterTypes
            || record.FunctionPointerType.FunctionPointerReturnType is null)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed lambda fact for '{record.FunctionName}' must carry a concrete function-pointer ABI type, but found '{record.FunctionPointerType.DisplayName}'.");
            return;
        }

        if (record.ParameterNames.Count != parameterCount
            || parameterTypes.Count != parameterCount)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed lambda fact for '{record.FunctionName}' has a parameter-count mismatch: recorded {record.ParameterNames.Count} name(s) and {parameterTypes.Count} ABI parameter(s), but the source lambda has {parameterCount}.");
        }

        if (!_typeModel.Functions.ContainsKey(record.FunctionName))
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed lambda fact for '{record.FunctionName}' does not have a corresponding generated function signature.");
        }
    }

    private void ValidateClosureLambdaFact(
        ClosureLambdaTypingRecord record,
        StarkParser.LambdaExpressionContext expression,
        string? filePath)
    {
        var parameterCount = expression.lambdaParameterList().parameter().Length;
        if (record.ClosureType.Kind != StarkTypeKind.Closure
            || record.ClosureType.ClosureParameterTypes is not { } parameterTypes
            || record.ClosureType.ClosureReturnType is null)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed closure-lambda fact for '{record.FunctionName}' must carry a concrete closure type, but found '{record.ClosureType.DisplayName}'.");
            return;
        }

        if (record.ParameterNames.Count != parameterCount
            || parameterTypes.Count != parameterCount)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed closure-lambda fact for '{record.FunctionName}' has a parameter-count mismatch: recorded {record.ParameterNames.Count} name(s) and {parameterTypes.Count} ABI parameter(s), but the source lambda has {parameterCount}.");
        }

        if (!_typeModel.Functions.TryGetValue(record.FunctionName, out var signature))
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed closure-lambda fact for '{record.FunctionName}' has no synthetic function signature in the type model.");
            return;
        }

        if (signature.Parameters.Count != parameterCount + 1)
        {
            ReportInvalid(
                expression,
                filePath,
                $"Typed closure-lambda fact for '{record.FunctionName}' must lower to one hidden environment parameter plus {parameterCount} source parameter(s), but the synthetic signature has {signature.Parameters.Count} parameter(s).");
        }

        if (signature.Parameters.Count > 0)
        {
            var environmentParameter = signature.Parameters[0];
            if (!string.Equals(environmentParameter.Name, CallableValueFacts.ClosureEnvironmentParameterName, StringComparison.Ordinal)
                || environmentParameter.Type != record.EnvironmentParameterType)
            {
                ReportInvalid(
                    expression,
                    filePath,
                    $"Typed closure-lambda fact for '{record.FunctionName}' has an invalid hidden environment parameter: expected '{CallableValueFacts.ClosureEnvironmentParameterName}: {record.EnvironmentParameterType.DisplayName}'.");
            }
        }
    }

    private bool TryGetEnumVariant(
        StarkTypeSymbol enumType,
        string variantName,
        out NamedTypeSymbol namedType,
        out EnumVariantSymbol variant)
    {
        namedType = null!;
        variant = null!;
        return enumType.Kind == StarkTypeKind.Named
            && enumType.NamedType is not null
            && _typeModel.NamedTypes.TryGetValue(enumType.NamedType, out namedType!)
            && namedType.Kind == DeclarationKind.Enum
            && namedType.TryGetVariant(variantName, out variant!, out _);
    }

    private bool IsOpenGenericLayoutTarget(StarkTypeSymbol type, string functionName)
    {
        return _typeModel.Functions.TryGetValue(functionName, out var signature)
            && signature.GenericParams.Count != 0
            && ContainsGenericParameter(type, signature.GenericParams);
    }

    private static bool ContainsGenericParameter(StarkTypeSymbol type, IReadOnlyList<string> genericParameterNames)
    {
        if (genericParameterNames.Count == 0)
        {
            return false;
        }

        if (type.Kind == StarkTypeKind.Named
            && type.NamedType is { } namedType
            && genericParameterNames.Contains(namedType, StringComparer.Ordinal))
        {
            return true;
        }

        if (type.ElementType is not null
            && ContainsGenericParameter(type.ElementType, genericParameterNames))
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 }
            && type.TypeArguments.Any(argument => ContainsGenericParameter(argument, genericParameterNames)))
        {
            return true;
        }

        if (type.FunctionPointerReturnType is not null
            && ContainsGenericParameter(type.FunctionPointerReturnType, genericParameterNames))
        {
            return true;
        }

        if (type.FunctionPointerParameterTypes is { Count: > 0 }
            && type.FunctionPointerParameterTypes.Any(parameter => ContainsGenericParameter(parameter, genericParameterNames)))
        {
            return true;
        }

        if (type.ClosureReturnType is not null
            && ContainsGenericParameter(type.ClosureReturnType, genericParameterNames))
        {
            return true;
        }

        return type.ClosureParameterTypes is { Count: > 0 }
            && type.ClosureParameterTypes.Any(parameter => ContainsGenericParameter(parameter, genericParameterNames));
    }

    private static string ClassifySwitchFamily(StarkTypeSymbol switchType, SwitchSourceShape shape)
    {
        if (CanUseFastLiteralSwitch(shape))
        {
            if (switchType.Kind is StarkTypeKind.Integer or StarkTypeKind.Bool)
            {
                return SwitchLoweringFamilies.Native;
            }

            if (switchType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                return SwitchLoweringFamilies.PartitionedText;
            }
        }

        return SwitchLoweringFamilies.Guarded;
    }

    private static bool CanUseFastLiteralSwitch(SwitchSourceShape shape)
    {
        return shape.LoweredDefaultLabelCount <= 1
            && shape.GuardedLabelCount == 0
            && shape.CaptureLabelCount == 0
            && shape.StructuredPatternLabelCount == 0
            && shape.LiteralLabelCount > 0
            && shape.LabelCount - shape.LoweredDefaultLabelCount == shape.LiteralLabelCount;
    }

    private static bool CanLowerImplementedSwitchType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.Bool
            or StarkTypeKind.RawPointer
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode
            or StarkTypeKind.Named;
    }

    private static bool RequiresAddressableCallArgument(TypedParameterSymbol parameter, bool isReceiver)
    {
        if (isReceiver)
        {
            return false;
        }

        if (parameter.Type.Kind == StarkTypeKind.Closure)
        {
            return false;
        }

        return !parameter.IsConst
            && (parameter.Type.InitializationKind != StarkInitializationKind.None
                || parameter.Type.BorrowKind != StarkBorrowKind.None);
    }

    private static bool RequiresMutableCallArgument(TypedParameterSymbol parameter, bool isReceiver)
    {
        if (isReceiver)
        {
            return false;
        }

        if (parameter.Type.Kind == StarkTypeKind.Closure)
        {
            return false;
        }

        return !parameter.IsConst
            && (parameter.Type.InitializationKind != StarkInitializationKind.None
                || parameter.Type.BorrowKind != StarkBorrowKind.None && parameter.Type.IsMutableView);
    }

    private static SwitchSourceShape InspectSwitchShape(StarkParser.SwitchStatementContext switchStatement)
    {
        var sectionCount = switchStatement.switchSection().Length;
        var labelCount = 0;
        var explicitDefaultLabelCount = 0;
        var loweredDefaultLabelCount = 0;
        var literalLabelCount = 0;
        var matchAllLabelCount = 0;
        var captureLabelCount = 0;
        var structuredPatternLabelCount = 0;
        var guardedLabelCount = 0;

        foreach (var section in switchStatement.switchSection())
        {
            foreach (var label in section.switchLabel())
            {
                if (label.DEFAULT() is not null)
                {
                    labelCount++;
                    explicitDefaultLabelCount++;
                    loweredDefaultLabelCount++;
                    continue;
                }

                foreach (var pattern in label.pattern())
                {
                    labelCount++;
                    if (label.whenClause() is not null)
                    {
                        guardedLabelCount++;
                    }

                    if (pattern.literal() is not null)
                    {
                        literalLabelCount++;
                        continue;
                    }

                    if (pattern.DISCARD() is not null)
                    {
                        matchAllLabelCount++;
                        if (label.whenClause() is null)
                        {
                            loweredDefaultLabelCount++;
                        }

                        continue;
                    }

                    if (pattern.VAR() is not null)
                    {
                        matchAllLabelCount++;
                        captureLabelCount++;
                        continue;
                    }

                    if (pattern.aggregatePattern() is not null
                        || pattern.enumNamedFieldPattern() is not null
                        || pattern.genericEnumAggregatePattern() is not null)
                    {
                        structuredPatternLabelCount++;
                    }
                }
            }
        }

        return new SwitchSourceShape(
            sectionCount,
            labelCount,
            explicitDefaultLabelCount,
            loweredDefaultLabelCount,
            literalLabelCount,
            matchAllLabelCount,
            captureLabelCount,
            structuredPatternLabelCount,
            guardedLabelCount);
    }

    private static bool ShouldTrackObjectCreation(StarkParser.ObjectCreationExpressionContext expression)
    {
        return expression.type_() is null
            || expression.objectInitializer() is not null
            || expression.argumentList() is { } argumentList && argumentList.argument().Length > 0;
    }

    private static bool IsUnsafeRawSliceConstructionPrefix(StarkParser.PostfixExpressionContext expression, int postfixPartIndex)
    {
        return postfixPartIndex == 0
            && string.Equals(expression.primaryExpression().Identifier()?.GetText(), "slice", StringComparison.Ordinal);
    }

    private bool TryGetRecord<T>(
        IReadOnlyDictionary<OperationKey, T> facts,
        ParserRuleContext context,
        string functionName,
        out T record)
    {
        var key = Key(functionName, context);
        if (facts.TryGetValue(key, out record!))
        {
            return true;
        }

        return facts.TryGetValue(key with { FunctionName = null }, out record!);
    }

    private bool TryGetBoundOperation<T>(
        ParserRuleContext context,
        string functionName,
        out T operation)
        where T : BoundOperation
    {
        var key = Key(functionName, context);
        if (TryGetBoundOperation(key, out operation))
        {
            return true;
        }

        return TryGetBoundOperation(key with { FunctionName = null }, out operation);
    }

    private bool TryGetBoundOperation<T>(OperationKey key, out T operation)
        where T : BoundOperation
    {
        if (_boundOperations.TryGetValue(key, out var operations))
        {
            foreach (var candidate in operations)
            {
                if (candidate is T typedOperation)
                {
                    operation = typedOperation;
                    return true;
                }
            }
        }

        operation = null!;
        return false;
    }

    private static bool IsTextBufferType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Named
            && type.NamedType is StarkTypeSymbols.OwnedAsciiName or StarkTypeSymbols.OwnedUnicodeName;
    }

    private static StarkParser.AdditiveExpressionContext? TryGetStandaloneAdditiveExpression(StarkParser.ExpressionContext expression)
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
        return shift.additiveExpression().Length == 1
            ? shift.additiveExpression(0)
            : null;
    }

    private static StarkParser.LiteralContext? TryGetStandaloneInterpolatedTextLiteral(StarkParser.ExpressionContext expression)
    {
        var additive = TryGetStandaloneAdditiveExpression(expression);
        if (additive is null || additive.multiplicativeExpression().Length != 1)
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

    private void ReportMissing(ParserRuleContext context, string? filePath, string message)
    {
        ReportInvalid(context, filePath, message);
    }

    private void ReportInvalid(ParserRuleContext context, string? filePath, string message)
    {
        _context.Diagnostics.Error(
            "STK5003",
            message,
            Stage,
            Location(context, filePath));
    }

    private static SourceLocation Location(ParserRuleContext context, string? filePath)
    {
        if (context.Stop is { } stop)
        {
            return new SourceLocation(
                filePath,
                context.Start.Line,
                context.Start.Column + 1,
                stop.Line,
                stop.Column + Math.Max(1, stop.Text?.Length ?? 1));
        }

        return new SourceLocation(filePath, context.Start.Line, context.Start.Column + 1);
    }

    private static string QualifyName(LoadedModuleDocument module, string localName)
    {
        return module.Reference.IsRoot
            ? localName
            : $"{module.SyntaxModel.ModuleName}.{localName}";
    }

    private static OperationKey Key(string? functionName, SourceLocation location)
    {
        return new OperationKey(functionName, location.Line, location.Column);
    }

    private static OperationKey Key(string functionName, ParserRuleContext context)
    {
        return new OperationKey(functionName, context.Start.Line, context.Start.Column + 1);
    }

    private static Dictionary<OperationKey, TRecord> BuildOperationMap<TRecord>(
        IEnumerable<TRecord> records,
        Func<TRecord, OperationKey> keySelector)
    {
        return records
            .GroupBy(keySelector)
            .ToDictionary(static group => group.Key, static group => group.Last());
    }

    private readonly record struct SwitchSourceShape(
        int SectionCount,
        int LabelCount,
        int ExplicitDefaultLabelCount,
        int LoweredDefaultLabelCount,
        int LiteralLabelCount,
        int MatchAllLabelCount,
        int CaptureLabelCount,
        int StructuredPatternLabelCount,
        int GuardedLabelCount);

    private readonly record struct OperationKey(string? FunctionName, int Line, int Column);
}
