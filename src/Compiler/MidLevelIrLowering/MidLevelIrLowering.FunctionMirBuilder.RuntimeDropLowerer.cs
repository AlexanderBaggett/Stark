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

        private bool RequiresRuntimeDropCore(StarkTypeSymbol type)
        {
            return RequiresRuntimeDropCore(type, new HashSet<string>(StringComparer.Ordinal));
        }

        private bool RequiresRuntimeDropCore(StarkTypeSymbol type, HashSet<string> visiting)
        {
            if (type.BorrowKind != StarkBorrowKind.None)
            {
                return false;
            }

            if (type.Kind == StarkTypeKind.FixedArray)
            {
                return type.ElementType is not null
                    && RequiresRuntimeDropCore(type.ElementType, visiting);
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
                            if (RequiresRuntimeDropCore(field.Type, visiting))
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
                    if (RequiresRuntimeDropCore(field.Type, visiting))
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

            var key = StarkTypeSymbols.GetGenericBaseName(type.NamedType);
            return _enumLayoutModel.Layouts.TryGetValue(key, out layout!);
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
            if (!RequiresRuntimeDropCore(type))
            {
                return;
            }

            var temporary = CreateTemporaryLocal(type, "drop");
            EmitOperandAssignment(temporary, operand, operand.Text);

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

        private void EmitStructFieldDropsCore(
            MidLevelIrLocalOperand aggregate,
            StarkTypeSymbol type,
            HashSet<string> visiting)
        {
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
                if (!RequiresRuntimeDropCore(field.Type))
                {
                    continue;
                }

                var fieldValue = LowerKnownFieldAccess(aggregate, field.Name, index, field.Type, field.Name);
                EmitRuntimeDropFromOperandCore(fieldValue, field.Type);
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
                            .Where(field => RequiresRuntimeDropCore(field.Type, visiting))
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
                        var displayName = field.SourceFieldName ?? $"[{field.SourcePosition}]";
                        var fieldValue = LowerKnownFieldAccess(
                            aggregate,
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            field.Type,
                            displayName);
                        EmitRuntimeDropFromOperandCore(fieldValue, field.Type);
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
            var hadAlias = _nameAliases.TryGetValue(aliasName, out var previousAlias);
            _moduleNameOverride = moduleName;
            _activeGenericTypeSubstitution = BuildNamedTypeGenericSubstitution(selfType);
            _nameAliases[aliasName] = localName;
            return new DestructorContext(this, previousModuleName, previousGenericTypeSubstitution, aliasName, previousAlias, hadAlias);
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
            if (assignment.ReplacesWholeValue
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
                    address: assignment.Address);
                RecordMoveFromOperandCore(assignment.ResultValue, assignment.TargetType);
                return;
            }

            Emit(MidLevelIrStatementKind.Assign, assignment.Text, assignment.TargetName, assignment.TargetType, value: assignment.DirectValue);
            if (assignment.ReplacesWholeValue
                && assignment.TargetName is not null)
            {
                SetRuntimeDropStateCore(assignment.TargetName, isActive: true);
            }

            RecordMoveFromOperandCore(assignment.ResultValue, assignment.TargetType);
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
        }
    }
}
