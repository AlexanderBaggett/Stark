namespace Stark.Compiler;

internal sealed class NonLexicalBorrowLifetimeValidator
{
    private readonly CompilerPassContext _context;
    private readonly MidLevelIrModule _mir;
    private readonly TypeCheckModel _typeModel;
    private readonly OwnershipValidationModel _ownershipModel;
    private readonly Dictionary<string, TypedFunctionSignature> _signatures;

    public NonLexicalBorrowLifetimeValidator(
        CompilerPassContext context,
        MidLevelIrModule mir,
        TypeCheckModel typeModel,
        OwnershipValidationModel ownershipModel)
    {
        _context = context;
        _mir = mir;
        _typeModel = typeModel;
        _ownershipModel = ownershipModel;
        _signatures = new Dictionary<string, TypedFunctionSignature>(typeModel.Functions, StringComparer.Ordinal);
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
    }

    public OwnershipValidationModel Validate()
    {
        var updatedFunctions = new Dictionary<string, FunctionOwnershipSummary>(_ownershipModel.Functions, StringComparer.Ordinal);

        foreach (var function in _mir.Functions)
        {
            if (!function.HasBody
                || function.Blocks.Count == 0
                || !_ownershipModel.Functions.TryGetValue(function.Name, out var existingSummary))
            {
                continue;
            }

            var hasConflicts = ValidateFunction(function);
            if (!hasConflicts)
            {
                continue;
            }

            updatedFunctions[function.Name] = existingSummary with
            {
                OwnershipValid = false
            };
        }

        return new OwnershipValidationModel(_ownershipModel.ModuleName, updatedFunctions);
    }

    private bool ValidateFunction(MidLevelIrFunction function)
    {
        var borrowLocals = function.Locals
            .Where(static local => local.Type.BorrowKind != StarkBorrowKind.None)
            .ToDictionary(static local => local.Name, static local => local.Type, StringComparer.Ordinal);
        if (borrowLocals.Count == 0)
        {
            return false;
        }

        var localMap = function.Locals.ToDictionary(static local => local.Name, StringComparer.Ordinal);
        var parameterMap = function.Parameters.ToDictionary(static parameter => parameter.Name, StringComparer.Ordinal);
        var blocks = function.Blocks.ToDictionary(static block => block.Id);
        var predecessors = BuildPredecessors(function.Blocks);
        var liveness = ComputeBorrowLocalLiveness(function, borrowLocals.Keys);
        var entryStates = new Dictionary<int, BorrowEnvironment>();
        var exitStates = new Dictionary<int, BorrowEnvironment>();

        foreach (var block in function.Blocks)
        {
            entryStates[block.Id] = new BorrowEnvironment();
            exitStates[block.Id] = new BorrowEnvironment();
        }

        var worklist = new Queue<int>(function.Blocks.Select(static block => block.Id));
        var enqueued = new HashSet<int>(function.Blocks.Select(static block => block.Id));

        while (worklist.Count > 0)
        {
            var blockId = worklist.Dequeue();
            enqueued.Remove(blockId);

            var incoming = MergeIncomingBorrowEnvironments(
                blockId == function.EntryBlockId ? [] : predecessors[blockId].Select(id => exitStates[id]),
                liveness.LiveIn[blockId]);

            var outgoing = TransferBlock(
                blocks[blockId],
                incoming,
                liveness,
                borrowLocals,
                emitDiagnostics: false,
                localMap,
                parameterMap,
                function.ReturnType,
                function.Name,
                emittedDiagnostics: null);

            var entryChanged = !BorrowEnvironment.AreEquivalent(entryStates[blockId], incoming);
            var exitChanged = !BorrowEnvironment.AreEquivalent(exitStates[blockId], outgoing.Environment);
            if (!entryChanged && !exitChanged)
            {
                continue;
            }

            entryStates[blockId] = incoming;
            exitStates[blockId] = outgoing.Environment;

            foreach (var successor in blocks[blockId].Terminator.Targets)
            {
                if (enqueued.Add(successor))
                {
                    worklist.Enqueue(successor);
                }
            }
        }

        var emittedDiagnostics = new HashSet<string>(StringComparer.Ordinal);
        var hasConflicts = false;

        foreach (var block in function.Blocks)
        {
            var finalState = TransferBlock(
                block,
                entryStates[block.Id],
                liveness,
                borrowLocals,
                emitDiagnostics: true,
                localMap,
                parameterMap,
                function.ReturnType,
                function.Name,
                emittedDiagnostics);
            hasConflicts |= finalState.HadConflicts;
        }

        return hasConflicts;
    }

