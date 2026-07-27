using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaDirectCallDevirtualizer
{
    private readonly IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> _dynVTableSlotTargets;
    private readonly IReadOnlyDictionary<string, StarkTypeSymbol> _dynSlotReceiverTypes;

    public SsaDirectCallDevirtualizer(TypeCheckModel? typeModel = null)
    {
        _dynVTableSlotTargets = BuildDynVTableSlotTargets(typeModel, out var dynSlotReceiverTypes);
        _dynSlotReceiverTypes = dynSlotReceiverTypes;
    }

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var changed = false;
        var functions = module.Functions
            .Select(function =>
            {
                var optimized = OptimizeFunction(function);
                changed |= !ReferenceEquals(optimized, function);
                return optimized;
            })
            .ToArray();

        var optimizedModule = changed
            ? new SsaIrModule(module.ModuleName, functions, module.AddressTakenFunctionRecords)
            : module;

        return SsaAddressTakenFunctionPruner.Prune(optimizedModule);
    }

    private SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var functionPointerTargets = FunctionPointerTargetFacts.Build(function, _dynVTableSlotTargets);
        var usedValueNames = CollectUsedValueNames(function);
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var blockChanged = false;
                var instructions = new List<SsaInstruction>(block.Instructions.Count);
                foreach (var instruction in block.Instructions)
                {
                    var optimized = OptimizeInstruction(instruction, functionPointerTargets, usedValueNames, instructions);
                    blockChanged |= !ReferenceEquals(optimized, instruction);
                    instructions.Add(optimized);
                }

                if (!blockChanged)
                {
                    return block;
                }

                changed = true;
                return block with { Instructions = instructions };
            })
            .ToArray();

        return changed
            ? function with { Blocks = blocks }
            : function;
    }

    private static HashSet<string> CollectUsedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            names.Add(parameter.Name);
        }

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                names.Add(phi.ResultName);
            }

            foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                names.Add(instruction.ResultName);
            }
        }

        return names;
    }

    private static IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> BuildDynVTableSlotTargets(
        TypeCheckModel? typeModel,
        out IReadOnlyDictionary<string, StarkTypeSymbol> dynSlotReceiverTypes)
    {
        if (typeModel is null)
        {
            dynSlotReceiverTypes = EmptyDynSlotReceiverTypes;
            return EmptyDynVTableSlotTargets;
        }

        var targets = new Dictionary<DynVTableSlotKey, SsaFunctionAddressValue>();
        var receiverTypes = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        foreach (var concreteType in typeModel.NamedTypes.Values
                     .Where(static type => type.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                           && type.ImplementedTraits.Count > 0)
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            foreach (var traitName in concreteType.ImplementedTraits
                         .Distinct()
                         .OrderBy(static name => name, StringComparer.Ordinal))
            {
                if (!typeModel.NamedTypes.TryGetValue(traitName, out var traitType)
                    || traitType.Kind != DeclarationKind.Trait
                    || !traitType.IsDynTrait)
                {
                    continue;
                }

                var vtableGlobalName = DynTraitFacts.BuildVtableGlobalName(concreteType.Name, traitName);
                foreach (var slot in DynTraitFacts.GetVtableLayout(traitName, typeModel.Functions))
                {
                    if (!TryResolveSlotFunction(typeModel, concreteType.Name, slot.MethodName, out var function))
                    {
                        continue;
                    }

                    targets[new DynVTableSlotKey(vtableGlobalName, slot.Index)] =
                        new SsaFunctionAddressValue(function.Name, BuildDynSlotFunctionPointerType(slot.TraitSignature));
                    if (function.Parameters.Count > 0)
                    {
                        receiverTypes[function.Name] = function.Parameters[0].Type;
                    }
                }
            }
        }

        dynSlotReceiverTypes = receiverTypes;
        return targets;
    }

    private static bool TryResolveSlotFunction(
        TypeCheckModel typeModel,
        string concreteTypeName,
        string methodName,
        out TypedFunctionSignature function)
    {
        var dot = concreteTypeName.LastIndexOf('.');
        var simpleType = dot < 0 ? concreteTypeName : concreteTypeName[(dot + 1)..];
        foreach (var key in new[] { $"{concreteTypeName}.{methodName}", $"{simpleType}.{methodName}" })
        {
            if (typeModel.Functions.TryGetValue(key, out function!)
                && !function.IsStatic)
            {
                return true;
            }
        }

        function = default!;
        return false;
    }

    private static StarkTypeSymbol BuildDynSlotFunctionPointerType(TypedFunctionSignature signature)
    {
        var erasedReceiverType = StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true);
        var parameterTypes = new List<StarkTypeSymbol>(signature.Parameters.Count) { erasedReceiverType };
        for (var index = 1; index < signature.Parameters.Count; index++)
        {
            parameterTypes.Add(signature.Parameters[index].Type);
        }

        return StarkTypeSymbols.FunctionPointer(
            signature.Kind,
            signature.ReturnType,
            parameterTypes,
            isTailCallable: signature.IsTailCallable,
            pointeeDeadOnReturnParameterNames: MapPointeeDeadOnReturnParameters(signature));
    }

    private static IReadOnlyList<string>? MapPointeeDeadOnReturnParameters(TypedFunctionSignature signature)
    {
        if (signature.PointeeDeadOnReturnParameters.Count == 0)
        {
            return null;
        }

        var deadParameters = signature.PointeeDeadOnReturnParameters.ToHashSet(StringComparer.Ordinal);
        var mapped = signature.Parameters
            .Select((parameter, index) => deadParameters.Contains(parameter.Name) ? $"arg{index}" : null)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .ToArray();
        return mapped.Length == 0 ? null : mapped;
    }

    private static IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> EmptyDynVTableSlotTargets { get; } =
        new Dictionary<DynVTableSlotKey, SsaFunctionAddressValue>();

    private static IReadOnlyDictionary<string, StarkTypeSymbol> EmptyDynSlotReceiverTypes { get; } =
        new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);

    private readonly record struct DynVTableSlotKey(string VtableGlobalName, int SlotIndex);

    private SsaInstruction OptimizeInstruction(
        SsaInstruction instruction,
        FunctionPointerTargetFacts functionPointerTargets,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions)
    {
        if (instruction is SsaValueInstruction valueInstruction
            && TryDevirtualizeDirectFunctionAddressCall(
                valueInstruction.Value,
                functionPointerTargets,
                usedValueNames,
                prologueInstructions,
                valueInstruction.Location,
                out var directCall))
        {
            return valueInstruction with { Value = directCall };
        }

        if (instruction is SsaIndirectCallInstruction indirectCall
            && TryDevirtualizeDirectFunctionAddressCall(
                indirectCall,
                functionPointerTargets,
                usedValueNames,
                prologueInstructions,
                out var directCallInstruction))
        {
            return directCallInstruction;
        }

        return instruction;
    }

    private bool TryDevirtualizeDirectFunctionAddressCall(
        SsaRValue value,
        FunctionPointerTargetFacts functionPointerTargets,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions,
        SourceLocation? location,
        out SsaCallRValue directCall)
    {
        directCall = default!;

        if (value is not SsaIndirectCallRValue indirectCall
            || !functionPointerTargets.TryGetSingletonTarget(indirectCall.Target, out var functionAddress))
        {
            return false;
        }

        var arguments = AdjustReceiverArgument(
            functionAddress.FunctionName,
            indirectCall.Arguments,
            usedValueNames,
            prologueInstructions,
            location);

        directCall = new SsaCallRValue(
            functionAddress.FunctionName,
            arguments,
            indirectCall.Type,
            indirectCall.Text,
            indirectCall.IndirectArgumentLocalNames,
            SourceReturnType: indirectCall.SourceReturnType,
            indirectCall.IndirectArgumentAddresses);
        return true;
    }

    private bool TryDevirtualizeDirectFunctionAddressCall(
        SsaIndirectCallInstruction indirectCall,
        FunctionPointerTargetFacts functionPointerTargets,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions,
        out SsaCallInstruction directCall)
    {
        directCall = default!;

        if (!functionPointerTargets.TryGetSingletonTarget(indirectCall.Target, out var functionAddress))
        {
            return false;
        }

        var arguments = AdjustReceiverArgument(
            functionAddress.FunctionName,
            indirectCall.Arguments,
            usedValueNames,
            prologueInstructions,
            indirectCall.Location);

        directCall = new SsaCallInstruction(
            functionAddress.FunctionName,
            arguments,
            indirectCall.Type,
            indirectCall.Text,
            indirectCall.IndirectArgumentLocalNames,
            SourceReturnType: indirectCall.SourceReturnType,
            IndirectArgumentAddresses: indirectCall.IndirectArgumentAddresses,
            Location: indirectCall.Location,
            ScopedNoAliasGroups: indirectCall.ScopedNoAliasGroups,
            LoopAccessGroups: indirectCall.LoopAccessGroups);
        return true;
    }

    /// <summary>
    /// Devirtualized dyn-trait slot calls pass the receiver as a type-erased raw
    /// pointer while the concrete target declares a typed pointer-backed receiver
    /// (e.g. <c>borrow Dog</c>). Re-type the receiver through raw-pointer and
    /// pointer-backed-borrow conversions (both no-ops at LLVM emission) so the
    /// direct call site matches the target's declared signature.
    /// </summary>
    private IReadOnlyList<SsaValue> AdjustReceiverArgument(
        string targetFunctionName,
        IReadOnlyList<SsaValue> arguments,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions,
        SourceLocation? location)
    {
        if (arguments.Count == 0
            || !_dynSlotReceiverTypes.TryGetValue(targetFunctionName, out var receiverType))
        {
            return arguments;
        }

        var receiver = arguments[0];
        if (receiver.Type.Kind != StarkTypeKind.RawPointer
            || !StarkTypeSymbols.IsPointerBackedBorrowType(receiverType))
        {
            return arguments;
        }

        var pointeeType = StarkTypeSymbols.BorrowReturnValueType(receiverType);
        if (receiver.Type.ElementType is { } receiverPointee
            && receiverPointee.Kind == pointeeType.Kind
            && string.Equals(receiverPointee.DisplayName, pointeeType.DisplayName, StringComparison.Ordinal))
        {
            return arguments;
        }

        var typedPointerName = CreateFreshName("__devirt_recv_ptr", usedValueNames);
        var typedPointerType = StarkTypeSymbols.RawPointer(pointeeType, isMutable: false);
        prologueInstructions.Add(new SsaValueInstruction(
            typedPointerName,
            new SsaConvertRValue(receiver, typedPointerType, $"devirt receiver pointer for {targetFunctionName}"),
            location));

        var borrowName = CreateFreshName("__devirt_recv", usedValueNames);
        prologueInstructions.Add(new SsaValueInstruction(
            borrowName,
            new SsaConvertRValue(
                new SsaValueReference(typedPointerName, typedPointerType),
                receiverType,
                $"devirt receiver for {targetFunctionName}"),
            location));

        var adjusted = new SsaValue[arguments.Count];
        adjusted[0] = new SsaValueReference(borrowName, receiverType);
        for (var index = 1; index < arguments.Count; index++)
        {
            adjusted[index] = arguments[index];
        }

        return adjusted;
    }

    private static string CreateFreshName(string baseName, ISet<string> usedValueNames)
    {
        if (usedValueNames.Add(baseName))
        {
            return baseName;
        }

        for (var suffix = 0; ; suffix++)
        {
            var candidate = $"{baseName}_{suffix.ToString(CultureInfo.InvariantCulture)}";
            if (usedValueNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private sealed class FunctionPointerTargetFacts
    {
        private readonly IReadOnlyDictionary<string, SsaRValue> _definitions;
        private readonly IReadOnlyDictionary<string, SsaPhi> _phis;
        private readonly IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> _dynVTableSlotTargets;

        private FunctionPointerTargetFacts(
            IReadOnlyDictionary<string, SsaRValue> definitions,
            IReadOnlyDictionary<string, SsaPhi> phis,
            IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> dynVTableSlotTargets)
        {
            _definitions = definitions;
            _phis = phis;
            _dynVTableSlotTargets = dynVTableSlotTargets;
        }

        public static FunctionPointerTargetFacts Build(
            SsaFunction function,
            IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> dynVTableSlotTargets)
        {
            var definitions = new Dictionary<string, SsaRValue>(StringComparer.Ordinal);
            var phis = new Dictionary<string, SsaPhi>(StringComparer.Ordinal);

            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    phis[phi.ResultName] = phi;
                }

                foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
                {
                    definitions[instruction.ResultName] = instruction.Value;
                }
            }

            return new FunctionPointerTargetFacts(definitions, phis, dynVTableSlotTargets);
        }

        public bool TryGetSingletonTarget(SsaValue value, out SsaFunctionAddressValue functionAddress)
        {
            var targets = new Dictionary<string, SsaFunctionAddressValue>(StringComparer.Ordinal);
            if (!TryCollectTargets(value, targets, new HashSet<string>(StringComparer.Ordinal))
                || targets.Count != 1)
            {
                functionAddress = default!;
                return false;
            }

            functionAddress = targets.Values.Single();
            return true;
        }

        private bool TryCollectTargets(
            SsaValue value,
            IDictionary<string, SsaFunctionAddressValue> targets,
            ISet<string> visitingValueNames)
        {
            switch (value)
            {
                case SsaFunctionAddressValue functionAddress:
                    targets[functionAddress.FunctionName] = functionAddress;
                    return true;
                case SsaValueReference reference:
                    return TryCollectTargets(reference, targets, visitingValueNames);
                default:
                    return false;
            }
        }

        private bool TryCollectTargets(
            SsaValueReference reference,
            IDictionary<string, SsaFunctionAddressValue> targets,
            ISet<string> visitingValueNames)
        {
            if (!visitingValueNames.Add(reference.Name))
            {
                return false;
            }

            try
            {
                if (_definitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryCollectTargets(definition, targets, visitingValueNames);
                }

                if (_phis.TryGetValue(reference.Name, out var phi))
                {
                    if (phi.Incomings.Count == 0)
                    {
                        return false;
                    }

                    foreach (var incoming in phi.Incomings)
                    {
                        if (!TryCollectTargets(incoming.Value, targets, visitingValueNames))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return false;
            }
            finally
            {
                visitingValueNames.Remove(reference.Name);
            }
        }

        private bool TryCollectTargets(
            SsaRValue value,
            IDictionary<string, SsaFunctionAddressValue> targets,
            ISet<string> visitingValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    return TryCollectTargets(use.Value, targets, visitingValueNames);
                case SsaSelectRValue select:
                    return TryCollectTargets(select.WhenTrue, targets, visitingValueNames)
                        && TryCollectTargets(select.WhenFalse, targets, visitingValueNames);
                case SsaConvertRValue convert when convert.Operand.Type.Kind == StarkTypeKind.FunctionPointer
                                                 && convert.TargetType.Kind == StarkTypeKind.FunctionPointer:
                    return TryCollectTargets(convert.Operand, targets, visitingValueNames);
                case SsaDynVTableSlotRValue dynVTableSlot:
                    return TryCollectDynVTableSlotTargets(dynVTableSlot, targets, visitingValueNames);
                case SsaExtractIndexRValue { ElementIndex: 0 } extractIndex
                    when extractIndex.Target.Type.Kind == StarkTypeKind.Closure:
                    return TryCollectClosureInvokeTargets(
                        extractIndex.Target,
                        targets,
                        visitingValueNames);
                default:
                    return false;
            }
        }

        private bool TryCollectDynVTableSlotTargets(
            SsaDynVTableSlotRValue vtableSlot,
            IDictionary<string, SsaFunctionAddressValue> targets,
            ISet<string> visitingValueNames)
        {
            if (_dynVTableSlotTargets.Count == 0)
            {
                return false;
            }

            var vtableGlobals = new HashSet<string>(StringComparer.Ordinal);
            if (!TryCollectDynVTableGlobals(vtableSlot.VtablePointer, vtableGlobals, visitingValueNames)
                || vtableGlobals.Count == 0)
            {
                return false;
            }

            foreach (var vtableGlobal in vtableGlobals)
            {
                if (!_dynVTableSlotTargets.TryGetValue(new DynVTableSlotKey(vtableGlobal, vtableSlot.SlotIndex), out var functionAddress))
                {
                    return false;
                }

                targets[functionAddress.FunctionName] = functionAddress;
            }

            return true;
        }

        private bool TryCollectDynVTableGlobals(
            SsaValue value,
            ISet<string> vtableGlobals,
            ISet<string> visitingValueNames)
        {
            switch (value)
            {
                case SsaGlobalAddressValue globalAddress when DynTraitFacts.IsVtableGlobalName(globalAddress.GlobalName):
                    vtableGlobals.Add(globalAddress.GlobalName);
                    return true;
                case SsaValueReference reference:
                    return TryCollectDynVTableGlobals(reference, vtableGlobals, visitingValueNames);
                default:
                    return false;
            }
        }

        private bool TryCollectDynVTableGlobals(
            SsaValueReference reference,
            ISet<string> vtableGlobals,
            ISet<string> visitingValueNames)
        {
            if (!visitingValueNames.Add(reference.Name))
            {
                return false;
            }

            try
            {
                if (_definitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryCollectDynVTableGlobals(definition, vtableGlobals, visitingValueNames);
                }

                if (_phis.TryGetValue(reference.Name, out var phi))
                {
                    if (phi.Incomings.Count == 0)
                    {
                        return false;
                    }

                    foreach (var incoming in phi.Incomings)
                    {
                        if (!TryCollectDynVTableGlobals(incoming.Value, vtableGlobals, visitingValueNames))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return false;
            }
            finally
            {
                visitingValueNames.Remove(reference.Name);
            }
        }

        private bool TryCollectDynVTableGlobals(
            SsaRValue value,
            ISet<string> vtableGlobals,
            ISet<string> visitingValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    return TryCollectDynVTableGlobals(use.Value, vtableGlobals, visitingValueNames);
                case SsaConvertRValue convert:
                    return TryCollectDynVTableGlobals(convert.Operand, vtableGlobals, visitingValueNames);
                case SsaSelectRValue select:
                    return TryCollectDynVTableGlobals(select.WhenTrue, vtableGlobals, visitingValueNames)
                        && TryCollectDynVTableGlobals(select.WhenFalse, vtableGlobals, visitingValueNames);
                case SsaExtractIndexRValue
                {
                    ElementIndex: 1,
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent
                } extractIndex:
                    return TryCollectDynObjectVTableGlobals(extractIndex.Target, vtableGlobals, visitingValueNames);
                case SsaInsertIndexRValue
                {
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    ElementIndex: 1
                } insertIndex:
                    return TryCollectDynVTableGlobals(insertIndex.Value, vtableGlobals, visitingValueNames);
                case SsaInsertIndexRValue
                {
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent
                } insertIndex:
                    return TryCollectDynObjectVTableGlobals(insertIndex.Target, vtableGlobals, visitingValueNames);
                default:
                    return false;
            }
        }

        private bool TryCollectDynObjectVTableGlobals(
            SsaValue value,
            ISet<string> vtableGlobals,
            ISet<string> visitingValueNames)
        {
            return value switch
            {
                SsaValueReference reference => TryCollectDynObjectVTableGlobals(reference, vtableGlobals, visitingValueNames),
                _ => false
            };
        }

        private bool TryCollectDynObjectVTableGlobals(
            SsaValueReference reference,
            ISet<string> vtableGlobals,
            ISet<string> visitingValueNames)
        {
            if (!visitingValueNames.Add(reference.Name))
            {
                return false;
            }

            try
            {
                if (_definitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryCollectDynObjectVTableGlobals(definition, vtableGlobals, visitingValueNames);
                }

                if (_phis.TryGetValue(reference.Name, out var phi))
                {
                    if (phi.Incomings.Count == 0)
                    {
                        return false;
                    }

                    foreach (var incoming in phi.Incomings)
                    {
                        if (!TryCollectDynObjectVTableGlobals(incoming.Value, vtableGlobals, visitingValueNames))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return false;
            }
            finally
            {
                visitingValueNames.Remove(reference.Name);
            }
        }

        private bool TryCollectDynObjectVTableGlobals(
            SsaRValue value,
            ISet<string> vtableGlobals,
            ISet<string> visitingValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    return TryCollectDynObjectVTableGlobals(use.Value, vtableGlobals, visitingValueNames);
                case SsaConvertRValue convert:
                    return TryCollectDynObjectVTableGlobals(convert.Operand, vtableGlobals, visitingValueNames);
                case SsaSelectRValue select:
                    return TryCollectDynObjectVTableGlobals(select.WhenTrue, vtableGlobals, visitingValueNames)
                        && TryCollectDynObjectVTableGlobals(select.WhenFalse, vtableGlobals, visitingValueNames);
                case SsaInsertIndexRValue
                {
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent,
                    ElementIndex: 1
                } insertIndex:
                    return TryCollectDynVTableGlobals(insertIndex.Value, vtableGlobals, visitingValueNames);
                case SsaInsertIndexRValue
                {
                    OperationFamily: IndexedElementOperationFamily.DynTraitComponent
                } insertIndex:
                    return TryCollectDynObjectVTableGlobals(insertIndex.Target, vtableGlobals, visitingValueNames);
                default:
                    return false;
            }
        }

        private bool TryCollectClosureInvokeTargets(
            SsaValue value,
            IDictionary<string, SsaFunctionAddressValue> targets,
            ISet<string> visitingValueNames)
        {
            switch (value)
            {
                case SsaClosureValue closure:
                    targets[closure.InvokeFunctionName] = new SsaFunctionAddressValue(
                        closure.InvokeFunctionName,
                        CallableValueFacts.BuildClosureInvokeFunctionPointerType(closure.Type));
                    return true;
                case SsaValueReference reference:
                    return TryCollectClosureInvokeTargets(reference, targets, visitingValueNames);
                default:
                    return false;
            }
        }

        private bool TryCollectClosureInvokeTargets(
            SsaValueReference reference,
            IDictionary<string, SsaFunctionAddressValue> targets,
            ISet<string> visitingValueNames)
        {
            if (!visitingValueNames.Add(reference.Name))
            {
                return false;
            }

            try
            {
                if (_definitions.TryGetValue(reference.Name, out var definition))
                {
                    return TryCollectClosureInvokeTargets(definition, targets, visitingValueNames);
                }

                if (_phis.TryGetValue(reference.Name, out var phi))
                {
                    if (phi.Incomings.Count == 0)
                    {
                        return false;
                    }

                    foreach (var incoming in phi.Incomings)
                    {
                        if (!TryCollectClosureInvokeTargets(incoming.Value, targets, visitingValueNames))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return false;
            }
            finally
            {
                visitingValueNames.Remove(reference.Name);
            }
        }

        private bool TryCollectClosureInvokeTargets(
            SsaRValue value,
            IDictionary<string, SsaFunctionAddressValue> targets,
            ISet<string> visitingValueNames)
        {
            switch (value)
            {
                case SsaUseRValue use:
                    return TryCollectClosureInvokeTargets(use.Value, targets, visitingValueNames);
                case SsaSelectRValue select
                    when select.Type.Kind == StarkTypeKind.Closure:
                    return TryCollectClosureInvokeTargets(select.WhenTrue, targets, visitingValueNames)
                        && TryCollectClosureInvokeTargets(select.WhenFalse, targets, visitingValueNames);
                default:
                    return false;
            }
        }
    }
}
