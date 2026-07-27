using System.Numerics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder
    {
        public bool TryLowerImportedTypedTemplateBody(ImportedTemplateTypedBodySummary typedBody)
        {
            return _importedTemplateLowerer.TryLowerBody(typedBody);
        }

        private bool TryLowerImportedTypedTemplateStatementList(
            IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> statements,
            bool createScope)
        {
            return _importedTemplateLowerer.TryLowerStatementList(statements, createScope);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateExpression(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            return _importedTemplateLowerer.LowerExpression(expression, expectedType);
        }

        private static string RenderImportedTypedTemplateExpression(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            return ImportedTemplateLowerer.RenderExpression(expression);
        }

        private sealed record ImportedTemplateEvaluationContext(
            ImportedFunctionTemplateSummary Summary,
            IReadOnlyDictionary<int, ImportedTemplateEnumConstructorSummary> EnumConstructors,
            IReadOnlyDictionary<int, ImportedTemplateEnumCallSummary> EnumCalls,
            IReadOnlyDictionary<int, ImportedTemplateEnumValueSummary> EnumValues,
            IReadOnlyDictionary<int, ImportedTemplateEnumPatternSummary> EnumPatterns,
            IReadOnlyDictionary<int, ImportedTemplateAggregatePatternSummary> AggregatePatterns,
            IReadOnlyDictionary<int, TypedFunctionSignature> DirectCalls,
            IReadOnlyDictionary<int, TypedFunctionSignature> MemberCalls,
            IReadOnlyDictionary<int, ImportedTemplateFieldAccessSummary> FieldAccesses);

        private readonly Dictionary<string, ImportedTemplateEvaluationContext> _importedTemplateEvaluationContextCache =
            new(StringComparer.Ordinal);

        private ImportedTemplateEvaluationContext? _activeImportedTemplateEvaluationContext;

        private ImportedFunctionTemplateSummary? CurrentImportedTemplateEvaluationSummary =>
            _activeImportedTemplateEvaluationContext?.Summary ?? _importedTemplateSummary;

        private IReadOnlyDictionary<int, ImportedTemplateEnumConstructorSummary> CurrentImportedTemplateEnumConstructors =>
            _activeImportedTemplateEvaluationContext?.EnumConstructors ?? _importedTemplateEnumConstructors;

        private IReadOnlyDictionary<int, ImportedTemplateEnumCallSummary> CurrentImportedTemplateEnumCalls =>
            _activeImportedTemplateEvaluationContext?.EnumCalls ?? _importedTemplateEnumCalls;

        private IReadOnlyDictionary<int, ImportedTemplateEnumValueSummary> CurrentImportedTemplateEnumValues =>
            _activeImportedTemplateEvaluationContext?.EnumValues ?? _importedTemplateEnumValues;

        private IReadOnlyDictionary<int, ImportedTemplateEnumPatternSummary> CurrentImportedTemplateEnumPatterns =>
            _activeImportedTemplateEvaluationContext?.EnumPatterns ?? _importedTemplateEnumPatterns;

        private IReadOnlyDictionary<int, ImportedTemplateAggregatePatternSummary> CurrentImportedTemplateAggregatePatterns =>
            _activeImportedTemplateEvaluationContext?.AggregatePatterns ?? _importedTemplateAggregatePatterns;

        private IReadOnlyDictionary<int, TypedFunctionSignature> CurrentImportedTemplateDirectCalls =>
            _activeImportedTemplateEvaluationContext?.DirectCalls ?? _importedTemplateDirectCalls;

        private IReadOnlyDictionary<int, TypedFunctionSignature> CurrentImportedTemplateMemberCalls =>
            _activeImportedTemplateEvaluationContext?.MemberCalls ?? _importedTemplateMemberCalls;

        private IReadOnlyDictionary<int, ImportedTemplateFieldAccessSummary> CurrentImportedTemplateFieldAccesses =>
            _activeImportedTemplateEvaluationContext?.FieldAccesses ?? _importedTemplateFieldAccesses;

        private ImportedTemplateEvaluationContext GetImportedTemplateEvaluationContext(
            string templateName,
            ImportedFunctionTemplateSummary summary)
        {
            if (_importedTemplateEvaluationContextCache.TryGetValue(templateName, out var cached))
            {
                return cached;
            }

            var context = new ImportedTemplateEvaluationContext(
                summary,
                summary.EnumConstructors.ToDictionary(
                    static enumConstructor => enumConstructor.Ordinal,
                    static enumConstructor => enumConstructor),
                summary.EnumCalls.ToDictionary(
                    static enumCall => enumCall.Ordinal,
                    static enumCall => enumCall),
                summary.EnumValues.ToDictionary(
                    static enumValue => enumValue.Ordinal,
                    static enumValue => enumValue),
                summary.EnumPatterns.ToDictionary(
                    static enumPattern => enumPattern.Ordinal,
                    static enumPattern => enumPattern),
                summary.AggregatePatterns.ToDictionary(
                    static aggregatePattern => aggregatePattern.Ordinal,
                    static aggregatePattern => aggregatePattern),
                summary.DirectCalls.ToDictionary(
                    static directCall => directCall.Ordinal,
                    static directCall => directCall.Signature),
                summary.MemberCalls.ToDictionary(
                    static memberCall => memberCall.Ordinal,
                    static memberCall => memberCall.Signature),
                summary.FieldAccesses.ToDictionary(
                    static fieldAccess => fieldAccess.Ordinal,
                    static fieldAccess => fieldAccess));
            _importedTemplateEvaluationContextCache[templateName] = context;
            return context;
        }

        private bool TryLowerImportedTypedTemplateLocalVariable(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Name is null
                || statement.StorageClass is null
                || statement.Type is not { } statementType)
            {
                return false;
            }

            var declaredType = ApplyGenericSubstitution(statementType);
            if (statement.StorageCapacity is { } storageCapacity && IsTextBufferType(declaredType))
            {
                return TryLowerImportedTypedTemplateFixedTextStorageLocal(statement, declaredType, storageCapacity);
            }

            var name = statement.Name;
            RegisterLocal(
                name,
                declaredType,
                statement.StorageClass,
                statement.IsMutable,
                statement.IsConstant,
                constProvenance: statement.ConstProvenance);
            TrackDeclaredLocal(name, declaredType);
            Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
            InitializeRuntimeDropState(name, declaredType, isActive: false);

            if (statement.Expression is null)
            {
                return true;
            }

            var initializer = LowerImportedTypedTemplateExpressionCore(statement.Expression, declaredType);
            if (initializer is null)
            {
                return false;
            }

            EmitOperandAssignment(new MidLevelIrLocalOperand(name, declaredType), initializer, initializer.Text);
            if (!statement.IsMutable
                && (ConstProvenanceFacts.HasPermanentConstProvenance(statement.ConstProvenance)
                    || OperandHasConstProvenance(initializer)))
            {
                MarkLocalHasConstProvenance(name);
            }

            RecordMoveFromOperand(initializer, declaredType);
            SetRuntimeDropState(name, isActive: true);
            return true;
        }

        private bool TryLowerImportedTypedTemplateFixedTextStorageLocal(
            ImportedTemplateTypedBodyStatementSummary statement,
            StarkTypeSymbol declaredType,
            int capacity)
        {
            var name = statement.Name!;
            var unitType = GetFixedTextStorageUnitType(declaredType);
            var storageType = StarkTypeSymbols.FixedArray(unitType, capacity);
            var storageName = AllocateTemporaryName($"{name}_text_storage");

            RegisterLocal(storageName, storageType, storageClass: "stack", isMutable: true, isConstant: false);
            TrackDeclaredLocal(storageName, storageType);
            Emit(MidLevelIrStatementKind.StorageLive, storageName, storageName, storageType);
            InitializeRuntimeDropState(storageName, storageType, isActive: false);

            RegisterLocal(
                name,
                declaredType,
                statement.StorageClass!,
                statement.IsMutable,
                statement.IsConstant,
                constProvenance: statement.ConstProvenance);
            TrackDeclaredLocal(name, declaredType);
            Emit(MidLevelIrStatementKind.StorageLive, name, name, declaredType);
            InitializeRuntimeDropState(name, declaredType, isActive: false);

            var emptyText = BuildFixedTextStorageValue(storageName, storageType, declaredType, capacity);
            if (emptyText is null)
            {
                throw LoweringInvariantViolation(null, "Imported fixed text storage value could not be initialized.");
            }

            Emit(MidLevelIrStatementKind.Assign, $"{name}[{capacity}]", name, declaredType, new MidLevelIrUseRValue(emptyText));
            SetRuntimeDropState(name, isActive: true);

            if (statement.Expression is null)
            {
                return true;
            }

            return statement.Expression.Kind switch
            {
                ImportedTemplateTypedBodyExpressionKind.TextInterpolation
                    => LowerImportedTypedTemplateFixedTextInterpolation(name, declaredType, statement.Expression),
                ImportedTemplateTypedBodyExpressionKind.TextBuild
                    => LowerImportedTypedTemplateFixedTextBuild(name, declaredType, statement.Expression),
                _ => false
            };
        }

        private bool LowerImportedTypedTemplateFixedTextInterpolation(
            string destinationName,
            StarkTypeSymbol destinationType,
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.LiteralText is null
                || !InterpolatedText.TryParse(expression.LiteralText, out var segments, out _))
            {
                return false;
            }

            var viewType = GetFixedTextStorageViewType(destinationType);
            var current = BuildTextBufferView(new MidLevelIrLocalOperand(destinationName, destinationType), viewType);
            if (current is null)
            {
                return false;
            }

            var holeIndex = 0;
            foreach (var segment in segments)
            {
                var next = LowerImportedTypedTemplateTextSegmentToView(segment, expression.Args, ref holeIndex, destinationType, viewType);
                if (next is null
                    || !AppendFixedTextStorageSegment(destinationName, destinationType, current, next, context: null!))
                {
                    return false;
                }

                current = next;
            }

            return holeIndex == expression.Args.Count;
        }

        private bool LowerImportedTypedTemplateFixedTextBuild(
            string destinationName,
            StarkTypeSymbol destinationType,
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count == 0)
            {
                return false;
            }

            var viewType = GetFixedTextStorageViewType(destinationType);
            var currentValue = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            if (currentValue is null)
            {
                return false;
            }

            var current = BuildTextBufferView(currentValue, viewType);
            if (current is null)
            {
                return false;
            }

            for (var index = 1; index < expression.Args.Count; index++)
            {
                var nextValue = LowerImportedTypedTemplateExpressionCore(expression.Args[index], expectedType: null);
                if (nextValue is null)
                {
                    return false;
                }

                var next = BuildTextBufferView(nextValue, viewType);
                if (next is null
                    || !AppendFixedTextStorageSegment(destinationName, destinationType, current, next, context: null!))
                {
                    return false;
                }

                current = next;
            }

            return true;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateTextSegmentToView(
            InterpolatedTextSegment segment,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> holes,
            ref int holeIndex,
            StarkTypeSymbol destinationType,
            StarkTypeSymbol viewType)
        {
            if (segment is InterpolatedTextRawSegment raw)
            {
                return raw.Value.Length == 0
                    ? BuildTextBufferView(new MidLevelIrStringConstantOperand("\"\"", viewType), viewType)
                    : new MidLevelIrStringConstantOperand(TextLiteralDecoder.EncodeStringLiteral(raw.Value), viewType);
            }

            if (holeIndex >= holes.Count)
            {
                return null;
            }

            var value = LowerImportedTypedTemplateExpressionCore(holes[holeIndex++], expectedType: null);
            if (value is null)
            {
                return null;
            }

            if (CanUseFixedTextConcatSource(destinationType, value.Type))
            {
                return BuildTextBufferView(value, viewType);
            }

            if (!TextFormattingFacts.TryGetFixedBufferFormatInfo(destinationType, value.Type, out var formatInfo))
            {
                return null;
            }

            return LowerFormattedImportedTypedTemplateTextHole(value, destinationType, viewType, formatInfo);
        }

        private MidLevelIrOperand? LowerFormattedImportedTypedTemplateTextHole(
            MidLevelIrOperand value,
            StarkTypeSymbol destinationType,
            StarkTypeSymbol viewType,
            FixedTextFormatInfo formatInfo)
        {
            var storageName = AllocateTemporaryName("interpolation_format_storage");
            var textName = AllocateTemporaryName("interpolation_format_text");
            var unitType = GetFixedTextStorageUnitType(destinationType);
            var storageType = StarkTypeSymbols.FixedArray(unitType, formatInfo.Capacity);

            RegisterLocal(storageName, storageType, storageClass: "stack", isMutable: true, isConstant: false);
            TrackDeclaredLocal(storageName, storageType);
            Emit(MidLevelIrStatementKind.StorageLive, storageName, storageName, storageType);
            InitializeRuntimeDropState(storageName, storageType, isActive: false);

            RegisterLocal(textName, destinationType, storageClass: "stack", isMutable: true, isConstant: false);
            TrackDeclaredLocal(textName, destinationType);
            Emit(MidLevelIrStatementKind.StorageLive, textName, textName, destinationType);
            InitializeRuntimeDropState(textName, destinationType, isActive: false);

            var emptyText = BuildFixedTextStorageValue(storageName, storageType, destinationType, formatInfo.Capacity);
            if (emptyText is null)
            {
                return null;
            }

            Emit(MidLevelIrStatementKind.Assign, $"{textName}[{formatInfo.Capacity}]", textName, destinationType, new MidLevelIrUseRValue(emptyText));
            SetRuntimeDropState(textName, isActive: true);

            var destinationAddress = CreateMutableAddressOfLocalForInitialization(textName, destinationType);
            if (destinationAddress is null
                || !TryBuildFixedTextFormatCall(destinationAddress, value, formatInfo.FunctionName, value.Text, out var call))
            {
                return null;
            }

            var success = EmitTemporary(call, "textformat");
            if (success is null)
            {
                return null;
            }

            EmitTrapOnFalse(success, "textformat_overflow");
            return BuildTextBufferView(new MidLevelIrLocalOperand(textName, destinationType), viewType);
        }

        private bool TryLowerImportedTypedTemplateExpressionStatement(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is not { } expression)
            {
                return false;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.DirectCall)
            {
                if (!TryBuildImportedTypedTemplateDirectCallStatement(expression, out var directCall))
                {
                    throw LoweringInvariantViolation(
                        null,
                        "Imported typed-template direct-call statement was accepted but did not bind to serialized call facts.");
                }

                EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), directCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.MemberCall)
            {
                if (TryBuildImportedTypedTemplateDynTraitMemberCallStatement(expression, out var dynTraitCall))
                {
                    EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), dynTraitCall);
                    return true;
                }

                if (!TryBuildImportedTypedTemplateMemberCallStatement(expression, out var memberCall))
                {
                    throw LoweringInvariantViolation(
                        null,
                        DescribeImportedTemplateMemberCallBindingFailure(expression, "member-call statement"));
                }

                EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), memberCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.ClosureCall)
            {
                if (!TryBuildImportedTypedTemplateClosureCallStatement(expression, out var closureCall))
                {
                    throw LoweringInvariantViolation(
                        null,
                        "Imported typed-template indirect-call statement was accepted but did not bind to function-pointer or closure facts.");
                }

                EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), closureCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.DynamicStorageOperation)
            {
                if (!TryBuildImportedTypedTemplateDynamicStorageOperation(expression, out var dynamicStorageOperation))
                {
                    throw LoweringInvariantViolation(
                        null,
                        "Imported typed-template dynamic-storage statement was accepted but did not bind to serialized operation facts.");
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpressionCore(expression), value: dynamicStorageOperation);
                return true;
            }

            if (TryLowerImportedTypedTemplateConditionalCallStatement(expression))
            {
                return true;
            }

            var operand = LowerImportedTypedTemplateExpressionCore(expression, expectedType: null);
            if (operand is null)
            {
                return false;
            }

            Emit(
                MidLevelIrStatementKind.Evaluate,
                RenderImportedTypedTemplateExpressionCore(expression),
                value: new MidLevelIrUseRValue(operand));
            return true;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateArrayInitializer(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            var targetType = expectedType ?? expression.Type;
            if (targetType is null
                || targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int fixedLength)
            {
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(targetType);
            var elementCount = Math.Min(fixedLength, expression.Args.Count);

            for (var index = 0; index < elementCount; index++)
            {
                var element = LowerImportedTypedTemplateExpressionCore(expression.Args[index], targetType.ElementType);
                if (element is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        current,
                        index,
                        IndexedElementOperationFamily.FixedArrayElement,
                        element,
                        targetType,
                        $"{current.Text}[{RenderImportedTypedTemplateExpressionCore(expression.Args[index])}]"),
                    "insertindex");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateObjectInitializerExpression(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            var targetType = expectedType ?? (expression.Type is { } publishedType ? ApplyGenericSubstitution(publishedType) : null);
            if (targetType is null
                || expression.Members.Count != expression.Args.Count
                || !TryBuildImportedTypedTemplateObjectInitializerMembers(targetType, expression, out var initializerMembers))
            {
                return null;
            }

            return LowerImportedTypedTemplateObjectInitializer(
                targetType,
                new MidLevelIrZeroInitializerOperand(targetType),
                initializerMembers,
                expression.Args);
        }

        private bool TryBuildImportedTypedTemplateObjectInitializerMembers(
            StarkTypeSymbol targetType,
            ImportedTemplateTypedBodyExpressionSummary expression,
            out IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary> initializerMembers)
        {
            initializerMembers = [];

            if (!TryResolveImportedTypedTemplateNamedType(targetType, out var namedType, out var substitution))
            {
                return false;
            }

            var builtMembers = new List<ImportedTemplateObjectInitializerMemberSummary>(expression.Members.Count);
            foreach (var memberName in expression.Members)
            {
                if (!namedType.TryGetField(memberName, out var field, out var fieldIndex))
                {
                    return false;
                }

                var fieldType = substitution.Count == 0
                    ? field.Type
                    : FunctionOverloadFacts.SubstituteType(field.Type, substitution);
                builtMembers.Add(new ImportedTemplateObjectInitializerMemberSummary(
                    memberName,
                    fieldIndex,
                    fieldType));
            }

            initializerMembers = builtMembers;
            return true;
        }

        private bool TryResolveImportedTypedTemplateNamedType(
            StarkTypeSymbol targetType,
            out NamedTypeSymbol namedType,
            out IReadOnlyDictionary<string, StarkTypeSymbol> substitution)
        {
            namedType = null!;
            substitution = EmptyTypeSubstitution;

            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null)
            {
                return false;
            }

            if (!_namedTypes.TryGetValue(targetType.NamedType, out namedType!))
            {
                var baseName = StarkTypeSymbols.GetGenericBaseName(targetType.NamedType);
                if (!_namedTypes.TryGetValue(baseName, out namedType!))
                {
                    return false;
                }
            }

            if (targetType.TypeArguments is not { Count: > 0 } || namedType.GenericParams.Count == 0)
            {
                substitution = EmptyTypeSubstitution;
                return true;
            }

            if (namedType.GenericParams.Count != targetType.TypeArguments.Count)
            {
                return false;
            }

            var builtSubstitution = new Dictionary<string, StarkTypeSymbol>(namedType.GenericParams.Count, StringComparer.Ordinal);
            for (var index = 0; index < namedType.GenericParams.Count; index++)
            {
                builtSubstitution[namedType.GenericParams[index]] = targetType.TypeArguments[index];
            }

            substitution = builtSubstitution;
            return true;
        }

        private bool TryLowerImportedTypedTemplateConditionalCallStatement(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Kind != ImportedTemplateTypedBodyExpressionKind.Conditional
                || expression.Args.Count != 3
                || !CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[1])
                || !CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[2]))
            {
                return false;
            }

            var condition = LowerImportedTypedTemplateExpressionCore(expression.Args[0], StarkTypeSymbols.Bool);
            if (condition is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported typed-template conditional call statement condition did not lower to a bool operand.");
            }

            var thenBlock = CreateBlock("typed_cond_true");
            var elseBlock = CreateBlock("typed_cond_false");
            var joinBlock = CreateBlock("typed_cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpressionCore(expression.Args[0]),
                Condition: condition);

            CurrentBlock = thenBlock;
            if (!TryLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[1]))
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported typed-template conditional call true branch was accepted but did not lower.");
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = elseBlock;
            if (!TryLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[2]))
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported typed-template conditional call false branch was accepted but did not lower.");
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplateConditionalCallStatementBranch(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.DirectCall)
            {
                if (!TryBuildImportedTypedTemplateDirectCallStatement(expression, out var directCall))
                {
                    throw LoweringInvariantViolation(
                        null,
                        "Imported typed-template conditional direct-call branch was accepted but did not bind to serialized call facts.");
                }

                EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), directCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.MemberCall)
            {
                if (TryBuildImportedTypedTemplateDynTraitMemberCallStatement(expression, out var dynTraitCall))
                {
                    EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), dynTraitCall);
                    return true;
                }

                if (!TryBuildImportedTypedTemplateMemberCallStatement(expression, out var memberCall))
                {
                    throw LoweringInvariantViolation(
                        null,
                        DescribeImportedTemplateMemberCallBindingFailure(expression, "conditional member-call branch"));
                }

                EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), memberCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.ClosureCall)
            {
                if (!TryBuildImportedTypedTemplateClosureCallStatement(expression, out var closureCall))
                {
                    throw LoweringInvariantViolation(
                        null,
                        "Imported typed-template conditional indirect-call branch was accepted but did not bind to function-pointer or closure facts.");
                }

                EmitEvaluateCallStatement(RenderImportedTypedTemplateExpressionCore(expression), closureCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.DynamicStorageOperation)
            {
                if (!TryBuildImportedTypedTemplateDynamicStorageOperation(expression, out var dynamicStorageOperation))
                {
                    throw LoweringInvariantViolation(
                        null,
                        "Imported typed-template conditional dynamic-storage branch was accepted but did not bind to serialized operation facts.");
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpressionCore(expression), value: dynamicStorageOperation);
                return true;
            }

            return TryLowerImportedTypedTemplateConditionalCallStatement(expression);
        }

        private static bool CanLowerImportedTypedTemplateConditionalCallStatementBranch(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Kind is ImportedTemplateTypedBodyExpressionKind.DirectCall
                or ImportedTemplateTypedBodyExpressionKind.MemberCall
                or ImportedTemplateTypedBodyExpressionKind.ClosureCall
                or ImportedTemplateTypedBodyExpressionKind.DynamicStorageOperation)
            {
                return true;
            }

            return expression.Kind == ImportedTemplateTypedBodyExpressionKind.Conditional
                && expression.Args.Count == 3
                && CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[1])
                && CanLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[2]);
        }

        private bool TryLowerImportedTypedTemplateAssignment(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null
                || !TryBuildImportedTypedTemplateAssignment(
                    statement.Name,
                    statement.TargetExpression,
                    statement.AssignmentOperator,
                    statement.Expression,
                    out var assignment))
            {
                return false;
            }

            EmitAssignment(assignment);
            return true;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateAssignmentExpression(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count != 1
                || !TryBuildImportedTypedTemplateAssignment(
                    expression.Name,
                    expression.TargetExpression,
                    expression.AssignmentOperator,
                    expression.Args[0],
                    out var assignment))
            {
                return null;
            }

            EmitAssignment(assignment);
            return assignment.ResultValue;
        }

        private bool TryBuildImportedTypedTemplateAssignment(
            string? targetName,
            ImportedTemplateTypedBodyExpressionSummary? targetExpression,
            string? assignmentOperatorText,
            ImportedTemplateTypedBodyExpressionSummary valueExpression,
            out LoweredAssignment assignment)
        {
            assignment = default!;

            var assignmentOperator = string.IsNullOrEmpty(assignmentOperatorText)
                ? "="
                : assignmentOperatorText;
            var isInitializationAssignment = string.Equals(assignmentOperator, "init =", StringComparison.Ordinal);
            PlaceTarget target;
            string assignmentTargetText;

            if (targetExpression is not null)
            {
                if (!TryResolveImportedTypedTemplateAssignmentTarget(targetExpression, out target))
                {
                    return false;
                }

                assignmentTargetText = RenderImportedTypedTemplateExpressionCore(targetExpression);
            }
            else
            {
                if (targetName is not { } name
                    || !_localsByName.TryGetValue(name, out var local)
                    || local.IsConstant
                    || !local.IsMutable)
                {
                    return false;
                }

                target = new PlaceTarget(
                    name,
                    RootAddress: null,
                    RootValue: null,
                    local.Type,
                    local.Type,
                    Path: [],
                    UsesAddressModel: false,
                    IsAddressMutable: CanFormMutableAddressFromLocal(local));
                assignmentTargetText = name;
            }

            if (target.RootName is { } rootName
                && _localsByName.TryGetValue(rootName, out var localBinding)
                && localBinding.IsConstant)
            {
                return false;
            }

            if (!CanAssignImportedTypedTemplatePlace(target))
            {
                return false;
            }

            var valueText = RenderImportedTypedTemplateExpressionCore(valueExpression);
            var assignmentText = isInitializationAssignment
                ? $"init {assignmentTargetText} = {valueText}"
                : $"{assignmentTargetText} {assignmentOperator} {valueText}";
            MidLevelIrOperand assignedValue;
            if (assignmentOperator == "=" || isInitializationAssignment)
            {
                var loweredAssignedValue = LowerImportedTypedTemplateExpressionCore(valueExpression, target.Type);
                if (loweredAssignedValue is null)
                {
                    return false;
                }

                assignedValue = loweredAssignedValue;
            }
            else
            {
                var currentValue = ReadPlace(target);
                var right = LowerImportedTypedTemplateExpressionCore(valueExpression, currentValue.Type);
                if (right is null)
                {
                    return false;
                }

                var commonType = FindCommonType(currentValue.Type, right.Type);
                var leftValue = CoerceOperand(currentValue, commonType);
                var rightValue = CoerceOperand(right, commonType);
                if (leftValue is null || rightValue is null)
                {
                    return false;
                }

                var temp = EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MapAssignmentOperator(assignmentOperator),
                        leftValue,
                        rightValue,
                        commonType,
                        assignmentText),
                    "compound");
                if (temp is null)
                {
                    return false;
                }

                assignedValue = CoerceOperand(temp, target.Type) ?? temp;
            }

            assignment = BuildAssignment(target, assignedValue, assignmentText);
            if (isInitializationAssignment)
            {
                assignment = assignment with { WriteKind = MemoryWriteKind.Initialization };
                if (TryBuildDynamicStorageLengthUpdate(target, out var dynamicLengthUpdate))
                {
                    assignment = assignment with { DynamicLengthUpdate = dynamicLengthUpdate };
                }
            }

            return true;
        }

        private bool CanAssignImportedTypedTemplatePlace(PlaceTarget target)
        {
            if (target.RootAddress is not null)
            {
                return target.IsAddressMutable;
            }

            if (target.RootName is not { } rootName)
            {
                return target.IsAddressMutable;
            }

            if (target.Path.Count == 0)
            {
                if (_localsByName.TryGetValue(rootName, out var local))
                {
                    return local.IsMutable && !local.IsConstant;
                }

                if (_parametersByName.ContainsKey(rootName))
                {
                    return false;
                }

                return _typeModel.Globals.TryGetValue(rootName, out var global)
                    ? global.IsMutable
                    : target.IsAddressMutable;
            }

            return target.IsAddressMutable;
        }

        private bool TryResolveImportedTypedTemplateAssignmentTarget(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out PlaceTarget target)
        {
            return _placeLowerer.TryResolveImportedTypedTemplateAssignmentTarget(expression, out target);
        }

        private bool TryResolveImportedTypedTemplateAssignmentTargetCore(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out PlaceTarget target,
            out MidLevelIrOperand? rootOperand)
        {
            target = default!;
            rootOperand = null;

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.NameReference)
            {
                if (expression.Name is null)
                {
                    return false;
                }

                var operand = ResolveNamedOperand(expression.Name);
                if (operand is null)
                {
                    return false;
                }

                target = new PlaceTarget(
                    operand.Text,
                    RootAddress: null,
                    RootValue: null,
                    operand.Type,
                    operand.Type,
                    Path: [],
                    UsesAddressModel: false,
                    IsAddressMutable: GetAddressMutability(operand));
                rootOperand = operand;
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.UnaryOperation
                && string.Equals(expression.Name, "*", StringComparison.Ordinal)
                && expression.Args.Count == 1)
            {
                var address = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
                if (address is null
                    || address.Type.Kind != StarkTypeKind.RawPointer
                    || !address.Type.IsMutablePointer
                    || address.Type.ElementType is not { } elementType
                    || !CanMutateThroughType(elementType))
                {
                    return false;
                }

                target = new PlaceTarget(
                    RootName: null,
                    RootAddress: address,
                    RootValue: null,
                    RootType: elementType,
                    Type: elementType,
                    Path: [],
                    UsesAddressModel: true,
                    IsAddressMutable: true);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.FieldAccess)
            {
                if (expression.Ordinal is not { } ordinal
                    || expression.Args.Count != 1
                    || !_importedTemplateFieldAccesses.TryGetValue(ordinal, out var publishedFieldAccess)
                    || !TryResolveImportedTypedTemplateAssignmentTargetCore(expression.Args[0], out target, out rootOperand))
                {
                    return false;
                }

                var fieldType = ProjectAddressProjectionType(target.Type, ApplyGenericSubstitution(publishedFieldAccess.FieldType));
                var updatedPath = target.Path.ToList();
                updatedPath.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    publishedFieldAccess.FieldName,
                    publishedFieldAccess.FieldIndex,
                    IndexOperand: null,
                    ParentType: target.Type,
                    SegmentType: fieldType));
                target = target with
                {
                    Type = fieldType,
                    Path = updatedPath
                };
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.IndexAccess)
            {
                if (expression.Args.Count < 2
                    || !TryResolveImportedTypedTemplateAssignmentTargetCore(expression.Args[0], out target, out rootOperand))
                {
                    return false;
                }

                var updatedPath = target.Path.ToList();
                var currentType = target.Type;
                var usesAddressModel = target.UsesAddressModel;
                var supportsAddressModel = target.RootAddress is not null
                    || (rootOperand is not null && SupportsAddressModel(rootOperand));

                for (var argumentIndex = 1; argumentIndex < expression.Args.Count; argumentIndex++)
                {
                    var index = LowerImportedTypedTemplateExpressionCore(expression.Args[argumentIndex], expectedType: null);
                    if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                    {
                        return false;
                    }

                    if (currentType.Kind == StarkTypeKind.FixedArray && currentType.ElementType is not null)
                    {
                        if (TryResolveImportedTypedTemplateConstantIndex(index, out var constantIndex))
                        {
                            var constantElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            updatedPath.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: constantElementType));
                            currentType = constantElementType;
                            continue;
                        }

                        if (!supportsAddressModel)
                        {
                            return false;
                        }

                        var dynamicElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                        updatedPath.Add(new PlacePathSegment(
                            PlacePathKind.DynamicArrayIndex,
                            FieldName: null,
                            ConstantIndex: null,
                            IndexOperand: index,
                            ParentType: currentType,
                            SegmentType: dynamicElementType));
                        currentType = dynamicElementType;
                        usesAddressModel = true;
                        supportsAddressModel = true;
                        continue;
                    }

                    if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                    {
                        var sliceElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                        updatedPath.Add(new PlacePathSegment(
                            PlacePathKind.SliceIndex,
                            FieldName: null,
                            ConstantIndex: null,
                            IndexOperand: index,
                            ParentType: currentType,
                            SegmentType: sliceElementType));
                        currentType = sliceElementType;
                        usesAddressModel = true;
                        supportsAddressModel = true;
                        continue;
                    }

                    if (currentType.Kind == StarkTypeKind.Dynamic && currentType.ElementType is not null)
                    {
                        var dynamicStorageElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                        updatedPath.Add(new PlacePathSegment(
                            PlacePathKind.DynamicStorageIndex,
                            FieldName: null,
                            ConstantIndex: null,
                            IndexOperand: index,
                            ParentType: currentType,
                            SegmentType: dynamicStorageElementType));
                        currentType = dynamicStorageElementType;
                        usesAddressModel = true;
                        supportsAddressModel = true;
                        continue;
                    }

                    if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
                    {
                        updatedPath.Add(new PlacePathSegment(
                            PlacePathKind.RawPointerIndex,
                            FieldName: null,
                            ConstantIndex: null,
                            IndexOperand: index,
                            ParentType: currentType,
                            SegmentType: currentType.ElementType));
                        currentType = currentType.ElementType;
                        usesAddressModel = true;
                        supportsAddressModel = true;
                        continue;
                    }

                    return false;
                }

                target = target with
                {
                    Type = currentType,
                    Path = updatedPath,
                    UsesAddressModel = usesAddressModel
                };
                return true;
            }

            return false;
        }

        private bool TryLowerImportedTypedTemplateSwitch(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null
                || statement.SwitchCases is not { Count: > 0 })
            {
                return false;
            }

            var switchValue = LowerImportedTypedTemplateExpressionCore(statement.Expression, expectedType: null);
            if (switchValue is null || !CanLowerSwitchType(switchValue.Type))
            {
                return false;
            }

            var defaultSectionCount = statement.SwitchCases.Count(static switchCase => switchCase.Kind == ImportedTemplateTypedSwitchCaseKind.Default);
            if (defaultSectionCount > 1)
            {
                return false;
            }

            if (IsGuardlessEmptyImportedTypedTemplateSwitch(statement))
            {
                return true;
            }

            var sections = new (ImportedTemplateTypedSwitchCaseSummary Case, IReadOnlyList<LowerableSwitchLabel> Labels, BasicBlockBuilder EntryBlock, BasicBlockBuilder BodyBlock)[statement.SwitchCases.Count];
            for (var index = 0; index < statement.SwitchCases.Count; index++)
            {
                var switchCase = statement.SwitchCases[index];
                if (!TryBuildImportedTypedTemplateSwitchLabel(switchCase, switchValue.Type, out var label))
                {
                    return false;
                }

                sections[index] = (
                    switchCase,
                    [label],
                    CreateBlock($"typed_switch_test_{index}"),
                    CreateBlock($"typed_switch_case_{index}"));
            }

            var exitBlock = CreateBlock("typed_switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault && label.GuardExpression is null && label.ImportedGuardExpression is null && label.CaptureName is null))
                .Select(static section => section.BodyBlock.Id)
                .FirstOrDefault(exitBlock.Id);

            for (var index = 0; index < sections.Length; index++)
            {
                if (!TryRegisterSwitchCaptureLocals(
                        sections[index].Labels,
                        switchValue.Type,
                        out var registeredLabels))
                {
                    return false;
                }

                sections[index].Labels = registeredLabels;
            }

            if (sections.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [defaultTarget]);
            }
            else
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [sections[0].EntryBlock.Id]);

                for (var index = 0; index < sections.Length; index++)
                {
                    CurrentBlock = sections[index].EntryBlock;
                    var nextTarget = index + 1 < sections.Length
                        ? sections[index + 1].EntryBlock.Id
                        : defaultTarget;
                    if (!EmitSwitchSectionDecision(
                            sections[index].Labels,
                            switchValue,
                            sections[index].BodyBlock.Id,
                            nextTarget,
                            RenderImportedTypedTemplateExpressionCore(statement.Expression),
                            index))
                    {
                        return false;
                    }
                }
            }

            _breakTargets.Push(new BreakTargets(statement.Name, exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.BodyBlock;
                    _scopes.Push(new ScopeFrame());
                    TrackSwitchSectionCaptureLocals(section.Labels, switchValue.Type);
                    try
                    {
                        if (!TryLowerImportedTypedTemplateStatementList(section.Case.Statements, createScope: false))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        var scope = _scopes.Pop();
                        EmitStorageDead(scope);
                        RestoreScopedNameAliases(scope);
                    }

                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(exitBlock.Id);
                    }
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private static bool IsGuardlessEmptyImportedTypedTemplateSwitch(ImportedTemplateTypedBodyStatementSummary statement)
        {
            foreach (var switchCase in statement.SwitchCases)
            {
                if (switchCase.GuardExpression is not null || switchCase.Statements.Count != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryBuildImportedTypedTemplateSwitchLabel(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            StarkTypeSymbol switchType,
            out LowerableSwitchLabel label)
        {
            label = null!;

            switch (switchCase.Kind)
            {
                case ImportedTemplateTypedSwitchCaseKind.Literal:
                    if (switchCase.Expression is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        RenderImportedTypedTemplateExpressionCore(switchCase.Expression),
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null,
                        ImportedLiteralExpression: switchCase.Expression,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.Range:
                    if (!TryBuildImportedIntegerRangePattern(
                            switchCase.Expression,
                            switchCase.EndExpression,
                            out var rangePattern))
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        $"{RenderImportedTypedTemplateExpressionCore(switchCase.Expression!)}..{RenderImportedTypedTemplateExpressionCore(switchCase.EndExpression!)}",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null,
                        ImportedGuardExpression: switchCase.GuardExpression,
                        RangePattern: rangePattern);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.MatchAll:
                    label = new LowerableSwitchLabel(
                        switchCase.Name is null ? "_" : $"var {switchCase.Name}",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: true,
                        CaptureName: switchCase.Name,
                        AggregatePattern: null,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.Default:
                    if (switchCase.Name is not null
                        || switchCase.Expression is not null
                        || switchCase.EndExpression is not null
                        || switchCase.GuardExpression is not null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "default",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: true,
                        IsMatchAll: true,
                        CaptureName: null,
                        AggregatePattern: null);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.EnumPattern:
                    if (!TryBuildImportedTypedTemplateSwitchPattern(switchCase, out var aggregatePattern)
                        || aggregatePattern is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "typed-switch-pattern",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: aggregatePattern,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.AggregatePattern:
                    if (!TryBuildImportedTypedTemplateSwitchPattern(switchCase, out aggregatePattern)
                        || aggregatePattern is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "typed-switch-pattern",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: aggregatePattern,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.ListPattern:
                    if (!TryBuildImportedTypedTemplateListSwitchPattern(switchType, switchCase.Members, out var listPattern)
                        || listPattern is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "typed-list-pattern",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null,
                        ListPattern: listPattern,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: switchCase.GuardExpression);
                    return true;

                default:
                    return false;
            }
        }

        private bool TryBuildImportedTypedTemplateSwitchLabel(
            ImportedTemplateTypedSwitchFieldPatternSummary pattern,
            StarkTypeSymbol switchType,
            out LowerableSwitchLabel label)
        {
            label = null!;

            switch (pattern.Kind)
            {
                case ImportedTemplateTypedSwitchFieldPatternKind.Discard:
                    label = new LowerableSwitchLabel(
                        "_",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: true,
                        CaptureName: null,
                        AggregatePattern: null,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: null);
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.Capture:
                    if (pattern.Name is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        $"var {pattern.Name}",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: true,
                        CaptureName: pattern.Name,
                        AggregatePattern: null,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: null);
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.Literal:
                    if (pattern.Expression is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        RenderImportedTypedTemplateExpressionCore(pattern.Expression),
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null,
                        ImportedLiteralExpression: pattern.Expression,
                        ImportedGuardExpression: null);
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.Range:
                    if (!TryBuildImportedIntegerRangePattern(
                            pattern.Expression,
                            pattern.EndExpression,
                            out var rangePattern))
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        $"{RenderImportedTypedTemplateExpressionCore(pattern.Expression!)}..{RenderImportedTypedTemplateExpressionCore(pattern.EndExpression!)}",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: null,
                        RangePattern: rangePattern);
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.EnumPattern:
                    if (pattern.Ordinal is not { } enumOrdinal
                        || !TryBuildImportedTypedTemplateEnumSwitchPattern(enumOrdinal, pattern.Name, pattern.Members, out var enumPattern)
                        || enumPattern is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "typed-switch-pattern",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: enumPattern,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: null);
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.AggregatePattern:
                    if (pattern.Ordinal is not { } aggregateOrdinal
                        || !TryBuildImportedTypedTemplateAggregateSwitchPattern(aggregateOrdinal, pattern.Name, pattern.Members, out var aggregatePattern)
                        || aggregatePattern is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "typed-switch-pattern",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: aggregatePattern,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: null);
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.ListPattern:
                    if (!TryBuildImportedTypedTemplateListSwitchPattern(switchType, pattern.Members, out var listPattern)
                        || listPattern is null)
                    {
                        return false;
                    }

                    label = new LowerableSwitchLabel(
                        "typed-list-pattern",
                        Literal: null,
                        GuardExpression: null,
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null,
                        ListPattern: listPattern,
                        ImportedLiteralExpression: null,
                        ImportedGuardExpression: null);
                    return true;

                default:
                    return false;
            }
        }

        private bool TryBuildImportedTypedTemplateSwitchPattern(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            switch (switchCase.Kind)
            {
                case ImportedTemplateTypedSwitchCaseKind.EnumPattern:
                    return TryBuildImportedTypedTemplateEnumSwitchPattern(switchCase, out aggregatePattern);
                case ImportedTemplateTypedSwitchCaseKind.AggregatePattern:
                    return TryBuildImportedTypedTemplateAggregateSwitchPattern(switchCase, out aggregatePattern);
                default:
                    return false;
            }
        }

        private bool TryBuildImportedTypedTemplateEnumSwitchPattern(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            return switchCase.Ordinal is { } ordinal
                && TryBuildImportedTypedTemplateEnumSwitchPattern(
                    ordinal,
                    switchCase.Name,
                    switchCase.Members,
                    out aggregatePattern);
        }

        private bool TryBuildImportedTypedTemplateEnumSwitchPattern(
            int ordinal,
            string? wholeCaptureName,
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            if (!_importedTemplateEnumPatterns.TryGetValue(ordinal, out var publishedEnumPattern))
            {
                return false;
            }

            var enumType = ApplyGenericSubstitution(publishedEnumPattern.EnumType);
            if (enumType.Kind != StarkTypeKind.Named
                || enumType.NamedType is null
                || !_enumLayoutModel.Layouts.TryGetValue(enumType.NamedType, out var enumLayout)
                || !enumLayout.TryGetVariant(publishedEnumPattern.VariantName, out var enumVariant))
            {
                return false;
            }

            if (wholeCaptureName is not null && memberPatterns.Count != 0)
            {
                return false;
            }

            if (wholeCaptureName is not null)
            {
                aggregatePattern = new LowerableAggregatePattern(
                    enumType.NamedType,
                    publishedEnumPattern.VariantName,
                    [],
                    WholeCaptureName: wholeCaptureName);
                return true;
            }

            if (enumVariant.UsesNamedFields)
            {
                if (publishedEnumPattern.Members.Count != enumVariant.Fields.Count
                    || memberPatterns.Count != publishedEnumPattern.Members.Count)
                {
                    return false;
                }

                var fieldPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
                for (var memberOrdinal = 0; memberOrdinal < memberPatterns.Count; memberOrdinal++)
                {
                    var publishedMember = publishedEnumPattern.Members[memberOrdinal];
                    if (publishedMember.FieldIndex < 0 || publishedMember.FieldIndex >= enumVariant.Fields.Count)
                    {
                        return false;
                    }

                    var field = enumVariant.Fields[publishedMember.FieldIndex];
                    if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                            memberPatterns[memberOrdinal],
                            publishedMember.FieldName,
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            ApplyGenericSubstitution(publishedMember.FieldType),
                            out fieldPatterns[memberOrdinal]))
                    {
                        return false;
                    }
                }

                aggregatePattern = new LowerableAggregatePattern(
                    enumType.NamedType,
                    publishedEnumPattern.VariantName,
                    fieldPatterns,
                    WholeCaptureName: wholeCaptureName);
                return true;
            }

            if (memberPatterns.Count != enumVariant.Fields.Count)
            {
                return false;
            }

            var tupleFieldPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
            for (var fieldIndex = 0; fieldIndex < memberPatterns.Count; fieldIndex++)
            {
                var field = enumVariant.Fields[fieldIndex];
                if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                        memberPatterns[fieldIndex],
                        field.SourceFieldName ?? field.SourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        ApplyGenericSubstitution(field.Type),
                        out tupleFieldPatterns[fieldIndex]))
                {
                    return false;
                }
            }

            aggregatePattern = new LowerableAggregatePattern(
                enumType.NamedType,
                publishedEnumPattern.VariantName,
                tupleFieldPatterns,
                WholeCaptureName: wholeCaptureName);
            return true;
        }

        private bool TryBuildImportedTypedTemplateAggregateSwitchPattern(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            return switchCase.Ordinal is { } ordinal
                && TryBuildImportedTypedTemplateAggregateSwitchPattern(
                    ordinal,
                    switchCase.Name,
                    switchCase.Members,
                    out aggregatePattern);
        }

        private bool TryBuildImportedTypedTemplateAggregateSwitchPattern(
            int ordinal,
            string? wholeCaptureName,
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            out LowerableAggregatePattern? aggregatePattern)
        {
            aggregatePattern = null;

            if (!_importedTemplateAggregatePatterns.TryGetValue(ordinal, out var publishedAggregatePattern))
            {
                return false;
            }

            var aggregateType = ApplyGenericSubstitution(publishedAggregatePattern.Type);
            if (aggregateType.Kind != StarkTypeKind.Named
                || aggregateType.NamedType is null
                || !_namedTypes.TryGetValue(aggregateType.NamedType, out var namedType))
            {
                return false;
            }

            if (wholeCaptureName is not null && memberPatterns.Count != 0)
            {
                return false;
            }

            if (memberPatterns.Count != 0 && memberPatterns.Count != namedType.OrderedFields.Count)
            {
                return false;
            }

            if (publishedAggregatePattern.Members.Count > 0)
            {
                if (publishedAggregatePattern.Members.Count != namedType.OrderedFields.Count
                    || memberPatterns.Count != publishedAggregatePattern.Members.Count)
                {
                    return false;
                }

                var namedFieldPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
                for (var memberOrdinal = 0; memberOrdinal < memberPatterns.Count; memberOrdinal++)
                {
                    var publishedMember = publishedAggregatePattern.Members[memberOrdinal];
                    if (publishedMember.FieldIndex < 0 || publishedMember.FieldIndex >= namedType.OrderedFields.Count)
                    {
                        return false;
                    }

                    var field = namedType.OrderedFields[publishedMember.FieldIndex];
                    if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                            memberPatterns[memberOrdinal],
                            publishedMember.FieldName,
                            field.Name,
                            publishedMember.FieldIndex,
                            ApplyGenericSubstitution(publishedMember.FieldType),
                            out namedFieldPatterns[publishedMember.FieldIndex]))
                    {
                        return false;
                    }
                }

                aggregatePattern = new LowerableAggregatePattern(
                    aggregateType.NamedType,
                    EnumVariantName: null,
                    namedFieldPatterns,
                    WholeCaptureName: wholeCaptureName);
                return true;
            }

            var fieldPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
            for (var fieldIndex = 0; fieldIndex < memberPatterns.Count; fieldIndex++)
            {
                var field = namedType.OrderedFields[fieldIndex];
                if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                        memberPatterns[fieldIndex],
                        field.Name,
                        field.Name,
                        fieldIndex,
                        ApplyGenericSubstitution(field.Type),
                        out fieldPatterns[fieldIndex]))
                {
                    return false;
                }
            }

            aggregatePattern = new LowerableAggregatePattern(
                aggregateType.NamedType,
                EnumVariantName: null,
                fieldPatterns,
                WholeCaptureName: wholeCaptureName);
            return true;
        }

        private bool TryBuildImportedTypedTemplateSwitchFieldPattern(
            ImportedTemplateTypedSwitchFieldPatternSummary fieldPattern,
            string fieldName,
            string storageFieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            out LowerableAggregateFieldPattern parsedFieldPattern)
        {
            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.Discard)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Discard,
                    "_",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.Capture
                && fieldPattern.Name is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Capture,
                    $"var {fieldPattern.Name}",
                    Literal: null,
                    CaptureName: fieldPattern.Name,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.Literal
                && fieldPattern.Expression is { Kind: ImportedTemplateTypedBodyExpressionKind.Literal } literalExpression)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Literal,
                    RenderImportedTypedTemplateExpressionCore(literalExpression),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: literalExpression);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.Range
                && TryBuildImportedIntegerRangePattern(
                    fieldPattern.Expression,
                    fieldPattern.EndExpression,
                    out var rangePattern))
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Range,
                    $"{RenderImportedTypedTemplateExpressionCore(fieldPattern.Expression!)}..{RenderImportedTypedTemplateExpressionCore(fieldPattern.EndExpression!)}",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: null,
                    RangePattern: rangePattern);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.EnumPattern
                && fieldPattern.Ordinal is { } enumOrdinal
                && TryBuildImportedTypedTemplateEnumSwitchPattern(enumOrdinal, fieldPattern.Name, fieldPattern.Members, out var nestedEnumPattern)
                && nestedEnumPattern is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    "typed-nested-enum-pattern",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: nestedEnumPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.AggregatePattern
                && fieldPattern.Ordinal is { } aggregateOrdinal
                && TryBuildImportedTypedTemplateAggregateSwitchPattern(aggregateOrdinal, fieldPattern.Name, fieldPattern.Members, out var nestedAggregatePattern)
                && nestedAggregatePattern is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    "typed-nested-aggregate-pattern",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: nestedAggregatePattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (fieldPattern.Kind == ImportedTemplateTypedSwitchFieldPatternKind.ListPattern
                && TryBuildImportedTypedTemplateListSwitchPattern(fieldType, fieldPattern.Members, out var nestedListPattern)
                && nestedListPattern is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.List,
                    "typed-list-pattern",
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ListPattern: nestedListPattern);
                return true;
            }

            parsedFieldPattern = default!;
            return false;
        }

        private bool TryBuildImportedTypedTemplateListSwitchPattern(
            StarkTypeSymbol listType,
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            out LowerableListPattern? listPattern)
        {
            listPattern = null;
            if (!TryGetListPatternElementType(listType, out var elementType, out var fixedLength))
            {
                return false;
            }

            if (fixedLength is int requiredLength && memberPatterns.Count != requiredLength)
            {
                return false;
            }

            var elementPatterns = new LowerableAggregateFieldPattern[memberPatterns.Count];
            for (var index = 0; index < memberPatterns.Count; index++)
            {
                if (!TryBuildImportedTypedTemplateSwitchFieldPattern(
                        memberPatterns[index],
                        $"#{index}",
                        $"#{index}",
                        index,
                        elementType,
                        out elementPatterns[index]))
                {
                    return false;
                }
            }

            listPattern = new LowerableListPattern(
                listType,
                elementType,
                elementPatterns,
                "typed-list-pattern");
            return true;
        }

        private static bool TryBuildImportedIntegerRangePattern(
            ImportedTemplateTypedBodyExpressionSummary? startExpression,
            ImportedTemplateTypedBodyExpressionSummary? endExpression,
            out LowerableIntegerRangePattern rangePattern)
        {
            rangePattern = default;
            if (!TryParseImportedIntegerLiteralValue(startExpression, out var start)
                || !TryParseImportedIntegerLiteralValue(endExpression, out var end))
            {
                return false;
            }

            rangePattern = new LowerableIntegerRangePattern(start, end);
            return true;
        }

        private static bool TryParseImportedIntegerLiteralValue(
            ImportedTemplateTypedBodyExpressionSummary? expression,
            out BigInteger value)
        {
            value = BigInteger.Zero;
            return expression is { Kind: ImportedTemplateTypedBodyExpressionKind.Literal, LiteralText: { } literalText }
                && BigInteger.TryParse(literalText, out value);
        }

        private bool TryLowerImportedTypedTemplateIf(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.ConditionPattern is not null)
            {
                return TryLowerImportedTypedTemplatePatternIf(statement);
            }

            if (statement.Expression is null)
            {
                return false;
            }

            var condition = LowerImportedTypedTemplateExpressionCore(statement.Expression, StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return false;
            }

            var thenBlock = CreateBlock("typed_if_then");
            var hasElse = statement.ElseBranch.Count > 0;
            var elseBlock = hasElse ? CreateBlock("typed_if_else") : null;
            var joinBlock = CreateBlock("typed_if_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock?.Id ?? joinBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpressionCore(statement.Expression),
                Condition: condition);

            CurrentBlock = thenBlock;
            if (!TryLowerImportedTypedTemplateStatementList(statement.ThenBranch, createScope: true))
            {
                return false;
            }

            if (!CurrentBlock.HasTerminator)
            {
                EnsureGoto(joinBlock.Id);
            }

            if (elseBlock is not null)
            {
                CurrentBlock = elseBlock;
                if (!TryLowerImportedTypedTemplateStatementList(statement.ElseBranch, createScope: true))
                {
                    return false;
                }

                if (!CurrentBlock.HasTerminator)
                {
                    EnsureGoto(joinBlock.Id);
                }
            }

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplatePatternIf(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null || statement.ConditionPattern is null)
            {
                return false;
            }

            var switchValue = LowerImportedTypedTemplateExpressionCore(statement.Expression, expectedType: null);
            if (switchValue is null)
            {
                return false;
            }

            if (!TryBuildImportedTypedTemplateSwitchLabel(statement.ConditionPattern, switchValue.Type, out var builtLabel)
                || !TryRegisterSwitchCaptureLocals([builtLabel], switchValue.Type, out var labels))
            {
                return false;
            }

            var thenBlock = CreateBlock("typed_if_then");
            var hasElse = statement.ElseBranch.Count > 0;
            var elseBlock = hasElse ? CreateBlock("typed_if_else") : null;
            var joinBlock = CreateBlock("typed_if_join");
            var failTarget = elseBlock?.Id ?? joinBlock.Id;

            if (!EmitSwitchSectionDecision(
                    labels,
                    switchValue,
                    thenBlock.Id,
                    failTarget,
                    RenderImportedTypedTemplateExpressionCore(statement.Expression),
                    0))
            {
                return false;
            }

            CurrentBlock = thenBlock;
            _scopes.Push(new ScopeFrame());
            TrackSwitchSectionCaptureLocals(labels, switchValue.Type);
            try
            {
                if (!TryLowerImportedTypedTemplateStatementList(statement.ThenBranch, createScope: false))
                {
                    return false;
                }
            }
            finally
            {
                var thenScope = _scopes.Pop();
                EmitStorageDead(thenScope);
                RestoreScopedNameAliases(thenScope);
            }

            if (!CurrentBlock.HasTerminator)
            {
                EnsureGoto(joinBlock.Id);
            }

            if (elseBlock is not null)
            {
                CurrentBlock = elseBlock;
                if (!TryLowerImportedTypedTemplateStatementList(statement.ElseBranch, createScope: true))
                {
                    return false;
                }

                if (!CurrentBlock.HasTerminator)
                {
                    EnsureGoto(joinBlock.Id);
                }
            }

            CurrentBlock = joinBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplateBreak(string? labelName = null)
        {
            if (!TryResolveBreakTarget(labelName, out var breakTarget))
            {
                return false;
            }

            EmitStorageDeadBeyondDepth(breakTarget.ScopeDepth);
            CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [breakTarget.Target]);
            return true;
        }

        private bool TryLowerImportedTypedTemplateContinue(string? labelName = null)
        {
            if (!TryResolveContinueTarget(labelName, out var loop))
            {
                return false;
            }

            EmitStorageDeadBeyondDepth(loop.ScopeDepth);
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Goto,
                [loop.ContinueTarget],
                LoopBehavior: loop.ContinueLoopBehavior,
                LoopContracts: loop.ContinueLoopContracts,
                LoopAccessGroups: loop.ContinueLoopAccessGroups);
            return true;
        }

        private bool TryLowerImportedTypedTemplateWhile(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.ConditionPattern is not null)
            {
                return TryLowerImportedTypedTemplatePatternWhile(statement);
            }

            if (statement.Expression is null)
            {
                return false;
            }

            var loopBehavior = statement.LoopBehavior;
            var loopContracts = statement.LoopContracts is { Count: > 0 } ? statement.LoopContracts : null;
            var loopAccessGroups = CreateIndependentLoopAccessGroups(loopContracts);
            var conditionBlock = CreateBlock("typed_while_cond");
            var bodyBlock = CreateBlock("typed_while_body");
            var exitBlock = CreateBlock("typed_while_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            var condition = LowerImportedTypedTemplateExpressionCore(statement.Expression, StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return false;
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [bodyBlock.Id, exitBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpressionCore(statement.Expression),
                Condition: condition);

            _loops.Push(new LoopTargets(
                statement.Name,
                conditionBlock.Id,
                exitBlock.Id,
                _scopes.Count,
                loopBehavior,
                loopContracts,
                loopAccessGroups));
            _breakTargets.Push(new BreakTargets(statement.Name, exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                if (loopAccessGroups is { Count: > 0 })
                {
                    foreach (var loopAccessGroup in loopAccessGroups.Reverse())
                    {
                        _activeLoopAccessGroups.Push(loopAccessGroup);
                    }
                }

                if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: true))
                {
                    return false;
                }

                if (!CurrentBlock.HasTerminator)
                {
                    EnsureGoto(conditionBlock.Id, loopContracts, loopAccessGroups, loopBehavior);
                }
            }
            finally
            {
                if (loopAccessGroups is { Count: > 0 })
                {
                    for (var index = 0; index < loopAccessGroups.Count; index++)
                    {
                        _activeLoopAccessGroups.Pop();
                    }
                }

                _breakTargets.Pop();
                _loops.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplatePatternWhile(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null || statement.ConditionPattern is null)
            {
                return false;
            }

            var loopBehavior = statement.LoopBehavior;
            var loopContracts = statement.LoopContracts is { Count: > 0 } ? statement.LoopContracts : null;
            var loopAccessGroups = CreateIndependentLoopAccessGroups(loopContracts);
            var conditionBlock = CreateBlock("typed_while_cond");
            var bodyBlock = CreateBlock("typed_while_body");
            var exitBlock = CreateBlock("typed_while_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            var switchValue = LowerImportedTypedTemplateExpressionCore(statement.Expression, expectedType: null);
            if (switchValue is null)
            {
                return false;
            }

            if (!TryBuildImportedTypedTemplateSwitchLabel(statement.ConditionPattern, switchValue.Type, out var builtLabel)
                || !TryRegisterSwitchCaptureLocals([builtLabel], switchValue.Type, out var labels))
            {
                return false;
            }

            if (!EmitSwitchSectionDecision(
                    labels,
                    switchValue,
                    bodyBlock.Id,
                    exitBlock.Id,
                    RenderImportedTypedTemplateExpressionCore(statement.Expression),
                    0))
            {
                return false;
            }

            _loops.Push(new LoopTargets(
                statement.Name,
                conditionBlock.Id,
                exitBlock.Id,
                _scopes.Count,
                loopBehavior,
                loopContracts,
                loopAccessGroups));
            _breakTargets.Push(new BreakTargets(statement.Name, exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                if (loopAccessGroups is { Count: > 0 })
                {
                    foreach (var loopAccessGroup in loopAccessGroups.Reverse())
                    {
                        _activeLoopAccessGroups.Push(loopAccessGroup);
                    }
                }

                _scopes.Push(new ScopeFrame());
                TrackSwitchSectionCaptureLocals(labels, switchValue.Type);
                try
                {
                    if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: false))
                    {
                        return false;
                    }
                }
                finally
                {
                    var bodyScope = _scopes.Pop();
                    EmitStorageDead(bodyScope);
                    RestoreScopedNameAliases(bodyScope);
                }

                if (!CurrentBlock.HasTerminator)
                {
                    EnsureGoto(conditionBlock.Id, loopContracts, loopAccessGroups, loopBehavior);
                }
            }
            finally
            {
                if (loopAccessGroups is { Count: > 0 })
                {
                    for (var index = 0; index < loopAccessGroups.Count; index++)
                    {
                        _activeLoopAccessGroups.Pop();
                    }
                }

                _breakTargets.Pop();
                _loops.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerImportedTypedTemplateFor(ImportedTemplateTypedBodyStatementSummary statement)
        {
            _scopes.Push(new ScopeFrame());
            try
            {
                if (!TryLowerImportedTypedTemplateStatementList(statement.Initializer, createScope: false))
                {
                    return false;
                }

                var loopBehavior = statement.LoopBehavior;
                var loopContracts = statement.LoopContracts is { Count: > 0 } ? statement.LoopContracts : null;
                var loopAccessGroups = CreateIndependentLoopAccessGroups(loopContracts);
                var conditionBlock = CreateBlock("typed_for_cond");
                var bodyBlock = CreateBlock("typed_for_body");
                var iteratorBlock = CreateBlock("typed_for_iter");
                var exitBlock = CreateBlock("typed_for_exit");

                EnsureGoto(conditionBlock.Id);

                CurrentBlock = conditionBlock;
                if (statement.Expression is null)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [bodyBlock.Id]);
                }
                else
                {
                    var condition = LowerImportedTypedTemplateExpressionCore(statement.Expression, StarkTypeSymbols.Bool);
                    if (condition is null)
                    {
                        return false;
                    }

                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [bodyBlock.Id, exitBlock.Id],
                        ConditionText: RenderImportedTypedTemplateExpressionCore(statement.Expression),
                        Condition: condition);
                }

                _loops.Push(new LoopTargets(statement.Name, iteratorBlock.Id, exitBlock.Id, _scopes.Count, null, null, null));
                _breakTargets.Push(new BreakTargets(statement.Name, exitBlock.Id, _scopes.Count));
                CurrentBlock = bodyBlock;
                var loopAccessGroupsPushed = false;
                try
                {
                    if (loopAccessGroups is { Count: > 0 })
                    {
                        foreach (var loopAccessGroup in loopAccessGroups.Reverse())
                        {
                            _activeLoopAccessGroups.Push(loopAccessGroup);
                        }

                        loopAccessGroupsPushed = true;
                    }

                    if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: true))
                    {
                        return false;
                    }

                    if (loopAccessGroups is { Count: > 0 })
                    {
                        for (var index = 0; index < loopAccessGroups.Count; index++)
                        {
                            _activeLoopAccessGroups.Pop();
                        }

                        loopAccessGroupsPushed = false;
                    }

                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(iteratorBlock.Id);
                    }

                    CurrentBlock = iteratorBlock;
                    if (!TryLowerImportedTypedTemplateStatementList(statement.Iterator, createScope: false))
                    {
                        return false;
                    }

                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(conditionBlock.Id, loopContracts, loopAccessGroups, loopBehavior);
                    }
                }
                finally
                {
                    if (loopAccessGroupsPushed && loopAccessGroups is { Count: > 0 })
                    {
                        for (var index = 0; index < loopAccessGroups.Count; index++)
                        {
                            _activeLoopAccessGroups.Pop();
                        }
                    }

                    _breakTargets.Pop();
                    _loops.Pop();
                }

                CurrentBlock = exitBlock;
                return true;
            }
            finally
            {
                var scope = _scopes.Pop();
                EmitStorageDead(scope);
                RestoreScopedNameAliases(scope);
            }
        }

        private bool TryLowerImportedTypedTemplateForTraversal(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.TraversalSourceExpression is null
                || string.IsNullOrWhiteSpace(statement.LoopBehavior)
                || string.IsNullOrWhiteSpace(statement.TraversalElementName)
                || statement.TraversalElementType is null)
            {
                return false;
            }

            var hasIndexBinding = statement.TraversalIndexName is not null
                || statement.TraversalIndexStorageClass is not null
                || statement.TraversalIndexType is not null;
            if (hasIndexBinding
                && (string.IsNullOrWhiteSpace(statement.TraversalIndexName)
                    || string.IsNullOrWhiteSpace(statement.TraversalIndexStorageClass)
                    || statement.TraversalIndexType is null))
            {
                return false;
            }

            _scopes.Push(new ScopeFrame());
            try
            {
                var source = LowerImportedTypedTemplateExpressionCore(statement.TraversalSourceExpression, expectedType: null);
                if (source is null)
                {
                    return false;
                }

                var sourceText = RenderImportedTypedTemplateExpressionCore(statement.TraversalSourceExpression);
                source = MaterializeTraversalSource(source, context: null, sourceText: sourceText);
                if (source.Type.Kind is not (StarkTypeKind.FixedArray or StarkTypeKind.Slice or StarkTypeKind.Dynamic)
                    || source.Type.ElementType is null)
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported for-in traversal source '{sourceText}' is not a fixed array, slice, or dynamic storage value.");
                }

                var length = LowerTraversalLength(source, context: null!);
                var hiddenIndex = CreateTemporaryLocal(NonNegativeI64Type, "typed_for_index");
                EmitOperandAssignment(
                    hiddenIndex,
                    new MidLevelIrIntegerConstantOperand(BigInteger.Zero, NonNegativeI64Type),
                    "0");

                MidLevelIrLocalOperand? userIndex = null;
                if (hasIndexBinding)
                {
                    var indexType = ApplyGenericSubstitution(statement.TraversalIndexType!);
                    var indexName = DeclareLocal(
                        statement.TraversalIndexName!,
                        indexType,
                        statement.TraversalIndexStorageClass!,
                        isMutable: false,
                        isConstant: false);
                    Emit(MidLevelIrStatementKind.StorageLive, indexName, indexName, indexType);
                    InitializeRuntimeDropState(indexName, indexType, isActive: false);
                    userIndex = new MidLevelIrLocalOperand(indexName, indexType);
                }

                var elementBindingType = ApplyGenericSubstitution(statement.TraversalElementType);
                var elementName = DeclareLocal(
                    statement.TraversalElementName,
                    elementBindingType,
                    storageClass: "stack",
                    isMutable: false,
                    isConstant: false);
                Emit(MidLevelIrStatementKind.StorageLive, elementName, elementName, elementBindingType);
                InitializeRuntimeDropState(elementName, elementBindingType, isActive: false);
                var elementLocal = new MidLevelIrLocalOperand(elementName, elementBindingType);

                var loopBehavior = statement.LoopBehavior;
                var loopContracts = statement.LoopContracts is { Count: > 0 } ? statement.LoopContracts : null;
                var loopAccessGroups = CreateIndependentLoopAccessGroups(loopContracts);
                var conditionBlock = CreateBlock($"typed_forin_{loopBehavior}_cond");
                var bodyBlock = CreateBlock("typed_forin_body");
                var iteratorBlock = CreateBlock("typed_forin_iter");
                var exitBlock = CreateBlock("typed_forin_exit");

                EnsureGoto(conditionBlock.Id);

                CurrentBlock = conditionBlock;
                var hasElement = EmitRequiredTemporary(
                    new MidLevelIrBinaryRValue(
                        MidLevelIrBinaryOperator.LessThan,
                        hiddenIndex,
                        length,
                        StarkTypeSymbols.Bool,
                        $"{hiddenIndex.Text} < {length.Text}"),
                    "cmp");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [bodyBlock.Id, exitBlock.Id],
                    ConditionText: $"{hiddenIndex.Text} < {length.Text}",
                    Condition: hasElement);

                _loops.Push(new LoopTargets(statement.Name, iteratorBlock.Id, exitBlock.Id, _scopes.Count, null, null, null));
                _breakTargets.Push(new BreakTargets(statement.Name, exitBlock.Id, _scopes.Count));
                CurrentBlock = bodyBlock;
                var loopAccessGroupsPushed = false;
                try
                {
                    if (loopAccessGroups is { Count: > 0 })
                    {
                        foreach (var loopAccessGroup in loopAccessGroups.Reverse())
                        {
                            _activeLoopAccessGroups.Push(loopAccessGroup);
                        }

                        loopAccessGroupsPushed = true;
                    }

                    if (userIndex is not null)
                    {
                        var visibleIndex = CoerceOperand(hiddenIndex, userIndex.Type) ?? hiddenIndex;
                        Emit(
                            MidLevelIrStatementKind.Assign,
                            $"{userIndex.Name} = {hiddenIndex.Text}",
                            userIndex.Name,
                            userIndex.Type,
                            new MidLevelIrUseRValue(visibleIndex),
                            writeKind: MemoryWriteKind.Initialization);
                        SetRuntimeDropState(userIndex.Name, isActive: true);
                    }

                    var elementValue = LowerTraversalElementBindingValue(source, hiddenIndex, elementBindingType, context: null!);
                    Emit(
                        MidLevelIrStatementKind.Assign,
                        $"{elementLocal.Name} = {source.Text}[{hiddenIndex.Text}]",
                        elementLocal.Name,
                        elementLocal.Type,
                        new MidLevelIrUseRValue(elementValue),
                        writeKind: MemoryWriteKind.Initialization);
                    SetRuntimeDropState(elementLocal.Name, isActive: true);

                    if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: true))
                    {
                        return false;
                    }

                    if (loopAccessGroups is { Count: > 0 })
                    {
                        for (var index = 0; index < loopAccessGroups.Count; index++)
                        {
                            _activeLoopAccessGroups.Pop();
                        }

                        loopAccessGroupsPushed = false;
                    }

                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(iteratorBlock.Id);
                    }

                    CurrentBlock = iteratorBlock;
                    var nextIndex = EmitRequiredTemporary(
                        new MidLevelIrBinaryRValue(
                            MidLevelIrBinaryOperator.Add,
                            hiddenIndex,
                            new MidLevelIrIntegerConstantOperand(BigInteger.One, NonNegativeI64Type),
                            NonNegativeI64Type,
                            $"{hiddenIndex.Text} + 1"),
                        "typed_for_index");
                    EmitOperandAssignment(hiddenIndex, nextIndex, nextIndex.Text);
                    if (!CurrentBlock.HasTerminator)
                    {
                        EnsureGoto(conditionBlock.Id, loopContracts, loopAccessGroups, loopBehavior);
                    }
                }
                finally
                {
                    if (loopAccessGroupsPushed && loopAccessGroups is { Count: > 0 })
                    {
                        for (var index = 0; index < loopAccessGroups.Count; index++)
                        {
                            _activeLoopAccessGroups.Pop();
                        }
                    }

                    _breakTargets.Pop();
                    _loops.Pop();
                }

                CurrentBlock = exitBlock;
                return true;
            }
            finally
            {
                var scope = _scopes.Pop();
                EmitStorageDead(scope);
                RestoreScopedNameAliases(scope);
            }
        }

        private bool TryLowerImportedTypedTemplateReturn(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null)
            {
                if (_function.Signature.ReturnType.Kind != StarkTypeKind.Void)
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported typed-template return for '{_function.Name}' is missing a value for non-void return type '{_function.Signature.ReturnType.DisplayName}'.");
                }

                EmitStorageDeadBeyondDepth(0);
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Return,
                    Targets: []);
                return true;
            }

            var operand = LowerImportedTypedTemplateReturnExpression(statement.Expression, _function.Signature.ReturnType);
            if (operand is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported typed-template return for '{_function.Name}' did not lower to a MIR operand.");
            }

            if (_function.Signature.ReturnType.BorrowKind == StarkBorrowKind.None)
            {
                RecordMoveFromOperand(operand, _function.Signature.ReturnType);
            }

            EmitStorageDeadBeyondDepth(0);
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Return,
                Targets: [],
                ValueText: operand.Text,
                Value: operand);
            return true;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateReturnExpression(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol returnType)
        {
            if (returnType.BorrowKind != StarkBorrowKind.None
                && StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType)
                && TryResolveImportedTypedTemplateAssignmentTarget(expression, out var target)
                && target.Type.BorrowKind == StarkBorrowKind.None)
            {
                return BuildAddress(target);
            }

            if (returnType.BorrowKind != StarkBorrowKind.None
                && !StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType))
            {
                return LowerImportedTypedTemplateExpressionCore(
                    expression,
                    StarkTypeSymbols.BorrowReturnValueType(returnType));
            }

            return LowerImportedTypedTemplateExpressionCore(expression, returnType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateExpressionCore(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            switch (expression.Kind)
            {
                case ImportedTemplateTypedBodyExpressionKind.NameReference:
                    {
                        if (expression.Name is null)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template name reference is missing its symbol name.");
                        }

                        if (_compileTimeConstantState.TryResolve(expression.Name, out var constant))
                        {
                            if (expectedType is not null
                                && CompileTimeExpressionEvaluator.TryCoerce(constant, expectedType, out var coerced))
                            {
                                return CreateCompileTimeOperand(coerced);
                            }

                            var constantOperand = CreateCompileTimeOperand(constant);
                            return expectedType is null
                                ? constantOperand
                                : CoerceOperand(constantOperand, expectedType);
                        }

                        var result = ResolveNamedOperand(expression.Name, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType: null);
                    }

                case ImportedTemplateTypedBodyExpressionKind.Literal:
                    {
                        var result = LowerImportedTypedTemplateLiteral(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.ArrayInitializer:
                    {
                        var result = LowerImportedTypedTemplateArrayInitializer(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.ObjectInitializer:
                    {
                        var result = LowerImportedTypedTemplateObjectInitializerExpression(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.Assignment:
                    {
                        var result = LowerImportedTypedTemplateAssignmentExpression(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.Conversion:
                    {
                        var result = LowerImportedTypedTemplateConversion(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.TryPropagation:
                    {
                        var result = LowerImportedTypedTemplateTryPropagation(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.UnaryOperation:
                    {
                        var result = LowerImportedTypedTemplateUnary(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.BinaryOperation:
                    {
                        var result = LowerImportedTypedTemplateBinary(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.ComparisonChain:
                    {
                        var result = LowerImportedTypedTemplateComparisonChain(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.Conditional:
                    {
                        var result = LowerImportedTypedTemplateConditional(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.Comptime:
                    {
                        var result = LowerImportedTypedTemplateComptime(expression, expectedType);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.TypeLayout:
                    {
                        var result = LowerImportedTypedTemplateTypeLayout(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.StructuralFact:
                    {
                        var result = LowerImportedTypedTemplateStructuralFact(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.ObjectCreation:
                    {
                        var result = LowerImportedTypedTemplateObjectCreation(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.EnumConstructor:
                    {
                        var result = LowerImportedTypedTemplateEnumConstructor(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.EnumCall:
                    {
                        var result = LowerImportedTypedTemplateEnumCall(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.EnumValue:
                    {
                        var result = LowerImportedTypedTemplateEnumValue(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.DirectCall:
                    {
                        if (!TryBuildImportedTypedTemplateDirectCall(expression, out var call))
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template direct call was accepted but did not bind to serialized call facts.");
                        }

                        if (call.Type.Kind == StarkTypeKind.Void)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template void direct call cannot be lowered as a value expression.");
                        }

                        var result = EmitRequiredTemporary(call, "call");
                        return RequireImportedTypedTemplateExpressionResult(
                            expression,
                            CoerceImportedTypedTemplateCallResult(call.SourceReturnType, result, expectedType),
                            expectedType: null);
                    }

                case ImportedTemplateTypedBodyExpressionKind.ClosureCall:
                    {
                        if (!TryBuildImportedTypedTemplateClosureCall(expression, out var call))
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template indirect call was accepted but did not bind to function-pointer or closure facts.");
                        }

                        if (call.Type.Kind == StarkTypeKind.Void)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template void indirect call cannot be lowered as a value expression.");
                        }

                        var result = EmitRequiredTemporary(call, "call");
                        return RequireImportedTypedTemplateExpressionResult(
                            expression,
                            CoerceImportedTypedTemplateCallResult(call.SourceReturnType, result, expectedType),
                            expectedType: null);
                    }

                case ImportedTemplateTypedBodyExpressionKind.IndexAccess:
                    {
                        var result = LowerImportedTypedTemplateIndexAccess(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.FieldAccess:
                    {
                        if (expression.Ordinal is not { } ordinal
                            || expression.Args.Count != 1)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template field access is missing its field ordinal or receiver.");
                        }

                        var receiver = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
                        if (receiver is null
                            || !_importedTemplateFieldAccesses.TryGetValue(ordinal, out var publishedFieldAccess))
                        {
                            throw LoweringInvariantViolation(
                                null,
                                $"Imported typed-template field access ordinal {ordinal} has no serialized field fact.");
                        }

                        var result = TryLowerKnownViewFieldAccess(receiver, publishedFieldAccess.FieldName, out var knownViewField)
                            ? knownViewField
                            : LowerKnownFieldAccess(
                                receiver,
                                publishedFieldAccess.FieldName,
                                publishedFieldAccess.FieldIndex,
                                ApplyGenericSubstitution(publishedFieldAccess.FieldType),
                                publishedFieldAccess.FieldName);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.MemberCall:
                    {
                        if (TryBuildImportedTypedTemplateDynTraitMemberCall(expression, out var dynTraitCall))
                        {
                            if (dynTraitCall.Type.Kind == StarkTypeKind.Void)
                            {
                                throw LoweringInvariantViolation(
                                    null,
                                    "Imported typed-template void dyn trait member call cannot be lowered as a value expression.");
                            }

                            var dynTraitResult = EmitRequiredTemporary(dynTraitCall, "call");
                            return RequireImportedTypedTemplateExpressionResult(
                                expression,
                                CoerceImportedTypedTemplateCallResult(dynTraitCall.SourceReturnType, dynTraitResult, expectedType),
                                expectedType: null);
                        }

                        if (!TryBuildImportedTypedTemplateMemberCall(expression, out var memberCall))
                        {
                            throw LoweringInvariantViolation(
                                null,
                                DescribeImportedTemplateMemberCallBindingFailure(expression, "member call"));
                        }

                        if (memberCall.Type.Kind == StarkTypeKind.Void)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template void member call cannot be lowered as a value expression.");
                        }

                        var result = EmitRequiredTemporary(memberCall, "call");
                        return RequireImportedTypedTemplateExpressionResult(
                            expression,
                            CoerceImportedTypedTemplateCallResult(memberCall.SourceReturnType, result, expectedType),
                            expectedType: null);
                    }

                case ImportedTemplateTypedBodyExpressionKind.FunctionAddress:
                    {
                        var result = LowerImportedTypedTemplateFunctionAddress(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.DynamicStorageOperation:
                    {
                        if (!TryBuildImportedTypedTemplateDynamicStorageOperation(expression, out var dynamicStorageOperation))
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template dynamic-storage operation was accepted but did not bind to serialized operation facts.");
                        }

                        if (dynamicStorageOperation.Type.Kind == StarkTypeKind.Void)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported typed-template void dynamic-storage operation cannot be lowered as a value expression.");
                        }

                        var result = EmitRequiredTemporary(dynamicStorageOperation, "dynamic");
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.DynTraitFromParts:
                    {
                        var result = LowerImportedTypedTemplateDynTraitFromParts(expression);
                        return RequireImportedTypedTemplateExpressionResult(expression, result, expectedType);
                    }

                case ImportedTemplateTypedBodyExpressionKind.TextInterpolation:
                case ImportedTemplateTypedBodyExpressionKind.TextBuild:
                    {
                        throw LoweringInvariantViolation(
                            null,
                            $"Imported typed-template text expression '{expression.Kind}' reached scalar MIR lowering instead of fixed-storage text lowering.");
                    }

                default:
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported typed-template expression kind '{expression.Kind}' has no MIR lowering case.");
            }
        }

        private MidLevelIrOperand RequireImportedTypedTemplateExpressionResult(
            ImportedTemplateTypedBodyExpressionSummary expression,
            MidLevelIrOperand? result,
            StarkTypeSymbol? expectedType)
        {
            if (result is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported typed-template expression '{RenderImportedTypedTemplateExpressionCore(expression)}' of kind '{expression.Kind}' did not lower to a MIR operand.");
            }

            if (expectedType is null)
            {
                return result;
            }

            var coerced = CoerceOperand(result, expectedType);
            if (coerced is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported typed-template expression '{RenderImportedTypedTemplateExpressionCore(expression)}' of kind '{expression.Kind}' produced '{result.Type.DisplayName}', which cannot coerce to expected type '{expectedType.DisplayName}'.");
            }

            return coerced;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateFunctionAddress(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateFunctionAddresses.TryGetValue(ordinal, out var functionAddress))
            {
                return null;
            }

            var signature = ApplyGenericSubstitution(functionAddress.Signature);
            var targetType = ApplyGenericSubstitution(functionAddress.TargetType);
            if (targetType.Kind != StarkTypeKind.FunctionPointer)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported function-address operation for '{signature.Name}' requires a function-pointer target type, but found '{targetType.DisplayName}'.");
            }

            return new MidLevelIrFunctionAddressOperand(
                ResolveCallTargetName(signature.Name, signature),
                targetType);
        }

        private MidLevelIrOperand? CoerceImportedTypedTemplateCallResult(
            StarkTypeSymbol? sourceReturnType,
            MidLevelIrOperand result,
            StarkTypeSymbol? expectedType)
        {
            if (expectedType is null)
            {
                return result;
            }

            if (sourceReturnType is { } returnType
                && StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType)
                && expectedType.BorrowKind != StarkBorrowKind.None
                && TypeCompatibilityFacts.CanAssign(expectedType, returnType))
            {
                return result;
            }

            return CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateTypeLayout(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Type is null || expression.Name is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported typed-template type-layout expression is missing its operation name or target type.");
            }

            var targetType = ApplyGenericSubstitution(expression.Type);
            var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                targetType,
                _namedTypes,
                _enumLayoutModel.Layouts,
                _publishedConcreteLayouts);
            if (layout is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported typed-template {expression.Name}({targetType.DisplayName}) requires a concrete runtime layout after generic substitution.");
            }

            var value = string.Equals(expression.Name, "alignof", StringComparison.Ordinal)
                ? layout.AlignmentBytes
                : layout.SizeBytes;
            var resultType = string.Equals(expression.Name, "alignof", StringComparison.Ordinal)
                ? StarkTypeSymbols.Integer(64, BigInteger.One, new BigInteger(long.MaxValue))
                : StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue));
            return new MidLevelIrIntegerConstantOperand(
                new BigInteger(value),
                resultType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateStructuralFact(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            return TryEvaluateImportedTypedTemplateStructuralFact(expression, out var constant)
                ? CreateCompileTimeOperand(constant)
                : null;
        }

        private bool TryEvaluateImportedTypedTemplateStructuralFact(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Name is null
                || expression.TypeArgs.Count == 0
                || !CompileTimeStructuralFacts.TryGetFactKind(expression.Name, out var factKind))
            {
                return false;
            }

            var targetType = ApplyGenericSubstitution(expression.TypeArgs[0]);
            var additionalTypeArguments = expression.TypeArgs
                .Skip(1)
                .Select(ApplyGenericSubstitution)
                .ToArray();
            var comptimeValueArguments = SubstituteImportedStructuralFactComptimeValues(expression.ComptimeValueArgs);
            var structuralArguments = new CompileTimeStructuralFactArguments(
                targetType,
                AdditionalTypeArgumentList: additionalTypeArguments,
                ComptimeValueArgumentList: comptimeValueArguments);

            if (!CompileTimeStructuralFacts.TryEvaluate(
                    factKind,
                    structuralArguments,
                    type => TryResolveCompileTimeNamedType(type, out var namedType)
                        ? namedType
                        : null,
                    type => ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                        type,
                        _namedTypes,
                        _enumLayoutModel.Layouts,
                        _publishedConcreteLayouts),
                    (target, trait) => TryResolveCompileTimeTraitConformance(
                        target,
                        trait,
                        CurrentModuleName,
                        out var implements)
                            ? implements
                            : null,
                    type => ResolveCompileTimeMethodSignatures(type, CurrentModuleName),
                    out var evaluated))
            {
                return false;
            }

            constant = evaluated;
            return true;
        }

        private IReadOnlyList<ComptimeValueArgumentSymbol> SubstituteImportedStructuralFactComptimeValues(
            IReadOnlyList<ComptimeValueArgumentSymbol> arguments)
        {
            if (arguments.Count == 0
                || _activeGenericValueSubstitution is not { Count: > 0 } substitution)
            {
                return arguments;
            }

            var result = new ComptimeValueArgumentSymbol[arguments.Count];
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                result[index] = argument.IsSymbolic
                    && argument.SymbolicSourceName is { } name
                    && substitution.TryGetValue(name, out var value)
                        ? argument with { IntegerValue = value, IsSymbolic = false, SymbolicSourceName = null }
                        : argument;
            }

            return result;
        }

        private bool TryEvaluateImportedTypedTemplateConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            switch (expression.Kind)
            {
                case ImportedTemplateTypedBodyExpressionKind.NameReference:
                    return expression.Name is not null
                        && TryResolveCompileTimeConstantValue(expression.Name, out constant);

                case ImportedTemplateTypedBodyExpressionKind.Literal:
                    return TryEvaluateImportedTypedTemplateLiteralConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.Comptime:
                    return expression.Args.Count == 1
                        && TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out constant);

                case ImportedTemplateTypedBodyExpressionKind.Conversion:
                    return TryEvaluateImportedTypedTemplateConversionConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.UnaryOperation:
                    return TryEvaluateImportedTypedTemplateUnaryConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.BinaryOperation:
                    return TryEvaluateImportedTypedTemplateBinaryConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.ComparisonChain:
                    return TryEvaluateImportedTypedTemplateComparisonChainConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.Conditional:
                    return TryEvaluateImportedTypedTemplateConditionalConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.TypeLayout:
                    return TryEvaluateImportedTypedTemplateTypeLayoutConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.StructuralFact:
                    return TryEvaluateImportedTypedTemplateStructuralFact(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.ArrayInitializer:
                    return TryEvaluateImportedTypedTemplateArrayConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.ObjectInitializer:
                    return TryEvaluateImportedTypedTemplateObjectInitializerConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.ObjectCreation:
                    return TryEvaluateImportedTypedTemplateObjectCreationConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.EnumConstructor:
                    return TryEvaluateImportedTypedTemplateEnumConstructorConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.EnumCall:
                    return TryEvaluateImportedTypedTemplateEnumCallConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.EnumValue:
                    return TryEvaluateImportedTypedTemplateEnumValueConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.DirectCall:
                    return TryEvaluateImportedTypedTemplateDirectCallConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.MemberCall:
                    return TryEvaluateImportedTypedTemplateMemberCallConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.IndexAccess:
                    return TryEvaluateImportedTypedTemplateIndexConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.FieldAccess:
                    return TryEvaluateImportedTypedTemplateFieldConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.TextInterpolation:
                    return TryEvaluateImportedTypedTemplateTextInterpolationConstant(expression, out constant);

                case ImportedTemplateTypedBodyExpressionKind.TextBuild:
                    return TryEvaluateImportedTypedTemplateTextBuildConstant(expression, out constant);

                default:
                    return false;
            }
        }

        private bool TryEvaluateImportedTypedTemplateDirectCallConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Ordinal is not { } ordinal
                || !CurrentImportedTemplateDirectCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            if (!TryResolveImportedTemplateSummary(signature, out var templateName, out var templateSignature, out var templateSummary)
                || templateSummary.TypedBody is not { } typedBody
                || !FunctionKindFacts.IsCompileTimeCallable(templateSignature.Kind)
                || expression.Args.Count != signature.Parameters.Count)
            {
                return false;
            }

            var arguments = new CompileTimeConstant[expression.Args.Count];
            for (var index = 0; index < expression.Args.Count; index++)
            {
                if (!TryEvaluateImportedTypedTemplateConstant(expression.Args[index], out var argument)
                    || !TryCoerceImportedTypedTemplateArgument(argument, signature.Parameters[index].Type, out arguments[index]))
                {
                    return false;
                }
            }

            return TryEvaluateImportedTypedTemplateFunctionBodyConstant(
                templateName,
                templateSignature,
                templateSummary,
                signature,
                typedBody,
                arguments,
                out constant);
        }

        private bool TryEvaluateImportedTypedTemplateMemberCallConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count == 0
                || !CurrentImportedTemplateMemberCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            if (!TryResolveImportedTemplateSummary(signature, out var templateName, out var templateSignature, out var templateSummary)
                || templateSummary.TypedBody is not { } typedBody
                || !FunctionKindFacts.IsCompileTimeCallable(templateSignature.Kind)
                || expression.Args.Count != signature.Parameters.Count)
            {
                return false;
            }

            var arguments = new CompileTimeConstant[expression.Args.Count];
            for (var index = 0; index < expression.Args.Count; index++)
            {
                if (!TryEvaluateImportedTypedTemplateConstant(expression.Args[index], out var argument)
                    || !TryCoerceImportedTypedTemplateArgument(argument, signature.Parameters[index].Type, out arguments[index]))
                {
                    return false;
                }
            }

            return TryEvaluateImportedTypedTemplateFunctionBodyConstant(
                templateName,
                templateSignature,
                templateSummary,
                signature,
                typedBody,
                arguments,
                out constant);
        }

        private static bool TryCoerceImportedTypedTemplateArgument(
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

            return TryRetypeImportedTypedTemplateConstant(unqualifiedCoerced, parameterType, out coerced);
        }

        private static bool TryRetypeImportedTypedTemplateConstant(
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

        private bool TryResolveImportedTemplateSummary(
            TypedFunctionSignature signature,
            out string templateName,
            out TypedFunctionSignature templateSignature,
            out ImportedFunctionTemplateSummary templateSummary)
        {
            templateName = signature.TemplateName ?? signature.SourceName ?? signature.Name;
            templateSignature = default!;
            templateSummary = default!;

            foreach (var candidate in new[] { signature.TemplateName, signature.SourceName, signature.Name })
            {
                if (string.IsNullOrWhiteSpace(candidate)
                    || !_importedFunctionTemplates.TryGetValue(candidate!, out var resolvedSummary)
                    || !TryResolveFunctionSignature(candidate!, out var resolvedSignature))
                {
                    continue;
                }

                templateName = candidate!;
                templateSignature = resolvedSignature;
                templateSummary = resolvedSummary;
                return true;
            }

            return false;
        }

        private bool TryEvaluateImportedTypedTemplateFunctionBodyConstant(
            string templateName,
            TypedFunctionSignature templateSignature,
            ImportedFunctionTemplateSummary templateSummary,
            TypedFunctionSignature concreteSignature,
            ImportedTemplateTypedBodySummary typedBody,
            IReadOnlyList<CompileTimeConstant> arguments,
            out CompileTimeConstant constant)
        {
            constant = default;
            var previousTypeSubstitution = _activeGenericTypeSubstitution;
            var previousValueSubstitution = _activeGenericValueSubstitution;
            var previousComptimeParameters = _activeComptimeGenericParameters;
            var previousImportedTemplateEvaluationContext = _activeImportedTemplateEvaluationContext;
            var typeSubstitution = previousTypeSubstitution is { Count: > 0 }
                ? new Dictionary<string, StarkTypeSymbol>(previousTypeSubstitution, StringComparer.Ordinal)
                : new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            var valueSubstitution = previousValueSubstitution is { Count: > 0 }
                ? new Dictionary<string, BigInteger>(previousValueSubstitution, StringComparer.Ordinal)
                : new Dictionary<string, BigInteger>(StringComparer.Ordinal);

            if (!BindImportedTypedTemplateTypeArguments(templateSignature, concreteSignature, typeSubstitution)
                || !BindImportedTypedTemplateComptimeArguments(templateSignature, concreteSignature, valueSubstitution))
            {
                return false;
            }

            _compileTimeConstantState.PushScope();
            try
            {
                _activeGenericTypeSubstitution = typeSubstitution.Count == 0 ? null : typeSubstitution;
                _activeGenericValueSubstitution = valueSubstitution.Count == 0 ? null : valueSubstitution;
                _activeComptimeGenericParameters = templateSignature.ComptimeGenericParams.Count == 0
                    ? null
                    : templateSignature.ComptimeGenericParams.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
                _activeImportedTemplateEvaluationContext = GetImportedTemplateEvaluationContext(templateName, templateSummary);

                DeclareImportedTypedTemplateComptimeConstants(templateSignature, valueSubstitution);
                for (var index = 0; index < templateSignature.Parameters.Count && index < arguments.Count; index++)
                {
                    _compileTimeConstantState.Declare(
                        templateSignature.Parameters[index].Name,
                        arguments[index],
                        isMutable: false);
                }

                if (!TryExecuteImportedTypedTemplateStatementListConstant(
                        typedBody.Statements,
                        createScope: true,
                        out var flow,
                        out var result)
                    || flow != CompileTimeStatementFlow.Return
                    || !CompileTimeExpressionEvaluator.TryCoerce(result, concreteSignature.ReturnType, out constant))
                {
                    return false;
                }

                return true;
            }
            finally
            {
                _compileTimeConstantState.PopScope();
                _activeGenericTypeSubstitution = previousTypeSubstitution;
                _activeGenericValueSubstitution = previousValueSubstitution;
                _activeComptimeGenericParameters = previousComptimeParameters;
                _activeImportedTemplateEvaluationContext = previousImportedTemplateEvaluationContext;
            }
        }

        private bool TryExecuteImportedTypedTemplateStatementListConstant(
            IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> statements,
            bool createScope,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            flow = CompileTimeStatementFlow.None;
            returnValue = default;

            if (createScope)
            {
                _compileTimeConstantState.PushScope();
            }

            try
            {
                foreach (var statement in statements)
                {
                    if (!TryExecuteImportedTypedTemplateStatementConstant(statement, out flow, out returnValue))
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
                if (createScope)
                {
                    _compileTimeConstantState.PopScope();
                }
            }
        }

        private bool TryExecuteImportedTypedTemplateStatementConstant(
            ImportedTemplateTypedBodyStatementSummary statement,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            flow = CompileTimeStatementFlow.None;
            returnValue = default;

            switch (statement.Kind)
            {
                case ImportedTemplateTypedBodyStatementKind.Block:
                    return TryExecuteImportedTypedTemplateStatementListConstant(
                        statement.Body,
                        createScope: true,
                        out flow,
                        out returnValue);

                case ImportedTemplateTypedBodyStatementKind.Empty:
                    return true;

                case ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration:
                    return TryExecuteImportedTypedTemplateLocalDeclarationConstant(statement);

                case ImportedTemplateTypedBodyStatementKind.ExpressionStatement:
                    return statement.Expression is not null
                        && TryEvaluateImportedTypedTemplateConstant(statement.Expression, out _);

                case ImportedTemplateTypedBodyStatementKind.Assignment:
                    return TryExecuteImportedTypedTemplateAssignmentConstant(statement);

                case ImportedTemplateTypedBodyStatementKind.If:
                    return TryExecuteImportedTypedTemplateIfConstant(statement, out flow, out returnValue);

                case ImportedTemplateTypedBodyStatementKind.While:
                    return TryExecuteImportedTypedTemplateWhileConstant(statement, out flow, out returnValue);

                case ImportedTemplateTypedBodyStatementKind.For:
                    return TryExecuteImportedTypedTemplateForConstant(statement, out flow, out returnValue);

                case ImportedTemplateTypedBodyStatementKind.ForTraversal:
                    return TryExecuteImportedTypedTemplateForTraversalConstant(statement, out flow, out returnValue);

                case ImportedTemplateTypedBodyStatementKind.Switch:
                    return TryExecuteImportedTypedTemplateSwitchConstant(statement, out flow, out returnValue);

                case ImportedTemplateTypedBodyStatementKind.Break:
                    flow = CompileTimeStatementFlow.Break;
                    _compileTimeConstantState.SetPendingControlFlowLabel(statement.Name);
                    return true;

                case ImportedTemplateTypedBodyStatementKind.Continue:
                    flow = CompileTimeStatementFlow.Continue;
                    _compileTimeConstantState.SetPendingControlFlowLabel(statement.Name);
                    return true;

                case ImportedTemplateTypedBodyStatementKind.Return:
                    flow = CompileTimeStatementFlow.Return;
                    if (statement.Expression is null)
                    {
                        returnValue = CompileTimeConstant.Void();
                        return true;
                    }

                    return TryEvaluateImportedTypedTemplateConstant(statement.Expression, out returnValue);

                default:
                    return false;
            }
        }

        private bool TryExecuteImportedTypedTemplateLocalDeclarationConstant(
            ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Name is null
                || statement.Type is not { } declaredSourceType)
            {
                return false;
            }

            var declaredType = ApplyGenericSubstitution(declaredSourceType);
            CompileTimeConstant value;
            if (statement.Expression is { } initializer)
            {
                if (!TryEvaluateImportedTypedTemplateConstant(initializer, out var initialized)
                    || !CompileTimeExpressionEvaluator.TryCoerce(initialized, declaredType, out value))
                {
                    return false;
                }
            }
            else if (!TryCreateZeroCompileTimeConstant(declaredType, out value))
            {
                return false;
            }

            _compileTimeConstantState.Declare(statement.Name, value, statement.IsMutable && !statement.IsConstant);
            return true;
        }

        private bool TryExecuteImportedTypedTemplateAssignmentConstant(
            ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null
                || !TryEvaluateImportedTypedTemplateConstant(statement.Expression, out var right))
            {
                return false;
            }

            var assignmentOperator = string.IsNullOrWhiteSpace(statement.AssignmentOperator)
                ? "="
                : statement.AssignmentOperator!;
            if (string.Equals(assignmentOperator, "init =", StringComparison.Ordinal))
            {
                assignmentOperator = "=";
            }

            if (statement.TargetExpression is { } targetExpression)
            {
                return TryResolveImportedTypedTemplateConstantPlace(targetExpression, out var rootName, out var segments)
                    && TryAssignImportedTypedTemplateConstantPlace(rootName, segments, assignmentOperator, right);
            }

            return statement.Name is not null
                && TryAssignImportedTypedTemplateConstantPlace(statement.Name, [], assignmentOperator, right);
        }

        private bool TryAssignImportedTypedTemplateConstantPlace(
            string rootName,
            IReadOnlyList<ImportedTemplateCompileTimePlaceSegment> segments,
            string assignmentOperator,
            CompileTimeConstant right)
        {
            if (!_compileTimeConstantState.TryResolve(rootName, out var root))
            {
                return false;
            }

            var assignedValue = right;
            if (!string.Equals(assignmentOperator, "=", StringComparison.Ordinal))
            {
                if (!TryGetImportedTypedTemplateAssignmentBinaryOperator(
                        assignmentOperator,
                        out var binaryOperator,
                        out var requireInteger)
                    || !TryReadImportedTypedTemplateConstantPlace(root, segments, out var current)
                    || !CompileTimeExpressionEvaluator.TryCoerce(right, current.Type, out var coercedRight)
                    || !CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                        binaryOperator,
                        current,
                        coercedRight,
                        requireInteger,
                        out assignedValue))
                {
                    return false;
                }
            }

            if (!TryUpdateImportedTypedTemplateConstantPlace(root, segments, assignedValue, out var updatedRoot))
            {
                return false;
            }

            return _compileTimeConstantState.TryAssign(rootName, updatedRoot);
        }

        private static bool TryGetImportedTypedTemplateAssignmentBinaryOperator(
            string assignmentOperator,
            out string binaryOperator,
            out bool requireInteger)
        {
            binaryOperator = assignmentOperator switch
            {
                "+=" => "+",
                "-=" => "-",
                "*=" => "*",
                "/=" => "/",
                "%=" => "%",
                "+%=" => "+%",
                "-%=" => "-%",
                "*%=" => "*%",
                "+|=" => "+|",
                "-|=" => "-|",
                "*|=" => "*|",
                "&=" => "&",
                "|=" => "|",
                "^=" => "^",
                _ => string.Empty
            };
            requireInteger = assignmentOperator is "+%=" or "-%=" or "*%=" or "+|=" or "-|=" or "*|=" or "&=" or "|=" or "^=";
            return binaryOperator.Length > 0;
        }

        private bool TryResolveImportedTypedTemplateConstantPlace(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out string rootName,
            out IReadOnlyList<ImportedTemplateCompileTimePlaceSegment> segments)
        {
            rootName = string.Empty;
            segments = [];

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.NameReference
                && !string.IsNullOrWhiteSpace(expression.Name))
            {
                rootName = expression.Name!;
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.FieldAccess
                && expression.Args.Count == 1
                && expression.Ordinal is { } fieldOrdinal
                && CurrentImportedTemplateFieldAccesses.TryGetValue(fieldOrdinal, out var fieldAccess)
                && TryResolveImportedTypedTemplateConstantPlace(expression.Args[0], out rootName, out var receiverSegments))
            {
                segments = receiverSegments
                    .Append(ImportedTemplateCompileTimePlaceSegment.Field(fieldAccess.FieldIndex))
                    .ToArray();
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.IndexAccess
                && expression.Args.Count == 2
                && TryResolveImportedTypedTemplateConstantPlace(expression.Args[0], out rootName, out var indexedSegments)
                && TryEvaluateImportedTypedTemplateConstant(expression.Args[1], out var indexConstant)
                && indexConstant.Kind == CompileTimeConstantKind.Integer
                && indexConstant.IntegerValue >= 0
                && indexConstant.IntegerValue <= int.MaxValue)
            {
                segments = indexedSegments
                    .Append(ImportedTemplateCompileTimePlaceSegment.Index((int)indexConstant.IntegerValue))
                    .ToArray();
                return true;
            }

            return false;
        }

        private static bool TryReadImportedTypedTemplateConstantPlace(
            CompileTimeConstant root,
            IReadOnlyList<ImportedTemplateCompileTimePlaceSegment> segments,
            out CompileTimeConstant value)
        {
            value = root;
            foreach (var segment in segments)
            {
                switch (segment.Kind)
                {
                    case ImportedTemplateCompileTimePlaceSegmentKind.Field:
                        if (value.Kind != CompileTimeConstantKind.NamedAggregate
                            || segment.Ordinal < 0
                            || segment.Ordinal >= value.Elements.Count)
                        {
                            value = default;
                            return false;
                        }

                        value = value.Elements[segment.Ordinal];
                        break;

                    case ImportedTemplateCompileTimePlaceSegmentKind.Index:
                        if (value.Kind != CompileTimeConstantKind.FixedArray
                            || segment.Ordinal < 0
                            || segment.Ordinal >= value.Elements.Count)
                        {
                            value = default;
                            return false;
                        }

                        value = value.Elements[segment.Ordinal];
                        break;

                    default:
                        value = default;
                        return false;
                }
            }

            return true;
        }

        private static bool TryUpdateImportedTypedTemplateConstantPlace(
            CompileTimeConstant root,
            IReadOnlyList<ImportedTemplateCompileTimePlaceSegment> segments,
            CompileTimeConstant assignedValue,
            out CompileTimeConstant updated)
        {
            if (segments.Count == 0)
            {
                return CompileTimeExpressionEvaluator.TryCoerce(assignedValue, root.Type, out updated);
            }

            var segment = segments[0];
            var remaining = segments.Skip(1).ToArray();
            switch (segment.Kind)
            {
                case ImportedTemplateCompileTimePlaceSegmentKind.Field:
                    {
                        if (root.Kind != CompileTimeConstantKind.NamedAggregate
                            || segment.Ordinal < 0
                            || segment.Ordinal >= root.Elements.Count
                            || !TryUpdateImportedTypedTemplateConstantPlace(
                                root.Elements[segment.Ordinal],
                                remaining,
                                assignedValue,
                                out var updatedField)
                            || !TryWithCompileTimeNamedAggregateField(root, segment.Ordinal, updatedField, out updated))
                        {
                            updated = default;
                            return false;
                        }

                        return true;
                    }

                case ImportedTemplateCompileTimePlaceSegmentKind.Index:
                    {
                        if (root.Kind != CompileTimeConstantKind.FixedArray
                            || segment.Ordinal < 0
                            || segment.Ordinal >= root.Elements.Count
                            || !TryUpdateImportedTypedTemplateConstantPlace(
                                root.Elements[segment.Ordinal],
                                remaining,
                                assignedValue,
                                out var updatedElement))
                        {
                            updated = default;
                            return false;
                        }

                        var elements = root.Elements.ToArray();
                        elements[segment.Ordinal] = updatedElement;
                        updated = CompileTimeConstant.FixedArray(elements, root.Type);
                        return true;
                    }

                default:
                    updated = default;
                    return false;
            }
        }

        private bool TryExecuteImportedTypedTemplateIfConstant(
            ImportedTemplateTypedBodyStatementSummary statement,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            flow = CompileTimeStatementFlow.None;
            returnValue = default;

            if (statement.Expression is null
                || !TryEvaluateImportedTypedTemplateConstant(statement.Expression, out var condition))
            {
                return false;
            }

            if (statement.ConditionPattern is { } conditionPattern)
            {
                _compileTimeConstantState.PushScope();
                try
                {
                    if (!TryImportedTypedTemplateFieldPatternMatchesConstant(conditionPattern, condition, out var matched))
                    {
                        return false;
                    }

                    if (matched)
                    {
                        return TryExecuteImportedTypedTemplateStatementListConstant(
                            statement.ThenBranch,
                            createScope: true,
                            out flow,
                            out returnValue);
                    }
                }
                finally
                {
                    _compileTimeConstantState.PopScope();
                }

                return TryExecuteImportedTypedTemplateStatementListConstant(
                    statement.ElseBranch,
                    createScope: true,
                    out flow,
                    out returnValue);
            }

            if (condition.Kind != CompileTimeConstantKind.Bool)
            {
                return false;
            }

            return TryExecuteImportedTypedTemplateStatementListConstant(
                condition.BoolValue ? statement.ThenBranch : statement.ElseBranch,
                createScope: true,
                out flow,
                out returnValue);
        }

        private bool TryExecuteImportedTypedTemplateWhileConstant(
            ImportedTemplateTypedBodyStatementSummary statement,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            flow = CompileTimeStatementFlow.None;
            returnValue = default;
            if (statement.Expression is null)
            {
                return false;
            }

            var iterations = 0;
            while (true)
            {
                if (!TryEvaluateImportedTypedTemplateConstant(statement.Expression, out var condition))
                {
                    return false;
                }

                if (statement.ConditionPattern is { } conditionPattern)
                {
                    _compileTimeConstantState.PushScope();
                    try
                    {
                        if (!TryImportedTypedTemplateFieldPatternMatchesConstant(conditionPattern, condition, out var matched))
                        {
                            return false;
                        }

                        if (!matched)
                        {
                            flow = CompileTimeStatementFlow.None;
                            return true;
                        }

                        if (++iterations > _maximumCompileTimeLoopIterations)
                        {
                            return false;
                        }

                        if (!TryExecuteImportedTypedTemplateStatementListConstant(
                                statement.Body,
                                createScope: true,
                                out flow,
                                out returnValue))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        _compileTimeConstantState.PopScope();
                    }
                }
                else
                {
                    if (condition.Kind != CompileTimeConstantKind.Bool)
                    {
                        return false;
                    }

                    if (!condition.BoolValue)
                    {
                        flow = CompileTimeStatementFlow.None;
                        return true;
                    }

                    if (++iterations > _maximumCompileTimeLoopIterations)
                    {
                        return false;
                    }

                    if (!TryExecuteImportedTypedTemplateStatementListConstant(
                            statement.Body,
                            createScope: true,
                            out flow,
                            out returnValue))
                    {
                        return false;
                    }
                }

                if (flow == CompileTimeStatementFlow.Return)
                {
                    return true;
                }

                if (flow == CompileTimeStatementFlow.Break)
                {
                    if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                    {
                        _compileTimeConstantState.ClearPendingControlFlowLabel();
                        flow = CompileTimeStatementFlow.None;
                        return true;
                    }

                    return true;
                }

                if (flow == CompileTimeStatementFlow.Continue)
                {
                    if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                    {
                        _compileTimeConstantState.ClearPendingControlFlowLabel();
                        flow = CompileTimeStatementFlow.None;
                    }
                    else
                    {
                        return true;
                    }
                }
            }
        }

        private bool TryExecuteImportedTypedTemplateForConstant(
            ImportedTemplateTypedBodyStatementSummary statement,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            flow = CompileTimeStatementFlow.None;
            returnValue = default;
            _compileTimeConstantState.PushScope();
            try
            {
                if (!TryExecuteImportedTypedTemplateStatementListConstant(
                        statement.Initializer,
                        createScope: false,
                        out flow,
                        out returnValue)
                    || flow != CompileTimeStatementFlow.None)
                {
                    return flow != CompileTimeStatementFlow.None;
                }

                var iterations = 0;
                while (true)
                {
                    if (statement.Expression is not null)
                    {
                        if (!TryEvaluateImportedTypedTemplateConstant(statement.Expression, out var condition)
                            || condition.Kind != CompileTimeConstantKind.Bool)
                        {
                            return false;
                        }

                        if (!condition.BoolValue)
                        {
                            flow = CompileTimeStatementFlow.None;
                            return true;
                        }
                    }

                    if (++iterations > _maximumCompileTimeLoopIterations)
                    {
                        return false;
                    }

                    if (!TryExecuteImportedTypedTemplateStatementListConstant(
                            statement.Body,
                            createScope: true,
                            out flow,
                            out returnValue))
                    {
                        return false;
                    }

                    if (flow == CompileTimeStatementFlow.Return)
                    {
                        return true;
                    }

                    if (flow == CompileTimeStatementFlow.Break)
                    {
                        if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                        {
                            _compileTimeConstantState.ClearPendingControlFlowLabel();
                            flow = CompileTimeStatementFlow.None;
                            return true;
                        }

                        return true;
                    }

                    if (flow == CompileTimeStatementFlow.Continue)
                    {
                        if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                        {
                            _compileTimeConstantState.ClearPendingControlFlowLabel();
                            flow = CompileTimeStatementFlow.None;
                        }
                        else
                        {
                            return true;
                        }
                    }

                    if (!TryExecuteImportedTypedTemplateStatementListConstant(
                            statement.Iterator,
                            createScope: false,
                            out var iteratorFlow,
                            out returnValue))
                    {
                        return false;
                    }

                    if (iteratorFlow == CompileTimeStatementFlow.Return)
                    {
                        flow = CompileTimeStatementFlow.Return;
                        return true;
                    }

                    if (iteratorFlow == CompileTimeStatementFlow.Break)
                    {
                        if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                        {
                            _compileTimeConstantState.ClearPendingControlFlowLabel();
                            flow = CompileTimeStatementFlow.None;
                            return true;
                        }

                        flow = CompileTimeStatementFlow.Break;
                        return true;
                    }

                    if (iteratorFlow == CompileTimeStatementFlow.Continue)
                    {
                        if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                        {
                            _compileTimeConstantState.ClearPendingControlFlowLabel();
                            flow = CompileTimeStatementFlow.None;
                        }
                        else
                        {
                            flow = CompileTimeStatementFlow.Continue;
                            return true;
                        }
                    }

                    flow = CompileTimeStatementFlow.None;
                }
            }
            finally
            {
                _compileTimeConstantState.PopScope();
            }
        }

        private bool TryExecuteImportedTypedTemplateForTraversalConstant(
            ImportedTemplateTypedBodyStatementSummary statement,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            flow = CompileTimeStatementFlow.None;
            returnValue = default;
            if (statement.TraversalSourceExpression is null
                || string.IsNullOrWhiteSpace(statement.TraversalElementName)
                || statement.TraversalElementType is null
                || !TryEvaluateImportedTypedTemplateConstant(statement.TraversalSourceExpression, out var source)
                || source.Kind != CompileTimeConstantKind.FixedArray)
            {
                return false;
            }

            var elementType = ApplyGenericSubstitution(statement.TraversalElementType);
            var indexType = statement.TraversalIndexType is null
                ? StarkTypeSymbols.Integer(64)
                : ApplyGenericSubstitution(statement.TraversalIndexType);
            var iterations = 0;
            for (var index = 0; index < source.Elements.Count; index++)
            {
                if (++iterations > _maximumCompileTimeLoopIterations)
                {
                    return false;
                }

                _compileTimeConstantState.PushScope();
                try
                {
                    if (statement.TraversalIndexName is { } indexName)
                    {
                        _compileTimeConstantState.Declare(
                            indexName,
                            CompileTimeConstant.Integer(new BigInteger(index), indexType),
                            isMutable: false);
                    }

                    if (!CompileTimeExpressionEvaluator.TryCoerce(source.Elements[index], elementType, out var element))
                    {
                        return false;
                    }

                    _compileTimeConstantState.Declare(statement.TraversalElementName!, element, isMutable: false);

                    if (!TryExecuteImportedTypedTemplateStatementListConstant(
                            statement.Body,
                            createScope: false,
                            out flow,
                            out returnValue))
                    {
                        return false;
                    }
                }
                finally
                {
                    _compileTimeConstantState.PopScope();
                }

                if (flow == CompileTimeStatementFlow.Return)
                {
                    return true;
                }

                if (flow == CompileTimeStatementFlow.Break)
                {
                    if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                    {
                        _compileTimeConstantState.ClearPendingControlFlowLabel();
                        flow = CompileTimeStatementFlow.None;
                        return true;
                    }

                    return true;
                }

                if (flow == CompileTimeStatementFlow.Continue)
                {
                    if (_compileTimeConstantState.ShouldConsumeControlFlow(statement.Name))
                    {
                        _compileTimeConstantState.ClearPendingControlFlowLabel();
                        flow = CompileTimeStatementFlow.None;
                    }
                    else
                    {
                        return true;
                    }
                }

                flow = CompileTimeStatementFlow.None;
            }

            return true;
        }

        private bool TryExecuteImportedTypedTemplateSwitchConstant(
            ImportedTemplateTypedBodyStatementSummary statement,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            flow = CompileTimeStatementFlow.None;
            returnValue = default;
            if (statement.Expression is null
                || !TryEvaluateImportedTypedTemplateConstant(statement.Expression, out var switchValue))
            {
                return false;
            }

            ImportedTemplateTypedSwitchCaseSummary? defaultCase = null;
            foreach (var switchCase in statement.SwitchCases)
            {
                if (switchCase.Kind == ImportedTemplateTypedSwitchCaseKind.Default)
                {
                    defaultCase ??= switchCase;
                    continue;
                }

                if (!TryExecuteImportedTypedTemplateSwitchCaseIfMatchedConstant(
                        switchCase,
                        switchValue,
                        statement.Name,
                        out var matched,
                        out flow,
                        out returnValue))
                {
                    return false;
                }

                if (!matched)
                {
                    continue;
                }

                return true;
            }

            return defaultCase is null
                || TryExecuteImportedTypedTemplateSwitchCaseIfMatchedConstant(
                    defaultCase,
                    switchValue,
                    statement.Name,
                    out _,
                    out flow,
                    out returnValue);
        }

        private bool TryExecuteImportedTypedTemplateSwitchCaseIfMatchedConstant(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            CompileTimeConstant switchValue,
            string? labelName,
            out bool matched,
            out CompileTimeStatementFlow flow,
            out CompileTimeConstant returnValue)
        {
            matched = false;
            flow = CompileTimeStatementFlow.None;
            returnValue = default;

            _compileTimeConstantState.PushScope();
            try
            {
                if (!TryImportedTypedTemplateSwitchCaseMatchesConstant(switchCase, switchValue, out matched))
                {
                    return false;
                }

                if (!matched)
                {
                    return true;
                }

                if (switchCase.GuardExpression is { } guardExpression)
                {
                    if (!TryEvaluateImportedTypedTemplateConstant(guardExpression, out var guard)
                        || guard.Kind != CompileTimeConstantKind.Bool)
                    {
                        return false;
                    }

                    if (!guard.BoolValue)
                    {
                        matched = false;
                        return true;
                    }
                }

                if (!TryExecuteImportedTypedTemplateStatementListConstant(
                        switchCase.Statements,
                        createScope: false,
                        out flow,
                        out returnValue))
                {
                    return false;
                }

                if (flow == CompileTimeStatementFlow.Break)
                {
                    if (_compileTimeConstantState.ShouldConsumeControlFlow(labelName))
                    {
                        _compileTimeConstantState.ClearPendingControlFlowLabel();
                        flow = CompileTimeStatementFlow.None;
                    }
                }

                return true;
            }
            finally
            {
                _compileTimeConstantState.PopScope();
            }
        }

        private bool TryImportedTypedTemplateSwitchCaseMatchesConstant(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
            CompileTimeConstant switchValue,
            out bool matched)
        {
            matched = false;
            switch (switchCase.Kind)
            {
                case ImportedTemplateTypedSwitchCaseKind.MatchAll:
                    if (!TryDeclareImportedTypedTemplatePatternCapture(switchCase.Name, switchValue))
                    {
                        return false;
                    }

                    matched = true;
                    return true;

                case ImportedTemplateTypedSwitchCaseKind.Literal:
                    return switchCase.Expression is not null
                        && TryEvaluateImportedTypedTemplateConstant(switchCase.Expression, out var literal)
                        && TryCompileTimeConstantsEqual(switchValue, literal, out matched);

                case ImportedTemplateTypedSwitchCaseKind.Range:
                    return switchCase.Expression is not null
                        && switchCase.EndExpression is not null
                        && switchValue.Kind == CompileTimeConstantKind.Integer
                        && TryEvaluateImportedTypedTemplateConstant(switchCase.Expression, out var start)
                        && TryEvaluateImportedTypedTemplateConstant(switchCase.EndExpression, out var end)
                        && start.Kind == CompileTimeConstantKind.Integer
                        && end.Kind == CompileTimeConstantKind.Integer
                        && SetMatched(
                            switchValue.IntegerValue >= start.IntegerValue
                            && switchValue.IntegerValue <= end.IntegerValue,
                            out matched);

                case ImportedTemplateTypedSwitchCaseKind.EnumPattern:
                    return switchCase.Ordinal is { } enumOrdinal
                        && TryImportedTypedTemplateEnumPatternMatchesConstant(
                            enumOrdinal,
                            switchCase.Name,
                            switchCase.Members,
                            switchValue,
                            out matched);

                case ImportedTemplateTypedSwitchCaseKind.AggregatePattern:
                    return switchCase.Ordinal is { } aggregateOrdinal
                        && TryImportedTypedTemplateAggregatePatternMatchesConstant(
                            aggregateOrdinal,
                            switchCase.Name,
                            switchCase.Members,
                            switchValue,
                            out matched);

                case ImportedTemplateTypedSwitchCaseKind.ListPattern:
                    return TryImportedTypedTemplateListPatternMatchesConstant(
                        switchCase.Members,
                        switchValue,
                        out matched);

                case ImportedTemplateTypedSwitchCaseKind.Default:
                    matched = true;
                    return true;

                default:
                    return false;
            }

            static bool SetMatched(bool value, out bool matched)
            {
                matched = value;
                return true;
            }
        }

        private bool TryImportedTypedTemplateFieldPatternMatchesConstant(
            ImportedTemplateTypedSwitchFieldPatternSummary pattern,
            CompileTimeConstant value,
            out bool matched)
        {
            matched = false;
            switch (pattern.Kind)
            {
                case ImportedTemplateTypedSwitchFieldPatternKind.Discard:
                    matched = true;
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.Capture:
                    if (!TryDeclareImportedTypedTemplatePatternCapture(pattern.Name, value))
                    {
                        return false;
                    }

                    matched = true;
                    return true;

                case ImportedTemplateTypedSwitchFieldPatternKind.Literal:
                    return pattern.Expression is not null
                        && TryEvaluateImportedTypedTemplateConstant(pattern.Expression, out var literal)
                        && TryCompileTimeConstantsEqual(value, literal, out matched);

                case ImportedTemplateTypedSwitchFieldPatternKind.Range:
                    return pattern.Expression is not null
                        && pattern.EndExpression is not null
                        && value.Kind == CompileTimeConstantKind.Integer
                        && TryEvaluateImportedTypedTemplateConstant(pattern.Expression, out var start)
                        && TryEvaluateImportedTypedTemplateConstant(pattern.EndExpression, out var end)
                        && start.Kind == CompileTimeConstantKind.Integer
                        && end.Kind == CompileTimeConstantKind.Integer
                        && SetMatched(
                            value.IntegerValue >= start.IntegerValue
                            && value.IntegerValue <= end.IntegerValue,
                            out matched);

                case ImportedTemplateTypedSwitchFieldPatternKind.EnumPattern:
                    return pattern.Ordinal is { } enumOrdinal
                        && TryImportedTypedTemplateEnumPatternMatchesConstant(
                            enumOrdinal,
                            pattern.Name,
                            pattern.Members,
                            value,
                            out matched);

                case ImportedTemplateTypedSwitchFieldPatternKind.AggregatePattern:
                    return pattern.Ordinal is { } aggregateOrdinal
                        && TryImportedTypedTemplateAggregatePatternMatchesConstant(
                            aggregateOrdinal,
                            pattern.Name,
                            pattern.Members,
                            value,
                            out matched);

                case ImportedTemplateTypedSwitchFieldPatternKind.ListPattern:
                    return TryImportedTypedTemplateListPatternMatchesConstant(pattern.Members, value, out matched);

                default:
                    return false;
            }

            static bool SetMatched(bool value, out bool matched)
            {
                matched = value;
                return true;
            }
        }

        private bool TryImportedTypedTemplateEnumPatternMatchesConstant(
            int ordinal,
            string? wholeCaptureName,
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            CompileTimeConstant value,
            out bool matched)
        {
            matched = false;
            if (!CurrentImportedTemplateEnumPatterns.TryGetValue(ordinal, out var publishedPattern))
            {
                return false;
            }

            var enumType = ApplyGenericSubstitution(publishedPattern.EnumType);
            if (value.Kind != CompileTimeConstantKind.EnumAggregate
                || value.VariantName is not { } variantName
                || !string.Equals(variantName, publishedPattern.VariantName, StringComparison.Ordinal)
                || !ImportedTypedTemplateConstantTypeMatches(value.Type, enumType))
            {
                return true;
            }

            if (wholeCaptureName is not null)
            {
                if (memberPatterns.Count != 0
                    || !TryDeclareImportedTypedTemplatePatternCapture(wholeCaptureName, value))
                {
                    return false;
                }

                matched = true;
                return true;
            }

            if (publishedPattern.Members.Count > 0)
            {
                if (memberPatterns.Count != publishedPattern.Members.Count)
                {
                    return false;
                }

                for (var memberIndex = 0; memberIndex < memberPatterns.Count; memberIndex++)
                {
                    var publishedMember = publishedPattern.Members[memberIndex];
                    if (publishedMember.FieldIndex < 0
                        || publishedMember.FieldIndex >= value.Elements.Count
                        || !TryImportedTypedTemplateFieldPatternMatchesConstant(
                            memberPatterns[memberIndex],
                            value.Elements[publishedMember.FieldIndex],
                            out var memberMatched))
                    {
                        return false;
                    }

                    if (!memberMatched)
                    {
                        return true;
                    }
                }

                matched = true;
                return true;
            }

            if (memberPatterns.Count != value.Elements.Count)
            {
                return true;
            }

            for (var memberIndex = 0; memberIndex < memberPatterns.Count; memberIndex++)
            {
                if (!TryImportedTypedTemplateFieldPatternMatchesConstant(
                        memberPatterns[memberIndex],
                        value.Elements[memberIndex],
                        out var memberMatched))
                {
                    return false;
                }

                if (!memberMatched)
                {
                    return true;
                }
            }

            matched = true;
            return true;
        }

        private bool TryImportedTypedTemplateAggregatePatternMatchesConstant(
            int ordinal,
            string? wholeCaptureName,
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            CompileTimeConstant value,
            out bool matched)
        {
            matched = false;
            if (!CurrentImportedTemplateAggregatePatterns.TryGetValue(ordinal, out var publishedPattern))
            {
                return false;
            }

            var aggregateType = ApplyGenericSubstitution(publishedPattern.Type);
            if (value.Kind != CompileTimeConstantKind.NamedAggregate
                || !ImportedTypedTemplateConstantTypeMatches(value.Type, aggregateType))
            {
                return true;
            }

            if (wholeCaptureName is not null)
            {
                if (memberPatterns.Count != 0
                    || !TryDeclareImportedTypedTemplatePatternCapture(wholeCaptureName, value))
                {
                    return false;
                }

                matched = true;
                return true;
            }

            if (publishedPattern.Members.Count > 0)
            {
                if (memberPatterns.Count != publishedPattern.Members.Count)
                {
                    return false;
                }

                for (var memberIndex = 0; memberIndex < memberPatterns.Count; memberIndex++)
                {
                    var publishedMember = publishedPattern.Members[memberIndex];
                    if (publishedMember.FieldIndex < 0
                        || publishedMember.FieldIndex >= value.Elements.Count
                        || !TryImportedTypedTemplateFieldPatternMatchesConstant(
                            memberPatterns[memberIndex],
                            value.Elements[publishedMember.FieldIndex],
                            out var memberMatched))
                    {
                        return false;
                    }

                    if (!memberMatched)
                    {
                        return true;
                    }
                }

                matched = true;
                return true;
            }

            if (memberPatterns.Count != value.Elements.Count)
            {
                return true;
            }

            for (var memberIndex = 0; memberIndex < memberPatterns.Count; memberIndex++)
            {
                if (!TryImportedTypedTemplateFieldPatternMatchesConstant(
                        memberPatterns[memberIndex],
                        value.Elements[memberIndex],
                        out var memberMatched))
                {
                    return false;
                }

                if (!memberMatched)
                {
                    return true;
                }
            }

            matched = true;
            return true;
        }

        private bool TryImportedTypedTemplateListPatternMatchesConstant(
            IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> memberPatterns,
            CompileTimeConstant value,
            out bool matched)
        {
            matched = false;
            if (value.Kind != CompileTimeConstantKind.FixedArray
                || value.Elements.Count != memberPatterns.Count)
            {
                return true;
            }

            for (var memberIndex = 0; memberIndex < memberPatterns.Count; memberIndex++)
            {
                if (!TryImportedTypedTemplateFieldPatternMatchesConstant(
                        memberPatterns[memberIndex],
                        value.Elements[memberIndex],
                        out var memberMatched))
                {
                    return false;
                }

                if (!memberMatched)
                {
                    return true;
                }
            }

            matched = true;
            return true;
        }

        private bool TryDeclareImportedTypedTemplatePatternCapture(string? name, CompileTimeConstant value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            _compileTimeConstantState.Declare(name!, value, isMutable: false);
            return true;
        }

        private static bool ImportedTypedTemplateConstantTypeMatches(
            StarkTypeSymbol actualType,
            StarkTypeSymbol expectedType)
        {
            var actual = UnqualifiedImportedTypedTemplatePatternType(actualType);
            var expected = UnqualifiedImportedTypedTemplatePatternType(expectedType);

            if (actual.Kind != expected.Kind)
            {
                return false;
            }

            return actual.Kind switch
            {
                StarkTypeKind.Named =>
                    string.Equals(actual.NamedType, expected.NamedType, StringComparison.Ordinal)
                    || string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal),
                StarkTypeKind.FixedArray =>
                    actual.FixedLength == expected.FixedLength
                    && actual.ElementType is not null
                    && expected.ElementType is not null
                    && ImportedTypedTemplateConstantTypeMatches(actual.ElementType, expected.ElementType),
                _ => string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal)
            };
        }

        private static StarkTypeSymbol UnqualifiedImportedTypedTemplatePatternType(StarkTypeSymbol type) =>
            StarkTypeSymbols.WithQualifiers(
                type,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);

        private static bool TryCompileTimeConstantsEqual(
            CompileTimeConstant left,
            CompileTimeConstant right,
            out bool equal)
        {
            equal = false;
            if (left.Kind != right.Kind)
            {
                return true;
            }

            switch (left.Kind)
            {
                case CompileTimeConstantKind.Integer:
                    equal = left.IntegerValue == right.IntegerValue;
                    return true;
                case CompileTimeConstantKind.Float:
                    equal = left.FloatValue.Equals(right.FloatValue);
                    return true;
                case CompileTimeConstantKind.Bool:
                    equal = left.BoolValue == right.BoolValue;
                    return true;
                case CompileTimeConstantKind.Text:
                    equal = string.Equals(left.TextLiteral, right.TextLiteral, StringComparison.Ordinal);
                    return true;
                case CompileTimeConstantKind.Null:
                case CompileTimeConstantKind.Void:
                    equal = true;
                    return true;
                case CompileTimeConstantKind.EnumAggregate:
                    equal = string.Equals(left.VariantName, right.VariantName, StringComparison.Ordinal)
                        && left.Elements.Count == right.Elements.Count;
                    if (!equal)
                    {
                        return true;
                    }

                    for (var index = 0; index < left.Elements.Count; index++)
                    {
                        if (!TryCompileTimeConstantsEqual(left.Elements[index], right.Elements[index], out var elementEqual)
                            || !elementEqual)
                        {
                            equal = false;
                            return true;
                        }
                    }

                    return true;
                default:
                    return false;
            }
        }

        private enum ImportedTemplateCompileTimePlaceSegmentKind
        {
            Field,
            Index
        }

        private readonly record struct ImportedTemplateCompileTimePlaceSegment(
            ImportedTemplateCompileTimePlaceSegmentKind Kind,
            int Ordinal)
        {
            public static ImportedTemplateCompileTimePlaceSegment Field(int fieldIndex) =>
                new(ImportedTemplateCompileTimePlaceSegmentKind.Field, fieldIndex);

            public static ImportedTemplateCompileTimePlaceSegment Index(int elementIndex) =>
                new(ImportedTemplateCompileTimePlaceSegmentKind.Index, elementIndex);
        }

        private bool BindImportedTypedTemplateTypeArguments(
            TypedFunctionSignature templateSignature,
            TypedFunctionSignature concreteSignature,
            IDictionary<string, StarkTypeSymbol> substitution)
        {
            var typeArguments = concreteSignature.TypeArguments ?? [];
            if (templateSignature.GenericParams.Count != typeArguments.Count)
            {
                return templateSignature.GenericParams.Count == 0 && typeArguments.Count == 0;
            }

            for (var index = 0; index < templateSignature.GenericParams.Count; index++)
            {
                substitution[templateSignature.GenericParams[index]] = ApplyGenericSubstitution(typeArguments[index]);
            }

            return true;
        }

        private bool BindImportedTypedTemplateComptimeArguments(
            TypedFunctionSignature templateSignature,
            TypedFunctionSignature concreteSignature,
            IDictionary<string, BigInteger> substitution)
        {
            var valueArguments = concreteSignature.ComptimeValueArguments ?? [];
            if (templateSignature.ComptimeGenericParams.Count != valueArguments.Count)
            {
                return templateSignature.ComptimeGenericParams.Count == 0 && valueArguments.Count == 0;
            }

            for (var index = 0; index < templateSignature.ComptimeGenericParams.Count; index++)
            {
                var argument = valueArguments[index];
                if (argument.IsSymbolic)
                {
                    if (_activeGenericValueSubstitution?.TryGetValue(argument.SourceName, out var resolvedValue) != true)
                    {
                        return false;
                    }

                    substitution[templateSignature.ComptimeGenericParams[index].Name] = resolvedValue;
                    continue;
                }

                substitution[templateSignature.ComptimeGenericParams[index].Name] = argument.IntegerValue;
            }

            return true;
        }

        private void DeclareImportedTypedTemplateComptimeConstants(
            TypedFunctionSignature templateSignature,
            IReadOnlyDictionary<string, BigInteger> valueSubstitution)
        {
            foreach (var parameter in templateSignature.ComptimeGenericParams)
            {
                if (!valueSubstitution.TryGetValue(parameter.Name, out var value))
                {
                    continue;
                }

                _compileTimeConstantState.Declare(
                    parameter.Name,
                    CompileTimeConstant.Integer(value, ApplyGenericSubstitution(parameter.Type)),
                    isMutable: false);
            }
        }

        private bool TryEvaluateImportedTypedTemplateLiteralConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.LiteralText is not { } literalText
                || expression.Type is not { } literalType)
            {
                return false;
            }

            var type = ApplyGenericSubstitution(literalType);
            if (type.Kind == StarkTypeKind.Bool)
            {
                if (string.Equals(literalText, "true", StringComparison.Ordinal))
                {
                    constant = CompileTimeConstant.Bool(true);
                    return true;
                }

                if (string.Equals(literalText, "false", StringComparison.Ordinal))
                {
                    constant = CompileTimeConstant.Bool(false);
                    return true;
                }
            }

            if (type.Kind == StarkTypeKind.RawPointer
                && string.Equals(literalText, "null", StringComparison.Ordinal))
            {
                constant = CompileTimeConstant.Null(type);
                return true;
            }

            if (type.Kind == StarkTypeKind.Float
                && double.TryParse(
                    CompileTimeExpressionEvaluator.StripFloatSuffix(literalText),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out var floatValue))
            {
                constant = CompileTimeConstant.Float(floatValue, type);
                return true;
            }

            if (type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                if (TryFoldImportedTypedTemplateInterpolatedTextLiteral(literalText, type, out var foldedLiteral))
                {
                    constant = CompileTimeConstant.Text(foldedLiteral, type);
                    return true;
                }

                constant = CompileTimeConstant.Text(literalText, type);
                return true;
            }

            if (type.Kind == StarkTypeKind.Integer)
            {
                if (BigInteger.TryParse(literalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    constant = CompileTimeConstant.Integer(value, type);
                    return true;
                }

                return false;
            }

            return false;
        }

        private bool TryFoldImportedTypedTemplateInterpolatedTextLiteral(
            string literalText,
            StarkTypeSymbol type,
            out string foldedLiteral)
        {
            foldedLiteral = string.Empty;
            if (type.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
                || !TryGetImportedTypedTemplateInterpolatedStringLiteralText(literalText, out var stringLiteralText))
            {
                return false;
            }

            return InterpolatedText.TryFold(
                stringLiteralText,
                CreateCompileTimeEvaluationServices(),
                out foldedLiteral,
                out _);
        }

        private static bool TryGetImportedTypedTemplateInterpolatedStringLiteralText(
            string literalText,
            out string stringLiteralText)
        {
            if (literalText.Length > 1 && literalText[0] == '$')
            {
                stringLiteralText = literalText[1..];
                return true;
            }

            stringLiteralText = string.Empty;
            return false;
        }

        private bool TryEvaluateImportedTypedTemplateConversionConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            return expression.Type is { } targetType
                && expression.Args.Count == 1
                && TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var operand)
                && CompileTimeExpressionEvaluator.TryExplicitConvert(operand, ApplyGenericSubstitution(targetType), out constant);
        }

        private bool TryEvaluateImportedTypedTemplateUnaryConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Name is not { } operatorText
                || expression.Args.Count != 1
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var operand))
            {
                return false;
            }

            return operatorText switch
            {
                "+" => CopyCompileTimeConstant(operand, out constant),
                "-" when operand.Kind == CompileTimeConstantKind.Integer =>
                    CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                        "-",
                        CompileTimeConstant.Integer(BigInteger.Zero, operand.Type),
                        operand,
                        requireInteger: false,
                        out constant),
                "-%" when operand.Kind == CompileTimeConstantKind.Integer =>
                    CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                        "-%",
                        CompileTimeConstant.Integer(BigInteger.Zero, operand.Type),
                        operand,
                        requireInteger: false,
                        out constant),
                "!" when operand.Kind == CompileTimeConstantKind.Bool =>
                    TryBuildBoolConstant(!operand.BoolValue, out constant),
                "~" when operand.Kind == CompileTimeConstantKind.Integer =>
                    TryBuildIntegerConstant(~operand.IntegerValue, operand.Type, out constant),
                _ => false
            };
        }

        private static bool CopyCompileTimeConstant(CompileTimeConstant source, out CompileTimeConstant target)
        {
            target = source;
            return true;
        }

        private static bool TryBuildBoolConstant(bool value, out CompileTimeConstant constant)
        {
            constant = CompileTimeConstant.Bool(value);
            return true;
        }

        private static bool TryBuildIntegerConstant(
            BigInteger value,
            StarkTypeSymbol type,
            out CompileTimeConstant constant)
        {
            constant = CompileTimeConstant.Integer(value, type);
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateBinaryConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Name is not { } operatorText
                || expression.Args.Count != 2
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var left)
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[1], out var right))
            {
                return false;
            }

            var requireInteger = operatorText is "&" or "^" or "|" or "<<" or ">>";
            return CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                operatorText,
                left,
                right,
                requireInteger,
                out constant);
        }

        private bool TryEvaluateImportedTypedTemplateComparisonChainConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Args.Count < 2
                || expression.Operators.Count != expression.Args.Count - 1
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var left))
            {
                return false;
            }

            for (var index = 0; index < expression.Operators.Count; index++)
            {
                if (!TryEvaluateImportedTypedTemplateConstant(expression.Args[index + 1], out var right)
                    || !CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                        expression.Operators[index],
                        left,
                        right,
                        requireInteger: false,
                        out var comparison)
                    || comparison.Kind != CompileTimeConstantKind.Bool)
                {
                    return false;
                }

                if (!comparison.BoolValue)
                {
                    constant = CompileTimeConstant.Bool(false);
                    return true;
                }

                left = right;
            }

            constant = CompileTimeConstant.Bool(true);
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateConditionalConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            return expression.Args.Count == 3
                && TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var condition)
                && condition.Kind == CompileTimeConstantKind.Bool
                && TryEvaluateImportedTypedTemplateConstant(
                    condition.BoolValue ? expression.Args[1] : expression.Args[2],
                    out constant);
        }

        private bool TryEvaluateImportedTypedTemplateTypeLayoutConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Type is null || expression.Name is null)
            {
                return false;
            }

            var targetType = ApplyGenericSubstitution(expression.Type);
            var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                targetType,
                _namedTypes,
                _enumLayoutModel.Layouts,
                _publishedConcreteLayouts);
            if (layout is null)
            {
                return false;
            }

            var kind = string.Equals(expression.Name, "alignof", StringComparison.Ordinal)
                ? BoundLayoutQueryKind.AlignOf
                : BoundLayoutQueryKind.SizeOf;
            constant = CompileTimeConstant.Integer(
                TypeLayoutQueryFacts.GetResultValue(kind, layout),
                TypeLayoutQueryFacts.GetResultType(kind));
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateArrayConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Type is not { } arrayType)
            {
                return false;
            }

            var targetType = ApplyGenericSubstitution(arrayType);
            if (targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is not { } elementType
                || targetType.FixedLength is not int fixedLength
                || expression.Args.Count > fixedLength)
            {
                return false;
            }

            var elements = new CompileTimeConstant[fixedLength];
            for (var index = 0; index < expression.Args.Count; index++)
            {
                if (!TryEvaluateImportedTypedTemplateConstant(expression.Args[index], out var element)
                    || !TryCoerceImportedTypedTemplateArgument(element, elementType, out elements[index]))
                {
                    return false;
                }
            }

            for (var index = expression.Args.Count; index < fixedLength; index++)
            {
                if (!TryCreateZeroCompileTimeConstant(elementType, out elements[index]))
                {
                    return false;
                }
            }

            constant = CompileTimeConstant.FixedArray(elements, targetType);
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateObjectInitializerConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Type is not { } publishedType
                || expression.Members.Count != expression.Args.Count)
            {
                return false;
            }

            var targetType = ApplyGenericSubstitution(publishedType);
            if (!TryBuildImportedTypedTemplateObjectInitializerMembers(targetType, expression, out var initializerMembers)
                || !TryCreateZeroCompileTimeConstant(targetType, out var seed))
            {
                return false;
            }

            return TryEvaluateImportedTypedTemplateObjectInitializerConstant(
                seed,
                initializerMembers,
                expression.Args,
                out constant);
        }

        private bool TryEvaluateImportedTypedTemplateObjectCreationConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Ordinal is not { } ordinal
                || CurrentImportedTemplateEvaluationSummary is not { ObjectCreations.Count: > 0 } importedTemplateSummary
                || ordinal < 0
                || ordinal >= importedTemplateSummary.ObjectCreations.Count)
            {
                return false;
            }

            var objectCreation = importedTemplateSummary.ObjectCreations[ordinal];
            var createdType = ApplyGenericSubstitution(objectCreation.CreatedType);
            if (!TryCreateZeroCompileTimeConstant(createdType, out var current))
            {
                return false;
            }

            return TryEvaluateImportedTypedTemplateObjectInitializerConstant(
                current,
                objectCreation.InitializerMembers,
                expression.Args,
                out constant);
        }

        private bool TryEvaluateImportedTypedTemplateObjectInitializerConstant(
            CompileTimeConstant seed,
            IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary> initializerMembers,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (initializerMembers.Count != arguments.Count
                || seed.Kind != CompileTimeConstantKind.NamedAggregate)
            {
                return false;
            }

            var current = seed;
            for (var index = 0; index < initializerMembers.Count; index++)
            {
                var member = initializerMembers[index];
                var fieldType = ApplyGenericSubstitution(member.FieldType);
                if (!TryEvaluateImportedTypedTemplateConstant(arguments[index], out var memberValue)
                    || !TryCoerceImportedTypedTemplateArgument(memberValue, fieldType, out var coerced)
                    || !TryWithCompileTimeNamedAggregateField(current, member.FieldIndex, coerced, out current))
                {
                    return false;
                }
            }

            constant = current;
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateEnumConstructorConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            return expression.Ordinal is { } ordinal
                && CurrentImportedTemplateEnumConstructors.TryGetValue(ordinal, out var publishedEnumConstructor)
                && TryEvaluateImportedTypedTemplateEnumAggregateConstant(
                    ApplyGenericSubstitution(publishedEnumConstructor.EnumType),
                    publishedEnumConstructor.VariantName,
                    publishedEnumConstructor.Members,
                    expression.Args,
                    out constant);
        }

        private bool TryEvaluateImportedTypedTemplateEnumCallConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Ordinal is not { } ordinal
                || !CurrentImportedTemplateEnumCalls.TryGetValue(ordinal, out var publishedEnumCall)
                || !TryGetEnumLayout(ApplyGenericSubstitution(publishedEnumCall.EnumType), out var layout)
                || !layout.TryGetVariant(publishedEnumCall.VariantName, out var variant))
            {
                return false;
            }

            var members = variant.Fields
                .Select((field, index) => new ImportedTemplateEnumConstructorMemberSummary(
                    field.SourceFieldName ?? field.StorageFieldName,
                    index,
                    field.Type))
                .ToArray();
            return TryEvaluateImportedTypedTemplateEnumAggregateConstant(
                ApplyGenericSubstitution(publishedEnumCall.EnumType),
                publishedEnumCall.VariantName,
                members,
                expression.Args,
                out constant);
        }

        private bool TryEvaluateImportedTypedTemplateEnumAggregateConstant(
            StarkTypeSymbol enumType,
            string variantName,
            IReadOnlyList<ImportedTemplateEnumConstructorMemberSummary> members,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (members.Count != arguments.Count)
            {
                return false;
            }

            var ordered = new CompileTimeConstant[members.Count];
            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];
                var fieldType = ApplyGenericSubstitution(member.FieldType);
                if (!TryEvaluateImportedTypedTemplateConstant(arguments[index], out var argument)
                    || !TryCoerceImportedTypedTemplateArgument(argument, fieldType, out ordered[member.FieldIndex]))
                {
                    return false;
                }
            }

            constant = CompileTimeConstant.EnumAggregate(variantName, ordered, enumType);
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateEnumValueConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Ordinal is not { } ordinal
                || !CurrentImportedTemplateEnumValues.TryGetValue(ordinal, out var publishedEnumValue))
            {
                return false;
            }

            constant = CompileTimeConstant.EnumAggregate(
                publishedEnumValue.VariantName,
                [],
                ApplyGenericSubstitution(publishedEnumValue.EnumType));
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateIndexConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Args.Count != 2
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var source)
                || source.Kind != CompileTimeConstantKind.FixedArray
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[1], out var indexConstant)
                || indexConstant.Kind != CompileTimeConstantKind.Integer
                || indexConstant.IntegerValue < 0
                || indexConstant.IntegerValue > int.MaxValue
                || indexConstant.IntegerValue >= source.Elements.Count)
            {
                return false;
            }

            constant = source.Elements[(int)indexConstant.IntegerValue];
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateFieldConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count != 1
                || !CurrentImportedTemplateFieldAccesses.TryGetValue(ordinal, out var publishedFieldAccess)
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var receiver)
                || receiver.Kind != CompileTimeConstantKind.NamedAggregate
                || publishedFieldAccess.FieldIndex < 0
                || publishedFieldAccess.FieldIndex >= receiver.Elements.Count)
            {
                return false;
            }

            constant = receiver.Elements[publishedFieldAccess.FieldIndex];
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateTextInterpolationConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.LiteralText is not { } literalText
                || !InterpolatedText.TryParse(literalText, out var segments, out _)
                || segments.OfType<InterpolatedTextHoleSegment>().Count() != expression.Args.Count)
            {
                return false;
            }

            var builder = new StringBuilder();
            var argumentIndex = 0;
            foreach (var segment in segments)
            {
                if (segment is InterpolatedTextRawSegment raw)
                {
                    builder.Append(raw.Value);
                    continue;
                }

                if (!TryEvaluateImportedTypedTemplateConstant(expression.Args[argumentIndex], out var argument)
                    || !InterpolatedText.TryAppendFormattedConstant(builder, argument))
                {
                    return false;
                }

                argumentIndex++;
            }

            var foldedLiteral = TextLiteralDecoder.EncodeStringLiteral(builder.ToString());
            constant = CompileTimeConstant.Text(foldedLiteral, InferImportedTypedTemplateTextLiteralType(foldedLiteral));
            return true;
        }

        private bool TryEvaluateImportedTypedTemplateTextBuildConstant(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out CompileTimeConstant constant)
        {
            constant = default;
            if (expression.Args.Count == 0
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var current))
            {
                return false;
            }

            for (var index = 1; index < expression.Args.Count; index++)
            {
                if (!TryEvaluateImportedTypedTemplateConstant(expression.Args[index], out var next)
                    || !CompileTimeExpressionEvaluator.TryEvaluateBinaryOperator(
                        "+",
                        current,
                        next,
                        requireInteger: false,
                        out current))
                {
                    return false;
                }
            }

            constant = current;
            return constant.Kind == CompileTimeConstantKind.Text;
        }

        private static StarkTypeSymbol InferImportedTypedTemplateTextLiteralType(string literalText)
        {
            return TextLiteralDecoder.CanUseUtf8Storage(literalText, TextLiteralKind.String)
                ? StarkTypeSymbols.Ascii
                : StarkTypeSymbols.Unicode;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateIndexAccess(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count < 1)
            {
                return null;
            }

            var target = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            if (target is null)
            {
                return null;
            }

            if (target.Type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            {
                if (expression.Args.Count == 1)
                {
                    return target;
                }

                if (expression.Args.Count == 2)
                {
                    var start = LowerImportedTypedTemplateExpressionCore(expression.Args[1], expectedType: null);
                    if (start is null || start.Type.Kind != StarkTypeKind.Integer)
                    {
                        return null;
                    }

                    return LowerTextSlice(
                        target,
                        start,
                        new MidLevelIrIntegerConstantOperand(BigInteger.One, StarkTypeSymbols.Integer(64)),
                        $"{target.Text}[{RenderImportedTypedTemplateExpressionCore(expression.Args[1])}]");
                }

                if (expression.Args.Count != 3)
                {
                    throw LoweringInvariantViolation(
                        null,
                        "Imported text indexing requires full-view, single-index, or start-and-length access.");
                }

                var sliceStart = LowerImportedTypedTemplateExpressionCore(expression.Args[1], expectedType: null);
                var sliceLength = LowerImportedTypedTemplateExpressionCore(expression.Args[2], expectedType: null);
                if (sliceStart is null
                    || sliceLength is null
                    || sliceStart.Type.Kind != StarkTypeKind.Integer
                    || sliceLength.Type.Kind != StarkTypeKind.Integer)
                {
                    return null;
                }

                return LowerTextSlice(
                    target,
                    sliceStart,
                    sliceLength,
                    $"{target.Text}[{RenderImportedTypedTemplateExpressionCore(expression.Args[1])}, {RenderImportedTypedTemplateExpressionCore(expression.Args[2])}]");
            }

            if (target.Type.Kind == StarkTypeKind.Dynamic && target.Type.ElementType is not null)
            {
                return LowerImportedTypedTemplateDynamicStorageAccess(target, expression);
            }

            var current = target;
            for (var argumentIndex = 1; argumentIndex < expression.Args.Count; argumentIndex++)
            {
                var index = LowerImportedTypedTemplateExpressionCore(expression.Args[argumentIndex], expectedType: null);
                if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                {
                    return null;
                }

                if (current.Type.Kind == StarkTypeKind.FixedArray && current.Type.ElementType is not null)
                {
                    if (TryResolveImportedTypedTemplateConstantIndex(index, out var constantIndex))
                    {
                        var elementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                        var extracted = EmitTemporary(
                            new MidLevelIrExtractIndexRValue(
                                current,
                                constantIndex,
                                IndexedElementOperationFamily.FixedArrayElement,
                                elementType,
                                $"{current.Text}[{constantIndex}]"),
                            "index");
                        if (extracted is null)
                        {
                            return null;
                        }

                        current = extracted;
                        continue;
                    }

                    var projectedElementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                    var baseAddress = TryCreateDynamicFixedArrayBaseAddress(current);
                    if (baseAddress is null)
                    {
                        throw LoweringInvariantViolation(
                            null,
                            "Dynamic fixed-array indexing from imported typed template bodies requires an addressable fixed-array source.");
                    }

                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            baseAddress,
                            current.Type,
                            index,
                            ConstantIndex: null,
                            AddressType(projectedElementType, isMutable: CanMutateThroughType(current.Type)),
                            $"{current.Text}[{index.Text}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            projectedElementType,
                            $"{current.Text}[{index.Text}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.Slice && current.Type.ElementType is not null)
                {
                    var elementType = ProjectFrozenView(current.Type, current.Type.ElementType);
                    var elementAddress = EmitTemporary(
                        new MidLevelIrSliceElementAddressRValue(
                            current,
                            index,
                            AddressType(elementType, current.Type.IsMutableView && CanMutateThroughType(current.Type)),
                            $"{current.Text}[{index.Text}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            elementType,
                            $"{current.Text}[{index.Text}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                if (current.Type.Kind == StarkTypeKind.RawPointer && current.Type.ElementType is not null)
                {
                    var elementType = current.Type.ElementType;
                    var elementAddress = EmitTemporary(
                        new MidLevelIrElementAddressRValue(
                            current,
                            elementType,
                            index,
                            ConstantIndex: null,
                            AddressType(elementType, current.Type.IsMutablePointer && CanMutateThroughType(elementType)),
                            $"{current.Text}[{index.Text}]"),
                        "addr");
                    if (elementAddress is null)
                    {
                        return null;
                    }

                    var loaded = EmitTemporary(
                        new MidLevelIrLoadIndirectRValue(
                            elementAddress,
                            elementType,
                            $"{current.Text}[{index.Text}]"),
                        "load");
                    if (loaded is null)
                    {
                        return null;
                    }

                    current = loaded;
                    continue;
                }

                throw LoweringInvariantViolation(
                    null,
                    "Imported typed-template indexing is only supported for fixed arrays, raw pointers, slices, ascii, unicode, and dynamic storage values.");
            }

            return current;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateDynamicStorageAccess(
            MidLevelIrOperand target,
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count is not (2 or 3)
                || target.Type.ElementType is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported dynamic storage indexing requires one integer index or a start/count pair.");
            }

            var start = LowerImportedTypedTemplateExpressionCore(expression.Args[1], expectedType: null);
            if (start is null || start.Type.Kind != StarkTypeKind.Integer)
            {
                return null;
            }

            var dataPointerType = StarkTypeSymbols.RawPointer(target.Type.ElementType, isMutable: true);
            var dataPointer = LowerKnownFieldAccess(target, "Data", 0, dataPointerType, "Data");
            var elementType = UsesFrozenProjectionSemantics(target)
                ? StarkTypeSymbols.FreezeReachableView(target.Type.ElementType)
                : ProjectFrozenView(target.Type, target.Type.ElementType);
            var startText = RenderImportedTypedTemplateExpressionCore(expression.Args[1]);
            var elementAddress = EmitTemporary(
                new MidLevelIrElementAddressRValue(
                    dataPointer,
                    elementType,
                    start,
                    ConstantIndex: null,
                    AddressType(elementType, dataPointer.Type.IsMutablePointer && CanMutateThroughType(elementType)),
                    $"{target.Text}[{startText}]"),
                "addr");
            if (elementAddress is null)
            {
                return null;
            }

            if (expression.Args.Count == 3)
            {
                var length = LowerImportedTypedTemplateExpressionCore(expression.Args[2], expectedType: null);
                if (length is null || length.Type.Kind != StarkTypeKind.Integer)
                {
                    return null;
                }

                var sliceType = StarkTypeSymbols.ApplyQualifiers(
                    StarkTypeSymbols.Slice(elementType),
                    isMutableView: dataPointer.Type.IsMutablePointer && CanMutateThroughType(elementType));
                return EmitTemporary(
                    new MidLevelIrMakeSliceFromPointerRValue(
                        elementAddress,
                        length,
                        sliceType,
                        $"{target.Text}[{startText}, {RenderImportedTypedTemplateExpressionCore(expression.Args[2])}]"),
                    "slice");
            }

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    elementAddress,
                    elementType,
                    $"{target.Text}[{startText}]"),
                "load");
        }

        private static bool TryResolveImportedTypedTemplateConstantIndex(
            MidLevelIrOperand operand,
            out int constantIndex)
        {
            constantIndex = 0;

            if (operand is not MidLevelIrIntegerConstantOperand integerConstant
                || integerConstant.Value < 0
                || integerConstant.Value > int.MaxValue)
            {
                return false;
            }

            constantIndex = (int)integerConstant.Value;
            return true;
        }

        private MidLevelIrOperand? TryCreateDynamicFixedArrayBaseAddress(MidLevelIrOperand source)
        {
            if (source.Type.Kind != StarkTypeKind.FixedArray)
            {
                return null;
            }

            var directAddress = source switch
            {
                MidLevelIrLocalOperand local => CreateAddressOfLocal(local.Name, local.Type),
                MidLevelIrParameterOperand parameter => CreateAddressOfParameter(parameter.Name, parameter.Type),
                MidLevelIrGlobalOperand global => CreateAddressOfGlobal(global.Name, global.Type),
                _ => null
            };
            if (directAddress is not null)
            {
                return directAddress;
            }

            // Spill non-addressable fixed-array temporaries so dynamic indexing can still
            // lower through address-based element access.
            var spilled = EmitTemporary(new MidLevelIrUseRValue(source), "indexbase");
            return spilled is MidLevelIrLocalOperand localSpill
                ? CreateAddressOfLocal(localSpill.Name, localSpill.Type)
                : null;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateLiteral(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.LiteralText is null || expression.Type is null)
            {
                return null;
            }

            var type = ApplyGenericSubstitution(expression.Type);
            if (TryFoldImportedTypedTemplateInterpolatedTextLiteral(expression.LiteralText, type, out var foldedLiteral))
            {
                return new MidLevelIrStringConstantOperand(foldedLiteral, type);
            }

            return CreateLiteralOperand(expression.LiteralText, type);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateConversion(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Type is not { } publishedType
                || expression.Args.Count != 1)
            {
                return null;
            }

            var targetType = ApplyGenericSubstitution(publishedType);
            var operand = LowerImportedTypedTemplateExpressionCore(expression.Args[0], targetType);
            if (operand is null)
            {
                return null;
            }

            var converted = CoerceOperand(operand, targetType);
            return expectedType is null ? converted : CoerceOperand(converted, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateTryPropagation(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count != 1
                || !_importedTemplateTryPropagations.TryGetValue(ordinal, out var summary))
            {
                return null;
            }

            var operand = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            if (operand is null)
            {
                return null;
            }

            var record = new TryPropagationTypingRecord(
                new SourceLocation(_moduleFilePath, 0, 0),
                summary.OperandType,
                summary.OperandOkVariantName,
                summary.OperandErrVariantName,
                summary.SuccessPayloadType,
                summary.OperandFailurePayloadType,
                summary.ReturnType,
                summary.EnclosingErrVariantName,
                summary.EnclosingFailurePayloadType,
                summary.ConversionFunnelVariant,
                _function.Name);
            var result = LowerTryPropagationCore(null, record, operand);
            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateUnary(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Name is not { } operatorText
                || expression.Args.Count != 1)
            {
                return null;
            }

            var text = RenderImportedTypedTemplateExpressionCore(expression);
            if (operatorText == "&")
            {
                var address = LowerImportedTypedTemplateAddressOfUnary(expression.Args[0]);
                return expectedType is null ? address : CoerceOperand(address, expectedType);
            }

            var operand = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            if (operand is null)
            {
                return null;
            }

            MidLevelIrOperand? result = operatorText switch
            {
                "+" => operand,
                "-" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.Negate, operand, operand.Type, text),
                    "neg"),
                "-%" => EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MidLevelIrBinaryOperator.WrappingSubtract,
                        new MidLevelIrIntegerConstantOperand(BigInteger.Zero, operand.Type),
                        operand,
                        operand.Type,
                        text),
                    "wrapneg"),
                "!" => EmitTemporary(
                    new MidLevelIrUnaryRValue(
                        MidLevelIrUnaryOperator.LogicalNot,
                        CoerceOperand(operand, StarkTypeSymbols.Bool) ?? operand,
                        StarkTypeSymbols.Bool,
                        text),
                    "not"),
                "~" => EmitTemporary(
                    new MidLevelIrUnaryRValue(MidLevelIrUnaryOperator.BitwiseNot, operand, operand.Type, text),
                    "bitnot"),
                "*" => LowerImportedTypedTemplateDereferenceUnary(operand, text),
                _ => null
            };

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateAddressOfUnary(
            ImportedTemplateTypedBodyExpressionSummary operandExpression)
        {
            if (!TryResolveImportedTypedTemplateAssignmentTarget(operandExpression, out var target))
            {
                return null;
            }

            return BuildAddress(target);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateDereferenceUnary(MidLevelIrOperand operand, string text)
        {
            if (operand.Type.Kind != StarkTypeKind.RawPointer || operand.Type.ElementType is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrLoadIndirectRValue(
                    operand,
                    operand.Type.ElementType,
                    text),
                "load");
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateBinary(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Name is not { } operatorText
                || expression.Args.Count != 2)
            {
                return null;
            }

            if (operatorText is "&&" or "||")
            {
                return LowerImportedTypedTemplateShortCircuitBinary(expression, operatorText, expectedType);
            }

            var numericExpectedType = IsExpectedIntegerBinaryResult(operatorText, expectedType)
                ? expectedType
                : null;
            var left = LowerImportedTypedTemplateExpressionCore(expression.Args[0], numericExpectedType);
            var right = LowerImportedTypedTemplateExpressionCore(expression.Args[1], numericExpectedType);
            if (left is null || right is null)
            {
                return null;
            }

            var text = RenderImportedTypedTemplateExpressionCore(expression);
            MidLevelIrOperand? result;
            if (operatorText is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                result = EmitPairComparison(left, right, operatorText, text);
            }
            else
            {
                var resultType = operatorText is "<<" or ">>"
                    ? GetShiftResultType(left.Type)
                    : FindCommonType(left.Type, right.Type);
                if (resultType.Kind == StarkTypeKind.Error)
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported binary expression '{text}' reached MIR without a common result type for '{left.Type.DisplayName}' and '{right.Type.DisplayName}'.");
                }

                if (operatorText is "&" or "^" or "|" or "<<" or ">>"
                    && resultType.Kind != StarkTypeKind.Integer)
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported bitwise/shift expression '{text}' reached MIR with non-integer result type '{resultType.DisplayName}'.");
                }

                var coercedLeft = CoerceOperand(left, resultType);
                var coercedRight = CoerceOperand(right, resultType);
                if (coercedLeft is null || coercedRight is null)
                {
                    return null;
                }

                if (operatorText == "+"
                    && TryFoldTextConstantConcatenation(coercedLeft, coercedRight, resultType, out var foldedText))
                {
                    result = foldedText;
                }
                else
                {
                    result = EmitTemporary(
                        new MidLevelIrBinaryRValue(
                            MapBinaryOperator(operatorText),
                            coercedLeft,
                            coercedRight,
                            resultType,
                            text),
                        "bin");
                }
            }

            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private static bool IsExpectedIntegerBinaryResult(string operatorText, StarkTypeSymbol? expectedType)
        {
            return expectedType?.Kind == StarkTypeKind.Integer
                && operatorText is "+"
                    or "-"
                    or "*"
                    or "**"
                    or "+%"
                    or "-%"
                    or "*%"
                    or "+|"
                    or "-|"
                    or "*|"
                    or "/"
                    or "%"
                    or "&"
                    or "^"
                    or "|"
                    or "<<"
                    or ">>";
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateComparisonChain(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Args.Count < 2 || expression.Operators.Count != expression.Args.Count - 1)
            {
                return null;
            }

            var left = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            if (left is null)
            {
                return null;
            }

            if (expression.Operators.Count == 1)
            {
                var right = LowerImportedTypedTemplateExpressionCore(expression.Args[1], expectedType: null);
                if (right is null)
                {
                    return null;
                }

                var comparison = EmitPairComparison(left, right, expression.Operators[0], RenderImportedTypedTemplateExpressionCore(expression));
                return expectedType is null ? comparison : CoerceOperand(comparison, expectedType);
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, "typed_cmpchain");
            var joinBlock = CreateBlock("typed_cmpchain_join");
            var currentLeft = left;

            for (var index = 0; index < expression.Operators.Count; index++)
            {
                var right = LowerImportedTypedTemplateExpressionCore(expression.Args[index + 1], expectedType: null);
                if (right is null)
                {
                    return null;
                }

                var comparisonText =
                    $"{RenderImportedTypedTemplateExpressionCore(expression.Args[index])} {expression.Operators[index]} {RenderImportedTypedTemplateExpressionCore(expression.Args[index + 1])}";
                var comparison = EmitPairComparison(currentLeft, right, expression.Operators[index], comparisonText);
                if (comparison is null)
                {
                    return null;
                }

                if (index == expression.Operators.Count - 1)
                {
                    EmitOperandAssignment(result, comparison, comparison.Text);
                    EnsureGoto(joinBlock.Id);
                    break;
                }

                var nextBlock = CreateBlock($"typed_cmpchain_next_{index + 1}");
                var falseBlock = CreateBlock($"typed_cmpchain_false_{index}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextBlock.Id, falseBlock.Id],
                    ConditionText: comparison.Text,
                    Condition: comparison);

                CurrentBlock = falseBlock;
                EmitOperandAssignment(result, new MidLevelIrBoolConstantOperand(false), "false");
                EnsureGoto(joinBlock.Id);

                CurrentBlock = nextBlock;
                currentLeft = right;
            }

            CurrentBlock = joinBlock;
            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateShortCircuitBinary(
            ImportedTemplateTypedBodyExpressionSummary expression,
            string operatorText,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Args.Count != 2)
            {
                return null;
            }

            var left = CoerceOperand(
                LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null),
                StarkTypeSymbols.Bool);
            if (left is null)
            {
                return null;
            }

            var result = CreateTemporaryLocal(StarkTypeSymbols.Bool, operatorText == "&&" ? "typed_and" : "typed_or");
            var shortCircuitBlock = CreateBlock(operatorText == "&&" ? "typed_and_short" : "typed_or_short");
            var rhsBlock = CreateBlock(operatorText == "&&" ? "typed_and_rhs" : "typed_or_rhs");
            var joinBlock = CreateBlock(operatorText == "&&" ? "typed_and_join" : "typed_or_join");

            CurrentBlock.Terminator = operatorText == "&&"
                ? new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [rhsBlock.Id, shortCircuitBlock.Id],
                    ConditionText: RenderImportedTypedTemplateExpressionCore(expression.Args[0]),
                    Condition: left)
                : new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [shortCircuitBlock.Id, rhsBlock.Id],
                    ConditionText: RenderImportedTypedTemplateExpressionCore(expression.Args[0]),
                    Condition: left);

            CurrentBlock = shortCircuitBlock;
            EmitOperandAssignment(
                result,
                new MidLevelIrBoolConstantOperand(operatorText == "||"),
                operatorText == "||" ? "true" : "false");
            EnsureGoto(joinBlock.Id);

            CurrentBlock = rhsBlock;
            var right = CoerceOperand(
                LowerImportedTypedTemplateExpressionCore(expression.Args[1], expectedType: null),
                StarkTypeSymbols.Bool);
            if (right is null)
            {
                return null;
            }

            EmitOperandAssignment(result, right, RenderImportedTypedTemplateExpressionCore(expression.Args[1]));
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return expectedType is null ? result : CoerceOperand(result, expectedType);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateConditional(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Args.Count != 3)
            {
                return null;
            }

            var condition = LowerImportedTypedTemplateExpressionCore(expression.Args[0], StarkTypeSymbols.Bool);
            if (condition is null)
            {
                return null;
            }

            var thenBlock = CreateBlock("typed_cond_true");
            var elseBlock = CreateBlock("typed_cond_false");
            var joinBlock = CreateBlock("typed_cond_join");

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [thenBlock.Id, elseBlock.Id],
                ConditionText: RenderImportedTypedTemplateExpressionCore(expression.Args[0]),
                Condition: condition);

            CurrentBlock = thenBlock;
            var trueValue = LowerImportedTypedTemplateExpressionCore(expression.Args[1], expectedType);
            var trueBlock = CurrentBlock;
            if (trueValue is null)
            {
                return null;
            }

            CurrentBlock = elseBlock;
            var falseValue = LowerImportedTypedTemplateExpressionCore(expression.Args[2], expectedType);
            var falseBlock = CurrentBlock;
            if (falseValue is null)
            {
                return null;
            }

            var resultType = expectedType ?? FindCommonType(trueValue.Type, falseValue.Type);
            if (resultType.Kind == StarkTypeKind.Error)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported conditional expression reached MIR without a common result type for '{trueValue.Type.DisplayName}' and '{falseValue.Type.DisplayName}'.");
            }

            var resultHasConstProvenance = OperandHasConstProvenance(trueValue)
                && OperandHasConstProvenance(falseValue);
            var result = CreateTemporaryLocal(resultType, "typed_cond", resultHasConstProvenance);

            CurrentBlock = trueBlock;
            var coercedTrue = CoerceOperand(trueValue, resultType);
            if (coercedTrue is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedTrue, RenderImportedTypedTemplateExpressionCore(expression.Args[1]));
            EnsureGoto(joinBlock.Id);

            CurrentBlock = falseBlock;
            var coercedFalse = CoerceOperand(falseValue, resultType);
            if (coercedFalse is null)
            {
                return null;
            }

            EmitOperandAssignment(result, coercedFalse, RenderImportedTypedTemplateExpressionCore(expression.Args[2]));
            EnsureGoto(joinBlock.Id);

            CurrentBlock = joinBlock;
            return result;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateComptime(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol? expectedType)
        {
            if (expression.Args.Count != 1
                || !TryEvaluateImportedTypedTemplateConstant(expression.Args[0], out var constant))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported typed-template comptime expression '{RenderImportedTypedTemplateExpressionCore(expression)}' did not evaluate to a compile-time constant.");
            }

            if (expectedType is not null
                && CompileTimeExpressionEvaluator.TryCoerce(constant, expectedType, out var coerced))
            {
                constant = coerced;
            }

            var operand = CreateCompileTimeOperand(constant);
            return expectedType is null ? operand : CoerceOperand(operand, expectedType);
        }

        private bool TryBuildImportedTypedTemplateDirectCall(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateDirectCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            var loweredArguments = new List<MidLevelIrOperand>(expression.Args.Count);
            for (var index = 0; index < expression.Args.Count; index++)
            {
                var parameterType = index < signature.Parameters.Count
                    ? signature.Parameters[index].Type
                    : null;
                var argument = LowerImportedTypedTemplateExpressionCore(expression.Args[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
            }

            return TryBuildCall(
                signature.Name,
                signature,
                receiver: null,
                receiverPlace: null,
                text: RenderImportedTypedTemplateExpressionCore(expression),
                out call,
                loweredExplicitArguments: loweredArguments);
        }

        private bool TryBuildImportedTypedTemplateDirectCallStatement(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrDirectCallStatementOperation call)
        {
            call = default!;

            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateDirectCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            var loweredArguments = new List<MidLevelIrOperand>(expression.Args.Count);
            for (var index = 0; index < expression.Args.Count; index++)
            {
                var parameterType = index < signature.Parameters.Count
                    ? signature.Parameters[index].Type
                    : null;
                var argument = LowerImportedTypedTemplateExpressionCore(expression.Args[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
            }

            return TryBuildCallStatement(
                signature.Name,
                signature,
                receiver: null,
                receiverPlace: null,
                text: RenderImportedTypedTemplateExpressionCore(expression),
                out call,
                loweredExplicitArguments: loweredArguments);
        }

        private bool TryBuildImportedTypedTemplateClosureCall(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrIndirectCallRValue call)
        {
            if (!TryBuildImportedTypedTemplateClosureCallParts(expression, out var parts))
            {
                call = default!;
                return false;
            }

            return TryCreateValueCall(parts, out call);
        }

        private bool TryBuildImportedTypedTemplateClosureCallStatement(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrIndirectCallStatementOperation call)
        {
            if (!TryBuildImportedTypedTemplateClosureCallParts(expression, out var parts))
            {
                call = default!;
                return false;
            }

            call = ToStatementCall(parts);
            return true;
        }

        private bool TryBuildImportedTypedTemplateClosureCallParts(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out LoweredIndirectCallParts call)
        {
            call = default!;

            if (expression.Args.Count == 0)
            {
                return false;
            }

            var target = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            if (target is null)
            {
                return false;
            }

            if (target.Type.Kind == StarkTypeKind.FunctionPointer)
            {
                if (target.Type.FunctionPointerReturnType is not { } functionPointerReturnType
                    || target.Type.FunctionPointerParameterTypes is not { } functionPointerParameterTypes
                    || functionPointerParameterTypes.Count != expression.Args.Count - 1)
                {
                    return false;
                }

                var functionPointerArguments = new List<MidLevelIrOperand>(functionPointerParameterTypes.Count);
                var functionPointerIndirectLocals = new List<string?>(functionPointerParameterTypes.Count);
                var functionPointerIndirectAddresses = new List<MidLevelIrOperand?>(functionPointerParameterTypes.Count);

                for (var index = 0; index < functionPointerParameterTypes.Count; index++)
                {
                    var parameterType = functionPointerParameterTypes[index];
                    var argumentExpression = expression.Args[index + 1];
                    var lowered = LowerImportedTypedTemplateExpressionCore(argumentExpression, parameterType);
                    if (lowered is null)
                    {
                        return false;
                    }

                    var argument = CoerceCallArgument(lowered, parameterType);
                    if (argument is null)
                    {
                        return false;
                    }

                    functionPointerArguments.Add(argument);
                    var indirectArgumentAddress = TryResolveImportedTypedTemplateAssignmentTarget(argumentExpression, out var argumentTarget)
                        ? ResolveIndirectArgumentAddress(parameterType, argumentTarget)
                        : null;
                    functionPointerIndirectLocals.Add(indirectArgumentAddress is null
                        ? ResolveIndirectArgumentLocal(parameterType, lowered)
                            ?? ResolveIndirectArgumentLocal(parameterType, argument)
                        : null);
                    functionPointerIndirectAddresses.Add(indirectArgumentAddress);
                    RecordMoveFromOperand(argument, parameterType);
                }

                call = new LoweredIndirectCallParts(
                    target,
                    functionPointerArguments,
                    StarkTypeSymbols.BorrowReturnRuntimeType(functionPointerReturnType),
                    RenderImportedTypedTemplateExpressionCore(expression),
                    functionPointerReturnType,
                    functionPointerIndirectLocals,
                    functionPointerIndirectAddresses,
                    MayFree: false);
                return true;
            }

            if (target.Type.Kind != StarkTypeKind.Closure
                || target.Type.ClosureReturnType is not { } returnType
                || target.Type.ClosureParameterTypes is not { } parameterTypes
                || parameterTypes.Count != expression.Args.Count - 1)
            {
                return false;
            }

            var invokePointerType = CallableValueFacts.BuildClosureInvokeFunctionPointerType(target.Type);
            var environmentPointerType = CallableValueFacts.BuildClosureEnvironmentPointerType(target.Type);
            var invokePointer = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    target,
                    ElementIndex: 0,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    invokePointerType,
                    $"{target.Text}.invoke"),
                "closure_invoke");
            var environmentPointer = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    target,
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    environmentPointerType,
                    $"{target.Text}.env"),
                "closure_env");
            if (invokePointer is null || environmentPointer is null)
            {
                return false;
            }

            var loweredArguments = new List<MidLevelIrOperand>(expression.Args.Count)
            {
                environmentPointer
            };
            var indirectArgumentLocals = new List<string?>(expression.Args.Count)
            {
                null
            };
            var indirectArgumentAddresses = new List<MidLevelIrOperand?>(expression.Args.Count)
            {
                null
            };

            for (var index = 0; index < parameterTypes.Count; index++)
            {
                var parameterType = parameterTypes[index];
                var argumentExpression = expression.Args[index + 1];
                var lowered = LowerImportedTypedTemplateExpressionCore(argumentExpression, parameterType);
                if (lowered is null)
                {
                    return false;
                }

                var argument = CoerceCallArgument(lowered, parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
                var indirectArgumentAddress = TryResolveImportedTypedTemplateAssignmentTarget(argumentExpression, out var argumentTarget)
                    ? ResolveIndirectArgumentAddress(parameterType, argumentTarget)
                    : null;
                indirectArgumentLocals.Add(indirectArgumentAddress is null
                    ? ResolveIndirectArgumentLocal(parameterType, lowered)
                        ?? ResolveIndirectArgumentLocal(parameterType, argument)
                    : null);
                indirectArgumentAddresses.Add(indirectArgumentAddress);
                RecordMoveFromOperand(argument, parameterType);
            }

            call = new LoweredIndirectCallParts(
                invokePointer,
                loweredArguments,
                StarkTypeSymbols.BorrowReturnRuntimeType(returnType),
                RenderImportedTypedTemplateExpressionCore(expression),
                returnType,
                indirectArgumentLocals,
                indirectArgumentAddresses,
                MayFree: target.Type.ClosureStorageKind == StarkClosureStorageKind.Heap
                    && target.Type.ClosureCallCapability == StarkClosureCallCapability.Once);
            if (target.Type.ClosureCallCapability == StarkClosureCallCapability.Once)
            {
                RecordMoveFromOperand(target, target.Type);
            }

            return true;
        }

        private bool TryBuildImportedTypedTemplateDynTraitMemberCall(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrIndirectCallRValue call)
        {
            call = default!;
            if (!TryBuildImportedTypedTemplateDynTraitMemberCallParts(expression, out var parts))
            {
                return false;
            }

            return TryCreateValueCall(parts, out call);
        }

        private bool TryBuildImportedTypedTemplateDynTraitMemberCallStatement(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrIndirectCallStatementOperation call)
        {
            call = default!;
            if (!TryBuildImportedTypedTemplateDynTraitMemberCallParts(expression, out var parts))
            {
                return false;
            }

            call = ToStatementCall(parts);
            return true;
        }

        private bool TryBuildImportedTypedTemplateDynTraitMemberCallParts(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out LoweredIndirectCallParts call)
        {
            call = default!;
            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count == 0
                || !_importedTemplateMemberCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            MidLevelIrOperand? receiver;
            if (TryResolveImportedTypedTemplateAssignmentTarget(expression.Args[0], out var resolvedReceiverPlace))
            {
                receiver = ReadPlace(resolvedReceiverPlace);
            }
            else
            {
                receiver = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            }

            if (receiver is null || receiver.Type.Kind != StarkTypeKind.DynTrait)
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            var memberName = GetLastNameSegment(signature.SourceName ?? signature.Name);
            if (receiver.Type.DynTraitName is not { } traitName)
            {
                return false;
            }

            if (!TryGetDynTraitSlot(traitName, memberName, out var slot))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dyn trait slot lookup failed for trait '{traitName}', member '{memberName}', signature '{signature.Name}'.");
            }

            var methodParameters = signature.Parameters;
            if (methodParameters.Count == 0 || methodParameters.Count - 1 != expression.Args.Count - 1)
            {
                return false;
            }

            var erasedReceiverType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true);
            var vtablePointerType = StarkTypeSymbols.DynTraitVtablePointerForTraitObject(receiver.Type);
            var slotParameterTypes = new List<StarkTypeSymbol>(methodParameters.Count) { erasedReceiverType };
            for (var index = 1; index < methodParameters.Count; index++)
            {
                slotParameterTypes.Add(methodParameters[index].Type);
            }

            var slotFunctionPointerType = StarkTypeSymbols.FunctionPointer(
                signature.Kind,
                signature.ReturnType,
                slotParameterTypes,
                isTailCallable: signature.IsTailCallable,
                pointeeDeadOnReturnParameterNames: MapPointeeDeadOnReturnParameters(signature));
            var vtablePointer = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    receiver,
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    vtablePointerType,
                    $"{receiver.Text}.vtable"),
                "dyn_vtable");
            var dataPointer = EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    receiver,
                    ElementIndex: 0,
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    erasedReceiverType,
                    $"{receiver.Text}.data"),
                "dyn_data");
            if (vtablePointer is null || dataPointer is null)
            {
                return false;
            }

            var methodPointer = EmitTemporary(
                new MidLevelIrDynVTableSlotRValue(
                    vtablePointer,
                    slot.Index,
                    slotFunctionPointerType,
                    $"{receiver.Text}.{memberName}#slot{slot.Index}"),
                "dyn_method");
            if (methodPointer is null)
            {
                return false;
            }

            var loweredArguments = new List<MidLevelIrOperand>(methodParameters.Count) { dataPointer };
            var indirectArgumentLocals = new List<string?>(methodParameters.Count) { null };
            var indirectArgumentAddresses = new List<MidLevelIrOperand?>(methodParameters.Count) { null };

            for (var index = 1; index < expression.Args.Count; index++)
            {
                var parameterType = methodParameters[index].Type;
                var argumentExpression = expression.Args[index];
                var lowered = LowerImportedTypedTemplateExpressionCore(argumentExpression, parameterType);
                if (lowered is null)
                {
                    return false;
                }

                var argument = CoerceCallArgument(lowered, parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
                var indirectArgumentAddress = TryResolveImportedTypedTemplateAssignmentTarget(argumentExpression, out var argumentTarget)
                    ? ResolveIndirectArgumentAddress(parameterType, argumentTarget)
                    : null;
                indirectArgumentLocals.Add(indirectArgumentAddress is null
                    ? ResolveIndirectArgumentLocal(parameterType, lowered)
                        ?? ResolveIndirectArgumentLocal(parameterType, argument)
                    : null);
                indirectArgumentAddresses.Add(indirectArgumentAddress);
                RecordMoveFromOperand(argument, parameterType);
            }

            call = new LoweredIndirectCallParts(
                methodPointer,
                loweredArguments,
                StarkTypeSymbols.BorrowReturnRuntimeType(signature.ReturnType),
                RenderImportedTypedTemplateExpressionCore(expression),
                signature.ReturnType,
                indirectArgumentLocals,
                indirectArgumentAddresses,
                MayFree: false);
            return true;
        }

        private static string GetLastNameSegment(string name)
        {
            var index = name.LastIndexOf('.');
            return index >= 0 ? name[(index + 1)..] : name;
        }

        private string DescribeImportedTemplateMemberCallBindingFailure(
            ImportedTemplateTypedBodyExpressionSummary expression,
            string siteKind)
        {
            // Enumerate the RAW serialized member-call facts (NOT the
            // CurrentImportedTemplateMemberCalls accessor): the failing builders
            // TryBuildImportedTypedTemplateMemberCall(Statement) read this exact field,
            // so the available-ordinal set reported here must match what they probed.
            var availableOrdinals = _importedTemplateMemberCalls.Count == 0
                ? "<none>"
                : string.Join(", ", _importedTemplateMemberCalls.Keys.OrderBy(k => k));
            return
                $"Imported typed-template {siteKind} was accepted but did not bind to serialized member-call facts: " +
                $"call site '{RenderImportedTypedTemplateExpressionCore(expression)}', " +
                $"requested ordinal {expression.Ordinal?.ToString() ?? "<none>"} not bound " +
                $"(or receiver/argument failed to lower); " +
                $"receiver/argument count {expression.Args.Count}; " +
                $"available ordinals: {availableOrdinals}.";
        }

        private bool TryBuildImportedTypedTemplateMemberCall(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrCallRValue call)
        {
            call = default!;

            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count == 0
                || !_importedTemplateMemberCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            PlaceTarget? receiverPlace = null;
            MidLevelIrOperand? receiver;
            if (TryResolveImportedTypedTemplateAssignmentTarget(expression.Args[0], out var resolvedReceiverPlace))
            {
                receiverPlace = resolvedReceiverPlace;
                receiver = ReadPlace(resolvedReceiverPlace);
            }
            else
            {
                receiver = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            }

            if (receiver is null)
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            if (receiver.Type.Kind == StarkTypeKind.DynTrait)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported typed-template dyn trait member call '{signature.Name}' on receiver '{receiver.Type.DisplayName}' did not lower through the dyn dispatch path.");
            }

            var loweredArguments = new List<MidLevelIrOperand>(expression.Args.Count - 1);
            for (var index = 1; index < expression.Args.Count; index++)
            {
                var parameterType = index < signature.Parameters.Count
                    ? signature.Parameters[index].Type
                    : null;
                var argument = LowerImportedTypedTemplateExpressionCore(expression.Args[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
            }

            return TryBuildCall(
                signature.Name,
                signature,
                receiver,
                receiverPlace,
                text: RenderImportedTypedTemplateExpressionCore(expression),
                out call,
                loweredExplicitArguments: loweredArguments);
        }

        private bool TryBuildImportedTypedTemplateMemberCallStatement(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrDirectCallStatementOperation call)
        {
            call = default!;

            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count == 0
                || !_importedTemplateMemberCalls.TryGetValue(ordinal, out var publishedSignature))
            {
                return false;
            }

            PlaceTarget? receiverPlace = null;
            MidLevelIrOperand? receiver;
            if (TryResolveImportedTypedTemplateAssignmentTarget(expression.Args[0], out var resolvedReceiverPlace))
            {
                receiverPlace = resolvedReceiverPlace;
                receiver = ReadPlace(resolvedReceiverPlace);
            }
            else
            {
                receiver = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            }

            if (receiver is null)
            {
                return false;
            }

            var signature = ApplyGenericSubstitution(publishedSignature);
            if (receiver.Type.Kind == StarkTypeKind.DynTrait)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported typed-template dyn trait member call '{signature.Name}' on receiver '{receiver.Type.DisplayName}' did not lower through the dyn dispatch path.");
            }

            var loweredArguments = new List<MidLevelIrOperand>(expression.Args.Count - 1);
            for (var index = 1; index < expression.Args.Count; index++)
            {
                var parameterType = index < signature.Parameters.Count
                    ? signature.Parameters[index].Type
                    : null;
                var argument = LowerImportedTypedTemplateExpressionCore(expression.Args[index], parameterType);
                if (argument is null)
                {
                    return false;
                }

                loweredArguments.Add(argument);
            }

            return TryBuildCallStatement(
                signature.Name,
                signature,
                receiver,
                receiverPlace,
                text: RenderImportedTypedTemplateExpressionCore(expression),
                out call,
                loweredExplicitArguments: loweredArguments);
        }

        private bool TryBuildImportedTypedTemplateDynamicStorageOperation(
            ImportedTemplateTypedBodyExpressionSummary expression,
            out MidLevelIrRValue operation)
        {
            operation = default!;

            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count == 0
                || !_importedTemplateDynamicStorageOperations.TryGetValue(ordinal, out var boundOperation))
            {
                return false;
            }

            var operationName = boundOperation.OperationName;
            if (operationName is not "Reserve" and not "TryReserve" and not "TryReserveCapacity" and not "MoveLast" and not "MoveAt")
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dynamic-storage operation '{operationName}' is not a supported dynamic storage operation.");
            }

            if (!TryResolveImportedTypedTemplateAssignmentTarget(expression.Args[0], out var receiverPlace))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dynamic-storage operation '{operationName}' requires an addressable dynamic receiver.");
            }

            var receiver = ReadPlace(receiverPlace);
            if (receiver.Type.Kind != StarkTypeKind.Dynamic || receiver.Type.ElementType is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dynamic-storage operation '{operationName}' requires a dynamic receiver, but found '{receiver.Type.DisplayName}'.");
            }

            var expectedReceiverType = ApplyGenericSubstitution(boundOperation.ReceiverType);
            var expectedResultType = ApplyGenericSubstitution(boundOperation.ResultType);
            var explicitArgumentCount = expression.Args.Count - 1;
            if (!HasSameStorageType(expectedReceiverType, receiver.Type)
                || boundOperation.ArgumentCount != explicitArgumentCount)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dynamic-storage operation '{operationName}' bound facts do not match receiver type '{receiver.Type.DisplayName}' or source arity {explicitArgumentCount}.");
            }

            if (!boundOperation.ReceiverIsAddressable
                || !boundOperation.ReceiverIsMutable
                || !receiverPlace.IsAddressMutable)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dynamic-storage operation '{operationName}' requires a mutable addressable dynamic owner.");
            }

            var storageAddress = BuildAddress(receiverPlace);
            if (storageAddress is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dynamic-storage operation '{operationName}' requires a dynamic owner address.");
            }

            var text = RenderImportedTypedTemplateExpressionCore(expression);
            switch (operationName)
            {
                case "Reserve":
                case "TryReserve":
                case "TryReserveCapacity":
                    {
                        if (explicitArgumentCount != 1)
                        {
                            var argumentName = operationName == "TryReserveCapacity" ? "target-capacity" : "additional-capacity";
                            throw LoweringInvariantViolation(
                                null,
                                $"Imported dynamic-storage operation '{operationName}' expects one {argumentName} argument.");
                        }

                        var capacity = LowerImportedTypedTemplateExpressionCore(expression.Args[1], NonNegativeI64Type);
                        if (capacity is null || capacity.Type.Kind != StarkTypeKind.Integer)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                $"Imported dynamic-storage operation '{operationName}' requires an integer capacity operand.");
                        }

                        capacity = CoerceOperand(capacity, NonNegativeI64Type) ?? capacity;
                        operation = operationName switch
                        {
                            "TryReserve" => new MidLevelIrDynamicStorageTryReserveRValue(
                                storageAddress,
                                receiver.Type,
                                capacity,
                                DynamicStorageAllocationKind.Runtime,
                                text),
                            "TryReserveCapacity" => new MidLevelIrDynamicStorageTryReserveCapacityRValue(
                                storageAddress,
                                receiver.Type,
                                capacity,
                                DynamicStorageAllocationKind.Runtime,
                                text),
                            _ => new MidLevelIrDynamicStorageReserveRValue(
                                storageAddress,
                                receiver.Type,
                                capacity,
                                DynamicStorageAllocationKind.Runtime,
                                text)
                        };
                        ValidateImportedTypedTemplateDynamicStorageResult(operation, expectedResultType, operationName);
                        return true;
                    }

                case "MoveLast":
                    {
                        if (explicitArgumentCount != 0)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported dynamic-storage operation 'MoveLast' expects no arguments.");
                        }

                        operation = new MidLevelIrDynamicStorageMoveLastRValue(
                            storageAddress,
                            receiver.Type,
                            receiver.Type.ElementType,
                            text);
                        ValidateImportedTypedTemplateDynamicStorageResult(operation, expectedResultType, operationName);
                        return true;
                    }

                case "MoveAt":
                    {
                        if (explicitArgumentCount != 1)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported dynamic-storage operation 'MoveAt' expects one index argument.");
                        }

                        var index = LowerImportedTypedTemplateExpressionCore(expression.Args[1], NonNegativeI64Type);
                        if (index is null || index.Type.Kind != StarkTypeKind.Integer)
                        {
                            throw LoweringInvariantViolation(
                                null,
                                "Imported dynamic-storage operation 'MoveAt' requires an integer index operand.");
                        }

                        index = CoerceOperand(index, NonNegativeI64Type) ?? index;
                        operation = new MidLevelIrDynamicStorageMoveAtRValue(
                            storageAddress,
                            receiver.Type,
                            index,
                            receiver.Type.ElementType,
                            text);
                        ValidateImportedTypedTemplateDynamicStorageResult(operation, expectedResultType, operationName);
                        return true;
                }

                default:
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported dynamic-storage operation '{operationName}' has no MIR lowering case.");
            }
        }

        private void ValidateImportedTypedTemplateDynamicStorageResult(
            MidLevelIrRValue operation,
            StarkTypeSymbol expectedResultType,
            string operationName)
        {
            if (!HasSameStorageType(expectedResultType, operation.Type))
            {
                throw LoweringInvariantViolation(
                    null,
                $"Imported dynamic-storage operation '{operationName}' result type '{operation.Type.DisplayName}' does not match bound result type '{expectedResultType.DisplayName}'.");
            }
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateDynTraitFromParts(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || expression.Args.Count != 2
                || !_importedTemplateDynTraitFromPartsOperations.TryGetValue(ordinal, out var boundOperation))
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported typed-template dyn-trait-from-parts expression did not bind to serialized operation facts.");
            }

            var operationName = boundOperation.OperationName;
            if (operationName is not "dynview" and not "dynbox")
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dyn-trait-from-parts operation '{operationName}' is not supported.");
            }

            if (expression.Name is { } expressionName
                && !string.Equals(expressionName, operationName, StringComparison.Ordinal))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dyn-trait-from-parts expression name '{expressionName}' does not match bound operation '{operationName}'.");
            }

            var targetType = ApplyGenericSubstitution(boundOperation.TargetType);
            var contextType = ApplyGenericSubstitution(boundOperation.ContextType);
            var vtableType = ApplyGenericSubstitution(boundOperation.VtableType);
            if (targetType.Kind != StarkTypeKind.DynTrait
                || contextType.Kind != StarkTypeKind.RawPointer
                || vtableType.Kind != StarkTypeKind.RawPointer)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dyn-trait-from-parts operation '{operationName}' has invalid representation types.");
            }

            var context = LowerImportedTypedTemplateExpressionCore(expression.Args[0], contextType);
            var vtable = LowerImportedTypedTemplateExpressionCore(expression.Args[1], vtableType);
            if (context is null || vtable is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dyn-trait-from-parts operation '{operationName}' could not lower its operands.");
            }

            context = CoerceOperand(context, contextType);
            vtable = CoerceOperand(vtable, vtableType);
            if (context is null || vtable is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported dyn-trait-from-parts operation '{operationName}' operands could not coerce to serialized representation types.");
            }

            var withContext = EmitTemporary(
                new MidLevelIrInsertIndexRValue(
                    new MidLevelIrZeroInitializerOperand(targetType),
                    ElementIndex: 0,
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    context,
                    targetType,
                    $"{operationName}.context"),
                "dyn");
            if (withContext is null)
            {
                return null;
            }

            return EmitTemporary(
                new MidLevelIrInsertIndexRValue(
                    withContext,
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    vtable,
                    targetType,
                    $"{operationName}.vtable"),
                "dyn");
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateObjectCreation(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || _importedTemplateSummary is not { ObjectCreations.Count: > 0 } importedTemplateSummary
                || ordinal < 0
                || ordinal >= importedTemplateSummary.ObjectCreations.Count)
            {
                return null;
            }

            var publishedObjectCreation = importedTemplateSummary.ObjectCreations[ordinal];
            var createdType = ApplyGenericSubstitution(publishedObjectCreation.CreatedType);
            if (createdType.Kind == StarkTypeKind.Dynamic)
            {
                return LowerImportedTypedTemplateDynamicStorageCreation(
                    expression,
                    createdType,
                    publishedObjectCreation.StorageSelector);
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);
            var constructor = publishedObjectCreation.Constructor is null
                ? null
                : ResolveImportedConstructorBodyKey(
                    createdType,
                    ApplyGenericSubstitution(publishedObjectCreation.Constructor));
            var initializerMembers = BuildImportedObjectInitializerMemberFacts(publishedObjectCreation.InitializerMembers);
            if (createdType.Kind != StarkTypeKind.Named)
            {
                if (constructor is not null || publishedObjectCreation.InitializerMembers.Count != 0)
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported non-object creation for '{createdType.DisplayName}' carried object constructor or initializer facts.");
                }

                return current;
            }

            var argumentOffset = 0;

            if (constructor is not null)
            {
                var constructorArguments = expression.Args.Take(constructor.Parameters.Count).ToArray();
                var constructed = constructor.IsPrimaryShape
                    ? LowerImportedTypedTemplatePrimaryConstructorObjectCreation(
                        createdType,
                        constructor,
                        constructorArguments)
                    : LowerImportedTypedTemplateExplicitConstructorObjectCreation(
                        createdType,
                        constructor,
                        constructorArguments);
                if (constructed is null)
                {
                    return null;
                }

                current = constructed;
                argumentOffset = constructor.Parameters.Count;
            }

            if (publishedObjectCreation.InitializerMembers.Count != expression.Args.Count - argumentOffset)
            {
                if (publishedObjectCreation.InitializerMembers.Count == 0 && expression.Args.Count == argumentOffset)
                {
                    return WrapObjectConstruction(
                        createdType,
                        current,
                        constructor,
                        initializerMembers,
                        hasInitializer: false,
                        RenderImportedTypedTemplateExpressionCore(expression));
                }

                throw LoweringInvariantViolation(
                    null,
                    $"Imported object creation summary for '{createdType.DisplayName}' expects {publishedObjectCreation.InitializerMembers.Count} initializer argument(s), but typed template supplied {expression.Args.Count - argumentOffset}.");
            }

            if (publishedObjectCreation.InitializerMembers.Count == 0)
            {
                return WrapObjectConstruction(
                    createdType,
                    current,
                    constructor,
                    initializerMembers,
                    hasInitializer: false,
                    RenderImportedTypedTemplateExpressionCore(expression));
            }

            var initialized = LowerImportedTypedTemplateObjectInitializer(
                createdType,
                current,
                publishedObjectCreation.InitializerMembers,
                expression.Args.Skip(argumentOffset).ToArray());
            if (initialized is null)
            {
                return null;
            }

            return WrapObjectConstruction(
                createdType,
                initialized,
                constructor,
                initializerMembers,
                hasInitializer: true,
                RenderImportedTypedTemplateExpressionCore(expression));
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateDynamicStorageCreation(
            ImportedTemplateTypedBodyExpressionSummary expression,
            StarkTypeSymbol createdType,
            ObjectCreationStorageSelector storageSelector)
        {
            if (createdType.ElementType is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported dynamic storage creation requires an element type.");
            }

            if (expression.Args.Count == 0)
            {
                return new MidLevelIrZeroInitializerOperand(createdType);
            }

            if (expression.Args.Count != 1)
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported dynamic storage creation expects zero arguments or one capacity argument.");
            }

            var capacity = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
            if (capacity is null || capacity.Type.Kind != StarkTypeKind.Integer)
            {
                throw LoweringInvariantViolation(
                    null,
                    "Imported dynamic storage creation requires an integer capacity operand.");
            }

            capacity = CoerceOperand(capacity, NonNegativeI64Type) ?? capacity;
            if (capacity is MidLevelIrIntegerConstantOperand { Value.Sign: 0 })
            {
                return new MidLevelIrZeroInitializerOperand(createdType);
            }

            return EmitTemporary(
                new MidLevelIrDynamicStorageAllocationRValue(
                    capacity,
                    createdType,
                    storageSelector == ObjectCreationStorageSelector.Arena
                        ? DynamicStorageAllocationKind.Arena
                        : DynamicStorageAllocationKind.Runtime,
                    RenderImportedTypedTemplateExpressionCore(expression)),
                "dynamic");
        }

        private IReadOnlyList<ObjectInitializerMemberTypingRecord> BuildImportedObjectInitializerMemberFacts(
            IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary> initializerMembers)
        {
            if (initializerMembers.Count == 0)
            {
                return [];
            }

            var facts = new ObjectInitializerMemberTypingRecord[initializerMembers.Count];
            for (var index = 0; index < initializerMembers.Count; index++)
            {
                var member = initializerMembers[index];
                facts[index] = new ObjectInitializerMemberTypingRecord(
                    member.FieldName,
                    member.FieldIndex,
                    ApplyGenericSubstitution(member.FieldType));
            }

            return facts;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateEnumCall(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateEnumCalls.TryGetValue(ordinal, out var publishedEnumCall))
            {
                return null;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumCall.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumCall.VariantName}";
            if (!TryGetEnumLayout(publishedEnumType, out var layout)
                || !layout.TryGetVariant(publishedEnumCall.VariantName, out var variant)
                || variant.UsesNamedFields
                || variant.Fields.Count != expression.Args.Count)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported positional enum constructor '{publishedCaseName}' reached MIR without matching positional enum layout facts.");
            }

            var loweredArguments = new MidLevelIrOperand[variant.Fields.Count];
            for (var index = 0; index < variant.Fields.Count; index++)
            {
                var field = variant.Fields[index];
                var argument = LowerImportedTypedTemplateExpressionCore(expression.Args[index], field.Type);
                if (argument is null)
                {
                    return null;
                }

                var coerced = CoerceOperand(argument, field.Type);
                if (coerced is null)
                {
                    return null;
                }

                loweredArguments[index] = coerced;
            }

            return LowerDirectTagEnumConstructor(publishedEnumType, layout, variant, loweredArguments, RenderImportedTypedTemplateExpressionCore(expression));
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateEnumConstructor(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateEnumConstructors.TryGetValue(ordinal, out var publishedEnumConstructor))
            {
                return null;
            }

            var enumType = ApplyGenericSubstitution(publishedEnumConstructor.EnumType);
            if (!TryGetEnumLayout(enumType, out var layout)
                || !layout.TryGetVariant(publishedEnumConstructor.VariantName, out var variant)
                || !variant.UsesNamedFields
                || publishedEnumConstructor.Members.Count != expression.Args.Count)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported named-field enum constructor '{enumType.DisplayName}.{publishedEnumConstructor.VariantName}' reached MIR without matching named-field enum layout facts.");
            }

            var orderedValues = new MidLevelIrOperand[variant.Fields.Count];
            var assigned = new bool[variant.Fields.Count];

            for (var memberOrdinal = 0; memberOrdinal < publishedEnumConstructor.Members.Count; memberOrdinal++)
            {
                var publishedMember = publishedEnumConstructor.Members[memberOrdinal];
                if (publishedMember.FieldIndex < 0
                    || publishedMember.FieldIndex >= variant.Fields.Count)
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported enum constructor member '{publishedMember.FieldName}' has invalid field index {publishedMember.FieldIndex} for '{enumType.DisplayName}.{publishedEnumConstructor.VariantName}'.");
                }

                var layoutField = variant.Fields[publishedMember.FieldIndex];
                var value = LowerImportedTypedTemplateExpressionCore(expression.Args[memberOrdinal], ApplyGenericSubstitution(publishedMember.FieldType));
                if (value is null)
                {
                    return null;
                }

                var coerced = CoerceOperand(value, layoutField.Type);
                if (coerced is null)
                {
                    return null;
                }

                orderedValues[publishedMember.FieldIndex] = coerced;
                assigned[publishedMember.FieldIndex] = true;
            }

            if (assigned.Any(static value => !value))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported enum constructor '{enumType.DisplayName}.{publishedEnumConstructor.VariantName}' did not provide every payload field.");
            }

            return LowerDirectTagEnumConstructor(enumType, layout, variant, orderedValues, RenderImportedTypedTemplateExpressionCore(expression));
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateEnumValue(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Ordinal is not { } ordinal
                || !_importedTemplateEnumValues.TryGetValue(ordinal, out var publishedEnumValue)
                || expression.Args.Count != 0)
            {
                return null;
            }

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumValue.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumValue.VariantName}";
            if (!TryGetEnumLayout(publishedEnumType, out var layout)
                || !layout.TryGetVariant(publishedEnumValue.VariantName, out var variant)
                || variant.Fields.Count != 0)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported unit enum value '{publishedCaseName}' reached MIR without unit enum layout facts.");
            }

            return LowerDirectTagEnumConstructor(publishedEnumType, layout, variant, [], publishedCaseName);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplatePrimaryConstructorObjectCreation(
            StarkTypeSymbol createdType,
            TypedConstructorShape constructor,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments)
        {
            if (createdType.Kind != StarkTypeKind.Named
                || createdType.NamedType is null
                || !_namedTypes.TryGetValue(createdType.NamedType, out var namedType)
                || !constructor.IsPrimaryShape
                || constructor.Parameters.Count != arguments.Count)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported object creation for '{createdType.DisplayName}' requires a primary constructor shape and matching argument count.");
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);
            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out var fieldIndex))
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Imported primary constructor parameter '{parameter.Name}' was accepted without a matching field on '{createdType.DisplayName}'.");
                }

                var loweredArgument = LowerImportedTypedTemplateExpressionCore(arguments[index], ApplyGenericSubstitution(parameter.Type));
                if (loweredArgument is null)
                {
                    return null;
                }

                var fieldType = ApplyGenericSubstitution(field.Type);
                var fieldValue = CoerceOperand(loweredArgument, fieldType);
                if (fieldValue is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        field.Name,
                        fieldIndex,
                        fieldValue,
                        createdType,
                        $"{current.Text}.{field.Name} = {RenderImportedTypedTemplateExpressionCore(arguments[index])}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateExplicitConstructorObjectCreation(
            StarkTypeSymbol createdType,
            TypedConstructorShape constructor,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments)
        {
            if (constructor.IsPrimaryShape || constructor.Parameters.Count != arguments.Count)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Imported object creation for '{createdType.DisplayName}' requires an explicit constructor shape and matching argument count.");
            }

            if (constructor.BodyKey is null
                || !_constructorsByBodyKey.TryGetValue(constructor.BodyKey, out var constructorContext))
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Constructor body for '{createdType.DisplayName}' is not available to MIR lowering.");
            }

            var loweredArguments = new MidLevelIrOperand[constructor.Parameters.Count];
            var argumentTexts = new string[constructor.Parameters.Count];
            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameterType = ApplyGenericSubstitution(constructor.Parameters[index].Type);
                var loweredArgument = LowerImportedTypedTemplateExpressionCore(arguments[index], parameterType);
                if (loweredArgument is null)
                {
                    return null;
                }

                loweredArguments[index] = CoerceOperand(loweredArgument, parameterType) ?? loweredArgument;
                argumentTexts[index] = RenderImportedTypedTemplateExpressionCore(arguments[index]);
            }

            return LowerExplicitConstructorBody(createdType, constructor, constructorContext, loweredArguments, argumentTexts);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplateObjectInitializer(
            StarkTypeSymbol targetType,
            MidLevelIrOperand seed,
            IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary> initializerMembers,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments)
        {
            if (initializerMembers.Count != arguments.Count)
            {
                return null;
            }

            var current = seed;
            for (var index = 0; index < initializerMembers.Count; index++)
            {
                var publishedMember = initializerMembers[index];
                var fieldType = ApplyGenericSubstitution(publishedMember.FieldType);
                var value = LowerImportedTypedTemplateExpressionCore(arguments[index], fieldType);
                if (value is null)
                {
                    return null;
                }

                var updated = EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        current,
                        publishedMember.FieldName,
                        publishedMember.FieldIndex,
                        value,
                        targetType,
                        $"{current.Text}.{publishedMember.FieldName} = {RenderImportedTypedTemplateExpressionCore(arguments[index])}"),
                    "insertfield");
                if (updated is null)
                {
                    return null;
                }

                current = updated;
            }

            return current;
        }

        private static string RenderImportedTypedTemplateExpressionCore(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            return expression.Kind switch
            {
                ImportedTemplateTypedBodyExpressionKind.NameReference => expression.Name ?? string.Empty,
                ImportedTemplateTypedBodyExpressionKind.Literal => expression.LiteralText ?? string.Empty,
                ImportedTemplateTypedBodyExpressionKind.ArrayInitializer => expression.Args.Count == 0
                    ? "{}"
                    : $"{{ {string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))} }}",
                ImportedTemplateTypedBodyExpressionKind.ObjectInitializer => RenderImportedTypedTemplateObjectInitializer(expression),
                ImportedTemplateTypedBodyExpressionKind.Assignment => RenderImportedTypedTemplateAssignmentExpression(expression),
                ImportedTemplateTypedBodyExpressionKind.Conversion => expression.Type is { } conversionType
                    && expression.Args.Count == 1
                    ? $"({conversionType.DisplayName}){RenderImportedTypedTemplateExpressionCore(expression.Args[0])}"
                    : "conversion",
                ImportedTemplateTypedBodyExpressionKind.TryPropagation => expression.Args.Count == 1
                    ? $"try {RenderImportedTypedTemplateExpressionCore(expression.Args[0])}"
                    : "try",
                ImportedTemplateTypedBodyExpressionKind.UnaryOperation => expression.Name is { } unaryOperator
                    && expression.Args.Count == 1
                    ? $"{unaryOperator}{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}"
                    : "unary",
                ImportedTemplateTypedBodyExpressionKind.BinaryOperation => expression.Name is { } binaryOperator
                    && expression.Args.Count == 2
                    ? $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])} {binaryOperator} {RenderImportedTypedTemplateExpressionCore(expression.Args[1])}"
                    : "binary",
                ImportedTemplateTypedBodyExpressionKind.ComparisonChain => RenderImportedTypedTemplateComparisonChain(expression),
                ImportedTemplateTypedBodyExpressionKind.Conditional => expression.Args.Count == 3
                    ? $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])} ? {RenderImportedTypedTemplateExpressionCore(expression.Args[1])} : {RenderImportedTypedTemplateExpressionCore(expression.Args[2])}"
                    : "conditional",
                ImportedTemplateTypedBodyExpressionKind.Comptime => expression.Args.Count == 1
                    ? $"comptime ({RenderImportedTypedTemplateExpressionCore(expression.Args[0])})"
                    : "comptime",
                ImportedTemplateTypedBodyExpressionKind.TypeLayout => expression.Type is not null && expression.Name is not null
                    ? $"{expression.Name}({expression.Type.DisplayName})"
                    : "type-layout",
                ImportedTemplateTypedBodyExpressionKind.ObjectCreation => $"new #{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.EnumConstructor => $"enumctor#{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.EnumCall => $"enumcall#{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.EnumValue => $"enumvalue#{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.DirectCall => $"{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.ClosureCall => expression.Args.Count >= 1
                    ? $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}({string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpressionCore))})"
                    : "closure-call",
                ImportedTemplateTypedBodyExpressionKind.IndexAccess => expression.Args.Count >= 1
                    ? $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}[{string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpressionCore))}]"
                    : "index",
                ImportedTemplateTypedBodyExpressionKind.FieldAccess => $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}.{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.MemberCall => $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}.{expression.Ordinal}({string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.FunctionAddress => $"fnaddr#{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.DynamicStorageOperation => expression.Args.Count >= 1
                    ? $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}.dynamic#{expression.Ordinal}({string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpressionCore))})"
                    : $"dynamic#{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.DynTraitFromParts => expression.Args.Count == 2
                    ? $"{expression.Name ?? "dyn"}#{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})"
                    : $"{expression.Name ?? "dyn"}#{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.TextInterpolation => expression.LiteralText ?? "$\"\"",
                ImportedTemplateTypedBodyExpressionKind.TextBuild => string.Join(" + ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore)),
                ImportedTemplateTypedBodyExpressionKind.StructuralFact => RenderImportedTypedTemplateStructuralFact(expression),
                _ => string.Empty
            };
        }

        private static string RenderImportedTypedTemplateStructuralFact(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            var arguments = expression.TypeArgs.Select(static argument => argument.DisplayName)
                .Concat(expression.ComptimeValueArgs.Select(static argument => argument.DisplayName));
            return expression.Name is { Length: > 0 } name
                ? $"comptime {name}<{string.Join(", ", arguments)}>()"
                : "comptime <structural-fact>";
        }

        private static string RenderImportedTypedTemplateObjectInitializer(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Members.Count != expression.Args.Count)
            {
                return "objectinit";
            }

            var parts = new string[expression.Members.Count];
            for (var index = 0; index < expression.Members.Count; index++)
            {
                parts[index] = $"{expression.Members[index]} = {RenderImportedTypedTemplateExpressionCore(expression.Args[index])}";
            }

            return $"{{ {string.Join(", ", parts)} }}";
        }

        private static string RenderImportedTypedTemplateAssignmentExpression(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count != 1)
            {
                return "assignment";
            }

            var targetText = expression.TargetExpression is not null
                ? RenderImportedTypedTemplateExpressionCore(expression.TargetExpression)
                : expression.Name;
            if (string.IsNullOrEmpty(targetText))
            {
                return "assignment";
            }

            var assignmentOperator = string.IsNullOrEmpty(expression.AssignmentOperator)
                ? "="
                : expression.AssignmentOperator;
            var valueText = RenderImportedTypedTemplateExpressionCore(expression.Args[0]);
            return string.Equals(assignmentOperator, "init =", StringComparison.Ordinal)
                ? $"init {targetText} = {valueText}"
                : $"{targetText} {assignmentOperator} {valueText}";
        }

        private static string RenderImportedTypedTemplateComparisonChain(ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Args.Count < 2 || expression.Operators.Count != expression.Args.Count - 1)
            {
                return "cmpchain";
            }

            var builder = new StringBuilder(RenderImportedTypedTemplateExpressionCore(expression.Args[0]));
            for (var index = 0; index < expression.Operators.Count; index++)
            {
                builder.Append(' ');
                builder.Append(expression.Operators[index]);
                builder.Append(' ');
                builder.Append(RenderImportedTypedTemplateExpressionCore(expression.Args[index + 1]));
            }

            return builder.ToString();
        }

        private sealed class ImportedTemplateLowerer
        {
            private readonly FunctionMirBuilder _builder;

            public ImportedTemplateLowerer(FunctionMirBuilder builder)
            {
                _builder = builder;
            }

            public bool TryLowerBody(ImportedTemplateTypedBodySummary typedBody)
            {
                if (!TryLowerStatementList(typedBody.Statements, createScope: true))
                {
                    return false;
                }

                if (!_builder.CurrentBlock.HasTerminator)
                {
                    _builder.CurrentBlock.Terminator = _builder._function.Signature.ReturnType.Kind == StarkTypeKind.Void
                        ? new MidLevelIrTerminator(MidLevelIrTerminatorKind.Return, Targets: [], Location: _builder._functionLocation)
                        : new MidLevelIrTerminator(MidLevelIrTerminatorKind.Unreachable, Targets: [], Location: _builder._functionLocation);
                }

                return true;
            }

            public bool TryLowerStatementList(
                IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> statements,
                bool createScope)
            {
                if (createScope)
                {
                    _builder._scopes.Push(new ScopeFrame());
                }

                try
                {
                    foreach (var statement in statements)
                    {
                        if (!TryLowerStatement(statement))
                        {
                            throw _builder.LoweringInvariantViolation(
                                null,
                                $"Imported typed-template statement '{statement.Kind}' was accepted but did not lower to MIR.");
                        }
                    }
                }
                finally
                {
                    if (createScope)
                    {
                        var scope = _builder._scopes.Pop();
                        _builder.EmitStorageDead(scope);
                        _builder.RestoreScopedNameAliases(scope);
                    }
                }

                return true;
            }

            public MidLevelIrOperand? LowerExpression(
                ImportedTemplateTypedBodyExpressionSummary expression,
                StarkTypeSymbol? expectedType)
            {
                return _builder.LowerImportedTypedTemplateExpressionCore(expression, expectedType);
            }

            public static string RenderExpression(ImportedTemplateTypedBodyExpressionSummary expression)
            {
                return FunctionMirBuilder.RenderImportedTypedTemplateExpressionCore(expression);
            }

            public MidLevelIrOperand? EmitSwitchLiteralComparison(
                MidLevelIrOperand switchValue,
                ImportedTemplateTypedBodyExpressionSummary literalExpression,
                string text)
            {
                return _builder.EmitImportedTypedTemplateSwitchLiteralComparisonCore(switchValue, literalExpression, text);
            }

            private bool TryLowerStatement(ImportedTemplateTypedBodyStatementSummary statement)
            {
                var previousStatementLocation = _builder._currentStatementLocation;
                _builder._currentStatementLocation = _builder._functionLocation;

                try
                {
                    if (_builder.CurrentBlock.HasTerminator)
                    {
                        _builder.CurrentBlock = _builder.CreateBlock("dead");
                    }

                    switch (statement.Kind)
                    {
                        case ImportedTemplateTypedBodyStatementKind.Block:
                            return TryLowerBlock(statement);
                        case ImportedTemplateTypedBodyStatementKind.Empty:
                            return TryLowerEmpty();
                        case ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration:
                            return _builder.TryLowerImportedTypedTemplateLocalVariable(statement);
                        case ImportedTemplateTypedBodyStatementKind.ExpressionStatement:
                            return _builder.TryLowerImportedTypedTemplateExpressionStatement(statement);
                        case ImportedTemplateTypedBodyStatementKind.Assignment:
                            return _builder.TryLowerImportedTypedTemplateAssignment(statement);
                        case ImportedTemplateTypedBodyStatementKind.Switch:
                            return _builder.TryLowerImportedTypedTemplateSwitch(statement);
                        case ImportedTemplateTypedBodyStatementKind.For:
                            return _builder.TryLowerImportedTypedTemplateFor(statement);
                        case ImportedTemplateTypedBodyStatementKind.ForTraversal:
                            return _builder.TryLowerImportedTypedTemplateForTraversal(statement);
                        case ImportedTemplateTypedBodyStatementKind.While:
                            return _builder.TryLowerImportedTypedTemplateWhile(statement);
                        case ImportedTemplateTypedBodyStatementKind.If:
                            return _builder.TryLowerImportedTypedTemplateIf(statement);
                        case ImportedTemplateTypedBodyStatementKind.Break:
                            return _builder.TryLowerImportedTypedTemplateBreak(statement.Name);
                        case ImportedTemplateTypedBodyStatementKind.Continue:
                            return _builder.TryLowerImportedTypedTemplateContinue(statement.Name);
                        case ImportedTemplateTypedBodyStatementKind.Return:
                            return _builder.TryLowerImportedTypedTemplateReturn(statement);
                        default:
                            throw _builder.LoweringInvariantViolation(
                                null,
                                $"Imported typed-template statement kind '{statement.Kind}' has no MIR lowering case.");
                    }
                }
                finally
                {
                    _builder._currentStatementLocation = previousStatementLocation;
                }
            }

            private bool TryLowerBlock(ImportedTemplateTypedBodyStatementSummary statement)
            {
                return TryLowerStatementList(statement.Body, createScope: true);
            }

            private static bool TryLowerEmpty()
            {
                return true;
            }
        }

    }
}