    private BorrowTransferResult TransferBlock(
        MidLevelIrBasicBlock block,
        BorrowEnvironment entry,
        BorrowLocalLiveness liveness,
        IReadOnlyDictionary<string, StarkTypeSymbol> borrowLocals,
        bool emitDiagnostics,
        IReadOnlyDictionary<string, MidLevelIrLocal> localMap,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterMap,
        StarkTypeSymbol returnType,
        string functionName,
        ISet<string>? emittedDiagnostics)
    {
        var environment = entry.Clone();
        BorrowEnvironment.FilterToLive(environment, liveness.LiveIn[block.Id]);
        var hadConflicts = false;

        for (var index = 0; index < block.Statements.Count; index++)
        {
            var statement = block.Statements[index];
            hadConflicts |= CheckStatementForConflicts(
                statement,
                environment,
                localMap,
                parameterMap,
                functionName,
                emitDiagnostics,
                emittedDiagnostics);

            ApplyBorrowDefinition(statement, environment, borrowLocals);
            BorrowEnvironment.FilterToLive(environment, liveness.LiveAfterStatements[(block.Id, index)]);
        }

        hadConflicts |= CheckTerminatorForConflicts(
            block.Terminator,
            environment,
            parameterMap,
            returnType,
            functionName,
            emitDiagnostics,
            emittedDiagnostics);

        BorrowEnvironment.FilterToLive(environment, liveness.LiveOut[block.Id]);
        return new BorrowTransferResult(environment, hadConflicts);
    }

    private bool CheckStatementForConflicts(
        MidLevelIrStatement statement,
        BorrowEnvironment environment,
        IReadOnlyDictionary<string, MidLevelIrLocal> localMap,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterMap,
        string functionName,
        bool emitDiagnostics,
        ISet<string>? emittedDiagnostics)
    {
        var hadConflicts = false;

        if (statement.Kind == MidLevelIrStatementKind.Assign
            && statement.TargetName is { } targetName
            && localMap.TryGetValue(targetName, out var local)
            && local.StorageClass != "temp"
            && IsMoveOnly(local.Type))
        {
            hadConflicts |= ReportOwnerConflict(
                environment,
                OwnerSource.Local(targetName),
                "overwrite",
                $"{functionName}|assign-overwrite|{targetName}|{statement.Text}",
                emitDiagnostics,
                emittedDiagnostics);
        }

        foreach (var (source, action, key) in EnumerateConsumedOwnerSources(statement, localMap, parameterMap, functionName))
        {
            hadConflicts |= ReportOwnerConflict(environment, source, action, key, emitDiagnostics, emittedDiagnostics);
        }

        return hadConflicts;
    }

    private bool CheckTerminatorForConflicts(
        MidLevelIrTerminator terminator,
        BorrowEnvironment environment,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterMap,
        StarkTypeSymbol returnType,
        string functionName,
        bool emitDiagnostics,
        ISet<string>? emittedDiagnostics)
    {
        if (terminator.Kind != MidLevelIrTerminatorKind.Return
            || terminator.Value is null
            || !IsMoveOnly(returnType)
            || !TryResolveOwnerSource(terminator.Value, out var owner))
        {
            return false;
        }

        return ReportOwnerConflict(
            environment,
            owner,
            "return",
            $"{functionName}|return|{owner.Kind}|{owner.Name}",
            emitDiagnostics,
            emittedDiagnostics);
    }

