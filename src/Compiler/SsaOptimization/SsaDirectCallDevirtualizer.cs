using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaDirectCallDevirtualizer
{
    private readonly IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> _dynVTableSlotTargets;

    public SsaDirectCallDevirtualizer(TypeCheckModel? typeModel = null)
    {
        _dynVTableSlotTargets = BuildDynVTableSlotTargets(typeModel);
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
        var changed = false;
        var blocks = function.Blocks
            .Select(block =>
            {
                var blockChanged = false;
                var instructions = block.Instructions
                    .Select(instruction =>
                    {
                        var optimized = OptimizeInstruction(instruction, functionPointerTargets);
                        blockChanged |= !ReferenceEquals(optimized, instruction);
                        return optimized;
                    })
                    .ToArray();

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

    private static IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> BuildDynVTableSlotTargets(TypeCheckModel? typeModel)
    {
        if (typeModel is null)
        {
            return EmptyDynVTableSlotTargets;
        }

        var targets = new Dictionary<DynVTableSlotKey, SsaFunctionAddressValue>();
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
                }
            }
        }

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

        return StarkTypeSymbols.FunctionPointer(signature.Kind, signature.ReturnType, parameterTypes);
    }

    private static IReadOnlyDictionary<DynVTableSlotKey, SsaFunctionAddressValue> EmptyDynVTableSlotTargets { get; } =
        new Dictionary<DynVTableSlotKey, SsaFunctionAddressValue>();

    private readonly record struct DynVTableSlotKey(string VtableGlobalName, int SlotIndex);

    private static SsaInstruction OptimizeInstruction(
        SsaInstruction instruction,
        FunctionPointerTargetFacts functionPointerTargets)
    {
        if (instruction is SsaValueInstruction valueInstruction
            && TryDevirtualizeDirectFunctionAddressCall(valueInstruction.Value, functionPointerTargets, out var directCall))
        {
            return valueInstruction with { Value = directCall };
        }

        if (instruction is SsaIndirectCallInstruction indirectCall
            && TryDevirtualizeDirectFunctionAddressCall(indirectCall, functionPointerTargets, out var directCallInstruction))
        {
            return directCallInstruction;
        }

        return instruction;
    }

    private static bool TryDevirtualizeDirectFunctionAddressCall(
        SsaRValue value,
        FunctionPointerTargetFacts functionPointerTargets,
        out SsaCallRValue directCall)
    {
        directCall = default!;

        if (value is not SsaIndirectCallRValue indirectCall
            || !functionPointerTargets.TryGetSingletonTarget(indirectCall.Target, out var functionAddress))
        {
            return false;
        }

        directCall = new SsaCallRValue(
            functionAddress.FunctionName,
            indirectCall.Arguments,
            indirectCall.Type,
            indirectCall.Text,
            indirectCall.IndirectArgumentLocalNames,
            SourceReturnType: indirectCall.SourceReturnType,
            indirectCall.IndirectArgumentAddresses);
        return true;
    }

    private static bool TryDevirtualizeDirectFunctionAddressCall(
        SsaIndirectCallInstruction indirectCall,
        FunctionPointerTargetFacts functionPointerTargets,
        out SsaCallInstruction directCall)
    {
        directCall = default!;

        if (!functionPointerTargets.TryGetSingletonTarget(indirectCall.Target, out var functionAddress))
        {
            return false;
        }

        directCall = new SsaCallInstruction(
            functionAddress.FunctionName,
            indirectCall.Arguments,
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
