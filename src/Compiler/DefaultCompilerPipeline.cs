using Stark.Parsing;

namespace Stark.Compiler;

public static class DefaultCompilerPipeline
{
    public static CompilerPipeline Create()
    {
        return new CompilerPipelineBuilder()
            .Add(new ParsePass())
            .Add(new BuildSyntaxModelPass())
            .Add(new IndexDeclarationsPass())
            .Add(new ResolveModuleGraphPass())
            .Add(new LoadModulesPass())
            .Add(new BuildSymbolCatalogPass())
            .Add(new DeriveFunctionEffectsPass())
            .Add(new TypeCheckPass())
            .Add(new EnumLayoutPass())
            .Add(new SemanticValidationPass())
            .Add(new RefineFunctionEffectsPass())
            .Add(new OwnershipValidationPass())
            .Add(new LowerToHighLevelIrPass())
            .Add(new LowerToMidLevelIrPass())
            .Add(new NonLexicalBorrowLifetimeValidationPass())
            .Add(new LowerToSsaIrPass())
            .Add(new CleanupSsaIrPass())
            .Add(new PropagateSsaConstantsPass())
            .Add(new LowerToAbiPass())
            .Add(new EmitLlvmIrPass())
            .Build();
    }

    private sealed class ParsePass : ICompilerPass
    {
        public string Id => "parse";

        public CompilerPhase Phase => CompilerPhase.Parsing;

        public PassExecutionMode ExecutionMode => PassExecutionMode.RunAlways;

        public IReadOnlyList<string> Dependencies => [];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = StarkSyntax.ParseCompilationUnit(context.Input.SourceText);
            context.Artifacts.Set(CompilerArtifactKeys.ParseResult, parseResult);

