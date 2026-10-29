namespace Stark.Compiler;

/// <summary>
/// Versions an isolated scalar queue drain. The fast loop preserves every
/// dequeue and exit. The entry guard and retained final-empty reset make the
/// wrap reset redundant, including when the last element is at capacity - 1.
/// </summary>
internal sealed class SsaQueueDequeueLoopVersioner
{
    private readonly SpecializationCodegenStrategyModel _strategies;
    private readonly HashSet<string> _dequeueNames;
    private readonly SsaDirectCallInliner _inliner;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> _parameterEffects;
    private Dictionary<string, SsaFunction> _functions = null!;
    private readonly Dictionary<(string, int), bool> _noCapture = [];
    private readonly Dictionary<string, bool> _readOnly = new(StringComparer.Ordinal);

    public SsaQueueDequeueLoopVersioner(SpecializationCodegenStrategyModel strategies, FunctionEffectModel effects,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>>? parameterEffects = null)
    {
        _strategies = strategies;
        _dequeueNames = strategies.Functions.Where(s => s.TemplateName == "System.Collections.Queue.TryDequeue"
            && s.DeclaringModuleName == "System.Collections"
            && s.StrategyKind != FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly)
            .Select(s => s.SymbolName).ToHashSet(StringComparer.Ordinal);
        _parameterEffects = parameterEffects ?? new Dictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>>(StringComparer.Ordinal);
        var functions = new Dictionary<string, FunctionEffectProfile>(effects.Functions, StringComparer.Ordinal);
        foreach (var strategy in strategies.Functions)
        {
            if (functions.TryGetValue(strategy.TemplateName, out var template))
                functions.TryAdd(strategy.SymbolName, template with { Name = strategy.SymbolName });
        }
        _inliner = new SsaDirectCallInliner(new FunctionEffectModel(effects.ModuleName, functions),
            new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
    }

    public SsaIrModule Optimize(SsaIrModule module)
    {
        _functions = module.Functions.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _noCapture.Clear();
        _readOnly.Clear();
        var changed = false;
        var functions = module.Functions.Select(function =>
        {
            var result = OptimizeFunction(function);
            changed |= !ReferenceEquals(function, result);
            return result;
        }).ToArray();
        return changed ? module with { Functions = functions } : module;
    }

    private SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count > 128)
        {
            return function;
        }
        if (!function.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>()
            .Any(i => i.Value is SsaCallRValue call && IsDequeueSpecialization(call.FunctionName))) return function;
        var blocks = function.Blocks.ToDictionary(b => b.Id);
        var predecessors = blocks.Keys.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var block in blocks.Values)
        {
            foreach (var target in block.Terminator.Targets) predecessors[target].Add(block.Id);
        }
        var dominators = Dominators(function, predecessors);
        var definitions = Definitions(function);
        var locals = function.Blocks.SelectMany(b => b.Instructions.OfType<SsaAllocateLocalInstruction>()
            .Select(local => (Local: local, Block: b.Id))).ToDictionary(p => p.Local.LocalName, StringComparer.Ordinal);

        foreach (var header in function.Blocks)
        {
            var tails = predecessors[header.Id].Where(id => dominators[id].Contains(header.Id)).ToArray();
            if (tails.Length == 0) continue;
            var loop = new HashSet<int> { header.Id };
            var pending = new Stack<int>(tails);
            while (pending.TryPop(out var id))
            {
                if (loop.Add(id)) foreach (var predecessor in predecessors[id]) pending.Push(predecessor);
            }
            if (loop.Any(id => !dominators[id].Contains(header.Id))) continue;
            var incoming = predecessors[header.Id].Where(id => !loop.Contains(id)).ToArray();
            if (incoming.Length != 1 || blocks[incoming[0]].Terminator is not { Kind: SsaTerminatorKind.Goto, Targets.Count: 1 }) continue;
            var preheader = blocks[incoming[0]];
            if (loop.Any(id => id != header.Id && predecessors[id].Any(p => !loop.Contains(p)))) continue;

            var calls = loop.SelectMany(id => blocks[id].Instructions).OfType<SsaValueInstruction>()
                .Where(i => i.Value is SsaCallRValue call && IsDequeueSpecialization(call.FunctionName)).ToArray();
            if (calls.Length != 1 || calls[0].Value is not SsaCallRValue dequeue
                || !_functions.TryGetValue(dequeue.FunctionName, out var callee)
                || !TryProveDequeue(callee, out var proof)
                || ReceiverLocal(dequeue, definitions) is not { } receiver
                || !locals.TryGetValue(receiver, out var queueLocal)
                || queueLocal.Local.StorageClass != "stack"
                || queueLocal.Local.LocalType.BorrowKind != StarkBorrowKind.None
                || queueLocal.Local.LocalType.InitializationKind != StarkInitializationKind.None
                || queueLocal.Local.LocalType.NamedType != callee.Parameters[0].Type.NamedType
                || !dominators[preheader.Id].Contains(queueLocal.Block)
                || OutputLocal(dequeue, definitions) is not { } output
                || output == receiver || !locals.TryGetValue(output, out var outputLocal)
                || !IsScalar(outputLocal.Local.LocalType)
                || outputLocal.Local.LocalType.BorrowKind != StarkBorrowKind.None
                || outputLocal.Local.LocalType.InitializationKind != StarkInitializationKind.None
                || !dominators[preheader.Id].Contains(outputLocal.Block)) continue;

            var beforeOrInLoop = new HashSet<int>();
            var reverse = new Stack<int>(); reverse.Push(header.Id);
            while (reverse.TryPop(out var id))
                if (beforeOrInLoop.Add(id)) foreach (var predecessor in predecessors[id]) reverse.Push(predecessor);
            if (!HeaderDoesNotEscape(function, receiver, definitions, beforeOrInLoop)) continue;
            if (loop.SelectMany(id => blocks[id].Instructions).Any(instruction => !PreservesLoop(instruction, dequeue, receiver))) continue;
            if (loop.Any(id => blocks[id].Terminator.Kind is SsaTerminatorKind.TailCall or SsaTerminatorKind.Switch)) continue;

            // Clone the continuation through returns as well as the natural loop.
            // This retains exit values without inventing loop live-out values or
            // assuming that the normal loop exit is the only exit.
            var region = Reachable(header.Id, blocks);
            if (region.Contains(preheader.Id)
                || region.Any(id => id != header.Id && predecessors[id].Any(p => !region.Contains(p)))
                || region.Any(id => blocks[id].Terminator.Kind is SsaTerminatorKind.TailCall or SsaTerminatorKind.Switch)
                || region.Sum(id => blocks[id].Instructions.Count) > 384
                || region.SelectMany(id => blocks[id].Instructions).Any(i => i is SsaAllocateLocalInstruction)) continue;
            var result = Version(function, header, preheader, region, loop, dequeue, receiver, output, outputLocal.Local.LocalType, proof);
            if (!ReferenceEquals(result, function)) return result;
        }
        return function;
    }

    private bool IsDequeueSpecialization(string name) => _dequeueNames.Contains(name);

    private bool PreservesLoop(SsaInstruction instruction, SsaCallRValue dequeue, string receiver)
    {
        if (instruction is SsaValueInstruction { Value: SsaCallRValue call })
        {
            return ReferenceEquals(call, dequeue) || IsReadOnly(call.FunctionName, []);
        }
        return instruction switch
        {
            SsaValueInstruction value => IsReadValue(value.Value),
            SsaStoreLocalInstruction store => store.LocalName != receiver && IsScalar(store.LocalType),
            SsaLifetimeStartInstruction or SsaLifetimeEndInstruction => true,
            _ => false
        };
    }

    private bool IsReadOnly(string name, HashSet<string> visiting)
    {
        if (_readOnly.TryGetValue(name, out var known)) return known;
        if (!visiting.Add(name) || !_functions.TryGetValue(name, out var function)
            || !function.HasBody || !function.SupportsDirectCodeGeneration
            || function.BodyLoweringKind != FunctionBodyLoweringKind.StarkCfg) return false;
        var result = function.Blocks.All(block => block.Terminator.Kind != SsaTerminatorKind.TailCall
            && block.Instructions.All(instruction => instruction switch
            {
                SsaValueInstruction { Value: SsaCallRValue call } => IsReadOnly(call.FunctionName, visiting),
                SsaValueInstruction value => IsReadValue(value.Value),
                SsaAllocateLocalInstruction local => IsScalar(local.LocalType),
                SsaStoreLocalInstruction store => IsScalar(store.LocalType),
                SsaLifetimeStartInstruction or SsaLifetimeEndInstruction => true,
                _ => false
            }));
        visiting.Remove(name);
        _readOnly[name] = result;
        return result;
    }

    private bool HeaderDoesNotEscape(SsaFunction function, string local, IReadOnlyDictionary<string, SsaRValue> definitions, HashSet<int> liveBlocks)
    {
        var aliases = PointerAliases(function, value => value is SsaAddressOfLocalRValue address && address.LocalName == local
            || value is SsaLoadLocalRValue load && load.LocalName == local);
        aliases.Add(local);
        foreach (var block in function.Blocks.Where(b => liveBlocks.Contains(b.Id)))
        {
            if (block.Terminator.Value is { } returned && IsAlias(returned, aliases)) return false;
            if (block.Terminator.Kind == SsaTerminatorKind.TailCall) return false;
            foreach (var instruction in block.Instructions)
            {
                if (!CheckEscapes(instruction, aliases, (call, index) =>
                    call.IndirectArgumentLocalNames?.ElementAtOrDefault(index) == local
                    || Resolve(call.Arguments[index], definitions) is SsaLoadLocalRValue load && load.LocalName == local))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private bool DoesNotCapture(string name, int index, HashSet<(string, int)> visiting)
    {
        if (!_functions.TryGetValue(name, out var function) || !function.HasBody || !function.SupportsDirectCodeGeneration
            || function.BodyLoweringKind != FunctionBodyLoweringKind.StarkCfg || index >= function.Parameters.Count) return false;
        // Published/root summaries keep the distinction between reading a queue
        // field and returning its address, including across package projections.
        // A declaration-only foreign function's default summary is not a proof.
        if (_parameterEffects.TryGetValue(name, out var parameters) && index < parameters.Count
            && parameters[index] is { IsMemoryBacked: true, CaptureKind: ParameterCaptureKind.None }) return true;
        var key = (name, index);
        if (_noCapture.TryGetValue(key, out var known)) return known;
        if (!visiting.Add(key)) return false;
        var parameter = function.Parameters[index];
        var aliases = PointerAliases(function, value => value is SsaAddressOfParameterRValue address
            && address.ParameterName == parameter.Name,
            parameter.Name);
        var result = function.Blocks.All(block => block.Terminator.Kind != SsaTerminatorKind.TailCall
            && (block.Terminator.Value is null || !IsAlias(block.Terminator.Value, aliases))
            && block.Instructions.All(instruction => CheckEscapes(instruction, aliases,
                (call, argument) => call.IndirectArgumentLocalNames?.ElementAtOrDefault(argument) == parameter.Name, visiting)));
        visiting.Remove(key);
        _noCapture[key] = result;
        return result;
    }

    private bool CheckEscapes(SsaInstruction instruction, HashSet<string> aliases,
        Func<ISsaDirectCallOperation, int, bool> indirectRoot, HashSet<(string, int)>? visiting = null)
    {
        ISsaDirectCallOperation? call = instruction switch
        {
            SsaValueInstruction { Value: SsaCallRValue value } => value,
            SsaCallInstruction statement => statement,
            _ => null
        };
        if (call is not null)
        {
            for (var i = 0; i < call.Arguments.Count; i++)
            {
                // A temporary aggregate can contain a boxed header address.
                // This proof models header references, not arbitrary embedded
                // pointer graphs; do not reinterpret that value as a receiver.
                if (IsAlias(call.Arguments[i], aliases) && call.Arguments[i].Type.Kind == StarkTypeKind.Named
                    && !indirectRoot(call, i)) return false;
                if ((indirectRoot(call, i) || IsAlias(call.Arguments[i], aliases)
                    || call.IndirectArgumentAddresses?.ElementAtOrDefault(i) is { } address && IsAlias(address, aliases))
                    && !DoesNotCapture(call.FunctionName, i, visiting ?? []))
                {
                    return false;
                }
            }
            return true;
        }
        return instruction switch
        {
            SsaStoreIndirectInstruction store => !IsAlias(store.Value, aliases),
            SsaStoreLocalInstruction store => !IsAlias(store.Value, aliases),
            SsaStoreGlobalInstruction store => !IsAlias(store.Value, aliases),
            SsaCopyMemoryInstruction copy => !IsAlias(copy.SourceAddress, aliases) && !IsAlias(copy.DestinationAddress, aliases),
            SsaValueInstruction { Value: SsaIndirectCallRValue indirect } => NoHeaderArguments(indirect, aliases),
            SsaIndirectCallInstruction indirect => NoHeaderArguments(indirect, aliases),
            SsaValueInstruction value => IsReadValue(value.Value),
            _ => true
        };
    }

    private static bool NoHeaderArguments(ISsaIndirectCallOperation call, HashSet<string> aliases)
        => !IsAlias(call.Target, aliases) && !call.Arguments.Any(a => IsAlias(a, aliases))
            && !(call.IndirectArgumentAddresses?.Any(a => a is not null && IsAlias(a, aliases)) ?? false)
            && !(call.IndirectArgumentLocalNames?.Any(n => n is not null && aliases.Contains(n)) ?? false);

    private static HashSet<string> PointerAliases(SsaFunction function, Func<SsaRValue, bool> root, string? parameter = null)
    {
        var definitions = Definitions(function);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        if (parameter is not null) aliases.Add(parameter);
        bool changed;
        do
        {
            changed = false;
            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                    if (phi.Incomings.Any(i => IsAlias(i.Value, aliases))) changed |= aliases.Add(phi.ResultName);
                foreach (var value in block.Instructions.OfType<SsaValueInstruction>())
                {
                    var alias = root(value.Value) || value.Value switch
                    {
                        SsaUseRValue use => IsAlias(use.Value, aliases),
                        SsaLoadLocalRValue load => aliases.Contains(load.LocalName),
                        SsaLoadIndirectRValue { Address: SsaValueReference address } when parameter is not null
                            && definitions.TryGetValue(address.Name, out var definition)
                            && definition is SsaAddressOfParameterRValue slot && slot.ParameterName == parameter
                            && slot.PointeeType.Kind == StarkTypeKind.RawPointer => true,
                        SsaConvertRValue convert => IsAlias(convert.Operand, aliases),
                        SsaFieldAddressRValue field => IsAlias(field.Address, aliases),
                        SsaElementAddressRValue element => IsAlias(element.Address, aliases),
                        SsaBinaryRValue binary => IsAlias(binary.Left, aliases) || IsAlias(binary.Right, aliases),
                        SsaUnaryRValue unary => IsAlias(unary.Operand, aliases),
                        SsaSelectRValue select => IsAlias(select.WhenTrue, aliases) || IsAlias(select.WhenFalse, aliases),
                        SsaExtractFieldRValue field => IsAlias(field.Target, aliases),
                        SsaInsertFieldRValue field => IsAlias(field.Target, aliases) || IsAlias(field.Value, aliases),
                        SsaExtractIndexRValue index => IsAlias(index.Target, aliases),
                        SsaInsertIndexRValue index => IsAlias(index.Target, aliases) || IsAlias(index.Value, aliases),
                        SsaMakeSliceFromPointerRValue slice => IsAlias(slice.Pointer, aliases),
                        _ => false
                    };
                    if (alias) changed |= aliases.Add(value.ResultName);
                }
            }
        } while (changed);
        return aliases;
    }

    private static bool IsAlias(SsaValue value, HashSet<string> aliases) => value is SsaValueReference r && aliases.Contains(r.Name);

    private static bool IsReadValue(SsaRValue value) => value is
        SsaUseRValue or SsaUnaryRValue or SsaBinaryRValue or SsaSelectRValue or SsaConvertRValue
        or SsaExtractFieldRValue or SsaInsertFieldRValue or SsaExtractIndexRValue or SsaInsertIndexRValue
        or SsaAddressOfLocalRValue or SsaAddressOfParameterRValue or SsaFieldAddressRValue or SsaElementAddressRValue
        or SsaLoadIndirectRValue or SsaLoadGlobalRValue or SsaLoadLocalRValue or SsaMakeSliceFromPointerRValue;

    private sealed record FieldFact(string Name, int Index, StarkTypeSymbol Type);
    private sealed record DequeueProof(SsaFunction Function, int WrapBlock,
        FieldFact Head, FieldFact Length, FieldFact Items, FieldFact Capacity);

    private static bool TryProveDequeue(SsaFunction function, out DequeueProof proof)
    {
        proof = null!;
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || function.Blocks.Count != 7
            || function.Parameters.Count != 2 || function.ReturnType.Kind != StarkTypeKind.Bool
            || !IsScalar(function.Parameters[1].Type)
            || function.Blocks.Any(b => b.Phis.Count != 0 || b.Instructions.Any(i => i is not SsaValueInstruction and not SsaStoreIndirectInstruction))
            || function.Blocks.SelectMany(b => b.Instructions).OfType<SsaValueInstruction>().Any(i => i.Value is not
                (SsaAddressOfParameterRValue or SsaFieldAddressRValue or SsaLoadIndirectRValue or SsaElementAddressRValue
                 or SsaBinaryRValue or SsaConvertRValue or SsaUseRValue or SsaExtractFieldRValue))) return false;
        var blocks = function.Blocks.ToDictionary(b => b.Id);
        var definitions = Definitions(function);
        var self = function.Parameters[0].Name;
        string? Address(SsaValue value)
        {
            return Resolve(value, definitions) switch
            {
                SsaAddressOfParameterRValue parameter => parameter.ParameterName,
                SsaFieldAddressRValue field when Address(field.Address) is { } parent => parent + "." + field.FieldName,
                _ => null
            };
        }
        string? ValuePath(SsaValue value) => Resolve(value, definitions) switch
        {
            SsaLoadIndirectRValue load => Address(load.Address),
            SsaExtractFieldRValue field when ValuePath(field.Target) is { } parent => parent + "." + field.FieldName,
            _ => null
        };
        SsaLoadIndirectRValue? Snapshot(SsaValue value) => Resolve(value, definitions) switch
        {
            SsaLoadIndirectRValue load => load,
            SsaExtractFieldRValue field => Snapshot(field.Target),
            _ => null
        };
        bool Read(SsaValue value, string path) => ValuePath(value) == path;
        bool Zero(SsaValue value) => value is SsaIntegerConstant { Value.IsZero: true };
        bool Compare(SsaValue? value, SsaBinaryOperator op, Func<SsaValue, bool> left, Func<SsaValue, bool> right)
            => value is not null && Resolve(value, definitions) is SsaBinaryRValue binary && binary.Operator == op && left(binary.Left) && right(binary.Right);
        bool Branch(SsaBasicBlock block) => block.Terminator.Kind == SsaTerminatorKind.Branch && block.Terminator.Targets.Count == 2;
        bool Return(SsaBasicBlock block, bool result) => block.Instructions.Count == 0
            && block.Terminator is { Kind: SsaTerminatorKind.Return, Value: SsaBoolConstant value } && value.Value == result;
        SsaStoreIndirectInstruction[] Stores(SsaBasicBlock block) => block.Instructions.OfType<SsaStoreIndirectInstruction>().ToArray();
        bool Reset(SsaBasicBlock block, int target) => Stores(block) is [var store] && Address(store.Address) == self + ".Head"
            && Zero(store.Value) && block.Terminator is { Kind: SsaTerminatorKind.Goto, Targets.Count: 1 } && block.Terminator.Targets[0] == target;

        var entry = blocks[function.EntryBlockId];
        if (!Branch(entry) || Stores(entry).Length != 0
            || !Compare(entry.Terminator.Condition, SsaBinaryOperator.Equal, v => Read(v, self + ".Length"), Zero)
            || !Return(blocks[entry.Terminator.Targets[0]], false)) return false;
        var move = blocks[entry.Terminator.Targets[1]];
        if (!Branch(move) || Stores(move) is not [var output, var increment]
            || Address(output.Address) != function.Parameters[1].Name
            || Resolve(output.Value, definitions) is not SsaLoadIndirectRValue moved
            || Resolve(moved.Address, definitions) is not SsaElementAddressRValue element
            || !Read(element.Address, self + ".Items.Items") || element.Index is null || !Read(element.Index, self + ".Head")
            || Address(increment.Address) != self + ".Head"
            || !Compare(increment.Value, SsaBinaryOperator.Add, v => Read(v, self + ".Head"), v => v is SsaIntegerConstant { Value.IsOne: true })
            || !Compare(move.Terminator.Condition, SsaBinaryOperator.GreaterThanOrEqual,
                v => Read(v, self + ".Head"), v => Read(v, self + ".Items.StorageCapacity"))) return false;
        var length = blocks[move.Terminator.Targets[1]];
        if (!Reset(blocks[move.Terminator.Targets[0]], length.Id) || !Branch(length)
            || Stores(length) is not [var decrement] || Address(decrement.Address) != self + ".Length"
            || !Compare(decrement.Value, SsaBinaryOperator.Subtract, v => Read(v, self + ".Length"), v => v is SsaIntegerConstant { Value.IsOne: true })
            || !Compare(length.Terminator.Condition, SsaBinaryOperator.Equal, v => Read(v, self + ".Length"), Zero)
            || !Reset(blocks[length.Terminator.Targets[0]], length.Terminator.Targets[1])
            || !Return(blocks[length.Terminator.Targets[1]], true)) return false;
        // The final empty test must read the decremented length, not an earlier
        // SSA snapshot. Otherwise the exact-end singleton would lose its reset.
        var emptyTest = (SsaBinaryRValue)Resolve(length.Terminator.Condition!, definitions)!;
        var finalRead = Snapshot(emptyTest.Left);
        var decrementIndex = length.Instructions.ToList().FindIndex(i => ReferenceEquals(i, decrement));
        if (!length.Instructions.Skip(decrementIndex + 1).OfType<SsaValueInstruction>()
            .Any(i => ReferenceEquals(i.Value, finalRead))) return false;
        FieldFact? Field(string path)
        {
            foreach (var value in definitions.Values)
            {
                if (value is SsaFieldAddressRValue address && Address(address.Address) + "." + address.FieldName == self + "." + path)
                    return new FieldFact(address.FieldName, address.FieldIndex, address.Type.ElementType!);
                if (value is SsaExtractFieldRValue field && ValuePath(field.Target) + "." + field.FieldName == self + "." + path)
                    return new FieldFact(field.FieldName, field.FieldIndex, field.Type);
            }
            return null;
        }
        if (Field("Head") is not { } head || Field("Length") is not { } count || Field("Items") is not { } items
            || Field("Items.StorageCapacity") is not { } capacity) return false;
        if (function.Parameters[0].Type.NamedType is null || items.Type.NamedType is null) return false;
        if (new[] { head, count, capacity }.Any(f => f.Type is not { Kind: StarkTypeKind.Integer, IsUnsigned: true, BitWidth: 64,
            RangeMin: { Sign: >= 0 }, RangeMax: { } max } || max > long.MaxValue)) return false;
        proof = new DequeueProof(function, move.Id, head, count, items, capacity);
        return true;
    }

    private SsaFunction Version(SsaFunction function, SsaBasicBlock header, SsaBasicBlock preheader,
        HashSet<int> region, HashSet<int> loop, SsaCallRValue dequeue, string receiver, string output, StarkTypeSymbol outputType, DequeueProof proof)
    {
        var nextId = function.Blocks.Max(b => b.Id) + 1;
        var remainderId = nextId++;
        var map = region.Order().ToDictionary(id => id, _ => nextId++);
        var names = function.Blocks.SelectMany(b => b.Phis.Select(p => p.ResultName)
            .Concat(b.Instructions.OfType<SsaValueInstruction>().Select(i => i.ResultName))).ToHashSet(StringComparer.Ordinal);
        string Fresh(string prefix)
        {
            var name = prefix;
            for (var i = 0; !names.Add(name); i++) name = prefix + i;
            return name;
        }
        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        foreach (var block in function.Blocks.Where(b => region.Contains(b.Id)))
        {
            foreach (var phi in block.Phis) replacements.Add(phi.ResultName, new SsaValueReference(Fresh(phi.ResultName + "_contiguous"), phi.Type));
            foreach (var value in block.Instructions.OfType<SsaValueInstruction>())
                replacements.Add(value.ResultName, new SsaValueReference(Fresh(value.ResultName + "_contiguous"), value.Value.Type));
        }
        var guard = new List<SsaInstruction>(preheader.Instructions);
        SsaValue Emit(SsaRValue value, List<SsaInstruction> instructions)
        {
            var name = Fresh("queue_guard");
            instructions.Add(new SsaValueInstruction(name, value, preheader.Terminator.Location));
            return new SsaValueReference(name, value.Type);
        }
        var queueType = dequeue.Arguments[0].Type;
        var receiverAddress = Emit(new SsaAddressOfLocalRValue(receiver, queueType,
            StarkTypeSymbols.RawPointer(queueType, isMutable: true), "queue receiver address"), guard);
        var outputAddress = Emit(new SsaAddressOfLocalRValue(output, outputType,
            StarkTypeSymbols.RawPointer(outputType, isMutable: true), "queue output address"), guard);
        var queue = Emit(new SsaLoadLocalRValue(receiver, queueType), guard);
        SsaValue Field(SsaValue aggregate, FieldFact field, List<SsaInstruction> instructions)
            => Emit(new SsaExtractFieldRValue(aggregate, field.Name, field.Index, field.Type, "queue field"), instructions);
        var head = Field(queue, proof.Head, guard);
        var length = Field(queue, proof.Length, guard);
        var items = Field(queue, proof.Items, guard);
        var capacity = Field(items, proof.Capacity, guard);
        var bounded = Emit(new SsaBinaryRValue(SsaBinaryOperator.LessThanOrEqual, head, capacity, StarkTypeSymbols.Bool, "head <= capacity"), guard);
        var rest = new List<SsaInstruction>();
        var remaining = Emit(new SsaBinaryRValue(SsaBinaryOperator.Subtract, capacity, head, capacity.Type, "capacity - head"), rest);
        var contiguous = Emit(new SsaBinaryRValue(SsaBinaryOperator.LessThanOrEqual, length, remaining, StarkTypeSymbols.Bool, "length <= remaining"), rest);
        var blocks = function.Blocks.Select(block => block.Id == preheader.Id ? block with
        {
            Instructions = guard,
            Terminator = new SsaTerminator(SsaTerminatorKind.Branch, [remainderId, header.Id], bounded, Location: preheader.Terminator.Location)
        } : block.Id == header.Id ? block with
        {
            Phis = block.Phis.Select(phi => phi with { Incomings = phi.Incomings.Concat(phi.Incomings.Where(i => i.PredecessorBlockId == preheader.Id)
                .Select(i => i with { PredecessorBlockId = remainderId })).ToArray() }).ToArray()
        } : block).ToList();
        blocks.Add(new SsaBasicBlock(remainderId, "queue_contiguous_guard", [], rest,
            new SsaTerminator(SsaTerminatorKind.Branch, [map[header.Id], header.Id], contiguous, Location: preheader.Terminator.Location)));
        foreach (var original in function.Blocks.Where(b => region.Contains(b.Id)))
        {
            var clone = SsaDirectCallInliner.RewriteBlock(original, replacements);
            blocks.Add(clone with
            {
                Id = map[original.Id], Label = original.Label + "_contiguous",
                Phis = clone.Phis.Select(phi => phi with
                {
                    ResultName = ((SsaValueReference)replacements[phi.ResultName]).Name,
                    Incomings = phi.Incomings.Select(i => i with { PredecessorBlockId = i.PredecessorBlockId == preheader.Id
                        ? remainderId : map[i.PredecessorBlockId] }).ToArray()
                }).ToArray(),
                Instructions = clone.Instructions.Select(instruction => instruction is SsaValueInstruction value
                    ? value with
                    {
                        ResultName = ((SsaValueReference)replacements[value.ResultName]).Name,
                        Value = loop.Contains(original.Id) && value.Value is SsaCallRValue call && call.FunctionName == dequeue.FunctionName
                            ? call with { IndirectArgumentAddresses = new SsaValue?[] { receiverAddress, outputAddress } }
                            : value.Value
                    } : instruction).ToArray(),
                Terminator = clone.Terminator with
                {
                    Targets = clone.Terminator.Targets.Select(id => map[id]).ToArray(),
                    DefaultTarget = clone.Terminator.DefaultTarget is { } target ? map[target] : null,
                    SwitchCases = clone.Terminator.SwitchCases?.Select(c => c with { TargetBlockId = map[c.TargetBlockId] }).ToArray()
                }
            });
        }
        var fastBody = proof.Function with { Blocks = proof.Function.Blocks.Select(block => block.Id == proof.WrapBlock
            ? block with { Terminator = new SsaTerminator(SsaTerminatorKind.Goto, [block.Terminator.Targets[1]], Location: block.Terminator.Location) }
            : block).ToArray() };
        fastBody = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(new SsaIrModule("queue", [fastBody])).Functions[0];
        var versioned = function with { Blocks = blocks };
        var inlined = _inliner.InlineVerifiedLoopCallee(versioned, fastBody, loop.Select(id => map[id]).ToHashSet());
        // Never leave a clone calling the original wrapping body as if the proof
        // had been consumed. A failed inline simply declines the entire rewrite.
        return ReferenceEquals(inlined, versioned) ? function : inlined;
    }

    private static string? ReceiverLocal(SsaCallRValue call, IReadOnlyDictionary<string, SsaRValue> definitions)
        => call.Arguments.Count == 2 && call.IndirectArgumentLocalNames?.ElementAtOrDefault(0) is { } local
            && Resolve(call.Arguments[0], definitions) is SsaLoadLocalRValue load && load.LocalName == local ? local : null;

    private static string? OutputLocal(SsaCallRValue call, IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        var name = call.IndirectArgumentLocalNames?.ElementAtOrDefault(1);
        if (call.IndirectArgumentAddresses?.ElementAtOrDefault(1) is not { } address) return name;
        return Resolve(address, definitions) is SsaAddressOfLocalRValue local && (name is null || name == local.LocalName)
            ? local.LocalName : null;
    }

    private static bool IsScalar(StarkTypeSymbol type) => type.Kind is StarkTypeKind.Integer or StarkTypeKind.Float or StarkTypeKind.Bool;
    private static Dictionary<string, SsaRValue> Definitions(SsaFunction function) => function.Blocks.SelectMany(b => b.Instructions)
        .OfType<SsaValueInstruction>().ToDictionary(i => i.ResultName, i => i.Value, StringComparer.Ordinal);

    private static SsaRValue? Resolve(SsaValue value, IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (value is SsaValueReference reference && visited.Add(reference.Name) && definitions.TryGetValue(reference.Name, out var definition))
        {
            if (definition is SsaUseRValue use) value = use.Value;
            else if (definition is SsaConvertRValue convert && convert.Operand.Type.Kind == convert.TargetType.Kind
                && convert.Operand.Type.BitWidth == convert.TargetType.BitWidth) value = convert.Operand;
            else return definition;
        }
        return null;
    }

    private static HashSet<int> Reachable(int start, IReadOnlyDictionary<int, SsaBasicBlock> blocks)
    {
        var reached = new HashSet<int>();
        var pending = new Stack<int>(); pending.Push(start);
        while (pending.TryPop(out var id)) if (reached.Add(id)) foreach (var target in blocks[id].Terminator.Targets) pending.Push(target);
        return reached;
    }

    private static Dictionary<int, HashSet<int>> Dominators(SsaFunction function, IReadOnlyDictionary<int, HashSet<int>> predecessors)
    {
        var reachable = Reachable(function.EntryBlockId, function.Blocks.ToDictionary(b => b.Id));
        var result = function.Blocks.ToDictionary(b => b.Id,
            b => b.Id == function.EntryBlockId || !reachable.Contains(b.Id) ? new HashSet<int> { b.Id } : new HashSet<int>(reachable));
        bool changed;
        do
        {
            changed = false;
            foreach (var id in reachable.Where(id => id != function.EntryBlockId))
            {
                var next = new HashSet<int>(reachable);
                foreach (var predecessor in predecessors[id].Where(reachable.Contains)) next.IntersectWith(result[predecessor]);
                next.Add(id);
                if (!next.SetEquals(result[id])) { result[id] = next; changed = true; }
            }
        } while (changed);
        return result;
    }
}
