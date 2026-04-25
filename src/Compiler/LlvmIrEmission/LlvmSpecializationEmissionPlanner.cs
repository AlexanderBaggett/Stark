using System.Text;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed record ImportedLawClonePlan(
    string FunctionName,
    TypedFunctionSignature Signature,
    AbiFunctionSignature AbiSignature,
    FunctionEffectProfile Effects,
    SsaFunction SsaFunction);

internal delegate IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? ResolveParameterEffectsDelegate(string functionName, bool hasBody);
internal delegate FunctionMemoryEffectSummary? ResolveFunctionMemoryEffectsDelegate(string functionName, bool hasBody);
internal delegate string BuildLlvmDeclarationSignatureDelegate(
    bool internalize,
    TypedFunctionSignature function,
    AbiFunctionSignature abiFunction,
    FunctionEffectProfile effects,
    FunctionMemoryEffectSummary? memoryEffects,
    IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects);
internal delegate void EmitLlvmFunctionDefinitionDelegate(
    StringBuilder builder,
    bool internalize,
    bool availableExternally,
    TypedFunctionSignature function,
    AbiFunctionSignature abiFunction,
    FunctionEffectProfile effects,
    FunctionMemoryEffectSummary? memoryEffects,
    SsaFunction ssaFunction,
    IReadOnlyDictionary<string, ParameterMemoryEffectSummary>? parameterEffects,
    Func<string, string, AbiFunctionSignature?> resolveCallAbi,
    MonomorphizationLinkageKind? specializationLinkage);
internal delegate void LogLlvmFallbackDelegate(
    string classification,
    string functionName,
    string reason,
    FunctionBodyLoweringKind bodyLoweringKind,
    bool supportsDirectCodeGeneration);

internal static class LlvmSpecializationEmissionPlanner
{
    public static IReadOnlyDictionary<string, SourceLocation> BuildFunctionLocationMap(
        LoadedModuleSet loadedModules,
        string? rootInputFilePath)
    {
        var locations = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values)
        {
            var filePath = module.Reference.FilePath ?? rootInputFilePath;
            foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
            {
                var qualifiedName = module.Reference.IsRoot
                    ? declaration.Name
                    : $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                locations[qualifiedName] = new SourceLocation(
                    filePath,
                    declaration.NameToken.Line,
                    declaration.NameToken.Column + 1);
            }
        }