    private bool ReportOwnerConflict(
        BorrowEnvironment environment,
        OwnerSource owner,
        string action,
        string diagnosticKey,
        bool emitDiagnostics,
        ISet<string>? emittedDiagnostics)
    {
        var conflictingBorrows = environment.FindBorrowsForOwner(owner);
        if (conflictingBorrows.Count == 0)
        {
            return false;
        }

        if (emitDiagnostics)
        {
            var borrowList = string.Join(", ", conflictingBorrows.OrderBy(static name => name, StringComparer.Ordinal).Select(name => $"'{name}'"));
            if (emittedDiagnostics is null || emittedDiagnostics.Add($"{diagnosticKey}|{borrowList}"))
            {
                _context.Diagnostics.Error(
                    "STK4201",
                    $"Borrow error: cannot {DescribeOwnerAction(owner.Name, action)} while safe borrow {borrowList} is still live. The borrow ends only after its last proven use.",
                    "borrow-liveness");
            }
        }

        return true;
    }

    private IEnumerable<(OwnerSource Source, string Action, string DiagnosticKey)> EnumerateConsumedOwnerSources(
        MidLevelIrStatement statement,
        IReadOnlyDictionary<string, MidLevelIrLocal> localMap,
        IReadOnlyDictionary<string, TypedParameterSymbol> parameterMap,
        string functionName)
    {
        if (statement.Kind == MidLevelIrStatementKind.Assign
            && statement.TargetType is { } targetType
            && IsMoveOnly(targetType)
            && statement.Value is not null)
        {
            if (TryResolveConsumedOwnerSource(statement.Value, out var assignmentSource))
            {
                yield return (assignmentSource, "move", $"{functionName}|assign-move|{assignmentSource.Kind}|{assignmentSource.Name}|{statement.Text}");
            }
        }

        if (statement.Value is MidLevelIrCallRValue call
            && _signatures.TryGetValue(call.FunctionName, out var callee))
        {
            var argumentCount = Math.Min(call.Arguments.Count, callee.Parameters.Count);
            for (var index = 0; index < argumentCount; index++)
            {
                var parameterType = callee.Parameters[index].Type;
                if (parameterType.BorrowKind != StarkBorrowKind.None || !IsMoveOnly(parameterType))
                {
                    continue;
                }

                if (!TryResolveOwnerSource(call.Arguments[index], out var owner))
                {
                    continue;
                }

                yield return (
                    owner,
                    $"move into call '{call.FunctionName}'",
                    $"{functionName}|call-move|{call.FunctionName}|{index}|{owner.Kind}|{owner.Name}");
            }
        }
    }

    private void ApplyBorrowDefinition(
        MidLevelIrStatement statement,
        BorrowEnvironment environment,
        IReadOnlyDictionary<string, StarkTypeSymbol> borrowLocals)
    {
        if (statement.Kind != MidLevelIrStatementKind.Assign
            || statement.TargetName is not { } targetName
            || !borrowLocals.ContainsKey(targetName))
        {
            return;
        }

        var sources = InferBorrowSources(statement.Value, environment);
        environment.Set(targetName, sources.Count == 0 ? [BorrowSource.Unknown(targetName)] : sources);
    }

    private HashSet<BorrowSource> InferBorrowSources(MidLevelIrRValue? value, BorrowEnvironment environment)
    {
        if (value is null)
        {
            return [];
        }

        switch (value)
        {
            case MidLevelIrUseRValue use:
                return ResolveOperandSources(use.Operand, environment);
            case MidLevelIrConvertRValue convert:
                return ResolveOperandSources(convert.Operand, environment);
            case MidLevelIrExtractFieldRValue extract:
                return ProjectFieldSources(ResolveOperandSources(extract.Target, environment), extract.FieldName);
            case MidLevelIrCallRValue call when call.Type.BorrowKind != StarkBorrowKind.None:
                return InferCallBorrowSources(call, environment);
            default:
                return [];
        }
    }

