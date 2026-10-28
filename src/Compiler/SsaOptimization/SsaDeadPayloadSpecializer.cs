using System.Numerics;

namespace Stark.Compiler;

// Proves an entire local backing region unobservable before deleting any writes.
// Header mutations, allocator operations and destruction stay in the graph. Calls
// are specialized privately, so an ordinary caller never inherits this proof.
internal sealed class SsaDeadPayloadSpecializer
{
    internal sealed record Plan(SsaIrModule Module, IReadOnlyDictionary<string, string> Origins,
        IReadOnlyDictionary<string, IReadOnlySet<string>> PayloadParameters);
    private enum RegionKind { Header, Pointer, View }
    private sealed record Region(RegionKind Kind, string Path = "", int AddressDepth = 0);
    private sealed class Proof
    {
        public required SsaFunction Function { get; init; }
        public required IReadOnlySet<string> PayloadParameters { get; init; }
        public HashSet<SsaInstruction> DeadStores { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<SsaInstruction, Proof> Calls { get; } = new(ReferenceEqualityComparer.Instance);
        public bool Changes => DeadStores.Count != 0 || Calls.Values.Any(static proof => proof.Changes);
    }

    private readonly TypeCheckModel _types;
    private readonly IReadOnlyDictionary<string, FunctionEffectProfile> _effects;
    private readonly int _pointerBits;
    private readonly SsaValueFactModel? _facts;
    private Dictionary<string, SsaFunction> _functions = null!;
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);
    private readonly List<SsaFunction> _clones = [];
    private readonly Dictionary<string, IReadOnlySet<string>> _payloadParameters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public SsaDeadPayloadSpecializer(TypeCheckModel types, IReadOnlyDictionary<string, FunctionEffectProfile> effects,
        SsaValueFactModel? facts = null, int pointerBits = 64)
    {
        _types = types;
        _effects = effects;
        _facts = facts;
        _pointerBits = pointerBits;
    }

    public Plan Optimize(SsaIrModule module, IReadOnlySet<string> roots)
    {
        _origins.Clear();
        _clones.Clear();
        _payloadParameters.Clear();
        _names.Clear();
        _functions = module.Functions.GroupBy(static f => f.Name).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        _names.UnionWith(_functions.Keys);
        var changed = false;
        var functions = module.Functions.Select(function =>
        {
            if (!roots.Contains(function.Name) || !function.SupportsDirectCodeGeneration)
                return function;
            var current = function;
            foreach (var local in function.Blocks.SelectMany(static b => b.Instructions).OfType<SsaAllocateLocalInstruction>())
            {
                if (local.StorageClass != "stack") continue;
                foreach (var path in PayloadPaths(local.LocalType, new HashSet<string>(StringComparer.Ordinal)))
                {
                    Proof? proof;
                    try
                    {
                        proof = Analyze(current, new Dictionary<string, Region>(StringComparer.Ordinal), local.LocalName,
                            new Region(RegionKind.Header, path), new HashSet<string>(StringComparer.Ordinal));
                    }
                    catch (NotSupportedException)
                    {
                        // A new/unmodelled SSA operation cannot establish liveness.
                        continue;
                    }
                    if (proof is { Changes: true }) current = Rewrite(proof, current.Name);
                }
            }
            changed |= !ReferenceEquals(current, function);
            return current;
        }).ToList();
        functions.AddRange(_clones);
        return new Plan(changed ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords) : module,
            new Dictionary<string, string>(_origins, StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(_payloadParameters, StringComparer.Ordinal));
    }

    private IEnumerable<string> PayloadPaths(StarkTypeSymbol type, HashSet<string> visited)
    {
        if (type.BorrowKind != StarkBorrowKind.None || type.InitializationKind != StarkInitializationKind.None) yield break;
        if (type.Kind == StarkTypeKind.Dynamic && type.ElementType is { } element && IsScalar(element))
        {
            yield return "";
        }
        else if (type.Kind == StarkTypeKind.Named && type.NamedType is { } name && visited.Add(name)
                 && _types.NamedTypes.TryGetValue(name, out var named) && !named.IsGeneric)
        {
            foreach (var field in named.Fields.Values)
                foreach (var nested in PayloadPaths(field.Type, new HashSet<string>(visited, StringComparer.Ordinal)))
                    yield return field.Name + (nested.Length == 0 ? "" : "." + nested);
        }
    }

