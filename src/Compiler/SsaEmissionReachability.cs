namespace Stark.Compiler;

/// <summary>
/// Enumerates every function name referenced from SSA bodies: direct calls, function
/// addresses, and closure invoke targets. Shared by inline-time lambda pruning and
/// emission-reachability pruning so both see the same reference kinds.
/// </summary>
internal static class SsaFunctionReferenceWalker
{
    public static void CollectReferencedFunctions(
        SsaFunction function,
        ISet<string> referencedFunctions)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    CollectReferencedFunctions(incoming.Value, referencedFunctions);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                CollectReferencedFunctions(instruction, referencedFunctions);
            }

            CollectReferencedFunctions(block.Terminator, referencedFunctions);
        }
    }

    public static void CollectReferencedFunctions(
        SsaInstruction instruction,
        ISet<string> referencedFunctions)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                CollectReferencedFunctions(valueInstruction.Value, referencedFunctions);
                break;
            case SsaCallInstruction call:
                referencedFunctions.Add(call.FunctionName);
                foreach (var argument in call.Arguments)
                {
                    CollectReferencedFunctions(argument, referencedFunctions);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    CollectReferencedFunctions(address, referencedFunctions);
                }

                break;
            case SsaIndirectCallInstruction call:
                CollectReferencedFunctions(call.Target, referencedFunctions);
                foreach (var argument in call.Arguments)
                {
                    CollectReferencedFunctions(argument, referencedFunctions);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    CollectReferencedFunctions(address, referencedFunctions);
                }

                break;
            case SsaStoreLocalInstruction storeLocal:
                CollectReferencedFunctions(storeLocal.Value, referencedFunctions);
                break;
            case SsaCopyMemoryInstruction copyMemory:
                CollectReferencedFunctions(copyMemory.DestinationAddress, referencedFunctions);
                CollectReferencedFunctions(copyMemory.SourceAddress, referencedFunctions);
                break;
            case SsaStoreIndirectInstruction storeIndirect:
                CollectReferencedFunctions(storeIndirect.Address, referencedFunctions);
                CollectReferencedFunctions(storeIndirect.Value, referencedFunctions);
                break;
            case SsaStoreGlobalInstruction storeGlobal:
                CollectReferencedFunctions(storeGlobal.Value, referencedFunctions);
                break;
            case SsaArenaFrameEnterInstruction:
            case SsaArenaFrameLeaveInstruction:
                break;
        }
    }

    public static void CollectReferencedFunctions(
        SsaRValue value,
        ISet<string> referencedFunctions)
    {
        switch (value)
        {
            case SsaUseRValue use:
                CollectReferencedFunctions(use.Value, referencedFunctions);
                break;
            case SsaUnaryRValue unary:
                CollectReferencedFunctions(unary.Operand, referencedFunctions);
                break;
            case SsaBinaryRValue binary:
                CollectReferencedFunctions(binary.Left, referencedFunctions);
                CollectReferencedFunctions(binary.Right, referencedFunctions);
                break;
            case SsaSelectRValue select:
                CollectReferencedFunctions(select.Condition, referencedFunctions);
                CollectReferencedFunctions(select.WhenTrue, referencedFunctions);
                CollectReferencedFunctions(select.WhenFalse, referencedFunctions);
                break;
            case SsaCallRValue call:
                referencedFunctions.Add(call.FunctionName);
                foreach (var argument in call.Arguments)
                {
                    CollectReferencedFunctions(argument, referencedFunctions);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    CollectReferencedFunctions(address, referencedFunctions);
                }

                break;
            case SsaIndirectCallRValue indirectCall:
                CollectReferencedFunctions(indirectCall.Target, referencedFunctions);
                foreach (var argument in indirectCall.Arguments)
                {
                    CollectReferencedFunctions(argument, referencedFunctions);
                }

                foreach (var address in indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    CollectReferencedFunctions(address, referencedFunctions);
                }

                break;
            case SsaConvertRValue convert:
                CollectReferencedFunctions(convert.Operand, referencedFunctions);
                break;
            case SsaExtractFieldRValue extractField:
                CollectReferencedFunctions(extractField.Target, referencedFunctions);
                break;
            case SsaInsertFieldRValue insertField:
                CollectReferencedFunctions(insertField.Target, referencedFunctions);
                CollectReferencedFunctions(insertField.Value, referencedFunctions);
                break;
            case SsaExtractIndexRValue extractIndex:
                CollectReferencedFunctions(extractIndex.Target, referencedFunctions);
                break;
            case SsaInsertIndexRValue insertIndex:
                CollectReferencedFunctions(insertIndex.Target, referencedFunctions);
                CollectReferencedFunctions(insertIndex.Value, referencedFunctions);
                break;
            case SsaMakeSliceFromPointerRValue makeSlice:
                CollectReferencedFunctions(makeSlice.Pointer, referencedFunctions);
                CollectReferencedFunctions(makeSlice.Length, referencedFunctions);
                break;
            case SsaDynamicStorageAllocationRValue allocation:
                CollectReferencedFunctions(allocation.Capacity, referencedFunctions);
                break;
            case SsaDynamicStorageFreeRValue free:
                CollectReferencedFunctions(free.Storage, referencedFunctions);
                break;
            case SsaHeapStorageFreeRValue free:
                CollectReferencedFunctions(free.Pointer, referencedFunctions);
                break;
            case SsaDynamicStorageReserveRValue reserve:
                CollectReferencedFunctions(reserve.StorageAddress, referencedFunctions);
                CollectReferencedFunctions(reserve.AdditionalCapacity, referencedFunctions);
                break;
            case SsaDynamicStorageTryReserveRValue reserve:
                CollectReferencedFunctions(reserve.StorageAddress, referencedFunctions);
                CollectReferencedFunctions(reserve.AdditionalCapacity, referencedFunctions);
                break;
            case SsaDynamicStorageTryReserveCapacityRValue reserve:
                CollectReferencedFunctions(reserve.StorageAddress, referencedFunctions);
                CollectReferencedFunctions(reserve.TargetCapacity, referencedFunctions);
                break;
            case SsaDynamicStorageMoveLastRValue moveLast:
                CollectReferencedFunctions(moveLast.StorageAddress, referencedFunctions);
                break;
            case SsaDynamicStorageMoveAtRValue moveAt:
                CollectReferencedFunctions(moveAt.StorageAddress, referencedFunctions);
                CollectReferencedFunctions(moveAt.Index, referencedFunctions);
                break;
            case SsaLoadSliceElementRValue loadSlice:
                CollectReferencedFunctions(loadSlice.Slice, referencedFunctions);
                CollectReferencedFunctions(loadSlice.Index, referencedFunctions);
                break;
            case SsaTextSliceRValue textSlice:
                CollectReferencedFunctions(textSlice.TextValue, referencedFunctions);
                CollectReferencedFunctions(textSlice.Start, referencedFunctions);
                CollectReferencedFunctions(textSlice.Length, referencedFunctions);
                break;
            case SsaFieldAddressRValue fieldAddress:
                CollectReferencedFunctions(fieldAddress.Address, referencedFunctions);
                break;
            case SsaElementAddressRValue elementAddress:
                CollectReferencedFunctions(elementAddress.Address, referencedFunctions);
                if (elementAddress.Index is not null)
                {
                    CollectReferencedFunctions(elementAddress.Index, referencedFunctions);
                }

                break;
            case SsaSliceElementAddressRValue sliceElementAddress:
                CollectReferencedFunctions(sliceElementAddress.Slice, referencedFunctions);
                CollectReferencedFunctions(sliceElementAddress.Index, referencedFunctions);
                break;
            case SsaLoadIndirectRValue loadIndirect:
                CollectReferencedFunctions(loadIndirect.Address, referencedFunctions);
                break;
        }
    }

    public static void CollectReferencedFunctions(
        SsaTerminator terminator,
        ISet<string> referencedFunctions)
    {
        if (terminator.Value is not null)
        {
            CollectReferencedFunctions(terminator.Value, referencedFunctions);
        }

        if (terminator.Condition is not null)
        {
            CollectReferencedFunctions(terminator.Condition, referencedFunctions);
        }

        foreach (var switchCase in terminator.SwitchCases ?? [])
        {
            CollectReferencedFunctions(switchCase.MatchValue, referencedFunctions);
        }
    }

    public static void CollectReferencedFunctions(
        SsaValue value,
        ISet<string> referencedFunctions)
    {
        switch (value)
        {
            case SsaFunctionAddressValue functionAddress:
                referencedFunctions.Add(functionAddress.FunctionName);
                break;
            case SsaClosureValue closure:
                referencedFunctions.Add(closure.InvokeFunctionName);
                break;
        }
    }
}

