namespace Stark.Compiler;

internal sealed class SsaLowerer
{
    private readonly Dictionary<string, TypedFunctionSignature> _signatures;
    private readonly IReadOnlyDictionary<string, TypedGlobalSymbol> _globals;

    public SsaLowerer()
        : this(typeModel: null)
    {
    }

    public SsaLowerer(TypeCheckModel? typeModel)
    {
        _signatures = typeModel is null
            ? new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal)
            : new Dictionary<string, TypedFunctionSignature>(typeModel.Functions, StringComparer.Ordinal);
        _globals = typeModel is null
            ? new Dictionary<string, TypedGlobalSymbol>(StringComparer.Ordinal)
            : new Dictionary<string, TypedGlobalSymbol>(typeModel.Globals, StringComparer.Ordinal);
    }

    public SsaIrModule Lower(MidLevelIrModule mir)
    {
        foreach (var function in mir.Functions)
        {
            _signatures.TryAdd(
                function.Name,
                new TypedFunctionSignature(
                    function.Name,
                    function.ReturnType,
                    function.Parameters,
                    SourceName: function.Name));
        }

        var functions = mir.Functions
            .Select(LowerFunction)
            .ToArray();

        return new SsaIrModule(mir.ModuleName, functions, mir.AddressTakenFunctions);
    }

    private SsaFunction LowerFunction(MidLevelIrFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count == 0)
        {
            return new SsaFunction(
                function.Name,
                function.ReturnType,
                function.Parameters,
                function.HasBody,
                SupportsDirectCodeGeneration: false,
                function.EntryBlockId,
                [],
                function.BodyLoweringKind,
                function.Location);
        }

        var builder = new FunctionSsaBuilder(function, _signatures, _globals);
        return builder.Lower();
    }

    private sealed class FunctionSsaBuilder
    {
        private readonly MidLevelIrFunction _function;
        private readonly IReadOnlyDictionary<string, TypedFunctionSignature> _signatures;
        private readonly IReadOnlyDictionary<string, TypedGlobalSymbol> _globals;
        private readonly Dictionary<int, MidLevelIrBasicBlock> _sourceBlocks;
        private readonly IReadOnlyList<int> _reachableOrder;
        private readonly Dictionary<int, List<int>> _predecessors;
        private readonly Dictionary<int, List<int>> _successors;
        private readonly Dictionary<int, Dictionary<string, SsaValue>> _definitions = new();
        private readonly Dictionary<int, List<PhiBuilder>> _incompletePhis = new();
        private readonly Dictionary<(int BlockId, string VariableName), PhiBuilder> _phis = new();
        private readonly Dictionary<int, SsaBlockBuilder> _blocks = new();
        private readonly HashSet<int> _sealed = [];
        private readonly HashSet<int> _processed = [];
        private readonly Dictionary<string, StarkTypeSymbol> _variableTypes;
        private readonly HashSet<string> _addressableLocals;
        private readonly IReadOnlyDictionary<string, MidLevelIrLocal> _localsByName;
        private readonly Dictionary<string, SsaValueReference> _parameterValues;
        private readonly Dictionary<string, SsaValue> _sharedValueNumbers = new(StringComparer.Ordinal);
        private Dictionary<string, SsaValue>? _currentValueNumbers;
        private SourceLocation? _currentSourceLocation;
        private IReadOnlyList<ScopedNoAliasGroup>? _currentScopedNoAliasGroups;
        private IReadOnlyList<string>? _currentLoopAccessGroups;
        private int _nextValueId;

        public FunctionSsaBuilder(
            MidLevelIrFunction function,
            IReadOnlyDictionary<string, TypedFunctionSignature> signatures,
            IReadOnlyDictionary<string, TypedGlobalSymbol> globals)
        {
            _function = function;
            _signatures = signatures;
            _globals = globals;
            _sourceBlocks = function.Blocks.ToDictionary(static block => block.Id);
            _successors = BuildSuccessors(function.Blocks);
            _reachableOrder = ComputeReachableOrder(function.EntryBlockId, function.Blocks, _successors);
            _predecessors = BuildPredecessors(_reachableOrder, _successors);
            _variableTypes = function.Parameters
                .Select(static parameter => KeyValuePair.Create(parameter.Name, parameter.Type))
                .Concat(function.Locals.Select(static local => KeyValuePair.Create(local.Name, local.Type)))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            _localsByName = function.Locals.ToDictionary(static local => local.Name, StringComparer.Ordinal);
            _addressableLocals = function.Locals
                .Where(static local => local.IsAddressable)
                .Select(static local => local.Name)
                .ToHashSet(StringComparer.Ordinal);
            _parameterValues = function.Parameters.ToDictionary(
                static parameter => parameter.Name,
                static parameter => new SsaValueReference($"arg_{parameter.Name}", parameter.Type),
                StringComparer.Ordinal);

            foreach (var blockId in _reachableOrder)
            {
                _definitions[blockId] = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
                _incompletePhis[blockId] = [];
                _blocks[blockId] = new SsaBlockBuilder(_sourceBlocks[blockId].Id, _sourceBlocks[blockId].Label);
            }
        }

        public SsaFunction Lower()
        {
            foreach (var blockId in _reachableOrder)
            {
                if (CanSeal(blockId))
                {
                    SealBlock(blockId);
                }

                LowerBlock(blockId);
                _processed.Add(blockId);

                foreach (var successor in _successors.GetValueOrDefault(blockId, []))
                {
                    if (_blocks.ContainsKey(successor) && CanSeal(successor))
                    {
                        SealBlock(successor);
                    }
                }
            }

            foreach (var blockId in _reachableOrder)
            {
                SealBlock(blockId);
            }

            var phiReplacements = ComputeTrivialPhiReplacements();
            var trampolineRedirects = ComputeTrampolineRedirects();
            var targetCache = new Dictionary<int, int>();
            var predecessorCache = new Dictionary<int, int>();

            return new SsaFunction(
                _function.Name,
                _function.ReturnType,
                _function.Parameters,
                _function.HasBody,
                SupportsDirectCodeGeneration: true,
                _function.EntryBlockId,
                _reachableOrder
                    .Where(blockId => !trampolineRedirects.ContainsKey(blockId))
                    .Select(blockId => _blocks[blockId].Build(
                        phiReplacements,
                        blockId => ResolveCollapsedTarget(blockId, trampolineRedirects, targetCache),
                        blockId => ResolveCollapsedPredecessor(blockId, trampolineRedirects, predecessorCache)))
                    .ToArray(),
                _function.BodyLoweringKind,
                _function.Location);
        }

        private void LowerBlock(int blockId)
        {
            _currentValueNumbers = new Dictionary<string, SsaValue>(StringComparer.Ordinal);

            try
            {
                var source = _sourceBlocks[blockId];
                var target = _blocks[blockId];

                foreach (var statement in source.Statements)
                {
                    LowerStatement(blockId, target, statement);
                }

                target.Terminator = LowerTerminator(blockId, target, source.Terminator);
            }
            finally
            {
                _currentValueNumbers = null;
            }
        }

        private void LowerStatement(int blockId, SsaBlockBuilder block, MidLevelIrStatement statement)
        {
            var previousSourceLocation = _currentSourceLocation;
            var previousScopedNoAliasGroups = _currentScopedNoAliasGroups;
            var previousLoopAccessGroups = _currentLoopAccessGroups;
            _currentSourceLocation = statement.Location ?? _function.Location;
            _currentScopedNoAliasGroups = statement.ScopedNoAliasGroups;
            _currentLoopAccessGroups = statement.LoopAccessGroups;

            try
            {
            switch (statement.Kind)
            {
                case MidLevelIrStatementKind.StorageLive:
                    if (statement.TargetName is not null
                        && statement.TargetType is not null
                        && _addressableLocals.Contains(statement.TargetName))
                    {
                        var storageClass = GetLocalStorageClass(statement.TargetName);
                        block.Instructions.Add(new SsaAllocateLocalInstruction(
                            statement.TargetName,
                            statement.TargetType,
                            storageClass,
                            statement.Location ?? _function.Location,
                            IsOnceInitializedReadonlyLocal(statement.TargetName, storageClass),
                            LocalHasConstProvenance(statement.TargetName)));
                        if (UsesStackLifetime(storageClass))
                        {
                            block.Instructions.Add(new SsaLifetimeStartInstruction(statement.TargetName, statement.TargetType, statement.Location ?? _function.Location));
                        }
                    }

                    return;
                case MidLevelIrStatementKind.StorageDead:
                    if (statement.TargetName is not null
                        && statement.TargetType is not null
                        && _addressableLocals.Contains(statement.TargetName))
                    {
                        var storageClass = GetLocalStorageClass(statement.TargetName);
                        if (UsesStackLifetime(storageClass))
                        {
                            block.Instructions.Add(new SsaLifetimeEndInstruction(statement.TargetName, statement.TargetType, statement.Location ?? _function.Location));
                        }
                        else if (storageClass == "heap")
                        {
                            block.Instructions.Add(new SsaDeallocateLocalInstruction(statement.TargetName, statement.TargetType, storageClass, statement.Location ?? _function.Location));
                        }
                    }

                    return;
                case MidLevelIrStatementKind.Assign:
                    if (statement.TargetName is null || statement.TargetType is null || statement.Value is null)
                    {
                        throw new InvalidOperationException($"MIR assignment '{statement.Text}' is missing typed information.");
                    }

                    if (_addressableLocals.Contains(statement.TargetName)
                        && TryLowerAggregateCopy(
                            blockId,
                            block,
                            statement.TargetType,
                            statement.Value,
                            destinationAddressFactory: () => CreateLocalAddress(block, statement.TargetName, statement.TargetType),
                            out var aggregateCopy,
                            out var assignmentMovedSource))
                    {
                        block.Instructions.Add(aggregateCopy);
                        InvalidateMovedAggregateSource(blockId, block, assignmentMovedSource, statement.TargetName);
                        return;
                    }

                    var assignedValue = LowerRValue(blockId, block, statement.Value);
                    if (_addressableLocals.Contains(statement.TargetName))
                    {
                        block.Instructions.Add(new SsaStoreLocalInstruction(statement.TargetName, statement.TargetType, assignedValue, statement.Location ?? _function.Location));
                    }
                    else if (_variableTypes.ContainsKey(statement.TargetName))
                    {
                        WriteVariable(blockId, statement.TargetName, assignedValue);
                    }
                    else
                    {
                        block.Instructions.Add(new SsaStoreGlobalInstruction(statement.TargetName, statement.TargetType, assignedValue, statement.Location ?? _function.Location));
                    }

                    InvalidateConsumedAggregateValue(blockId, block, statement.TargetType, statement.Value, statement.TargetName);
                    return;
                case MidLevelIrStatementKind.StoreIndirect:
                    if (statement.Address is null || statement.TargetType is null || statement.Value is null)
                    {
                        throw new InvalidOperationException($"MIR indirect store '{statement.Text}' is missing typed information.");
                    }

                    if (TryLowerAggregateCopy(
                            blockId,
                            block,
                            statement.TargetType,
                            statement.Value,
                            destinationAddressFactory: () => LowerOperand(blockId, block, statement.Address),
                            out var indirectAggregateCopy,
                            out var indirectMovedSource))
                    {
                        block.Instructions.Add(indirectAggregateCopy);
                        InvalidateMovedAggregateSource(blockId, block, indirectMovedSource);
                        return;
                    }

                    block.Instructions.Add(new SsaStoreIndirectInstruction(
                        LowerOperand(blockId, block, statement.Address),
                        statement.TargetType,
                        LowerRValue(blockId, block, statement.Value),
                        statement.Location ?? _function.Location,
                        statement.ScopedNoAliasGroups,
                        statement.LoopAccessGroups));
                    InvalidateConsumedAggregateValue(blockId, block, statement.TargetType, statement.Value);
                    return;
                case MidLevelIrStatementKind.Evaluate:
                    if (statement.Value is not null)
                    {
                        _ = LowerRValue(blockId, block, statement.Value);
                    }

                    return;
                default:
                    throw new InvalidOperationException($"Unsupported MIR statement kind '{statement.Kind}'.");
            }
            }
            finally
            {
                _currentSourceLocation = previousSourceLocation;
                _currentScopedNoAliasGroups = previousScopedNoAliasGroups;
                _currentLoopAccessGroups = previousLoopAccessGroups;
            }
        }

        private SsaTerminator LowerTerminator(int blockId, SsaBlockBuilder block, MidLevelIrTerminator terminator)
        {
            return terminator.Kind switch
            {
                MidLevelIrTerminatorKind.Goto => new SsaTerminator(
                    SsaTerminatorKind.Goto,
                    terminator.Targets,
                    Location: terminator.Location ?? _function.Location,
                    LoopContracts: terminator.LoopContracts,
                    LoopAccessGroups: terminator.LoopAccessGroups),
                MidLevelIrTerminatorKind.Branch => new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    terminator.Targets,
                    Condition: terminator.Condition is null ? null : LowerOperand(blockId, block, terminator.Condition),
                    Location: terminator.Location ?? _function.Location,
                    BranchWeights: terminator.BranchWeights,
                    LoopContracts: terminator.LoopContracts,
                    LoopAccessGroups: terminator.LoopAccessGroups),
                MidLevelIrTerminatorKind.Return => new SsaTerminator(
                    SsaTerminatorKind.Return,
                    terminator.Targets,
                    Value: terminator.Value is null ? null : LowerOperand(blockId, block, terminator.Value),
                    Location: terminator.Location ?? _function.Location),
                MidLevelIrTerminatorKind.Unreachable => new SsaTerminator(SsaTerminatorKind.Unreachable, terminator.Targets, Location: terminator.Location ?? _function.Location),
                MidLevelIrTerminatorKind.Switch => new SsaTerminator(
                    SsaTerminatorKind.Switch,
                    terminator.Targets,
                    Condition: terminator.Condition is null ? null : LowerOperand(blockId, block, terminator.Condition),
                    SwitchCases: terminator.SwitchCases?
                        .Where(static switchCase => switchCase.MatchValue is not null && !switchCase.IsDefault)
                        .Select(switchCase => new SsaSwitchCase(
                            switchCase.Label,
                            switchCase.TargetBlockId,
                            LowerOperand(blockId, block, switchCase.MatchValue!)))
                        .ToArray(),
                    DefaultTarget: terminator.DefaultTarget,
                    Location: terminator.Location ?? _function.Location,
                    BranchWeights: terminator.BranchWeights,
                    LoopContracts: terminator.LoopContracts,
                    LoopAccessGroups: terminator.LoopAccessGroups),
                _ => throw new InvalidOperationException($"Unsupported MIR terminator kind '{terminator.Kind}'.")
            };
        }

        private SsaValue LowerRValue(int blockId, SsaBlockBuilder block, MidLevelIrRValue value)
        {
            return value switch
            {
                MidLevelIrUseRValue use => LowerOperand(blockId, block, use.Operand),
                MidLevelIrUnaryRValue unary => EmitValue(block, new SsaUnaryRValue(
                    MapUnaryOperator(unary.Operator),
                    LowerOperand(blockId, block, unary.Operand),
                    unary.Type,
                    unary.Text)),
                MidLevelIrBinaryRValue binary => EmitValue(block, new SsaBinaryRValue(
                    MapBinaryOperator(binary.Operator),
                    LowerOperand(blockId, block, binary.Left),
                    LowerOperand(blockId, block, binary.Right),
                    binary.Type,
                    binary.Text)),
                MidLevelIrCallRValue call => LowerCallRValue(blockId, block, call),
                MidLevelIrIndirectCallRValue call => LowerIndirectCallRValue(blockId, block, call),
                MidLevelIrConvertRValue convert => LowerConvertRValue(blockId, block, convert),
                MidLevelIrExtractFieldRValue extract => EmitValue(block, new SsaExtractFieldRValue(
                    LowerOperand(blockId, block, extract.Target),
                    extract.FieldName,
                    extract.FieldIndex,
                    extract.Type,
                    extract.Text)),
                MidLevelIrInsertFieldRValue insert => EmitValue(block, new SsaInsertFieldRValue(
                    LowerOperand(blockId, block, insert.Target),
                    insert.FieldName,
                    insert.FieldIndex,
                    LowerOperand(blockId, block, insert.Value),
                    insert.Type,
                    insert.Text)),
                MidLevelIrExtractIndexRValue extractIndex => EmitValue(block, new SsaExtractIndexRValue(
                    LowerOperand(blockId, block, extractIndex.Target),
                    extractIndex.ElementIndex,
                    extractIndex.Type,
                    extractIndex.Text)),
                MidLevelIrInsertIndexRValue insertIndex => EmitValue(block, new SsaInsertIndexRValue(
                    LowerOperand(blockId, block, insertIndex.Target),
                    insertIndex.ElementIndex,
                    LowerOperand(blockId, block, insertIndex.Value),
                    insertIndex.Type,
                    insertIndex.Text)),
                MidLevelIrMakeSliceFromLocalRValue makeSlice => EmitValue(block, new SsaMakeSliceFromLocalRValue(
                    makeSlice.LocalName,
                    makeSlice.SourceType,
                    makeSlice.Type,
                    makeSlice.Text)),
                MidLevelIrMakeSliceFromPointerRValue makeSlice => EmitValue(block, new SsaMakeSliceFromPointerRValue(
                    LowerOperand(blockId, block, makeSlice.Pointer),
                    LowerOperand(blockId, block, makeSlice.Length),
                    makeSlice.Type,
                    makeSlice.Text)),
                MidLevelIrDynamicStorageAllocationRValue allocation => EmitValue(block, new SsaDynamicStorageAllocationRValue(
                    LowerOperand(blockId, block, allocation.Capacity),
                    allocation.Type,
                    allocation.Text)),
                MidLevelIrDynamicStorageFreeRValue free => EmitValue(block, new SsaDynamicStorageFreeRValue(
                    LowerOperand(blockId, block, free.Storage),
                    free.Text)),
                MidLevelIrDynamicStorageReserveRValue reserve => EmitValue(block, new SsaDynamicStorageReserveRValue(
                    LowerOperand(blockId, block, reserve.StorageAddress),
                    reserve.StorageType,
                    LowerOperand(blockId, block, reserve.AdditionalCapacity),
                    reserve.Text)),
                MidLevelIrDynamicStorageTryReserveRValue reserve => EmitValue(block, new SsaDynamicStorageTryReserveRValue(
                    LowerOperand(blockId, block, reserve.StorageAddress),
                    reserve.StorageType,
                    LowerOperand(blockId, block, reserve.AdditionalCapacity),
                    reserve.Text)),
                MidLevelIrDynamicStorageMoveLastRValue moveLast => EmitValue(block, new SsaDynamicStorageMoveLastRValue(
                    LowerOperand(blockId, block, moveLast.StorageAddress),
                    moveLast.StorageType,
                    moveLast.Type,
                    moveLast.Text)),
                MidLevelIrDynamicStorageMoveAtRValue moveAt => EmitValue(block, new SsaDynamicStorageMoveAtRValue(
                    LowerOperand(blockId, block, moveAt.StorageAddress),
                    moveAt.StorageType,
                    LowerOperand(blockId, block, moveAt.Index),
                    moveAt.Type,
                    moveAt.Text)),
                MidLevelIrLoadSliceElementRValue loadSlice => EmitValue(block, new SsaLoadSliceElementRValue(
                    LowerOperand(blockId, block, loadSlice.Slice),
                    LowerOperand(blockId, block, loadSlice.Index),
                    loadSlice.Type,
                    loadSlice.Text)),
                MidLevelIrTextSliceRValue textSlice => EmitValue(block, new SsaTextSliceRValue(
                    LowerOperand(blockId, block, textSlice.TextValue),
                    LowerOperand(blockId, block, textSlice.Start),
                    LowerOperand(blockId, block, textSlice.Length),
                    textSlice.Type,
                    textSlice.Text)),
                MidLevelIrAddressOfLocalRValue addressOfLocal => EmitValue(block, new SsaAddressOfLocalRValue(
                    addressOfLocal.LocalName,
                    addressOfLocal.PointeeType,
                    addressOfLocal.Type,
                    addressOfLocal.Text)),
                MidLevelIrAddressOfParameterRValue addressOfParameter => EmitValue(block, new SsaAddressOfParameterRValue(
                    addressOfParameter.ParameterName,
                    addressOfParameter.PointeeType,
                    addressOfParameter.Type,
                    addressOfParameter.Text)),
                MidLevelIrFieldAddressRValue fieldAddress => EmitValue(block, new SsaFieldAddressRValue(
                    LowerOperand(blockId, block, fieldAddress.Address),
                    fieldAddress.AggregateType,
                    fieldAddress.FieldName,
                    fieldAddress.FieldIndex,
                    fieldAddress.Type,
                    fieldAddress.Text)),
                MidLevelIrElementAddressRValue elementAddress => EmitValue(block, new SsaElementAddressRValue(
                    LowerOperand(blockId, block, elementAddress.Address),
                    elementAddress.AggregateType,
                    elementAddress.Index is null ? null : LowerOperand(blockId, block, elementAddress.Index),
                    elementAddress.ConstantIndex,
                    elementAddress.Type,
                    elementAddress.Text)),
                MidLevelIrSliceElementAddressRValue sliceElementAddress => EmitValue(block, new SsaSliceElementAddressRValue(
                    LowerOperand(blockId, block, sliceElementAddress.Slice),
                    LowerOperand(blockId, block, sliceElementAddress.Index),
                    sliceElementAddress.Type,
                    sliceElementAddress.Text)),
                MidLevelIrLoadIndirectRValue loadIndirect => EmitValue(block, new SsaLoadIndirectRValue(
                    LowerOperand(blockId, block, loadIndirect.Address),
                    loadIndirect.Type,
                    loadIndirect.Text)),
                _ => throw new InvalidOperationException($"Unsupported MIR rvalue '{value.GetType().Name}'.")
            };
        }

        private SsaValue LowerConvertRValue(int blockId, SsaBlockBuilder block, MidLevelIrConvertRValue convert)
        {
            var convertedOperand = LowerOperand(blockId, block, convert.Operand);
            return convertedOperand.Type == convert.TargetType
                ? convertedOperand
                : EmitValue(block, new SsaConvertRValue(
                    convertedOperand,
                    convert.TargetType,
                    convert.Text));
        }

        private SsaValue LowerCallRValue(int blockId, SsaBlockBuilder block, MidLevelIrCallRValue call)
        {
            var loweredCall = EmitValue(block, new SsaCallRValue(
                call.FunctionName,
                call.Arguments.Select(argument => LowerOperand(blockId, block, argument)).ToArray(),
                call.Type,
                call.Text,
                call.IndirectArgumentLocalNames,
                call.SourceReturnType,
                call.IndirectArgumentAddresses?
                    .Select(address => address is null ? null : LowerOperand(blockId, block, address))
                    .ToArray()));

            InvalidateMovedAggregateCallArguments(blockId, block, call);
            return loweredCall;
        }

        private SsaValue LowerIndirectCallRValue(int blockId, SsaBlockBuilder block, MidLevelIrIndirectCallRValue call)
        {
            return EmitValue(block, new SsaIndirectCallRValue(
                LowerOperand(blockId, block, call.Target),
                call.Arguments.Select(argument => LowerOperand(blockId, block, argument)).ToArray(),
                call.Type,
                call.Text,
                call.SourceReturnType));
        }

        private SsaValue LowerOperand(int blockId, SsaBlockBuilder block, MidLevelIrOperand operand)
        {
            return operand switch
            {
                MidLevelIrLocalOperand local when _addressableLocals.Contains(local.Name)
                    => EmitValue(block, new SsaLoadLocalRValue(local.Name, local.Type)),
                MidLevelIrLocalOperand local => ReadVariable(blockId, local.Name, local.Type),
                MidLevelIrParameterOperand parameter when parameter.Type.BorrowKind != StarkBorrowKind.None
                                                       || parameter.Type.InitializationKind != StarkInitializationKind.None
                    => LoadIndirectParameter(block, parameter),
                MidLevelIrParameterOperand parameter => ReadVariable(blockId, parameter.Name, parameter.Type),
                MidLevelIrGlobalOperand global => EmitValue(block, new SsaLoadGlobalRValue(global.Name, global.Type)),
                MidLevelIrGlobalAddressOperand globalAddress => new SsaGlobalAddressValue(globalAddress.Name, globalAddress.PointeeType, globalAddress.Type),
                MidLevelIrFunctionAddressOperand functionAddress => new SsaFunctionAddressValue(functionAddress.FunctionName, functionAddress.Type),
                MidLevelIrIntegerConstantOperand integer => new SsaIntegerConstant(integer.Value, integer.Type),
                MidLevelIrFloatConstantOperand floating => new SsaFloatConstant(floating.LiteralText, floating.Type),
                MidLevelIrStringConstantOperand text => new SsaStringConstant(text.LiteralText, text.Type),
                MidLevelIrBoolConstantOperand boolean => new SsaBoolConstant(boolean.Value),
                MidLevelIrNullOperand nullValue => new SsaNullConstant(nullValue.Type),
                MidLevelIrZeroInitializerOperand zero => new SsaZeroInitializerValue(zero.Type),
                _ => throw new InvalidOperationException($"Unsupported MIR operand '{operand.GetType().Name}'.")
            };
        }

        private SsaValue EmitValue(SsaBlockBuilder block, SsaRValue value)
        {
            if (_currentValueNumbers is not null
                && TryGetPureValueNumberingKey(value, out var key)
                && _currentValueNumbers.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (TryGetPureValueNumberingKey(value, out var sharedKey)
                && _sharedValueNumbers.TryGetValue(sharedKey, out var sharedExisting))
            {
                return sharedExisting;
            }

            var name = $"v{_nextValueId++}";
            var result = new SsaValueReference(name, value.Type);
            block.Instructions.Add(new SsaValueInstruction(
                name,
                value,
                _currentSourceLocation ?? _function.Location,
                _currentScopedNoAliasGroups,
                _currentLoopAccessGroups));

            if (_currentValueNumbers is not null
                && TryGetPureValueNumberingKey(value, out var emittedKey))
            {
                _currentValueNumbers[emittedKey] = result;
            }

            if (block.Id == _function.EntryBlockId
                && TryGetPureValueNumberingKey(value, out var sharedEmittedKey))
            {
                _sharedValueNumbers[sharedEmittedKey] = result;
            }

            return result;
        }

        private SsaValue LoadIndirectParameter(SsaBlockBuilder block, MidLevelIrParameterOperand parameter)
        {
            var address = EmitValue(block, new SsaAddressOfParameterRValue(
                parameter.Name,
                parameter.Type,
                StarkTypeSymbols.RawPointer(parameter.Type, isMutable: parameter.Type.AccessKind != StarkAccessKind.Frozen),
                $"&{parameter.Name}"));

            return EmitValue(block, new SsaLoadIndirectRValue(address, parameter.Type, $"{parameter.Name}:load"));
        }

        private bool TryLowerAggregateCopy(
            int blockId,
            SsaBlockBuilder block,
            StarkTypeSymbol targetType,
            MidLevelIrRValue value,
            Func<SsaValue> destinationAddressFactory,
            out SsaCopyMemoryInstruction aggregateCopy,
            out AggregateMoveSource? movedSource)
        {
            aggregateCopy = default!;
            movedSource = null;

            if (!SupportsAggregateMemoryCopy(targetType)
                || value is not MidLevelIrUseRValue use
                || !TryGetAggregateCopySourceAddress(blockId, block, targetType, use.Operand, out var sourceAddress))
            {
                return false;
            }

            var transferKind = DetermineAggregateTransferKind(targetType, use.Operand, out movedSource);
            aggregateCopy = new SsaCopyMemoryInstruction(
                destinationAddressFactory(),
                sourceAddress,
                targetType,
                transferKind,
                _currentSourceLocation ?? _function.Location,
                _currentScopedNoAliasGroups,
                _currentLoopAccessGroups);
            return true;
        }

        private bool TryGetAggregateCopySourceAddress(
            int blockId,
            SsaBlockBuilder block,
            StarkTypeSymbol targetType,
            MidLevelIrOperand operand,
            out SsaValue sourceAddress)
        {
            sourceAddress = default!;

            if (operand.Type != targetType)
            {
                return false;
            }

            switch (operand)
            {
                case MidLevelIrLocalOperand local when _addressableLocals.Contains(local.Name):
                    sourceAddress = CreateLocalAddress(block, local.Name, local.Type);
                    return true;
                case MidLevelIrGlobalOperand global:
                    sourceAddress = new SsaGlobalAddressValue(
                        global.Name,
                        global.Type,
                        StarkTypeSymbols.RawPointer(
                            global.Type,
                            _globals.TryGetValue(global.Name, out var globalBinding)
                                ? globalBinding.IsMutable
                                : true));
                    return true;
                case MidLevelIrGlobalAddressOperand globalAddress when globalAddress.PointeeType == targetType:
                    sourceAddress = new SsaGlobalAddressValue(globalAddress.Name, globalAddress.PointeeType, globalAddress.Type);
                    return true;
                default:
                    return false;
            }
        }

        private static bool SupportsAggregateMemoryCopy(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Named or StarkTypeKind.FixedArray;
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

        private static bool ConsumesAssignmentSource(StarkTypeSymbol targetType)
        {
            return IsMoveOnly(targetType);
        }

        private static bool ConsumesCallArgument(StarkTypeSymbol parameterType)
        {
            return parameterType.BorrowKind == StarkBorrowKind.None
                && parameterType.Kind != StarkTypeKind.RawPointer
                && IsMoveOnly(parameterType);
        }

        private SsaMemoryTransferKind DetermineAggregateTransferKind(
            StarkTypeSymbol targetType,
            MidLevelIrOperand sourceOperand,
            out AggregateMoveSource? movedSource)
        {
            movedSource = null;

            if (!ConsumesAssignmentSource(targetType)
                || !TryGetOwnedAggregateMoveSource(targetType, sourceOperand, out var resolvedSource))
            {
                return SsaMemoryTransferKind.Copy;
            }

            movedSource = resolvedSource;
            return SsaMemoryTransferKind.Move;
        }

        private bool TryGetOwnedAggregateMoveSource(
            StarkTypeSymbol expectedType,
            MidLevelIrOperand operand,
            out AggregateMoveSource source)
        {
            switch (operand)
            {
                case MidLevelIrLocalOperand local when local.Type == expectedType:
                    source = new AggregateMoveSource(local.Name, local.Type, _addressableLocals.Contains(local.Name));
                    return true;
                case MidLevelIrParameterOperand parameter when parameter.Type == expectedType:
                    source = new AggregateMoveSource(parameter.Name, parameter.Type, IsAddressable: false);
                    return true;
                default:
                    source = default;
                    return false;
            }
        }

        private void InvalidateConsumedAggregateValue(
            int blockId,
            SsaBlockBuilder block,
            StarkTypeSymbol targetType,
            MidLevelIrRValue value,
            string? destinationLocalName = null)
        {
            if (!SupportsAggregateMemoryCopy(targetType)
                || !ConsumesAssignmentSource(targetType)
                || value is not MidLevelIrUseRValue use
                || !TryGetOwnedAggregateMoveSource(targetType, use.Operand, out var movedSource))
            {
                return;
            }

            InvalidateMovedAggregateSource(blockId, block, movedSource, destinationLocalName);
        }

        private void InvalidateMovedAggregateCallArguments(int blockId, SsaBlockBuilder block, MidLevelIrCallRValue call)
        {
            if (!_signatures.TryGetValue(call.FunctionName, out var signature))
            {
                return;
            }

            var argumentCount = Math.Min(call.Arguments.Count, signature.Parameters.Count);
            for (var index = 0; index < argumentCount; index++)
            {
                var parameterType = signature.Parameters[index].Type;
                if (!SupportsAggregateMemoryCopy(parameterType)
                    || !ConsumesCallArgument(parameterType)
                    || !TryGetOwnedAggregateMoveSource(parameterType, call.Arguments[index], out var movedSource))
                {
                    continue;
                }

                InvalidateMovedAggregateSource(blockId, block, movedSource);
            }
        }

        private void InvalidateMovedAggregateSource(
            int blockId,
            SsaBlockBuilder block,
            AggregateMoveSource? movedSource,
            string? destinationLocalName = null)
        {
            if (movedSource is not { } source
                || (destinationLocalName is not null
                    && string.Equals(source.Name, destinationLocalName, StringComparison.Ordinal)))
            {
                return;
            }

            var undef = new SsaUndefValue(source.Type);
            if (source.IsAddressable)
            {
                block.Instructions.Add(new SsaStoreLocalInstruction(source.Name, source.Type, undef));
                return;
            }

            WriteVariable(blockId, source.Name, undef);
        }

        private SsaValue CreateLocalAddress(SsaBlockBuilder block, string localName, StarkTypeSymbol localType)
        {
            return EmitValue(block, new SsaAddressOfLocalRValue(
                localName,
                localType,
                StarkTypeSymbols.RawPointer(localType, isMutable: true),
                $"&{localName}"));
        }

        private string GetLocalStorageClass(string localName)
        {
            return _localsByName.TryGetValue(localName, out var local)
                ? local.StorageClass
                : "stack";
        }

        private bool IsOnceInitializedReadonlyLocal(string localName, string storageClass)
        {
            return storageClass == "stack"
                && _localsByName.TryGetValue(localName, out var local)
                && !local.IsMutable
                && !local.IsConstant;
        }

        private bool LocalHasConstProvenance(string localName)
        {
            return _localsByName.TryGetValue(localName, out var local)
                && local.HasConstProvenance;
        }

        private static bool UsesStackLifetime(string storageClass) => storageClass == "stack";

        private void WriteVariable(int blockId, string name, SsaValue value)
        {
            _definitions[blockId][name] = value;
        }

        private SsaValue ReadVariable(int blockId, string name, StarkTypeSymbol type)
        {
            if (_definitions[blockId].TryGetValue(name, out var existing))
            {
                return existing;
            }

            if (!_sealed.Contains(blockId))
            {
                return CreateIncompletePhi(blockId, name, type).Result;
            }

            var predecessors = _predecessors.GetValueOrDefault(blockId, []);
            SsaValue value;

            if (predecessors.Count == 0)
            {
                value = GetEntryValue(name, type);
            }
            else if (predecessors.Count == 1)
            {
                value = ReadVariable(predecessors[0], name, type);
            }
            else
            {
                var phi = GetOrCreatePhi(blockId, name, type);
                _definitions[blockId][name] = phi.Result;
                foreach (var predecessor in predecessors)
                {
                    phi.Incomings.Add(new SsaPhiIncoming(predecessor, ReadVariable(predecessor, name, type)));
                }

                value = phi.Result;
            }

            _definitions[blockId][name] = value;
            return value;
        }

        private PhiBuilder CreateIncompletePhi(int blockId, string name, StarkTypeSymbol type)
        {
            var phi = GetOrCreatePhi(blockId, name, type);
            _definitions[blockId][name] = phi.Result;

            if (!_incompletePhis[blockId].Contains(phi))
            {
                _incompletePhis[blockId].Add(phi);
            }

            return phi;
        }

        private PhiBuilder GetOrCreatePhi(int blockId, string name, StarkTypeSymbol type)
        {
            var key = (blockId, name);
            if (_phis.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var result = new SsaValueReference($"v{_nextValueId++}_phi", type);
            var created = new PhiBuilder(blockId, name, type, result, _currentSourceLocation ?? _function.Location);
            _phis[key] = created;
            _blocks[blockId].Phis.Add(created);
            return created;
        }

        private SsaValue GetEntryValue(string name, StarkTypeSymbol type)
        {
            if (_parameterValues.TryGetValue(name, out var parameter))
            {
                return parameter;
            }

            return new SsaUndefValue(type);
        }

        private bool CanSeal(int blockId)
        {
            return _predecessors.GetValueOrDefault(blockId, []).All(_processed.Contains);
        }

        private void SealBlock(int blockId)
        {
            if (_sealed.Contains(blockId))
            {
                return;
            }

            foreach (var phi in _incompletePhis[blockId])
            {
                if (phi.Incomings.Count != 0)
                {
                    continue;
                }

                foreach (var predecessor in _predecessors.GetValueOrDefault(blockId, []))
                {
                    phi.Incomings.Add(new SsaPhiIncoming(predecessor, ReadVariable(predecessor, phi.VariableName, phi.Type)));
                }
            }

            _sealed.Add(blockId);
        }

        private static IReadOnlyList<int> ComputeReachableOrder(
            int entryBlockId,
            IReadOnlyList<MidLevelIrBasicBlock> blocks,
            IReadOnlyDictionary<int, List<int>> successors)
        {
            var byId = blocks.ToDictionary(static block => block.Id);
            var reachable = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(entryBlockId);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!reachable.Add(current))
                {
                    continue;
                }

                foreach (var successor in successors.GetValueOrDefault(current, []))
                {
                    if (byId.ContainsKey(successor))
                    {
                        stack.Push(successor);
                    }
                }
            }

            return blocks
                .Where(block => reachable.Contains(block.Id))
                .Select(static block => block.Id)
                .ToArray();
        }

        private static Dictionary<int, List<int>> BuildSuccessors(IReadOnlyList<MidLevelIrBasicBlock> blocks)
        {
            return blocks.ToDictionary(
                static block => block.Id,
                static block => block.Terminator.Targets.Distinct().ToList());
        }

        private static Dictionary<int, List<int>> BuildPredecessors(
            IReadOnlyList<int> reachableOrder,
            IReadOnlyDictionary<int, List<int>> successors)
        {
            var predecessors = reachableOrder.ToDictionary(static blockId => blockId, static _ => new List<int>());

            foreach (var blockId in reachableOrder)
            {
                foreach (var successor in successors.GetValueOrDefault(blockId, []))
                {
                    if (predecessors.TryGetValue(successor, out var list))
                    {
                        list.Add(blockId);
                    }
                }
            }

            return predecessors;
        }

        private Dictionary<string, SsaValue> ComputeTrivialPhiReplacements()
        {
            var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
            var changed = true;

            while (changed)
            {
                changed = false;

                foreach (var blockId in _reachableOrder)
                {
                    foreach (var phi in _blocks[blockId].Phis)
                    {
                        if (replacements.ContainsKey(phi.Result.Name))
                        {
                            continue;
                        }

                        var rewrittenIncomings = phi.Incomings
                            .Select(incoming => RewriteValue(incoming.Value, replacements))
                            .ToArray();

                        if (rewrittenIncomings.Length == 0)
                        {
                            continue;
                        }

                        var first = rewrittenIncomings[0];
                        if (!rewrittenIncomings.All(value => EqualityComparer<SsaValue>.Default.Equals(value, first)))
                        {
                            continue;
                        }

                        if (first is SsaValueReference reference
                            && string.Equals(reference.Name, phi.Result.Name, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!replacements.TryGetValue(phi.Result.Name, out var existing)
                            || !EqualityComparer<SsaValue>.Default.Equals(existing, first))
                        {
                            replacements[phi.Result.Name] = first;
                            changed = true;
                        }
                    }
                }
            }

            return replacements;
        }

        private Dictionary<int, int> ComputeTrampolineRedirects()
        {
            var redirects = new Dictionary<int, int>();

            foreach (var blockId in _reachableOrder)
            {
                if (blockId == _function.EntryBlockId)
                {
                    continue;
                }

                if (!_blocks.TryGetValue(blockId, out var block))
                {
                    continue;
                }

                if (block.Phis.Count != 0 || block.Instructions.Count != 0)
                {
                    continue;
                }

                if (block.Terminator is not { Kind: SsaTerminatorKind.Goto, Targets.Count: 1 })
                {
                    continue;
                }

                if (_predecessors.GetValueOrDefault(blockId, []).Count != 1)
                {
                    continue;
                }

                if (_blocks.TryGetValue(block.Terminator.Targets[0], out var targetBlock)
                    && targetBlock.Phis.Count != 0)
                {
                    continue;
                }

                if (block.Terminator.Targets[0] == blockId)
                {
                    continue;
                }

                redirects[blockId] = block.Terminator.Targets[0];
            }

            return redirects;
        }

        private static SsaValue RewriteValue(SsaValue value, IReadOnlyDictionary<string, SsaValue> replacements)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (value is SsaValueReference reference
                && replacements.TryGetValue(reference.Name, out var replacement)
                && seen.Add(reference.Name))
            {
                value = replacement;
            }

            return value;
        }

        private int ResolveCollapsedTarget(
            int blockId,
            IReadOnlyDictionary<int, int> redirects,
            Dictionary<int, int> cache)
        {
            if (cache.TryGetValue(blockId, out var resolved))
            {
                return resolved;
            }

            if (!redirects.TryGetValue(blockId, out var target))
            {
                cache[blockId] = blockId;
                return blockId;
            }

            resolved = ResolveCollapsedTarget(target, redirects, cache);
            cache[blockId] = resolved;
            return resolved;
        }

        private int ResolveCollapsedPredecessor(
            int blockId,
            IReadOnlyDictionary<int, int> redirects,
            Dictionary<int, int> cache)
        {
            if (cache.TryGetValue(blockId, out var resolved))
            {
                return resolved;
            }

            if (!redirects.ContainsKey(blockId))
            {
                cache[blockId] = blockId;
                return blockId;
            }

            var predecessors = _predecessors.GetValueOrDefault(blockId, []);
            if (predecessors.Count != 1)
            {
                cache[blockId] = blockId;
                return blockId;
            }

            resolved = ResolveCollapsedPredecessor(predecessors[0], redirects, cache);
            cache[blockId] = resolved;
            return resolved;
        }

        private static bool TryGetPureValueNumberingKey(SsaRValue value, out string key)
        {
            switch (value)
            {
                case SsaUnaryRValue unary:
                    key = $"unary|{unary.Operator}|{ValueKey(unary.Operand)}|{TypeKey(unary.Type)}";
                    return true;
                case SsaBinaryRValue binary:
                    var left = ValueKey(binary.Left);
                    var right = ValueKey(binary.Right);
                    if (IsCommutative(binary.Operator) && string.CompareOrdinal(right, left) < 0)
                    {
                        (left, right) = (right, left);
                    }

                    key = $"binary|{binary.Operator}|{left}|{right}|{TypeKey(binary.Type)}";
                    return true;
                case SsaConvertRValue convert:
                    key = $"convert|{ValueKey(convert.Operand)}|{TypeKey(convert.TargetType)}";
                    return true;
                case SsaExtractFieldRValue extractField:
                    key = $"extract-field|{ValueKey(extractField.Target)}|{extractField.FieldName}|{extractField.FieldIndex}|{TypeKey(extractField.Type)}";
                    return true;
                case SsaInsertFieldRValue insertField:
                    key = $"insert-field|{ValueKey(insertField.Target)}|{insertField.FieldName}|{insertField.FieldIndex}|{ValueKey(insertField.Value)}|{TypeKey(insertField.Type)}";
                    return true;
                case SsaExtractIndexRValue extractIndex:
                    key = $"extract-index|{ValueKey(extractIndex.Target)}|{extractIndex.ElementIndex}|{TypeKey(extractIndex.Type)}";
                    return true;
                case SsaInsertIndexRValue insertIndex:
                    key = $"insert-index|{ValueKey(insertIndex.Target)}|{insertIndex.ElementIndex}|{ValueKey(insertIndex.Value)}|{TypeKey(insertIndex.Type)}";
                    return true;
                case SsaMakeSliceFromLocalRValue makeSlice:
                    key = $"make-slice|{makeSlice.LocalName}|{TypeKey(makeSlice.SourceType)}|{TypeKey(makeSlice.Type)}";
                    return true;
                case SsaMakeSliceFromPointerRValue makeSlice:
                    key = $"make-slice-ptr|{ValueKey(makeSlice.Pointer)}|{ValueKey(makeSlice.Length)}|{TypeKey(makeSlice.Type)}";
                    return true;
                case SsaTextSliceRValue textSlice:
                    key = $"text-slice|{ValueKey(textSlice.TextValue)}|{ValueKey(textSlice.Start)}|{ValueKey(textSlice.Length)}|{TypeKey(textSlice.Type)}";
                    return true;
                case SsaAddressOfLocalRValue addressOfLocal:
                    key = $"address-of-local|{addressOfLocal.LocalName}|{TypeKey(addressOfLocal.PointeeType)}|{TypeKey(addressOfLocal.Type)}";
                    return true;
                case SsaAddressOfParameterRValue addressOfParameter:
                    key = $"address-of-parameter|{addressOfParameter.ParameterName}|{TypeKey(addressOfParameter.PointeeType)}|{TypeKey(addressOfParameter.Type)}";
                    return true;
                case SsaFieldAddressRValue fieldAddress:
                    key = $"field-address|{ValueKey(fieldAddress.Address)}|{TypeKey(fieldAddress.AggregateType)}|{fieldAddress.FieldName}|{fieldAddress.FieldIndex}|{TypeKey(fieldAddress.Type)}";
                    return true;
                case SsaElementAddressRValue elementAddress:
                    key = $"element-address|{ValueKey(elementAddress.Address)}|{TypeKey(elementAddress.AggregateType)}|{ValueKey(elementAddress.Index)}|{elementAddress.ConstantIndex}|{TypeKey(elementAddress.Type)}";
                    return true;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    key = $"slice-element-address|{ValueKey(sliceElementAddress.Slice)}|{ValueKey(sliceElementAddress.Index)}|{TypeKey(sliceElementAddress.Type)}";
                    return true;
                default:
                    key = string.Empty;
                    return false;
            }
        }

        private static bool IsCommutative(SsaBinaryOperator operatorKind)
        {
            return operatorKind switch
            {
                SsaBinaryOperator.Add => true,
                SsaBinaryOperator.Multiply => true,
                SsaBinaryOperator.WrappingAdd => true,
                SsaBinaryOperator.WrappingMultiply => true,
                SsaBinaryOperator.SaturatingAdd => true,
                SsaBinaryOperator.SaturatingMultiply => true,
                SsaBinaryOperator.BitwiseAnd => true,
                SsaBinaryOperator.BitwiseXor => true,
                SsaBinaryOperator.BitwiseOr => true,
                SsaBinaryOperator.Equal => true,
                SsaBinaryOperator.NotEqual => true,
                _ => false
            };
        }

        private static string ValueKey(SsaValue? value)
        {
            return value switch
            {
                null => "<null>",
                SsaValueReference reference => $"ref:{reference.Name}:{TypeKey(reference.Type)}",
                SsaIntegerConstant integer => $"int:{integer.Value}:{TypeKey(integer.Type)}",
                SsaFloatConstant floating => $"float:{floating.LiteralText}:{TypeKey(floating.Type)}",
                SsaStringConstant text => $"string:{text.LiteralText}:{TypeKey(text.Type)}",
                SsaBoolConstant boolean => $"bool:{boolean.Value}",
                SsaNullConstant nullValue => $"null:{TypeKey(nullValue.Type)}",
                SsaUndefValue undef => $"undef:{TypeKey(undef.Type)}",
                SsaZeroInitializerValue zero => $"zero:{TypeKey(zero.Type)}",
                _ => $"{value.GetType().Name}:{value.Text}:{TypeKey(value.Type)}"
            };
        }

        private static string TypeKey(StarkTypeSymbol type)
        {
            return type.ToString();
        }

        private static SsaUnaryOperator MapUnaryOperator(MidLevelIrUnaryOperator operatorKind)
        {
            return operatorKind switch
            {
                MidLevelIrUnaryOperator.Negate => SsaUnaryOperator.Negate,
                MidLevelIrUnaryOperator.LogicalNot => SsaUnaryOperator.LogicalNot,
                MidLevelIrUnaryOperator.BitwiseNot => SsaUnaryOperator.BitwiseNot,
                _ => throw new InvalidOperationException($"Unsupported MIR unary operator '{operatorKind}'.")
            };
        }

        private static SsaBinaryOperator MapBinaryOperator(MidLevelIrBinaryOperator operatorKind)
        {
            return operatorKind switch
            {
                MidLevelIrBinaryOperator.Add => SsaBinaryOperator.Add,
                MidLevelIrBinaryOperator.Subtract => SsaBinaryOperator.Subtract,
                MidLevelIrBinaryOperator.Multiply => SsaBinaryOperator.Multiply,
                MidLevelIrBinaryOperator.WrappingAdd => SsaBinaryOperator.WrappingAdd,
                MidLevelIrBinaryOperator.WrappingSubtract => SsaBinaryOperator.WrappingSubtract,
                MidLevelIrBinaryOperator.WrappingMultiply => SsaBinaryOperator.WrappingMultiply,
                MidLevelIrBinaryOperator.SaturatingAdd => SsaBinaryOperator.SaturatingAdd,
                MidLevelIrBinaryOperator.SaturatingSubtract => SsaBinaryOperator.SaturatingSubtract,
                MidLevelIrBinaryOperator.SaturatingMultiply => SsaBinaryOperator.SaturatingMultiply,
                MidLevelIrBinaryOperator.Divide => SsaBinaryOperator.Divide,
                MidLevelIrBinaryOperator.Modulo => SsaBinaryOperator.Modulo,
                MidLevelIrBinaryOperator.BitwiseAnd => SsaBinaryOperator.BitwiseAnd,
                MidLevelIrBinaryOperator.BitwiseXor => SsaBinaryOperator.BitwiseXor,
                MidLevelIrBinaryOperator.BitwiseOr => SsaBinaryOperator.BitwiseOr,
                MidLevelIrBinaryOperator.Exponent => SsaBinaryOperator.Exponent,
                MidLevelIrBinaryOperator.ShiftLeft => SsaBinaryOperator.ShiftLeft,
                MidLevelIrBinaryOperator.ShiftRight => SsaBinaryOperator.ShiftRight,
                MidLevelIrBinaryOperator.Equal => SsaBinaryOperator.Equal,
                MidLevelIrBinaryOperator.NotEqual => SsaBinaryOperator.NotEqual,
                MidLevelIrBinaryOperator.LessThan => SsaBinaryOperator.LessThan,
                MidLevelIrBinaryOperator.LessThanOrEqual => SsaBinaryOperator.LessThanOrEqual,
                MidLevelIrBinaryOperator.GreaterThan => SsaBinaryOperator.GreaterThan,
                MidLevelIrBinaryOperator.GreaterThanOrEqual => SsaBinaryOperator.GreaterThanOrEqual,
                _ => throw new InvalidOperationException($"Unsupported MIR binary operator '{operatorKind}'.")
            };
        }

        private readonly record struct AggregateMoveSource(
            string Name,
            StarkTypeSymbol Type,
            bool IsAddressable);

        private sealed class PhiBuilder
        {
            public PhiBuilder(int blockId, string variableName, StarkTypeSymbol type, SsaValueReference result, SourceLocation? location)
            {
                BlockId = blockId;
                VariableName = variableName;
                Type = type;
                Result = result;
                Location = location;
            }

            public int BlockId { get; }

            public string VariableName { get; }

            public StarkTypeSymbol Type { get; }

            public SsaValueReference Result { get; }

            public List<SsaPhiIncoming> Incomings { get; } = [];

            public SourceLocation? Location { get; }

            public SsaPhi Build(
                IReadOnlyDictionary<string, SsaValue> replacements,
                Func<int, int> resolveTarget,
                Func<int, int> resolvePredecessor)
            {
                return new SsaPhi(
                    Result.Name,
                    VariableName,
                    Type,
                    Incomings.Select(incoming => new SsaPhiIncoming(
                        resolvePredecessor(incoming.PredecessorBlockId),
                        RewriteValue(incoming.Value, replacements))).ToArray(),
                    Location);
            }
        }

        private sealed class SsaBlockBuilder
        {
            public SsaBlockBuilder(int id, string label)
            {
                Id = id;
                Label = label;
            }

            public int Id { get; }

            public string Label { get; }

            public List<PhiBuilder> Phis { get; } = [];

            public List<SsaInstruction> Instructions { get; } = [];

            public SsaTerminator? Terminator { get; set; }

            public SsaBasicBlock Build(
                IReadOnlyDictionary<string, SsaValue> replacements,
                Func<int, int> resolveTarget,
                Func<int, int> resolvePredecessor)
            {
                return new SsaBasicBlock(
                    Id,
                    Label,
                    Phis.Where(phi => !replacements.ContainsKey(phi.Result.Name))
                        .Select(phi => phi.Build(replacements, resolveTarget, resolvePredecessor))
                        .ToArray(),
                    Instructions.Select(instruction => RewriteInstruction(instruction, replacements)).ToArray(),
                    RewriteTerminator(
                        Terminator ?? new SsaTerminator(SsaTerminatorKind.Unreachable, []),
                        replacements,
                        resolveTarget));
            }
        }

        private static SsaInstruction RewriteInstruction(
            SsaInstruction instruction,
            IReadOnlyDictionary<string, SsaValue> replacements)
        {
            return instruction switch
            {
                SsaValueInstruction valueInstruction => new SsaValueInstruction(
                    valueInstruction.ResultName,
                    RewriteRValue(valueInstruction.Value, replacements),
                    valueInstruction.Location,
                    valueInstruction.ScopedNoAliasGroups,
                    valueInstruction.LoopAccessGroups),
                SsaAllocateLocalInstruction allocateLocal => allocateLocal,
                SsaLifetimeStartInstruction lifetimeStart => lifetimeStart,
                SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd,
                SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal,
                SsaStoreLocalInstruction storeLocal => new SsaStoreLocalInstruction(
                    storeLocal.LocalName,
                    storeLocal.LocalType,
                    RewriteValue(storeLocal.Value, replacements),
                    storeLocal.Location),
                SsaCopyMemoryInstruction copyMemory => new SsaCopyMemoryInstruction(
                    RewriteValue(copyMemory.DestinationAddress, replacements),
                    RewriteValue(copyMemory.SourceAddress, replacements),
                    copyMemory.CopyType,
                    copyMemory.TransferKind,
                    copyMemory.Location,
                    copyMemory.ScopedNoAliasGroups,
                    copyMemory.LoopAccessGroups),
                SsaStoreIndirectInstruction storeIndirect => new SsaStoreIndirectInstruction(
                    RewriteValue(storeIndirect.Address, replacements),
                    storeIndirect.ValueType,
                    RewriteValue(storeIndirect.Value, replacements),
                    storeIndirect.Location,
                    storeIndirect.ScopedNoAliasGroups,
                    storeIndirect.LoopAccessGroups),
                SsaStoreGlobalInstruction storeGlobal => new SsaStoreGlobalInstruction(
                    storeGlobal.GlobalName,
                    storeGlobal.GlobalType,
                    RewriteValue(storeGlobal.Value, replacements),
                    storeGlobal.Location),
                _ => instruction
            };
        }

        private static SsaRValue RewriteRValue(
            SsaRValue value,
            IReadOnlyDictionary<string, SsaValue> replacements)
        {
            return value switch
            {
                SsaUnaryRValue unary => new SsaUnaryRValue(
                    unary.Operator,
                    RewriteValue(unary.Operand, replacements),
                    unary.Type,
                    unary.Text),
                SsaBinaryRValue binary => new SsaBinaryRValue(
                    binary.Operator,
                    RewriteValue(binary.Left, replacements),
                    RewriteValue(binary.Right, replacements),
                    binary.Type,
                    binary.Text),
                SsaCallRValue call => new SsaCallRValue(
                    call.FunctionName,
                        call.Arguments.Select(argument => RewriteValue(argument, replacements)).ToArray(),
                        call.Type,
                        call.Text,
                        call.IndirectArgumentLocalNames,
                        call.SourceReturnType,
                        call.IndirectArgumentAddresses?
                            .Select(address => address is null ? null : RewriteValue(address, replacements))
                            .ToArray()),
                SsaIndirectCallRValue call => new SsaIndirectCallRValue(
                    RewriteValue(call.Target, replacements),
                    call.Arguments.Select(argument => RewriteValue(argument, replacements)).ToArray(),
                    call.Type,
                    call.Text,
                    call.SourceReturnType),
                SsaConvertRValue convert => new SsaConvertRValue(
                    RewriteValue(convert.Operand, replacements),
                    convert.TargetType,
                    convert.Text),
                SsaExtractFieldRValue extractField => new SsaExtractFieldRValue(
                    RewriteValue(extractField.Target, replacements),
                    extractField.FieldName,
                    extractField.FieldIndex,
                    extractField.Type,
                    extractField.Text),
                SsaInsertFieldRValue insertField => new SsaInsertFieldRValue(
                    RewriteValue(insertField.Target, replacements),
                    insertField.FieldName,
                    insertField.FieldIndex,
                    RewriteValue(insertField.Value, replacements),
                    insertField.Type,
                    insertField.Text),
                SsaExtractIndexRValue extractIndex => new SsaExtractIndexRValue(
                    RewriteValue(extractIndex.Target, replacements),
                    extractIndex.ElementIndex,
                    extractIndex.Type,
                    extractIndex.Text),
                SsaInsertIndexRValue insertIndex => new SsaInsertIndexRValue(
                    RewriteValue(insertIndex.Target, replacements),
                    insertIndex.ElementIndex,
                    RewriteValue(insertIndex.Value, replacements),
                    insertIndex.Type,
                    insertIndex.Text),
                SsaMakeSliceFromLocalRValue makeSlice => makeSlice,
                SsaMakeSliceFromPointerRValue makeSlice => new SsaMakeSliceFromPointerRValue(
                    RewriteValue(makeSlice.Pointer, replacements),
                    RewriteValue(makeSlice.Length, replacements),
                    makeSlice.Type,
                    makeSlice.Text),
                SsaDynamicStorageAllocationRValue allocation => new SsaDynamicStorageAllocationRValue(
                    RewriteValue(allocation.Capacity, replacements),
                    allocation.Type,
                    allocation.Text),
                SsaDynamicStorageFreeRValue free => new SsaDynamicStorageFreeRValue(
                    RewriteValue(free.Storage, replacements),
                    free.Text),
                SsaDynamicStorageReserveRValue reserve => new SsaDynamicStorageReserveRValue(
                    RewriteValue(reserve.StorageAddress, replacements),
                    reserve.StorageType,
                    RewriteValue(reserve.AdditionalCapacity, replacements),
                    reserve.Text),
                SsaDynamicStorageTryReserveRValue reserve => new SsaDynamicStorageTryReserveRValue(
                    RewriteValue(reserve.StorageAddress, replacements),
                    reserve.StorageType,
                    RewriteValue(reserve.AdditionalCapacity, replacements),
                    reserve.Text),
                SsaDynamicStorageMoveLastRValue moveLast => new SsaDynamicStorageMoveLastRValue(
                    RewriteValue(moveLast.StorageAddress, replacements),
                    moveLast.StorageType,
                    moveLast.Type,
                    moveLast.Text),
                SsaDynamicStorageMoveAtRValue moveAt => new SsaDynamicStorageMoveAtRValue(
                    RewriteValue(moveAt.StorageAddress, replacements),
                    moveAt.StorageType,
                    RewriteValue(moveAt.Index, replacements),
                    moveAt.Type,
                    moveAt.Text),
                SsaLoadSliceElementRValue loadSlice => new SsaLoadSliceElementRValue(
                    RewriteValue(loadSlice.Slice, replacements),
                    RewriteValue(loadSlice.Index, replacements),
                    loadSlice.Type,
                    loadSlice.Text),
                SsaTextSliceRValue textSlice => new SsaTextSliceRValue(
                    RewriteValue(textSlice.TextValue, replacements),
                    RewriteValue(textSlice.Start, replacements),
                    RewriteValue(textSlice.Length, replacements),
                    textSlice.Type,
                    textSlice.Text),
                SsaAddressOfLocalRValue addressOfLocal => addressOfLocal,
                SsaAddressOfParameterRValue addressOfParameter => addressOfParameter,
                SsaFieldAddressRValue fieldAddress => new SsaFieldAddressRValue(
                    RewriteValue(fieldAddress.Address, replacements),
                    fieldAddress.AggregateType,
                    fieldAddress.FieldName,
                    fieldAddress.FieldIndex,
                    fieldAddress.Type,
                    fieldAddress.Text),
                SsaElementAddressRValue elementAddress => new SsaElementAddressRValue(
                    RewriteValue(elementAddress.Address, replacements),
                    elementAddress.AggregateType,
                    elementAddress.Index is null ? null : RewriteValue(elementAddress.Index, replacements),
                    elementAddress.ConstantIndex,
                    elementAddress.Type,
                    elementAddress.Text),
                SsaSliceElementAddressRValue sliceElementAddress => new SsaSliceElementAddressRValue(
                    RewriteValue(sliceElementAddress.Slice, replacements),
                    RewriteValue(sliceElementAddress.Index, replacements),
                    sliceElementAddress.Type,
                    sliceElementAddress.Text),
                SsaLoadIndirectRValue loadIndirect => new SsaLoadIndirectRValue(
                    RewriteValue(loadIndirect.Address, replacements),
                    loadIndirect.Type,
                    loadIndirect.Text),
                SsaLoadGlobalRValue loadGlobal => loadGlobal,
                SsaLoadLocalRValue loadLocal => loadLocal,
                _ => value
            };
        }

        private static SsaTerminator RewriteTerminator(
            SsaTerminator terminator,
            IReadOnlyDictionary<string, SsaValue> replacements,
            Func<int, int> resolveTarget)
        {
            return new SsaTerminator(
                terminator.Kind,
                terminator.Targets.Select(resolveTarget).ToArray(),
                Condition: terminator.Condition is null ? null : RewriteValue(terminator.Condition, replacements),
                Value: terminator.Value is null ? null : RewriteValue(terminator.Value, replacements),
                SwitchCases: terminator.SwitchCases?.Select(switchCase => new SsaSwitchCase(
                    switchCase.Label,
                    resolveTarget(switchCase.TargetBlockId),
                    RewriteValue(switchCase.MatchValue, replacements))).ToArray(),
                DefaultTarget: terminator.DefaultTarget is null
                    ? null
                    : resolveTarget(terminator.DefaultTarget.Value),
                Location: terminator.Location,
                BranchWeights: terminator.BranchWeights,
                LoopContracts: terminator.LoopContracts,
                LoopAccessGroups: terminator.LoopAccessGroups);
        }
    }
}
