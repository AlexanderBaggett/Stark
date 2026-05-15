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
                    ValidateDynamicStorageOperation(postfix, index, dynamicStorageOperation, arguments, filePath);
                    _checkedDynamicStorageOperationCount++;
                    continue;
                }

                if (TryGetRecord(_directCalls, arguments, functionName, out var directCall))
                {
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
                    ValidateIndirectCall(indirectCall, arguments, filePath);
                    _checkedCallCount++;
                    continue;
                }

                if (TryGetRecord(_closureCalls, arguments, functionName, out var closureCall))
                {
                    ValidateClosureCall(closureCall, arguments, filePath);
                    _checkedCallCount++;
                    continue;
                }

                if (TryGetRecord(_enumCalls, arguments, functionName, out var enumCall))
                {
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
                if (label.pattern() is { } pattern)
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
                labelCount++;
                if (label.DEFAULT() is not null)
                {
                    explicitDefaultLabelCount++;
                    loweredDefaultLabelCount++;
                    continue;
                }

                if (label.whenClause() is not null)
                {
                    guardedLabelCount++;
                }

                var pattern = label.pattern();
                if (pattern is null)
                {
                    continue;
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