/// <summary>
/// Lowers only the MIR functions that can influence the emitted LLVM module: this
/// module's own functions, specialization strategy symbols, hot or exported functions,
/// address-taken functions, dyn-trait vtable entries, and everything they reference.
/// CLI binary outputs use this because imported-module functions outside that set are
/// lowered and optimized today only to be discarded at emission.
/// </summary>
internal static class SsaEmissionReachability
{
    public static SsaIrModule LowerReachableFromEmission(
        SsaLowerer lowerer,
        MidLevelIrModule mir,
        LoadedModuleSet loadedModules,
        TypeCheckModel typeModel,
        FunctionEffectModel effectModel,
        SpecializationCodegenStrategyModel specializationCodegenStrategy)
    {
        var importedModulePrefixes = loadedModules.ImportedModules
            .Select(static imported => imported.SyntaxModel.ModuleName + ".")
            .ToArray();
        if (importedModulePrefixes.Length == 0)
        {
            return lowerer.Lower(mir);
        }

        lowerer.SeedFunctionSignatures(mir);

        var mirFunctionsByName = new Dictionary<string, MidLevelIrFunction>(StringComparer.Ordinal);
        foreach (var function in mir.Functions)
        {
            mirFunctionsByName[function.Name] = function;
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();

        void Keep(string functionName)
        {
            if (kept.Add(functionName))
            {
                pending.Push(functionName);
            }
        }

        foreach (var function in mir.Functions)
        {
            if (!IsImportedModuleFunctionName(function.Name, importedModulePrefixes))
            {
                Keep(function.Name);
            }
        }

        foreach (var functionName in mir.AddressTakenFunctions)
        {
            Keep(functionName);
        }

        foreach (var strategy in specializationCodegenStrategy.Functions)
        {
            Keep(strategy.SymbolName);
        }

        foreach (var (functionName, effects) in effectModel.Functions)
        {
            if (effects.IsHot)
            {
                Keep(functionName);
            }
        }

        foreach (var (functionName, signature) in typeModel.Functions)
        {
            if (signature.Visibility == StarkVisibility.Export)
            {
                Keep(functionName);
            }
        }

        foreach (var concreteType in typeModel.NamedTypes.Values)
        {
            if (concreteType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || concreteType.ImplementedTraits.Count == 0)
            {
                continue;
            }

            foreach (var traitName in concreteType.ImplementedTraits.Distinct())
            {
                foreach (var slot in DynTraitFacts.GetVtableLayout(traitName, typeModel.Functions))
                {
                    if (TryResolveVtableSlotFunction(typeModel, concreteType.Name, slot.MethodName, out var slotFunction))
                    {
                        Keep(slotFunction.Name);
                    }
                }
            }

            Keep(DynTraitFacts.BuildDropThunkName(concreteType.Name));
        }

        Keep(CallableValueFacts.EmptyClosureDropFunctionName);

        var lowered = new Dictionary<string, SsaFunction>(StringComparer.Ordinal);

        // Closure drop thunks are referenced by emission convention rather than by SSA
        // operands, so re-run the walk until kept lambdas stop adding drop functions.
        while (true)
        {
            while (pending.Count != 0)
            {
                var functionName = pending.Pop();
                if (!mirFunctionsByName.TryGetValue(functionName, out var mirFunction)
                    || lowered.ContainsKey(functionName))
                {
                    continue;
                }

                var ssaFunction = lowerer.LowerFunction(mirFunction);
                lowered[functionName] = ssaFunction;

                var references = new HashSet<string>(StringComparer.Ordinal);
                SsaFunctionReferenceWalker.CollectReferencedFunctions(ssaFunction, references);
                foreach (var reference in references)
                {
                    Keep(reference);
                }
            }

            var added = false;
            foreach (var lambda in typeModel.ClosureLambdas)
            {
                if (lambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Heap
                    && lambda.HasCaptures
                    && kept.Contains(lambda.FunctionName))
                {
                    var dropFunctionName = CallableValueFacts.BuildClosureDropFunctionName(lambda.FunctionName);
                    if (!kept.Contains(dropFunctionName))
                    {
                        Keep(dropFunctionName);
                        added = true;
                    }
                }
            }

            if (!added)
            {
                break;
            }
        }

        var functions = new List<SsaFunction>(lowered.Count);
        foreach (var function in mir.Functions)
        {
            if (lowered.TryGetValue(function.Name, out var ssaFunction))
            {
                functions.Add(ssaFunction);
            }
        }

        return new SsaIrModule(mir.ModuleName, functions, mir.AddressTakenFunctions);
    }

    private static bool IsImportedModuleFunctionName(string functionName, IReadOnlyList<string> importedModulePrefixes)
    {
        foreach (var prefix in importedModulePrefixes)
        {
            if (functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveVtableSlotFunction(
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
}
