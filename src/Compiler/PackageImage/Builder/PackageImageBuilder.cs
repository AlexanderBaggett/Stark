using System.Globalization;

namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    public static StarkPackageManifest Create(
        CompilationResult result,
        string libraryOutputPath,
        StarkPackageNativeDependencyManifest? nativeDependencies = null)
    {
        var loadedModules = result.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
        var moduleGraph = result.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
        var typeModel = result.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
        var enumLayoutModel = result.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
        var abiModel = result.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
        var effectModel = result.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
        result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validationModel);
        result.Artifacts.TryGet(CompilerArtifactKeys.OwnershipValidation, out OwnershipValidationModel? ownershipModel);

        var modules = new List<StarkPackageModuleManifest>();
        var loadedModuleDocuments = loadedModules.Modules.Values.ToArray();
        var packagedModuleNames = BuildPackagedModuleNames(loadedModuleDocuments);

        foreach (var module in loadedModuleDocuments.OrderBy(static module => module.SyntaxModel.ModuleName, StringComparer.Ordinal))
        {
            if (!packagedModuleNames.Contains(module.SyntaxModel.ModuleName))
            {
                continue;
            }

            var reExports = module.SyntaxModel.Imports
                .Where(static import => import.IsReExport)
                .OrderBy(static import => import.ModuleName, StringComparer.Ordinal)
                .Select(static import => new StarkPackageReExportManifest(import.ModuleName))
                .ToArray();
            var imports = module.SyntaxModel.Imports
                .Where(import => import.IsReExport || packagedModuleNames.Contains(import.ModuleName))
                .OrderBy(static import => import.ModuleName, StringComparer.Ordinal)
                .ThenByDescending(static import => import.IsReExport)
                .Select(static import => new StarkPackageImportManifest(import.ModuleName, import.IsReExport))
                .ToArray();

            var functions = new List<StarkPackageFunctionManifest>();
            var types = new List<StarkPackageTypeManifest>();
            var globals = new List<StarkPackageGlobalManifest>();
            var typeAliases = new List<StarkPackageTypeAliasManifest>();
            var typedFunctions = new List<StarkPackageTypedFunctionManifest>();
            var typedTypes = new List<StarkPackageTypedTypeManifest>();
            var typedGlobals = new List<StarkPackageTypedGlobalManifest>();
            var typedTypeAliases = new List<StarkPackageTypedTypeAliasManifest>();
            var functionEffects = new List<StarkPackageFunctionEffectManifest>();
            var abiFunctions = new List<StarkPackageAbiFunctionManifest>();
            var compilerFactNamedTypes = new List<StarkPackageTypedTypeManifest>();
            var concreteLayouts = new List<StarkPackageConcreteTypeLayoutManifest>();
            var enumLayouts = new List<StarkPackageEnumLayoutManifest>();
            var functionSemantics = new List<StarkPackageFunctionSemanticManifest>();
            var genericTemplates = new List<StarkPackageFunctionTemplateManifest>();

            foreach (var declaration in module.SyntaxModel.Declarations
                         .Where(static declaration => ShouldIncludeInPackageImageSurface(declaration.Visibility))
                         .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal))
            {
                var lookupName = LookupName(module.SyntaxModel.ModuleName, module.Reference.IsRoot, declaration.Name);
                var qualifiedName = $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                var visibility = declaration.Visibility.ToString().ToLowerInvariant();
                var resolvedLookupName = declaration.Function is null
                    ? lookupName
                    : FunctionOverloadFacts.QualifyResolvedName(
                        module,
                        FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));

                switch (declaration.Kind)
                {
                    case DeclarationKind.Function when declaration.Function is not null:
                        if (!declaration.Name.Contains('.', StringComparison.Ordinal)
                            && TryBuildFunctionManifest(
                                declaration.Function,
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                resolvedLookupName,
                                typeModel,
                                abiModel,
                                effectModel,
                                out var functionManifest))
                        {
                            functions.Add(functionManifest);
                            typedFunctions.Add(BuildTypedFunctionManifest(
                                declaration.Function,
                                functionManifest,
                                declaration.Visibility,
                                resolvedLookupName,
                                typeModel,
                                module.SyntaxModel.ModuleName));
                        }

                        break;

                    case DeclarationKind.Struct:
                    case DeclarationKind.Record:
                    case DeclarationKind.Trait:
                    case DeclarationKind.Doctrine:
                        if (typeModel.NamedTypes.TryGetValue(lookupName, out var namedType))
                        {
                            types.Add(BuildTypeManifest(module, declaration, qualifiedName, visibility, namedType, typeModel, abiModel, effectModel));
                            typedTypes.Add(BuildTypedTypeManifest(module, declaration, qualifiedName, visibility, namedType, typeModel, abiModel, effectModel, moduleGraph));
                            if (TryBuildConcreteLayoutManifest(namedType, qualifiedName, typeModel, enumLayoutModel, out var concreteLayoutManifest))
                            {
                                concreteLayouts.Add(concreteLayoutManifest);
                            }
                        }

                        break;

                    case DeclarationKind.Enum:
                        if (typeModel.NamedTypes.TryGetValue(lookupName, out var enumType))
                        {
                            types.Add(BuildTypeManifest(module, declaration, qualifiedName, visibility, enumType, typeModel, abiModel, effectModel));
                            typedTypes.Add(BuildTypedTypeManifest(module, declaration, qualifiedName, visibility, enumType, typeModel, abiModel, effectModel, moduleGraph));
                            if (TryBuildConcreteLayoutManifest(enumType, qualifiedName, typeModel, enumLayoutModel, out var concreteLayoutManifest))
                            {
                                concreteLayouts.Add(concreteLayoutManifest);
                            }

                            if (enumLayoutModel.Layouts.TryGetValue(lookupName, out var enumLayout))
                            {
                                enumLayouts.Add(BuildEnumLayoutManifest(module, qualifiedName, enumLayout));
                            }
                        }

                        break;

                    case DeclarationKind.GlobalConstant:
                    case DeclarationKind.GlobalVariable:
                        if (typeModel.Globals.TryGetValue(lookupName, out var globalType))
                        {
                            globals.Add(new StarkPackageGlobalManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                declaration.Kind.ToString().ToLowerInvariant(),
                                RenderManifestTypeText(globalType.Type, module.SyntaxModel.ModuleName),
                                globalType.IsMutable,
                                BuildConstantInitializerManifest(globalType.ConstantInitializer, module.SyntaxModel.ModuleName)));
                            typedGlobals.Add(new StarkPackageTypedGlobalManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                declaration.Kind.ToString().ToLowerInvariant(),
                                BuildTypeReference(globalType.Type, module.SyntaxModel.ModuleName),
                                globalType.IsMutable,
                                BuildConstantInitializerManifest(globalType.ConstantInitializer, module.SyntaxModel.ModuleName)));
                        }

                        break;

                    case DeclarationKind.TypeAlias:
                        if (declaration.TypeAlias is not null
                            && typeModel.TypeAliases.TryGetValue(lookupName, out var aliasType))
                        {
                            typeAliases.Add(new StarkPackageTypeAliasManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                RenderManifestTypeText(aliasType.TargetType, module.SyntaxModel.ModuleName),
                                GenericParameters: aliasType.GenericParams.Count == 0 ? null : aliasType.GenericParams.ToArray(),
                                ComptimeGenericParameters: BuildComptimeGenericParameterManifests(
                                    aliasType.ComptimeGenericParams,
                                    module.SyntaxModel.ModuleName)));
                            typedTypeAliases.Add(new StarkPackageTypedTypeAliasManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                BuildTypeReference(aliasType.TargetType, module.SyntaxModel.ModuleName),
                                GenericParameters: aliasType.GenericParams.Count == 0 ? null : aliasType.GenericParams.ToArray(),
                                ComptimeGenericParameters: BuildComptimeGenericParameterManifests(
                                    aliasType.ComptimeGenericParams,
                                    module.SyntaxModel.ModuleName)));
                        }

                        break;
                }

                if (declaration.Function is not null
                    && TryBuildFunctionEffectManifest(module, declaration, effectModel, out var functionEffectManifest))
                {
                    functionEffects.Add(functionEffectManifest);
                }

                if (declaration.Function is not null
                    && TryBuildAbiFunctionManifest(module, declaration, abiModel, out var abiFunctionManifest))
                {
                    abiFunctions.Add(abiFunctionManifest);
                }

                if (declaration.Function is not null
                    && validationModel is not null
                    && TryBuildFunctionSemanticManifest(module, declaration, validationModel, ownershipModel, out var functionSemanticManifest))
                {
                    functionSemantics.Add(functionSemanticManifest);
                }
            }

            AddModuleLocalFunctionCompilerFacts(
                module,
                abiModel,
                effectModel,
                validationModel,
                ownershipModel,
                functionEffects,
                abiFunctions,
                functionSemantics);
            AddModuleLocalNamedTypeCompilerFacts(
                module,
                moduleGraph,
                typeModel,
                abiModel,
                effectModel,
                compilerFactNamedTypes);

            foreach (var genericTemplate in BuildGenericFunctionTemplates(module, typeModel, validationModel, ownershipModel))
            {
                genericTemplates.Add(genericTemplate);
            }

            AddModuleConcreteLayoutFacts(module, typeModel, enumLayoutModel, concreteLayouts);
            AddModuleEnumLayoutFacts(module, enumLayoutModel, enumLayouts);

            modules.Add(new StarkPackageModuleManifest(
                module.SyntaxModel.ModuleName,
                [],
                [],
                [],
                [],
                TypeAliases: null,
                TypedInterface: null,
                CompilerFacts: null,
                GenericTemplates: null,
                Imports: null,
                SourceSurface: BuildSourceSurfaceSection(
                    module,
                    imports,
                    reExports,
                    functions,
                    types,
                    globals,
                    typeAliases),
                CompilerSections: new StarkPackageCompilerSectionsManifest(
                    TypedInterface: new StarkPackageTypedInterfaceSection(
                        typedFunctions,
                        typedTypes,
                        typedGlobals,
                        TypeAliases: typedTypeAliases,
                        Imports: imports),
                    CompilerFacts: new StarkPackageCompilerFactsSection(
                        functionEffects,
                        AbiFunctions: abiFunctions,
                        NamedTypes: compilerFactNamedTypes.Count == 0 ? null : compilerFactNamedTypes,
                        ConcreteLayouts: concreteLayouts,
                        EnumLayouts: enumLayouts,
                        FunctionSemantics: functionSemantics,
                        Linkage: BuildLinkageManifest(module, abiModel, abiFunctions, functionSemantics),
                        BackendOptimizationMode: RenderBackendOptimizationMode(module.SyntaxModel.BackendOptimizationMode)),
                    GenericTemplates: genericTemplates.Count == 0
                        ? null
                        : new StarkPackageGenericTemplateSection(genericTemplates))));
        }

        return new StarkPackageManifest(
            loadedModules.RootModuleName,
            Path.GetFileName(libraryOutputPath),
            modules,
            NormalizeNativeDependencies(nativeDependencies));
    }

    private static StarkPackageTypedConstantInitializerManifest? BuildConstantInitializerManifest(
        TypedConstantInitializer? initializer,
        string moduleName)
    {
        if (initializer is null)
        {
            return null;
        }

        return new StarkPackageTypedConstantInitializerManifest(
            initializer.Kind.ToString().ToLowerInvariant(),
            BuildTypeReference(initializer.Type, moduleName),
            IntegerValue: initializer.IntegerValue?.ToString(CultureInfo.InvariantCulture),
            FloatLiteralText: initializer.FloatLiteralText,
            BoolValue: initializer.BoolValue,
            TextLiteralText: initializer.TextLiteralText,
            Elements: initializer.Elements?.Select(element => BuildConstantInitializerManifest(element, moduleName)!).ToArray());
    }

    private static void AddModuleConcreteLayoutFacts(
        LoadedModuleDocument module,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        List<StarkPackageConcreteTypeLayoutManifest> concreteLayouts)
    {
        var localNamedTypes = GetModuleLocalNamedTypes(module);
        if (localNamedTypes.Count == 0)
        {
            return;
        }

        var publishedLayouts = concreteLayouts
            .Select(static layout => layout.QualifiedTypeName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var namedType in typeModel.NamedTypes.Values.OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            var qualifiedName = QualifyPackageLocalNamedTypeName(module.SyntaxModel.ModuleName, localNamedTypes, namedType.Name);
            if (qualifiedName is null || publishedLayouts.Contains(qualifiedName))
            {
                continue;
            }

            if (TryBuildConcreteLayoutManifest(namedType, qualifiedName, typeModel, enumLayoutModel, out var concreteLayoutManifest))
            {
                concreteLayouts.Add(concreteLayoutManifest);
                publishedLayouts.Add(qualifiedName);
            }
        }
    }

    private static void AddModuleLocalFunctionCompilerFacts(
        LoadedModuleDocument module,
        AbiModel abiModel,
        FunctionEffectModel effectModel,
        SemanticValidationModel? validationModel,
        OwnershipValidationModel? ownershipModel,
        List<StarkPackageFunctionEffectManifest> functionEffects,
        List<StarkPackageAbiFunctionManifest> abiFunctions,
        List<StarkPackageFunctionSemanticManifest> functionSemantics)
    {
        var publishedEffects = functionEffects
            .Select(static function => function.QualifiedResolvedName)
            .ToHashSet(StringComparer.Ordinal);
        var publishedAbiFunctions = abiFunctions
            .Select(static function => function.QualifiedResolvedName)
            .ToHashSet(StringComparer.Ordinal);
        var publishedSemantics = functionSemantics
            .Select(static function => function.QualifiedResolvedName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var declaration in module.SyntaxModel.Declarations
                     .Where(static declaration => declaration.Function is not null)
                     .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal))
        {
            var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
            var qualifiedResolvedName = $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}";

            if (!publishedEffects.Contains(qualifiedResolvedName)
                && TryBuildFunctionEffectManifest(module, declaration, effectModel, out var functionEffectManifest))
            {
                functionEffects.Add(functionEffectManifest);
                publishedEffects.Add(qualifiedResolvedName);
            }

            if (!publishedAbiFunctions.Contains(qualifiedResolvedName)
                && TryBuildAbiFunctionManifest(module, declaration, abiModel, out var abiFunctionManifest))
            {
                abiFunctions.Add(abiFunctionManifest);
                publishedAbiFunctions.Add(qualifiedResolvedName);
            }

            if (validationModel is not null
                && !publishedSemantics.Contains(qualifiedResolvedName)
                && TryBuildFunctionSemanticManifest(module, declaration, validationModel, ownershipModel, out var functionSemanticManifest))
            {
                functionSemantics.Add(functionSemanticManifest);
                publishedSemantics.Add(qualifiedResolvedName);
            }
        }
    }

    private static void AddModuleLocalNamedTypeCompilerFacts(
        LoadedModuleDocument module,
        ModuleGraph moduleGraph,
        TypeCheckModel typeModel,
        AbiModel abiModel,
        FunctionEffectModel effectModel,
        List<StarkPackageTypedTypeManifest> namedTypes)
    {
        var publishedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in module.SyntaxModel.Declarations
                     .Where(static declaration => declaration.Kind is DeclarationKind.Struct
                         or DeclarationKind.Record
                         or DeclarationKind.Trait
                         or DeclarationKind.Doctrine
                         or DeclarationKind.Enum)
                     .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal))
        {
            var lookupName = LookupName(module.SyntaxModel.ModuleName, module.Reference.IsRoot, declaration.Name);
            if (!typeModel.NamedTypes.TryGetValue(lookupName, out var namedType)
                || !publishedTypes.Add($"{module.SyntaxModel.ModuleName}.{declaration.Name}"))
            {
                continue;
            }

            namedTypes.Add(BuildTypedTypeManifest(
                module,
                declaration,
                $"{module.SyntaxModel.ModuleName}.{declaration.Name}",
                declaration.Visibility.ToString().ToLowerInvariant(),
                namedType,
                typeModel,
                abiModel,
                effectModel,
                moduleGraph));
        }
    }

    private static string? QualifyPackageLocalNamedTypeName(
        string moduleName,
        ISet<string> localNamedTypes,
        string namedTypeName)
    {
        if (string.IsNullOrWhiteSpace(namedTypeName))
        {
            return null;
        }

        if (namedTypeName.Contains('.', StringComparison.Ordinal))
        {
            return namedTypeName.StartsWith($"{moduleName}.", StringComparison.Ordinal)
                ? namedTypeName
                : null;
        }

        return localNamedTypes.Contains(namedTypeName)
            ? $"{moduleName}.{namedTypeName}"
            : null;
    }

    private static void AddModuleEnumLayoutFacts(
        LoadedModuleDocument module,
        EnumLayoutModel enumLayoutModel,
        List<StarkPackageEnumLayoutManifest> enumLayouts)
    {
        var localNamedTypes = GetModuleLocalNamedTypes(module);
        if (localNamedTypes.Count == 0)
        {
            return;
        }

        var publishedLayouts = enumLayouts
            .Select(static layout => layout.QualifiedTypeName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (enumName, enumLayout) in enumLayoutModel.Layouts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var qualifiedName = QualifyPackageLocalNamedTypeName(module.SyntaxModel.ModuleName, localNamedTypes, enumName);
            if (qualifiedName is null || publishedLayouts.Contains(qualifiedName))
            {
                continue;
            }

            enumLayouts.Add(BuildEnumLayoutManifest(module, qualifiedName, enumLayout));
            publishedLayouts.Add(qualifiedName);
        }
    }

    private static StarkPackageNativeDependencyManifest? NormalizeNativeDependencies(StarkPackageNativeDependencyManifest? dependencies)
    {
        if (dependencies is null)
        {
            return null;
        }

        var sources = NormalizeNativeDependencyList(dependencies.Sources);
        var includeDirectories = NormalizeNativeDependencyList(dependencies.IncludeDirectories);
        var libraryDirectories = NormalizeNativeDependencyList(dependencies.LibraryDirectories);
        var libraries = NormalizeNativeDependencyList(dependencies.Libraries);
        var linkArguments = NormalizeNativeDependencyList(dependencies.LinkArguments);
        var pkgConfigPackages = NormalizeNativeDependencyList(dependencies.PkgConfigPackages);

        if (sources is null
            && includeDirectories is null
            && libraryDirectories is null
            && libraries is null
            && linkArguments is null
            && pkgConfigPackages is null)
        {
            return null;
        }

        return new StarkPackageNativeDependencyManifest(
            sources,
            includeDirectories,
            libraryDirectories,
            libraries,
            linkArguments,
            pkgConfigPackages);
    }

    private static IReadOnlyList<string>? NormalizeNativeDependencyList(IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return null;
        }

        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return normalized.Length == 0 ? null : normalized;
    }

    private static string LookupName(string moduleName, bool isRoot, string declarationName)
    {
        return isRoot ? declarationName : $"{moduleName}.{declarationName}";
    }

    private static HashSet<string> BuildPackagedModuleNames(IReadOnlyList<LoadedModuleDocument> modules)
    {
        var modulesByName = modules.ToDictionary(
            static module => module.SyntaxModel.ModuleName,
            StringComparer.Ordinal);
        var packagedModuleNames = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<LoadedModuleDocument>();

        foreach (var module in modules)
        {
            if (!HasPackageImageSurface(module)
                || !packagedModuleNames.Add(module.SyntaxModel.ModuleName))
            {
                continue;
            }

            pending.Enqueue(module);
        }

        while (pending.Count != 0)
        {
            var module = pending.Dequeue();
            foreach (var import in module.SyntaxModel.Imports)
            {
                if (!modulesByName.TryGetValue(import.ModuleName, out var importedModule)
                    || !packagedModuleNames.Add(importedModule.SyntaxModel.ModuleName))
                {
                    continue;
                }

                pending.Enqueue(importedModule);
            }
        }

        return packagedModuleNames;
    }

    private static bool HasPackageImageSurface(LoadedModuleDocument module)
    {
        return module.SyntaxModel.Imports.Any(static import => import.IsReExport)
            || module.SyntaxModel.Declarations.Any(
                static declaration => ShouldIncludeInPackageImageSurface(declaration.Visibility));
    }

    private static bool ShouldIncludeInPackageImageSurface(StarkVisibility visibility)
    {
        return visibility is StarkVisibility.Internal or StarkVisibility.Public or StarkVisibility.Export;
    }

    private static string? RenderBackendOptimizationMode(ModuleBackendOptimizationMode mode)
    {
        return mode == ModuleBackendOptimizationMode.Opaque ? "opaque" : null;
    }

    private static StarkPackageLinkageManifest BuildLinkageManifest(
        LoadedModuleDocument module,
        AbiModel abiModel,
        IReadOnlyList<StarkPackageAbiFunctionManifest> abiFunctions,
        IReadOnlyList<StarkPackageFunctionSemanticManifest> functionSemantics)
    {
        var symbolByResolvedName = abiModel.Functions.Values
            .GroupBy(static function => function.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => EscapeObjectSymbolName(group.First().SymbolName),
                StringComparer.Ordinal);
        var definedSymbols = abiFunctions
            .Select(static function => EscapeObjectSymbolName(function.SymbolName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static symbol => symbol, StringComparer.Ordinal)
            .ToArray();
        var referencedSymbols = functionSemantics
            .SelectMany(static semantic => semantic.CalledFunctions)
            .Select(calledFunction => symbolByResolvedName.TryGetValue(calledFunction, out var symbolName) ? symbolName : null)
            .Where(static symbolName => !string.IsNullOrWhiteSpace(symbolName))
            .Cast<string>()
            .Except(definedSymbols, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static symbol => symbol, StringComparer.Ordinal)
            .ToArray();

        return new StarkPackageLinkageManifest(
            BuildArchiveObjectFileName(module),
            definedSymbols,
            referencedSymbols.Length == 0 ? null : referencedSymbols);
    }

    private static string BuildArchiveObjectFileName(LoadedModuleDocument module)
    {
        var extension = OperatingSystem.IsWindows() ? ".obj" : ".o";
        return module.Reference.IsRoot
            ? $"root{extension}"
            : $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}{extension}";
    }

    private static string EscapeObjectSymbolName(string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return "_";
        }

        var builder = new System.Text.StringBuilder(symbolName.Length);
        foreach (var ch in symbolName)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }
}