    private HashSet<BorrowSource> InferCallBorrowSources(MidLevelIrCallRValue call, BorrowEnvironment environment)
    {
        if (!_signatures.TryGetValue(call.FunctionName, out var signature))
        {
            return [BorrowSource.Unknown(call.FunctionName)];
        }

        var sources = new HashSet<BorrowSource>();
        var argumentCount = Math.Min(call.Arguments.Count, signature.Parameters.Count);
        for (var index = 0; index < argumentCount; index++)
        {
            if (signature.Parameters[index].Type.BorrowKind == StarkBorrowKind.None)
            {
                continue;
            }

            sources.UnionWith(ResolveOperandSources(call.Arguments[index], environment));
        }

        return sources.Count == 0 ? [BorrowSource.Unknown(call.FunctionName)] : sources;
    }

    private HashSet<BorrowSource> ResolveOperandSources(MidLevelIrOperand operand, BorrowEnvironment environment)
    {
        return operand switch
        {
            MidLevelIrLocalOperand local when local.Type.BorrowKind != StarkBorrowKind.None =>
                environment.Get(local.Name),
            MidLevelIrLocalOperand local =>
                [BorrowSource.Local(local.Name)],
            MidLevelIrParameterOperand parameter when parameter.Type.BorrowKind != StarkBorrowKind.None =>
                [BorrowSource.External(parameter.Name)],
            MidLevelIrParameterOperand parameter =>
                [BorrowSource.Parameter(parameter.Name)],
            MidLevelIrGlobalOperand global when global.Type.BorrowKind != StarkBorrowKind.None =>
                [BorrowSource.External(global.Name)],
            MidLevelIrGlobalOperand global =>
                [BorrowSource.Global(global.Name)],
            _ => []
        };
    }

    private static HashSet<BorrowSource> ProjectFieldSources(IEnumerable<BorrowSource> sources, string fieldName)
    {
        return sources
            .Select(source => source with { TopLevelField = source.TopLevelField ?? fieldName })
            .ToHashSet();
    }

    private static bool TryResolveOwnerSource(MidLevelIrOperand operand, out OwnerSource owner)
    {
        switch (operand)
        {
            case MidLevelIrLocalOperand local:
                owner = OwnerSource.Local(local.Name);
                return true;
            case MidLevelIrParameterOperand parameter:
                owner = OwnerSource.Parameter(parameter.Name);
                return true;
            default:
                owner = default;
                return false;
        }
    }

    private static bool TryResolveConsumedOwnerSource(MidLevelIrRValue value, out OwnerSource owner)
    {
        switch (value)
        {
            case MidLevelIrUseRValue use:
                return TryResolveOwnerSource(use.Operand, out owner);
            case MidLevelIrExtractFieldRValue extract:
                return TryResolveOwnerSource(extract.Target, out owner);
            case MidLevelIrConvertRValue convert:
                return TryResolveOwnerSource(convert.Operand, out owner);
            default:
                owner = default;
                return false;
        }
    }

