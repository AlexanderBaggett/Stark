using System.Numerics;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder
    {
        private void InitializeRuntimeDropState(string name, StarkTypeSymbol type, bool isActive)
        {
            _runtimeDropLowerer.InitializeRuntimeDropState(name, type, isActive);
        }

        private void SetRuntimeDropState(string name, bool isActive)
        {
            _runtimeDropLowerer.SetRuntimeDropState(name, isActive);
        }

        private void RecordMoveFromOperand(MidLevelIrOperand? operand, StarkTypeSymbol destinationType)
        {
            _runtimeDropLowerer.RecordMoveFromOperand(operand, destinationType);
        }

        private bool RequiresRuntimeDrop(StarkTypeSymbol type)
        {
            return _runtimeDropLowerer.RequiresRuntimeDrop(type);
        }

        private bool TryGetEnumLayout(StarkTypeSymbol type, out EnumLayoutSymbol layout)
        {
            return _runtimeDropLowerer.TryGetEnumLayout(type, out layout);
        }

        private void EmitStorageDead(ScopeFrame scope)
        {
            _runtimeDropLowerer.EmitStorageDead(scope);
        }

        private void EmitStorageDeadBeyondDepth(int depth)
        {
            _runtimeDropLowerer.EmitStorageDeadBeyondDepth(depth);
        }

        private void EmitAssignment(LoweredAssignment assignment)
        {
            _runtimeDropLowerer.EmitAssignment(assignment);
        }

        private void EmitOnceClosureEnvironmentCleanup()
        {
            _runtimeDropLowerer.EmitOnceClosureEnvironmentCleanup();
        }

        private void InitializeRuntimeDropStateCore(string name, StarkTypeSymbol type, bool isActive)
        {
            if (!RequiresRuntimeDropCore(type))
            {
                return;
            }

            _runtimeDropStates[name] = isActive;
        }

        private void SetRuntimeDropStateCore(string name, bool isActive)
        {
            if (_runtimeDropStates.ContainsKey(name))
            {
                _runtimeDropStates[name] = isActive;
            }
        }

        private void EmitRuntimeDropIfActiveCore(string name, StarkTypeSymbol type)
        {
            if (!_runtimeDropStates.TryGetValue(name, out var isActive) || !isActive)
            {
                return;
            }

            EmitRuntimeDropFromNamedValueCore(name, type);
            _runtimeDropStates[name] = false;
        }

        private void RecordMoveFromOperandCore(MidLevelIrOperand? operand, StarkTypeSymbol destinationType)
        {
            if (operand is null
                || destinationType.BorrowKind != StarkBorrowKind.None)
            {
                return;
            }

            if (operand is MidLevelIrObjectConstructionOperand objectConstruction)
            {
                RecordMoveFromOperandCore(objectConstruction.Value, destinationType);
                return;
            }

            if (operand is MidLevelIrEnumConstructionOperand enumConstruction)
            {
                RecordMoveFromOperandCore(enumConstruction.Value, destinationType);
                return;
            }

            if (operand is MidLevelIrLocalOperand closureCaptureLoad
                && _closureCaptureMoveSourcesByTempName.TryGetValue(closureCaptureLoad.Name, out var captureName))
            {
                var captureDropStateKey = BuildClosureCaptureDropStateKey(captureName);
                if (_runtimeDropStates.ContainsKey(captureDropStateKey))
                {
                    _runtimeDropStates[captureDropStateKey] = false;
                }
            }

            switch (operand)
            {
                case MidLevelIrLocalOperand localOperand when _runtimeDropStates.ContainsKey(localOperand.Name):
                    _runtimeDropStates[localOperand.Name] = false;
                    break;
                case MidLevelIrParameterOperand parameterOperand when _runtimeDropStates.ContainsKey(parameterOperand.Name):
                    _runtimeDropStates[parameterOperand.Name] = false;
                    break;
            }
        }

        private void EmitOnceClosureEnvironmentCleanupCore()
        {
            if (!IsCurrentOnceHeapClosureInvoke()
                || _currentClosureLambda is null
                || _closureEnvironmentType is null
                || !_parametersByName.TryGetValue(
                    CallableValueFacts.ClosureEnvironmentParameterName,
                    out var environmentParameterSymbol))
            {
                return;
            }

            var typedEnvironmentPointer = GetTypedClosureEnvironmentAddress();
            if (typedEnvironmentPointer is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Once heap closure invoke '{_function.Name}' could not resolve its environment pointer.");
            }

            for (var index = _currentClosureLambda.CaptureFields.Count - 1; index >= 0; index--)
            {
                var capture = _currentClosureLambda.CaptureFields[index];
                var captureDropStateKey = BuildClosureCaptureDropStateKey(capture.Name);
                if (!IsOwnedClosureCaptureFieldForDrop(capture)
                    || !RequiresRuntimeDrop(capture.FieldType)
                    || !_runtimeDropStates.TryGetValue(captureDropStateKey, out var isActive)
                    || !isActive)
                {
                    continue;
                }

                if (!TryResolveField(_closureEnvironmentType, capture.FieldName, out _, out var fieldIndex))
                {
                    throw LoweringInvariantViolation(
                        null,
                        $"Once heap closure invoke '{_function.Name}' could not resolve environment field '{capture.FieldName}'.");
                }

                var fieldAddress = EmitRequiredTemporary(
                    new MidLevelIrFieldAddressRValue(
                        typedEnvironmentPointer,
                        _closureEnvironmentType,
                        capture.FieldName,
                        fieldIndex,
                        AddressType(capture.FieldType, isMutable: true),
                        $"{typedEnvironmentPointer.Text}.{capture.FieldName}"),
                    "closure_field");
                var fieldValue = EmitRequiredTemporary(
                    new MidLevelIrLoadIndirectRValue(
                        fieldAddress,
                        capture.FieldType,
                        $"{fieldAddress.Text}:load"),
                    "closure_field");
                EmitRuntimeDropFromOperandCore(fieldValue, capture.FieldType);
                _runtimeDropStates[captureDropStateKey] = false;
            }

            var environmentParameter = new MidLevelIrParameterOperand(
                environmentParameterSymbol.Name,
                environmentParameterSymbol.Type);
            Emit(
                MidLevelIrStatementKind.Evaluate,
                $"free {environmentParameter.Text}",
                value: new MidLevelIrHeapStorageFreeRValue(
                    environmentParameter,
                    $"free {environmentParameter.Text}"));
        }

        private bool RequiresRuntimeDropCore(StarkTypeSymbol type)
        {
            return RequiresRuntimeDropCore(type, new HashSet<string>(StringComparer.Ordinal));
        }

        private bool RequiresRuntimeDropCore(StarkTypeSymbol type, HashSet<string> visiting)
        {
            type = ApplyGenericSubstitution(type);
            if (type.BorrowKind != StarkBorrowKind.None)
            {
                return false;
            }

            if (type.Kind == StarkTypeKind.FixedArray)
            {
                return type.ElementType is not null
                    && RequiresRuntimeDropCore(type.ElementType, visiting);
            }

            if (type.Kind == StarkTypeKind.Dynamic)
            {
                return true;
            }

            if (type.Kind == StarkTypeKind.Closure
                && type.ClosureStorageKind == StarkClosureStorageKind.Heap)
            {
                return true;
            }

            // An owning `heap dyn` trait object owns its boxed data and must drop it
            // (concrete destructor via the vtable Drop slot) and free the box. A
            // borrowed `dyn` view owns nothing and needs no drop.
            if (type.Kind == StarkTypeKind.DynTrait
                && type.DynTraitStorageKind == StarkDynTraitStorageKind.Heap)
            {
                return true;
            }

            if (type.Kind != StarkTypeKind.Named || type.NamedType is null)
            {
                return false;
            }

            if (!visiting.Add(type.NamedType))
            {
                return false;
            }

            try
            {
                if (TryGetDestructorCore(type, out _))
                {
                    return true;
                }

                if (TryGetEnumLayoutCore(type, out var layout))
                {
                    foreach (var variant in layout.Variants.Values)
                    {
                        foreach (var field in variant.Fields)
                        {
                            var fieldType = ApplyRuntimeDropGenericSubstitution(field.Type, type);
                            if (RequiresRuntimeDropCore(fieldType, visiting))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                if (!_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                    || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record))
                {
                    return false;
                }

                foreach (var field in namedType.OrderedFields)
                {
                    var fieldType = ApplyRuntimeDropGenericSubstitution(field.Type, type);
                    if (RequiresRuntimeDropCore(fieldType, visiting))
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                visiting.Remove(type.NamedType);
            }
        }

        private bool TryGetDestructorCore(StarkTypeSymbol type, out DestructorLoweringContext destructor)
        {
            destructor = default!;

            if (type.NamedType is null)
            {
                return false;
            }

            var key = StarkTypeSymbols.GetGenericBaseName(type.NamedType);
            return _destructorsByTypeName.TryGetValue(key, out destructor!);
        }

        private bool TryGetEnumLayoutCore(StarkTypeSymbol type, out EnumLayoutSymbol layout)
        {
            layout = default!;

            if (type.NamedType is null)
            {
                return false;
            }

            if (_enumLayoutModel.Layouts.TryGetValue(type.NamedType, out layout!))
            {
                return true;
            }

            if (_publishedEnumLayouts.TryGetValue(type.NamedType, out layout!))
            {
                return true;
            }

            var key = StarkTypeSymbols.GetGenericBaseName(type.NamedType);
            return _enumLayoutModel.Layouts.TryGetValue(key, out layout!)
                || _publishedEnumLayouts.TryGetValue(key, out layout!);
        }

        private StarkTypeSymbol ApplyRuntimeDropGenericSubstitution(StarkTypeSymbol type, StarkTypeSymbol ownerType)
        {
            var substitution = BuildRuntimeDropGenericSubstitution(ownerType);
            return substitution is { Count: > 0 }
                ? FunctionOverloadFacts.SubstituteType(type, substitution)
                : ApplyGenericSubstitution(type);
        }

        private IReadOnlyDictionary<string, StarkTypeSymbol>? BuildRuntimeDropGenericSubstitution(StarkTypeSymbol ownerType)
        {
            Dictionary<string, StarkTypeSymbol>? substitution = _activeGenericTypeSubstitution is { Count: > 0 }
                ? new Dictionary<string, StarkTypeSymbol>(_activeGenericTypeSubstitution, StringComparer.Ordinal)
                : null;
            var concreteOwnerType = substitution is { Count: > 0 }
                ? FunctionOverloadFacts.SubstituteType(ownerType, substitution)
                : ownerType;

            if (concreteOwnerType.NamedType is null
                || concreteOwnerType.TypeArguments is not { Count: > 0 } typeArguments)
            {
                return substitution;
            }

            var baseTypeName = StarkTypeSymbols.GetGenericBaseName(concreteOwnerType.NamedType);
            if (!_typeModel.NamedTypes.TryGetValue(baseTypeName, out var template)
                || template.GenericParams.Count == 0)
            {
                return substitution;
            }

            substitution ??= new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            for (var index = 0; index < template.GenericParams.Count && index < typeArguments.Count; index++)
            {
                substitution[template.GenericParams[index]] = typeArguments[index];
            }

            return substitution;
        }

        private void EmitRuntimeDropFromNamedValueCore(string name, StarkTypeSymbol type)
        {
            var source = ResolveNamedOperand(name);
            if (source is null)
            {
                return;
            }

            EmitRuntimeDropFromOperandCore(source, type);
        }

        private void EmitRuntimeDropFromOperandCore(MidLevelIrOperand operand, StarkTypeSymbol type)
        {
            type = ApplyGenericSubstitution(type);
            if (!RequiresRuntimeDropCore(type))
            {
                return;
            }

            if (type.Kind == StarkTypeKind.Dynamic)
            {
                if (type.ElementType is { } elementType)
                {
                    EmitDynamicStorageElementDropsCore(operand, type, elementType);
                }

                if (IsArenaBackedDynamicStorageOperand(operand))
                {
                    return;
                }

                Emit(
                    MidLevelIrStatementKind.Evaluate,
                    $"drop {operand.Text}",
                    value: new MidLevelIrDynamicStorageFreeRValue(operand, $"drop {operand.Text}"));
                return;
            }

            if (type.Kind == StarkTypeKind.Closure
                && type.ClosureStorageKind == StarkClosureStorageKind.Heap)
            {
                EmitHeapClosureDropCore(operand, type);
                return;
            }

            if (type.Kind == StarkTypeKind.DynTrait
                && type.DynTraitStorageKind == StarkDynTraitStorageKind.Heap)
            {
                EmitOwnedDynTraitDropCore(operand, type);
                return;
            }

            var temporary = operand is MidLevelIrLocalOperand localOperand && localOperand.Type == type
                ? localOperand
                : CreateTemporaryLocal(type, "drop");
            if (!ReferenceEquals(temporary, operand))
            {
                EmitOperandAssignment(temporary, operand, operand.Text);
            }

            if (type.Kind == StarkTypeKind.FixedArray
                && type.ElementType is not null
                && type.FixedLength is int fixedLength)
            {
                EmitFixedArrayElementDropsCore(temporary, type.ElementType, fixedLength);
                return;
            }

            if (TryGetDestructorCore(type, out var destructor))
            {
                using var destructorContext = PushDestructorContextCore(destructor.ModuleName, "self", temporary.Name, type);
                LowerBlock(destructor.Body);
            }

            if (TryGetEnumLayoutCore(type, out var layout))
            {
                EmitEnumPayloadDropsCore(temporary, type, layout, new HashSet<string>(StringComparer.Ordinal));
                return;
            }

            EmitStructFieldDropsCore(temporary, type, new HashSet<string>(StringComparer.Ordinal));
        }

        private bool IsArenaBackedDynamicStorageOperand(MidLevelIrOperand operand)
        {
            return operand is MidLevelIrLocalOperand local
                && _dynamicStorageAllocationKindByLocal.TryGetValue(local.Name, out var allocationKind)
                && allocationKind == DynamicStorageAllocationKind.Arena;
        }

        private void EmitHeapClosureDropCore(MidLevelIrOperand operand, StarkTypeSymbol type)
        {
            var environmentPointerType = CallableValueFacts.BuildClosureEnvironmentPointerType(type);
            var dropEnvironmentPointerType = CallableValueFacts.BuildClosureDropEnvironmentPointerType();
            var dropPointerType = CallableValueFacts.BuildClosureDropFunctionPointerType();

            var environmentPointer = EmitRequiredTemporary(
                new MidLevelIrExtractIndexRValue(
                    operand,
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    environmentPointerType,
                    $"{operand.Text}.env"),
                "closure_drop_env");
            var mutableEnvironmentPointer = environmentPointer.Type == dropEnvironmentPointerType
                ? environmentPointer
                : EmitRequiredTemporary(
                    new MidLevelIrConvertRValue(
                        environmentPointer,
                        dropEnvironmentPointerType,
                        $"{environmentPointer.Text}:drop-env"),
                    "closure_drop_env");
            var dropPointer = EmitRequiredTemporary(
                new MidLevelIrExtractIndexRValue(
                    operand,
                    ElementIndex: 2,
                    OperationFamily: IndexedElementOperationFamily.ClosureComponent,
                    dropPointerType,
                    $"{operand.Text}.drop"),
                "closure_drop_fn");

            Emit(
                MidLevelIrStatementKind.Evaluate,
                $"drop {operand.Text}",
                call: new MidLevelIrIndirectCallStatementOperation(
                    dropPointer,
                    [mutableEnvironmentPointer],
                    StarkTypeSymbols.Void,
                    $"drop {operand.Text}",
                    SourceReturnType: StarkTypeSymbols.Void,
                    MayFree: true));
        }

        // Drops an owning `heap dyn` trait object: load the vtable's Drop slot (which
        // follows the method slots) and call it with the erased data pointer. The
        // thunk runs the boxed value's destructor/field drops and frees the box, so
        // the call is `MayFree`. Mirrors the heap-closure drop, but the drop function
        // comes from the shared vtable rather than from the value itself.
        private void EmitOwnedDynTraitDropCore(MidLevelIrOperand operand, StarkTypeSymbol type)
        {
            if (type.DynTraitName is not { } traitName)
            {
                return;
            }

            var erasedDataType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true);
            var vtablePointerType = StarkTypeSymbols.DynTraitVtablePointerForTraitObject(type);
            var dropSlotFunctionPointerType = CallableValueFacts.BuildClosureDropFunctionPointerType();
            var dropSlotIndex = DynTraitFacts.GetVtableLayout(traitName, _typeModel.Functions).Count;

            var dataPointer = EmitRequiredTemporary(
                new MidLevelIrExtractIndexRValue(
                    operand,
                    ElementIndex: 0,
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    erasedDataType,
                    $"{operand.Text}.data"),
                "dyn_drop_data");
            var vtablePointer = EmitRequiredTemporary(
                new MidLevelIrExtractIndexRValue(
                    operand,
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    vtablePointerType,
                    $"{operand.Text}.vtable"),
                "dyn_drop_vtable");
            var dropPointer = EmitRequiredTemporary(
                new MidLevelIrDynVTableSlotRValue(
                    vtablePointer,
                    dropSlotIndex,
                    dropSlotFunctionPointerType,
                    $"{operand.Text}.drop#slot{dropSlotIndex}"),
                "dyn_drop_fn");

            Emit(
                MidLevelIrStatementKind.Evaluate,
                $"drop {operand.Text}",
                call: new MidLevelIrIndirectCallStatementOperation(
                    dropPointer,
                    [dataPointer],
                    StarkTypeSymbols.Void,
                    $"drop {operand.Text}",
                    SourceReturnType: StarkTypeSymbols.Void,
                    MayFree: true));
        }

        private void EmitDynamicStorageElementDropsCore(
            MidLevelIrOperand storage,
            StarkTypeSymbol storageType,
            StarkTypeSymbol elementType)
        {
            if (!RequiresRuntimeDropCore(elementType))
            {
                return;
            }

            var dataPointerType = StarkTypeSymbols.RawPointer(elementType, isMutable: true);
            var dataPointer = LowerKnownFieldAccess(storage, "Data", 0, dataPointerType, "Data");
            var length = LowerKnownFieldAccess(storage, "Length", 1, NonNegativeI64Type, "Length");
            var index = CreateTemporaryLocal(NonNegativeI64Type, "dynamic_drop_index");
            EmitOperandAssignment(index, length, length.Text);

            var conditionBlock = CreateBlock("dynamic_drop_cond");
            var bodyBlock = CreateBlock("dynamic_drop_body");
            var exitBlock = CreateBlock("dynamic_drop_exit");

            EnsureGoto(conditionBlock.Id);

            CurrentBlock = conditionBlock;
            var hasElement = EmitRequiredTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.GreaterThan,
                    index,
                    new MidLevelIrIntegerConstantOperand(BigInteger.Zero, NonNegativeI64Type),
                    StarkTypeSymbols.Bool,
                    $"{index.Text} > 0"),
                "cmp");
            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [bodyBlock.Id, exitBlock.Id],
                ConditionText: $"{storage.Text}.Length > 0",
                Condition: hasElement);

            CurrentBlock = bodyBlock;
            var nextIndex = EmitRequiredTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Subtract,
                    index,
                    new MidLevelIrIntegerConstantOperand(BigInteger.One, NonNegativeI64Type),
                    NonNegativeI64Type,
                    $"{index.Text} - 1"),
                "dynamic_drop_index");
            EmitOperandAssignment(index, nextIndex, nextIndex.Text);

            var elementAddress = EmitRequiredTemporary(
                new MidLevelIrElementAddressRValue(
                    dataPointer,
                    elementType,
                    index,
                    ConstantIndex: null,
                    AddressType(elementType, isMutable: true),
                    $"{storage.Text}[{index.Text}]"),
                "addr");
            var elementValue = EmitRequiredTemporary(
                new MidLevelIrLoadIndirectRValue(
                    elementAddress,
                    elementType,
                    $"{storage.Text}[{index.Text}]"),
                "drop");
            EmitRuntimeDropFromOperandCore(elementValue, elementType);
            EnsureGoto(conditionBlock.Id);

            CurrentBlock = exitBlock;
        }

        private void EmitStructFieldDropsCore(
            MidLevelIrLocalOperand aggregate,
            StarkTypeSymbol type,
            HashSet<string> visiting)
        {
            type = ApplyGenericSubstitution(type);
            if (type.Kind != StarkTypeKind.Named
                || type.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !visiting.Add(type.NamedType))
            {
                return;
            }

            for (var index = namedType.OrderedFields.Count - 1; index >= 0; index--)
            {
                var field = namedType.OrderedFields[index];
                var fieldType = ApplyRuntimeDropGenericSubstitution(field.Type, type);
                if (!RequiresRuntimeDropCore(fieldType))
                {
                    continue;
                }

                var fieldValue = LowerKnownFieldAccess(aggregate, field.Name, index, fieldType, field.Name);
                EmitRuntimeDropFromOperandCore(fieldValue, fieldType);
            }

            visiting.Remove(type.NamedType);
        }

        private void EmitFixedArrayElementDropsCore(
            MidLevelIrLocalOperand aggregate,
            StarkTypeSymbol elementType,
            int fixedLength)
        {
            if (!RequiresRuntimeDropCore(elementType))
            {
                return;
            }

            for (var index = fixedLength - 1; index >= 0; index--)
            {
                var elementValue = EmitRequiredTemporary(
                    new MidLevelIrExtractIndexRValue(
                        aggregate,
                        index,
                        IndexedElementOperationFamily.FixedArrayElement,
                        elementType,
                        $"{aggregate.Text}[{index}]"),
                    "index");
                EmitRuntimeDropFromOperandCore(elementValue, elementType);
            }
        }

        private void EmitEnumPayloadDropsCore(
            MidLevelIrLocalOperand aggregate,
            StarkTypeSymbol type,
            EnumLayoutSymbol layout,
            HashSet<string> visiting)
        {
            type = ApplyGenericSubstitution(type);
            if (!visiting.Add(layout.EnumName))
            {
                return;
            }

            try
            {
                var dropVariants = layout.Variants.Values
                    .Select(variant => (
                        Variant: variant,
                        Fields: variant.Fields
                            .Where(field => RequiresRuntimeDropCore(
                                ApplyRuntimeDropGenericSubstitution(field.Type, type),
                                visiting))
                            .ToArray()))
                    .Where(static item => item.Fields.Length > 0)
                    .OrderBy(static item => item.Variant.TagValue)
                    .ToArray();
                if (dropVariants.Length == 0)
                {
                    return;
                }

                var tagValue = LowerKnownFieldAccess(aggregate, layout.TagField.Name, 0, layout.TagField.Type, "$tag");
                var joinBlock = CreateBlock("enum_drop_join");
                BasicBlockBuilder? nextDecisionBlock = CurrentBlock;

                for (var variantIndex = 0; variantIndex < dropVariants.Length; variantIndex++)
                {
                    if (nextDecisionBlock is null)
                    {
                        break;
                    }

                    CurrentBlock = nextDecisionBlock;

                    var (variant, fields) = dropVariants[variantIndex];
                    var matchBlock = CreateBlock($"enum_drop_{variant.Name}");
                    var fallthroughBlock = variantIndex == dropVariants.Length - 1
                        ? null
                        : CreateBlock($"enum_drop_next_{variantIndex}");
                    var expectedTag = new MidLevelIrIntegerConstantOperand(new BigInteger(variant.TagValue), layout.TagField.Type);
                    var condition = EmitResolvedEqualityComparison(tagValue, expectedTag, $"{aggregate.Text}.$tag == {variant.TagValue}");

                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [matchBlock.Id, fallthroughBlock?.Id ?? joinBlock.Id],
                        ConditionText: $"{layout.EnumName}.{variant.Name}",
                        Condition: condition);

                    CurrentBlock = matchBlock;
                    for (var fieldIndex = fields.Length - 1; fieldIndex >= 0; fieldIndex--)
                    {
                        var field = fields[fieldIndex];
                        var fieldType = ApplyRuntimeDropGenericSubstitution(field.Type, type);
                        var displayName = field.SourceFieldName ?? $"[{field.SourcePosition}]";
                        var fieldValue = LowerKnownFieldAccess(
                            aggregate,
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            fieldType,
                            displayName);
                        EmitRuntimeDropFromOperandCore(fieldValue, fieldType);
                    }

                    EnsureGoto(joinBlock.Id);
                    nextDecisionBlock = fallthroughBlock;
                }

                CurrentBlock = joinBlock;
            }
            finally
            {
                visiting.Remove(layout.EnumName);
            }
        }

        private IDisposable PushDestructorContextCore(
            string moduleName,
            string aliasName,
            string localName,
            StarkTypeSymbol selfType)
        {
            var previousModuleName = _moduleNameOverride;
            var previousGenericTypeSubstitution = _activeGenericTypeSubstitution;
            var previousGenericValueSubstitution = _activeGenericValueSubstitution;
            var previousComptimeGenericParameters = _activeComptimeGenericParameters;
            var hadAlias = _nameAliases.TryGetValue(aliasName, out var previousAlias);
            _moduleNameOverride = moduleName;
            _activeComptimeGenericParameters = BuildNamedTypeComptimeGenericParameters(selfType);
            _activeGenericValueSubstitution = BuildNamedTypeComptimeValueSubstitution(selfType);
            _activeGenericTypeSubstitution = BuildNamedTypeGenericSubstitution(selfType);
            _nameAliases[aliasName] = localName;
            return new DestructorContext(
                this,
                previousModuleName,
                previousGenericTypeSubstitution,
                previousGenericValueSubstitution,
                previousComptimeGenericParameters,
                aliasName,
                previousAlias,
                hadAlias);
        }

        private void EmitStorageDeadCore(ScopeFrame scope)
        {
            if (CurrentBlock.HasTerminator)
            {
                return;
            }

            var locals = scope.Locals.ToArray();
            for (var index = locals.Length - 1; index >= 0; index--)
            {
                var (name, type) = locals[index];
                EmitRuntimeDropIfActiveCore(name, type);
                Emit(MidLevelIrStatementKind.StorageDead, name, name, type);
            }
        }

        private void EmitStorageDeadBeyondDepthCore(int depth)
        {
            if (CurrentBlock.HasTerminator)
            {
                return;
            }

            var scopesToDrop = _scopes
                .Take(Math.Max(0, _scopes.Count - depth))
                .ToArray();
            foreach (var scope in scopesToDrop)
            {
                var locals = scope.Locals.ToArray();
                for (var index = locals.Length - 1; index >= 0; index--)
                {
                    var (name, type) = locals[index];
                    EmitRuntimeDropIfActiveCore(name, type);
                    Emit(MidLevelIrStatementKind.StorageDead, name, name, type);
                }
            }

            if (depth != 0)
            {
                return;
            }

            for (var index = _parameterDropOrder.Count - 1; index >= 0; index--)
            {
                var name = _parameterDropOrder[index];
                if (_parametersByName.TryGetValue(name, out var parameter))
                {
                    EmitRuntimeDropIfActiveCore(name, parameter.Type);
                }
            }
        }

        private void EmitAssignmentCore(LoweredAssignment assignment)
        {
            if (assignment.WriteKind != MemoryWriteKind.Initialization
                && assignment.ReplacesWholeValue
                && assignment.TargetName is not null)
            {
                EmitRuntimeDropIfActiveCore(assignment.TargetName, assignment.TargetType);
            }

            if (assignment.Address is not null)
            {
                Emit(
                    MidLevelIrStatementKind.StoreIndirect,
                    assignment.Text,
                    targetType: assignment.TargetType,
                    value: new MidLevelIrUseRValue(assignment.ResultValue),
                    address: assignment.Address,
                    writeKind: assignment.WriteKind);
                if (assignment.DynamicLengthUpdate is not null)
                {
                    EmitDynamicStorageLengthUpdateCore(assignment.DynamicLengthUpdate, assignment.Text);
                }

                RecordMoveFromOperandCore(assignment.ResultValue, assignment.TargetType);
                return;
            }

            Emit(
                MidLevelIrStatementKind.Assign,
                assignment.Text,
                assignment.TargetName,
                assignment.TargetType,
                value: assignment.DirectValue,
                writeKind: assignment.WriteKind);
            if (assignment.ReplacesWholeValue
                && assignment.TargetName is not null)
            {
                SetRuntimeDropStateCore(assignment.TargetName, isActive: true);
            }

            RecordMoveFromOperandCore(assignment.ResultValue, assignment.TargetType);
        }

        private void EmitDynamicStorageLengthUpdateCore(DynamicStorageLengthUpdate update, string text)
        {
            var lengthType = NonNegativeI64Type;
            var lengthAddress = EmitTemporary(
                new MidLevelIrFieldAddressRValue(
                    update.StorageAddress,
                    update.StorageType,
                    "Length",
                    1,
                    AddressType(lengthType, isMutable: true),
                    $"{update.StorageAddress.Text}.Length"),
                "addr");
            if (lengthAddress is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Dynamic storage initialization for '{text}' could not address the owner length.");
            }

            var index = CoerceOperand(update.InitializedIndex, lengthType) ?? update.InitializedIndex;
            var initializedLength = EmitTemporary(
                new MidLevelIrBinaryRValue(
                    MidLevelIrBinaryOperator.Add,
                    index,
                    new MidLevelIrIntegerConstantOperand(BigInteger.One, lengthType),
                    lengthType,
                    $"{update.InitializedIndex.Text} + 1"),
                "dynamic_len");
            if (initializedLength is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Dynamic storage initialization for '{text}' could not compute the initialized length.");
            }

            EmitDynamicStorageLengthCommitCore(
                new MidLevelIrDynamicStorageLengthCommit(
                    update.StorageAddress,
                    update.StorageType,
                    initializedLength),
                text);
        }

        private void EmitDynamicStorageLengthCommitCore(MidLevelIrDynamicStorageLengthCommit commit, string text)
        {
            var lengthType = NonNegativeI64Type;
            var lengthAddress = EmitTemporary(
                new MidLevelIrFieldAddressRValue(
                    commit.StorageAddress,
                    commit.StorageType,
                    "Length",
                    1,
                    AddressType(lengthType, isMutable: true),
                    $"{commit.StorageAddress.Text}.Length"),
                "addr");
            if (lengthAddress is null)
            {
                throw LoweringInvariantViolation(
                    null,
                    $"Dynamic storage length commit for '{text}' could not address the owner length.");
            }

            var initializedLength = CoerceOperand(commit.InitializedLength, lengthType) ?? commit.InitializedLength;
            Emit(
                MidLevelIrStatementKind.StoreIndirect,
                $"{text}: length",
                targetType: lengthType,
                value: new MidLevelIrUseRValue(initializedLength),
                address: lengthAddress);
        }

        private sealed class RuntimeDropLowerer
        {
            private readonly FunctionMirBuilder _builder;

            public RuntimeDropLowerer(FunctionMirBuilder builder)
            {
                _builder = builder;
            }

            public void InitializeRuntimeDropState(string name, StarkTypeSymbol type, bool isActive)
            {
                _builder.InitializeRuntimeDropStateCore(name, type, isActive);
            }

            public void SetRuntimeDropState(string name, bool isActive)
            {
                _builder.SetRuntimeDropStateCore(name, isActive);
            }

            public void RecordMoveFromOperand(MidLevelIrOperand? operand, StarkTypeSymbol destinationType)
            {
                _builder.RecordMoveFromOperandCore(operand, destinationType);
            }

            public bool RequiresRuntimeDrop(StarkTypeSymbol type)
            {
                return _builder.RequiresRuntimeDropCore(type);
            }

            public bool TryGetEnumLayout(StarkTypeSymbol type, out EnumLayoutSymbol layout)
            {
                return _builder.TryGetEnumLayoutCore(type, out layout);
            }

            public void EmitStorageDead(ScopeFrame scope)
            {
                _builder.EmitStorageDeadCore(scope);
            }

            public void EmitStorageDeadBeyondDepth(int depth)
            {
                _builder.EmitStorageDeadBeyondDepthCore(depth);
            }

            public void EmitAssignment(LoweredAssignment assignment)
            {
                _builder.EmitAssignmentCore(assignment);
            }

            public void EmitOnceClosureEnvironmentCleanup()
            {
                _builder.EmitOnceClosureEnvironmentCleanupCore();
            }
        }
    }
}
