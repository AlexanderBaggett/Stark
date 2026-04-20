namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    public static StarkPackageManifest Create(
        CompilationResult result,
        string libraryOutputPath)
    {
        var loadedModules = result.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
        var moduleGraph = result.Artifacts.GetRequired(CompilerArtifactKeys.ModuleGraph);
        var typeModel = result.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
        var enumLayoutModel = result.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
        var abiModel = result.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
        var effectModel = result.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
        result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validationModel);

        var modules = new List<StarkPackageModuleManifest>();
        var publishedModuleNames = loadedModules.Modules.Values
            .Where(HasPublishedSurface)
            .Select(static module => module.SyntaxModel.ModuleName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var module in loadedModules.Modules.Values.OrderBy(static module => module.SyntaxModel.ModuleName, StringComparer.Ordinal))
        {
            var reExports = module.SyntaxModel.Imports
                .Where(static import => import.IsReExport)
                .OrderBy(static import => import.ModuleName, StringComparer.Ordinal)
                .Select(static import => new StarkPackageReExportManifest(import.ModuleName))
                .ToArray();
            var imports = module.SyntaxModel.Imports
                .Where(import => import.IsReExport || publishedModuleNames.Contains(import.ModuleName))
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
                                globalType.IsMutable));
                            typedGlobals.Add(new StarkPackageTypedGlobalManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                declaration.Kind.ToString().ToLowerInvariant(),
                                BuildTypeReference(globalType.Type, module.SyntaxModel.ModuleName),
                                globalType.IsMutable));
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
                                GenericParameters: aliasType.GenericParams.Count == 0 ? null : aliasType.GenericParams.ToArray()));
                            typedTypeAliases.Add(new StarkPackageTypedTypeAliasManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                BuildTypeReference(aliasType.TargetType, module.SyntaxModel.ModuleName),
                                GenericParameters: aliasType.GenericParams.Count == 0 ? null : aliasType.GenericParams.ToArray()));
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
                    && TryBuildFunctionSemanticManifest(module, declaration, validationModel, out var functionSemanticManifest))
                {
                    functionSemantics.Add(functionSemanticManifest);
                }
            }

            foreach (var genericTemplate in BuildGenericFunctionTemplates(module, typeModel, validationModel))
            {
                genericTemplates.Add(genericTemplate);
            }

            if (reExports.Length == 0
                && functions.Count == 0
                && types.Count == 0
                && globals.Count == 0
                && typeAliases.Count == 0)
            {
                continue;
            }

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
                        ConcreteLayouts: concreteLayouts,
                        EnumLayouts: enumLayouts,
                        FunctionSemantics: functionSemantics),
                    GenericTemplates: genericTemplates.Count == 0
                        ? null
                        : new StarkPackageGenericTemplateSection(genericTemplates))));
        }

        return new StarkPackageManifest(
            loadedModules.RootModuleName,
            Path.GetFileName(libraryOutputPath),
            modules);
    }

    private static string LookupName(string moduleName, bool isRoot, string declarationName)
    {
        return isRoot ? declarationName : $"{moduleName}.{declarationName}";
    }

    private static bool HasPublishedSurface(LoadedModuleDocument module)
    {
        return module.SyntaxModel.Imports.Any(static import => import.IsReExport)
            || module.SyntaxModel.Declarations.Any(
                static declaration => declaration.Visibility is StarkVisibility.Public or StarkVisibility.Export);
    }

    private static bool ShouldIncludeInPackageImageSurface(StarkVisibility visibility)
    {
        return visibility is StarkVisibility.Internal or StarkVisibility.Public or StarkVisibility.Export;
    }
}