    private static bool IsScalar(StarkTypeSymbol type) => type.Kind is StarkTypeKind.Integer or StarkTypeKind.Float or StarkTypeKind.Bool;

    private Proof? Analyze(SsaFunction function, Dictionary<string, Region> parameters, string? owner,
        Region? ownerRegion, HashSet<string> active)
    {
        // Recursion/unknown bodies require a different proof; never assume a
        // recursive fixed point is safe just because no read has been seen yet.
        if (!function.HasBody || !function.SupportsDirectCodeGeneration || active.Count >= 32 || !active.Add(function.Name)
            || !_effects.TryGetValue(function.Name, out var effect) || effect.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
            || owner is null && (effect.IsFfi || effect.IsVarargs
                || _types.Functions.GetValueOrDefault(function.Name)?.Visibility == StarkVisibility.Export))
            return null;
        var proof = new Proof
        {
            Function = function,
            PayloadParameters = function.Parameters.Where(parameter =>
                    parameters.GetValueOrDefault("arg_" + parameter.Name) is
                        { Kind: RegionKind.Pointer or RegionKind.View, AddressDepth: 0 })
                .Select(static parameter => parameter.Name).ToHashSet(StringComparer.Ordinal)
        };
        var values = new Dictionary<string, Region>(parameters, StringComparer.Ordinal);
        var locals = new Dictionary<string, Region>(StringComparer.Ordinal);
        if (owner is not null) locals[owner] = ownerRegion!;
        var instructions = function.Blocks.SelectMany(static b => b.Instructions).ToArray();
        var localStorage = instructions.OfType<SsaAllocateLocalInstruction>()
            .ToDictionary(static local => local.LocalName, static local => local.StorageClass, StringComparer.Ordinal);
        // Arena suballocations need a separate bounded-region proof. This pass
        // handles runtime-owned dynamic backing, never an entire arena slab.
        if (instructions.OfType<SsaValueInstruction>().Any(static instruction => instruction.Value is
            SsaDynamicStorageAllocationRValue { AllocationKind: DynamicStorageAllocationKind.Arena }
            or SsaDynamicStorageReserveRValue { AllocationKind: DynamicStorageAllocationKind.Arena }
            or SsaDynamicStorageTryReserveRValue { AllocationKind: DynamicStorageAllocationKind.Arena }
            or SsaDynamicStorageTryReserveCapacityRValue { AllocationKind: DynamicStorageAllocationKind.Arena }))
            return null;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instruction in instructions)
            foreach (var operand in Operands(instruction)) Use(operand);
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
                foreach (var incoming in phi.Incomings) Use(incoming.Value);
            if (block.Terminator.Value is { } returned) Use(returned);
            if (block.Terminator.Condition is { } condition) Use(condition);
        }
        void Use(SsaValue value) { if (value is SsaValueReference reference) used.Add(reference.Name); }
        Region? Value(SsaValue value) => value is SsaValueReference reference ? values.GetValueOrDefault(reference.Name) : null;
        static Region? Address(Region? region) => region is null ? null : region with { AddressDepth = region.AddressDepth + 1 };
        Region? Project(Region? region, string field, int fieldIndex, bool address)
        {
            if (region is null) return null;
            Region? result = null;
            if (region.Kind == RegionKind.Header)
            {
                if (region.Path.Length != 0)
                {
                    var split = region.Path.Split('.', 2);
                    if (split[0] == field) result = new Region(RegionKind.Header, split.Length == 2 ? split[1] : "");
                }
                else if (fieldIndex == 0) result = new Region(RegionKind.Pointer);
            }
            else if (region.Kind == RegionKind.View && fieldIndex == 0) result = new Region(RegionKind.Pointer);
            else if (region.Kind == RegionKind.Pointer) throw new NotSupportedException("Reinterpreted payload field");
            return address ? Address(result) : result;
        }
        bool invalid = false;
        Region? Load(Region? address, StarkTypeSymbol type, bool observed)
        {
            if (address is null) return null;
            if (address.AddressDepth > 0)
            {
                var result = address with { AddressDepth = address.AddressDepth - 1 };
                // Reinterpreting header/pointer slots as integers can expose a
                // backing address without a pointer-typed SSA value.
                if (result.Kind == RegionKind.Header && type.Kind is not (StarkTypeKind.Named or StarkTypeKind.Dynamic)
                    || result.Kind == RegionKind.Pointer && type.Kind != StarkTypeKind.RawPointer
                    || result.Kind == RegionKind.View && type.Kind is not (StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode))
                    invalid = true;
                return result;
            }
            if (address.Kind == RegionKind.Pointer && observed) invalid = true;
            if (address.Kind == RegionKind.Header)
            {
                if (type.Kind is StarkTypeKind.Named or StarkTypeKind.Dynamic) return address;
                invalid = true;
            }
            if (address.Kind == RegionKind.View)
            {
                if (type.Kind is StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode) return address;
                invalid = true;
            }
            return null;
        }
        Region? Element(Region? region, SsaValue? index, int? constantIndex = null, int stride = 1)
        {
            if (region is null) return null;
            // A payload-derived raw pointer must never walk backwards into the
            // runtime header. Retain SSA range facts, including target pointer
            // width, instead of assuming an unsigned index stays nonnegative
            // after LLVM's pointer-index conversion.
            var range = index is SsaIntegerConstant c ? new SsaIntegerRangeFact(c.Value, c.Value)
                : index is SsaValueReference r && _facts?.Functions.GetValueOrDefault(function.Name)?.Values.GetValueOrDefault(r.Name)?.IntegerRange is { } known ? known
                : index?.Type is { RangeMin: { } min, RangeMax: { } max } ? new SsaIntegerRangeFact(min, max)
                : constantIndex is { } literal ? new SsaIntegerRangeFact(literal, literal) : null;
            if (range is null || range.Min < 0 || range.Max > ((BigInteger.One << (_pointerBits - 1)) - 1) / stride)
            {
                invalid = true;
                return null;
            }
            if (region.AddressDepth == 0 && (region.Kind is RegionKind.Pointer or RegionKind.View
                || region.Kind == RegionKind.Header && region.Path.Length == 0))
                return new Region(RegionKind.Pointer);
            invalid = true;
            return null;
        }
        Region? Derive(SsaRValue value, bool observed)
        {
            switch (value)
            {
                case SsaUseRValue use: return Value(use.Value);
                case SsaLoadLocalRValue local: return locals.GetValueOrDefault(local.LocalName);
                case SsaAddressOfLocalRValue local: return Address(locals.GetValueOrDefault(local.LocalName));
                case SsaAddressOfParameterRValue parameter: return Address(parameters.GetValueOrDefault("arg_" + parameter.ParameterName));
                case SsaExtractFieldRValue field: return Project(Value(field.Target), field.FieldName, field.FieldIndex, false);
                case SsaFieldAddressRValue field: return Project(Value(field.Address), field.FieldName, field.FieldIndex, true);
                case SsaLoadIndirectRValue load: return Load(Value(load.Address), load.Type, observed);
                case SsaElementAddressRValue element:
                    if (element.Index is not null && Value(element.Index) is not null) invalid = true;
                    // A raw GEP is not a bounded access to this owner merely
                    // because its base came from Data. Nonzero offsets require
                    // the original structured dynamic-storage access contract.
                    if (Value(element.Address) is not null && element.SourceStorageType?.Kind != StarkTypeKind.Dynamic
                        && element.ConstantIndex != 0 && element.Index is not SsaIntegerConstant { Value.IsZero: true }) invalid = true;
                    return Element(Value(element.Address), element.Index, element.ConstantIndex,
                        Math.Max(1, (element.Type.ElementType?.BitWidth + 7 ?? 8) / 8));
                case SsaSliceElementAddressRValue element:
                    return Element(Value(element.Slice), element.Index,
                    stride: Math.Max(1, (element.Slice.Type.ElementType?.BitWidth + 7 ?? 8) / 8));
                case SsaMakeSliceFromPointerRValue slice:
                    var pointer = Value(slice.Pointer);
                    if (pointer is null) return null;
                    if (slice.SourceStorageType?.Kind != StarkTypeKind.Dynamic) invalid = true;
                    if (pointer is { Kind: RegionKind.Pointer, AddressDepth: 0 }) return new Region(RegionKind.View);
                    invalid = true;
                    return null;
                case SsaMakeSliceFromLocalRValue slice:
                    var localSlice = locals.GetValueOrDefault(slice.LocalName);
                    if (localSlice is not null && localSlice.Kind != RegionKind.View) invalid = true;
                    return localSlice;
                case SsaTextSliceRValue slice:
                    return Element(Value(slice.TextValue), slice.Start,
                        stride: Math.Max(1, (slice.Type.ElementType?.BitWidth + 7 ?? 8) / 8)) is null ? null : new Region(RegionKind.View);
                case SsaLoadSliceElementRValue load:
                    if (Value(load.Slice) is not null && observed) invalid = true;
                    return null;
                case SsaExtractIndexRValue index:
                    var target = Value(index.Target);
                    if (target?.Kind == RegionKind.View) return index.ElementIndex == 0 ? new Region(RegionKind.Pointer) : null;
                    if (target is not null) invalid = true;
                    return null;
                case SsaConvertRValue convert:
                    var converted = Value(convert.Operand);
                    if (converted is not null && (convert.Type.Kind != convert.Operand.Type.Kind
                        || convert.Type.NamedType != convert.Operand.Type.NamedType
                        || convert.Type.ElementType != convert.Operand.Type.ElementType)) invalid = true;
                    return converted;
                case SsaSelectRValue select:
                    var left = Value(select.WhenTrue); var right = Value(select.WhenFalse);
                    if (left is not null || right is not null) return left ?? right;
                    return null;
                case SsaDynamicStorageMoveLastRValue move:
                    if (Value(move.StorageAddress) is not null && observed) invalid = true;
                    return null;
                case SsaDynamicStorageMoveAtRValue move:
                    if (Value(move.StorageAddress) is not null && observed) invalid = true;
                    return null;
                case SsaBinaryRValue binary:
                    if (Value(binary.Left) is not null || Value(binary.Right) is not null)
                    {
                        if (binary.Operator is not (SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual)
                            || binary.Left.Type.Kind is not (StarkTypeKind.RawPointer or StarkTypeKind.Null)
                            || binary.Right.Type.Kind is not (StarkTypeKind.RawPointer or StarkTypeKind.Null)) invalid = true;
                    }
                    return null;
                case SsaDynamicStorageAllocationRValue or SsaDynamicStorageFreeRValue or SsaHeapStorageFreeRValue
                    or SsaDynamicStorageReserveRValue or SsaDynamicStorageTryReserveRValue or SsaDynamicStorageTryReserveCapacityRValue:
                    return null;
                case SsaCallRValue or SsaIndirectCallRValue: return null;
                default:
                    if (Operands(value).Any(operand => Value(operand) is not null)) invalid = true;
                    return null;
            }
        }
        // Monotone may-alias propagation, followed by exactness checks below.
        // A mixed phi/local is never used as an exclusive dead destination.
        bool Set(Dictionary<string, Region> map, string name, Region? region)
        {
            if (region is null) return false;
            if (map.TryGetValue(name, out var prior)) { if (prior != region) invalid = true; return false; }
            map[name] = region; return true;
        }
        var changed = true;
        while (changed && !invalid)
        {
            changed = false;
            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                    foreach (var incoming in phi.Incomings) changed |= Set(values, phi.ResultName, Value(incoming.Value));
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is SsaValueInstruction value) changed |= Set(values, value.ResultName, Derive(value.Value, used.Contains(value.ResultName)));
                    if (instruction is SsaStoreLocalInstruction store && store.LocalName != owner) changed |= Set(locals, store.LocalName, Value(store.Value));
                }
            }
        }
        if (invalid) return null;
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
                if (values.TryGetValue(phi.ResultName, out var region) && phi.Incomings.Any(i => Value(i.Value) != region)) return null;
            if (block.Terminator.Kind is SsaTerminatorKind.TailCall) return null;
            if (block.Terminator.Value is { } result && Value(result) is not null) return null;
            if (block.Terminator.Condition is { } condition && Value(condition) is not null) return null;
        }
        foreach (var instruction in instructions)
        {
            var storage = instruction switch
            {
                SsaValueInstruction { Value: SsaDynamicStorageFreeRValue free } => (Value(free.Storage), 0),
                SsaValueInstruction { Value: SsaDynamicStorageReserveRValue reserve } => (Value(reserve.StorageAddress), 1),
                SsaValueInstruction { Value: SsaDynamicStorageTryReserveRValue reserve } => (Value(reserve.StorageAddress), 1),
                SsaValueInstruction { Value: SsaDynamicStorageTryReserveCapacityRValue reserve } => (Value(reserve.StorageAddress), 1),
                SsaValueInstruction { Value: SsaDynamicStorageMoveLastRValue move } => (Value(move.StorageAddress), 1),
                SsaValueInstruction { Value: SsaDynamicStorageMoveAtRValue move } => (Value(move.StorageAddress), 1),
                _ => ((Region?)null, 0)
            };
            if (storage.Item1 is { } header && (header.Kind != RegionKind.Header || header.Path.Length != 0 || header.AddressDepth != storage.Item2)) return null;
            if (instruction is SsaValueInstruction { Value: SsaSelectRValue select }
                && Value(select.WhenTrue) != Value(select.WhenFalse)) return null;
            if (instruction is SsaStoreLocalInstruction store)
            {
                if (Value(store.Value) is not null && localStorage.GetValueOrDefault(store.LocalName) != "stack") return null;
                if (store.LocalName == owner)
                {
                    if (store.Value is not SsaZeroInitializerValue && !(store.Value is SsaValueReference r && instructions.OfType<SsaValueInstruction>().Any(v => v.ResultName == r.Name && v.Value is SsaDynamicStorageAllocationRValue))) return null;
                }
                else if (locals.TryGetValue(store.LocalName, out var region) && Value(store.Value) != region) return null;
            }
            else if (instruction is SsaStoreIndirectInstruction indirect)
            {
                if (Value(indirect.Value) is not null) return null;
                if (Value(indirect.Address) is { } destination)
                {
                    if (destination is not { Kind: RegionKind.Pointer, AddressDepth: 0 } || !IsScalar(indirect.ValueType)) return null;
                    proof.DeadStores.Add(instruction);
                }
            }
            else if (instruction is SsaCopyMemoryInstruction copy)
            {
                if (Value(copy.DestinationAddress) is { Kind: RegionKind.Pointer, AddressDepth: 0 } && IsScalar(copy.CopyType)) proof.DeadStores.Add(instruction);
                else if (Value(copy.DestinationAddress) is not null || Value(copy.SourceAddress) is not null) return null;
            }
            else if (instruction is SsaStoreGlobalInstruction global && Value(global.Value) is not null) return null;
            var call = instruction switch { SsaValueInstruction { Value: SsaCallRValue c } => c as ISsaDirectCallOperation, SsaCallInstruction c => c, _ => null };
            if (call is not null)
            {
                var arguments = new Dictionary<string, Region>(StringComparer.Ordinal);
                if (!_functions.TryGetValue(call.FunctionName, out var callee) || callee.Parameters.Count != call.Arguments.Count)
                {
                    if (Operands(instruction).Any(v => Value(v) is not null)
                        || call.IndirectArgumentLocalNames?.Any(name => name is not null && locals.ContainsKey(name)) == true) return null;
                    continue;
                }
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    var region = Value(call.Arguments[i]);
                    if (region is null && call.IndirectArgumentLocalNames is { } names && i < names.Count && names[i] is { } local)
                        region = locals.GetValueOrDefault(local);
                    if (region is null && call.IndirectArgumentAddresses is { } addresses && i < addresses.Count && addresses[i] is { } address)
                    {
                        region = Value(address);
                        if (region is { AddressDepth: > 0 } && callee.Parameters[i].Type.Kind != StarkTypeKind.RawPointer) region = region with { AddressDepth = region.AddressDepth - 1 };
                    }
                    if (region is not null)
                    {
                        // Full-object out/init contracts promise initialization
                        // after return. Do not retain that promise on a clone
                        // whose writes are discarded. Open slices have no such
                        // LLVM initialization extent in the current ABI.
                        if (callee.Parameters[i].Type.InitializationKind != StarkInitializationKind.None
                            && callee.Parameters[i].Type.Kind != StarkTypeKind.Slice) return null;
                        arguments["arg_" + callee.Parameters[i].Name] = region;
                    }
                }
                if (arguments.Count == 0) continue;
                var child = Analyze(callee, arguments, null, null, new HashSet<string>(active, StringComparer.Ordinal));
                if (child is null) return null;
                proof.Calls[instruction] = child;
            }
            else if (instruction is SsaValueInstruction { Value: SsaIndirectCallRValue } or SsaIndirectCallInstruction
                     && Operands(instruction).Any(v => Value(v) is not null)) return null;
        }
        return proof;
    }


    private SsaFunction Rewrite(Proof proof, string name)
    {
        string Clone(Proof child)
        {
            var cloneName = "__stark_dead_payload_" + _clones.Count;
            while (!_names.Add(cloneName)) cloneName += "_";
            // Reserve a slot before recursing to keep names unique.
            var slot = _clones.Count;
            _clones.Add(child.Function);
            _origins[cloneName] = child.Function.Name;
            _payloadParameters[cloneName] = child.PayloadParameters;
            _clones[slot] = Rewrite(child, cloneName);
            return cloneName;
        }
        return proof.Function with
        {
            Name = name,
            Blocks = proof.Function.Blocks.Select(block => block with
            {
                Instructions = block.Instructions.Where(i => !proof.DeadStores.Contains(i)).Select(instruction =>
                {
                    if (!proof.Calls.TryGetValue(instruction, out var child) || !child.Changes) return instruction;
                    var target = Clone(child);
                    return instruction switch
                    {
                        SsaValueInstruction { Value: SsaCallRValue call } value => value with { Value = call with { FunctionName = target } },
                        SsaCallInstruction call => call with { FunctionName = target },
                        _ => instruction
                    };
                }).ToArray()
            }).ToArray()
        };
    }

    // Unknown operations conservatively reject the proof; adding a new SSA
    // operation must not silently turn its operands into untracked memory.
    private static IEnumerable<SsaValue> Operands(SsaInstruction instruction) => instruction switch
    {
        SsaValueInstruction value => Operands(value.Value),
        ISsaDirectCallOperation call => call.Arguments.Concat(call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? []),
        ISsaIndirectCallOperation call => new[] { call.Target }.Concat(call.Arguments).Concat(call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? []),
        SsaStoreLocalInstruction store => [store.Value],
        SsaStoreIndirectInstruction store => [store.Address, store.Value],
        SsaCopyMemoryInstruction copy => [copy.DestinationAddress, copy.SourceAddress],
        SsaStoreGlobalInstruction store => [store.Value],
        SsaAllocateLocalInstruction or SsaLifetimeStartInstruction or SsaLifetimeEndInstruction or SsaDeallocateLocalInstruction
            or SsaArenaFrameEnterInstruction or SsaArenaFrameLeaveInstruction => [],
        _ => throw new NotSupportedException(instruction.GetType().Name)
    };
    private static IEnumerable<SsaValue> Operands(SsaRValue value) => value switch
    {
        SsaUseRValue use => [use.Value],
        SsaUnaryRValue unary => [unary.Operand],
        SsaBinaryRValue binary => [binary.Left, binary.Right],
        SsaSelectRValue select => [select.Condition, select.WhenTrue, select.WhenFalse],
        ISsaDirectCallOperation call => call.Arguments.Concat(call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? []),
        ISsaIndirectCallOperation call => new[] { call.Target }.Concat(call.Arguments).Concat(call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? []),
        SsaConvertRValue convert => [convert.Operand],
        SsaExtractFieldRValue field => [field.Target],
        SsaInsertFieldRValue field => [field.Target, field.Value],
        SsaExtractIndexRValue index => [index.Target],
        SsaInsertIndexRValue index => [index.Target, index.Value],
        SsaDynVTableSlotRValue slot => [slot.VtablePointer],
        SsaMakeSliceFromPointerRValue slice => [slice.Pointer, slice.Length],
        SsaDynamicStorageAllocationRValue allocation => [allocation.Capacity],
        SsaDynamicStorageFreeRValue free => [free.Storage],
        SsaHeapStorageFreeRValue free => [free.Pointer],
        SsaDynamicStorageReserveRValue reserve => [reserve.StorageAddress, reserve.AdditionalCapacity],
        SsaDynamicStorageTryReserveRValue reserve => [reserve.StorageAddress, reserve.AdditionalCapacity],
        SsaDynamicStorageTryReserveCapacityRValue reserve => [reserve.StorageAddress, reserve.TargetCapacity],
        SsaDynamicStorageMoveLastRValue move => [move.StorageAddress],
        SsaDynamicStorageMoveAtRValue move => [move.StorageAddress, move.Index],
        SsaLoadSliceElementRValue load => [load.Slice, load.Index],
        SsaTextSliceRValue slice => [slice.TextValue, slice.Start, slice.Length],
        SsaFieldAddressRValue field => [field.Address],
        SsaElementAddressRValue element => element.Index is null ? [element.Address] : [element.Address, element.Index],
        SsaSliceElementAddressRValue element => [element.Slice, element.Index],
        SsaLoadIndirectRValue load => [load.Address],
        SsaLoadLocalRValue or SsaLoadGlobalRValue or SsaAddressOfLocalRValue or SsaAddressOfParameterRValue or SsaMakeSliceFromLocalRValue => [],
        _ => throw new NotSupportedException(value.GetType().Name)
    };
}
