using System.Numerics;
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

        private bool TryLowerImportedTypedTemplateLocalVariable(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Name is null
                || statement.StorageClass is null
                || statement.Type is not { } statementType)
            {
                return false;
            }

            var declaredType = ApplyGenericSubstitution(statementType);
            var name = statement.Name;
            RegisterLocal(name, declaredType, statement.StorageClass, statement.IsMutable, statement.IsConstant);
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
            RecordMoveFromOperand(initializer, declaredType);
            SetRuntimeDropState(name, isActive: true);
            return true;
        }

        private bool TryLowerImportedTypedTemplateExpressionStatement(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is not { } expression)
            {
                return false;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.DirectCall)
            {
                if (!TryBuildImportedTypedTemplateDirectCall(expression, out var directCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpressionCore(expression), value: directCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.MemberCall)
            {
                if (!TryBuildImportedTypedTemplateMemberCall(expression, out var memberCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpressionCore(expression), value: memberCall);
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

            if (!_typeModel.NamedTypes.TryGetValue(targetType.NamedType, out namedType!))
            {
                var baseName = StarkTypeSymbols.GetGenericBaseName(targetType.NamedType);
                if (!_typeModel.NamedTypes.TryGetValue(baseName, out namedType!))
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
                return false;
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
                return false;
            }

            EnsureGoto(joinBlock.Id);

            CurrentBlock = elseBlock;
            if (!TryLowerImportedTypedTemplateConditionalCallStatementBranch(expression.Args[2]))
            {
                return false;
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
                if (!TryBuildImportedTypedTemplateDirectCall(expression, out var directCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpressionCore(expression), value: directCall);
                return true;
            }

            if (expression.Kind == ImportedTemplateTypedBodyExpressionKind.MemberCall)
            {
                if (!TryBuildImportedTypedTemplateMemberCall(expression, out var memberCall))
                {
                    return false;
                }

                Emit(MidLevelIrStatementKind.Evaluate, RenderImportedTypedTemplateExpressionCore(expression), value: memberCall);
                return true;
            }

            return TryLowerImportedTypedTemplateConditionalCallStatement(expression);
        }

        private static bool CanLowerImportedTypedTemplateConditionalCallStatementBranch(
            ImportedTemplateTypedBodyExpressionSummary expression)
        {
            if (expression.Kind is ImportedTemplateTypedBodyExpressionKind.DirectCall or ImportedTemplateTypedBodyExpressionKind.MemberCall)
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

            var sections = new (ImportedTemplateTypedSwitchCaseSummary Case, IReadOnlyList<LowerableSwitchLabel> Labels, BasicBlockBuilder EntryBlock, BasicBlockBuilder BodyBlock)[statement.SwitchCases.Count];
            for (var index = 0; index < statement.SwitchCases.Count; index++)
            {
                var switchCase = statement.SwitchCases[index];
                if (!TryBuildImportedTypedTemplateSwitchLabel(switchCase, out var label))
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

            if (!TryRegisterSwitchCaptureLocals(sections.Select(static section => section.Labels), switchValue.Type))
            {
                return false;
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

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.BodyBlock;
                    if (!TryLowerImportedTypedTemplateStatementList(section.Case.Statements, createScope: false))
                    {
                        return false;
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

        private bool TryBuildImportedTypedTemplateSwitchLabel(
            ImportedTemplateTypedSwitchCaseSummary switchCase,
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
                || !_typeModel.NamedTypes.TryGetValue(aggregateType.NamedType, out var namedType))
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

            parsedFieldPattern = default!;
            return false;
        }

        private bool TryLowerImportedTypedTemplateIf(ImportedTemplateTypedBodyStatementSummary statement)
        {
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

        private bool TryLowerImportedTypedTemplateBreak()
        {
            if (_breakTargets.Count == 0)
            {
                return false;
            }

            var breakTarget = _breakTargets.Peek();
            EmitStorageDeadBeyondDepth(breakTarget.ScopeDepth);
            CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [breakTarget.Target]);
            return true;
        }

        private bool TryLowerImportedTypedTemplateContinue()
        {
            if (_loops.Count == 0)
            {
                return false;
            }

            var loop = _loops.Peek();
            EmitStorageDeadBeyondDepth(loop.ScopeDepth);
            CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [loop.ContinueTarget]);
            return true;
        }

        private bool TryLowerImportedTypedTemplateWhile(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null)
            {
                return false;
            }

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

            _loops.Push(new LoopTargets(conditionBlock.Id, exitBlock.Id, _scopes.Count));
            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            CurrentBlock = bodyBlock;
            try
            {
                if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: true))
                {
                    return false;
                }

                if (!CurrentBlock.HasTerminator)
                {
                    EnsureGoto(conditionBlock.Id);
                }
            }
            finally
            {
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

                _loops.Push(new LoopTargets(iteratorBlock.Id, exitBlock.Id, _scopes.Count));
                _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
                CurrentBlock = bodyBlock;
                try
                {
                    if (!TryLowerImportedTypedTemplateStatementList(statement.Body, createScope: true))
                    {
                        return false;
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
                        EnsureGoto(conditionBlock.Id);
                    }
                }
                finally
                {
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
            }
        }

        private bool TryLowerImportedTypedTemplateReturn(ImportedTemplateTypedBodyStatementSummary statement)
        {
            if (statement.Expression is null)
            {
                if (_function.Signature.ReturnType.Kind != StarkTypeKind.Void)
                {
                    return false;
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
                return false;
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
                        return null;
                    }

                    var operand = ResolveNamedOperand(expression.Name);
                    return operand is null || expectedType is null
                        ? operand
                        : CoerceOperand(operand, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Literal:
                {
                    var result = LowerImportedTypedTemplateLiteral(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ArrayInitializer:
                {
                    var result = LowerImportedTypedTemplateArrayInitializer(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ObjectInitializer:
                {
                    var result = LowerImportedTypedTemplateObjectInitializerExpression(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Assignment:
                {
                    var result = LowerImportedTypedTemplateAssignmentExpression(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Conversion:
                {
                    var result = LowerImportedTypedTemplateConversion(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.UnaryOperation:
                {
                    var result = LowerImportedTypedTemplateUnary(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.BinaryOperation:
                {
                    var result = LowerImportedTypedTemplateBinary(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ComparisonChain:
                {
                    var result = LowerImportedTypedTemplateComparisonChain(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.Conditional:
                {
                    var result = LowerImportedTypedTemplateConditional(expression, expectedType);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.TypeLayout:
                {
                    var result = LowerImportedTypedTemplateTypeLayout(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.ObjectCreation:
                {
                    var result = LowerImportedTypedTemplateObjectCreation(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.EnumConstructor:
                {
                    var result = LowerImportedTypedTemplateEnumConstructor(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.EnumCall:
                {
                    var result = LowerImportedTypedTemplateEnumCall(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.EnumValue:
                {
                    var result = LowerImportedTypedTemplateEnumValue(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.DirectCall:
                {
                    if (!TryBuildImportedTypedTemplateDirectCall(expression, out var call))
                    {
                        return null;
                    }

                    if (call.Type.Kind == StarkTypeKind.Void)
                    {
                        return null;
                    }

                    var result = EmitTemporary(call, "call");
                    return result is null ? null : CoerceImportedTypedTemplateCallResult(call, result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.IndexAccess:
                {
                    var result = LowerImportedTypedTemplateIndexAccess(expression);
                    return result is null || expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.FieldAccess:
                {
                    if (expression.Ordinal is not { } ordinal
                        || expression.Args.Count != 1)
                    {
                        return null;
                    }

                    var receiver = LowerImportedTypedTemplateExpressionCore(expression.Args[0], expectedType: null);
                    if (receiver is null
                        || !_importedTemplateFieldAccesses.TryGetValue(ordinal, out var publishedFieldAccess))
                    {
                        return null;
                    }

                    var result = LowerKnownFieldAccess(
                        receiver,
                        publishedFieldAccess.FieldName,
                        publishedFieldAccess.FieldIndex,
                        ApplyGenericSubstitution(publishedFieldAccess.FieldType),
                        publishedFieldAccess.FieldName);
                    return expectedType is null
                        ? result
                        : CoerceOperand(result, expectedType);
                }

                case ImportedTemplateTypedBodyExpressionKind.MemberCall:
                {
                    if (!TryBuildImportedTypedTemplateMemberCall(expression, out var memberCall))
                    {
                        return null;
                    }

                    if (memberCall.Type.Kind == StarkTypeKind.Void)
                    {
                        return null;
                    }

                    var result = EmitTemporary(memberCall, "call");
                    return result is null ? null : CoerceImportedTypedTemplateCallResult(memberCall, result, expectedType);
                }

                default:
                    return null;
            }
        }

        private MidLevelIrOperand? CoerceImportedTypedTemplateCallResult(
            MidLevelIrCallRValue call,
            MidLevelIrOperand result,
            StarkTypeSymbol? expectedType)
        {
            if (expectedType is null)
            {
                return result;
            }

            if (call.SourceReturnType is { } sourceReturnType
                && StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType)
                && expectedType.BorrowKind != StarkBorrowKind.None
                && TypeCompatibilityFacts.CanAssign(expectedType, sourceReturnType))
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
                return null;
            }

            var targetType = ApplyGenericSubstitution(expression.Type);
            var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                targetType,
                _typeModel.NamedTypes,
                _enumLayoutModel.Layouts);
            if (layout is null)
            {
                return null;
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
                    MarkUnsupported(reason: "Imported typed template-body text postfix brackets currently support full-view, single-index, or start-and-length access.");
                    return null;
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
                        MarkUnsupported(reason: "Dynamic fixed-array indexing from imported typed template bodies currently requires an addressable fixed-array source.");
                        return null;
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

                MarkUnsupported(reason: "Imported typed template-body indexing is currently limited to fixed arrays, raw pointers, and slices, and text slicing with two integer indices.");
                return null;
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
                MarkUnsupported(reason: "Imported typed template-body dynamic storage indexing requires one integer index or a start/count pair.");
                return null;
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

            return CreateLiteralOperand(expression.LiteralText, ApplyGenericSubstitution(expression.Type));
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
                    MarkUnsupported();
                    return null;
                }

                if (operatorText is "&" or "^" or "|" or "<<" or ">>"
                    && resultType.Kind != StarkTypeKind.Integer)
                {
                    MarkUnsupported();
                    return null;
                }

                var coercedLeft = CoerceOperand(left, resultType);
                var coercedRight = CoerceOperand(right, resultType);
                if (coercedLeft is null || coercedRight is null)
                {
                    return null;
                }

                result = EmitTemporary(
                    new MidLevelIrBinaryRValue(
                        MapBinaryOperator(operatorText),
                        coercedLeft,
                        coercedRight,
                        resultType,
                        text),
                    "bin");
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
                MarkUnsupported();
                return null;
            }

            var result = CreateTemporaryLocal(resultType, "typed_cond");

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
            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);
            var argumentOffset = 0;

            if (publishedObjectCreation.Constructor is { } constructor)
            {
                current = LowerImportedTypedTemplatePrimaryConstructorObjectCreation(
                    createdType,
                    constructor,
                    expression.Args.Take(constructor.Parameters.Count).ToArray());
                if (current is null)
                {
                    return null;
                }

                argumentOffset = constructor.Parameters.Count;
            }

            if (publishedObjectCreation.InitializerMembers.Count != expression.Args.Count - argumentOffset)
            {
                return publishedObjectCreation.InitializerMembers.Count == 0 && expression.Args.Count == argumentOffset
                    ? current
                    : null;
            }

            if (publishedObjectCreation.InitializerMembers.Count == 0)
            {
                return current;
            }

            return LowerImportedTypedTemplateObjectInitializer(
                createdType,
                current,
                publishedObjectCreation.InitializerMembers,
                expression.Args.Skip(argumentOffset).ToArray());
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
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.UsesNamedFields
                || variant.Fields.Count != expression.Args.Count)
            {
                MarkUnsupported();
                return null;
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

            return LowerDirectTagEnumConstructor(enumType, layout, variant, loweredArguments, RenderImportedTypedTemplateExpressionCore(expression));
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
                MarkUnsupported();
                return null;
            }

            var orderedValues = new MidLevelIrOperand[variant.Fields.Count];
            var assigned = new bool[variant.Fields.Count];

            for (var memberOrdinal = 0; memberOrdinal < publishedEnumConstructor.Members.Count; memberOrdinal++)
            {
                var publishedMember = publishedEnumConstructor.Members[memberOrdinal];
                if (publishedMember.FieldIndex < 0
                    || publishedMember.FieldIndex >= variant.Fields.Count)
                {
                    MarkUnsupported();
                    return null;
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
                MarkUnsupported();
                return null;
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
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out var layout, out var variant)
                || variant.Fields.Count != 0)
            {
                MarkUnsupported();
                return null;
            }

            return LowerDirectTagEnumConstructor(enumType, layout, variant, [], publishedCaseName);
        }

        private MidLevelIrOperand? LowerImportedTypedTemplatePrimaryConstructorObjectCreation(
            StarkTypeSymbol createdType,
            TypedConstructorShape constructor,
            IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> arguments)
        {
            if (createdType.Kind != StarkTypeKind.Named
                || createdType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(createdType.NamedType, out var namedType)
                || !constructor.IsPrimaryShape
                || constructor.Parameters.Count != arguments.Count)
            {
                MarkUnsupported();
                return null;
            }

            MidLevelIrOperand current = new MidLevelIrZeroInitializerOperand(createdType);
            for (var index = 0; index < constructor.Parameters.Count; index++)
            {
                var parameter = constructor.Parameters[index];
                if (!namedType.TryGetField(parameter.Name, out var field, out var fieldIndex))
                {
                    MarkUnsupported();
                    return null;
                }

                var loweredArgument = LowerImportedTypedTemplateExpressionCore(arguments[index], ApplyGenericSubstitution(parameter.Type));
                if (loweredArgument is null)
                {
                    return null;
                }

                var fieldValue = CoerceOperand(loweredArgument, ApplyGenericSubstitution(field.Type));
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
                ImportedTemplateTypedBodyExpressionKind.TypeLayout => expression.Type is not null && expression.Name is not null
                    ? $"{expression.Name}({expression.Type.DisplayName})"
                    : "type-layout",
                ImportedTemplateTypedBodyExpressionKind.ObjectCreation => $"new #{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.EnumConstructor => $"enumctor#{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.EnumCall => $"enumcall#{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.EnumValue => $"enumvalue#{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.DirectCall => $"{expression.Ordinal}({string.Join(", ", expression.Args.Select(RenderImportedTypedTemplateExpressionCore))})",
                ImportedTemplateTypedBodyExpressionKind.IndexAccess => expression.Args.Count >= 1
                    ? $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}[{string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpressionCore))}]"
                    : "index",
                ImportedTemplateTypedBodyExpressionKind.FieldAccess => $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}.{expression.Ordinal}",
                ImportedTemplateTypedBodyExpressionKind.MemberCall => $"{RenderImportedTypedTemplateExpressionCore(expression.Args[0])}.{expression.Ordinal}({string.Join(", ", expression.Args.Skip(1).Select(RenderImportedTypedTemplateExpressionCore))})",
                _ => string.Empty
            };
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
                            return false;
                        }
                    }
                }
                finally
                {
                    if (createScope)
                    {
                        var scope = _builder._scopes.Pop();
                        _builder.EmitStorageDead(scope);
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
                        case ImportedTemplateTypedBodyStatementKind.While:
                            return _builder.TryLowerImportedTypedTemplateWhile(statement);
                        case ImportedTemplateTypedBodyStatementKind.If:
                            return _builder.TryLowerImportedTypedTemplateIf(statement);
                        case ImportedTemplateTypedBodyStatementKind.Break:
                            return _builder.TryLowerImportedTypedTemplateBreak();
                        case ImportedTemplateTypedBodyStatementKind.Continue:
                            return _builder.TryLowerImportedTypedTemplateContinue();
                        case ImportedTemplateTypedBodyStatementKind.Return:
                            return _builder.TryLowerImportedTypedTemplateReturn(statement);
                        default:
                            return false;
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