    private static BorrowLocalLiveness ComputeBorrowLocalLiveness(
        MidLevelIrFunction function,
        IEnumerable<string> borrowLocalNames)
    {
        var borrowLocals = new HashSet<string>(borrowLocalNames, StringComparer.Ordinal);
        var blocks = function.Blocks.ToDictionary(static block => block.Id);
        var successors = function.Blocks.ToDictionary(
            static block => block.Id,
            static block => block.Terminator.Targets.ToArray());
        var liveIn = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal));
        var liveOut = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new HashSet<string>(StringComparer.Ordinal));
        var liveBeforeTerminator = new Dictionary<int, HashSet<string>>();
        var liveAfterStatements = new Dictionary<(int BlockId, int StatementIndex), HashSet<string>>();

        var changed = true;
        while (changed)
        {
            changed = false;

            for (var blockIndex = function.Blocks.Count - 1; blockIndex >= 0; blockIndex--)
            {
                var block = function.Blocks[blockIndex];
                var newOut = new HashSet<string>(StringComparer.Ordinal);
                foreach (var successor in successors[block.Id])
                {
                    newOut.UnionWith(liveIn[successor]);
                }

                var current = new HashSet<string>(newOut, StringComparer.Ordinal);
                foreach (var used in CollectBorrowLocalUses(block.Terminator, borrowLocals))
                {
                    current.Add(used);
                }

                liveBeforeTerminator[block.Id] = new HashSet<string>(current, StringComparer.Ordinal);

                for (var statementIndex = block.Statements.Count - 1; statementIndex >= 0; statementIndex--)
                {
                    liveAfterStatements[(block.Id, statementIndex)] = new HashSet<string>(current, StringComparer.Ordinal);

                    var statement = block.Statements[statementIndex];
                    if (statement.Kind == MidLevelIrStatementKind.Assign
                        && statement.TargetName is { } targetName
                        && borrowLocals.Contains(targetName))
                    {
                        current.Remove(targetName);
                    }

                    foreach (var used in CollectBorrowLocalUses(statement, borrowLocals))
                    {
                        current.Add(used);
                    }
                }

                if (!liveOut[block.Id].SetEquals(newOut))
                {
                    liveOut[block.Id] = newOut;
                    changed = true;
                }

                if (!liveIn[block.Id].SetEquals(current))
                {
                    liveIn[block.Id] = current;
                    changed = true;
                }
            }
        }

        return new BorrowLocalLiveness(liveIn, liveOut, liveBeforeTerminator, liveAfterStatements);
    }

    private static IEnumerable<string> CollectBorrowLocalUses(MidLevelIrStatement statement, IReadOnlySet<string> borrowLocals)
    {
        if (statement.Address is not null)
        {
            foreach (var name in CollectBorrowLocalUses(statement.Address, borrowLocals))
            {
                yield return name;
            }
        }

        if (statement.Value is not null)
        {
            foreach (var name in CollectBorrowLocalUses(statement.Value, borrowLocals))
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<string> CollectBorrowLocalUses(MidLevelIrTerminator terminator, IReadOnlySet<string> borrowLocals)
    {
        if (terminator.Condition is not null)
        {
            foreach (var name in CollectBorrowLocalUses(terminator.Condition, borrowLocals))
            {
                yield return name;
            }
        }

        if (terminator.Value is not null)
        {
            foreach (var name in CollectBorrowLocalUses(terminator.Value, borrowLocals))
            {
                yield return name;
            }
        }

        if (terminator.SwitchCases is null)
        {
            yield break;
        }

        foreach (var switchCase in terminator.SwitchCases)
        {
            if (switchCase.MatchValue is null)
            {
                continue;
            }

            foreach (var name in CollectBorrowLocalUses(switchCase.MatchValue, borrowLocals))
            {
                yield return name;
            }
        }
    }

    private static IEnumerable<string> CollectBorrowLocalUses(MidLevelIrRValue value, IReadOnlySet<string> borrowLocals)
    {
        switch (value)
        {
            case MidLevelIrUseRValue use:
                foreach (var name in CollectBorrowLocalUses(use.Operand, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrUnaryRValue unary:
                foreach (var name in CollectBorrowLocalUses(unary.Operand, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrBinaryRValue binary:
                foreach (var name in CollectBorrowLocalUses(binary.Left, borrowLocals))
                {
                    yield return name;
                }

                foreach (var name in CollectBorrowLocalUses(binary.Right, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    foreach (var name in CollectBorrowLocalUses(argument, borrowLocals))
                    {
                        yield return name;
                    }
                }

                yield break;

            case MidLevelIrConvertRValue convert:
                foreach (var name in CollectBorrowLocalUses(convert.Operand, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrExtractFieldRValue extractField:
                foreach (var name in CollectBorrowLocalUses(extractField.Target, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrInsertFieldRValue insertField:
                foreach (var name in CollectBorrowLocalUses(insertField.Target, borrowLocals))
                {
                    yield return name;
                }

                foreach (var name in CollectBorrowLocalUses(insertField.Value, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrExtractIndexRValue extractIndex:
                foreach (var name in CollectBorrowLocalUses(extractIndex.Target, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrInsertIndexRValue insertIndex:
                foreach (var name in CollectBorrowLocalUses(insertIndex.Target, borrowLocals))
                {
                    yield return name;
                }

                foreach (var name in CollectBorrowLocalUses(insertIndex.Value, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrLoadSliceElementRValue loadSlice:
                foreach (var name in CollectBorrowLocalUses(loadSlice.Slice, borrowLocals))
                {
                    yield return name;
                }

                foreach (var name in CollectBorrowLocalUses(loadSlice.Index, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrFieldAddressRValue fieldAddress:
                foreach (var name in CollectBorrowLocalUses(fieldAddress.Address, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrElementAddressRValue elementAddress:
                foreach (var name in CollectBorrowLocalUses(elementAddress.Address, borrowLocals))
                {
                    yield return name;
                }

                if (elementAddress.Index is not null)
                {
                    foreach (var name in CollectBorrowLocalUses(elementAddress.Index, borrowLocals))
                    {
                        yield return name;
                    }
                }

                yield break;

            case MidLevelIrSliceElementAddressRValue sliceAddress:
                foreach (var name in CollectBorrowLocalUses(sliceAddress.Slice, borrowLocals))
                {
                    yield return name;
                }

                foreach (var name in CollectBorrowLocalUses(sliceAddress.Index, borrowLocals))
                {
                    yield return name;
                }

                yield break;

            case MidLevelIrLoadIndirectRValue loadIndirect:
                foreach (var name in CollectBorrowLocalUses(loadIndirect.Address, borrowLocals))
                {
                    yield return name;
                }

                yield break;
        }
    }

    private static IEnumerable<string> CollectBorrowLocalUses(MidLevelIrOperand operand, IReadOnlySet<string> borrowLocals)
    {
        if (operand is MidLevelIrLocalOperand local
            && local.Type.BorrowKind != StarkBorrowKind.None
            && borrowLocals.Contains(local.Name))
        {
            yield return local.Name;
        }
    }

    private static Dictionary<int, int[]> BuildPredecessors(IReadOnlyList<MidLevelIrBasicBlock> blocks)
    {
        var predecessors = blocks.ToDictionary(
            static block => block.Id,
            static _ => new List<int>());

        foreach (var block in blocks)
        {
            foreach (var target in block.Terminator.Targets)
            {
                predecessors[target].Add(block.Id);
            }
        }

        return predecessors.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray());
    }

    private static BorrowEnvironment MergeIncomingBorrowEnvironments(
        IEnumerable<BorrowEnvironment> incomingStates,
        IReadOnlySet<string> liveAtBlockEntry)
    {
        var merged = new BorrowEnvironment();
        foreach (var incoming in incomingStates)
        {
            merged.UnionWith(incoming);
        }

        BorrowEnvironment.FilterToLive(merged, liveAtBlockEntry);
        return merged;
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
            StarkTypeKind.Null => false,
            _ => true
        };
    }

    private static string DescribeOwnerAction(string ownerName, string action)
    {
        return action switch
        {
            "overwrite" => $"overwrite '{ownerName}'",
            "return" => $"return '{ownerName}' by move",
            _ when action.StartsWith("move into call ", StringComparison.Ordinal) => $"move '{ownerName}' {action["move ".Length..]}",
            _ => $"move '{ownerName}'"
        };
    }

    private sealed record BorrowTransferResult(BorrowEnvironment Environment, bool HadConflicts);

    private sealed record BorrowLocalLiveness(
        IReadOnlyDictionary<int, HashSet<string>> LiveIn,
        IReadOnlyDictionary<int, HashSet<string>> LiveOut,
        IReadOnlyDictionary<int, HashSet<string>> LiveBeforeTerminator,
        IReadOnlyDictionary<(int BlockId, int StatementIndex), HashSet<string>> LiveAfterStatements);

    private sealed class BorrowEnvironment
    {
        private readonly Dictionary<string, HashSet<BorrowSource>> _sources = new(StringComparer.Ordinal);

        public BorrowEnvironment()
        {
        }

        private BorrowEnvironment(Dictionary<string, HashSet<BorrowSource>> sources)
        {
            _sources = sources;
        }

        public BorrowEnvironment Clone()
        {
            return new BorrowEnvironment(_sources.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToHashSet(),
                StringComparer.Ordinal));
        }

        public HashSet<BorrowSource> Get(string name)
        {
            return _sources.TryGetValue(name, out var sources)
                ? sources.ToHashSet()
                : [];
        }

        public void Set(string name, IEnumerable<BorrowSource> sources)
        {
            var set = sources.Where(static source => source.Kind != BorrowSourceKind.None).ToHashSet();
            if (set.Count == 0)
            {
                _sources.Remove(name);
                return;
            }

            _sources[name] = set;
        }

        public void UnionWith(BorrowEnvironment other)
        {
            foreach (var (name, sources) in other._sources)
            {
                if (!_sources.TryGetValue(name, out var existing))
                {
                    _sources[name] = sources.ToHashSet();
                    continue;
                }

                existing.UnionWith(sources);
            }
        }

        public IReadOnlyList<string> FindBorrowsForOwner(OwnerSource owner)
        {
            return _sources
                .Where(pair => pair.Value.Any(source => source.RootName == owner.Name && source.Kind switch
                {
                    BorrowSourceKind.Local when owner.Kind == OwnerSourceKind.Local => true,
                    BorrowSourceKind.Parameter when owner.Kind == OwnerSourceKind.Parameter => true,
                    _ => false
                }))
                .Select(static pair => pair.Key)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }

        public static void FilterToLive(BorrowEnvironment environment, IReadOnlySet<string> liveBorrows)
        {
            foreach (var name in environment._sources.Keys.ToArray())
            {
                if (!liveBorrows.Contains(name))
                {
                    environment._sources.Remove(name);
                }
            }
        }

        public static bool AreEquivalent(BorrowEnvironment left, BorrowEnvironment right)
        {
            if (left._sources.Count != right._sources.Count)
            {
                return false;
            }

            foreach (var (name, sources) in left._sources)
            {
                if (!right._sources.TryGetValue(name, out var otherSources) || !sources.SetEquals(otherSources))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private enum BorrowSourceKind
    {
        None,
        Local,
        Parameter,
        Global,
        External,
        Unknown
    }

    private sealed record BorrowSource(BorrowSourceKind Kind, string RootName, string? TopLevelField = null)
    {
        public static BorrowSource Local(string name) => new(BorrowSourceKind.Local, name);

        public static BorrowSource Parameter(string name) => new(BorrowSourceKind.Parameter, name);

        public static BorrowSource Global(string name) => new(BorrowSourceKind.Global, name);

        public static BorrowSource External(string name) => new(BorrowSourceKind.External, name);

        public static BorrowSource Unknown(string name) => new(BorrowSourceKind.Unknown, name);
    }

    private enum OwnerSourceKind
    {
        Local,
        Parameter
    }

    private readonly record struct OwnerSource(OwnerSourceKind Kind, string Name)
    {
        public static OwnerSource Local(string name) => new(OwnerSourceKind.Local, name);

        public static OwnerSource Parameter(string name) => new(OwnerSourceKind.Parameter, name);
    }
}
