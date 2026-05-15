using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaDirectCallDevirtualizer
{
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

    private static SsaFunction OptimizeFunction(SsaFunction function)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var functionPointerTargets = FunctionPointerTargetFacts.Build(function);
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

    private static SsaInstruction OptimizeInstruction(
        SsaInstruction instruction,
        FunctionPointerTargetFacts functionPointerTargets)
    {
        return instruction is SsaValueInstruction valueInstruction
               && TryDevirtualizeDirectFunctionAddressCall(valueInstruction.Value, functionPointerTargets, out var directCall)
            ? valueInstruction with { Value = directCall }
            : instruction;
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

    private sealed class FunctionPointerTargetFacts
    {
        private readonly IReadOnlyDictionary<string, SsaRValue> _definitions;
        private readonly IReadOnlyDictionary<string, SsaPhi> _phis;

        private FunctionPointerTargetFacts(
            IReadOnlyDictionary<string, SsaRValue> definitions,
            IReadOnlyDictionary<string, SsaPhi> phis)
        {
            _definitions = definitions;
            _phis = phis;
        }

        public static FunctionPointerTargetFacts Build(SsaFunction function)
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

            return new FunctionPointerTargetFacts(definitions, phis);
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