            foreach (var diagnostic in parseResult.Diagnostics)
            {
                context.Diagnostics.Error(
                    "STK1000",
                    diagnostic.Message,
                    Id,
                    new SourceLocation(context.Input.FilePath, diagnostic.Line, diagnostic.Column));
            }
        }
    }

    private sealed class BuildSyntaxModelPass : ICompilerPass
    {
        public string Id => "syntax-model";

        public CompilerPhase Phase => CompilerPhase.SyntaxModel;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["parse"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var buildResult = SyntaxModelFactory.CreateWithDiagnostics(parseResult, context.Options.TargetInfo);

            foreach (var diagnostic in buildResult.Diagnostics)
            {
                context.Diagnostics.Error(
                    diagnostic.Code,
                    diagnostic.Message,
                    Id,
                    new SourceLocation(context.Input.FilePath, diagnostic.Line, diagnostic.Column));
            }

            context.Artifacts.Set(CompilerArtifactKeys.SyntaxModel, buildResult.Model);
        }
    }

    private sealed class IndexDeclarationsPass : ICompilerPass
    {
        public string Id => "declaration-index";

        public CompilerPhase Phase => CompilerPhase.Declarations;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model"];

        public void Execute(CompilerPassContext context)
        {
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);

            var grouped = syntaxModel.Declarations
                .GroupBy(static declaration => declaration.Name, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<TopLevelDeclarationModel>)group.ToArray(),
                    StringComparer.Ordinal);

            context.Artifacts.Set(
                CompilerArtifactKeys.DeclarationIndex,
                new DeclarationIndex(syntaxModel.ModuleName, grouped, syntaxModel.Declarations));
        }
    }

    private sealed class BuildSymbolCatalogPass : ICompilerPass
    {
        public string Id => "symbol-catalog";

        public CompilerPhase Phase => CompilerPhase.Symbols;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["declaration-index", "module-graph"];

        public void Execute(CompilerPassContext context)
        {
            var declarations = context.Artifacts.GetRequired(CompilerArtifactKeys.DeclarationIndex);

            var exported = new List<string>();
            var published = new List<string>();
            var internalNames = new List<string>();
            var privateNames = new List<string>();

            foreach (var declaration in declarations.OrderedDeclarations)
            {
                switch (declaration.Visibility)
                {
                    case StarkVisibility.Export:
                        exported.Add(declaration.Name);
                        break;
                    case StarkVisibility.Public:
                        published.Add(declaration.Name);
                        break;
                    case StarkVisibility.Internal:
                        internalNames.Add(declaration.Name);
                        break;
                    case StarkVisibility.Module:
                        privateNames.Add(declaration.Name);
                        break;
                }
            }

            context.Artifacts.Set(
                CompilerArtifactKeys.SymbolCatalog,
                new SymbolCatalog(
                    declarations.ModuleName,
                    exported,
                    published,
                    internalNames,
                    privateNames));
        }
    }

    private sealed class ResolveModuleGraphPass : ICompilerPass
    {
        public string Id => "module-graph";

        public CompilerPhase Phase => CompilerPhase.ModuleResolution;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model"];

        public void Execute(CompilerPassContext context)
        {
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var resolver = context.Options.ModuleResolver ?? EmptyModuleResolver.Instance;
            var modules = new Dictionary<string, ResolvedModuleReference>(StringComparer.Ordinal)
            {
                [syntaxModel.ModuleName] = new ResolvedModuleReference(
                    syntaxModel.ModuleName,
                    context.Input.FilePath,
                    IsExternal: false,
                    IsRoot: true)
            };

            var imports = new List<ModuleImportEdge>();

            var pendingImports = new Queue<(string FromModule, ImportDeclarationModel Import)>(
                syntaxModel.Imports.Select(import => (syntaxModel.ModuleName, import)));
            var exploredModules = new HashSet<string>(StringComparer.Ordinal)
            {
                syntaxModel.ModuleName
            };

            while (pendingImports.Count != 0)
            {
                var (fromModule, import) = pendingImports.Dequeue();
                var importName = import.ModuleName;

                if (string.Equals(importName, syntaxModel.ModuleName, StringComparison.Ordinal))
                {
                    context.Diagnostics.Error(
                        "STK2001",
                        $"Module '{syntaxModel.ModuleName}' cannot import itself.",
                        Id);

                    if (string.Equals(fromModule, syntaxModel.ModuleName, StringComparison.Ordinal))
                    {
                        imports.Add(new ModuleImportEdge(
                            syntaxModel.ModuleName,
                            importName,
                            IsResolved: false,
                            Target: null,
                            IsExported: import.IsExported));
                    }

                    continue;
                }

                if (resolver.TryResolveModule(importName, out var resolved))
                {
                    modules[resolved.ModuleName] = resolved;

                    imports.Add(new ModuleImportEdge(
                        fromModule,
                        importName,
                        IsResolved: true,
                        Target: resolved,
                        IsExported: import.IsExported));

                    if (exploredModules.Add(resolved.ModuleName)
                        && resolver is IModuleSourceResolver sourceResolver
                        && sourceResolver.TryLoadModuleSource(resolved, out var sourceText, out var filePath))
                    {
                        var parseResult = StarkSyntax.ParseCompilationUnit(sourceText);
                        var importedSyntax = SyntaxModelFactory.Create(parseResult);

                        if (!string.Equals(importedSyntax.ModuleName, resolved.ModuleName, StringComparison.Ordinal))
                        {
                            context.Diagnostics.Error(
                                "STK2002",
                                $"Resolved module '{resolved.ModuleName}' declares itself as '{importedSyntax.ModuleName}'.",
                                Id,
                                new SourceLocation(filePath ?? resolved.FilePath, 1, 1));
                        }

                        foreach (var nestedImport in importedSyntax.Imports)
                        {
                            pendingImports.Enqueue((resolved.ModuleName, nestedImport));
                        }
                    }
                }
                else
                {
                    context.Diagnostics.Error(
                        "STK2000",
                        $"Unable to resolve imported module '{importName}'.",
                        Id);

                    imports.Add(new ModuleImportEdge(
                        fromModule,
                        importName,
                        IsResolved: false,
                        Target: null,
                        IsExported: import.IsExported));
                }
            }

            var accessibleModules = new HashSet<string>(StringComparer.Ordinal);
            var accessibleQueue = new Queue<string>();

            foreach (var edge in imports.Where(edge => edge.IsResolved && string.Equals(edge.FromModule, syntaxModel.ModuleName, StringComparison.Ordinal)))
            {
                var targetModule = edge.Target!.ModuleName;
                if (accessibleModules.Add(targetModule))
                {
                    accessibleQueue.Enqueue(targetModule);
                }
            }

            while (accessibleQueue.Count != 0)
            {
                var currentModule = accessibleQueue.Dequeue();

                foreach (var edge in imports.Where(edge =>
                             edge.IsResolved
                             && edge.IsExported
                             && string.Equals(edge.FromModule, currentModule, StringComparison.Ordinal)))
                {
                    var targetModule = edge.Target!.ModuleName;
                    if (accessibleModules.Add(targetModule))
                    {
                        accessibleQueue.Enqueue(targetModule);
                    }
                }
            }

            context.Artifacts.Set(
                CompilerArtifactKeys.ModuleGraph,
                new ModuleGraph(syntaxModel.ModuleName, modules, imports, accessibleModules));
        }
    }

    private sealed class LoadModulesPass : ICompilerPass
    {
        public string Id => "load-modules";

        public CompilerPhase Phase => CompilerPhase.ModuleResolution;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["parse", "syntax-model", "module-graph"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var resolver = context.Options.ModuleResolver as IModuleSourceResolver;

            var modules = new Dictionary<string, LoadedModuleDocument>(StringComparer.Ordinal)
            {
                [syntaxModel.ModuleName] = new LoadedModuleDocument(
                    new ResolvedModuleReference(syntaxModel.ModuleName, context.Input.FilePath, IsExternal: false, IsRoot: true),
                    parseResult,
                    syntaxModel)
            };

            if (resolver is not null)
            {
                foreach (var module in moduleGraph.Modules.Values.Where(static module => !module.IsRoot))
                {
                    if (!resolver.TryLoadModuleSource(module, out var sourceText, out var filePath))
                    {
                        continue;
                    }

                    var importedParse = StarkSyntax.ParseCompilationUnit(sourceText);
                    foreach (var diagnostic in importedParse.Diagnostics)
                    {
                        context.Diagnostics.Error(
                            "STK1000",
                            diagnostic.Message,
                            Id,
                            new SourceLocation(filePath ?? module.FilePath, diagnostic.Line, diagnostic.Column));
                    }

                    var importedBuildResult = SyntaxModelFactory.CreateWithDiagnostics(importedParse, context.Options.TargetInfo);
                    foreach (var diagnostic in importedBuildResult.Diagnostics)
                    {
                        context.Diagnostics.Error(
                            diagnostic.Code,
                            diagnostic.Message,
                            Id,
                            new SourceLocation(filePath ?? module.FilePath, diagnostic.Line, diagnostic.Column));
                    }

                    var importedSyntax = importedBuildResult.Model;
                    modules[module.ModuleName] = new LoadedModuleDocument(
                        module with { FilePath = filePath ?? module.FilePath },
                        importedParse,
                        importedSyntax);
                }
            }

            context.Artifacts.Set(
                CompilerArtifactKeys.LoadedModules,
                new LoadedModuleSet(syntaxModel.ModuleName, modules));
        }
    }

    private sealed class DeriveFunctionEffectsPass : ICompilerPass
    {
        public string Id => "function-effects";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "symbol-catalog", "load-modules"];

        public void Execute(CompilerPassContext context)
        {
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var effects = new Dictionary<string, FunctionEffectProfile>(StringComparer.Ordinal);

            foreach (var module in loadedModules.Modules.Values)
            {
                var isRoot = module.Reference.IsRoot;

                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
                {
                    var function = declaration.Function!;
                    var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                    effects[qualifiedName] = CreateEffectProfile(qualifiedName, function);
                }
            }

            context.Artifacts.Set(
                CompilerArtifactKeys.FunctionEffects,
                new FunctionEffectModel(loadedModules.RootModuleName, effects));
        }

        private static FunctionEffectProfile CreateEffectProfile(string name, FunctionDeclarationModel function)
        {
            if (function.Asm is not null)
            {
                var touchesMemory = function.Parameters.Any(static parameter => IsMemoryBackedType(parameter.TypeText))
                    || function.Asm.Outputs.Any(static output => !output.BindsReturnValue)
                    || function.Asm.Clobbers.Count != 0;

                return new FunctionEffectProfile(
                    Name: name,
                    Kind: StarkFunctionKind.Fn,
                    ReadsArgumentMemory: touchesMemory,
                    IsPure: false,
                    NoSync: false,
                    NoFree: false,
                    NoUnwind: false,
                    WillReturn: false,
                    MustProgress: false,
                    UseFastCallingConvention: false,
                    IsFfi: true,
                    IsHot: false,
                    IsCold: false,
                    InlinePreference: InlinePreference.InlineHint);
            }

            var isLaw = function.Kind is StarkFunctionKind.Law or StarkFunctionKind.FiniteLaw;
            var isFinite = function.Kind is StarkFunctionKind.Finite or StarkFunctionKind.FiniteLaw;
            var readsArgumentMemory = isLaw && function.Parameters.Any(static parameter => IsMemoryBackedType(parameter.TypeText));

            return new FunctionEffectProfile(
                Name: name,
                Kind: function.Kind,
                ReadsArgumentMemory: readsArgumentMemory,
                IsPure: isLaw,
                NoSync: isLaw,
                NoFree: isLaw,
                NoUnwind: !function.Modifiers.IsFfi,
                WillReturn: isFinite,
                MustProgress: isFinite,
                UseFastCallingConvention: !function.Modifiers.IsFfi,
                IsFfi: function.Modifiers.IsFfi,
                IsHot: function.Modifiers.IsHot,
                IsCold: function.Modifiers.IsCold,
                InlinePreference: function.Modifiers.InlinePreference);
        }

        private static bool IsMemoryBackedType(string typeText)
        {
            return typeText.Contains("borrow", StringComparison.Ordinal)
                || typeText.Contains("rawptr", StringComparison.Ordinal)
                || typeText.Contains("rawmutptr", StringComparison.Ordinal)
                || typeText.Contains('[', StringComparison.Ordinal)
                || typeText.Contains("ascii", StringComparison.Ordinal)
                || typeText.Contains("unicode", StringComparison.Ordinal);
        }
    }

    private sealed class TypeCheckPass : ICompilerPass
    {
        public string Id => "type-check";

        public CompilerPhase Phase => CompilerPhase.Typing;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["parse", "syntax-model", "module-graph", "load-modules", "function-effects"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);

            var typeCheckModel = new TypeChecker(context, parseResult, syntaxModel, moduleGraph, loadedModules).Check();
            context.Artifacts.Set(CompilerArtifactKeys.TypeCheckModel, typeCheckModel);
        }
    }

    private sealed class SemanticValidationPass : ICompilerPass
    {
        public string Id => "semantic-validate";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["parse", "syntax-model", "module-graph", "function-effects", "type-check", "enum-layout"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);

            var validationModel = new SemanticValidator(context, parseResult, syntaxModel, moduleGraph, effectModel, typeModel, enumLayoutModel).Validate();
            context.Artifacts.Set(CompilerArtifactKeys.SemanticValidation, validationModel);
        }
    }

    private sealed class OwnershipValidationPass : ICompilerPass
    {
        public string Id => "ownership-validate";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["parse", "syntax-model", "module-graph", "type-check", "semantic-validate"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);

            var ownershipModel = new OwnershipValidator(context, parseResult, syntaxModel, moduleGraph, typeModel).Validate();
            context.Artifacts.Set(CompilerArtifactKeys.OwnershipValidation, ownershipModel);
        }
    }

    private sealed class RefineFunctionEffectsPass : ICompilerPass
    {
        public string Id => "refine-function-effects";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "load-modules", "function-effects", "type-check", "semantic-validate"];

        public void Execute(CompilerPassContext context)
        {
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var validationModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation);
            var refined = effectModel.Functions
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
            var rootDeclarations = syntaxModel.Declarations
                .Where(static declaration => declaration.Function is not null)
                .ToDictionary(
                    declaration => FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration),
                    StringComparer.Ordinal);
            var recursiveLawFunctions = FindRecursiveFunctions(
                validationModel.Functions,
                static summary => FunctionKindFacts.IsLaw(summary.EffectiveKind));
            var lawOnlyCallTargets = FindLawOnlyCallTargets(validationModel.Functions);
            var importedDeclarations = CollectImportedFunctionDeclarations(loadedModules);
            var importedCallGraph = CollectImportedDirectCallGraph(loadedModules);
            var importedRecursiveLawFunctions = FindRecursiveFunctions(
                importedCallGraph,
                functionName => importedDeclarations.TryGetValue(functionName, out var declaration)
                    && FunctionKindFacts.IsLaw(declaration.Declaration.Function!.Kind));
            var importedLawOnlyCallTargets = FindImportedLawOnlyCallTargets(
                validationModel.Functions,
                importedDeclarations,
                importedCallGraph);

            foreach (var (name, existing) in effectModel.Functions)
            {
                if (!refined.ContainsKey(name))
                {
                    continue;
                }

                validationModel.Functions.TryGetValue(name, out var summary);
                var effectiveKind = summary?.EffectiveKind ?? existing.Kind;
                var isLaw = FunctionKindFacts.IsLaw(effectiveKind);
                var isFinite = FunctionKindFacts.IsFinite(effectiveKind);
                var readsArgumentMemory = summary?.MemoryEffects?.ReadsArgumentMemory ?? existing.ReadsArgumentMemory;
                var inlinePreference = DetermineInlinePreference(
                    name,
                    summary,
                    existing,
                    rootDeclarations,
                    lawOnlyCallTargets,
                    recursiveLawFunctions,
                    importedDeclarations,
                    importedLawOnlyCallTargets,
                    importedRecursiveLawFunctions);

                refined[name] = existing with
                {
                    Kind = effectiveKind,
                    ReadsArgumentMemory = readsArgumentMemory,
                    IsPure = isLaw,
                    NoSync = isLaw,
                    NoFree = isLaw,
                    WillReturn = isFinite,
                    MustProgress = isFinite,
                    InlinePreference = inlinePreference
                };
            }

            var refinedModel = new FunctionEffectModel(effectModel.ModuleName, refined);
            context.Artifacts.Set(CompilerArtifactKeys.FunctionEffects, refinedModel);
            context.Artifacts.Set(
                CompilerArtifactKeys.ClosedWorldOptimization,
                BuildClosedWorldOptimizationModel(
                    syntaxModel,
                    loadedModules,
                    typeModel,
                    refinedModel,
                    rootDeclarations,
                    importedDeclarations,
                    importedRecursiveLawFunctions));
        }

        private static InlinePreference DetermineInlinePreference(
            string functionName,
            FunctionValidationSummary? summary,
            FunctionEffectProfile existing,
            IReadOnlyDictionary<string, TopLevelDeclarationModel> rootDeclarations,
            ISet<string> lawOnlyCallTargets,
            ISet<string> recursiveLawFunctions,
            IReadOnlyDictionary<string, ImportedFunctionDeclaration> importedDeclarations,
            ISet<string> importedLawOnlyCallTargets,
            ISet<string> importedRecursiveLawFunctions)
        {
            if (summary is not null
                && rootDeclarations.TryGetValue(functionName, out var rootDeclaration)
                && rootDeclaration.Function is { HasBody: true } rootFunction
                && rootDeclaration.Visibility == StarkVisibility.Module
                && !rootFunction.Modifiers.HasExplicitInlinePreference
                && existing.InlinePreference == InlinePreference.InlineHint
                && !existing.IsFfi
                && !existing.IsCold
                && FunctionKindFacts.IsLaw(summary.EffectiveKind)
                && lawOnlyCallTargets.Contains(functionName)
                && !recursiveLawFunctions.Contains(functionName))
            {
                return InlinePreference.Inline;
            }

            if (!importedDeclarations.TryGetValue(functionName, out var importedDeclaration)
                || importedDeclaration.Declaration.Function is not { HasBody: true } importedFunction
                || importedDeclaration.Declaration.Visibility == StarkVisibility.Export
                || importedFunction.Modifiers.HasExplicitInlinePreference
                || existing.InlinePreference != InlinePreference.InlineHint
                || existing.IsFfi
                || existing.IsCold
                || !FunctionKindFacts.IsLaw(importedFunction.Kind)
                || !importedLawOnlyCallTargets.Contains(functionName)
                || importedRecursiveLawFunctions.Contains(functionName))
            {
                return existing.InlinePreference;
            }

            return InlinePreference.Inline;
        }

        private static ClosedWorldOptimizationModel BuildClosedWorldOptimizationModel(
            SyntaxModel syntaxModel,
            LoadedModuleSet loadedModules,
            TypeCheckModel typeModel,
            FunctionEffectModel effectModel,
            IReadOnlyDictionary<string, TopLevelDeclarationModel> rootDeclarations,
            IReadOnlyDictionary<string, ImportedFunctionDeclaration> importedDeclarations,
            ISet<string> importedRecursiveLawFunctions)
        {
            var sealedModules = loadedModules.Modules.Values
                .Where(static module => module.Reference.ManifestPath is null && module.Reference.LibraryPath is null)
                .Select(static module => module.SyntaxModel.ModuleName)
                .ToHashSet(StringComparer.Ordinal);
            var rootFunctionNames = rootDeclarations.Keys.ToHashSet(StringComparer.Ordinal);
            var typeInfos = typeModel.NamedTypes.Values
                .Where(static type => type.Kind is DeclarationKind.Trait or DeclarationKind.Doctrine)
                .ToDictionary(
                    type => type.Name,
                    type => new ClosedWorldTypeOptimizationInfo(
                        type.Name,
                        type.Kind,
                        ResolveClosedWorldSeal(type.Name, syntaxModel.ModuleName, sealedModules),
                        HasRuntimeDispatch: false),
                    StringComparer.Ordinal);
            var sourceFunctionDeclarations = CollectSourceLoadedFunctionDeclarations(
                rootDeclarations,
                importedDeclarations);
            var functionInfos = new Dictionary<string, ClosedWorldFunctionOptimizationInfo>(StringComparer.Ordinal);

            foreach (var function in effectModel.Functions.Values)
            {
                if (!TryResolveContainingAbstraction(function.Name, typeInfos, out var abstraction))
                {
                    continue;
                }

                functionInfos[function.Name] = BuildClosedWorldFunctionInfo(
                    function,
                    abstraction,
                    rootFunctionNames,
                    sourceFunctionDeclarations,
                    importedRecursiveLawFunctions);
            }

            return new ClosedWorldOptimizationModel(
                syntaxModel.ModuleName,
                typeInfos,
                functionInfos);
        }

        private static Dictionary<string, TopLevelDeclarationModel> CollectSourceLoadedFunctionDeclarations(
            IReadOnlyDictionary<string, TopLevelDeclarationModel> rootDeclarations,
            IReadOnlyDictionary<string, ImportedFunctionDeclaration> importedDeclarations)
        {
            var declarations = rootDeclarations.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);

            foreach (var (name, declaration) in importedDeclarations)
            {
                declarations[name] = declaration.Declaration;
            }

            return declarations;
        }

        private static ClosedWorldFunctionOptimizationInfo BuildClosedWorldFunctionInfo(
            FunctionEffectProfile function,
            ClosedWorldTypeOptimizationInfo abstraction,
            ISet<string> rootFunctionNames,
            IReadOnlyDictionary<string, TopLevelDeclarationModel> sourceFunctionDeclarations,
            ISet<string> importedRecursiveLawFunctions)
        {
            if (abstraction.Kind == DeclarationKind.Trait)
            {
                return new ClosedWorldFunctionOptimizationInfo(
                    function.Name,
                    abstraction.Kind,
                    abstraction.Seal,
                    [ClosedWorldCallLoweringStrategy.CompileTimeOnlyContract],
                    ClosedWorldCodeGenerationMode.MonomorphizationDeferred,
                    CanDevirtualize: false);
            }

            if (rootFunctionNames.Contains(function.Name) && sourceFunctionDeclarations.TryGetValue(function.Name, out var rootDeclaration))
            {
                return new ClosedWorldFunctionOptimizationInfo(
                    function.Name,
                    abstraction.Kind,
                    abstraction.Seal,
                    rootDeclaration.Function is { HasBody: true }
                        ? [ClosedWorldCallLoweringStrategy.DirectSharedBody]
                        : [ClosedWorldCallLoweringStrategy.DirectAbiBoundary],
                    ClosedWorldCodeGenerationMode.SharedCode,
                    CanDevirtualize: true);
            }

            if (!sourceFunctionDeclarations.TryGetValue(function.Name, out var sourceDeclaration))
            {
                return new ClosedWorldFunctionOptimizationInfo(
                    function.Name,
                    abstraction.Kind,
                    abstraction.Seal,
                    [ClosedWorldCallLoweringStrategy.DirectAbiBoundary],
                    ClosedWorldCodeGenerationMode.SharedCode,
                    CanDevirtualize: true);
            }

            IReadOnlyList<ClosedWorldCallLoweringStrategy> selectionOrder = CanUseLawCallerSpecializedClone(function, sourceDeclaration, importedRecursiveLawFunctions)
                ? [
                    ClosedWorldCallLoweringStrategy.LawCallerSpecializedClone,
                    ClosedWorldCallLoweringStrategy.DirectAbiBoundary]
                : [ClosedWorldCallLoweringStrategy.DirectAbiBoundary];
            var codeGenerationMode = selectionOrder.Contains(ClosedWorldCallLoweringStrategy.LawCallerSpecializedClone)
                ? ClosedWorldCodeGenerationMode.CallerSpecializedClone
                : ClosedWorldCodeGenerationMode.SharedCode;

            return new ClosedWorldFunctionOptimizationInfo(
                function.Name,
                abstraction.Kind,
                abstraction.Seal,
                selectionOrder,
                codeGenerationMode,
                CanDevirtualize: true);
        }

        private static bool TryResolveContainingAbstraction(
            string functionName,
            IReadOnlyDictionary<string, ClosedWorldTypeOptimizationInfo> typeInfos,
            out ClosedWorldTypeOptimizationInfo abstraction)
        {
            abstraction = null!;

            var separatorIndex = functionName.LastIndexOf('.');
            if (separatorIndex <= 0)
            {
                return false;
            }

            var containingTypeName = functionName[..separatorIndex];
            if (!typeInfos.TryGetValue(containingTypeName, out var resolvedAbstraction))
            {
                return false;
            }

            abstraction = resolvedAbstraction;
            return true;
        }

        private static ClosedWorldSealKind ResolveClosedWorldSeal(
            string qualifiedName,
            string rootModuleName,
            ISet<string> sealedModules)
        {
            var moduleName = GetModuleName(qualifiedName, rootModuleName);
            return sealedModules.Contains(moduleName)
                ? ClosedWorldSealKind.SealedByDefault
                : ClosedWorldSealKind.AbiBoundary;
        }

        private static string GetModuleName(string qualifiedName, string rootModuleName)
        {
            var separatorIndex = qualifiedName.IndexOf('.');
            return separatorIndex >= 0
                ? qualifiedName[..separatorIndex]
                : rootModuleName;
        }

        private static bool CanUseLawCallerSpecializedClone(
            FunctionEffectProfile function,
            TopLevelDeclarationModel declaration,
            ISet<string> importedRecursiveLawFunctions)
        {
            return declaration.Function is { HasBody: true } sourceFunction
                && declaration.Visibility != StarkVisibility.Export
                && !sourceFunction.Modifiers.IsFfi
                && !sourceFunction.Modifiers.IsCold
                && sourceFunction.Modifiers.InlinePreference != InlinePreference.NoInline
                && (!sourceFunction.Modifiers.HasExplicitInlinePreference || sourceFunction.Modifiers.InlinePreference == InlinePreference.Inline)
                && FunctionKindFacts.IsLaw(function.Kind)
                && !function.IsFfi
                && !function.IsCold
                && !importedRecursiveLawFunctions.Contains(function.Name);
        }

        private static Dictionary<string, ImportedFunctionDeclaration> CollectImportedFunctionDeclarations(LoadedModuleSet loadedModules)
        {
            var declarations = new Dictionary<string, ImportedFunctionDeclaration>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
            {
                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
                {
                    var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                    declarations[qualifiedName] = new ImportedFunctionDeclaration(module.SyntaxModel.ModuleName, declaration);
                }
            }

            return declarations;
        }

        private static Dictionary<string, HashSet<string>> CollectImportedDirectCallGraph(LoadedModuleSet loadedModules)
        {
            var callGraph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
            {
                var localFunctionsBySourceName = module.SyntaxModel.Declarations
                    .Where(static declaration => declaration.Function is not null)
                    .GroupBy(static declaration => declaration.Function!.Name, StringComparer.Ordinal)
                    .ToDictionary(
                        static group => group.Key,
                        group => group
                            .Select(declaration => FunctionOverloadFacts.QualifyResolvedName(
                                module,
                                FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration)))
                            .ToArray(),
                        StringComparer.Ordinal);

                foreach (var function in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
                {
                    var qualifiedName = $"{module.SyntaxModel.ModuleName}.{function.Name}";
                    var callees = new HashSet<string>(StringComparer.Ordinal);

                    if (function.Body.block() is { } body)
                    {
                        foreach (var callName in CollectDirectCallNames(body))
                        {
                            if (localFunctionsBySourceName.TryGetValue(callName, out var resolvedNames))
                            {
                                foreach (var resolvedName in resolvedNames)
                                {
                                    callees.Add(resolvedName);
                                }
                            }
                        }
                    }

                    callGraph[qualifiedName] = callees;
                }
            }

            return callGraph;
        }

        private static HashSet<string> CollectDirectCallNames(Antlr4.Runtime.Tree.IParseTree node)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            Collect(node, names);
            return names;

            static void Collect(Antlr4.Runtime.Tree.IParseTree current, HashSet<string> accumulator)
            {
                if (current is StarkParser.PostfixExpressionContext postfixExpression
                    && TryGetDirectCallName(postfixExpression, out var callName))
                {
                    accumulator.Add(callName);
                }

                for (var index = 0; index < current.ChildCount; index++)
                {
                    Collect(current.GetChild(index), accumulator);
                }
            }
        }

        private static bool TryGetDirectCallName(StarkParser.PostfixExpressionContext expression, out string callName)
        {
            callName = string.Empty;

            if (expression.primaryExpression() is not { } primaryExpression)
            {
                return false;
            }

            string? currentName = primaryExpression.Identifier()?.GetText()
                ?? primaryExpression.qualifiedName()?.GetText();
            if (currentName is null)
            {
                return false;
            }

            foreach (var postfixPart in expression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    callName = currentName;
                    return true;
                }

                if (postfixPart.Identifier() is { } identifier)
                {
                    currentName = $"{currentName}.{identifier.GetText()}";
                    continue;
                }

                return false;
            }

            return false;
        }

        private static HashSet<string> FindLawOnlyCallTargets(IReadOnlyDictionary<string, FunctionValidationSummary> summaries)
        {
            var callersByCallee = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var summary in summaries.Values)
            {
                foreach (var callee in summary.CalledFunctions)
                {
                    if (!summaries.ContainsKey(callee))
                    {
                        continue;
                    }

                    if (!callersByCallee.TryGetValue(callee, out var callers))
                    {
                        callers = new HashSet<string>(StringComparer.Ordinal);
                        callersByCallee[callee] = callers;
                    }

                    callers.Add(summary.Name);
                }
            }

            return callersByCallee
                .Where(static pair => pair.Value.Count != 0)
                .Where(pair => pair.Value.All(caller => FunctionKindFacts.IsLaw(summaries[caller].EffectiveKind)))
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static HashSet<string> FindLawOnlyCallTargets(
            IReadOnlyDictionary<string, HashSet<string>> callGraph,
            Func<string, bool> isLawFunction)
        {
            var callersByCallee = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var (caller, callees) in callGraph)
            {
                foreach (var callee in callees)
                {
                    if (!callersByCallee.TryGetValue(callee, out var callers))
                    {
                        callers = new HashSet<string>(StringComparer.Ordinal);
                        callersByCallee[callee] = callers;
                    }

                    callers.Add(caller);
                }
            }

            return callersByCallee
                .Where(static pair => pair.Value.Count != 0)
                .Where(pair => pair.Value.All(isLawFunction))
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static HashSet<string> FindImportedLawOnlyCallTargets(
            IReadOnlyDictionary<string, FunctionValidationSummary> rootSummaries,
            IReadOnlyDictionary<string, ImportedFunctionDeclaration> importedDeclarations,
            IReadOnlyDictionary<string, HashSet<string>> importedCallGraph)
        {
            var callersByCallee = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var summary in rootSummaries.Values)
            {
                foreach (var callee in summary.CalledFunctions.Where(importedDeclarations.ContainsKey))
                {
                    AddCaller(callee, summary.Name);
                }
            }

            foreach (var (caller, callees) in importedCallGraph)
            {
                foreach (var callee in callees.Where(importedDeclarations.ContainsKey))
                {
                    AddCaller(callee, caller);
                }
            }

            return callersByCallee
                .Where(static pair => pair.Value.Count != 0)
                .Where(pair => pair.Value.All(IsLawCaller))
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);

            bool IsLawCaller(string caller)
            {
                if (rootSummaries.TryGetValue(caller, out var rootSummary))
                {
                    return FunctionKindFacts.IsLaw(rootSummary.EffectiveKind);
                }

                return importedDeclarations.TryGetValue(caller, out var importedDeclaration)
                    && importedDeclaration.Declaration.Function is { } importedFunction
                    && FunctionKindFacts.IsLaw(importedFunction.Kind);
            }

            void AddCaller(string callee, string caller)
            {
                if (!callersByCallee.TryGetValue(callee, out var callers))
                {
                    callers = new HashSet<string>(StringComparer.Ordinal);
                    callersByCallee[callee] = callers;
                }

                callers.Add(caller);
            }
        }

        private static HashSet<string> FindRecursiveFunctions(
            IReadOnlyDictionary<string, FunctionValidationSummary> summaries,
            Func<FunctionValidationSummary, bool> include)
        {
            var visited = new Dictionary<string, VisitState>(StringComparer.Ordinal);
            var stack = new List<string>();
            var cyclic = new HashSet<string>(StringComparer.Ordinal);

            foreach (var function in summaries.Values.Where(include).Select(static summary => summary.Name))
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

                if (summaries.TryGetValue(function, out var summary))
                {
                    foreach (var callee in summary.CalledFunctions)
                    {
                        if (summaries.TryGetValue(callee, out var calleeSummary) && include(calleeSummary))
                        {
                            Visit(callee);
                        }
                    }
                }

                stack.RemoveAt(stack.Count - 1);
                visited[function] = VisitState.Visited;
            }
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

        private sealed record ImportedFunctionDeclaration(string ModuleName, TopLevelDeclarationModel Declaration);

        private enum VisitState
        {
            Visiting,
            Visited
        }
    }

    private sealed class LowerToHighLevelIrPass : ICompilerPass
    {
        public string Id => "lower-hir";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "load-modules", "module-graph", "refine-function-effects", "type-check", "semantic-validate", "ownership-validate"];

        public void Execute(CompilerPassContext context)
        {
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var effects = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var types = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var fallbackSignatures = CollectFallbackFunctionSignatures(context, moduleGraph, types.NamedTypes, loadedModules);

            var functions = loadedModules.Modules.Values
                .OrderBy(static module => module.Reference.IsRoot ? 0 : 1)
                .ThenBy(static module => module.SyntaxModel.ModuleName, StringComparer.Ordinal)
                .SelectMany(module => module.SyntaxModel.Declarations
                    .Where(static declaration => declaration.Function is not null)
                    .Select(declaration =>
                    {
                        var function = declaration.Function!;
                        var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                            module,
                            FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                        if (!effects.Functions.ContainsKey(qualifiedName)
                            || (!types.Functions.ContainsKey(qualifiedName) && !fallbackSignatures.ContainsKey(qualifiedName)))
                        {
                            return null;
                        }

                        return new HighLevelIrFunction(
                            qualifiedName,
                            types.Functions.TryGetValue(qualifiedName, out var signature)
                                ? signature
                                : fallbackSignatures[qualifiedName],
                            function.HasBody,
                            DetermineBodyLoweringKind(function),
                            effects.Functions[qualifiedName]);
                    })
                    .Where(static function => function is not null)
                    .Select(static function => function!))
                .ToArray();

            context.Artifacts.Set(
                CompilerArtifactKeys.HighLevelIr,
                new HighLevelIrModule(loadedModules.RootModuleName, functions));
        }

        private static FunctionBodyLoweringKind DetermineBodyLoweringKind(FunctionDeclarationModel function)
        {
            if (function.Asm is not null)
            {
                return FunctionBodyLoweringKind.AsmBypass;
            }

            return function.HasBody
                ? FunctionBodyLoweringKind.StarkCfg
                : FunctionBodyLoweringKind.DeclarationOnly;
        }

        private static Dictionary<string, TypedFunctionSignature> CollectFallbackFunctionSignatures(
            CompilerPassContext context,
            ModuleGraph moduleGraph,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            LoadedModuleSet loadedModules)
        {
            var resolver = new StarkTypeResolver(context, "lower-hir", moduleGraph, namedTypes);
            var functions = new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
            {
                foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
                {
                    var qualifiedName = $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                    var genericParameters = resolver.GetGenericParameterNames(declaration.TypeParameters);
                    var parameters = declaration.ParameterList.parameter()
                        .Select(parameter => new TypedParameterSymbol(
                            parameter.Identifier().GetText(),
                            resolver.ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName)))
                        .ToArray();
                    functions[qualifiedName] = new TypedFunctionSignature(
                        qualifiedName,
                        resolver.ResolveReturnType(declaration.ReturnType, genericParameters, module.SyntaxModel.ModuleName),
                        parameters,
                        SourceName: FunctionOverloadFacts.QualifySourceName(module, declaration.DisplaySourceName));
                }
            }

            return functions;
        }
    }

    private sealed class LowerToMidLevelIrPass : ICompilerPass
    {
        public string Id => "lower-mir";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["load-modules", "module-graph", "type-check", "enum-layout", "lower-hir"];

        public void Execute(CompilerPassContext context)
        {
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
            var hir = context.Artifacts.GetRequired(CompilerArtifactKeys.HighLevelIr);
            var mir = new MidLevelIrLowerer(context, loadedModules, moduleGraph, typeModel, enumLayoutModel).Lower(hir);
            context.Artifacts.Set(CompilerArtifactKeys.MidLevelIr, mir);
        }
    }

    private sealed class NonLexicalBorrowLifetimeValidationPass : ICompilerPass
    {
        public string Id => "borrow-liveness";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["type-check", "ownership-validate", "lower-mir"];

        public void Execute(CompilerPassContext context)
        {
            var mir = context.Artifacts.GetRequired(CompilerArtifactKeys.MidLevelIr);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var ownershipModel = context.Artifacts.GetRequired(CompilerArtifactKeys.OwnershipValidation);

            var refinedOwnership = new NonLexicalBorrowLifetimeValidator(context, mir, typeModel, ownershipModel).Validate();
            context.Artifacts.Set(CompilerArtifactKeys.OwnershipValidation, refinedOwnership);
        }
    }

    private sealed class EmitLlvmIrPass : ICompilerPass
    {
        public string Id => "emit-llvm";

        public CompilerPhase Phase => CompilerPhase.CodeGeneration;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "refine-function-effects", "type-check", "enum-layout", "semantic-validate", "const-prop", "lower-abi"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
            var validationModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation);
            var closedWorldModel = context.Artifacts.GetRequired(CompilerArtifactKeys.ClosedWorldOptimization);
            var abiModel = context.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var llvmModule = new LlvmIrEmitter(
                context.Input,
                parseResult,
                syntaxModel,
                loadedModules,
                effectModel,
                typeModel,
                enumLayoutModel,
                abiModel,
                ssa,
                context.Options.TargetInfo,
                internalizeModulePrivate: context.Options.QualifyModuleSymbols,
                semanticValidation: validationModel,
                closedWorldModel: closedWorldModel,
                logs: context.Logs).Emit();
            context.Artifacts.Set(CompilerArtifactKeys.LlvmIrModule, llvmModule);
        }
    }

    private sealed class LowerToSsaIrPass : ICompilerPass
    {
        public string Id => "lower-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["type-check", "lower-mir"];

        public void Execute(CompilerPassContext context)
        {
            var mir = context.Artifacts.GetRequired(CompilerArtifactKeys.MidLevelIr);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var ssa = new SsaLowerer(typeModel).Lower(mir);
            context.Artifacts.Set(CompilerArtifactKeys.SsaIr, ssa);
        }
    }

    private sealed class CleanupSsaIrPass : ICompilerPass
    {
        public string Id => "cleanup-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["lower-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.SsaIr);
            var optimized = new SsaCleanupOptimizer().Optimize(ssa);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, optimized);
        }
    }

    private sealed class PropagateSsaConstantsPass : ICompilerPass
    {
        public string Id => "const-prop";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["cleanup-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var optimized = new SsaConstantPropagator().Optimize(ssa);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, optimized);
        }
    }

    private sealed class LowerToAbiPass : ICompilerPass
    {
        public string Id => "lower-abi";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "type-check", "refine-function-effects"];

        public void Execute(CompilerPassContext context)
        {
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var abiModel = new AbiLowerer(syntaxModel, loadedModules, typeModel, effectModel, context.Options).Lower();
            context.Artifacts.Set(CompilerArtifactKeys.AbiModel, abiModel);
        }
    }

    private sealed class EnumLayoutPass : ICompilerPass
    {
        public string Id => "enum-layout";

        public CompilerPhase Phase => CompilerPhase.Typing;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["type-check"];

        public void Execute(CompilerPassContext context)
        {
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            context.Artifacts.Set(CompilerArtifactKeys.EnumLayoutModel, EnumLayoutBuilder.Build(typeModel));
        }
    }
}
