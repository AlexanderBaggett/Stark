namespace Stark.Compiler;

internal static class SsaDynamicStorageCallFactPolicy
{
    public static IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> BuildDirectCallParameterEffects(
        SemanticValidationModel semanticValidation,
        LoadedModuleSet loadedModules,
        SpecializationCodegenStrategyModel specializationCodegenStrategy)
    {
        var parameterEffects = new Dictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>>(StringComparer.Ordinal);

        foreach (var (functionName, summary) in semanticValidation.Functions)
        {
            AddParameterEffects(functionName, summary.Parameters);
            AddParameterEffects(summary.Name, summary.Parameters);
        }

        foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
        {
            if (module.PackageImageFacts is not { FunctionSemantics.Count: > 0 } packageImageFacts)
            {
                continue;
            }

            foreach (var (functionName, summary) in packageImageFacts.FunctionSemantics)
            {
                AddParameterEffects(functionName, summary.Parameters);
                AddParameterEffects(summary.Name, summary.Parameters);
            }
        }

        foreach (var strategy in specializationCodegenStrategy.Functions)
        {
            if (parameterEffects.TryGetValue(strategy.TemplateName, out var templateParameterEffects))
            {
                parameterEffects[strategy.SymbolName] = templateParameterEffects;
            }
        }

        return parameterEffects;

        void AddParameterEffects(string functionName, IReadOnlyList<ParameterMemoryEffectSummary>? parameters)
        {
            if (parameters is { Count: > 0 })
            {
                parameterEffects[functionName] = parameters;
            }
        }
    }

    public static bool ShouldPreserveDirectCallArgumentDynamicFacts(
        ISsaDirectCallOperation call,
        int argumentIndex,
        IReadOnlyDictionary<string, IReadOnlyList<ParameterMemoryEffectSummary>> directCallParameterEffects)
    {
        if (IsFullInitSliceHelperDestinationArgument(call, argumentIndex))
        {
            return true;
        }

        return directCallParameterEffects.TryGetValue(call.FunctionName, out var parameterEffects)
               && argumentIndex >= 0
               && argumentIndex < parameterEffects.Count
               && !parameterEffects[argumentIndex].Writes
               && parameterEffects[argumentIndex].CaptureKind == ParameterCaptureKind.None;
    }

    private static bool IsFullInitSliceHelperDestinationArgument(ISsaDirectCallOperation call, int argumentIndex)
    {
        return argumentIndex >= 0
               && argumentIndex < call.Arguments.Count
               && call.Arguments[argumentIndex].Type is
               {
                   Kind: StarkTypeKind.Slice,
                   InitializationKind: StarkInitializationKind.Init
               }
               && TryGetFullInitSliceHelperDestinationIndex(call.FunctionName, out var destinationIndex)
               && argumentIndex == destinationIndex;
    }

    private static bool TryGetFullInitSliceHelperDestinationIndex(string functionName, out int destinationIndex)
    {
        if (MatchesMemoryHelper(
                functionName,
                "InitializeBytesDisjoint",
                "InitializeBytes",
                "InitializeCodePointsDisjoint",
                "InitializeCodePoints"))
        {
            destinationIndex = 1;
            return true;
        }

        if (MatchesMemoryHelper(
                functionName,
                "InitializeBytesFromPointerDisjoint",
                "InitializeCodePointsFromPointerDisjoint"))
        {
            destinationIndex = 2;
            return true;
        }

        if (MatchesMemoryHelper(functionName, "FillBytes", "FillCodePoints"))
        {
            destinationIndex = 0;
            return true;
        }

        destinationIndex = -1;
        return false;
    }

    private static bool MatchesMemoryHelper(string functionName, params string[] helperNames)
    {
        foreach (var helperName in helperNames)
        {
            if (string.Equals(functionName, helperName, StringComparison.Ordinal)
                || string.Equals(functionName, $"System.Memory.{helperName}", StringComparison.Ordinal)
                || string.Equals(functionName, $"System_Memory_{helperName}", StringComparison.Ordinal)
                || functionName.EndsWith($".{helperName}", StringComparison.Ordinal)
                || functionName.Contains("System_Memory", StringComparison.Ordinal)
                && functionName.EndsWith($"_{helperName}", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
