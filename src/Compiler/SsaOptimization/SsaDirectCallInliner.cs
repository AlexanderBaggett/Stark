using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed class SsaDirectCallInliner
{
    private const int MaxOrdinaryInlineInstructionCount = 12;
    private const int MaxLawInlineInstructionCount = 20;
    private const int MaxInlineClosureSpecializationInstructionCount = 512;
    private const int MaxInlineSitesPerFunction = 64;
    private const int MaxInlineRounds = 4;
    private static readonly IReadOnlyDictionary<string, SsaValue> EmptyValueReplacements =
        new Dictionary<string, SsaValue>(StringComparer.Ordinal);

    private readonly FunctionEffectModel _effectModel;
    private readonly IReadOnlySet<string> _modulePrivateFunctionNames;
    private readonly IReadOnlySet<string> _declaredLawFunctionNames;

    public SsaDirectCallInliner(
        FunctionEffectModel effectModel,
        IReadOnlySet<string> modulePrivateFunctionNames,
        IReadOnlySet<string> declaredLawFunctionNames)
    {
        _effectModel = effectModel;
        _modulePrivateFunctionNames = modulePrivateFunctionNames;
        _declaredLawFunctionNames = declaredLawFunctionNames;
    }

    public SsaIrModule Optimize(SsaIrModule module)
    {
        var candidates = RemoveRecursiveCandidates(CollectCandidates(module));
        if (candidates.Count == 0)
        {
            return module;
        }

        var current = module;
        var changedAny = false;

        for (var round = 0; round < MaxInlineRounds; round++)
        {
            var changedRound = false;
            var functions = current.Functions
                .Select(function =>
                {
                    var optimized = InlineFunction(function, candidates);
                    changedRound |= !ReferenceEquals(optimized, function);
                    return optimized;
                })
                .ToArray();

            if (!changedRound)
            {
                break;
            }

            changedAny = true;
            current = new SsaIrModule(current.ModuleName, functions, current.AddressTakenFunctionRecords);
        }

        return changedAny
            ? current
            : module;
    }

    private IReadOnlyDictionary<string, InlineCandidate> CollectCandidates(SsaIrModule module)
    {
        var candidates = new Dictionary<string, InlineCandidate>(StringComparer.Ordinal);

        foreach (var function in module.Functions)
        {
            if (!TryBuildCandidate(function, out var candidate))
            {
                continue;
            }

            candidates[function.Name] = candidate;
        }

        return candidates;
    }

    private static IReadOnlyDictionary<string, InlineCandidate> RemoveRecursiveCandidates(
        IReadOnlyDictionary<string, InlineCandidate> candidates)
    {
        var result = new Dictionary<string, InlineCandidate>(candidates, StringComparer.Ordinal);

        foreach (var candidate in candidates.Values)
        {
            if (CanReachCandidate(candidate.Function.Name, candidate.Function.Name, candidates, []))
            {
                result.Remove(candidate.Function.Name);
            }
        }

        return result;
    }

    private static bool CanReachCandidate(
        string originFunctionName,
        string currentFunctionName,
        IReadOnlyDictionary<string, InlineCandidate> candidates,
        HashSet<string> visited)
    {
        if (!candidates.TryGetValue(currentFunctionName, out var candidate))
        {
            return false;
        }

        foreach (var callee in candidate.DirectCalls)
        {
            if (string.Equals(callee, originFunctionName, StringComparison.Ordinal))
            {
                return true;
            }

            if (visited.Add(callee)
                && CanReachCandidate(originFunctionName, callee, candidates, visited))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBuildCandidate(SsaFunction function, out InlineCandidate candidate)
    {
        candidate = default!;

        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || !_effectModel.Functions.TryGetValue(function.Name, out var effects)
            || effects.IsFfi
            || effects.IsCold
            || effects.InlinePreference == InlinePreference.NoInline
            || !IsInlineSafeType(function.ReturnType)
            || function.Parameters.Any(static parameter => !IsInlineSafeType(parameter.Type))
            || function.Blocks.Count == 0)
        {
            return false;
        }

        if (!function.Blocks.Any(block => block.Id == function.EntryBlockId))
        {
            return false;
        }

        var hasInlineClosureParameter = function.Parameters.Any(static parameter => IsInlineClosureParameter(parameter.Type));
        var isDeclaredLaw = _declaredLawFunctionNames.Contains(function.Name)
            || FunctionKindFacts.IsLaw(effects.Kind);
        var maxInlineInstructionCount = hasInlineClosureParameter
            ? MaxInlineClosureSpecializationInstructionCount
            : GetMaxInlineInstructionCount(isDeclaredLaw);

        var instructionCount = 0;
        var directCalls = new List<string>();
        var definedNames = new HashSet<string>(StringComparer.Ordinal);
        var singleBlockInstructions = new List<SsaInstruction>();
        SsaValue? singleBlockReturnValue = null;
        var usedValueNames = CollectUsedValueNames(function);

        foreach (var block in function.Blocks)
        {
            if (!IsInlineSafeTerminator(block.Terminator))
            {
                return false;
            }

            if (block.Terminator.Kind == SsaTerminatorKind.Return)
            {
                if (function.ReturnType.Kind == StarkTypeKind.Void)
                {
                    if (block.Terminator.Value is not null)
                    {
                        return false;
                    }
                }
                else if (block.Terminator.Value is null)
                {
                    return false;
                }
            }

            foreach (var phi in block.Phis)
            {
                definedNames.Add(phi.ResultName);
                if (!IsInlineSafeType(phi.Type)
                    || phi.Incomings.Any(static incoming => !IsInlineSafeValue(incoming.Value)))
                {
                    return false;
                }
            }

            foreach (var instruction in block.Instructions)
            {
                var resultIsUsed = instruction is SsaValueInstruction valueInstruction
                    && usedValueNames.Contains(valueInstruction.ResultName);
                if (!IsInlineSafeInstruction(
                        instruction,
                        function.Name,
                        hasInlineClosureParameter,
                        resultIsUsed))
                {
                    return false;
                }

                if (instruction is SsaValueInstruction definedValueInstruction)
                {
                    definedNames.Add(definedValueInstruction.ResultName);
                }

                instructionCount++;
                if (instructionCount > maxInlineInstructionCount)
                {
                    return false;
                }

                if (instruction is SsaValueInstruction { Value: SsaCallRValue call })
                {
                    directCalls.Add(call.FunctionName);
                }

                if (function.Blocks.Count == 1)
                {
                    singleBlockInstructions.Add(instruction);
                }
            }
        }

        if (function.Blocks.Count == 1)
        {
            var block = function.Blocks[0];
            if (block.Id != function.EntryBlockId
                || block.Phis.Count != 0
                || block.Terminator.Kind != SsaTerminatorKind.Return)
            {
                return false;
            }

            if (function.ReturnType.Kind == StarkTypeKind.Void)
            {
                if (block.Terminator.Value is not null)
                {
                    return false;
                }
            }
            else if (block.Terminator.Value is not { } returnValue
                     || !IsInlineSafeValue(returnValue))
            {
                return false;
            }

            singleBlockReturnValue = block.Terminator.Value;
        }
        else if (function.Blocks.Any(static block => block.Terminator.Kind == SsaTerminatorKind.Return
                                                    && block.Terminator.Value is not null
                                                    && !IsInlineSafeValue(block.Terminator.Value)))
        {
            return false;
        }

        var canInlineByDefault = hasInlineClosureParameter
            || IsInlineCandidateByPolicy(function, effects);
        var canInlineWithConstantArguments = !canInlineByDefault
                                             && function.Parameters.Count > 0
                                             && directCalls.Count == 0;
        if (!canInlineByDefault && !canInlineWithConstantArguments)
        {
            return false;
        }

        candidate = new InlineCandidate(
            function,
            function.Blocks,
            singleBlockInstructions,
            singleBlockReturnValue,
            definedNames,
            usedValueNames,
            directCalls,
            canInlineByDefault,
            canInlineWithConstantArguments);
        return true;
    }

    private bool IsInlineCandidateByPolicy(
        SsaFunction function,
        FunctionEffectProfile effects)
    {
        return effects.InlinePreference == InlinePreference.Inline
            || _modulePrivateFunctionNames.Contains(function.Name)
            || _declaredLawFunctionNames.Contains(function.Name);
    }

    private static int GetMaxInlineInstructionCount(bool isDeclaredLaw)
    {
        return isDeclaredLaw
            ? MaxLawInlineInstructionCount
            : MaxOrdinaryInlineInstructionCount;
    }

    private SsaFunction InlineFunction(
        SsaFunction function,
        IReadOnlyDictionary<string, InlineCandidate> candidates)
    {
        if (!function.HasBody
            || !function.SupportsDirectCodeGeneration
            || function.Blocks.Count == 0)
        {
            return function;
        }

        var replacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var usedValueNames = CollectDefinedValueNames(function);
        var nextBlockId = function.Blocks.Count == 0
            ? 0
            : function.Blocks.Max(static block => block.Id) + 1;
        var inlineSiteIndex = 0;
        var changed = false;
        var blocks = new List<SsaBasicBlock>(function.Blocks.Count);
        var continuationPredecessorRedirects = new Dictionary<int, int>();

        foreach (var block in function.Blocks)
        {
            var instructions = new List<SsaInstruction>(block.Instructions.Count);
            var blockWasSplit = false;

            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                var rewrittenInstruction = RewriteInstruction(instruction, replacements);

                if (inlineSiteIndex < MaxInlineSitesPerFunction
                    && rewrittenInstruction is SsaValueInstruction
                    {
                        Value: SsaCallRValue call
                    } valueInstruction
                    && TryInlineCall(
                        function,
                        valueInstruction,
                        call,
                        candidates,
                        inlineSiteIndex,
                        usedValueNames,
                        out var clonedInstructions,
                        out var replacement))
                {
                    instructions.AddRange(clonedInstructions);
                    if (replacement is not null)
                    {
                        replacements[valueInstruction.ResultName] = replacement;
                    }

                    inlineSiteIndex++;
                    changed = true;
                    continue;
                }

                if (inlineSiteIndex < MaxInlineSitesPerFunction
                    && rewrittenInstruction is SsaValueInstruction
                    {
                        Value: SsaCallRValue multiBlockCall
                    } multiBlockCallInstruction
                    && TryInlineMultiBlockCall(
                        function,
                        block,
                        instructions,
                        block.Instructions.Skip(instructionIndex + 1),
                        RewriteTerminator(block.Terminator, replacements),
                        multiBlockCallInstruction,
                        multiBlockCall,
                        candidates,
                        inlineSiteIndex,
                        usedValueNames,
                        ref nextBlockId,
                        out var continuationBlockId,
                        out var splitBlocks))
                {
                    blocks.AddRange(splitBlocks);
                    continuationPredecessorRedirects[block.Id] = continuationBlockId;
                    inlineSiteIndex++;
                    changed = true;
                    blockWasSplit = true;
                    break;
                }

                instructions.Add(rewrittenInstruction);
            }

            if (!blockWasSplit)
            {
                blocks.Add(block with { Instructions = instructions });
            }
        }

        if (!changed)
        {
            return function;
        }

        var rewrittenBlocks = blocks
            .Select(block => RewriteBlock(block, replacements, continuationPredecessorRedirects))
            .ToArray();

        return function with { Blocks = rewrittenBlocks };
    }

    private static bool TryInlineCall(
        SsaFunction caller,
        SsaValueInstruction callInstruction,
        SsaCallRValue call,
        IReadOnlyDictionary<string, InlineCandidate> candidates,
        int inlineSiteIndex,
        ISet<string> usedValueNames,
        out IReadOnlyList<SsaInstruction> clonedInstructions,
        out SsaValue? replacement)
    {
        clonedInstructions = [];
        replacement = null;

        if (!candidates.TryGetValue(call.FunctionName, out var candidate)
            || string.Equals(candidate.Function.Name, caller.Name, StringComparison.Ordinal)
            || candidate.Blocks.Count != 1
            || candidate.Function.Parameters.Count != call.Arguments.Count
            || (!candidate.CanInlineByDefault
                && (!candidate.CanInlineWithConstantArguments || !HasConstantSpecializationArgument(call)))
            || HasUnsupportedIndirectArgumentMetadata(call, candidate.Function.Parameters))
        {
            return false;
        }

        var returnsValue = call.Type.Kind != StarkTypeKind.Void;
        if (returnsValue)
        {
            if (candidate.ReturnValue is not { })
            {
                return false;
            }
        }
        else if (candidate.ReturnValue is not null)
        {
            return false;
        }

        var localReplacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var parameterAddressReplacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);

        var clones = new List<SsaInstruction>(candidate.Instructions.Count);
        AddParameterReplacements(
            caller,
            candidate,
            call,
            inlineSiteIndex,
            usedValueNames,
            localReplacements,
            clones,
            callInstruction.Location,
            parameterAddressReplacements);

        foreach (var candidateInstruction in candidate.Instructions)
        {
            if (candidateInstruction is not SsaValueInstruction valueInstruction)
            {
                clones.Add(RewriteInstruction(candidateInstruction, localReplacements));
                continue;
            }

            if (TryResolveParameterAddressAlias(
                    valueInstruction.Value,
                    parameterAddressReplacements,
                    out var parameterAddressAlias))
            {
                localReplacements[valueInstruction.ResultName] = parameterAddressAlias;
                continue;
            }

            if (!candidate.UsedValueNames.Contains(valueInstruction.ResultName)
                && IsInlineDroppableRValue(valueInstruction.Value))
            {
                continue;
            }

            var rewrittenValue = RewriteRValue(
                valueInstruction.Value,
                localReplacements,
                parameterAddressReplacements);
            var resultName = CreateFreshName(
                $"{valueInstruction.ResultName}_inl{inlineSiteIndex}",
                usedValueNames);

            clones.Add(new SsaValueInstruction(
                resultName,
                rewrittenValue,
                callInstruction.Location ?? valueInstruction.Location,
                valueInstruction.ScopedNoAliasGroups,
                valueInstruction.LoopAccessGroups));

            localReplacements[valueInstruction.ResultName] = new SsaValueReference(
                resultName,
                rewrittenValue.Type);
        }

        if (candidate.ReturnValue is { } returnValue)
        {
            replacement = RewriteValue(returnValue, localReplacements);
        }

        clonedInstructions = clones;
        return true;
    }

    private static bool TryInlineMultiBlockCall(
        SsaFunction caller,
        SsaBasicBlock callerBlock,
        IReadOnlyList<SsaInstruction> prefixInstructions,
        IEnumerable<SsaInstruction> suffixInstructions,
        SsaTerminator rewrittenCallerTerminator,
        SsaValueInstruction callInstruction,
        SsaCallRValue call,
        IReadOnlyDictionary<string, InlineCandidate> candidates,
        int inlineSiteIndex,
        ISet<string> usedValueNames,
        ref int nextBlockId,
        out int continuationBlockId,
        out IReadOnlyList<SsaBasicBlock> splitBlocks)
    {
        splitBlocks = [];
        continuationBlockId = -1;

        if (!candidates.TryGetValue(call.FunctionName, out var candidate)
            || candidate.Blocks.Count <= 1
            || candidate.Function.Parameters.Count != call.Arguments.Count
            || (!candidate.CanInlineByDefault
                && (!candidate.CanInlineWithConstantArguments || !HasConstantSpecializationArgument(call)))
            || HasUnsupportedIndirectArgumentMetadata(call, candidate.Function.Parameters))
        {
            return false;
        }

        var blockIdMap = new Dictionary<int, int>();
        foreach (var candidateBlock in candidate.Blocks)
        {
            blockIdMap[candidateBlock.Id] = nextBlockId++;
        }

        continuationBlockId = nextBlockId++;
        var localReplacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var parameterAddressReplacements = new Dictionary<string, SsaValue>(StringComparer.Ordinal);
        var parameterAliasInstructions = new List<SsaInstruction>();
        AddParameterReplacements(
            caller,
            candidate,
            call,
            inlineSiteIndex,
            usedValueNames,
            localReplacements,
            parameterAliasInstructions,
            callInstruction.Location,
            parameterAddressReplacements);

        foreach (var candidateBlock in candidate.Blocks)
        {
            foreach (var phi in candidateBlock.Phis)
            {
                localReplacements[phi.ResultName] = new SsaValueReference(
                    CreateFreshName($"{phi.ResultName}_inl{inlineSiteIndex}", usedValueNames),
                    phi.Type);
            }

            foreach (var instruction in candidateBlock.Instructions.OfType<SsaValueInstruction>())
            {
                localReplacements[instruction.ResultName] = new SsaValueReference(
                    CreateFreshName($"{instruction.ResultName}_inl{inlineSiteIndex}", usedValueNames),
                    instruction.Value.Type);
            }
        }

        var returnIncomings = new List<SsaPhiIncoming>();
        var clonedBlocks = new List<SsaBasicBlock>(candidate.Blocks.Count + 2)
        {
            callerBlock with
            {
                Instructions = prefixInstructions.Concat(parameterAliasInstructions).ToArray(),
                Terminator = new SsaTerminator(
                    SsaTerminatorKind.Goto,
                    [blockIdMap[candidate.Function.EntryBlockId]],
                    Location: callInstruction.Location)
            }
        };

        foreach (var candidateBlock in candidate.Blocks)
        {
            var clonedBlockId = blockIdMap[candidateBlock.Id];
            var clonedPhis = candidateBlock.Phis
                .Select(phi =>
                {
                    var replacement = (SsaValueReference)localReplacements[phi.ResultName];
                    return new SsaPhi(
                        replacement.Name,
                        $"{phi.VariableName}_inl{inlineSiteIndex}",
                        phi.Type,
                        phi.Incomings
                            .Select(incoming => new SsaPhiIncoming(
                                blockIdMap[incoming.PredecessorBlockId],
                                RewriteValue(incoming.Value, localReplacements)))
                            .ToArray(),
                        phi.Location ?? callInstruction.Location);
                })
                .ToArray();
            var clonedInstructions = candidateBlock.Instructions
                .Select(instruction => CloneInlineInstruction(
                    instruction,
                    localReplacements,
                    parameterAddressReplacements,
                    candidate.UsedValueNames,
                    callInstruction.Location,
                    inlineSiteIndex))
                .Where(static instruction => instruction is not null)
                .Cast<SsaInstruction>()
                .ToArray();
            var clonedTerminator = CloneInlineTerminator(
                candidateBlock.Terminator,
                clonedBlockId,
                continuationBlockId,
                blockIdMap,
                localReplacements,
                callInstruction,
                returnIncomings);

            clonedBlocks.Add(new SsaBasicBlock(
                clonedBlockId,
                $"{candidateBlock.Label}_inl{inlineSiteIndex}",
                clonedPhis,
                clonedInstructions,
                clonedTerminator));
        }

        var continuationPhis = call.Type.Kind == StarkTypeKind.Void
            ? []
            : new[]
            {
                new SsaPhi(
                    callInstruction.ResultName,
                    callInstruction.ResultName,
                    call.Type,
                    returnIncomings.ToArray(),
                    callInstruction.Location)
            };
        if (call.Type.Kind != StarkTypeKind.Void && returnIncomings.Count == 0)
        {
            return false;
        }

        clonedBlocks.Add(new SsaBasicBlock(
            continuationBlockId,
            $"{callerBlock.Label}_inl{inlineSiteIndex}_continue",
            continuationPhis,
            suffixInstructions
                .Select(instruction => RewriteInstruction(instruction, EmptyValueReplacements))
                .ToArray(),
            rewrittenCallerTerminator));

        splitBlocks = clonedBlocks;
        return true;
    }

    private static void AddParameterReplacements(
        SsaFunction caller,
        InlineCandidate candidate,
        SsaCallRValue call,
        int inlineSiteIndex,
        ISet<string> usedValueNames,
        IDictionary<string, SsaValue> localReplacements,
        ICollection<SsaInstruction> prologueInstructions,
        SourceLocation? callLocation,
        IDictionary<string, SsaValue>? parameterAddressReplacements = null)
    {
        for (var index = 0; index < candidate.Function.Parameters.Count; index++)
        {
            var parameter = candidate.Function.Parameters[index];
            var argument = call.Arguments[index];
            if (parameterAddressReplacements is not null
                && IsPointerBackedParameterType(parameter.Type))
            {
                var indirectAddress = call.IndirectArgumentAddresses is not null
                                      && index < call.IndirectArgumentAddresses.Count
                    ? call.IndirectArgumentAddresses[index]
                    : null;
                if (indirectAddress?.Type.Kind == StarkTypeKind.RawPointer)
                {
                    parameterAddressReplacements[parameter.Name] = ProtectInlineReplacementValue(
                        candidate,
                        indirectAddress,
                        $"arg_{parameter.Name}_addr_inl{inlineSiteIndex}",
                        usedValueNames,
                        prologueInstructions,
                        callLocation);
                }
                else if (argument.Type.Kind == StarkTypeKind.RawPointer)
                {
                    parameterAddressReplacements[parameter.Name] = ProtectInlineReplacementValue(
                        candidate,
                        argument,
                        $"arg_{parameter.Name}_addr_inl{inlineSiteIndex}",
                        usedValueNames,
                        prologueInstructions,
                        callLocation);
                }
                else if (TryCreateIndirectLocalAddressReplacement(
                             caller,
                             call,
                             index,
                             parameter.Type,
                             $"arg_{parameter.Name}_addr_inl{inlineSiteIndex}",
                             usedValueNames,
                             prologueInstructions,
                             callLocation,
                             out var indirectLocalAddress))
                {
                    parameterAddressReplacements[parameter.Name] = indirectLocalAddress;
                }
                else if (TryCreateArgumentReferenceAddressReplacement(
                             caller,
                             argument,
                             parameter.Type,
                             $"arg_{parameter.Name}_addr_inl{inlineSiteIndex}",
                             usedValueNames,
                             prologueInstructions,
                             callLocation,
                             out var argumentReferenceAddress))
                {
                    parameterAddressReplacements[parameter.Name] = argumentReferenceAddress;
                }
                else if (parameter.Type.Kind == StarkTypeKind.Closure
                         && CanReplaceParameterAddressLoadsWithValue(candidate, parameter.Name))
                {
                    parameterAddressReplacements[parameter.Name] = ProtectInlineReplacementValue(
                        candidate,
                        argument,
                        $"arg_{parameter.Name}_value_inl{inlineSiteIndex}",
                        usedValueNames,
                        prologueInstructions,
                        callLocation);
                }
                else if (parameter.Type.Kind == StarkTypeKind.Closure
                         && TryCreateTemporaryArgumentAddressReplacement(
                             argument,
                             parameter.Type,
                             $"arg_{parameter.Name}_slot_inl{inlineSiteIndex}",
                             $"arg_{parameter.Name}_addr_inl{inlineSiteIndex}",
                             usedValueNames,
                             prologueInstructions,
                             callLocation,
                             out var temporaryArgumentAddress))
                {
                    parameterAddressReplacements[parameter.Name] = temporaryArgumentAddress;
                }
            }

            if (argument is SsaValueReference reference
                && candidate.DefinedValueNames.Contains(reference.Name))
            {
                var aliasName = CreateFreshName(
                    $"arg_{parameter.Name}_inl{inlineSiteIndex}",
                    usedValueNames);
                prologueInstructions.Add(new SsaValueInstruction(
                    aliasName,
                    new SsaUseRValue(reference),
                    callLocation));
                localReplacements[$"arg_{parameter.Name}"] = new SsaValueReference(
                    aliasName,
                    reference.Type);
                continue;
            }

            localReplacements[$"arg_{parameter.Name}"] = argument;
        }
    }

    private static bool TryCreateArgumentReferenceAddressReplacement(
        SsaFunction caller,
        SsaValue argument,
        StarkTypeSymbol parameterType,
        string addressBaseName,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions,
        SourceLocation? callLocation,
        out SsaValue address)
    {
        address = default!;
        if (argument is not SsaValueReference reference)
        {
            return false;
        }

        var parameterName = reference.Name.StartsWith("arg_", StringComparison.Ordinal)
            ? reference.Name["arg_".Length..]
            : reference.Name;
        var callerParameter = caller.Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, parameterName, StringComparison.Ordinal));
        if (callerParameter is null)
        {
            return false;
        }

        var pointerType = CreateIndirectArgumentAddressType(parameterType);
        var addressName = CreateFreshName(addressBaseName, usedValueNames);
        prologueInstructions.Add(new SsaValueInstruction(
            addressName,
            new SsaAddressOfParameterRValue(
                callerParameter.Name,
                callerParameter.Type,
                pointerType,
                $"&{callerParameter.Name}"),
            callLocation));
        address = new SsaValueReference(addressName, pointerType);
        return true;
    }

    private static bool TryCreateIndirectLocalAddressReplacement(
        SsaFunction caller,
        SsaCallRValue call,
        int argumentIndex,
        StarkTypeSymbol parameterType,
        string addressBaseName,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions,
        SourceLocation? callLocation,
        out SsaValue address)
    {
        address = default!;

        if (call.IndirectArgumentLocalNames is null
            || argumentIndex >= call.IndirectArgumentLocalNames.Count
            || call.IndirectArgumentLocalNames[argumentIndex] is not { Length: > 0 } localName)
        {
            return false;
        }

        var pointerType = CreateIndirectArgumentAddressType(parameterType);
        if (caller.Parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, localName, StringComparison.Ordinal)) is { } callerParameter)
        {
            var addressName = CreateFreshName(addressBaseName, usedValueNames);
            prologueInstructions.Add(new SsaValueInstruction(
                addressName,
                new SsaAddressOfParameterRValue(
                    callerParameter.Name,
                    callerParameter.Type,
                    pointerType,
                    $"&{callerParameter.Name}"),
                callLocation));
            address = new SsaValueReference(addressName, pointerType);
            return true;
        }

        if (TryFindLocalType(caller, localName, out var localType))
        {
            var addressName = CreateFreshName(addressBaseName, usedValueNames);
            prologueInstructions.Add(new SsaValueInstruction(
                addressName,
                new SsaAddressOfLocalRValue(
                    localName,
                    localType,
                    pointerType,
                    $"&{localName}"),
                callLocation));
            address = new SsaValueReference(addressName, pointerType);
            return true;
        }

        return false;
    }

    private static bool CanReplaceParameterAddressLoadsWithValue(InlineCandidate candidate, string parameterName)
    {
        var addressResultNames = candidate.Instructions
            .OfType<SsaValueInstruction>()
            .Where(instruction => instruction.Value is SsaAddressOfParameterRValue addressOfParameter
                && string.Equals(addressOfParameter.ParameterName, parameterName, StringComparison.Ordinal))
            .Select(static instruction => instruction.ResultName)
            .ToHashSet(StringComparer.Ordinal);
        if (addressResultNames.Count == 0)
        {
            return false;
        }

        var sawLoad = false;
        foreach (var block in candidate.Function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                if (phi.Incomings.Any(incoming => ValueUsesAny(incoming.Value, addressResultNames)))
                {
                    return false;
                }
            }

            foreach (var instruction in block.Instructions)
            {
                if (instruction is SsaValueInstruction
                    {
                        Value: SsaAddressOfParameterRValue addressOfParameter
                    }
                    && string.Equals(addressOfParameter.ParameterName, parameterName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (instruction is SsaValueInstruction
                    {
                        Value: SsaLoadIndirectRValue
                        {
                            Address: SsaValueReference addressReference
                        }
                    }
                    && addressResultNames.Contains(addressReference.Name))
                {
                    sawLoad = true;
                    continue;
                }

                var usedNames = new HashSet<string>(StringComparer.Ordinal);
                CollectUsedValueNames(instruction, usedNames);
                if (usedNames.Overlaps(addressResultNames))
                {
                    return false;
                }
            }

            var terminatorUsedNames = new HashSet<string>(StringComparer.Ordinal);
            CollectUsedValueNames(block.Terminator, terminatorUsedNames);
            if (terminatorUsedNames.Overlaps(addressResultNames))
            {
                return false;
            }
        }

        return sawLoad;
    }

    private static bool ValueUsesAny(SsaValue value, IReadOnlySet<string> names)
    {
        return value is SsaValueReference reference && names.Contains(reference.Name);
    }

    private static bool TryCreateTemporaryArgumentAddressReplacement(
        SsaValue argument,
        StarkTypeSymbol parameterType,
        string localBaseName,
        string addressBaseName,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions,
        SourceLocation? callLocation,
        out SsaValue address)
    {
        address = default!;
        if (argument.Type.Kind == StarkTypeKind.Void)
        {
            return false;
        }

        var localType = StarkTypeSymbols.WithQualifiers(
            parameterType,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        var localName = CreateFreshName(localBaseName, usedValueNames);
        prologueInstructions.Add(new SsaAllocateLocalInstruction(
            localName,
            localType,
            StorageClass: "stack",
            Location: callLocation));
        prologueInstructions.Add(new SsaLifetimeStartInstruction(
            localName,
            localType,
            callLocation));
        prologueInstructions.Add(new SsaStoreLocalInstruction(
            localName,
            localType,
            argument,
            callLocation));

        var pointerType = CreateIndirectArgumentAddressType(parameterType);
        var addressName = CreateFreshName(addressBaseName, usedValueNames);
        prologueInstructions.Add(new SsaValueInstruction(
            addressName,
            new SsaAddressOfLocalRValue(
                localName,
                localType,
                pointerType,
                $"&{localName}"),
            callLocation));
        address = new SsaValueReference(addressName, pointerType);
        return true;
    }

    private static StarkTypeSymbol CreateIndirectArgumentAddressType(StarkTypeSymbol parameterType)
    {
        var pointeeType = StarkTypeSymbols.WithQualifiers(
            parameterType,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        var isMutable = parameterType.IsMutableView
                        || parameterType.InitializationKind != StarkInitializationKind.None;
        return StarkTypeSymbols.RawPointer(pointeeType, isMutable);
    }

    private static bool TryFindLocalType(
        SsaFunction function,
        string localName,
        out StarkTypeSymbol localType)
    {
        foreach (var instruction in function.Blocks.SelectMany(static block => block.Instructions))
        {
            switch (instruction)
            {
                case SsaAllocateLocalInstruction allocate
                    when string.Equals(allocate.LocalName, localName, StringComparison.Ordinal):
                    localType = allocate.LocalType;
                    return true;
                case SsaLifetimeStartInstruction lifetimeStart
                    when string.Equals(lifetimeStart.LocalName, localName, StringComparison.Ordinal):
                    localType = lifetimeStart.LocalType;
                    return true;
                case SsaStoreLocalInstruction storeLocal
                    when string.Equals(storeLocal.LocalName, localName, StringComparison.Ordinal):
                    localType = storeLocal.LocalType;
                    return true;
            }
        }

        localType = StarkTypeSymbols.Error;
        return false;
    }

    private static SsaValue ProtectInlineReplacementValue(
        InlineCandidate candidate,
        SsaValue value,
        string aliasBaseName,
        ISet<string> usedValueNames,
        ICollection<SsaInstruction> prologueInstructions,
        SourceLocation? callLocation)
    {
        if (value is not SsaValueReference reference
            || !candidate.DefinedValueNames.Contains(reference.Name))
        {
            return value;
        }

        var aliasName = CreateFreshName(aliasBaseName, usedValueNames);
        prologueInstructions.Add(new SsaValueInstruction(
            aliasName,
            new SsaUseRValue(reference),
            callLocation));
        return new SsaValueReference(aliasName, reference.Type);
    }

    private static bool HasUnsupportedIndirectArgumentMetadata(
        SsaCallRValue call,
        IReadOnlyList<TypedParameterSymbol> parameters)
    {
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var hasIndirectLocal = call.IndirectArgumentLocalNames is not null
                                   && index < call.IndirectArgumentLocalNames.Count
                                   && call.IndirectArgumentLocalNames[index] is not null;
            var hasIndirectAddress = call.IndirectArgumentAddresses is not null
                                     && index < call.IndirectArgumentAddresses.Count
                                     && call.IndirectArgumentAddresses[index] is not null;
            if (!hasIndirectLocal && !hasIndirectAddress)
            {
                continue;
            }

            if (index < parameters.Count && IsInlineClosureParameter(parameters[index].Type))
            {
                continue;
            }

            if (index < parameters.Count
                && IsPointerBackedParameterType(parameters[index].Type)
                && hasIndirectAddress)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static SsaInstruction? CloneInlineInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaValue> parameterAddressReplacements,
        IReadOnlySet<string> usedValueNames,
        SourceLocation? callLocation,
        int inlineSiteIndex)
    {
        if (instruction is SsaValueInstruction candidateValueInstruction)
        {
            if (TryResolveParameterAddressAlias(
                    candidateValueInstruction.Value,
                    parameterAddressReplacements,
                    out var parameterAddressAlias))
            {
                if (replacements is IDictionary<string, SsaValue> mutableReplacements)
                {
                    mutableReplacements[candidateValueInstruction.ResultName] = parameterAddressAlias;
                }

                return null;
            }

            if (TryResolveParameterLoadAlias(
                    candidateValueInstruction.Value,
                    replacements,
                    out var parameterLoadAlias))
            {
                if (replacements is IDictionary<string, SsaValue> mutableReplacements)
                {
                    mutableReplacements[candidateValueInstruction.ResultName] = parameterLoadAlias;
                }

                return null;
            }

            if (!usedValueNames.Contains(candidateValueInstruction.ResultName)
                && IsInlineDroppableRValue(candidateValueInstruction.Value))
            {
                return null;
            }
        }

        return instruction switch
        {
            SsaValueInstruction valueInstruction => new SsaValueInstruction(
                ((SsaValueReference)replacements[valueInstruction.ResultName]).Name,
                RewriteRValue(
                    valueInstruction.Value,
                    replacements,
                    parameterAddressReplacements),
                callLocation ?? valueInstruction.Location,
                valueInstruction.ScopedNoAliasGroups,
                valueInstruction.LoopAccessGroups),
            _ => RewriteInstruction(instruction, replacements)
        };
    }

    private static SsaTerminator CloneInlineTerminator(
        SsaTerminator terminator,
        int clonedBlockId,
        int continuationBlockId,
        IReadOnlyDictionary<int, int> blockIdMap,
        IReadOnlyDictionary<string, SsaValue> replacements,
        SsaValueInstruction callInstruction,
        ICollection<SsaPhiIncoming> returnIncomings)
    {
        switch (terminator.Kind)
        {
            case SsaTerminatorKind.Return:
                if (terminator.Value is not null)
                {
                    returnIncomings.Add(new SsaPhiIncoming(
                        clonedBlockId,
                        RewriteValue(terminator.Value, replacements)));
                }

                return new SsaTerminator(
                    SsaTerminatorKind.Goto,
                    [continuationBlockId],
                    Location: callInstruction.Location ?? terminator.Location);
            case SsaTerminatorKind.Goto:
            case SsaTerminatorKind.Branch:
                return terminator with
                {
                    Targets = terminator.Targets.Select(target => blockIdMap[target]).ToArray(),
                    Condition = terminator.Condition is null ? null : RewriteValue(terminator.Condition, replacements),
                    Value = terminator.Value is null ? null : RewriteValue(terminator.Value, replacements),
                    Location = callInstruction.Location ?? terminator.Location
                };
            case SsaTerminatorKind.Switch:
                return terminator with
                {
                    Targets = terminator.Targets.Select(target => blockIdMap[target]).ToArray(),
                    Condition = terminator.Condition is null ? null : RewriteValue(terminator.Condition, replacements),
                    Value = terminator.Value is null ? null : RewriteValue(terminator.Value, replacements),
                    SwitchCases = terminator.SwitchCases?
                        .Select(switchCase => switchCase with
                        {
                            TargetBlockId = blockIdMap[switchCase.TargetBlockId],
                            MatchValue = RewriteValue(switchCase.MatchValue, replacements)
                        })
                        .ToArray(),
                    DefaultTarget = terminator.DefaultTarget is { } defaultTarget
                        ? blockIdMap[defaultTarget]
                        : null,
                    Location = callInstruction.Location ?? terminator.Location
                };
            default:
                return terminator with
                {
                    Condition = terminator.Condition is null ? null : RewriteValue(terminator.Condition, replacements),
                    Value = terminator.Value is null ? null : RewriteValue(terminator.Value, replacements),
                    Location = callInstruction.Location ?? terminator.Location
                };
        }
    }

    private static bool HasConstantSpecializationArgument(SsaCallRValue call)
    {
        return call.Arguments.Any(static argument => argument is
            SsaIntegerConstant
            or SsaFloatConstant
            or SsaBoolConstant
            or SsaNullConstant
            or SsaGlobalAddressValue
            or SsaFunctionAddressValue
            or SsaClosureValue);
    }

    private static HashSet<string> CollectDefinedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                names.Add(phi.ResultName);
            }

            foreach (var valueInstruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                names.Add(valueInstruction.ResultName);
            }
        }

        return names;
    }

    private static HashSet<string> CollectUsedValueNames(SsaFunction function)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                foreach (var incoming in phi.Incomings)
                {
                    CollectUsedValueNames(incoming.Value, names);
                }
            }

            foreach (var instruction in block.Instructions)
            {
                CollectUsedValueNames(instruction, names);
            }

            CollectUsedValueNames(block.Terminator, names);
        }

        return names;
    }

    private static void CollectUsedValueNames(SsaInstruction instruction, ISet<string> names)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                CollectUsedValueNames(valueInstruction.Value, names);
                break;
            case SsaStoreLocalInstruction storeLocal:
                CollectUsedValueNames(storeLocal.Value, names);
                break;
            case SsaCopyMemoryInstruction copyMemory:
                CollectUsedValueNames(copyMemory.DestinationAddress, names);
                CollectUsedValueNames(copyMemory.SourceAddress, names);
                break;
            case SsaStoreIndirectInstruction storeIndirect:
                CollectUsedValueNames(storeIndirect.Address, names);
                CollectUsedValueNames(storeIndirect.Value, names);
                break;
            case SsaStoreGlobalInstruction storeGlobal:
                CollectUsedValueNames(storeGlobal.Value, names);
                break;
        }
    }

    private static void CollectUsedValueNames(SsaRValue value, ISet<string> names)
    {
        switch (value)
        {
            case SsaUseRValue use:
                CollectUsedValueNames(use.Value, names);
                break;
            case SsaUnaryRValue unary:
                CollectUsedValueNames(unary.Operand, names);
                break;
            case SsaBinaryRValue binary:
                CollectUsedValueNames(binary.Left, names);
                CollectUsedValueNames(binary.Right, names);
                break;
            case SsaSelectRValue select:
                CollectUsedValueNames(select.Condition, names);
                CollectUsedValueNames(select.WhenTrue, names);
                CollectUsedValueNames(select.WhenFalse, names);
                break;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    CollectUsedValueNames(argument, names);
                }

                foreach (var address in call.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    CollectUsedValueNames(address, names);
                }

                break;
            case SsaIndirectCallRValue indirectCall:
                CollectUsedValueNames(indirectCall.Target, names);
                foreach (var argument in indirectCall.Arguments)
                {
                    CollectUsedValueNames(argument, names);
                }

                foreach (var address in indirectCall.IndirectArgumentAddresses?.OfType<SsaValue>() ?? [])
                {
                    CollectUsedValueNames(address, names);
                }

                break;
            case SsaConvertRValue convert:
                CollectUsedValueNames(convert.Operand, names);
                break;
            case SsaExtractFieldRValue extractField:
                CollectUsedValueNames(extractField.Target, names);
                break;
            case SsaInsertFieldRValue insertField:
                CollectUsedValueNames(insertField.Target, names);
                CollectUsedValueNames(insertField.Value, names);
                break;
            case SsaExtractIndexRValue extractIndex:
                CollectUsedValueNames(extractIndex.Target, names);
                break;
            case SsaInsertIndexRValue insertIndex:
                CollectUsedValueNames(insertIndex.Target, names);
                CollectUsedValueNames(insertIndex.Value, names);
                break;
            case SsaMakeSliceFromPointerRValue makeSlice:
                CollectUsedValueNames(makeSlice.Pointer, names);
                CollectUsedValueNames(makeSlice.Length, names);
                break;
            case SsaDynamicStorageAllocationRValue allocation:
                CollectUsedValueNames(allocation.Capacity, names);
                break;
            case SsaDynamicStorageFreeRValue free:
                CollectUsedValueNames(free.Storage, names);
                break;
            case SsaHeapStorageFreeRValue free:
                CollectUsedValueNames(free.Pointer, names);
                break;
            case SsaDynamicStorageReserveRValue reserve:
                CollectUsedValueNames(reserve.StorageAddress, names);
                CollectUsedValueNames(reserve.AdditionalCapacity, names);
                break;
            case SsaDynamicStorageTryReserveRValue reserve:
                CollectUsedValueNames(reserve.StorageAddress, names);
                CollectUsedValueNames(reserve.AdditionalCapacity, names);
                break;
            case SsaDynamicStorageTryReserveCapacityRValue reserve:
                CollectUsedValueNames(reserve.StorageAddress, names);
                CollectUsedValueNames(reserve.TargetCapacity, names);
                break;
            case SsaDynamicStorageMoveLastRValue moveLast:
                CollectUsedValueNames(moveLast.StorageAddress, names);
                break;
            case SsaDynamicStorageMoveAtRValue moveAt:
                CollectUsedValueNames(moveAt.StorageAddress, names);
                CollectUsedValueNames(moveAt.Index, names);
                break;
            case SsaLoadSliceElementRValue loadSlice:
                CollectUsedValueNames(loadSlice.Slice, names);
                CollectUsedValueNames(loadSlice.Index, names);
                break;
            case SsaTextSliceRValue textSlice:
                CollectUsedValueNames(textSlice.TextValue, names);
                CollectUsedValueNames(textSlice.Start, names);
                CollectUsedValueNames(textSlice.Length, names);
                break;
            case SsaFieldAddressRValue fieldAddress:
                CollectUsedValueNames(fieldAddress.Address, names);
                break;
            case SsaElementAddressRValue elementAddress:
                CollectUsedValueNames(elementAddress.Address, names);
                if (elementAddress.Index is not null)
                {
                    CollectUsedValueNames(elementAddress.Index, names);
                }

                break;
            case SsaSliceElementAddressRValue sliceElementAddress:
                CollectUsedValueNames(sliceElementAddress.Slice, names);
                CollectUsedValueNames(sliceElementAddress.Index, names);
                break;
            case SsaLoadIndirectRValue loadIndirect:
                CollectUsedValueNames(loadIndirect.Address, names);
                break;
        }
    }

    private static void CollectUsedValueNames(SsaTerminator terminator, ISet<string> names)
    {
        if (terminator.Condition is not null)
        {
            CollectUsedValueNames(terminator.Condition, names);
        }

        if (terminator.Value is not null)
        {
            CollectUsedValueNames(terminator.Value, names);
        }

        foreach (var switchCase in terminator.SwitchCases ?? [])
        {
            CollectUsedValueNames(switchCase.MatchValue, names);
        }
    }

    private static void CollectUsedValueNames(SsaValue value, ISet<string> names)
    {
        if (value is SsaValueReference reference)
        {
            names.Add(reference.Name);
        }
    }

    private static string CreateFreshName(string baseName, ISet<string> usedValueNames)
    {
        if (usedValueNames.Add(baseName))
        {
            return baseName;
        }

        var suffix = 1;
        while (true)
        {
            var candidate = $"{baseName}_{suffix}";
            if (usedValueNames.Add(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private static SsaBasicBlock RewriteBlock(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<int, int>? predecessorRedirects = null)
    {
        return block with
        {
            Phis = block.Phis
                .Select(phi => phi with
                {
                    Incomings = phi.Incomings
                        .Select(incoming => incoming with
                        {
                            PredecessorBlockId = predecessorRedirects is not null
                                                 && predecessorRedirects.TryGetValue(incoming.PredecessorBlockId, out var redirectedPredecessor)
                                ? redirectedPredecessor
                                : incoming.PredecessorBlockId,
                            Value = RewriteValue(incoming.Value, replacements)
                        })
                        .ToArray()
                })
                .ToArray(),
            Instructions = block.Instructions
                .Select(instruction => RewriteInstruction(instruction, replacements))
                .ToArray(),
            Terminator = RewriteTerminator(block.Terminator, replacements)
        };
    }

    private static SsaInstruction RewriteInstruction(
        SsaInstruction instruction,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => valueInstruction with
            {
                Value = RewriteRValue(valueInstruction.Value, replacements)
            },
            SsaAllocateLocalInstruction allocateLocal => allocateLocal,
            SsaLifetimeStartInstruction lifetimeStart => lifetimeStart,
            SsaLifetimeEndInstruction lifetimeEnd => lifetimeEnd,
            SsaDeallocateLocalInstruction deallocateLocal => deallocateLocal,
            SsaStoreLocalInstruction storeLocal => storeLocal with
            {
                Value = RewriteValue(storeLocal.Value, replacements)
            },
            SsaCopyMemoryInstruction copyMemory => copyMemory with
            {
                DestinationAddress = RewriteValue(copyMemory.DestinationAddress, replacements),
                SourceAddress = RewriteValue(copyMemory.SourceAddress, replacements)
            },
            SsaStoreIndirectInstruction storeIndirect => storeIndirect with
            {
                Address = RewriteValue(storeIndirect.Address, replacements),
                Value = RewriteValue(storeIndirect.Value, replacements)
            },
            SsaStoreGlobalInstruction storeGlobal => storeGlobal with
            {
                Value = RewriteValue(storeGlobal.Value, replacements)
            },
            _ => instruction
        };
    }

    private static SsaRValue RewriteRValue(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaValue>? parameterAddressReplacements = null)
    {
        return value switch
        {
            SsaUseRValue use => use with
            {
                Value = RewriteValue(use.Value, replacements)
            },
            SsaUnaryRValue unary => unary with
            {
                Operand = RewriteValue(unary.Operand, replacements)
            },
            SsaBinaryRValue binary => binary with
            {
                Left = RewriteValue(binary.Left, replacements),
                Right = RewriteValue(binary.Right, replacements)
            },
            SsaSelectRValue select => select with
            {
                Condition = RewriteValue(select.Condition, replacements),
                WhenTrue = RewriteValue(select.WhenTrue, replacements),
                WhenFalse = RewriteValue(select.WhenFalse, replacements)
            },
            SsaCallRValue call => call with
            {
                Arguments = call.Arguments
                    .Select(argument => RewriteValue(argument, replacements))
                    .ToArray(),
                IndirectArgumentLocalNames = RewriteIndirectArgumentLocalNames(
                    call.IndirectArgumentLocalNames,
                    RewriteIndirectArgumentAddresses(
                        call.IndirectArgumentLocalNames,
                        call.IndirectArgumentAddresses,
                        replacements,
                        parameterAddressReplacements)),
                IndirectArgumentAddresses = RewriteIndirectArgumentAddresses(
                    call.IndirectArgumentLocalNames,
                    call.IndirectArgumentAddresses,
                    replacements,
                    parameterAddressReplacements)
            },
            SsaIndirectCallRValue indirectCall => RewriteIndirectCallRValue(
                indirectCall,
                replacements,
                parameterAddressReplacements),
            SsaConvertRValue convert => convert with
            {
                Operand = RewriteValue(convert.Operand, replacements)
            },
            SsaExtractFieldRValue extractField => extractField with
            {
                Target = RewriteValue(extractField.Target, replacements)
            },
            SsaInsertFieldRValue insertField => insertField with
            {
                Target = RewriteValue(insertField.Target, replacements),
                Value = RewriteValue(insertField.Value, replacements)
            },
            SsaExtractIndexRValue extractIndex => extractIndex with
            {
                Target = RewriteValue(extractIndex.Target, replacements)
            },
            SsaInsertIndexRValue insertIndex => insertIndex with
            {
                Target = RewriteValue(insertIndex.Target, replacements),
                Value = RewriteValue(insertIndex.Value, replacements)
            },
            SsaMakeSliceFromPointerRValue makeSlice => makeSlice with
            {
                Pointer = RewriteValue(makeSlice.Pointer, replacements),
                Length = RewriteValue(makeSlice.Length, replacements)
            },
            SsaDynamicStorageAllocationRValue allocation => allocation with
            {
                Capacity = RewriteValue(allocation.Capacity, replacements)
            },
            SsaDynamicStorageFreeRValue free => free with
            {
                Storage = RewriteValue(free.Storage, replacements)
            },
            SsaHeapStorageFreeRValue free => free with
            {
                Pointer = RewriteValue(free.Pointer, replacements)
            },
            SsaDynamicStorageReserveRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                AdditionalCapacity = RewriteValue(reserve.AdditionalCapacity, replacements)
            },
            SsaDynamicStorageTryReserveRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                AdditionalCapacity = RewriteValue(reserve.AdditionalCapacity, replacements)
            },
            SsaDynamicStorageTryReserveCapacityRValue reserve => reserve with
            {
                StorageAddress = RewriteValue(reserve.StorageAddress, replacements),
                TargetCapacity = RewriteValue(reserve.TargetCapacity, replacements)
            },
            SsaDynamicStorageMoveLastRValue moveLast => moveLast with
            {
                StorageAddress = RewriteValue(moveLast.StorageAddress, replacements)
            },
            SsaDynamicStorageMoveAtRValue moveAt => moveAt with
            {
                StorageAddress = RewriteValue(moveAt.StorageAddress, replacements),
                Index = RewriteValue(moveAt.Index, replacements)
            },
            SsaLoadSliceElementRValue loadSlice => loadSlice with
            {
                Slice = RewriteValue(loadSlice.Slice, replacements),
                Index = RewriteValue(loadSlice.Index, replacements)
            },
            SsaTextSliceRValue textSlice => textSlice with
            {
                TextValue = RewriteValue(textSlice.TextValue, replacements),
                Start = RewriteValue(textSlice.Start, replacements),
                Length = RewriteValue(textSlice.Length, replacements)
            },
            SsaFieldAddressRValue fieldAddress => fieldAddress with
            {
                Address = RewriteValue(fieldAddress.Address, replacements)
            },
            SsaElementAddressRValue elementAddress => elementAddress with
            {
                Address = RewriteValue(elementAddress.Address, replacements),
                Index = elementAddress.Index is null ? null : RewriteValue(elementAddress.Index, replacements)
            },
            SsaSliceElementAddressRValue sliceElementAddress => sliceElementAddress with
            {
                Slice = RewriteValue(sliceElementAddress.Slice, replacements),
                Index = RewriteValue(sliceElementAddress.Index, replacements)
            },
            SsaLoadIndirectRValue loadIndirect => RewriteLoadIndirectRValue(loadIndirect, replacements),
            _ => value
        };
    }

    private static SsaRValue RewriteLoadIndirectRValue(
        SsaLoadIndirectRValue loadIndirect,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        var address = RewriteValue(loadIndirect.Address, replacements);
        return address.Type.Kind == StarkTypeKind.Closure
            ? new SsaUseRValue(address)
            : loadIndirect with
            {
                Address = address
            };
    }

    private static SsaRValue RewriteIndirectCallRValue(
        SsaIndirectCallRValue indirectCall,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaValue>? parameterAddressReplacements = null)
    {
        var target = RewriteValue(indirectCall.Target, replacements);
        var arguments = indirectCall.Arguments
            .Select(argument => RewriteValue(argument, replacements))
            .ToArray();
        var indirectArgumentAddresses = RewriteIndirectArgumentAddresses(
            indirectCall.IndirectArgumentLocalNames,
            indirectCall.IndirectArgumentAddresses,
            replacements,
            parameterAddressReplacements);

        return target is SsaFunctionAddressValue functionAddress
            ? new SsaCallRValue(
                functionAddress.FunctionName,
                arguments,
                indirectCall.Type,
                indirectCall.Text,
                RewriteIndirectArgumentLocalNames(
                    indirectCall.IndirectArgumentLocalNames,
                    indirectArgumentAddresses),
                SourceReturnType: indirectCall.SourceReturnType,
                indirectArgumentAddresses)
            : indirectCall with
            {
                Target = target,
                Arguments = arguments,
                IndirectArgumentLocalNames = RewriteIndirectArgumentLocalNames(
                    indirectCall.IndirectArgumentLocalNames,
                    indirectArgumentAddresses),
                IndirectArgumentAddresses = indirectArgumentAddresses
            };
    }

    private static IReadOnlyList<SsaValue?>? RewriteIndirectArgumentAddresses(
        IReadOnlyList<string?>? localNames,
        IReadOnlyList<SsaValue?>? addresses,
        IReadOnlyDictionary<string, SsaValue> replacements,
        IReadOnlyDictionary<string, SsaValue>? parameterAddressReplacements)
    {
        if (addresses is null
            && (localNames is null || parameterAddressReplacements is null))
        {
            return null;
        }

        var length = Math.Max(addresses?.Count ?? 0, localNames?.Count ?? 0);
        if (length == 0)
        {
            return addresses;
        }

        var rewritten = new SsaValue?[length];
        var changed = addresses is null || addresses.Count != length;
        if (addresses is not null)
        {
            for (var index = 0; index < addresses.Count; index++)
            {
                if (addresses[index] is { } address)
                {
                    var rewrittenAddress = RewriteValue(address, replacements);
                    rewritten[index] = rewrittenAddress;
                    changed |= !ReferenceEquals(address, rewrittenAddress);
                }
            }
        }

        if (localNames is not null && parameterAddressReplacements is not null)
        {
            for (var index = 0; index < localNames.Count; index++)
            {
                if (localNames[index] is { } localName
                    && parameterAddressReplacements.TryGetValue(localName, out var replacementAddress))
                {
                    var rewrittenAddress = RewriteValue(replacementAddress, replacements);
                    rewritten[index] = rewrittenAddress;
                    changed = true;
                }
            }
        }

        return changed
            ? rewritten
            : addresses;
    }

    private static IReadOnlyList<string?>? RewriteIndirectArgumentLocalNames(
        IReadOnlyList<string?>? localNames,
        IReadOnlyList<SsaValue?>? addresses)
    {
        if (localNames is null)
        {
            return null;
        }

        if (addresses is null)
        {
            return localNames;
        }

        var rewritten = localNames.ToArray();
        for (var index = 0; index < Math.Min(rewritten.Length, addresses.Count); index++)
        {
            if (addresses[index] is not null)
            {
                rewritten[index] = null;
            }
        }

        return rewritten;
    }

    private static SsaTerminator RewriteTerminator(
        SsaTerminator terminator,
        IReadOnlyDictionary<string, SsaValue> replacements)
    {
        return terminator with
        {
            Condition = terminator.Condition is null
                ? null
                : RewriteValue(terminator.Condition, replacements),
            Value = terminator.Value is null
                ? null
                : RewriteValue(terminator.Value, replacements),
            SwitchCases = terminator.SwitchCases?
                .Select(switchCase => switchCase with
                {
                    MatchValue = RewriteValue(switchCase.MatchValue, replacements)
                })
                .ToArray()
        };
    }

    private static SsaValue RewriteValue(
        SsaValue value,
        IReadOnlyDictionary<string, SsaValue> replacements)
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

    private static bool IsInlineSafeRValue(SsaRValue value, string ownerFunctionName)
    {
        return value switch
        {
            SsaUseRValue use => IsInlineSafeValue(use.Value),
            SsaUnaryRValue unary => IsInlineSafeValue(unary.Operand),
            SsaBinaryRValue binary => IsInlineSafeValue(binary.Left)
                                      && IsInlineSafeValue(binary.Right),
            SsaSelectRValue select => IsInlineSafeValue(select.Condition)
                                      && IsInlineSafeValue(select.WhenTrue)
                                      && IsInlineSafeValue(select.WhenFalse),
            SsaCallRValue call => !string.Equals(call.FunctionName, ownerFunctionName, StringComparison.Ordinal)
                                  && IsInlineSafeType(call.Type)
                                  && call.Arguments.All(IsInlineSafeValue)
                                  && HasInlineSafeIndirectArgumentMetadata(
                                      call.IndirectArgumentLocalNames,
                                      call.IndirectArgumentAddresses),
            SsaIndirectCallRValue indirectCall => IsInlineSafeType(indirectCall.Type)
                                                  && IsInlineSafeValue(indirectCall.Target)
                                                  && indirectCall.Arguments.All(IsInlineSafeValue)
                                                  && HasInlineSafeIndirectArgumentMetadata(
                                                      indirectCall.IndirectArgumentLocalNames,
                                                      indirectCall.IndirectArgumentAddresses),
            SsaConvertRValue convert => IsInlineSafeValue(convert.Operand),
            SsaExtractFieldRValue extractField => IsInlineSafeValue(extractField.Target),
            SsaInsertFieldRValue insertField => IsInlineSafeValue(insertField.Target)
                                                && IsInlineSafeValue(insertField.Value),
            SsaExtractIndexRValue extractIndex => IsInlineSafeValue(extractIndex.Target),
            SsaInsertIndexRValue insertIndex => IsInlineSafeValue(insertIndex.Target)
                                                && IsInlineSafeValue(insertIndex.Value),
            SsaFieldAddressRValue fieldAddress => IsInlineSafeValue(fieldAddress.Address),
            SsaElementAddressRValue elementAddress => IsInlineSafeValue(elementAddress.Address)
                                                      && (elementAddress.Index is null || IsInlineSafeValue(elementAddress.Index)),
            SsaLoadIndirectRValue loadIndirect => IsInlineSafeValue(loadIndirect.Address),
            SsaAddressOfParameterRValue => true,
            SsaLoadLocalRValue => true,
            _ => false
        };
    }

    private static bool IsInlineSafeInstruction(
        SsaInstruction instruction,
        string ownerFunctionName,
        bool allowInlineClosureSpecialization,
        bool resultIsUsed)
    {
        return instruction switch
        {
            SsaValueInstruction valueInstruction => IsInlineSafeType(valueInstruction.Value.Type)
                                                    && IsInlineSafeRValue(valueInstruction.Value, ownerFunctionName)
                                                    && (allowInlineClosureSpecialization
                                                        || resultIsUsed
                                                        || !IsInlineDroppableRValue(valueInstruction.Value)),
            SsaStoreIndirectInstruction storeIndirect => IsInlineSafeValue(storeIndirect.Address)
                                                         && IsInlineSafeValue(storeIndirect.Value),
            SsaCopyMemoryInstruction copyMemory => IsInlineSafeValue(copyMemory.DestinationAddress)
                                                   && IsInlineSafeValue(copyMemory.SourceAddress),
            SsaStoreGlobalInstruction storeGlobal => IsInlineSafeValue(storeGlobal.Value),
            _ => false
        };
    }

    private static bool TryResolveParameterAddressAlias(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> parameterAddressReplacements,
        out SsaValue replacement)
    {
        if (value is SsaAddressOfParameterRValue addressOfParameter
            && parameterAddressReplacements.TryGetValue(addressOfParameter.ParameterName, out replacement!))
        {
            return true;
        }

        replacement = default!;
        return false;
    }

    private static bool TryResolveParameterLoadAlias(
        SsaRValue value,
        IReadOnlyDictionary<string, SsaValue> replacements,
        out SsaValue replacement)
    {
        if (value is SsaLoadIndirectRValue
            {
                Address: SsaValueReference addressReference
            }
            && replacements.TryGetValue(addressReference.Name, out replacement!)
            && replacement.Type.Kind == StarkTypeKind.Closure)
        {
            return true;
        }

        replacement = default!;
        return false;
    }

    private static bool IsInlineDroppableRValue(SsaRValue value)
    {
        return value is not SsaCallRValue
            and not SsaIndirectCallRValue
            and not SsaDynamicStorageAllocationRValue
            and not SsaDynamicStorageFreeRValue
            and not SsaHeapStorageFreeRValue
            and not SsaDynamicStorageReserveRValue
            and not SsaDynamicStorageTryReserveRValue
            and not SsaDynamicStorageTryReserveCapacityRValue
            and not SsaDynamicStorageMoveLastRValue
            and not SsaDynamicStorageMoveAtRValue;
    }

    private static bool HasInlineSafeIndirectArgumentMetadata(
        IReadOnlyList<string?>? localNames,
        IReadOnlyList<SsaValue?>? addresses)
    {
        if (localNames?.Any(static name => name is not null) == true
            && addresses is null)
        {
            return false;
        }

        return addresses?.All(static address => address is null || IsInlineSafeValue(address)) != false;
    }

    private static bool IsInlineSafeTerminator(SsaTerminator terminator)
    {
        return terminator.Kind switch
        {
            SsaTerminatorKind.Goto => true,
            SsaTerminatorKind.Branch => terminator.Condition is not null
                                        && IsInlineSafeValue(terminator.Condition),
            SsaTerminatorKind.Switch => terminator.Condition is not null
                                        && IsInlineSafeValue(terminator.Condition)
                                        && (terminator.SwitchCases?.All(static switchCase => IsInlineSafeValue(switchCase.MatchValue)) ?? true),
            SsaTerminatorKind.Return => terminator.Value is null
                                        || IsInlineSafeValue(terminator.Value),
            SsaTerminatorKind.Unreachable => true,
            _ => false
        };
    }

    private static bool IsInlineSafeValue(SsaValue value)
    {
        return value is SsaValueReference
            or SsaIntegerConstant
            or SsaFloatConstant
            or SsaStringConstant
            or SsaBoolConstant
            or SsaNullConstant
            or SsaGlobalAddressValue
            or SsaFunctionAddressValue
            or SsaClosureValue
            or SsaUndefValue
            or SsaZeroInitializerValue;
    }

    private static bool IsInlineSafeType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Bool
            or StarkTypeKind.Void
            or StarkTypeKind.Ascii
            or StarkTypeKind.Unicode
            or StarkTypeKind.Integer
            or StarkTypeKind.Float
            or StarkTypeKind.RawPointer
            or StarkTypeKind.Slice
            or StarkTypeKind.FunctionPointer
            or StarkTypeKind.Closure
            or StarkTypeKind.Named
            or StarkTypeKind.Null;
    }

    private static bool IsPointerBackedParameterType(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None
            || type.InitializationKind != StarkInitializationKind.None
            || type.Kind == StarkTypeKind.RawPointer;
    }

    private static bool IsInlineClosureParameter(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Closure
            && type.ClosureStorageKind == StarkClosureStorageKind.Inline;
    }

    private sealed record InlineCandidate(
        SsaFunction Function,
        IReadOnlyList<SsaBasicBlock> Blocks,
        IReadOnlyList<SsaInstruction> Instructions,
        SsaValue? ReturnValue,
        IReadOnlySet<string> DefinedValueNames,
        IReadOnlySet<string> UsedValueNames,
        IReadOnlyList<string> DirectCalls,
        bool CanInlineByDefault,
        bool CanInlineWithConstantArguments);
}