        return locations;
    }

    public static IReadOnlyDictionary<string, string> BuildSpecializationTemplateNames(
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        if (specializationCodegenStrategy is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return specializationCodegenStrategy.Functions.ToDictionary(
            static function => function.SymbolName,
            static function => function.TemplateName,
            StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, ConcreteTypeLayout> BuildPublishedConcreteLayouts(LoadedModuleSet loadedModules)
    {
        var layouts = new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);

        foreach (var module in loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is not { } packageImageFacts)
            {
                continue;
            }

            foreach (var (qualifiedName, layout) in packageImageFacts.ConcreteLayouts)
            {
                layouts[qualifiedName] = layout;
            }
        }

        return layouts;
    }

    public static IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> BuildPublishedFunctionSemantics(
        LoadedModuleSet loadedModules,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        var semantics = BuildPublishedFunctionSemantics(loadedModules).ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        if (specializationCodegenStrategy is null)
        {
            return semantics;
        }

        foreach (var strategy in specializationCodegenStrategy.Functions)
        {
            if (semantics.ContainsKey(strategy.SymbolName)
                || !semantics.TryGetValue(strategy.TemplateName, out var templateSemantics))
            {
                continue;
            }

            semantics[strategy.SymbolName] = templateSemantics with { Name = strategy.SymbolName };
        }

        return semantics;
    }

    public static void EmitMaterializedSpecializationDefinitions(
        StringBuilder builder,
        ISet<string> handledFunctionNames,
        Func<string, string, AbiFunctionSignature?> resolveCallAbi,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy,
        LoadedModuleSet loadedModules,
        SsaIrModule ssa,
        IReadOnlyDictionary<string, TypedFunctionSignature> allFunctionSignatures,
        IReadOnlyDictionary<string, AbiFunctionSignature> allAbiFunctions,
        IReadOnlyDictionary<string, FunctionEffectProfile> allFunctionEffects,
        ResolveParameterEffectsDelegate getParameterEffects,
        ResolveFunctionMemoryEffectsDelegate getFunctionMemoryEffects,
        BuildLlvmDeclarationSignatureDelegate buildDeclarationSignature,
        EmitLlvmFunctionDefinitionDelegate emitFunctionDefinition,
        LogLlvmFallbackDelegate logLlvmFallback,
        Func<string, string> escapeIdentifier)
    {
        if (specializationCodegenStrategy is null)
        {
            return;
        }

        var ssaByName = ssa.Functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);

        foreach (var strategy in specializationCodegenStrategy.Functions.OrderBy(static function => function.SymbolName, StringComparer.Ordinal))
        {
            if (handledFunctionNames.Contains(strategy.SymbolName)
                || !allFunctionSignatures.TryGetValue(strategy.SymbolName, out var signature)
                || !allAbiFunctions.TryGetValue(strategy.SymbolName, out var abiSignature)
                || !allFunctionEffects.TryGetValue(strategy.SymbolName, out var effects))
            {
                continue;
            }

            handledFunctionNames.Add(strategy.SymbolName);
            ssaByName.TryGetValue(strategy.SymbolName, out var ssaFunction);
            var hasBody = ssaFunction is { HasBody: true };
            if (!hasBody && strategy.StrategyKind == FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly)
            {
                handledFunctionNames.Remove(strategy.SymbolName);
                continue;
            }

            var parameterEffects = getParameterEffects(strategy.SymbolName, hasBody && !effects.IsFfi);
            var memoryEffects = getFunctionMemoryEffects(strategy.SymbolName, hasBody && !effects.IsFfi);
            var definitionInternalize = strategy.Linkage == MonomorphizationLinkageKind.InternalSingleOwner;

            builder.AppendLine($"; specialization template: {strategy.TemplateName}");
            builder.AppendLine($"; specialization linkage: {strategy.Linkage}");

            if (hasBody && ssaFunction!.SupportsDirectCodeGeneration)
            {
                try
                {
                    // Package images publish typed template bodies, not use-site-specific
                    // monomorphized symbols. Emitting these specializations as
                    // available_externally lets LLVM discard the only real definition
                    // and leaves non-law callers with an unresolved symbol at link time.
                    var availableExternally = false;
                    if (strategy.Linkage == MonomorphizationLinkageKind.LinkOnceOdrComdat)
                    {
                        builder.AppendLine($"${escapeIdentifier(strategy.SymbolName)} = comdat any");
                    }

                    emitFunctionDefinition(
                        builder,
                        definitionInternalize,
                        availableExternally,
                        signature,
                        abiSignature,
                        effects,
                        memoryEffects,
                        ssaFunction,
                        parameterEffects,
                        resolveCallAbi,
                        strategy.Linkage);
                    builder.AppendLine();
                    continue;
                }
                catch (UnsupportedBodyEmissionException exception)
                {
                    builder.AppendLine($"; LLVM body emission fallback for {strategy.SymbolName}: {exception.Message}");
                    logLlvmFallback(
                        "llvm-body-fallback",
                        strategy.SymbolName,
                        exception.Message,
                        ssaFunction.BodyLoweringKind,
                        ssaFunction.SupportsDirectCodeGeneration);
                }
            }
            else if (hasBody)
            {
                builder.AppendLine($"; LLVM body emission pending for {strategy.SymbolName}");
                logLlvmFallback(
                    "llvm-body-pending",
                    strategy.SymbolName,
                    "SSA lowering did not leave this function in a direct-codegen-capable form, so LLVM emitted only a declaration.",
                    ssaFunction!.BodyLoweringKind,
                    ssaFunction.SupportsDirectCodeGeneration);
            }

            builder.AppendLine(buildDeclarationSignature(definitionInternalize, signature, abiSignature, effects, memoryEffects, parameterEffects));
            builder.AppendLine();
        }
    }

    public static IReadOnlyDictionary<string, ImportedLawClonePlan> BuildClosedWorldImportedLawClones(
        LoadedModuleSet loadedModules,
        SsaIrModule ssa,
        FunctionEffectModel effectModel,
        SyntaxModel syntaxModel,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        ClosedWorldOptimizationModel? closedWorldModel,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy,
        IReadOnlyDictionary<string, FunctionEffectProfile> allFunctionEffects,
        IReadOnlyDictionary<string, TypedFunctionSignature> allFunctionSignatures)
    {
        var importedDeclarations = CollectImportedFunctionDeclarations(loadedModules);
        var cloneEligibleSpecializations = CollectLawCloneEligibleSpecializations(specializationCodegenStrategy);
        if (importedDeclarations.Count == 0 && cloneEligibleSpecializations.Count == 0)
        {
            return new Dictionary<string, ImportedLawClonePlan>(StringComparer.Ordinal);
        }

        var ssaByName = ssa.Functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);
        var callsByFunction = CollectCallsByFunction(ssa);
        var recursiveImportedLawFunctions = FindRecursiveFunctions(
            callsByFunction,
            functionName =>
                (importedDeclarations.ContainsKey(functionName) || cloneEligibleSpecializations.ContainsKey(functionName))
                && effectModel.Functions.TryGetValue(functionName, out var effects)
                && FunctionKindFacts.IsLaw(effects.Kind));
        var rootLawFunctions = syntaxModel.Declarations
            .Where(static declaration => declaration.Function is not null)
            .Select(declaration => FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration))
            .Where(name => effectModel.Functions.TryGetValue(name, out var effects) && FunctionKindFacts.IsLaw(effects.Kind))
            .ToArray();
        var clones = new Dictionary<string, ImportedLawClonePlan>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        foreach (var rootFunction in rootLawFunctions)
        {
            if (!callsByFunction.TryGetValue(rootFunction, out var callees))
            {
                continue;
            }

            foreach (var callee in callees)
            {
                EnqueueIfEligible(callee);
            }
        }

        while (pending.Count != 0)
        {
            var functionName = pending.Dequeue();
            if (clones.ContainsKey(functionName))
            {
                continue;
            }

            if (!ssaByName.TryGetValue(functionName, out var ssaFunction))
            {
                continue;
            }

            var signature = allFunctionSignatures.TryGetValue(functionName, out var resolvedSignature)
                ? resolvedSignature
                : new TypedFunctionSignature(
                    functionName,
                    ssaFunction.ReturnType,
                    ssaFunction.Parameters,
                    SourceName: functionName);
            var effects = BuildLawCloneEffectProfile(functionName, allFunctionEffects);
            var cloneAbi = BuildSyntheticAbiSignature(
                signature,
                GetImportedLawCloneSymbolName(functionName),
                isFfi: false,
                typeModel.NamedTypes,
                enumLayoutModel.Layouts);
            clones[functionName] = new ImportedLawClonePlan(functionName, signature, cloneAbi, effects, ssaFunction);

            if (!callsByFunction.TryGetValue(functionName, out var importedCallees))
            {
                continue;
            }

            foreach (var callee in importedCallees)
            {
                EnqueueIfEligible(callee);
            }
        }

        return clones;

        void EnqueueIfEligible(string functionName)
        {
            if (!visited.Add(functionName))
            {
                return;
            }

            if (cloneEligibleSpecializations.TryGetValue(functionName, out var specializationStrategy))
            {
                if (!IsSpecializationLawCloneEligible(specializationStrategy, ssaByName, recursiveImportedLawFunctions, allFunctionEffects))
                {
                    return;
                }

                pending.Enqueue(functionName);
                return;
            }

            if (!IsImportedLawCloneEligible(
                    functionName,
                    importedDeclarations,
                    ssaByName,
                    recursiveImportedLawFunctions,
                    effectModel,
                    allFunctionSignatures,
                    closedWorldModel))
            {
                return;
            }

            pending.Enqueue(functionName);
        }
    }

    public static void LogSpecializationCodegenStrategies(
        CompilerLogBag? logs,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        if (logs is null || specializationCodegenStrategy is null)
        {
            return;
        }

        foreach (var strategy in specializationCodegenStrategy.Functions)
        {
            logs.Info(
                "decision",
                "specialization-codegen-strategy",
                $"Emit path for specialization '{strategy.SymbolName}' is '{strategy.StrategyKind}'.",
                stage: "emit-llvm",
                symbolName: strategy.SymbolName,
                operation: "specialization-codegen-strategy",
                location: strategy.FirstUseLocation,
                data: CompilerLogData.Create(
                    ("template", strategy.TemplateName),
                    ("linkage", strategy.Linkage.ToString()),
                    ("supportsAbiFallback", strategy.SupportsAbiFallback.ToString()),
                    ("strategy", strategy.StrategyKind.ToString())),
                kind: CompilerLogKind.Decision,
                outcome: CompilerLogOutcome.Continued,
                verbosity: CompilerLogVerbosity.Verbose);
        }
    }

    public static AbiFunctionSignature BuildSyntheticAbiSignature(
        TypedFunctionSignature function,
        string symbolName,
        bool isFfi,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts,
        bool isVarargs = false)
    {
        var returnsIndirect = !isFfi && AbiLoweringHeuristics.RequiresIndirectReturnAbi(function.ReturnType, namedTypes, enumLayouts);
        var parameters = new List<AbiParameterSymbol>();

        if (returnsIndirect)
        {
            parameters.Add(new AbiParameterSymbol(
                SourceName: "$ret",
                LlvmName: "ret",
                SourceType: function.ReturnType,
                LlvmType: StarkTypeSymbols.RawPointer(function.ReturnType, isMutable: true),
                Kind: AbiParameterKind.SRet));
        }

        foreach (var parameter in function.Parameters)
        {
            var kind = !isFfi && AbiLoweringHeuristics.RequiresIndirectParameterAbi(parameter.Type, namedTypes, enumLayouts)
                ? AbiParameterKind.IndirectIn
                : AbiParameterKind.Direct;

            parameters.Add(new AbiParameterSymbol(
                SourceName: parameter.Name,
                LlvmName: $"arg_{parameter.Name}",
                SourceType: parameter.Type,
                LlvmType: kind == AbiParameterKind.Direct
                    ? SyntheticLowerAbiValueType(parameter.Type, isFfi, forReturnValue: false)
                    : StarkTypeSymbols.RawPointer(parameter.Type, isMutable: false),
                Kind: kind));
        }

        return new AbiFunctionSignature(
            function.Name,
            symbolName,
            function.ReturnType,
            returnsIndirect
                ? StarkTypeSymbols.Void
                : SyntheticLowerAbiValueType(function.ReturnType, isFfi, forReturnValue: true),
            parameters,
            isFfi,
            SourceName: function.SourceName,
            UsesFastCallingConvention: !isFfi,
            IsVarargs: isVarargs);
    }

    private static IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> BuildPublishedFunctionSemantics(LoadedModuleSet loadedModules)
    {
        var semantics = new Dictionary<string, ImportedFunctionSemanticSummary>(StringComparer.Ordinal);

        foreach (var module in loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is not { } packageImageFacts)
            {
                continue;
            }

            foreach (var (qualifiedName, summary) in packageImageFacts.FunctionSemantics)
            {
                semantics[qualifiedName] = summary;
            }
        }

        return semantics;
    }

    private static Dictionary<string, FunctionSpecializationCodegenStrategy> CollectLawCloneEligibleSpecializations(
        SpecializationCodegenStrategyModel? specializationCodegenStrategy)
    {
        if (specializationCodegenStrategy is null)
        {
            return new Dictionary<string, FunctionSpecializationCodegenStrategy>(StringComparer.Ordinal);
        }

        return specializationCodegenStrategy.Functions
            .Where(static strategy => strategy.StrategyKind == FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBodyAndPreferLawCallerClone)
            .ToDictionary(static strategy => strategy.SymbolName, static strategy => strategy, StringComparer.Ordinal);
    }

    private static Dictionary<string, TopLevelDeclarationModel> CollectImportedFunctionDeclarations(LoadedModuleSet loadedModules)
    {
        var declarations = new Dictionary<string, TopLevelDeclarationModel>(StringComparer.Ordinal);

        foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
        {
            foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
            {
                var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                    module,
                    FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                declarations[qualifiedName] = declaration;
            }
        }

        return declarations;
    }

    private static FunctionEffectProfile BuildLawCloneEffectProfile(
        string functionName,
        IReadOnlyDictionary<string, FunctionEffectProfile> allFunctionEffects)
    {
        var effects = allFunctionEffects[functionName];
        return effects.InlinePreference == InlinePreference.Inline
            ? effects
            : effects with { InlinePreference = InlinePreference.Inline };
    }

    private static bool IsImportedLawCloneEligible(
        string functionName,
        IReadOnlyDictionary<string, TopLevelDeclarationModel> importedDeclarations,
        IReadOnlyDictionary<string, SsaFunction> ssaByName,
        ISet<string> recursiveImportedLawFunctions,
        FunctionEffectModel effectModel,
        IReadOnlyDictionary<string, TypedFunctionSignature> allFunctionSignatures,
        ClosedWorldOptimizationModel? closedWorldModel)
    {
        return importedDeclarations.TryGetValue(functionName, out var declaration)
            && declaration.Function is { HasBody: true } function
            && declaration.Visibility != StarkVisibility.Export
            && !function.Modifiers.IsFfi
            && !function.Modifiers.IsCold
            && function.Modifiers.InlinePreference != InlinePreference.NoInline
            && (!function.Modifiers.HasExplicitInlinePreference || function.Modifiers.InlinePreference == InlinePreference.Inline)
            && !recursiveImportedLawFunctions.Contains(functionName)
            && effectModel.Functions.TryGetValue(functionName, out var effects)
            && FunctionKindFacts.IsLaw(effects.Kind)
            && !effects.IsFfi
            && !effects.IsCold
            && allFunctionSignatures.ContainsKey(functionName)
            && ssaByName.TryGetValue(functionName, out var ssaFunction)
            && ssaFunction.HasBody
            && IsClosedWorldLawCloneEnabled(functionName, closedWorldModel)
            && ssaFunction.SupportsDirectCodeGeneration;
    }

    private static bool IsSpecializationLawCloneEligible(
        FunctionSpecializationCodegenStrategy strategy,
        IReadOnlyDictionary<string, SsaFunction> ssaByName,
        ISet<string> recursiveImportedLawFunctions,
        IReadOnlyDictionary<string, FunctionEffectProfile> allFunctionEffects)
    {
        return !recursiveImportedLawFunctions.Contains(strategy.SymbolName)
            && allFunctionEffects.TryGetValue(strategy.SymbolName, out var effects)
            && FunctionKindFacts.IsLaw(effects.Kind)
            && !effects.IsFfi
            && !effects.IsCold
            && ssaByName.TryGetValue(strategy.SymbolName, out var ssaFunction)
            && ssaFunction.HasBody
            && ssaFunction.SupportsDirectCodeGeneration;
    }

    private static bool IsClosedWorldLawCloneEnabled(
        string functionName,
        ClosedWorldOptimizationModel? closedWorldModel)
    {
        if (closedWorldModel?.Functions.TryGetValue(functionName, out var optimization) is not true)
        {
            return true;
        }

        return optimization.SelectionOrder.Contains(ClosedWorldCallLoweringStrategy.LawCallerSpecializedClone);
    }

    private static Dictionary<string, HashSet<string>> CollectCallsByFunction(SsaIrModule ssa)
    {
        var callsByFunction = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var function in ssa.Functions)
        {
            var callees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions.OfType<SsaValueInstruction>())
                {
                    if (instruction.Value is SsaCallRValue call)
                    {
                        callees.Add(call.FunctionName);
                    }
                }
            }

            callsByFunction[function.Name] = callees;
        }

        return callsByFunction;
    }

    private static HashSet<string> FindRecursiveFunctions(
        IReadOnlyDictionary<string, HashSet<string>> callGraph,
        Func<string, bool> include)
    {
        var visited = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        foreach (var function in callGraph.Keys.Where(include))
        {
            Visit(function);
        }

        return cyclic;

        void Visit(string function)
        {
            if (visited.TryGetValue(function, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    var cycleStart = stack.LastIndexOf(function);
                    if (cycleStart >= 0)
                    {
                        foreach (var item in stack.Skip(cycleStart))
                        {
                            cyclic.Add(item);
                        }
                    }
                }

                return;
            }

            visited[function] = VisitState.Visiting;
            stack.Add(function);

            if (callGraph.TryGetValue(function, out var callees))
            {
                foreach (var callee in callees.Where(include))
                {
                    Visit(callee);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            visited[function] = VisitState.Visited;
        }
    }

    private static StarkTypeSymbol SyntheticLowerAbiValueType(StarkTypeSymbol type, bool isFfi, bool forReturnValue)
    {
        if (!isFfi && forReturnValue)
        {
            return StarkTypeSymbols.BorrowReturnRuntimeType(type);
        }

        if (!isFfi)
        {
            return type;
        }

        return type.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false),
            StarkTypeKind.Unicode when !forReturnValue => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false),
            _ => type
        };
    }

    private static string GetImportedLawCloneSymbolName(string functionName)
    {
        return $"__stark_law_clone_{functionName}";
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
