namespace Stark.Compiler;

internal sealed class SsaLowerer
{
    public SsaIrModule Lower(MidLevelIrModule mir)
    {
        var functions = mir.Functions
            .Select(LowerFunction)
            .ToArray();

        return new SsaIrModule(mir.ModuleName, functions);
    }

    private static SsaFunction LowerFunction(MidLevelIrFunction function)
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
                []);
        }

        var builder = new FunctionSsaBuilder(function);
        return builder.Lower();
    }

    private sealed class FunctionSsaBuilder
    {
        private readonly MidLevelIrFunction _function;
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
        private readonly Dictionary<string, SsaValueReference> _parameterValues;
        private int _nextValueId;

        public FunctionSsaBuilder(MidLevelIrFunction function)
        {
            _function = function;
            _sourceBlocks = function.Blocks.ToDictionary(static block => block.Id);
            _successors = BuildSuccessors(function.Blocks);
            _reachableOrder = ComputeReachableOrder(function.EntryBlockId, function.Blocks, _successors);
            _predecessors = BuildPredecessors(_reachableOrder, _successors);
            _variableTypes = function.Parameters
                .Select(static parameter => KeyValuePair.Create(parameter.Name, parameter.Type))
                .Concat(function.Locals.Select(static local => KeyValuePair.Create(local.Name, local.Type)))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
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

            return new SsaFunction(
                _function.Name,
                _function.ReturnType,
                _function.Parameters,
                _function.HasBody,
                SupportsDirectCodeGeneration: true,
                _function.EntryBlockId,
                _reachableOrder.Select(blockId => _blocks[blockId].Build()).ToArray());
        }

        private void LowerBlock(int blockId)
        {
            var source = _sourceBlocks[blockId];
            var target = _blocks[blockId];

            foreach (var statement in source.Statements)
            {
                LowerStatement(blockId, target, statement);
            }

            target.Terminator = LowerTerminator(blockId, target, source.Terminator);
        }

        private void LowerStatement(int blockId, SsaBlockBuilder block, MidLevelIrStatement statement)
        {
            switch (statement.Kind)
            {
                case MidLevelIrStatementKind.StorageLive:
                    if (statement.TargetName is not null
                        && statement.TargetType is not null
                        && _addressableLocals.Contains(statement.TargetName))
                    {
                        block.Instructions.Add(new SsaAllocateLocalInstruction(statement.TargetName, statement.TargetType));
                    }

                    return;
                case MidLevelIrStatementKind.StorageDead:
                    return;
                case MidLevelIrStatementKind.Assign:
                    if (statement.TargetName is null || statement.TargetType is null || statement.Value is null)
                    {
                        throw new InvalidOperationException($"MIR assignment '{statement.Text}' is missing typed information.");
                    }

                    var assignedValue = LowerRValue(blockId, block, statement.Value);
                    if (_addressableLocals.Contains(statement.TargetName))
                    {
                        block.Instructions.Add(new SsaStoreLocalInstruction(statement.TargetName, statement.TargetType, assignedValue));
                    }
                    else if (_variableTypes.ContainsKey(statement.TargetName))
                    {
                        WriteVariable(blockId, statement.TargetName, assignedValue);
                    }
                    else
                    {
                        block.Instructions.Add(new SsaStoreGlobalInstruction(statement.TargetName, statement.TargetType, assignedValue));
                    }

                    return;
                case MidLevelIrStatementKind.StoreIndirect:
                    if (statement.Address is null || statement.TargetType is null || statement.Value is null)
                    {
                        throw new InvalidOperationException($"MIR indirect store '{statement.Text}' is missing typed information.");
                    }

                    block.Instructions.Add(new SsaStoreIndirectInstruction(
                        LowerOperand(blockId, block, statement.Address),
                        statement.TargetType,
                        LowerRValue(blockId, block, statement.Value)));
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

        private SsaTerminator LowerTerminator(int blockId, SsaBlockBuilder block, MidLevelIrTerminator terminator)
        {
            return terminator.Kind switch
            {
                MidLevelIrTerminatorKind.Goto => new SsaTerminator(SsaTerminatorKind.Goto, terminator.Targets),
                MidLevelIrTerminatorKind.Branch => new SsaTerminator(
                    SsaTerminatorKind.Branch,
                    terminator.Targets,
                    Condition: terminator.Condition is null ? null : LowerOperand(blockId, block, terminator.Condition)),
                MidLevelIrTerminatorKind.Return => new SsaTerminator(
                    SsaTerminatorKind.Return,
                    terminator.Targets,
                    Value: terminator.Value is null ? null : LowerOperand(blockId, block, terminator.Value)),
                MidLevelIrTerminatorKind.Unreachable => new SsaTerminator(SsaTerminatorKind.Unreachable, terminator.Targets),
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
                    DefaultTarget: terminator.DefaultTarget),
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
                MidLevelIrCallRValue call => EmitValue(block, new SsaCallRValue(
                    call.FunctionName,
                    call.Arguments.Select(argument => LowerOperand(blockId, block, argument)).ToArray(),
                    call.Type,
                    call.Text)),
                MidLevelIrConvertRValue convert => EmitValue(block, new SsaConvertRValue(
                    LowerOperand(blockId, block, convert.Operand),
                    convert.TargetType,
                    convert.Text)),
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
                MidLevelIrLoadSliceElementRValue loadSlice => EmitValue(block, new SsaLoadSliceElementRValue(
                    LowerOperand(blockId, block, loadSlice.Slice),
                    LowerOperand(blockId, block, loadSlice.Index),
                    loadSlice.Type,
                    loadSlice.Text)),
                MidLevelIrAddressOfLocalRValue addressOfLocal => EmitValue(block, new SsaAddressOfLocalRValue(
                    addressOfLocal.LocalName,
                    addressOfLocal.PointeeType,
                    addressOfLocal.Type,
                    addressOfLocal.Text)),
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

        private SsaValue LowerOperand(int blockId, SsaBlockBuilder block, MidLevelIrOperand operand)
        {
            return operand switch
            {
                MidLevelIrLocalOperand local when _addressableLocals.Contains(local.Name)
                    => EmitValue(block, new SsaLoadLocalRValue(local.Name, local.Type)),
                MidLevelIrLocalOperand local => ReadVariable(blockId, local.Name, local.Type),
                MidLevelIrParameterOperand parameter => ReadVariable(blockId, parameter.Name, parameter.Type),
                MidLevelIrGlobalOperand global => EmitValue(block, new SsaLoadGlobalRValue(global.Name, global.Type)),
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
            var name = $"v{_nextValueId++}";
            block.Instructions.Add(new SsaValueInstruction(name, value));
            return new SsaValueReference(name, value.Type);
        }

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
            var created = new PhiBuilder(blockId, name, type, result);
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

        private sealed class PhiBuilder
        {
            public PhiBuilder(int blockId, string variableName, StarkTypeSymbol type, SsaValueReference result)
            {
                BlockId = blockId;
                VariableName = variableName;
                Type = type;
                Result = result;
            }

            public int BlockId { get; }

            public string VariableName { get; }

            public StarkTypeSymbol Type { get; }

            public SsaValueReference Result { get; }

            public List<SsaPhiIncoming> Incomings { get; } = [];

            public SsaPhi Build()
            {
                return new SsaPhi(Result.Name, VariableName, Type, Incomings.ToArray());
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

            public SsaBasicBlock Build()
            {
                return new SsaBasicBlock(
                    Id,
                    Label,
                    Phis.Select(static phi => phi.Build()).ToArray(),
                    Instructions.ToArray(),
                    Terminator ?? new SsaTerminator(SsaTerminatorKind.Unreachable, []));
            }
        }
    }
}
