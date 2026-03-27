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
            .Add(new SemanticValidationPass())
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
            var model = SyntaxModelFactory.Create(parseResult);

            context.Artifacts.Set(CompilerArtifactKeys.SyntaxModel, model);
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

                    var importedSyntax = SyntaxModelFactory.Create(importedParse);
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
                    var qualifiedName = isRoot
                        ? function.Name
                        : $"{module.SyntaxModel.ModuleName}.{function.Name}";
                    effects[qualifiedName] = CreateEffectProfile(qualifiedName, function);
                }
            }

            context.Artifacts.Set(
                CompilerArtifactKeys.FunctionEffects,
                new FunctionEffectModel(loadedModules.RootModuleName, effects));
        }

        private static FunctionEffectProfile CreateEffectProfile(string name, FunctionDeclarationModel function)
        {
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

        public IReadOnlyList<string> Dependencies => ["parse", "syntax-model", "module-graph", "function-effects", "type-check"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);

            var validationModel = new SemanticValidator(context, parseResult, syntaxModel, moduleGraph, effectModel, typeModel).Validate();
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

    private sealed class LowerToHighLevelIrPass : ICompilerPass
    {
        public string Id => "lower-hir";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["syntax-model", "function-effects", "type-check", "semantic-validate", "ownership-validate"];

        public void Execute(CompilerPassContext context)
        {
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var effects = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var types = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);

            var functions = syntaxModel.Declarations
                .Where(static declaration => declaration.Function is not null)
                .Select(declaration =>
                {
                    var function = declaration.Function!;
                    return new HighLevelIrFunction(
                        function.Name,
                        types.Functions[function.Name],
                        function.HasBody,
                        effects.Functions[function.Name]);
                })
                .ToArray();

            context.Artifacts.Set(
                CompilerArtifactKeys.HighLevelIr,
                new HighLevelIrModule(syntaxModel.ModuleName, functions));
        }
    }

    private sealed class LowerToMidLevelIrPass : ICompilerPass
    {
        public string Id => "lower-mir";

        public CompilerPhase Phase => CompilerPhase.Lowering;

        public PassExecutionMode ExecutionMode => PassExecutionMode.SkipOnErrors;

        public IReadOnlyList<string> Dependencies => ["parse", "module-graph", "type-check", "lower-hir"];

        public void Execute(CompilerPassContext context)
        {
            var parseResult = context.Artifacts.GetRequired(CompilerArtifactKeys.ParseResult);
            var moduleGraph = context.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var hir = context.Artifacts.GetRequired(CompilerArtifactKeys.HighLevelIr);
            var mir = new MidLevelIrLowerer(context, parseResult, moduleGraph, typeModel).Lower(hir);
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

        public IReadOnlyList<string> Dependencies => ["syntax-model", "function-effects", "type-check", "const-prop", "lower-abi"];

        public void Execute(CompilerPassContext context)
        {
            var syntaxModel = context.Artifacts.GetRequired(CompilerArtifactKeys.SyntaxModel);
            var effectModel = context.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
            var typeModel = context.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
            var abiModel = context.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
            var ssa = context.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var llvmModule = new LlvmIrEmitter(
                context.Input,
                syntaxModel,
                effectModel,
                typeModel,
                abiModel,
                ssa,
                context.Options.TargetInfo,
                internalizeModulePrivate: context.Options.QualifyModuleSymbols).Emit();
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

        public IReadOnlyList<string> Dependencies => ["syntax-model", "type-check", "function-effects"];

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
}
