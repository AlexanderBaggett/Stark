using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal static class SsaAddressTakenFunctionPruner
{
    public static SsaIrModule Prune(SsaIrModule module)
    {
        if (module.AddressTakenFunctions.Count == 0)
        {
            return module;
        }

        var referencedFunctions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in module.Functions)
        {
            AddReferencedFunctionAddresses(function, referencedFunctions);
        }

        var prunedFunctions = module.AddressTakenFunctions
            .Where(referencedFunctions.Contains)
            .ToArray();

        return prunedFunctions.Length == module.AddressTakenFunctions.Count
            ? module
            : module with { AddressTakenFunctionRecords = prunedFunctions };
    }

    private static void AddReferencedFunctionAddresses(
        SsaFunction function,
        HashSet<string> referencedFunctions)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    AddReferencedFunctionAddress(incoming.Value, referencedFunctions);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                AddReferencedFunctionAddresses(instruction, referencedFunctions);
            }

            AddReferencedFunctionAddresses(block.Terminator, referencedFunctions);
        }
    }

    private static void AddReferencedFunctionAddresses(
        SsaInstruction instruction,
        HashSet<string> referencedFunctions)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                AddReferencedFunctionAddresses(valueInstruction.Value, referencedFunctions);
                break;
            case SsaStoreLocalInstruction storeLocal:
                AddReferencedFunctionAddress(storeLocal.Value, referencedFunctions);
                break;
            case SsaStoreIndirectInstruction storeIndirect:
                AddReferencedFunctionAddress(storeIndirect.Address, referencedFunctions);
                AddReferencedFunctionAddress(storeIndirect.Value, referencedFunctions);
                break;
            case SsaCopyMemoryInstruction copyMemory:
                AddReferencedFunctionAddress(copyMemory.DestinationAddress, referencedFunctions);
                AddReferencedFunctionAddress(copyMemory.SourceAddress, referencedFunctions);
                break;
            case SsaStoreGlobalInstruction storeGlobal:
                AddReferencedFunctionAddress(storeGlobal.Value, referencedFunctions);
                break;
        }
    }

    private static void AddReferencedFunctionAddresses(
        SsaRValue value,
        HashSet<string> referencedFunctions)
    {
        switch (value)
        {
            case SsaUseRValue use:
                AddReferencedFunctionAddress(use.Value, referencedFunctions);
                break;
            case SsaUnaryRValue unary:
                AddReferencedFunctionAddress(unary.Operand, referencedFunctions);
                break;
            case SsaBinaryRValue binary:
                AddReferencedFunctionAddress(binary.Left, referencedFunctions);
                AddReferencedFunctionAddress(binary.Right, referencedFunctions);
                break;
            case SsaSelectRValue select:
                AddReferencedFunctionAddress(select.Condition, referencedFunctions);
                AddReferencedFunctionAddress(select.WhenTrue, referencedFunctions);
                AddReferencedFunctionAddress(select.WhenFalse, referencedFunctions);
                break;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    AddReferencedFunctionAddress(argument, referencedFunctions);
                }

                if (call.IndirectArgumentAddresses is not null)
                {
                    foreach (var address in call.IndirectArgumentAddresses)
                    {
                        if (address is not null)
                        {
                            AddReferencedFunctionAddress(address, referencedFunctions);
                        }
                    }
                }

                break;
            case SsaIndirectCallRValue indirectCall:
                AddReferencedFunctionAddress(indirectCall.Target, referencedFunctions);

                foreach (var argument in indirectCall.Arguments)
                {
                    AddReferencedFunctionAddress(argument, referencedFunctions);
                }

                foreach (var address in indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    AddReferencedFunctionAddress(address, referencedFunctions);
                }

                break;
            case SsaConvertRValue convert:
                AddReferencedFunctionAddress(convert.Operand, referencedFunctions);
                break;
            case SsaExtractFieldRValue extractField:
                AddReferencedFunctionAddress(extractField.Target, referencedFunctions);
                break;
            case SsaInsertFieldRValue insertField:
                AddReferencedFunctionAddress(insertField.Target, referencedFunctions);
                AddReferencedFunctionAddress(insertField.Value, referencedFunctions);
                break;
            case SsaExtractIndexRValue extractIndex:
                AddReferencedFunctionAddress(extractIndex.Target, referencedFunctions);
                break;
            case SsaInsertIndexRValue insertIndex:
                AddReferencedFunctionAddress(insertIndex.Target, referencedFunctions);
                AddReferencedFunctionAddress(insertIndex.Value, referencedFunctions);
                break;
            case SsaLoadSliceElementRValue loadSlice:
                AddReferencedFunctionAddress(loadSlice.Slice, referencedFunctions);
                AddReferencedFunctionAddress(loadSlice.Index, referencedFunctions);
                break;
            case SsaTextSliceRValue textSlice:
                AddReferencedFunctionAddress(textSlice.TextValue, referencedFunctions);
                AddReferencedFunctionAddress(textSlice.Start, referencedFunctions);
                AddReferencedFunctionAddress(textSlice.Length, referencedFunctions);
                break;
            case SsaFieldAddressRValue fieldAddress:
                AddReferencedFunctionAddress(fieldAddress.Address, referencedFunctions);
                break;
            case SsaElementAddressRValue elementAddress:
                AddReferencedFunctionAddress(elementAddress.Address, referencedFunctions);

                if (elementAddress.Index is not null)
                {
                    AddReferencedFunctionAddress(elementAddress.Index, referencedFunctions);
                }

                break;
            case SsaSliceElementAddressRValue sliceElementAddress:
                AddReferencedFunctionAddress(sliceElementAddress.Slice, referencedFunctions);
                AddReferencedFunctionAddress(sliceElementAddress.Index, referencedFunctions);
                break;
            case SsaLoadIndirectRValue loadIndirect:
                AddReferencedFunctionAddress(loadIndirect.Address, referencedFunctions);
                break;
        }
    }

    private static void AddReferencedFunctionAddresses(
        SsaTerminator terminator,
        HashSet<string> referencedFunctions)
    {
        if (terminator.Condition is not null)
        {
            AddReferencedFunctionAddress(terminator.Condition, referencedFunctions);
        }

        if (terminator.Value is not null)
        {
            AddReferencedFunctionAddress(terminator.Value, referencedFunctions);
        }

        if (terminator.SwitchCases is null)
        {
            return;
        }

        foreach (var switchCase in terminator.SwitchCases)
        {
            AddReferencedFunctionAddress(switchCase.MatchValue, referencedFunctions);
        }
    }

    private static void AddReferencedFunctionAddress(
        SsaValue value,
        HashSet<string> referencedFunctions)
    {
        if (value is SsaFunctionAddressValue functionAddress)
        {
            referencedFunctions.Add(functionAddress.FunctionName);
        }
        else if (value is SsaClosureValue closure)
        {
            referencedFunctions.Add(closure.InvokeFunctionName);
        }
    }
}

