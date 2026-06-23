using System.Globalization;
using System.Numerics;
using Stark.Compiler.LlvmIrEmission;
using Stark.Parsing;

namespace Stark.Compiler;

public static class DefaultCompilerPipeline
{
    private static readonly IReadOnlySet<string> LoweringFallbackEventIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "unsupported-lowering",
        "missing-function-body"
    };

    private static readonly IReadOnlySet<string> BackendFallbackEventIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "llvm-body-fallback",
        "llvm-asm-fallback",
        "llvm-body-pending"
    };

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
            .Add(new InstantiationOwnershipPass())
            .Add(new MonomorphizationPlanPass())
            .Add(new EnumLayoutPass())
            .Add(new SemanticValidationPass())
            .Add(new RefineFunctionEffectsPass())
            .Add(new SpecializationPlanPass())
            .Add(new SpecializationCodegenStrategyPass())
            .Add(new OwnershipValidationPass())
            .Add(new ValidateLoweringContractPass())
            .Add(new LowerToHighLevelIrPass())
            .Add(new LowerToMidLevelIrPass())
            .Add(new NonLexicalBorrowLifetimeValidationPass())
            .Add(new LowerToSsaIrPass())
            .Add(new CleanupSsaIrPass())
            .Add(new PropagateSsaConstantsPass())
            .Add(new DevirtualizeSsaIrPass())
            .Add(new InlineSsaIrPass())
            .Add(new CseConstGraphCallsSsaPass())
            .Add(new OptimizeConstLookupTablesSsaPass())
            .Add(new SsaValueFactsPass())
            .Add(new OptimizeSsaDynamicStoragePass())
            .Add(new OptimizeSsaDynamicAppendLoopsPass())
            .Add(new SpecializeConstStdlibHelpersSsaPass())
            .Add(new SpecializeAsciiToUnicodeLiteralsSsaPass())
            .Add(new SpecializeConstantTextFormattingSsaPass())
            .Add(new PruneSsaBranchesPass())
            .Add(new OptimizeSsaMemoryPass())
            .Add(new OptimizeSsaAggregateConstructionPass())
            .Add(new OptimizeSsaOwnershipTrafficPass())
            .Add(new ScalarReplaceSsaAggregatesPass())
            .Add(new ShapeSsaBranchesPass())
            .Add(new FoldIntegerArithmeticSsaPass())
            .Add(new LowerToAbiPass())
            .Add(new ValidateSsaIrPass())
            .Add(new EmitLlvmIrPass())
            .Build();
    }

    private static bool EmitFallbackLogDiagnostics(
        CompilerPassContext context,
        string diagnosticCode,
        IReadOnlySet<string> eventIds)
    {
        var emittedKeys = new HashSet<(string Stage, string EventId, string SymbolName, string Message, SourceLocation Location)>();
        var emittedAny = false;

        foreach (var log in context.Logs.Items)
        {
            if (log.Kind != CompilerLogKind.Gap
                || !eventIds.Contains(log.EventId))
            {
                continue;
            }

            var message = log.Data.TryGetValue("reason", out var reason) && !string.IsNullOrWhiteSpace(reason)
                ? reason
                : log.Data.TryGetValue("feature", out var feature) && !string.IsNullOrWhiteSpace(feature)
                    ? $"Code generation does not yet support this construct ({feature})."
                    : log.Message;

            if (!emittedKeys.Add((log.Stage, log.EventId, log.SymbolName, message, log.Location)))
            {
                continue;
            }

            emittedAny = true;
            context.Diagnostics.Error(diagnosticCode, message, log.Stage, log.Location);
        }

        return emittedAny;
    }

    private static TypedFunctionSignature InstantiateSignatureForInstantiation(
        TypedFunctionSignature template,
        IReadOnlyList<StarkTypeSymbol> typeArguments,
        string materializedName,
        TypeCheckModel typeModel,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments = null)
    {
        return FunctionOverloadFacts.InstantiateSignature(
            template,
            typeArguments,
            materializedName,
            (ownerType, associatedTypeName) => ResolveAssociatedTypeForSubstitution(ownerType, associatedTypeName, typeModel),
            valueArguments);
    }

    private static StarkTypeSymbol SubstituteTypeForInstantiation(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        TypeCheckModel typeModel,
        IReadOnlyDictionary<string, BigInteger>? valueSubstitution = null)
    {
        return FunctionOverloadFacts.SubstituteType(
            type,
            substitution,
            (ownerType, associatedTypeName) => ResolveAssociatedTypeForSubstitution(ownerType, associatedTypeName, typeModel),
            valueSubstitution);
    }

    private static StarkTypeSymbol? ResolveAssociatedTypeForSubstitution(
        StarkTypeSymbol ownerType,
        string associatedTypeName,
        TypeCheckModel typeModel)
    {
        return AssociatedTypeFacts.TryResolveAssociatedType(
            ownerType,
            associatedTypeName,
            typeModel.NamedTypes,
            out var targetType)
                ? targetType
                : null;
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
            var sourceModuleParseCache = new Dictionary<string, SourceModuleParse>(StringComparer.Ordinal);

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

                    if (exploredModules.Add(resolved.ModuleName))
                    {
                        if (resolver is IModuleDocumentResolver documentResolver
                            && documentResolver.TryLoadModuleDocument(resolved, context.Options.TargetInfo, out var importedDocument))
                        {
                            var importedSyntax = importedDocument.SyntaxModel;

                            if (!string.Equals(importedSyntax.ModuleName, resolved.ModuleName, StringComparison.Ordinal))
                            {
                                context.Diagnostics.Error(
                                    "STK2002",
                                    $"Resolved module '{resolved.ModuleName}' declares itself as '{importedSyntax.ModuleName}'.",
                                    Id,
                                    new SourceLocation(importedDocument.Reference.FilePath ?? resolved.FilePath, 1, 1));
                            }

                            foreach (var nestedImport in importedSyntax.Imports)
                            {
                                pendingImports.Enqueue((resolved.ModuleName, nestedImport));
                            }
                        }
                        else if (context.Options.SharedSourceModuleParseCache is { } sharedParseCache
                                 && resolved.ManifestPath is null
                                 && sharedParseCache.TryGet(resolved.ModuleName, resolved.FilePath, out var sharedParse))
                        {
                            sourceModuleParseCache[resolved.ModuleName] = sharedParse;

                            if (!string.Equals(sharedParse.SyntaxModel.ModuleName, resolved.ModuleName, StringComparison.Ordinal))
                            {
                                context.Diagnostics.Error(
                                    "STK2002",
                                    $"Resolved module '{resolved.ModuleName}' declares itself as '{sharedParse.SyntaxModel.ModuleName}'.",
                                    Id,
                                    new SourceLocation(sharedParse.Reference.FilePath ?? resolved.FilePath, 1, 1));
                            }

                            foreach (var nestedImport in sharedParse.SyntaxModel.Imports)
                            {
                                pendingImports.Enqueue((resolved.ModuleName, nestedImport));
                            }
                        }
                        else if (resolver is IModuleSourceResolver sourceResolver
                                 && sourceResolver.TryLoadModuleSource(resolved, out var sourceText, out var filePath))
                        {
                            var cachedReference = resolved with { FilePath = filePath ?? resolved.FilePath };
                            var parseResult = StarkSyntax.ParseCompilationUnit(sourceText);
                            foreach (var diagnostic in parseResult.Diagnostics)
                            {
                                context.Diagnostics.Error(
                                    "STK1000",
                                    diagnostic.Message,
                                    Id,
                                    new SourceLocation(cachedReference.FilePath, diagnostic.Line, diagnostic.Column));
                            }

                            if (!parseResult.Succeeded)
                            {
                                continue;
                            }

                            var buildResult = SyntaxModelFactory.CreateWithDiagnostics(parseResult, context.Options.TargetInfo);
                            foreach (var diagnostic in buildResult.Diagnostics)
                            {
                                context.Diagnostics.Error(
                                    diagnostic.Code,
                                    diagnostic.Message,
                                    Id,
                                    new SourceLocation(cachedReference.FilePath, diagnostic.Line, diagnostic.Column));
                            }

                            var importedSyntax = buildResult.Model;
                            var sourceModuleParse = new SourceModuleParse(
                                cachedReference,
                                parseResult,
                                importedSyntax);
                            sourceModuleParseCache[resolved.ModuleName] = sourceModuleParse;

                            // Only diagnostic-free plain source parses may be reused by later
                            // pipeline runs; anything else must re-parse so diagnostics repeat.
                            if (context.Options.SharedSourceModuleParseCache is { } populateSharedCache
                                && resolved.ManifestPath is null
                                && parseResult.Diagnostics.Count == 0
                                && buildResult.Diagnostics.Count == 0)
                            {
                                populateSharedCache.Add(sourceModuleParse);
                            }

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
            context.Artifacts.Set(
                CompilerArtifactKeys.SourceModuleParseCache,
                new SourceModuleParseCache(sourceModuleParseCache));
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
            context.Artifacts.TryGet(CompilerArtifactKeys.SourceModuleParseCache, out SourceModuleParseCache? sourceModuleParseCache);
            var resolver = context.Options.ModuleResolver as IModuleSourceResolver;

            var modules = new Dictionary<string, LoadedModuleDocument>(StringComparer.Ordinal)
            {
                [syntaxModel.ModuleName] = new LoadedModuleDocument(
                    new ResolvedModuleReference(syntaxModel.ModuleName, context.Input.FilePath, IsExternal: false, IsRoot: true),
                    parseResult,
                    syntaxModel,
                    TargetInfo: context.Options.TargetInfo)
            };

            if (resolver is not null)
            {
                foreach (var module in moduleGraph.Modules.Values.Where(static module => !module.IsRoot))
                {
                    if (resolver is IModuleDocumentResolver documentResolver
                        && documentResolver.TryLoadModuleDocument(module, context.Options.TargetInfo, out var importedDocument))
                    {
                        foreach (var diagnostic in importedDocument.ParseResult.Diagnostics)
                        {
                            context.Diagnostics.Error(
                                "STK1000",
                                diagnostic.Message,
                                Id,
                                new SourceLocation(importedDocument.Reference.FilePath ?? module.FilePath, diagnostic.Line, diagnostic.Column));
                        }

                        if (!string.Equals(importedDocument.SyntaxModel.ModuleName, module.ModuleName, StringComparison.Ordinal))
                        {
                            context.Diagnostics.Error(
                                "STK2002",
                                $"Resolved module '{module.ModuleName}' declares itself as '{importedDocument.SyntaxModel.ModuleName}'.",
                                Id,
                                new SourceLocation(importedDocument.Reference.FilePath ?? module.FilePath, 1, 1));
                        }

                        var targetDiagnostics = new List<CompilerDiagnostic>();
                        TargetCompatibilityValidator.ValidateLoadedPackageTarget(
                            importedDocument,
                            context.Options.TargetInfo,
                            targetDiagnostics);
                        foreach (var diagnostic in targetDiagnostics)
                        {
                            context.Diagnostics.Add(diagnostic);
                        }

                        modules[module.ModuleName] = importedDocument;
                        continue;
                    }

                    if (sourceModuleParseCache is not null
                        && sourceModuleParseCache.TryGet(module.ModuleName, out var cachedParse)
                        && cachedParse is not null)
                    {
                        AddParsedSourceModule(context, modules, module, cachedParse);
                        continue;
                    }

                    if (!resolver.TryLoadModuleSource(module, out var sourceText, out var filePath))
                    {
                        continue;
                    }

                    var importedParse = StarkSyntax.ParseCompilationUnit(sourceText);
                    AddParsedSourceModule(
                        context,
                        modules,
                        module with { FilePath = filePath ?? module.FilePath },
                        importedParse);
                }
            }

            context.Artifacts.Set(
                CompilerArtifactKeys.LoadedModules,
                new LoadedModuleSet(syntaxModel.ModuleName, modules));
        }

        private void AddParsedSourceModule(
            CompilerPassContext context,
            Dictionary<string, LoadedModuleDocument> modules,
            ResolvedModuleReference graphReference,
            SourceModuleParse cachedParse)
        {
            var reference = graphReference with { FilePath = cachedParse.Reference.FilePath ?? graphReference.FilePath };
            AddParseDiagnostics(context, reference, cachedParse.ParseResult);

            modules[reference.ModuleName] = new LoadedModuleDocument(
                reference,
                cachedParse.ParseResult,
                cachedParse.SyntaxModel,
                TargetInfo: context.Options.TargetInfo);
        }

        private void AddParsedSourceModule(
            CompilerPassContext context,
            Dictionary<string, LoadedModuleDocument> modules,
            ResolvedModuleReference reference,
            ParseResult importedParse)
        {
            AddParseDiagnostics(context, reference, importedParse);

            var importedBuildResult = SyntaxModelFactory.CreateWithDiagnostics(importedParse, context.Options.TargetInfo);
            foreach (var diagnostic in importedBuildResult.Diagnostics)
            {
                context.Diagnostics.Error(
                    diagnostic.Code,
                    diagnostic.Message,
                    Id,
                    new SourceLocation(reference.FilePath, diagnostic.Line, diagnostic.Column));
            }

            modules[reference.ModuleName] = new LoadedModuleDocument(
                reference,
                importedParse,
                importedBuildResult.Model,
                TargetInfo: context.Options.TargetInfo);
        }

        private void AddParseDiagnostics(
            CompilerPassContext context,
            ResolvedModuleReference reference,
            ParseResult importedParse)
        {
            foreach (var diagnostic in importedParse.Diagnostics)
            {
                context.Diagnostics.Error(
                    "STK1000",
                    diagnostic.Message,
                    Id,
                    new SourceLocation(reference.FilePath, diagnostic.Line, diagnostic.Column));
            }
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
                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
                {
                    var function = declaration.Function!;
                    var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                    effects[qualifiedName] = CreateEffectProfile(qualifiedName, function, declaration.Visibility);
                }
            }

            foreach (var module in loadedModules.ImportedModules)
            {
                if (module.PackageImageFacts is not { } packageImageFacts)
                {
                    continue;
                }

                foreach (var (qualifiedName, functionEffects) in packageImageFacts.FunctionEffects)
                {
                    if (effects.ContainsKey(qualifiedName))
                    {
                        effects[qualifiedName] = functionEffects;
                    }
                }
            }

            context.Artifacts.Set(
                CompilerArtifactKeys.FunctionEffects,
                new FunctionEffectModel(loadedModules.RootModuleName, effects));
        }

        private static FunctionEffectProfile CreateEffectProfile(
            string name,
            FunctionDeclarationModel function,
            StarkVisibility visibility)
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
                    NoUnwind: true,
                    WillReturn: false,
                    MustProgress: false,
                    UseFastCallingConvention: false,
                    IsFfi: true,
                    IsVarargs: false,
                    FfiAbi: null,
                    IsHot: false,
                    IsCold: false,
                    InlinePreference: InlinePreference.InlineHint,
                    IsStrictFp: false,
                    BackendOptimizationMode: function.BackendOptimizationMode);
            }

            var isLaw = function.Kind is StarkFunctionKind.Law or StarkFunctionKind.FiniteLaw;
            var isFinite = function.Kind is StarkFunctionKind.Finite or StarkFunctionKind.FiniteLaw;
            var isTailCallable = function.Modifiers.IsTailCallable;
            var readsArgumentMemory = isLaw && function.Parameters.Any(static parameter => IsMemoryBackedType(parameter.TypeText));
            var inlinePreference = function.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
                ? InlinePreference.NoInline
                : function.Modifiers.InlinePreference;

            return new FunctionEffectProfile(
                Name: name,
                Kind: function.Kind,
                ReadsArgumentMemory: readsArgumentMemory,
                IsPure: isLaw,
                NoSync: isLaw,
                NoFree: isLaw,
                NoUnwind: true,
                WillReturn: isFinite,
                MustProgress: isFinite,
                UseFastCallingConvention: !isTailCallable && !function.Modifiers.IsFfi && visibility != StarkVisibility.Export,
                IsTailCallable: isTailCallable,
                IsFfi: function.Modifiers.IsFfi,
                IsVarargs: function.Modifiers.IsVarargs,
                FfiAbi: function.Modifiers.FfiAbi,
                IsHot: function.Modifiers.IsHot,
                IsCold: function.Modifiers.IsCold,
                InlinePreference: inlinePreference,
                IsStrictFp: function.Modifiers.IsStrictFp,
                BackendOptimizationMode: function.BackendOptimizationMode);
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

    private sealed class InstantiationOwnershipPass : ICompilerPass
    {
        public string Id => "instantiation-ownership";

        public CompilerPhase Phase => CompilerPhase.Typing;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["type-check", "load-modules"];

        public void Execute(CompilerPassContext context)
        {
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var ownership = BuildInstantiationOwnershipModel(loadedModules, typeModel);

            ValidateDictionaryKeyConstraints(context, ownership.Types, typeModel);
            ValidateGenericConstraints(context, ownership.Functions, typeModel);

            context.Artifacts.Set(
                CompilerArtifactKeys.InstantiationOwnership,
                ownership);
        }

        private static void ValidateDictionaryKeyConstraints(
            CompilerPassContext context,
            IEnumerable<TypeInstantiationOwnership> types,
            TypeCheckModel typeModel)
        {
            foreach (var type in types)
            {
                StarkTypeSymbol keyType;
                string collectionUseText;
                if (type.TemplateName is SystemCollectionsDictionaryKeyFacts.DictionaryTypeName
                    && type.TypeArguments.Count == 2)
                {
                    keyType = SystemCollectionsDictionaryKeyFacts.NormalizeType(type.TypeArguments[0]);
                    collectionUseText = $"Dictionary<{keyType.DisplayName}, V>";
                }
                else if (type.TemplateName is SystemCollectionsDictionaryKeyFacts.HashSetTypeName
                    && type.TypeArguments.Count == 1)
                {
                    keyType = SystemCollectionsDictionaryKeyFacts.NormalizeType(type.TypeArguments[0]);
                    collectionUseText = $"HashSet<{keyType.DisplayName}>";
                }
                else
                {
                    continue;
                }

                if (SystemCollectionsDictionaryKeyFacts.TryResolveContract(
                        keyType,
                        typeModel.Overloads,
                        out _,
                        out var diagnostic))
                {
                    continue;
                }

                context.Diagnostics.Error(
                    "STK3023",
                    $"{collectionUseText} collection use requires a compile-time DictionaryKey contract for key type '{keyType.DisplayName}'. {diagnostic}",
                    "instantiation-ownership",
                    type.FirstUseLocation);
            }
        }

        // Enforces `where T: Trait` bounds at each concrete generic call site: the
        // type argument bound to a constrained parameter must implement every
        // required trait (its base list, captured as `ImplementedTraits`). A
        // still-generic argument is skipped here and validated at its own concrete
        // instantiation. Deep method conformance is enforced at the `struct X :
        // Trait` declaration site.
        private static void ValidateGenericConstraints(
            CompilerPassContext context,
            IEnumerable<FunctionInstantiationOwnership> functions,
            TypeCheckModel typeModel)
        {
            foreach (var instantiation in functions)
            {
                var signature = instantiation.Signature;
                if (signature.Constraints.Count == 0)
                {
                    continue;
                }

                // The instantiated signature preserves the `where` constraints but
                // clears the generic-parameter name list, so take the parameter
                // ordering (which `TypeArguments` is positional against) from the
                // uninstantiated template.
                var genericParameters = signature.GenericParams;
                if (genericParameters.Count == 0
                    && signature.TemplateName is { } templateKey
                    && typeModel.Functions.TryGetValue(templateKey, out var templateSignature))
                {
                    genericParameters = templateSignature.GenericParams;
                }
                foreach (var constraint in signature.Constraints)
                {
                    var parameterIndex = -1;
                    for (var index = 0; index < genericParameters.Count; index++)
                    {
                        if (string.Equals(genericParameters[index], constraint.ParameterName, StringComparison.Ordinal))
                        {
                            parameterIndex = index;
                            break;
                        }
                    }

                    if (parameterIndex < 0 || parameterIndex >= instantiation.TypeArguments.Count)
                    {
                        continue;
                    }

                    var typeArgument = instantiation.TypeArguments[parameterIndex];
                    if (typeArgument.NamedType is not { } typeArgumentName
                        || !typeModel.NamedTypes.TryGetValue(typeArgumentName, out var typeArgumentSymbol))
                    {
                        // Not a concrete named type (e.g. still a type parameter of the
                        // caller); validated when the caller is itself instantiated.
                        continue;
                    }

                    foreach (var bound in constraint.BoundTraits)
                    {
                        if (bound.NamedType is not { } boundName)
                        {
                            continue;
                        }

                        if (!typeArgumentSymbol.ImplementedTraits.Contains(boundName))
                        {
                            context.Diagnostics.Error(
                                "STK3034",
                                $"Type argument '{typeArgumentSymbol.Name}' does not satisfy the '{bound.DisplayName}' bound on type parameter '{constraint.ParameterName}' of '{instantiation.TemplateName}'. The type must declare ': {bound.DisplayName}' in its base list.",
                                "instantiation-ownership",
                                instantiation.FirstUseLocation);
                        }
                    }
                }
            }
        }

        private static InstantiationOwnershipModel BuildInstantiationOwnershipModel(
            LoadedModuleSet loadedModules,
            TypeCheckModel typeModel)
        {
            var moduleNames = loadedModules.Modules.Keys
                .OrderByDescending(static moduleName => moduleName.Length)
                .ThenBy(static moduleName => moduleName, StringComparer.Ordinal)
                .ToArray();
            var functionOwnership = new Dictionary<string, FunctionInstantiationOwnership>(StringComparer.Ordinal);
            var typeOwnership = new Dictionary<string, TypeInstantiationOwnership>(StringComparer.Ordinal);
            var expandedFunctionTriggers = ExpandFunctionInstantiationTriggers(typeModel, loadedModules);
            var expandedTypeTriggers = ExpandTypeInstantiationTriggers(typeModel, loadedModules, expandedFunctionTriggers);
            var destructorFunctionTriggers = BuildDestructorFunctionInstantiationTriggers(typeModel, loadedModules, expandedTypeTriggers);
            if (destructorFunctionTriggers.Count > 0)
            {
                expandedFunctionTriggers = ExpandFunctionInstantiationTriggers(typeModel, loadedModules, destructorFunctionTriggers);
                expandedTypeTriggers = ExpandTypeInstantiationTriggers(typeModel, loadedModules, expandedFunctionTriggers);
            }

            foreach (var trigger in expandedFunctionTriggers)
            {
                var templateName = trigger.Signature.TemplateName ?? trigger.FunctionName;
                var declaringModuleName = ResolveDeclaringModuleName(templateName, loadedModules.RootModuleName, moduleNames);
                var ownerModuleName = ResolveOwnerModuleName(declaringModuleName, loadedModules);
                var key = $"{ownerModuleName}|{templateName}|{FunctionOverloadFacts.BuildInstantiationArgumentKey(trigger.TypeArguments, trigger.ComptimeValueArguments)}";
                if (functionOwnership.ContainsKey(key))
                {
                    continue;
                }

                functionOwnership[key] = new FunctionInstantiationOwnership(
                    templateName,
                    trigger.TypeArguments.ToArray(),
                    trigger.ComptimeValueArguments?.ToArray(),
                    trigger.Signature,
                    declaringModuleName,
                    ownerModuleName,
                    IsSourceBackedModule(declaringModuleName, loadedModules),
                    trigger.Location);
            }

            foreach (var trigger in expandedTypeTriggers)
            {
                var templateName = StarkTypeSymbols.GetGenericBaseName(trigger.TypeName);
                var declaringModuleName = ResolveDeclaringModuleName(templateName, loadedModules.RootModuleName, moduleNames);
                var ownerModuleName = ResolveOwnerModuleName(declaringModuleName, loadedModules);
                var key = $"{ownerModuleName}|{trigger.TypeName}";
                if (typeOwnership.ContainsKey(key))
                {
                    continue;
                }

                typeOwnership[key] = new TypeInstantiationOwnership(
                    templateName,
                    trigger.TypeName,
                    trigger.TypeArguments.ToArray(),
                    trigger.ComptimeValueArguments?.ToArray(),
                    declaringModuleName,
                    ownerModuleName,
                    IsSourceBackedModule(declaringModuleName, loadedModules),
                    trigger.Location);
            }

            return new InstantiationOwnershipModel(
                loadedModules.RootModuleName,
                functionOwnership.Values
                    .OrderBy(static ownership => ownership.OwnerModuleName, StringComparer.Ordinal)
                    .ThenBy(static ownership => ownership.TemplateName, StringComparer.Ordinal)
                    .ThenBy(static ownership => FunctionOverloadFacts.BuildTypeArgumentKey(ownership.TypeArguments), StringComparer.Ordinal)
                    .ToArray(),
                typeOwnership.Values
                    .OrderBy(static ownership => ownership.OwnerModuleName, StringComparer.Ordinal)
                    .ThenBy(static ownership => ownership.TemplateName, StringComparer.Ordinal)
                    .ThenBy(static ownership => ownership.InstantiatedTypeName, StringComparer.Ordinal)
                    .ToArray());
        }

        private static IReadOnlyList<FunctionInstantiationTriggerRecord> ExpandFunctionInstantiationTriggers(
            TypeCheckModel typeModel,
            LoadedModuleSet loadedModules,
            IReadOnlyList<FunctionInstantiationTriggerRecord>? additionalSeeds = null)
        {
            var expanded = new List<FunctionInstantiationTriggerRecord>();
            var pending = new Queue<FunctionInstantiationTriggerRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deferredByEnclosingFunction = typeModel.DeferredInstantiationTriggers
                .GroupBy(static trigger => trigger.EnclosingFunctionName, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<DeferredFunctionInstantiationTriggerRecord>)group.ToArray(),
                    StringComparer.Ordinal);
            var importedDeferredByTemplate = loadedModules.ImportedModules
                .Where(static module => module.PackageImageFacts is { FunctionTemplates.Count: > 0 })
                .SelectMany(static module => module.PackageImageFacts!.FunctionTemplates)
                .ToDictionary(
                    static entry => entry.Key,
                    static entry => entry.Value,
                    StringComparer.Ordinal);
            var availableFunctionSignatures = new Dictionary<string, TypedFunctionSignature>(
                typeModel.Functions,
                StringComparer.Ordinal);
            foreach (var signature in loadedModules.ImportedModules
                         .Where(static module => module.PackageImageFacts is { FunctionSignatures.Count: > 0 })
                         .SelectMany(static module => module.PackageImageFacts!.FunctionSignatures))
            {
                availableFunctionSignatures.TryAdd(signature.Key, signature.Value);
            }

            foreach (var trigger in typeModel.InstantiationTriggers)
            {
                if (TryAddExpandedTrigger(trigger, seen, expanded))
                {
                    pending.Enqueue(trigger);
                }
            }

            foreach (var trigger in additionalSeeds ?? [])
            {
                if (TryAddExpandedTrigger(trigger, seen, expanded))
                {
                    pending.Enqueue(trigger);
                }
            }

            while (pending.Count != 0)
            {
                var trigger = pending.Dequeue();
                var enclosingTemplateName = trigger.Signature.TemplateName ?? trigger.FunctionName;
                if (!availableFunctionSignatures.TryGetValue(enclosingTemplateName, out var enclosingTemplateSignature))
                {
                    continue;
                }

                var substitution = FunctionOverloadFacts.BuildGenericSubstitution(enclosingTemplateSignature, trigger.TypeArguments);
                var valueSubstitution = FunctionOverloadFacts.BuildComptimeValueSubstitution(enclosingTemplateSignature, trigger.ComptimeValueArguments);
                if (importedDeferredByTemplate.TryGetValue(enclosingTemplateName, out var importedTemplate))
                {
                    ExpandImportedDeferredFunctionTriggers(
                        importedTemplate,
                        substitution,
                        valueSubstitution,
                        trigger.Location,
                        typeModel,
                        availableFunctionSignatures,
                        seen,
                        expanded,
                        pending);
                    ExpandImportedTemplateCallSummaryTriggers(
                        GetImportedTemplateReachableCallSignatures(importedTemplate),
                        substitution,
                        valueSubstitution,
                        trigger.Location,
                        typeModel,
                        availableFunctionSignatures,
                        seen,
                        expanded,
                        pending);

                    continue;
                }

                if (!deferredByEnclosingFunction.TryGetValue(enclosingTemplateName, out var deferredTriggers))
                {
                    continue;
                }

                foreach (var deferredTrigger in deferredTriggers)
                {
                    if (deferredTrigger.Signature.TemplateName is not { } calleeTemplateName
                        || !typeModel.Functions.TryGetValue(calleeTemplateName, out var calleeTemplateSignature))
                    {
                        continue;
                    }

                    var openTypeArguments = deferredTrigger.Signature.TypeArguments ?? [];
                    var openValueArguments = deferredTrigger.Signature.ComptimeValueArguments;
                    if (openTypeArguments.Count == 0 && openValueArguments is not { Count: > 0 })
                    {
                        continue;
                    }

                    var concreteTypeArguments = openTypeArguments
                        .Select(typeArgument => SubstituteTypeForInstantiation(typeArgument, substitution, typeModel, valueSubstitution))
                        .ToArray();
                    if (concreteTypeArguments.Any(typeArgument => ContainsUnboundGenericParameter(typeArgument, typeModel)))
                    {
                        continue;
                    }
                    var concreteValueArguments = FunctionOverloadFacts.SubstituteComptimeValues(openValueArguments, valueSubstitution);
                    if (concreteValueArguments is { Count: > 0 } && concreteValueArguments.Any(static value => value.IsSymbolic))
                    {
                        continue;
                    }

                    var instantiatedSignature = InstantiateSignatureForInstantiation(
                        calleeTemplateSignature,
                        concreteTypeArguments,
                        calleeTemplateSignature.Name,
                        typeModel,
                        concreteValueArguments);
                    var expandedTrigger = new FunctionInstantiationTriggerRecord(
                        calleeTemplateSignature.DisplaySourceName,
                        concreteTypeArguments,
                        concreteValueArguments?.ToArray(),
                        instantiatedSignature,
                        deferredTrigger.Location);
                    if (TryAddExpandedTrigger(expandedTrigger, seen, expanded))
                    {
                        pending.Enqueue(expandedTrigger);
                    }
                }
            }

            return expanded
                .OrderBy(static trigger => trigger.Signature.TemplateName ?? trigger.FunctionName, StringComparer.Ordinal)
                .ThenBy(static trigger => FunctionOverloadFacts.BuildInstantiationArgumentKey(trigger.TypeArguments, trigger.ComptimeValueArguments), StringComparer.Ordinal)
                .ToArray();
        }

        private static IReadOnlyList<FunctionInstantiationTriggerRecord> BuildDestructorFunctionInstantiationTriggers(
            TypeCheckModel typeModel,
            LoadedModuleSet loadedModules,
            IReadOnlyList<TypeInstantiationTriggerRecord> expandedTypeTriggers)
        {
            var destructorCallsByType = CollectZeroArgumentSelfMemberDestructorCalls(loadedModules);
            if (destructorCallsByType.Count == 0)
            {
                return [];
            }

            var triggers = new List<FunctionInstantiationTriggerRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var typeTrigger in expandedTypeTriggers)
            {
                var baseTypeName = StarkTypeSymbols.GetGenericBaseName(typeTrigger.TypeName);
                if (!destructorCallsByType.TryGetValue(baseTypeName, out var memberNames)
                    || typeTrigger.TypeArguments.Count == 0)
                {
                    continue;
                }

                var receiverType = StarkTypeSymbols.GenericInstantiation(baseTypeName, typeTrigger.TypeArguments);
                foreach (var memberName in memberNames)
                {
                    var sourceName = $"{baseTypeName}.{memberName}";
                    if (!typeModel.Overloads.TryGetValue(sourceName, out var overloads))
                    {
                        continue;
                    }

                    var instanceOverloads = overloads
                        .Where(static overload => !overload.IsStatic)
                        .ToArray();
                    var resolution = FunctionOverloadFacts.Resolve(
                        instanceOverloads,
                        receiverType,
                        [],
                        TypeCompatibilityFacts.CanAssign,
                        (ownerType, associatedTypeName) => ResolveAssociatedTypeForSubstitution(ownerType, associatedTypeName, typeModel));
                    if (!resolution.Succeeded
                        || resolution.Match is not { IsGenericInstantiation: true } signature
                        || signature.TypeArguments is not { Count: > 0 } typeArguments
                        || signature.TemplateName is null
                        || !typeModel.Functions.TryGetValue(signature.TemplateName, out var templateSignature))
                    {
                        continue;
                    }

                    var instantiatedSignature = InstantiateSignatureForInstantiation(
                        templateSignature,
                        typeArguments,
                        templateSignature.Name,
                        typeModel);
                    var key = $"{instantiatedSignature.TemplateName ?? instantiatedSignature.Name}|{FunctionOverloadFacts.BuildTypeArgumentKey(typeArguments)}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    triggers.Add(new FunctionInstantiationTriggerRecord(
                        templateSignature.DisplaySourceName,
                        typeArguments.ToArray(),
                        null,
                        instantiatedSignature,
                        typeTrigger.Location));
                }
            }

            return triggers
                .OrderBy(static trigger => trigger.Signature.TemplateName ?? trigger.FunctionName, StringComparer.Ordinal)
                .ThenBy(static trigger => FunctionOverloadFacts.BuildInstantiationArgumentKey(trigger.TypeArguments, trigger.ComptimeValueArguments), StringComparer.Ordinal)
                .ToArray();
        }

        private static Dictionary<string, IReadOnlySet<string>> CollectZeroArgumentSelfMemberDestructorCalls(
            LoadedModuleSet loadedModules)
        {
            var callsByType = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

            foreach (var module in loadedModules.Modules.Values)
            {
                foreach (var destructor in DeclaredDestructorSyntaxCollector.Collect(module))
                {
                    var calls = CollectZeroArgumentSelfMemberCalls(destructor.Body);
                    if (calls.Count == 0)
                    {
                        continue;
                    }

                    callsByType[destructor.QualifiedTypeName] = calls;
                }
            }

            return callsByType;
        }

        private static IReadOnlySet<string> CollectZeroArgumentSelfMemberCalls(Antlr4.Runtime.Tree.IParseTree root)
        {
            var calls = new HashSet<string>(StringComparer.Ordinal);
            Visit(root);
            return calls;

            void Visit(Antlr4.Runtime.Tree.IParseTree node)
            {
                if (node is StarkParser.PostfixExpressionContext postfix
                    && string.Equals(postfix.primaryExpression()?.GetText(), "self", StringComparison.Ordinal))
                {
                    var parts = postfix.postfixPart();
                    for (var index = 0; index + 1 < parts.Length; index++)
                    {
                        var memberName = parts[index].Identifier()?.GetText();
                        var arguments = parts[index + 1].argumentList();
                        if (memberName is not null && arguments is not null && arguments.argument().Length == 0)
                        {
                            calls.Add(memberName);
                        }
                    }
                }

                for (var index = 0; index < node.ChildCount; index++)
                {
                    Visit(node.GetChild(index));
                }
            }
        }

        private static IReadOnlyList<TypeInstantiationTriggerRecord> ExpandTypeInstantiationTriggers(
            TypeCheckModel typeModel,
            LoadedModuleSet loadedModules,
            IReadOnlyList<FunctionInstantiationTriggerRecord> expandedFunctionTriggers)
        {
            var expanded = new List<TypeInstantiationTriggerRecord>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deferredByEnclosingFunction = typeModel.DeferredTypeTriggers
                .GroupBy(static trigger => trigger.EnclosingFunctionName, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<DeferredTypeInstantiationTriggerRecord>)group.ToArray(),
                    StringComparer.Ordinal);
            var importedDeferredByTemplate = loadedModules.ImportedModules
                .Where(static module => module.PackageImageFacts is { FunctionTemplates.Count: > 0 })
                .SelectMany(static module => module.PackageImageFacts!.FunctionTemplates)
                .ToDictionary(
                    static entry => entry.Key,
                    static entry => entry.Value,
                    StringComparer.Ordinal);

            foreach (var trigger in typeModel.TypeTriggers)
            {
                AddExpandedTypeTriggers(
                    StarkTypeSymbols.GenericInstantiation(
                        StarkTypeSymbols.GetGenericBaseName(trigger.TypeName),
                        trigger.TypeArguments),
                    trigger.Location,
                    typeModel,
                    seen,
                    expanded);
            }

            foreach (var functionTrigger in expandedFunctionTriggers)
            {
                var enclosingTemplateName = functionTrigger.Signature.TemplateName ?? functionTrigger.FunctionName;
                if (!typeModel.Functions.TryGetValue(enclosingTemplateName, out var enclosingTemplateSignature))
                {
                    continue;
                }

                var substitution = FunctionOverloadFacts.BuildGenericSubstitution(enclosingTemplateSignature, functionTrigger.TypeArguments);
                foreach (var typeArgument in functionTrigger.TypeArguments)
                {
                    AddExpandedTypeTriggers(typeArgument, functionTrigger.Location, typeModel, seen, expanded);
                }

                if (importedDeferredByTemplate.TryGetValue(enclosingTemplateName, out var importedTemplate))
                {
                    AddTypeTriggersFromImportedTemplateFacts(
                        importedTemplate,
                        substitution,
                        functionTrigger.Location,
                        typeModel,
                        seen,
                        expanded);

                    continue;
                }

                if (!deferredByEnclosingFunction.TryGetValue(enclosingTemplateName, out var deferredTypes))
                {
                    continue;
                }

                foreach (var deferredType in deferredTypes)
                {
                    var concreteType = SubstituteTypeForInstantiation(deferredType.Type, substitution, typeModel);
                    if (ContainsUnboundGenericParameter(concreteType, typeModel))
                    {
                        continue;
                    }

                    AddExpandedTypeTriggers(concreteType, deferredType.Location, typeModel, seen, expanded);
                }
            }

            return expanded
                .OrderBy(static trigger => trigger.TypeName, StringComparer.Ordinal)
                .ThenBy(static trigger => FunctionOverloadFacts.BuildInstantiationArgumentKey(trigger.TypeArguments, trigger.ComptimeValueArguments), StringComparer.Ordinal)
                .ToArray();
        }

        private static void ExpandImportedDeferredFunctionTriggers(
            ImportedFunctionTemplateSummary importedTemplate,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            IReadOnlyDictionary<string, BigInteger> valueSubstitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            IReadOnlyDictionary<string, TypedFunctionSignature> availableFunctionSignatures,
            ISet<string> seen,
            ICollection<FunctionInstantiationTriggerRecord> expanded,
            Queue<FunctionInstantiationTriggerRecord> pending)
        {
            foreach (var deferredTrigger in importedTemplate.DeferredInstantiations)
            {
                if (!availableFunctionSignatures.TryGetValue(deferredTrigger.CalleeTemplateName, out var calleeTemplateSignature))
                {
                    continue;
                }

                TryEnqueueFunctionTriggerFromOpenTypeArguments(
                    calleeTemplateSignature,
                    deferredTrigger.TypeArguments,
                    deferredTrigger.ComptimeValueArguments,
                    substitution,
                    valueSubstitution,
                    location,
                    typeModel,
                    seen,
                    expanded,
                    pending);
            }
        }

        private static void ExpandImportedTemplateCallSummaryTriggers(
            IEnumerable<TypedFunctionSignature> callSignatures,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            IReadOnlyDictionary<string, BigInteger> valueSubstitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            IReadOnlyDictionary<string, TypedFunctionSignature> availableFunctionSignatures,
            ISet<string> seen,
            ICollection<FunctionInstantiationTriggerRecord> expanded,
            Queue<FunctionInstantiationTriggerRecord> pending)
        {
            foreach (var callSignature in callSignatures)
            {
                if (callSignature.TemplateName is not { } calleeTemplateName
                    || !availableFunctionSignatures.TryGetValue(calleeTemplateName, out var calleeTemplateSignature))
                {
                    continue;
                }

                var openTypeArguments = callSignature.TypeArguments ?? [];
                var openValueArguments = callSignature.ComptimeValueArguments;
                if (openTypeArguments.Count == 0 && openValueArguments is not { Count: > 0 })
                {
                    continue;
                }

                TryEnqueueFunctionTriggerFromOpenTypeArguments(
                    calleeTemplateSignature,
                    openTypeArguments,
                    openValueArguments,
                    substitution,
                    valueSubstitution,
                    location,
                    typeModel,
                    seen,
                    expanded,
                    pending);
            }
        }

        private static void TryEnqueueFunctionTriggerFromOpenTypeArguments(
            TypedFunctionSignature calleeTemplateSignature,
            IReadOnlyList<StarkTypeSymbol> openTypeArguments,
            IReadOnlyList<ComptimeValueArgumentSymbol>? openValueArguments,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            IReadOnlyDictionary<string, BigInteger> valueSubstitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<FunctionInstantiationTriggerRecord> expanded,
            Queue<FunctionInstantiationTriggerRecord> pending)
        {
            var concreteTypeArguments = openTypeArguments
                .Select(typeArgument => SubstituteTypeForInstantiation(typeArgument, substitution, typeModel, valueSubstitution))
                .ToArray();
            if (concreteTypeArguments.Any(typeArgument => ContainsUnboundGenericParameter(typeArgument, typeModel)))
            {
                return;
            }
            var concreteValueArguments = FunctionOverloadFacts.SubstituteComptimeValues(openValueArguments, valueSubstitution);
            if (concreteValueArguments is { Count: > 0 } && concreteValueArguments.Any(static value => value.IsSymbolic))
            {
                return;
            }

            var instantiatedSignature = InstantiateSignatureForInstantiation(
                calleeTemplateSignature,
                concreteTypeArguments,
                calleeTemplateSignature.Name,
                typeModel,
                concreteValueArguments);
            var expandedTrigger = new FunctionInstantiationTriggerRecord(
                calleeTemplateSignature.DisplaySourceName,
                concreteTypeArguments,
                concreteValueArguments?.ToArray(),
                instantiatedSignature,
                location);
            if (TryAddExpandedTrigger(expandedTrigger, seen, expanded))
            {
                pending.Enqueue(expandedTrigger);
            }
        }

        private static void AddTypeTriggersFromImportedTemplateFacts(
            ImportedFunctionTemplateSummary importedTemplate,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded)
        {
            foreach (var deferredType in importedTemplate.DeferredTypes)
            {
                var concreteType = SubstituteTypeForInstantiation(deferredType.Type, substitution, typeModel);
                if (ContainsUnboundGenericParameter(concreteType, typeModel))
                {
                    continue;
                }

                AddExpandedTypeTriggers(concreteType, location, typeModel, seen, expanded);
            }

            foreach (var objectCreation in importedTemplate.ObjectCreations)
            {
                var concreteCreatedType = SubstituteTypeForInstantiation(objectCreation.CreatedType, substitution, typeModel);
                if (!ContainsUnboundGenericParameter(concreteCreatedType, typeModel))
                {
                    AddExpandedTypeTriggers(concreteCreatedType, location, typeModel, seen, expanded);
                }

                if (objectCreation.Constructor is not { } constructor)
                {
                    continue;
                }

                foreach (var parameter in constructor.Parameters)
                {
                    var concreteParameterType = SubstituteTypeForInstantiation(parameter.Type, substitution, typeModel);
                    if (ContainsUnboundGenericParameter(concreteParameterType, typeModel))
                    {
                        continue;
                    }

                    AddExpandedTypeTriggers(concreteParameterType, location, typeModel, seen, expanded);
                }
            }

            foreach (var localDeclaration in importedTemplate.LocalDeclarations)
            {
                var concreteLocalType = SubstituteTypeForInstantiation(localDeclaration.Type, substitution, typeModel);
                if (ContainsUnboundGenericParameter(concreteLocalType, typeModel))
                {
                    continue;
                }

                AddExpandedTypeTriggers(concreteLocalType, location, typeModel, seen, expanded);
            }

            foreach (var conversion in importedTemplate.Conversions)
            {
                var concreteTargetType = SubstituteTypeForInstantiation(conversion.TargetType, substitution, typeModel);
                if (ContainsUnboundGenericParameter(concreteTargetType, typeModel))
                {
                    continue;
                }

                AddExpandedTypeTriggers(concreteTargetType, location, typeModel, seen, expanded);
            }

            foreach (var callSignature in GetImportedTemplateReachableCallSignatures(importedTemplate))
            {
                AddTypeTriggersFromCallSignature(
                    callSignature,
                    substitution,
                    location,
                    typeModel,
                    seen,
                    expanded);
            }

            AddTypeTriggersFromImportedTemplateBoundOperations(
                importedTemplate,
                substitution,
                location,
                typeModel,
                seen,
                expanded);
        }

        private static IReadOnlyList<TypedFunctionSignature> GetImportedTemplateReachableCallSignatures(
            ImportedFunctionTemplateSummary importedTemplate)
        {
            var boundCalls = importedTemplate.BoundOperations
                .Select(static summary => summary.Operation)
                .Select(static operation => operation switch
                {
                    BoundDirectCallOperation directCall => directCall.Signature,
                    BoundMemberCallOperation memberCall => memberCall.Signature,
                    _ => null
                })
                .Where(static signature => signature is not null)
                .Cast<TypedFunctionSignature>()
                .ToArray();
            var functionAddressSignatures = importedTemplate.FunctionAddresses
                .Select(static functionAddress => functionAddress.Signature)
                .ToArray();
            if (boundCalls.Length != 0)
            {
                return boundCalls
                    .Concat(functionAddressSignatures)
                    .ToArray();
            }

            return importedTemplate.DirectCalls.Select(static call => call.Signature)
                .Concat(importedTemplate.MemberCalls.Select(static call => call.Signature))
                .Concat(functionAddressSignatures)
                .ToArray();
        }

        private static void AddTypeTriggersFromImportedTemplateBoundOperations(
            ImportedFunctionTemplateSummary importedTemplate,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded)
        {
            foreach (var summary in importedTemplate.BoundOperations)
            {
                var operation = summary.Operation;
                AddTypeTriggersFromImportedBoundOperationType(
                    operation.ResultType,
                    substitution,
                    location,
                    typeModel,
                    seen,
                    expanded);

                switch (operation)
                {
                    case BoundDirectCallOperation directCall:
                        AddTypeTriggersFromCallSignature(
                            directCall.Signature,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        AddTypeTriggersFromCallArguments(
                            directCall.Arguments,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundMemberCallOperation memberCall:
                        AddTypeTriggersFromImportedBoundOperationType(
                            memberCall.ReceiverType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        AddTypeTriggersFromCallSignature(
                            memberCall.Signature,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        AddTypeTriggersFromCallArguments(
                            memberCall.Arguments,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundFunctionPointerCallOperation functionPointerCall:
                        AddTypeTriggersFromImportedBoundOperationType(
                            functionPointerCall.FunctionPointerType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        AddTypeTriggersFromCallArguments(
                            functionPointerCall.Arguments,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundClosureCallOperation closureCall:
                        AddTypeTriggersFromImportedBoundOperationType(
                            closureCall.ClosureType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        AddTypeTriggersFromCallArguments(
                            closureCall.Arguments,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundIndexAccessOperation indexAccess:
                        AddTypeTriggersFromImportedBoundOperationType(
                            indexAccess.SourceType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundDynamicStorageOperation dynamicStorage:
                        AddTypeTriggersFromImportedBoundOperationType(
                            dynamicStorage.ReceiverType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        if (dynamicStorage.ReceiverType.ElementType is { } elementType)
                        {
                            AddTypeTriggersFromImportedBoundOperationType(
                                elementType,
                                substitution,
                                location,
                                typeModel,
                                seen,
                                expanded);
                        }

                        break;

                    case BoundObjectCreationOperation objectCreation:
                        AddTypeTriggersFromImportedBoundOperationType(
                            objectCreation.CreatedType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        if (objectCreation.Constructor is { } constructor)
                        {
                            foreach (var parameter in constructor.Parameters)
                            {
                                AddTypeTriggersFromImportedBoundOperationType(
                                    parameter.Type,
                                    substitution,
                                    location,
                                    typeModel,
                                    seen,
                                    expanded);
                            }
                        }

                        foreach (var member in objectCreation.Members)
                        {
                            AddTypeTriggersFromImportedBoundOperationType(
                                member.FieldType,
                                substitution,
                                location,
                                typeModel,
                                seen,
                                expanded);
                        }

                        break;

                    case BoundEnumConstructionOperation enumConstruction:
                        AddTypeTriggersFromImportedBoundOperationType(
                            enumConstruction.EnumType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        foreach (var member in enumConstruction.Members)
                        {
                            AddTypeTriggersFromImportedBoundOperationType(
                                member.FieldType,
                                substitution,
                                location,
                                typeModel,
                                seen,
                                expanded);
                        }

                        break;

                    case BoundEnumCallOperation enumCall:
                        AddTypeTriggersFromImportedBoundOperationType(
                            enumCall.EnumType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundEnumValueOperation enumValue:
                        AddTypeTriggersFromImportedBoundOperationType(
                            enumValue.EnumType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundLayoutQueryOperation layoutQuery:
                        AddTypeTriggersFromImportedBoundOperationType(
                            layoutQuery.TargetType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;

                    case BoundSwitchDispatchOperation switchDispatch:
                        AddTypeTriggersFromImportedBoundOperationType(
                            switchDispatch.SwitchType,
                            substitution,
                            location,
                            typeModel,
                            seen,
                            expanded);
                        break;
                }
            }
        }

        private static void AddTypeTriggersFromCallArguments(
            IReadOnlyList<CallArgumentTypingRecord> arguments,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded)
        {
            foreach (var argument in arguments)
            {
                AddTypeTriggersFromImportedBoundOperationType(
                    argument.ParameterType,
                    substitution,
                    location,
                    typeModel,
                    seen,
                    expanded);
                AddTypeTriggersFromImportedBoundOperationType(
                    argument.ArgumentType,
                    substitution,
                    location,
                    typeModel,
                    seen,
                    expanded);
            }
        }

        private static void AddTypeTriggersFromImportedBoundOperationType(
            StarkTypeSymbol type,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded)
        {
            var concreteType = SubstituteTypeForInstantiation(type, substitution, typeModel);
            if (ContainsUnboundGenericParameter(concreteType, typeModel))
            {
                return;
            }

            AddExpandedTypeTriggers(concreteType, location, typeModel, seen, expanded);

            if (concreteType.Kind == StarkTypeKind.FunctionPointer)
            {
                if (concreteType.FunctionPointerReturnType is { } returnType)
                {
                    AddTypeTriggersFromImportedBoundOperationType(
                        returnType,
                        substitution,
                        location,
                        typeModel,
                        seen,
                        expanded);
                }

                foreach (var parameterType in concreteType.FunctionPointerParameterTypes ?? [])
                {
                    AddTypeTriggersFromImportedBoundOperationType(
                        parameterType,
                        substitution,
                        location,
                        typeModel,
                        seen,
                        expanded);
                }

                return;
            }

            if (concreteType.Kind != StarkTypeKind.Closure)
            {
                return;
            }

            if (concreteType.ClosureReturnType is { } closureReturnType)
            {
                AddTypeTriggersFromImportedBoundOperationType(
                    closureReturnType,
                    substitution,
                    location,
                    typeModel,
                    seen,
                    expanded);
            }

            foreach (var parameterType in concreteType.ClosureParameterTypes ?? [])
            {
                AddTypeTriggersFromImportedBoundOperationType(
                    parameterType,
                    substitution,
                    location,
                    typeModel,
                    seen,
                    expanded);
            }
        }

        private static void AddTypeTriggersFromCallSignature(
            TypedFunctionSignature signature,
            IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded)
        {
            foreach (var typeArgument in signature.TypeArguments ?? [])
            {
                var concreteTypeArgument = SubstituteTypeForInstantiation(typeArgument, substitution, typeModel);
                if (ContainsUnboundGenericParameter(concreteTypeArgument, typeModel))
                {
                    continue;
                }

                AddExpandedTypeTriggers(concreteTypeArgument, location, typeModel, seen, expanded);
            }

            var concreteReturnType = SubstituteTypeForInstantiation(signature.ReturnType, substitution, typeModel);
            if (!ContainsUnboundGenericParameter(concreteReturnType, typeModel))
            {
                AddExpandedTypeTriggers(concreteReturnType, location, typeModel, seen, expanded);
            }

            foreach (var parameter in signature.Parameters)
            {
                var concreteParameterType = SubstituteTypeForInstantiation(parameter.Type, substitution, typeModel);
                if (ContainsUnboundGenericParameter(concreteParameterType, typeModel))
                {
                    continue;
                }

                AddExpandedTypeTriggers(concreteParameterType, location, typeModel, seen, expanded);
            }
        }

        private static bool TryAddExpandedTrigger(
            FunctionInstantiationTriggerRecord trigger,
            ISet<string> seen,
            ICollection<FunctionInstantiationTriggerRecord> expanded)
        {
            var templateName = trigger.Signature.TemplateName ?? trigger.FunctionName;
            var key = $"{templateName}|{FunctionOverloadFacts.BuildInstantiationArgumentKey(trigger.TypeArguments, trigger.ComptimeValueArguments)}";
            if (!seen.Add(key))
            {
                return false;
            }

            expanded.Add(trigger);
            return true;
        }

        private static void AddExpandedTypeTriggers(
            StarkTypeSymbol type,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded)
        {
            AddExpandedTypeTriggers(type, location, typeModel, seen, expanded, new HashSet<string>(StringComparer.Ordinal));
        }

        private static void AddExpandedTypeTriggers(
            StarkTypeSymbol type,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded,
            ISet<string> activeNamedTypes)
        {
            var coreType = StarkTypeSymbols.WithQualifiers(
                type,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);

            if (coreType.TypeArguments is { Count: > 0 })
            {
                foreach (var typeArgument in coreType.TypeArguments)
                {
                    AddExpandedTypeTriggers(typeArgument, location, typeModel, seen, expanded, activeNamedTypes);
                }
            }

            if (coreType.ElementType is not null)
            {
                AddExpandedTypeTriggers(coreType.ElementType, location, typeModel, seen, expanded, activeNamedTypes);
            }

            if (coreType.Kind != StarkTypeKind.Named
                || coreType.NamedType is null
                || !TryResolveNamedTypeDefinition(coreType, typeModel, out var namedType, out var typeArguments, out var resolvedTypeName))
            {
                return;
            }

            if (StarkTypeSymbols.IsGenericInstantiation(coreType)
                && !ContainsUnboundGenericParameter(coreType, typeModel))
            {
                var key = $"{StarkTypeSymbols.GetGenericBaseName(coreType.NamedType)}|{FunctionOverloadFacts.BuildInstantiationArgumentKey(coreType.TypeArguments, coreType.ComptimeValueArguments)}";
                if (!seen.Add(key))
                {
                    return;
                }

                expanded.Add(new TypeInstantiationTriggerRecord(
                    coreType.NamedType,
                    (coreType.TypeArguments ?? []).ToArray(),
                    coreType.ComptimeValueArguments?.ToArray(),
                    location));
            }

            AddNestedTypeTriggersFromNamedType(
                namedType,
                typeArguments,
                resolvedTypeName,
                location,
                typeModel,
                seen,
                expanded,
                activeNamedTypes);
        }

        private static bool TryResolveNamedTypeDefinition(
            StarkTypeSymbol coreType,
            TypeCheckModel typeModel,
            out NamedTypeSymbol namedType,
            out IReadOnlyList<StarkTypeSymbol> typeArguments,
            out string instantiatedTypeName)
        {
            namedType = null!;
            typeArguments = [];
            instantiatedTypeName = coreType.NamedType ?? string.Empty;

            if (coreType.NamedType is null)
            {
                return false;
            }

            if (typeModel.NamedTypes.TryGetValue(coreType.NamedType, out var directNamedType))
            {
                namedType = directNamedType;
                if (coreType.TypeArguments is { Count: > 0 })
                {
                    typeArguments = coreType.TypeArguments;
                }

                return true;
            }

            if (coreType.TypeArguments is not { Count: > 0 })
            {
                return false;
            }

            var baseName = StarkTypeSymbols.GetGenericBaseName(coreType.NamedType);
            if (!typeModel.NamedTypes.TryGetValue(baseName, out var genericNamedType))
            {
                return false;
            }

            namedType = genericNamedType;
            typeArguments = coreType.TypeArguments;
            return true;
        }

        private static void AddNestedTypeTriggersFromNamedType(
            NamedTypeSymbol namedType,
            IReadOnlyList<StarkTypeSymbol> typeArguments,
            string instantiatedTypeName,
            SourceLocation location,
            TypeCheckModel typeModel,
            ISet<string> seen,
            ICollection<TypeInstantiationTriggerRecord> expanded,
            ISet<string> activeNamedTypes)
        {
            if (!activeNamedTypes.Add(instantiatedTypeName))
            {
                return;
            }

            try
            {
                IReadOnlyDictionary<string, StarkTypeSymbol>? substitution = null;
                if (namedType.IsGeneric
                    && typeArguments.Count == namedType.GenericParams.Count)
                {
                    substitution = namedType.GenericParams
                        .Zip(typeArguments, static (parameter, argument) => new KeyValuePair<string, StarkTypeSymbol>(parameter, argument))
                        .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
                }

                foreach (var field in namedType.OrderedFields)
                {
                    var nestedFieldType = substitution is null
                        ? field.Type
                        : SubstituteTypeForInstantiation(field.Type, substitution, typeModel);
                    AddExpandedTypeTriggers(nestedFieldType, location, typeModel, seen, expanded, activeNamedTypes);
                }

                foreach (var variant in namedType.Variants)
                {
                    foreach (var field in variant.Fields)
                    {
                        var nestedFieldType = substitution is null
                            ? field.Type
                            : SubstituteTypeForInstantiation(field.Type, substitution, typeModel);
                        AddExpandedTypeTriggers(nestedFieldType, location, typeModel, seen, expanded, activeNamedTypes);
                    }
                }
            }
            finally
            {
                activeNamedTypes.Remove(instantiatedTypeName);
            }
        }

        private static bool ContainsUnboundGenericParameter(StarkTypeSymbol type, TypeCheckModel typeModel)
        {
            var coreType = StarkTypeSymbols.WithQualifiers(
                type,
                borrowKind: StarkBorrowKind.None,
                accessKind: StarkAccessKind.None,
                initializationKind: StarkInitializationKind.None,
                isMutableView: false);

            if (coreType.Kind == StarkTypeKind.Named
                && coreType.NamedType is not null
                && coreType.TypeArguments is not { Count: > 0 }
                && !coreType.NamedType.Contains('.', StringComparison.Ordinal)
                && !typeModel.NamedTypes.ContainsKey(coreType.NamedType)
                && !typeModel.TypeAliases.ContainsKey(coreType.NamedType))
            {
                return true;
            }

            if (coreType.TypeArguments is { Count: > 0 }
                && coreType.TypeArguments.Any(typeArgument => ContainsUnboundGenericParameter(typeArgument, typeModel)))
            {
                return true;
            }

            if (coreType.ComptimeValueArguments is { Count: > 0 }
                && coreType.ComptimeValueArguments.Any(static value => value.IsSymbolic))
            {
                return true;
            }

            if (coreType.Kind == StarkTypeKind.AssociatedType)
            {
                return true;
            }

            return coreType.ElementType is not null
                && ContainsUnboundGenericParameter(coreType.ElementType, typeModel);
        }

        private static string ResolveDeclaringModuleName(
            string symbolName,
            string rootModuleName,
            IReadOnlyList<string> moduleNames)
        {
            var baseName = StripGenericSuffix(symbolName);
            foreach (var moduleName in moduleNames)
            {
                if (string.Equals(baseName, moduleName, StringComparison.Ordinal)
                    || baseName.StartsWith($"{moduleName}.", StringComparison.Ordinal))
                {
                    return moduleName;
                }
            }

            return rootModuleName;
        }

        private static string ResolveOwnerModuleName(
            string declaringModuleName,
            LoadedModuleSet loadedModules)
        {
            return IsSourceBackedModule(declaringModuleName, loadedModules)
                ? declaringModuleName
                : loadedModules.RootModuleName;
        }

        private static bool IsSourceBackedModule(
            string moduleName,
            LoadedModuleSet loadedModules)
        {
            if (!loadedModules.TryGet(moduleName, out var module) || module is null)
            {
                return string.Equals(moduleName, loadedModules.RootModuleName, StringComparison.Ordinal);
            }

            return !module.Reference.IsExternal
                && module.Reference.ManifestPath is null
                && module.Reference.LibraryPath is null;
        }

        private static string StripGenericSuffix(string symbolName)
        {
            var angleIndex = symbolName.IndexOf('<');
            return angleIndex >= 0 ? symbolName[..angleIndex] : symbolName;
        }

    }

    private sealed class MonomorphizationPlanPass : ICompilerPass
    {
        private sealed record FunctionTemplatePlanInfo(
            bool HasBody,
            bool IsHot,
            bool IsCold,
            InlinePreference InlinePreference,
            int? TopLevelStatementCount,
            int? EstimatedBodyCost,
            FunctionOptimizationSummary? OptimizationSummary,
            ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default);

        public string Id => "monomorphization-plan";

        public CompilerPhase Phase => CompilerPhase.Typing;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["instantiation-ownership", "load-modules"];

        public void Execute(CompilerPassContext context)
        {
            var ownership = context.Artifacts.GetRequired(CompilerArtifactKeys.InstantiationOwnership);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            context.Artifacts.Set(
                CompilerArtifactKeys.MonomorphizationPlan,
                BuildMonomorphizationPlan(ownership, loadedModules, typeModel));
        }

        private static MonomorphizationPlanModel BuildMonomorphizationPlan(
            InstantiationOwnershipModel ownership,
            LoadedModuleSet loadedModules,
            TypeCheckModel typeModel)
        {
            var functionInfos = CollectFunctionTemplatePlanInfos(loadedModules);
            var publishedConcreteLayouts = BuildPublishedConcreteLayouts(loadedModules);
            var enumLayouts = BuildPlanningEnumLayouts(loadedModules, typeModel);
            var functions = ownership.Functions
                .Select(function =>
                {
                    functionInfos.TryGetValue(function.TemplateName, out var info);
                    var hasIndirectByValueAggregateAbiCost =
                        !function.IsDeclaringModuleSourceBacked
                        && HasIndirectByValueAggregateAbiCost(
                            function,
                            typeModel,
                            publishedConcreteLayouts,
                            enumLayouts);
                    return new MonomorphizedFunctionPlan(
                        function.TemplateName,
                        function.TypeArguments.ToArray(),
                        function.ComptimeValueArguments?.ToArray(),
                        function.DeclaringModuleName,
                        function.OwnerModuleName,
                        function.IsDeclaringModuleSourceBacked,
                        DetermineCodeSizeHeuristic(info, hasIndirectByValueAggregateAbiCost),
                        info?.TopLevelStatementCount,
                        info?.EstimatedBodyCost,
                        DetermineLinkageKind(function, ownership.RootModuleName),
                        GlobalSymbolNaming.ComputeMonomorphizedFunctionSymbolName(
                            function.OwnerModuleName,
                            function.TemplateName,
                            function.TypeArguments,
                            function.ComptimeValueArguments),
                        function.FirstUseLocation);
                })
                .OrderBy(static function => function.SymbolName, StringComparer.Ordinal)
                .ToArray();

            var types = ownership.Types
                .Select(type => new MonomorphizedTypePlan(
                    type.TemplateName,
                    type.InstantiatedTypeName,
                    type.TypeArguments.ToArray(),
                    type.ComptimeValueArguments?.ToArray(),
                    type.DeclaringModuleName,
                    type.OwnerModuleName,
                    type.IsDeclaringModuleSourceBacked,
                    DetermineLinkageKind(type, ownership.RootModuleName),
                    GlobalSymbolNaming.ComputeMonomorphizedTypeSymbolName(
                        type.OwnerModuleName,
                        type.TemplateName,
                        type.TypeArguments,
                        type.ComptimeValueArguments),
                    type.FirstUseLocation))
                .OrderBy(static type => type.SymbolName, StringComparer.Ordinal)
                .ToArray();

            return new MonomorphizationPlanModel(
                ownership.RootModuleName,
                functions,
                types);
        }

        private static Dictionary<string, ConcreteTypeLayout> BuildPublishedConcreteLayouts(
            LoadedModuleSet loadedModules)
        {
            var layouts = new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules)
            {
                if (module.PackageImageFacts is not { ConcreteLayouts.Count: > 0 } packageImageFacts)
                {
                    continue;
                }

                foreach (var (qualifiedTypeName, layout) in packageImageFacts.ConcreteLayouts)
                {
                    layouts.TryAdd(qualifiedTypeName, layout);
                }
            }

            return layouts;
        }

        private static Dictionary<string, EnumLayoutSymbol> BuildPlanningEnumLayouts(
            LoadedModuleSet loadedModules,
            TypeCheckModel typeModel)
        {
            var layouts = new Dictionary<string, EnumLayoutSymbol>(
                EnumLayoutBuilder.Build(typeModel).Layouts,
                StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules)
            {
                if (module.PackageImageFacts is not { EnumLayouts.Count: > 0 } packageImageFacts)
                {
                    continue;
                }

                foreach (var (qualifiedTypeName, layout) in packageImageFacts.EnumLayouts)
                {
                    layouts.TryAdd(qualifiedTypeName, layout);
                }
            }

            return layouts;
        }

        private static Dictionary<string, FunctionTemplatePlanInfo> CollectFunctionTemplatePlanInfos(
            LoadedModuleSet loadedModules)
        {
            var infos = new Dictionary<string, FunctionTemplatePlanInfo>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules)
            {
                if (module.PackageImageFacts is not { FunctionTemplates.Count: > 0 } packageImageFacts)
                {
                    continue;
                }

                foreach (var (resolvedName, template) in packageImageFacts.FunctionTemplates)
                {
                    packageImageFacts.FunctionEffects.TryGetValue(resolvedName, out var effects);
                    infos[resolvedName] = new FunctionTemplatePlanInfo(
                        HasBody: true,
                        IsHot: effects?.IsHot ?? false,
                        IsCold: effects?.IsCold ?? false,
                        InlinePreference: effects?.InlinePreference ?? InlinePreference.InlineHint,
                        TopLevelStatementCount: template.TopLevelStatementCount,
                        EstimatedBodyCost: template.EstimatedBodyCost,
                        OptimizationSummary: template.OptimizationSummary,
                        BackendOptimizationMode: effects?.BackendOptimizationMode ?? template.BackendOptimizationMode);
                }
            }

            foreach (var module in loadedModules.Modules.Values)
            {
                var importedTemplateInfos = !module.Reference.IsRoot
                    ? module.PackageImageFacts?.FunctionTemplates
                    : null;

                foreach (var functionSyntax in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
                {
                    var overloadKey = FunctionOverloadFacts.BuildOverloadKey(functionSyntax.ParameterList);
                    if (!FunctionOverloadFacts.TryFindFunctionDeclaration(
                            module.SyntaxModel,
                            functionSyntax.DisplaySourceName,
                            overloadKey,
                            out var declaration)
                        || declaration.Function is null)
                    {
                        continue;
                    }

                    var resolvedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(
                            module.SyntaxModel,
                            functionSyntax.DisplaySourceName,
                            overloadKey));
                    if (importedTemplateInfos?.ContainsKey(resolvedName) == true)
                    {
                        // Imported generic planning should trust the published package-image
                        // summary for complexity/optimization shape, but still preserve
                        // source-visible modifiers from the typed interface bridge.
                        if (infos.TryGetValue(resolvedName, out var importedExisting))
                        {
                            infos[resolvedName] = importedExisting with
                            {
                                HasBody = declaration.Function.HasBody || importedExisting.HasBody,
                                IsHot = declaration.Function.Modifiers.IsHot,
                                IsCold = declaration.Function.Modifiers.IsCold,
                                InlinePreference = declaration.Function.Modifiers.InlinePreference,
                                BackendOptimizationMode = declaration.Function.BackendOptimizationMode
                            };
                        }

                        continue;
                    }

                    infos.TryAdd(
                        resolvedName,
                        new FunctionTemplatePlanInfo(
                            declaration.Function.HasBody,
                            declaration.Function.Modifiers.IsHot,
                            declaration.Function.Modifiers.IsCold,
                            declaration.Function.Modifiers.InlinePreference,
                            functionSyntax.Body.block()?.statement().Length,
                            GenericTemplateBodyComplexityEstimator.Estimate(functionSyntax.Body),
                            FunctionOptimizationSummaryBuilder.Build(functionSyntax.Body),
                            declaration.Function.BackendOptimizationMode));

                    if (infos.TryGetValue(resolvedName, out var existing))
                    {
                        var syntaxTopLevelStatementCount = functionSyntax.Body.block()?.statement().Length;
                        var syntaxEstimatedBodyComplexity = functionSyntax.HasBody
                            ? GenericTemplateBodyComplexityEstimator.Estimate(functionSyntax.Body)
                            : null;
                        var syntaxOptimizationSummary = functionSyntax.HasBody
                            ? FunctionOptimizationSummaryBuilder.Build(functionSyntax.Body)
                            : null;

                        infos[resolvedName] = new FunctionTemplatePlanInfo(
                            HasBody: declaration.Function.HasBody || existing.HasBody,
                            IsHot: declaration.Function.Modifiers.IsHot,
                            IsCold: declaration.Function.Modifiers.IsCold,
                            InlinePreference: declaration.Function.Modifiers.InlinePreference,
                            TopLevelStatementCount: syntaxTopLevelStatementCount ?? existing.TopLevelStatementCount,
                            EstimatedBodyCost: syntaxEstimatedBodyComplexity ?? existing.EstimatedBodyCost,
                            OptimizationSummary: syntaxOptimizationSummary ?? existing.OptimizationSummary,
                            BackendOptimizationMode: declaration.Function.BackendOptimizationMode);
                    }
                }
            }

            return infos;
        }

        private static MonomorphizationCodeSizeHeuristic DetermineCodeSizeHeuristic(
            FunctionTemplatePlanInfo? info,
            bool hasIndirectByValueAggregateAbiCost)
        {
            if (info is null || !info.HasBody)
            {
                return MonomorphizationCodeSizeHeuristic.DeclarationOnly;
            }

            if (info.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
                || info.IsCold
                || info.InlinePreference == InlinePreference.NoInline)
            {
                return MonomorphizationCodeSizeHeuristic.ReduceCodeSize;
            }

            if (hasIndirectByValueAggregateAbiCost
                && info.InlinePreference != InlinePreference.Inline
                && !info.IsHot)
            {
                return MonomorphizationCodeSizeHeuristic.ReduceCodeSize;
            }

            if (info.OptimizationSummary?.IsInlineWrapperLike == true)
            {
                return MonomorphizationCodeSizeHeuristic.InlineSmallBody;
            }

            if (info.InlinePreference == InlinePreference.Inline
                || (info.EstimatedBodyCost is { } estimatedBodyComplexity
                    && estimatedBodyComplexity <= (info.IsHot ? 4 : 2))
                || (info.EstimatedBodyCost is null
                    && info.TopLevelStatementCount is { } topLevelStatementCount
                    && topLevelStatementCount <= (info.IsHot ? 4 : 2)))
            {
                return MonomorphizationCodeSizeHeuristic.InlineSmallBody;
            }

            return MonomorphizationCodeSizeHeuristic.SpecializeDefault;
        }

        private static bool HasIndirectByValueAggregateAbiCost(
            FunctionInstantiationOwnership function,
            TypeCheckModel typeModel,
            IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            if (!typeModel.Functions.TryGetValue(function.TemplateName, out var templateSignature))
            {
                return false;
            }

            var instantiatedSignature = InstantiateSignatureForInstantiation(
                templateSignature,
                function.TypeArguments,
                function.TemplateName,
                typeModel,
                function.ComptimeValueArguments);

            if (RequiresIndirectAggregateReturnAbi(
                    instantiatedSignature.ReturnType,
                    typeModel.NamedTypes,
                    publishedConcreteLayouts,
                    enumLayouts))
            {
                return true;
            }

            return instantiatedSignature.Parameters.Any(parameter =>
                parameter.Type.BorrowKind == StarkBorrowKind.None
                && parameter.Type.InitializationKind == StarkInitializationKind.None
                && RequiresIndirectAggregateParameterAbi(
                    parameter.Type,
                    typeModel.NamedTypes,
                    publishedConcreteLayouts,
                    enumLayouts));
        }

        private static bool RequiresIndirectAggregateReturnAbi(
            StarkTypeSymbol type,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            return TryGetConcreteTypeLayout(
                       type,
                       namedTypes,
                       publishedConcreteLayouts,
                       enumLayouts) is { SizeBytes: > 16 };
        }

        private static bool RequiresIndirectAggregateParameterAbi(
            StarkTypeSymbol type,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            return RequiresIndirectAggregateReturnAbi(
                type,
                namedTypes,
                publishedConcreteLayouts,
                enumLayouts);
        }

        private static ConcreteTypeLayout? TryGetConcreteTypeLayout(
            StarkTypeSymbol type,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
            IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts,
            IReadOnlyDictionary<string, EnumLayoutSymbol> enumLayouts)
        {
            var concreteType = type with
            {
                BorrowKind = StarkBorrowKind.None,
                AccessKind = StarkAccessKind.None,
                InitializationKind = StarkInitializationKind.None,
                IsMutableView = false
            };

            if (concreteType.Kind == StarkTypeKind.Named
                && concreteType.NamedType is not null
                && concreteType.TypeArguments is not { Count: > 0 }
                && publishedConcreteLayouts.TryGetValue(concreteType.NamedType, out var publishedLayout))
            {
                return publishedLayout;
            }

            return ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
                concreteType,
                namedTypes,
                enumLayouts,
                publishedConcreteLayouts);
        }

        private static MonomorphizationLinkageKind DetermineLinkageKind(
            FunctionInstantiationOwnership function,
            string rootModuleName)
        {
            return function.IsDeclaringModuleSourceBacked
                   && string.Equals(function.DeclaringModuleName, function.OwnerModuleName, StringComparison.Ordinal)
                   && !string.Equals(function.OwnerModuleName, rootModuleName, StringComparison.Ordinal)
                ? MonomorphizationLinkageKind.LinkOnceOdrComdat
                : MonomorphizationLinkageKind.InternalSingleOwner;
        }

        private static MonomorphizationLinkageKind DetermineLinkageKind(
            TypeInstantiationOwnership type,
            string rootModuleName)
        {
            return type.IsDeclaringModuleSourceBacked
                   && string.Equals(type.DeclaringModuleName, type.OwnerModuleName, StringComparison.Ordinal)
                   && !string.Equals(type.OwnerModuleName, rootModuleName, StringComparison.Ordinal)
                ? MonomorphizationLinkageKind.LinkOnceOdrComdat
                : MonomorphizationLinkageKind.InternalSingleOwner;
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
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);

            var validationModel = new SemanticValidator(context, parseResult, syntaxModel, moduleGraph, loadedModules, effectModel, typeModel, enumLayoutModel).Validate();
            context.Artifacts.Set(CompilerArtifactKeys.SemanticValidation, validationModel);
        }
    }

    private sealed class SpecializationPlanPass : ICompilerPass
    {
        public string Id => "specialization-plan";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["monomorphization-plan", "refine-function-effects", "load-modules"];

        public void Execute(CompilerPassContext context)
        {
            var monomorphization = context.Artifacts.GetRequired(CompilerArtifactKeys.MonomorphizationPlan);
            var closedWorld = context.Artifacts.GetRequired(CompilerArtifactKeys.ClosedWorldOptimization);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var plan = BuildSpecializationPlan(monomorphization, closedWorld, loadedModules);
            ValidatePlan(context, plan);
            context.Artifacts.Set(
                CompilerArtifactKeys.SpecializationPlan,
                plan);
        }

        private static SpecializationPlanModel BuildSpecializationPlan(
            MonomorphizationPlanModel monomorphization,
            ClosedWorldOptimizationModel closedWorld,
            LoadedModuleSet loadedModules)
        {
            var importedAbiFallbacks = CollectImportedAbiFallbacks(loadedModules);
            var functions = monomorphization.Functions
                .Select(function => BuildFunctionPlan(function, monomorphization.RootModuleName, closedWorld, importedAbiFallbacks))
                .OrderBy(static function => function.SymbolName, StringComparer.Ordinal)
                .ToArray();

            return new SpecializationPlanModel(
                monomorphization.RootModuleName,
                functions);
        }

        private static HashSet<string> CollectImportedAbiFallbacks(LoadedModuleSet loadedModules)
        {
            var fallbacks = new HashSet<string>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules)
            {
                if (module.PackageImageFacts is not { AbiFunctions.Count: > 0 } packageImageFacts)
                {
                    continue;
                }

                foreach (var qualifiedResolvedName in packageImageFacts.AbiFunctions.Keys)
                {
                    fallbacks.Add(qualifiedResolvedName);
                }
            }

            return fallbacks;
        }

        private static FunctionSpecializationPlan BuildFunctionPlan(
            MonomorphizedFunctionPlan function,
            string rootModuleName,
            ClosedWorldOptimizationModel closedWorld,
            IReadOnlySet<string> importedAbiFallbacks)
        {
            var selectionOrder = new List<FunctionSpecializationStrategy>();

            if (CanUseLawCallerSpecializedClone(function, rootModuleName, closedWorld))
            {
                selectionOrder.Add(FunctionSpecializationStrategy.LawCallerSpecializedClone);
            }

            if (function.CodeSizeHeuristic != MonomorphizationCodeSizeHeuristic.DeclarationOnly
                && (function.CodeSizeHeuristic != MonomorphizationCodeSizeHeuristic.ReduceCodeSize
                    || ShouldEmitOwnedConcreteBodyForImportedPackageTemplate(function, rootModuleName)))
            {
                selectionOrder.Add(FunctionSpecializationStrategy.OwnedConcreteBody);
            }

            if (ShouldIncludeAbiFallback(function, rootModuleName, importedAbiFallbacks) || selectionOrder.Count == 0)
            {
                selectionOrder.Add(FunctionSpecializationStrategy.DirectAbiBoundaryFallback);
            }

            var codeGenerationMode = selectionOrder[0] switch
            {
                FunctionSpecializationStrategy.LawCallerSpecializedClone => FunctionSpecializationCodeGenerationMode.CallerSpecializedClone,
                FunctionSpecializationStrategy.OwnedConcreteBody => FunctionSpecializationCodeGenerationMode.SingleOwnerConcreteBody,
                _ => FunctionSpecializationCodeGenerationMode.AbiBoundaryOnly
            };

            return new FunctionSpecializationPlan(
                function.TemplateName,
                function.TypeArguments.ToArray(),
                function.ComptimeValueArguments?.ToArray(),
                function.DeclaringModuleName,
                function.OwnerModuleName,
                function.SymbolName,
                selectionOrder.ToArray(),
                codeGenerationMode,
                function.FirstUseLocation);
        }

        private static void ValidatePlan(
            CompilerPassContext context,
            SpecializationPlanModel plan)
        {
            foreach (var collision in plan.Functions
                         .GroupBy(static function => function.SymbolName, StringComparer.Ordinal)
                         .Where(static group => group.Count() > 1))
            {
                var candidates = collision
                    .OrderBy(static function => function.TemplateName, StringComparer.Ordinal)
                    .ThenBy(static function => FormatConcreteArguments(function.TypeArguments, function.ComptimeValueArguments), StringComparer.Ordinal)
                    .ToArray();
                var first = candidates[0];

                for (var index = 1; index < candidates.Length; index++)
                {
                    var conflicting = candidates[index];
                    var message = HaveConflictingPriority(first, conflicting)
                        ? $"Specialization symbol '{collision.Key}' is ambiguous between '{FormatFunctionInstance(first)}' and '{FormatFunctionInstance(conflicting)}' because they require different specialization priority orders ({FormatSelectionOrder(first.SelectionOrder)} vs {FormatSelectionOrder(conflicting.SelectionOrder)})."
                        : $"Specialization symbol '{collision.Key}' is ambiguous between '{FormatFunctionInstance(first)}' and '{FormatFunctionInstance(conflicting)}'. Rename one of the generic templates so the fully spelled internal specialization symbol remains unique.";
                    context.Diagnostics.Error("STK4115", message, "specialization-plan", conflicting.FirstUseLocation);
                    context.Diagnostics.Info(
                        "STK4116",
                        $"The first conflicting specialization candidate '{FormatFunctionInstance(first)}' is planned here.",
                        "specialization-plan",
                        first.FirstUseLocation);
                }
            }
        }

        private static bool CanUseLawCallerSpecializedClone(
            MonomorphizedFunctionPlan function,
            string rootModuleName,
            ClosedWorldOptimizationModel closedWorld)
        {
            if (function.CodeSizeHeuristic is MonomorphizationCodeSizeHeuristic.DeclarationOnly
                or MonomorphizationCodeSizeHeuristic.ReduceCodeSize)
            {
                return false;
            }

            if (string.Equals(function.DeclaringModuleName, rootModuleName, StringComparison.Ordinal))
            {
                return false;
            }

            return closedWorld.Functions.TryGetValue(function.TemplateName, out var optimization)
                && optimization.SelectionOrder.Contains(ClosedWorldCallLoweringStrategy.LawCallerSpecializedClone);
        }

        private static bool ShouldIncludeAbiFallback(
            MonomorphizedFunctionPlan function,
            string rootModuleName,
            IReadOnlySet<string> importedAbiFallbacks)
        {
            return function.CodeSizeHeuristic == MonomorphizationCodeSizeHeuristic.DeclarationOnly
                || (!string.Equals(function.DeclaringModuleName, rootModuleName, StringComparison.Ordinal)
                    && (function.IsDeclaringModuleSourceBacked
                        || importedAbiFallbacks.Contains(function.TemplateName)));
        }

        private static bool ShouldEmitOwnedConcreteBodyForImportedPackageTemplate(
            MonomorphizedFunctionPlan function,
            string rootModuleName)
        {
            return !function.IsDeclaringModuleSourceBacked
                && string.Equals(function.OwnerModuleName, rootModuleName, StringComparison.Ordinal);
        }

        private static bool HaveConflictingPriority(
            FunctionSpecializationPlan left,
            FunctionSpecializationPlan right)
        {
            return !left.SelectionOrder.SequenceEqual(right.SelectionOrder)
                   || left.CodeGenerationMode != right.CodeGenerationMode;
        }

        private static string FormatFunctionInstance(FunctionSpecializationPlan plan)
        {
            return $"{plan.TemplateName}<{FormatConcreteArguments(plan.TypeArguments, plan.ComptimeValueArguments)}>";
        }

        private static string FormatConcreteArguments(
            IReadOnlyList<StarkTypeSymbol> typeArguments,
            IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments)
        {
            return string.Join(
                ", ",
                typeArguments.Select(static argument => argument.DisplayName)
                    .Concat((valueArguments ?? []).Select(static argument => argument.IntegerValue.ToString())));
        }

        private static string FormatSelectionOrder(IReadOnlyList<FunctionSpecializationStrategy> selectionOrder)
        {
            return string.Join(" -> ", selectionOrder);
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

    private sealed class ValidateLoweringContractPass : ICompilerPass
    {
        public string Id => "validate-lowering-contract";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["parse", "load-modules", "type-check", "enum-layout", "semantic-validate", "ownership-validate"];

        public void Execute(CompilerPassContext context)
        {
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
            var validation = new LoweringContractValidator(context, loadedModules, typeModel, enumLayoutModel).Validate();
            context.Artifacts.Set(CompilerArtifactKeys.LoweringContractValidation, validation);
        }
    }

    private sealed class SpecializationCodegenStrategyPass : ICompilerPass
    {
        public string Id => "specialization-codegen-strategy";

        public CompilerPhase Phase => CompilerPhase.Semantics;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["monomorphization-plan", "specialization-plan"];

        public void Execute(CompilerPassContext context)
        {
            var monomorphization = context.Artifacts.GetRequired(CompilerArtifactKeys.MonomorphizationPlan);
            var specialization = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationPlan);
            var strategy = BuildStrategy(monomorphization, specialization);
            context.Artifacts.Set(CompilerArtifactKeys.SpecializationCodegenStrategy, strategy);
            LogStrategy(context, strategy);
        }

        private static SpecializationCodegenStrategyModel BuildStrategy(
            MonomorphizationPlanModel monomorphization,
            SpecializationPlanModel specialization)
        {
            var monomorphizedFunctionsBySymbol = monomorphization.Functions.ToDictionary(
                static function => function.SymbolName,
                StringComparer.Ordinal);
            var functions = specialization.Functions
                .Select(function =>
                {
                    var monomorphized = monomorphizedFunctionsBySymbol[function.SymbolName];
                    return new FunctionSpecializationCodegenStrategy(
                        function.TemplateName,
                        function.TypeArguments.ToArray(),
                        function.ComptimeValueArguments?.ToArray(),
                        function.DeclaringModuleName,
                        function.OwnerModuleName,
                        monomorphized.IsDeclaringModuleSourceBacked,
                        function.SymbolName,
                        monomorphized.Linkage,
                        DetermineStrategyKind(function),
                        function.SelectionOrder.Contains(FunctionSpecializationStrategy.DirectAbiBoundaryFallback),
                        function.FirstUseLocation);
                })
                .OrderBy(static function => function.SymbolName, StringComparer.Ordinal)
                .ToArray();

            return new SpecializationCodegenStrategyModel(
                specialization.RootModuleName,
                functions);
        }

        private static FunctionSpecializationCodegenStrategyKind DetermineStrategyKind(
            FunctionSpecializationPlan function)
        {
            return function.CodeGenerationMode switch
            {
                FunctionSpecializationCodeGenerationMode.AbiBoundaryOnly => FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly,
                FunctionSpecializationCodeGenerationMode.SingleOwnerConcreteBody => FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBody,
                FunctionSpecializationCodeGenerationMode.CallerSpecializedClone => FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBodyAndPreferLawCallerClone,
                _ => throw new InvalidOperationException($"Unsupported specialization code generation mode '{function.CodeGenerationMode}'.")
            };
        }

        private static void LogStrategy(
            CompilerPassContext context,
            SpecializationCodegenStrategyModel strategy)
        {
            foreach (var function in strategy.Functions)
            {
                context.Logs.Info(
                    "decision",
                    "specialization-codegen-strategy",
                    $"Planned code generation strategy '{function.StrategyKind}' for specialization '{function.SymbolName}'.",
                    stage: "specialization-codegen-strategy",
                    symbolName: function.SymbolName,
                    operation: "plan-specialization-codegen",
                    location: function.FirstUseLocation,
                    data: CompilerLogData.Create(
                        ("template", function.TemplateName),
                        ("linkage", function.Linkage.ToString()),
                        ("supportsAbiFallback", function.SupportsAbiFallback.ToString()),
                        ("strategy", function.StrategyKind.ToString())),
                    kind: CompilerLogKind.Decision,
                    outcome: CompilerLogOutcome.Continued,
                    verbosity: CompilerLogVerbosity.Verbose);
            }
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
            var importedSemantics = CollectImportedFunctionSemantics(loadedModules);
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
            var recursiveFunctions = FindRecursiveFunctions(
                validationModel.Functions,
                static _ => true);
            var lawOnlyCallTargets = FindLawOnlyCallTargets(validationModel.Functions);
            var importedDeclarations = CollectImportedFunctionDeclarations(loadedModules);
            var importedCallGraph = CollectImportedDirectCallGraph(loadedModules);
            var importedRecursiveLawFunctions = FindRecursiveFunctions(
                importedCallGraph,
                functionName => IsImportedEffectiveLaw(functionName, importedDeclarations, importedSemantics));
            var importedRecursiveFunctions = FindRecursiveFunctions(
                importedCallGraph,
                importedDeclarations.ContainsKey);
            var opaqueReachableFunctions = FindFunctionsThatReachOpaqueCalls(
                validationModel.Functions,
                importedSemantics);
            var importedLawOnlyCallTargets = FindImportedLawOnlyCallTargets(
                validationModel.Functions,
                importedDeclarations,
                importedCallGraph,
                importedSemantics);

            foreach (var (name, existing) in effectModel.Functions)
            {
                if (!refined.ContainsKey(name))
                {
                    continue;
                }

                validationModel.Functions.TryGetValue(name, out var summary);
                importedSemantics.TryGetValue(name, out var importedSummary);
                var effectiveKind = summary?.EffectiveKind ?? importedSummary?.EffectiveKind ?? existing.Kind;
                var isLaw = FunctionKindFacts.IsLaw(effectiveKind);
                var isFinite = FunctionKindFacts.IsFinite(effectiveKind);
                var readsArgumentMemory = summary?.MemoryEffects?.ReadsArgumentMemory
                    ?? importedSummary?.MemoryEffects?.ReadsArgumentMemory
                    ?? existing.ReadsArgumentMemory;
                var readsOtherMemory = summary?.MemoryEffects?.ReadsOtherMemory
                    ?? importedSummary?.MemoryEffects?.ReadsOtherMemory
                    ?? existing.ReadsOtherMemory;
                var writesOtherMemory = summary?.MemoryEffects?.WritesOtherMemory
                    ?? importedSummary?.MemoryEffects?.WritesOtherMemory
                    ?? existing.WritesOtherMemory;
                var inlinePreference = DetermineInlinePreference(
                    name,
                    summary,
                    importedSummary,
                    existing,
                    rootDeclarations,
                    lawOnlyCallTargets,
                    recursiveLawFunctions,
                    recursiveFunctions,
                    importedDeclarations,
                    importedLawOnlyCallTargets,
                    importedRecursiveLawFunctions,
                    importedRecursiveFunctions);
                var noRecurse = DetermineNoRecurse(
                    name,
                    summary,
                    importedSummary,
                    existing,
                    recursiveFunctions,
                    importedRecursiveFunctions,
                    opaqueReachableFunctions);

                refined[name] = existing with
                {
                    Kind = effectiveKind,
                    ReadsArgumentMemory = readsArgumentMemory,
                    ReadsOtherMemory = readsOtherMemory,
                    WritesOtherMemory = writesOtherMemory,
                    IsPure = isLaw,
                    NoSync = isLaw,
                    NoFree = isLaw,
                    WillReturn = isFinite,
                    MustProgress = isFinite,
                    InlinePreference = inlinePreference,
                    NoRecurse = noRecurse
                };
            }

            foreach (var lambda in typeModel.Lambdas)
            {
                var lambdaEffects = CallableValueFacts.BuildLambdaEffectProfile(lambda);
                if (validationModel.Functions.TryGetValue(lambda.FunctionName, out var lambdaSummary))
                {
                    var effectiveKind = lambdaSummary.EffectiveKind;
                    var isLaw = FunctionKindFacts.IsLaw(effectiveKind);
                    var isFinite = FunctionKindFacts.IsFinite(effectiveKind);
                    lambdaEffects = lambdaEffects with
                    {
                        Kind = effectiveKind,
                        ReadsArgumentMemory = lambdaSummary.MemoryEffects?.ReadsArgumentMemory ?? lambdaEffects.ReadsArgumentMemory,
                        ReadsOtherMemory = lambdaSummary.MemoryEffects?.ReadsOtherMemory ?? lambdaEffects.ReadsOtherMemory,
                        WritesOtherMemory = lambdaSummary.MemoryEffects?.WritesOtherMemory ?? lambdaEffects.WritesOtherMemory,
                        IsPure = isLaw,
                        NoSync = isLaw,
                        NoFree = isLaw,
                        WillReturn = isFinite,
                        MustProgress = isFinite,
                        NoRecurse = DetermineNoRecurse(
                            lambda.FunctionName,
                            lambdaSummary,
                            importedSummary: null,
                            lambdaEffects,
                            recursiveFunctions,
                            importedRecursiveFunctions,
                            opaqueReachableFunctions)
                    };
                }

                refined[lambda.FunctionName] = lambdaEffects;
            }

            foreach (var lambda in typeModel.ClosureLambdas)
            {
                var lambdaEffects = CallableValueFacts.BuildClosureLambdaEffectProfile(lambda);
                if (validationModel.Functions.TryGetValue(lambda.FunctionName, out var lambdaSummary))
                {
                    var effectiveKind = lambdaSummary.EffectiveKind;
                    var isLaw = FunctionKindFacts.IsLaw(effectiveKind);
                    var isFinite = FunctionKindFacts.IsFinite(effectiveKind);
                    lambdaEffects = lambdaEffects with
                    {
                        Kind = effectiveKind,
                        ReadsArgumentMemory = lambdaSummary.MemoryEffects?.ReadsArgumentMemory ?? lambdaEffects.ReadsArgumentMemory,
                        ReadsOtherMemory = lambdaSummary.MemoryEffects?.ReadsOtherMemory ?? lambdaEffects.ReadsOtherMemory,
                        WritesOtherMemory = lambdaSummary.MemoryEffects?.WritesOtherMemory ?? lambdaEffects.WritesOtherMemory,
                        IsPure = isLaw,
                        NoSync = isLaw,
                        NoFree = isLaw,
                        WillReturn = isFinite,
                        MustProgress = isFinite,
                        NoRecurse = DetermineNoRecurse(
                            lambda.FunctionName,
                            lambdaSummary,
                            importedSummary: null,
                            lambdaEffects,
                            recursiveFunctions,
                            importedRecursiveFunctions,
                            opaqueReachableFunctions)
                    };
                }

                refined[lambda.FunctionName] = lambdaEffects;
            }

            foreach (var adapter in typeModel.ClosureFunctionPromotions)
            {
                refined[adapter.AdapterFunctionName] = CallableValueFacts.BuildClosureFunctionAdapterEffectProfile(adapter);
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

        private static bool DetermineNoRecurse(
            string functionName,
            FunctionValidationSummary? summary,
            ImportedFunctionSemanticSummary? importedSummary,
            FunctionEffectProfile existing,
            ISet<string> recursiveFunctions,
            ISet<string> importedRecursiveFunctions,
            ISet<string> opaqueReachableFunctions)
        {
            if (existing.IsFfi || existing.IsVarargs)
            {
                return false;
            }

            if (summary is not null)
            {
                return summary.HasBody
                    && !opaqueReachableFunctions.Contains(functionName)
                    && !recursiveFunctions.Contains(functionName);
            }

            if (importedSummary is not null)
            {
                return existing.NoRecurse
                    && !opaqueReachableFunctions.Contains(functionName)
                    && !importedRecursiveFunctions.Contains(functionName);
            }

            return existing.NoRecurse && !opaqueReachableFunctions.Contains(functionName);
        }

        private static InlinePreference DetermineInlinePreference(
            string functionName,
            FunctionValidationSummary? summary,
            ImportedFunctionSemanticSummary? importedSummary,
            FunctionEffectProfile existing,
            IReadOnlyDictionary<string, TopLevelDeclarationModel> rootDeclarations,
            ISet<string> lawOnlyCallTargets,
            ISet<string> recursiveLawFunctions,
            ISet<string> recursiveFunctions,
            IReadOnlyDictionary<string, ImportedFunctionDeclaration> importedDeclarations,
            ISet<string> importedLawOnlyCallTargets,
            ISet<string> importedRecursiveLawFunctions,
            ISet<string> importedRecursiveFunctions)
        {
            if (existing.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque)
            {
                return InlinePreference.NoInline;
            }

            if (summary is not null
                && rootDeclarations.TryGetValue(functionName, out var wrapperRootDeclaration)
                && wrapperRootDeclaration.Function is { HasBody: true } wrapperRootFunction
                && wrapperRootDeclaration.Visibility != StarkVisibility.Export
                && !wrapperRootFunction.Modifiers.HasExplicitInlinePreference
                && existing.InlinePreference == InlinePreference.InlineHint
                && !existing.IsFfi
                && !existing.IsCold
                && summary.OptimizationSummary?.IsInlineWrapperLike == true
                && !recursiveFunctions.Contains(functionName))
            {
                return InlinePreference.Inline;
            }

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

            if (importedSummary?.OptimizationSummary?.IsInlineWrapperLike == true
                && importedDeclarations.TryGetValue(functionName, out var importedWrapperDeclaration)
                && importedWrapperDeclaration.Declaration.Function is { HasBody: true } importedWrapperFunction
                && importedWrapperDeclaration.Declaration.Visibility != StarkVisibility.Export
                && !importedWrapperFunction.Modifiers.HasExplicitInlinePreference
                && existing.InlinePreference == InlinePreference.InlineHint
                && !existing.IsFfi
                && !existing.IsCold
                && !importedRecursiveFunctions.Contains(functionName))
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
                || !FunctionKindFacts.IsLaw(importedSummary?.EffectiveKind ?? importedFunction.Kind)
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
                if (TryResolveContainingAbstraction(function.Name, typeInfos, out var abstraction))
                {
                    functionInfos[function.Name] = BuildClosedWorldFunctionInfo(
                        function,
                        abstraction.Kind,
                        abstraction.Seal,
                        rootFunctionNames,
                        sourceFunctionDeclarations,
                        importedRecursiveLawFunctions);
                    continue;
                }

                if (!IsTopLevelFunction(function.Name, typeModel.NamedTypes))
                {
                    continue;
                }

                functionInfos[function.Name] = BuildClosedWorldFunctionInfo(
                    function,
                    DeclarationKind.Function,
                    ResolveClosedWorldTopLevelFunctionSeal(function.Name, syntaxModel.ModuleName, sealedModules),
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
            DeclarationKind kind,
            ClosedWorldSealKind seal,
            ISet<string> rootFunctionNames,
            IReadOnlyDictionary<string, TopLevelDeclarationModel> sourceFunctionDeclarations,
            ISet<string> importedRecursiveLawFunctions)
        {
            if (kind == DeclarationKind.Trait)
            {
                return new ClosedWorldFunctionOptimizationInfo(
                    function.Name,
                    kind,
                    seal,
                    [ClosedWorldCallLoweringStrategy.CompileTimeOnlyContract],
                    ClosedWorldCodeGenerationMode.MonomorphizationDeferred,
                    CanDevirtualize: false);
            }

            if (rootFunctionNames.Contains(function.Name) && sourceFunctionDeclarations.TryGetValue(function.Name, out var rootDeclaration))
            {
                return new ClosedWorldFunctionOptimizationInfo(
                    function.Name,
                    kind,
                    seal,
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
                    kind,
                    seal,
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
                kind,
                seal,
                selectionOrder,
                codeGenerationMode,
                CanDevirtualize: true);
        }

        private static bool IsTopLevelFunction(
            string functionName,
            IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
        {
            var separatorIndex = functionName.LastIndexOf('.');
            if (separatorIndex < 0)
            {
                return true;
            }

            var containingName = functionName[..separatorIndex];
            return !namedTypes.ContainsKey(containingName);
        }

        private static ClosedWorldSealKind ResolveClosedWorldTopLevelFunctionSeal(
            string functionName,
            string rootModuleName,
            ISet<string> sealedModules)
        {
            var separatorIndex = functionName.LastIndexOf('.');
            var moduleName = separatorIndex >= 0
                ? functionName[..separatorIndex]
                : rootModuleName;
            return sealedModules.Contains(moduleName)
                ? ClosedWorldSealKind.SealedByDefault
                : ClosedWorldSealKind.AbiBoundary;
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
                && function.BackendOptimizationMode != ModuleBackendOptimizationMode.Opaque
                && sourceFunction.BackendOptimizationMode != ModuleBackendOptimizationMode.Opaque
                && !sourceFunction.Modifiers.IsFfi
                && !sourceFunction.Modifiers.IsCold
                && sourceFunction.Modifiers.InlinePreference != InlinePreference.NoInline
                && function.InlinePreference != InlinePreference.NoInline
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
                var publishedTemplates = module.PackageImageFacts?.FunctionTemplates;

                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
                {
                    var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                    var publishedDeclaration = declaration;
                    var function = declaration.Function!;
                    if (!function.HasBody
                        && publishedTemplates?.ContainsKey(qualifiedName) == true)
                    {
                        function = function with { HasBody = true };
                        publishedDeclaration = declaration with { Function = function };
                    }

                    declarations[qualifiedName] = new ImportedFunctionDeclaration(module.SyntaxModel.ModuleName, publishedDeclaration);
                }
            }

            return declarations;
        }

        private static Dictionary<string, ImportedFunctionSemanticSummary> CollectImportedFunctionSemantics(LoadedModuleSet loadedModules)
        {
            var semantics = new Dictionary<string, ImportedFunctionSemanticSummary>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
            {
                if (module.PackageImageFacts is not { FunctionSemantics.Count: > 0 } packageImageFacts)
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

        private static Dictionary<string, HashSet<string>> CollectImportedDirectCallGraph(LoadedModuleSet loadedModules)
        {
            var callGraph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
            {
                if (module.HasPublishedFunctionSemantics)
                {
                    var packageImageFacts = module.PackageImageFacts!;
                    foreach (var (qualifiedName, summary) in packageImageFacts.FunctionSemantics)
                    {
                        callGraph[qualifiedName] = summary.CalledFunctions.ToHashSet(StringComparer.Ordinal);
                    }

                    foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
                    {
                        var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                            module,
                            FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                        callGraph.TryAdd(qualifiedName, new HashSet<string>(StringComparer.Ordinal));
                    }

                    continue;
                }

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
                    if (callGraph.ContainsKey(qualifiedName))
                    {
                        continue;
                    }

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

        private static HashSet<string> FindFunctionsThatReachOpaqueCalls(
            IReadOnlyDictionary<string, FunctionValidationSummary> validationSummaries,
            IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> importedSemantics)
        {
            var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var directOpaque = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (name, summary) in validationSummaries)
            {
                graph[name] = summary.CalledFunctions;
                if (summary.HasOpaqueCall)
                {
                    directOpaque.Add(name);
                }
            }

            foreach (var (name, summary) in importedSemantics)
            {
                graph[name] = summary.CalledFunctions;
                if (summary.HasOpaqueCall)
                {
                    directOpaque.Add(name);
                }
            }

            var reachesOpaque = new HashSet<string>(StringComparer.Ordinal);
            var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in graph.Keys)
            {
                if (ReachesOpaque(name))
                {
                    reachesOpaque.Add(name);
                }
            }

            return reachesOpaque;

            bool ReachesOpaque(string name)
            {
                if (memo.TryGetValue(name, out var cached))
                {
                    return cached;
                }

                if (directOpaque.Contains(name))
                {
                    memo[name] = true;
                    return true;
                }

                if (!graph.TryGetValue(name, out var callees))
                {
                    memo[name] = true;
                    return true;
                }

                if (!visiting.Add(name))
                {
                    return false;
                }

                foreach (var callee in callees)
                {
                    if (ReachesOpaque(callee))
                    {
                        visiting.Remove(name);
                        memo[name] = true;
                        return true;
                    }
                }

                visiting.Remove(name);
                memo[name] = false;
                return false;
            }
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
            IReadOnlyDictionary<string, HashSet<string>> importedCallGraph,
            IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> importedSemantics)
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

                if (importedSemantics.TryGetValue(caller, out var importedSummary))
                {
                    return FunctionKindFacts.IsLaw(importedSummary.EffectiveKind);
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

        private static bool IsImportedEffectiveLaw(
            string functionName,
            IReadOnlyDictionary<string, ImportedFunctionDeclaration> importedDeclarations,
            IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> importedSemantics)
        {
            if (importedSemantics.TryGetValue(functionName, out var importedSummary))
            {
                return FunctionKindFacts.IsLaw(importedSummary.EffectiveKind);
            }

            return importedDeclarations.TryGetValue(functionName, out var declaration)
                && declaration.Declaration.Function is { } function
                && FunctionKindFacts.IsLaw(function.Kind);
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

        public IReadOnlyList<string> Dependencies => ["syntax-model", "load-modules", "module-graph", "refine-function-effects", "type-check", "semantic-validate", "ownership-validate", "validate-lowering-contract", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var effects = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var types = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var specializationStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var fallbackSignatures = CollectFallbackFunctionSignatures(context, moduleGraph, types.NamedTypes, types.TypeAliases, loadedModules);

            var declaredFunctions = loadedModules.Modules.Values
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
            var lambdaFunctions = types.Lambdas
                .Select(lambda =>
                {
                    var signature = types.Functions.TryGetValue(lambda.FunctionName, out var typedSignature)
                        ? typedSignature
                        : CallableValueFacts.BuildLambdaSignature(lambda);
                    var profile = effects.Functions.TryGetValue(lambda.FunctionName, out var lambdaEffects)
                        ? lambdaEffects
                        : CallableValueFacts.BuildLambdaEffectProfile(lambda);

                    return new HighLevelIrFunction(
                        lambda.FunctionName,
                        signature,
                        HasBody: true,
                        BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg,
                        Effects: profile);
                })
                .ToArray();
            var closureLambdaFunctions = types.ClosureLambdas
                .Select(lambda =>
                {
                    var signature = types.Functions.TryGetValue(lambda.FunctionName, out var typedSignature)
                        ? typedSignature
                        : CallableValueFacts.BuildClosureLambdaSignature(lambda);
                    var profile = effects.Functions.TryGetValue(lambda.FunctionName, out var lambdaEffects)
                        ? lambdaEffects
                        : CallableValueFacts.BuildClosureLambdaEffectProfile(lambda);

                    return new HighLevelIrFunction(
                        lambda.FunctionName,
                        signature,
                        HasBody: true,
                        BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg,
                        Effects: profile);
                })
                .ToArray();
            var closureFunctionAdapterFunctions = types.ClosureFunctionPromotions
                .Select(adapter =>
                {
                    var signature = types.Functions.TryGetValue(adapter.AdapterFunctionName, out var typedSignature)
                        ? typedSignature
                        : CallableValueFacts.BuildClosureFunctionAdapterSignature(adapter);
                    var sourceProfile = effects.Functions.TryGetValue(adapter.Signature.Name, out var resolvedSourceProfile)
                        ? resolvedSourceProfile
                        : CallableValueFacts.BuildClosureFunctionAdapterEffectProfile(adapter);
                    var profile = sourceProfile with
                    {
                        Name = adapter.AdapterFunctionName,
                        UseFastCallingConvention = true,
                        InlinePreference = InlinePreference.Inline
                    };

                    return new HighLevelIrFunction(
                        adapter.AdapterFunctionName,
                        signature,
                        HasBody: true,
                        BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg,
                        Effects: profile);
                })
                .ToArray();
            var closureDropFunctions = BuildClosureDropFunctions(types);
            var dynDropThunks = BuildDynDropThunks(types);
            var declarationsByQualifiedName = CollectFunctionDeclarationsByQualifiedName(loadedModules);
            var specializedFunctions = MaterializeSpecializedFunctions(
                specializationStrategy,
                declarationsByQualifiedName,
                effects,
                types.Functions,
                fallbackSignatures,
                types);
            var functions = declaredFunctions
                .Concat(lambdaFunctions)
                .Concat(closureLambdaFunctions)
                .Concat(closureFunctionAdapterFunctions)
                .Concat(closureDropFunctions)
                .Concat(dynDropThunks)
                .Concat(specializedFunctions)
                .ToArray();

            context.Artifacts.Set(
                CompilerArtifactKeys.HighLevelIr,
                new HighLevelIrModule(
                    loadedModules.RootModuleName,
                    functions,
                    types.AddressTakenFunctions
                        .Select(static function => function.Signature.Name)
                        .Concat(types.Lambdas.Select(static lambda => lambda.FunctionName))
                        .Concat(types.ClosureLambdas.Select(static lambda => lambda.FunctionName))
                        .Concat(types.ClosureFunctionPromotions.Select(static adapter => adapter.AdapterFunctionName))
                        .Concat(closureDropFunctions.Select(static function => function.Name))
                        .Concat(dynDropThunks.Select(static function => function.Name))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static name => name, StringComparer.Ordinal)
                        .ToArray()));
        }

        private static IReadOnlyList<HighLevelIrFunction> BuildClosureDropFunctions(TypeCheckModel types)
        {
            var functions = new List<HighLevelIrFunction>();
            var needsEmptyDrop = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var lambda in types.ClosureLambdas)
            {
                if (lambda.ClosureType.ClosureStorageKind != StarkClosureStorageKind.Heap)
                {
                    continue;
                }

                if (!lambda.HasCaptures)
                {
                    needsEmptyDrop = true;
                    continue;
                }

                var functionName = CallableValueFacts.BuildClosureDropFunctionName(lambda.FunctionName);
                if (!seen.Add(functionName))
                {
                    continue;
                }

                functions.Add(new HighLevelIrFunction(
                    functionName,
                    CallableValueFacts.BuildClosureDropSignature(functionName),
                    HasBody: true,
                    BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg,
                    Effects: CallableValueFacts.BuildClosureDropEffectProfile(functionName)));
            }

            if (needsEmptyDrop)
            {
                var functionName = CallableValueFacts.EmptyClosureDropFunctionName;
                functions.Add(new HighLevelIrFunction(
                    functionName,
                    CallableValueFacts.BuildClosureDropSignature(functionName),
                    HasBody: true,
                    BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg,
                    Effects: CallableValueFacts.BuildClosureDropEffectProfile(functionName)));
            }

            return functions;
        }

        // Synthesizes the per-type drop thunk referenced by each `dyn trait` vtable's
        // Drop slot: `<Type>.__dyn_drop(rawmutptr<i8> self)` drops the boxed value and
        // frees the box. Emitted for every non-generic concrete type that implements a
        // `dyn trait` (the slot is dead for borrowed objects but correct for `heap dyn`);
        // the thunk's signature/effects mirror a heap-closure drop thunk.
        private static IReadOnlyList<HighLevelIrFunction> BuildDynDropThunks(TypeCheckModel types)
        {
            var functions = new List<HighLevelIrFunction>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var namedType in types.NamedTypes.Values)
            {
                if (namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                    || namedType.IsGeneric
                    || namedType.ImplementedTraits.Count == 0)
                {
                    continue;
                }

                var implementsDynTrait = namedType.ImplementedTraits.Any(traitName =>
                    types.NamedTypes.TryGetValue(traitName, out var traitType) && traitType.IsDynTrait);
                if (!implementsDynTrait)
                {
                    continue;
                }

                var functionName = DynTraitFacts.BuildDropThunkName(namedType.Name);
                if (!seen.Add(functionName))
                {
                    continue;
                }

                functions.Add(new HighLevelIrFunction(
                    functionName,
                    CallableValueFacts.BuildClosureDropSignature(functionName),
                    HasBody: true,
                    BodyLoweringKind: FunctionBodyLoweringKind.StarkCfg,
                    Effects: CallableValueFacts.BuildClosureDropEffectProfile(functionName)));
            }

            return functions;
        }

        private static IReadOnlyDictionary<string, FunctionDeclarationModel> CollectFunctionDeclarationsByQualifiedName(
            LoadedModuleSet loadedModules)
        {
            var declarations = new Dictionary<string, FunctionDeclarationModel>(StringComparer.Ordinal);

            foreach (var module in loadedModules.Modules.Values)
            {
                var publishedTemplates = module.PackageImageFacts?.FunctionTemplates;
                foreach (var declaration in module.SyntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
                {
                    var qualifiedName = FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                    var function = declaration.Function!;
                    if (!function.HasBody
                        && publishedTemplates?.ContainsKey(qualifiedName) == true)
                    {
                        function = function with { HasBody = true };
                    }

                    declarations[qualifiedName] = function;
                }
            }

            return declarations;
        }

        private static IReadOnlyList<HighLevelIrFunction> MaterializeSpecializedFunctions(
            SpecializationCodegenStrategyModel specializationStrategy,
            IReadOnlyDictionary<string, FunctionDeclarationModel> declarationsByQualifiedName,
            FunctionEffectModel effects,
            IReadOnlyDictionary<string, TypedFunctionSignature> signatures,
            IReadOnlyDictionary<string, TypedFunctionSignature> fallbackSignatures,
            TypeCheckModel typeModel)
        {
            var functions = new List<HighLevelIrFunction>();

            foreach (var strategy in specializationStrategy.Functions)
            {
                if (!declarationsByQualifiedName.TryGetValue(strategy.TemplateName, out var declaration)
                    || !declaration.HasBody)
                {
                    if (strategy.StrategyKind != FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly
                        || !ShouldMaterializeCompilerBuiltinAbiSpecialization(strategy.TemplateName)
                        || !declarationsByQualifiedName.TryGetValue(strategy.TemplateName, out declaration))
                    {
                        continue;
                    }
                }

                if (!signatures.TryGetValue(strategy.TemplateName, out var templateSignature)
                    && !fallbackSignatures.TryGetValue(strategy.TemplateName, out templateSignature!))
                {
                    continue;
                }

                if (!effects.Functions.TryGetValue(strategy.TemplateName, out var templateEffects))
                {
                    continue;
                }

                var substitution = FunctionOverloadFacts.BuildGenericSubstitution(templateSignature, strategy.TypeArguments);
                var valueSubstitution = FunctionOverloadFacts.BuildComptimeValueSubstitution(templateSignature, strategy.ComptimeValueArguments);
                var specializedSignature = InstantiateSignatureForInstantiation(
                    templateSignature,
                    strategy.TypeArguments,
                    strategy.SymbolName,
                    typeModel,
                    strategy.ComptimeValueArguments);
                functions.Add(new HighLevelIrFunction(
                    strategy.SymbolName,
                    specializedSignature,
                    declaration.HasBody,
                    DetermineBodyLoweringKind(declaration),
                    templateEffects with { Name = strategy.SymbolName },
                    BodyTemplateName: strategy.TemplateName,
                    GenericTypeSubstitution: substitution,
                    GenericValueSubstitution: valueSubstitution));
            }

            return functions
                .OrderBy(static function => function.Name, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool ShouldMaterializeCompilerBuiltinAbiSpecialization(string templateName)
        {
            return templateName is "System.Collections.List.AsSlice"
                or "System.Collections.List.AsMutableSlice"
                or "System.Collections.DictionaryKey.Equals"
                or "System.Collections.DictionaryKey.Hash";
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
            IReadOnlyDictionary<string, TypeAliasSymbol> typeAliases,
            LoadedModuleSet loadedModules)
        {
            var resolver = new StarkTypeResolver(context, "lower-hir", moduleGraph, namedTypes, typeAliases);
            var functions = new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
            {
                if (module.PackageImageFacts is { FunctionSignatures.Count: > 0 } packageImageFacts)
                {
                    foreach (var (qualifiedName, signature) in packageImageFacts.FunctionSignatures)
                    {
                        functions[qualifiedName] = signature;
                    }

                    continue;
                }

                foreach (var declaration in DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel))
                {
                    var qualifiedName = $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                    FunctionOverloadFacts.TryFindFunctionDeclaration(
                        module.SyntaxModel,
                        declaration.DisplaySourceName,
                        FunctionOverloadFacts.BuildOverloadKey(declaration.ParameterList),
                        out var declarationModel);
                    var genericParameterNames = FunctionGenericParameterFacts.GetEffectiveGenericParameterNames(module, declaration);
                    var genericParameters = FunctionGenericParameterFacts.ToGenericParameterSet(genericParameterNames);
                    var parameters = declaration.ParameterList.parameter()
                        .Select(parameter => new TypedParameterSymbol(
                            parameter.Identifier().GetText(),
                            resolver.ResolveParameterType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName, out var rawPointerElementCountExpression),
                            parameter.parameterContractPrefix().Any(static prefix => prefix.Start.Type == StarkParser.DISJOINT),
                            parameter.parameterContractPrefix().Any(static prefix => prefix.Start.Type == StarkParser.CONST),
                            rawPointerElementCountExpression))
                        .ToArray();
                    var isFfi = declaration.Modifiers.Any(FfiAbiSyntaxFacts.IsFfiModifier);
                    var isAsm = declarationModel?.Function?.Asm is not null;
                    var overlapGroups = declarationModel?.Function?.OverlapGroups ?? [];
                    var sameGroups = declarationModel?.Function?.SameGroups ?? [];
                    var disjointGroups = ParameterMemoryContractFacts.BuildEffectiveDisjointGroups(
                        parameters,
                        declarationModel?.Function?.DisjointGroups ?? [],
                        overlapGroups,
                        sameGroups,
                        applyDefaultNonOverlap: !isFfi && !isAsm);
                    functions[qualifiedName] = new TypedFunctionSignature(
                        qualifiedName,
                        resolver.ResolveReturnType(declaration.ReturnType, genericParameters, module.SyntaxModel.ModuleName),
                        parameters,
                        SourceName: FunctionOverloadFacts.QualifySourceName(module, declaration.DisplaySourceName),
                        GenericParameterNames: genericParameterNames.Count == 0 ? null : genericParameterNames.ToArray(),
                        IsStatic: declaration.IsStatic,
                        IsUnsafe: declaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "unsafe", StringComparison.Ordinal)),
                        IsVarargs: declaration.Modifiers.Any(static modifier => string.Equals(modifier.GetText(), "varargs", StringComparison.Ordinal)),
                        FfiAbi: declarationModel?.Function?.Modifiers.FfiAbi,
                        DisjointParameterGroups: disjointGroups,
                        OverlapParameterGroups: overlapGroups,
                        SameParameterGroups: sameGroups,
                        PointeeDeadOnReturnParameterNames: declarationModel?.Function?.PointeeDeadOnReturnParameters);
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

        public IReadOnlyList<string> Dependencies => ["load-modules", "module-graph", "type-check", "enum-layout", "lower-hir", "ownership-validate"];

        public void Execute(CompilerPassContext context)
        {
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
            var hir = context.Artifacts.GetRequired(CompilerArtifactKeys.HighLevelIr);
            var ownership = context.Artifacts.GetRequired(CompilerArtifactKeys.OwnershipValidation);
            var mir = new MidLevelIrLowerer(context, loadedModules, moduleGraph, typeModel, enumLayoutModel, ownership).Lower(hir);
            context.Artifacts.Set(CompilerArtifactKeys.MidLevelIr, mir);
            EmitFallbackLogDiagnostics(context, "STK5000", LoweringFallbackEventIds);
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
            context.Artifacts.Set(CompilerArtifactKeys.MidLevelIr, AttachOwnershipSummaries(mir, refinedOwnership));
        }

        private static MidLevelIrModule AttachOwnershipSummaries(
            MidLevelIrModule mir,
            OwnershipValidationModel ownership)
        {
            var changed = false;
            var functions = mir.Functions
                .Select(function =>
                {
                    var next = ownership.Functions.TryGetValue(function.Name, out var summary)
                        ? function with { Ownership = summary }
                        : function;
                    changed |= !ReferenceEquals(next, function);
                    return next;
                })
                .ToArray();

            return changed
                ? mir with { Functions = functions }
                : mir;
        }
    }

    private sealed class EmitLlvmIrPass : ICompilerPass
    {
        public string Id => "emit-llvm";

        public CompilerPhase Phase => CompilerPhase.CodeGeneration;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "refine-function-effects", "type-check", "enum-layout", "semantic-validate", "memory-opt-ssa", "lower-abi", "validate-ssa", "specialization-codegen-strategy"];

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
            var specializationCodegenStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var abiModel = context.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            SsaValueFactModel? ssaValueFacts = null;
            if (context.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts))
            {
                ssaValueFacts = facts;
            }

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
                internalizeModulePrivate: context.Options.InternalizeModulePrivate || context.Options.QualifyModuleSymbols,
                isOptimizedBuild: true,
                enableOptimizedRawPointerLoopIntrinsics: true,
                semanticValidation: validationModel,
                closedWorldModel: closedWorldModel,
                specializationCodegenStrategy: specializationCodegenStrategy,
                logs: context.Logs,
                ssaValueFacts: ssaValueFacts,
                importedInlineCloneSeedFunctions: context.Options.ImportedInlineCloneSeedFunctions,
                emitFallbackDeclarationsForSourceBodies: false).Emit();
            context.Artifacts.Set(CompilerArtifactKeys.LlvmIrModule, llvmModule);
            EmitFallbackLogDiagnostics(context, "STK5001", BackendFallbackEventIds);
        }
    }

    private sealed class LowerToSsaIrPass : ICompilerPass
    {
        public string Id => "lower-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["type-check", "lower-mir", "borrow-liveness", "load-modules", "function-effects", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var mir = context.Artifacts.GetRequired(CompilerArtifactKeys.MidLevelIr);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var lowerer = new SsaLowerer(typeModel);
            var ssa = context.Options.PruneUnusedLoweredFunctions
                ? SsaEmissionReachability.LowerReachableFromEmission(
                    lowerer,
                    mir,
                    context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules),
                    typeModel,
                    context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects),
                    context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy))
                : lowerer.Lower(mir);
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
            var optimized = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(ssa);
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

    private sealed class InlineSsaIrPass : ICompilerPass
    {
        public string Id => "inline-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["devirt-ssa", "refine-function-effects", "syntax-model", "declaration-index", "type-check", "monomorphization-plan", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var monomorphization = context.Artifacts.GetRequired(CompilerArtifactKeys.MonomorphizationPlan);
            var specializationCodegenStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var declarationIndex = context.Artifacts.GetRequired(CompilerArtifactKeys.DeclarationIndex);
            var modulePrivateFunctionNames = declarationIndex.OrderedDeclarations
                .Where(static declaration => declaration.Function is not null
                                             && declaration.Visibility == StarkVisibility.Module)
                .Select(declaration => FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var functionName in GetInlineableMonomorphizedFunctionNames(monomorphization, specializationCodegenStrategy))
            {
                modulePrivateFunctionNames.Add(functionName);
            }

            foreach (var functionName in typeModel.ClosureLambdas
                         .Where(static lambda => lambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Inline)
                         .Select(static lambda => lambda.FunctionName))
            {
                modulePrivateFunctionNames.Add(functionName);
            }

            var declaredLawFunctionNames = declarationIndex.OrderedDeclarations
                .Where(static declaration => declaration.Function is not null
                                             && FunctionKindFacts.IsLaw(declaration.Function.Kind))
                .Select(declaration => FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration))
                .ToHashSet(StringComparer.Ordinal);
            var inlinerEffectModel = BuildInlinerEffectModel(effectModel, specializationCodegenStrategy);
            var inliner = new SsaDirectCallInliner(
                inlinerEffectModel,
                modulePrivateFunctionNames,
                declaredLawFunctionNames);
            var cleanup = new SsaCleanupOptimizer(enableSelectPredication: false);
            var constants = new SsaConstantPropagator();

            var optimized = ssa;
            for (var round = 0; round < 3; round++)
            {
                optimized = inliner.Optimize(optimized);
                optimized = cleanup.Optimize(optimized);
                optimized = constants.Optimize(optimized);
                optimized = new SsaDirectCallDevirtualizer(typeModel).Optimize(optimized);
            }

            optimized = cleanup.Optimize(optimized);
            optimized = constants.Optimize(optimized);
            optimized = PruneUnreferencedInlineClosureLambdas(optimized, typeModel);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, optimized);
        }

        private static SsaIrModule PruneUnreferencedInlineClosureLambdas(
            SsaIrModule module,
            TypeCheckModel typeModel)
        {
            var inlineClosureLambdaNames = typeModel.ClosureLambdas
                .Where(static lambda => lambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Inline)
                .Select(static lambda => lambda.FunctionName)
                .ToHashSet(StringComparer.Ordinal);
            var prunableSyntheticNames = inlineClosureLambdaNames
                .Concat(typeModel.ClosureFunctionPromotions.Select(static adapter => adapter.AdapterFunctionName))
                .ToHashSet(StringComparer.Ordinal);
            if (prunableSyntheticNames.Count == 0)
            {
                return module;
            }

            var referencedFunctions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var function in module.Functions)
            {
                if (!prunableSyntheticNames.Contains(function.Name))
                {
                    SsaFunctionReferenceWalker.CollectReferencedFunctions(function, referencedFunctions);
                }
            }

            var functionsByName = module.Functions.ToDictionary(static function => function.Name, StringComparer.Ordinal);
            var pending = new Stack<string>(referencedFunctions.Where(prunableSyntheticNames.Contains));
            while (pending.Count != 0)
            {
                var functionName = pending.Pop();
                if (!functionsByName.TryGetValue(functionName, out var function))
                {
                    continue;
                }

                var nestedReferences = new HashSet<string>(StringComparer.Ordinal);
                SsaFunctionReferenceWalker.CollectReferencedFunctions(function, nestedReferences);
                foreach (var nestedReference in nestedReferences)
                {
                    if (prunableSyntheticNames.Contains(nestedReference)
                        && referencedFunctions.Add(nestedReference))
                    {
                        pending.Push(nestedReference);
                    }
                }
            }

            var prunedFunctions = module.Functions
                .Where(function => !prunableSyntheticNames.Contains(function.Name)
                                   || referencedFunctions.Contains(function.Name))
                .ToArray();
            if (prunedFunctions.Length == module.Functions.Count)
            {
                return module;
            }

            var prunedAddressTakenFunctions = module.AddressTakenFunctions
                .Where(functionName => !prunableSyntheticNames.Contains(functionName)
                                       || referencedFunctions.Contains(functionName))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static functionName => functionName, StringComparer.Ordinal)
                .ToArray();
            return new SsaIrModule(
                module.ModuleName,
                prunedFunctions,
                prunedAddressTakenFunctions);
        }

        private static FunctionEffectModel BuildInlinerEffectModel(
            FunctionEffectModel effectModel,
            SpecializationCodegenStrategyModel specializationCodegenStrategy)
        {
            var functions = effectModel.Functions.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);

            foreach (var strategy in specializationCodegenStrategy.Functions)
            {
                if (!functions.TryGetValue(strategy.TemplateName, out var templateEffects))
                {
                    continue;
                }

                functions.TryAdd(
                    strategy.SymbolName,
                    templateEffects with { Name = strategy.SymbolName });
            }

            return new FunctionEffectModel(effectModel.ModuleName, functions);
        }

        private static IEnumerable<string> GetInlineableMonomorphizedFunctionNames(
            MonomorphizationPlanModel monomorphization,
            SpecializationCodegenStrategyModel specializationCodegenStrategy)
        {
            var monomorphizedBySymbol = monomorphization.Functions.ToDictionary(
                static function => function.SymbolName,
                StringComparer.Ordinal);

            foreach (var strategy in specializationCodegenStrategy.Functions)
            {
                if (strategy.StrategyKind == FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly
                    || !monomorphizedBySymbol.TryGetValue(strategy.SymbolName, out var function)
                    || function.CodeSizeHeuristic != MonomorphizationCodeSizeHeuristic.InlineSmallBody)
                {
                    continue;
                }

                yield return strategy.SymbolName;
            }
        }
    }

    private sealed class DevirtualizeSsaIrPass : ICompilerPass
    {
        public string Id => "devirt-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["const-prop"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var optimized = new SsaDirectCallDevirtualizer(context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel)).Optimize(ssa);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, optimized);
        }
    }

    private sealed class CseConstGraphCallsSsaPass : ICompilerPass
    {
        public string Id => "cse-const-graph-calls-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["inline-ssa", "refine-function-effects", "semantic-validate", "type-check"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var semanticValidation = context.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var optimized = new SsaConstGraphCallCseOptimizer(
                effectModel,
                semanticValidation,
                typeModel).Optimize(ssa);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
        }
    }

    private sealed class SsaValueFactsPass : ICompilerPass
    {
        public string Id => "value-facts";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies =>
            ["const-lookup-tables-ssa", "semantic-validate", "load-modules", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var semanticValidation = context.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var specializationCodegenStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var directCallParameterEffects = SsaDynamicStorageCallFactPolicy.BuildDirectCallParameterEffects(
                semanticValidation,
                loadedModules,
                specializationCodegenStrategy);
            var facts = new SsaValueFactAnalyzer(directCallParameterEffects).Analyze(ssa);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, facts);
            context.Logs.Info(
                category: "optimization",
                eventId: "ssa.value-facts.summary",
                message: "Analyzed SSA value facts.",
                stage: Id,
                operation: "analyze",
                data: CreateValueFactsLogData(facts),
                kind: CompilerLogKind.Decision,
                outcome: CompilerLogOutcome.Continued,
                verbosity: CompilerLogVerbosity.Verbose);
        }

        private static IReadOnlyDictionary<string, string> CreateValueFactsLogData(SsaValueFactModel facts)
        {
            var valueCount = 0;
            var integerRangeCount = 0;
            var knownBitsCount = 0;
            var booleanCount = 0;
            var nullabilityCount = 0;
            var pointerAlignmentCount = 0;
            var lengthCount = 0;
            var capacityCount = 0;
            var initializedPrefixCount = 0;
            var textLiteralPayloadCount = 0;
            var boundedRawPointerRegionCount = 0;
            var dynamicStorageRegionCount = 0;
            var blockEntryBlockCount = 0;
            var blockEntryFactCount = 0;
            var blockExitBlockCount = 0;
            var blockExitFactCount = 0;

            foreach (var function in facts.Functions.Values)
            {
                valueCount += function.Values.Count;
                foreach (var valueFacts in function.Values.Values)
                {
                    if (valueFacts.IntegerRangeKind != SsaFactLatticeKind.Unknown)
                    {
                        integerRangeCount++;
                    }

                    if (valueFacts.KnownBitsKind != SsaFactLatticeKind.Unknown)
                    {
                        knownBitsCount++;
                    }

                    if (valueFacts.BooleanKind != SsaFactLatticeKind.Unknown)
                    {
                        booleanCount++;
                    }

                    if (valueFacts.Nullability != SsaNullabilityFactKind.Unknown)
                    {
                        nullabilityCount++;
                    }

                    if (valueFacts.PointerAlignmentKind != SsaFactLatticeKind.Unknown)
                    {
                        pointerAlignmentCount++;
                    }

                    if (valueFacts.LengthKind != SsaFactLatticeKind.Unknown)
                    {
                        lengthCount++;
                    }

                    if (valueFacts.CapacityKind != SsaFactLatticeKind.Unknown)
                    {
                        capacityCount++;
                    }

                    if (valueFacts.InitializedPrefixKind != SsaFactLatticeKind.Unknown)
                    {
                        initializedPrefixCount++;
                    }

                    if (valueFacts.TextLiteralPayloadKind != SsaFactLatticeKind.Unknown)
                    {
                        textLiteralPayloadCount++;
                    }

                    if (valueFacts.BoundedRawPointerRegionKind != SsaFactLatticeKind.Unknown)
                    {
                        boundedRawPointerRegionCount++;
                    }

                    if (valueFacts.DynamicStorageRegionKind != SsaFactLatticeKind.Unknown)
                    {
                        dynamicStorageRegionCount++;
                    }
                }

                if (function.BlockEntryValueFacts is { } blockEntryFacts)
                {
                    blockEntryBlockCount += blockEntryFacts.Count;
                    blockEntryFactCount += blockEntryFacts.Values.Sum(static blockFacts => blockFacts.Count);
                }

                if (function.BlockExitValueFacts is { } blockExitFacts)
                {
                    blockExitBlockCount += blockExitFacts.Count;
                    blockExitFactCount += blockExitFacts.Values.Sum(static blockFacts => blockFacts.Count);
                }
            }

            return CompilerLogData.Create(
                ("module", facts.ModuleName),
                ("functions", facts.Functions.Count.ToString(CultureInfo.InvariantCulture)),
                ("values", valueCount.ToString(CultureInfo.InvariantCulture)),
                ("integerRanges", integerRangeCount.ToString(CultureInfo.InvariantCulture)),
                ("knownBits", knownBitsCount.ToString(CultureInfo.InvariantCulture)),
                ("booleans", booleanCount.ToString(CultureInfo.InvariantCulture)),
                ("nullability", nullabilityCount.ToString(CultureInfo.InvariantCulture)),
                ("pointerAlignments", pointerAlignmentCount.ToString(CultureInfo.InvariantCulture)),
                ("lengths", lengthCount.ToString(CultureInfo.InvariantCulture)),
                ("capacities", capacityCount.ToString(CultureInfo.InvariantCulture)),
                ("initializedPrefixes", initializedPrefixCount.ToString(CultureInfo.InvariantCulture)),
                ("textLiteralPayloads", textLiteralPayloadCount.ToString(CultureInfo.InvariantCulture)),
                ("boundedRawPointerRegions", boundedRawPointerRegionCount.ToString(CultureInfo.InvariantCulture)),
                ("dynamicStorageRegions", dynamicStorageRegionCount.ToString(CultureInfo.InvariantCulture)),
                ("blockEntryBlocks", blockEntryBlockCount.ToString(CultureInfo.InvariantCulture)),
                ("blockEntryFacts", blockEntryFactCount.ToString(CultureInfo.InvariantCulture)),
                ("blockExitBlocks", blockExitBlockCount.ToString(CultureInfo.InvariantCulture)),
                ("blockExitFacts", blockExitFactCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private sealed class OptimizeConstLookupTablesSsaPass : ICompilerPass
    {
        public string Id => "const-lookup-tables-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["cse-const-graph-calls-ssa", "type-check"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var optimized = new SsaConstLookupTableOptimizer(typeModel).Optimize(ssa);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
        }
    }

    private sealed class SpecializeAsciiToUnicodeLiteralsSsaPass : ICompilerPass
    {
        public string Id => "specialize-ascii-to-unicode-literals-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["specialize-const-stdlib-helpers-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var facts = context.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts);
            var specialized = new SsaAsciiToUnicodeLiteralSpecializer().Optimize(ssa, facts);
            if (ReferenceEquals(specialized, ssa))
            {
                return;
            }

            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, specialized);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(specialized));
        }
    }

    private sealed class SpecializeConstStdlibHelpersSsaPass : ICompilerPass
    {
        public string Id => "specialize-const-stdlib-helpers-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["dynamic-storage-ssa", "type-check"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var facts = context.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var specialized = new SsaConstStdlibHelperSpecializer(
                typeModel,
                context.Options.TargetInfo).Optimize(ssa, facts);
            if (ReferenceEquals(specialized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(specialized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(propagated));
        }
    }

    private sealed class OptimizeSsaDynamicStoragePass : ICompilerPass
    {
        public string Id => "dynamic-storage-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies =>
            ["value-facts", "semantic-validate", "load-modules", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var facts = context.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts);
            var semanticValidation = context.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var specializationCodegenStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var directCallParameterEffects = SsaDynamicStorageCallFactPolicy.BuildDirectCallParameterEffects(
                semanticValidation,
                loadedModules,
                specializationCodegenStrategy);
            var optimized = new SsaDynamicStorageOptimizer(directCallParameterEffects).Optimize(ssa, facts);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer(directCallParameterEffects).Analyze(propagated));
        }
    }

    private sealed class OptimizeSsaDynamicAppendLoopsPass : ICompilerPass
    {
        public string Id => "dynamic-append-loop-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies =>
            ["dynamic-storage-ssa", "semantic-validate", "load-modules", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var optimized = new SsaDynamicAppendLoopOptimizer().Optimize(ssa);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            var semanticValidation = context.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var specializationCodegenStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var directCallParameterEffects = SsaDynamicStorageCallFactPolicy.BuildDirectCallParameterEffects(
                semanticValidation,
                loadedModules,
                specializationCodegenStrategy);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer(directCallParameterEffects).Analyze(propagated));
        }
    }

    private sealed class SpecializeConstantTextFormattingSsaPass : ICompilerPass
    {
        public string Id => "specialize-constant-text-formatting-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["specialize-ascii-to-unicode-literals-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var specialized = new SsaConstantTextFormatSpecializer().Optimize(ssa);
            if (ReferenceEquals(specialized, ssa))
            {
                return;
            }

            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, specialized);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(specialized));
        }
    }

    private sealed class PruneSsaBranchesPass : ICompilerPass
    {
        public string Id => "prune-branches";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["specialize-constant-text-formatting-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var facts = context.Artifacts.GetRequired(CompilerArtifactKeys.SsaValueFacts);
            var pruned = new SsaFactDrivenBranchPruner().Optimize(ssa, facts);
            if (ReferenceEquals(pruned, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(pruned);
            var optimized = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, optimized);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(optimized));
        }
    }

    private sealed class OptimizeSsaMemoryPass : ICompilerPass
    {
        public string Id => "memory-opt-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["prune-branches", "refine-function-effects"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);

            var optimized = new SsaAliasAwareMemoryOptimizer(effectModel).Optimize(ssa);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(propagated));
        }
    }

    private sealed class ScalarReplaceSsaAggregatesPass : ICompilerPass
    {
        public string Id => "sroa-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["ownership-traffic-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);

            var optimized = new SsaScalarReplacementOptimizer(effectModel).Optimize(ssa);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(propagated));
        }
    }

    private sealed class OptimizeSsaOwnershipTrafficPass : ICompilerPass
    {
        public string Id => "ownership-traffic-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["aggregate-construction-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var optimized = new SsaOwnershipTrafficOptimizer().Optimize(ssa);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(propagated));
        }
    }

    private sealed class OptimizeSsaAggregateConstructionPass : ICompilerPass
    {
        public string Id => "aggregate-construction-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["memory-opt-ssa", "type-check"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var optimized = new SsaAggregateConstructionStoreOptimizer(typeModel.NamedTypes).Optimize(ssa);
            if (ReferenceEquals(optimized, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(optimized);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(propagated));
        }
    }

    private sealed class ShapeSsaBranchesPass : ICompilerPass
    {
        public string Id => "shape-branches";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["sroa-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var shaped = new SsaCleanupOptimizer(enableSelectPredication: true).Optimize(ssa);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, shaped);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer().Analyze(shaped));
        }
    }

    private sealed class FoldIntegerArithmeticSsaPass : ICompilerPass
    {
        public string Id => "arithmetic-fold-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies =>
            ["shape-branches", "semantic-validate", "load-modules", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);

            var folded = new SsaIntegerArithmeticFolder().Optimize(ssa);
            if (ReferenceEquals(folded, ssa))
            {
                return;
            }

            var cleaned = new SsaCleanupOptimizer(enableSelectPredication: false).Optimize(folded);
            var propagated = new SsaConstantPropagator().Optimize(cleaned);
            var semanticValidation = context.Artifacts.GetRequired(CompilerArtifactKeys.SemanticValidation);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var specializationCodegenStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var directCallParameterEffects = SsaDynamicStorageCallFactPolicy.BuildDirectCallParameterEffects(
                semanticValidation,
                loadedModules,
                specializationCodegenStrategy);
            context.Artifacts.Set(CompilerArtifactKeys.OptimizedSsaIr, propagated);
            context.Artifacts.Set(CompilerArtifactKeys.SsaValueFacts, new SsaValueFactAnalyzer(directCallParameterEffects).Analyze(propagated));
        }
    }

    private sealed class LowerToAbiPass : ICompilerPass
    {
        public string Id => "lower-abi";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "type-check", "enum-layout", "refine-function-effects", "lower-hir", "arithmetic-fold-ssa"];

        public void Execute(CompilerPassContext context)
        {
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var hir = context.Artifacts.GetRequired(CompilerArtifactKeys.HighLevelIr);
            var abiModel = new AbiLowerer(syntaxModel, loadedModules, typeModel, enumLayoutModel, effectModel, hir, context.Options, context.Diagnostics).Lower();
            context.Artifacts.Set(CompilerArtifactKeys.AbiModel, abiModel);
        }
    }

    private sealed class ValidateSsaIrPass : ICompilerPass
    {
        public string Id => "validate-ssa";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["type-check", "load-modules", "enum-layout", "arithmetic-fold-ssa", "lower-abi", "specialization-codegen-strategy"];

        public void Execute(CompilerPassContext context)
        {
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var abiModel = context.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var enumLayoutModel = context.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var specializationCodegenStrategy = context.Artifacts.GetRequired(CompilerArtifactKeys.SpecializationCodegenStrategy);
            var publishedConcreteLayouts = LlvmSpecializationEmissionPlanner.BuildPublishedConcreteLayouts(loadedModules);
            new SsaIrValidator(context, ssa, abiModel, typeModel, enumLayoutModel, publishedConcreteLayouts, specializationCodegenStrategy, loadedModules).Validate();
        }
    }

    private sealed class EnumLayoutPass : ICompilerPass
    {
        public string Id => "enum-layout";

        public CompilerPhase Phase => CompilerPhase.Typing;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["type-check", "load-modules"];

        public void Execute(CompilerPassContext context)
        {
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var loadedModules = context.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
            var layouts = EnumLayoutBuilder.Build(typeModel).Layouts
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

            foreach (var module in loadedModules.ImportedModules)
            {
                if (module.PackageImageFacts is not { } packageImageFacts)
                {
                    continue;
                }

                foreach (var (qualifiedName, enumLayout) in packageImageFacts.EnumLayouts)
                {
                    layouts[qualifiedName] = enumLayout;
                }
            }

            context.Artifacts.Set(CompilerArtifactKeys.EnumLayoutModel, new EnumLayoutModel(typeModel.ModuleName, layouts));
        }
    }
}
