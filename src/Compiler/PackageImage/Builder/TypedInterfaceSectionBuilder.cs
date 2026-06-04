using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    private static bool TryBuildFunctionManifest(
        FunctionDeclarationModel declarationFunction,
        string name,
        string qualifiedName,
        string visibility,
        string lookupName,
        TypeCheckModel typeModel,
        AbiModel abiModel,
        FunctionEffectModel effectModel,
        out StarkPackageFunctionManifest manifest)
    {
        manifest = default!;

        if (!typeModel.Functions.TryGetValue(lookupName, out var function)
            || !abiModel.Functions.TryGetValue(lookupName, out var abiFunction)
            || !effectModel.Functions.TryGetValue(lookupName, out var effects))
        {
            return false;
        }

        manifest = new StarkPackageFunctionManifest(
            name,
            qualifiedName,
            visibility,
            abiFunction.SymbolName,
            declarationFunction.Kind.ToString().ToLowerInvariant(),
            RenderManifestTypeText(function.ReturnType, ModuleNameFromQualifiedName(qualifiedName)),
            function.Parameters
                .Select(parameter => new StarkPackageParameterManifest(
                    parameter.Name,
                    RenderManifestTypeText(parameter.Type, ModuleNameFromQualifiedName(qualifiedName)),
                    parameter.IsDisjoint,
                    parameter.IsConst,
                    parameter.RawPointerElementCountExpression))
                .ToArray(),
            effects.IsFfi,
            effects.IsStrictFp,
            effects.UseFastCallingConvention,
            BuildAsmManifest(declarationFunction.Asm),
            GenericParameters: declarationFunction.GenericParams.Count == 0 ? null : declarationFunction.GenericParams.ToArray(),
            IsHot: declarationFunction.Modifiers.IsHot,
            IsCold: declarationFunction.Modifiers.IsCold,
            InlinePreference: RenderInlinePreference(declarationFunction.Modifiers.InlinePreference),
            HasExplicitInlinePreference: declarationFunction.Modifiers.HasExplicitInlinePreference,
            IsUnsafe: declarationFunction.Modifiers.IsUnsafe,
            IsVarargs: effects.IsVarargs,
            FfiAbi: effects.FfiAbi is { } ffiAbi ? StarkFfiAbiFacts.DisplayName(ffiAbi) : null,
            BackendOptimizationMode: RenderBackendOptimizationMode(declarationFunction.BackendOptimizationMode),
            DisjointParameterGroups: BuildParameterDisjointGroupManifests(function.DisjointGroups),
            OverlapParameterGroups: BuildParameterOverlapGroupManifests(function.OverlapGroups),
            SameParameterGroups: BuildParameterSameGroupManifests(function.SameGroups));
        return true;
    }

    private static StarkPackageTypedFunctionManifest BuildTypedFunctionManifest(
        FunctionDeclarationModel declarationFunction,
        StarkPackageFunctionManifest manifest,
        StarkVisibility declarationVisibility,
        string lookupName,
        TypeCheckModel typeModel,
        string moduleName)
    {
        var function = typeModel.Functions[lookupName];
        return new StarkPackageTypedFunctionManifest(
            manifest.Name,
            manifest.QualifiedName,
            manifest.Visibility,
            manifest.SymbolName,
            manifest.Kind,
            BuildTypeReference(function.ReturnType, moduleName),
            function.Parameters
                .Select(parameter => new StarkPackageTypedParameterManifest(
                    parameter.Name,
                    BuildTypeReference(parameter.Type, moduleName),
                    parameter.IsDisjoint,
                    parameter.IsConst,
                    parameter.RawPointerElementCountExpression))
                .ToArray(),
            manifest.IsFfi,
            manifest.IsStrictFp,
            manifest.UseFastCallingConvention,
            manifest.Asm,
            manifest.GenericParameters,
            QualifiedResolvedName: QualifyPublishedResolvedName(moduleName, lookupName),
            PublishedOverloadKey: declarationFunction.PublishedOverloadKey ?? FunctionOverloadFacts.BuildOverloadKey(declarationFunction.Parameters),
            HasGenericTemplateBody: GenericTemplatePublicationPolicy.ShouldPublishTypedTemplateBody(
                declarationVisibility,
                declarationFunction,
                function),
            IsHot: manifest.IsHot,
            IsCold: manifest.IsCold,
            InlinePreference: manifest.InlinePreference,
            HasExplicitInlinePreference: manifest.HasExplicitInlinePreference,
            IsUnsafe: manifest.IsUnsafe,
            IsVarargs: manifest.IsVarargs,
            FfiAbi: manifest.FfiAbi,
            BackendOptimizationMode: manifest.BackendOptimizationMode,
            DisjointParameterGroups: BuildParameterDisjointGroupManifests(function.DisjointGroups),
            OverlapParameterGroups: BuildParameterOverlapGroupManifests(function.OverlapGroups),
            SameParameterGroups: BuildParameterSameGroupManifests(function.SameGroups),
            HasBody: declarationFunction.HasBody);
    }

    private static StarkPackageTypeManifest BuildTypeManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        string qualifiedName,
        string visibility,
        NamedTypeSymbol namedType,
        TypeCheckModel typeModel,
        AbiModel abiModel,
        FunctionEffectModel effectModel)
    {
        return new StarkPackageTypeManifest(
            declaration.Name,
            qualifiedName,
            visibility,
            declaration.Kind.ToString().ToLowerInvariant(),
            declaration.Kind == DeclarationKind.Enum
                ? []
                : namedType.OrderedFields
                    .Select(field => new StarkPackageFieldManifest(
                        field.Name,
                        RenderManifestTypeText(field.Type, module.SyntaxModel.ModuleName),
                        RenderFieldVisibility(field.Visibility),
                        field.ExplicitOffsetBytes))
                    .ToArray(),
            GenericParameters: namedType.GenericParams.Count == 0 ? null : namedType.GenericParams.ToArray(),
            PrimaryConstructorParameters: BuildTypePrimaryConstructorParameters(module, declaration.Name, namedType),
            Variants: declaration.Kind != DeclarationKind.Enum
                ? null
                : namedType.Variants
                    .Select(variant => new StarkPackageEnumVariantManifest(
                        variant.Name,
                        variant.UsesNamedFields,
                        variant.Fields
                            .Select(field => new StarkPackageFieldManifest(
                                field.Name ?? $"Item{field.Position}",
                                RenderManifestTypeText(field.Type, module.SyntaxModel.ModuleName)))
                            .ToArray(),
                        Role: RenderEnumVariantRole(variant.Role),
                        AbsorbsErrorType: variant.AbsorbsErrorType is { } absorbedErrorType
                            ? RenderManifestTypeText(absorbedErrorType, module.SyntaxModel.ModuleName)
                            : null))
                    .ToArray(),
            Methods: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypeMethodManifests(module, declaration.Name, typeModel, abiModel, effectModel),
            Destructor: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypeDestructorManifest(module, declaration.Name),
            BackendOptimizationMode: RenderBackendOptimizationMode(declaration.BackendOptimizationMode),
            StructLayout: RenderStructLayoutKind(namedType.Layout?.Kind),
            PackBytes: namedType.Layout?.PackBytes,
            AlignBytes: namedType.Layout?.AlignBytes,
            ImplementedTraits: BuildImplementedTraitManifestNames(namedType));
    }

    private static StarkPackageTypedTypeManifest BuildTypedTypeManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        string qualifiedName,
        string visibility,
        NamedTypeSymbol namedType,
        TypeCheckModel typeModel,
        AbiModel abiModel,
        FunctionEffectModel effectModel,
        ModuleGraph moduleGraph)
    {
        return new StarkPackageTypedTypeManifest(
            declaration.Name,
            qualifiedName,
            visibility,
            declaration.Kind.ToString().ToLowerInvariant(),
            declaration.Kind == DeclarationKind.Enum
                ? []
                : namedType.OrderedFields
                    .Select(field => new StarkPackageTypedFieldManifest(
                        field.Name,
                        BuildTypeReference(field.Type, module.SyntaxModel.ModuleName),
                        RenderFieldVisibility(field.Visibility),
                        field.ExplicitOffsetBytes))
                    .ToArray(),
            GenericParameters: namedType.GenericParams.Count == 0 ? null : namedType.GenericParams.ToArray(),
            PrimaryConstructorParameters: BuildTypedTypePrimaryConstructorParameters(module, declaration.Name, namedType),
            Variants: declaration.Kind != DeclarationKind.Enum
                ? null
                : namedType.Variants
                    .Select(variant => new StarkPackageTypedEnumVariantManifest(
                        variant.Name,
                        variant.UsesNamedFields,
                        variant.Fields
                            .Select(field => new StarkPackageTypedFieldManifest(
                                field.Name ?? $"Item{field.Position}",
                                BuildTypeReference(field.Type, module.SyntaxModel.ModuleName)))
                            .ToArray(),
                        Role: RenderEnumVariantRole(variant.Role),
                        AbsorbsErrorType: variant.AbsorbsErrorType is { } absorbedErrorType
                            ? BuildTypeReference(absorbedErrorType, module.SyntaxModel.ModuleName)
                            : null))
                    .ToArray(),
            Methods: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypedTypeMethodManifests(module, declaration.Name, typeModel, abiModel, effectModel),
            Destructor: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypeDestructorManifest(module, declaration.Name),
            Constructors: declaration.Kind is DeclarationKind.Struct or DeclarationKind.Record
                ? BuildTypedTypeConstructors(module, declaration.Name, namedType, typeModel, moduleGraph)
                : null,
            BackendOptimizationMode: RenderBackendOptimizationMode(declaration.BackendOptimizationMode),
            StructLayout: RenderStructLayoutKind(namedType.Layout?.Kind),
            PackBytes: namedType.Layout?.PackBytes,
            AlignBytes: namedType.Layout?.AlignBytes,
            ImplementedTraits: BuildImplementedTraitManifestNames(namedType));
    }

    private static IReadOnlyList<string>? BuildImplementedTraitManifestNames(NamedTypeSymbol namedType)
    {
        return namedType.ImplementedTraits.Count == 0
            ? null
            : namedType.ImplementedTraits
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static traitName => traitName, StringComparer.Ordinal)
                .ToArray();
    }

    private static IReadOnlyList<StarkPackageParameterManifest>? BuildTypePrimaryConstructorParameters(
        LoadedModuleDocument module,
        string typeName,
        NamedTypeSymbol namedType)
    {
        var parameters = GetRecordPrimaryConstructorParameters(module, typeName);
        if (parameters is null)
        {
            return null;
        }

        return parameters
            .Select(parameter => (Name: parameter.Identifier().GetText(), Parameter: parameter))
            .Where(parameter => namedType.TryGetField(parameter.Name, out _, out _))
            .Select(parameter =>
            {
                namedType.TryGetField(parameter.Name, out var field, out _);
                return new StarkPackageParameterManifest(
                    parameter.Name,
                    RenderManifestTypeText(field.Type, module.SyntaxModel.ModuleName),
                    ParameterHasPrefix(parameter.Parameter, StarkParser.DISJOINT),
                    ParameterHasPrefix(parameter.Parameter, StarkParser.CONST));
            })
            .ToArray();
    }

    private static IReadOnlyList<StarkPackageTypedParameterManifest>? BuildTypedTypePrimaryConstructorParameters(
        LoadedModuleDocument module,
        string typeName,
        NamedTypeSymbol namedType)
    {
        var parameters = GetRecordPrimaryConstructorParameters(module, typeName);
        if (parameters is null)
        {
            return null;
        }

        return parameters
            .Select(parameter => (Name: parameter.Identifier().GetText(), Parameter: parameter))
            .Where(parameter => namedType.TryGetField(parameter.Name, out _, out _))
            .Select(parameter =>
            {
                namedType.TryGetField(parameter.Name, out var field, out _);
                return new StarkPackageTypedParameterManifest(
                    parameter.Name,
                    BuildTypeReference(field.Type, module.SyntaxModel.ModuleName),
                    ParameterHasPrefix(parameter.Parameter, StarkParser.DISJOINT),
                    ParameterHasPrefix(parameter.Parameter, StarkParser.CONST));
            })
            .ToArray();
    }

    private static IReadOnlyList<StarkParser.ParameterContext>? GetRecordPrimaryConstructorParameters(
        LoadedModuleDocument module,
        string typeName)
    {
        var recordDeclaration = module.ParseResult.Root.topLevelDeclaration()
            .Select(static declaration => declaration.recordDeclaration())
            .FirstOrDefault(recordDeclaration => recordDeclaration is not null
                && string.Equals(recordDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal));

        return recordDeclaration?.primaryConstructorParameters()?.parameterList().parameter();
    }

    private static IReadOnlyList<StarkPackageTypedConstructorManifest>? BuildTypedTypeConstructors(
        LoadedModuleDocument module,
        string typeName,
        NamedTypeSymbol namedType,
        TypeCheckModel typeModel,
        ModuleGraph moduleGraph)
    {
        var constructors = GetTypeConstructorDeclarations(module, typeName)
            .Where(constructor => string.Equals(constructor.Identifier().GetText(), typeName, StringComparison.Ordinal))
            .ToArray();
        if (constructors.Length == 0)
        {
            return null;
        }

        var genericParameters = namedType.GenericParams.Count == 0
            ? null
            : namedType.GenericParams.ToHashSet(StringComparer.Ordinal);
        var resolver = CreatePackageImageTypeResolver(moduleGraph, typeModel);

        return constructors
            .Select(constructor => new StarkPackageTypedConstructorManifest(
                constructor.parameterList().parameter()
                    .Select(parameter => new StarkPackageTypedParameterManifest(
                        parameter.Identifier().GetText(),
                        BuildTypeReference(
                            resolver.ResolveParameterType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName, out var rawPointerElementCountExpression),
                            module.SyntaxModel.ModuleName),
                        ParameterHasPrefix(parameter, StarkParser.DISJOINT),
                        ParameterHasPrefix(parameter, StarkParser.CONST),
                        rawPointerElementCountExpression))
                    .ToArray()))
            .ToArray();
    }

    private static IReadOnlyList<StarkParser.ConstructorDeclarationContext> GetTypeConstructorDeclarations(
        LoadedModuleDocument module,
        string typeName)
    {
        foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
        {
            if (declaration.structDeclaration() is { } structDeclaration
                && string.Equals(structDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal))
            {
                return structDeclaration.structBody().structMember()
                    .Select(static member => member.constructorDeclaration())
                    .Where(static constructor => constructor is not null)!
                    .ToArray();
            }

            if (declaration.recordDeclaration() is { } recordDeclaration
                && string.Equals(recordDeclaration.Identifier().GetText(), typeName, StringComparison.Ordinal))
            {
                return recordDeclaration.recordBody().recordMember()
                    .Select(static member => member.constructorDeclaration())
                    .Where(static constructor => constructor is not null)!
                    .ToArray();
            }
        }

        return [];
    }

    private static StarkTypeResolver CreatePackageImageTypeResolver(
        ModuleGraph moduleGraph,
        TypeCheckModel typeModel)
    {
        return new StarkTypeResolver(
            new CompilerPassContext(new CompilationState(new CompilationInput(string.Empty), new CompilerOptions())),
            "package-image-build",
            moduleGraph,
            typeModel.NamedTypes,
            typeModel.TypeAliases);
    }

    private static StarkPackageAsmManifest? BuildAsmManifest(AsmFunctionModel? asm)
    {
        if (asm is null)
        {
            return null;
        }

        return new StarkPackageAsmManifest(
            asm.ArchitectureText,
            asm.TemplateText,
            asm.Inputs
                .Select(static input => new StarkPackageAsmInputManifest(input.RegisterName, input.ValueName))
                .ToArray(),
            asm.Outputs
                .Select(static output => new StarkPackageAsmOutputManifest(output.RegisterName, output.ValueName, output.BindsReturnValue))
                .ToArray(),
            asm.Clobbers.ToArray());
    }

    private static IReadOnlyList<StarkPackageMethodManifest>? BuildTypeMethodManifests(
        LoadedModuleDocument module,
        string containingTypeName,
        TypeCheckModel typeModel,
        AbiModel abiModel,
        FunctionEffectModel effectModel)
    {
        var methods = module.SyntaxModel.Declarations
            .Where(declaration => declaration.Kind == DeclarationKind.Function
                                  && declaration.Function is not null
                                  && declaration.Name.StartsWith($"{containingTypeName}.", StringComparison.Ordinal))
            .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .Select(declaration =>
            {
                var lookupName = FunctionOverloadFacts.QualifyResolvedName(
                    module,
                    FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                if (!typeModel.Functions.TryGetValue(lookupName, out var function)
                    || !abiModel.Functions.TryGetValue(lookupName, out var abiFunction)
                    || !effectModel.Functions.TryGetValue(lookupName, out var effects))
                {
                    return null;
                }

                return new StarkPackageMethodManifest(
                    declaration.Name[(containingTypeName.Length + 1)..],
                    $"{module.SyntaxModel.ModuleName}.{declaration.Name}",
                    abiFunction.SymbolName,
                    declaration.Function!.Kind.ToString().ToLowerInvariant(),
                    RenderManifestTypeText(function.ReturnType, module.SyntaxModel.ModuleName),
                    function.Parameters
                        .Select(parameter => new StarkPackageParameterManifest(
                            parameter.Name,
                            RenderManifestTypeText(parameter.Type, module.SyntaxModel.ModuleName),
                            parameter.IsDisjoint,
                            parameter.IsConst,
                            parameter.RawPointerElementCountExpression))
                        .ToArray(),
                    effects.IsFfi,
                    effects.IsStrictFp,
                    effects.UseFastCallingConvention,
                    GenericParameters: declaration.Function.GenericParams.Count == 0 ? null : declaration.Function.GenericParams.ToArray(),
                    IsHot: declaration.Function.Modifiers.IsHot,
                    IsCold: declaration.Function.Modifiers.IsCold,
                    InlinePreference: RenderInlinePreference(declaration.Function.Modifiers.InlinePreference),
                    HasExplicitInlinePreference: declaration.Function.Modifiers.HasExplicitInlinePreference,
                    IsStatic: declaration.Function.IsStatic,
                    IsUnsafe: declaration.Function.Modifiers.IsUnsafe,
                    IsVarargs: effects.IsVarargs,
                    FfiAbi: effects.FfiAbi is { } ffiAbi ? StarkFfiAbiFacts.DisplayName(ffiAbi) : null,
                    Visibility: declaration.Visibility.ToString().ToLowerInvariant(),
                    BackendOptimizationMode: RenderBackendOptimizationMode(declaration.Function.BackendOptimizationMode),
                    DisjointParameterGroups: BuildParameterDisjointGroupManifests(function.DisjointGroups),
                    OverlapParameterGroups: BuildParameterOverlapGroupManifests(function.OverlapGroups),
                    SameParameterGroups: BuildParameterSameGroupManifests(function.SameGroups));
            })
            .Where(static manifest => manifest is not null)
            .Cast<StarkPackageMethodManifest>()
            .ToArray();

        return methods.Length == 0 ? null : methods;
    }

    private static IReadOnlyList<StarkPackageTypedMethodManifest>? BuildTypedTypeMethodManifests(
        LoadedModuleDocument module,
        string containingTypeName,
        TypeCheckModel typeModel,
        AbiModel abiModel,
        FunctionEffectModel effectModel)
    {
        var methods = module.SyntaxModel.Declarations
            .Where(declaration => declaration.Kind == DeclarationKind.Function
                                  && declaration.Function is not null
                                  && declaration.Name.StartsWith($"{containingTypeName}.", StringComparison.Ordinal))
            .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .Select(declaration =>
            {
                var lookupName = FunctionOverloadFacts.QualifyResolvedName(
                    module,
                    FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration));
                if (!typeModel.Functions.TryGetValue(lookupName, out var function)
                    || !abiModel.Functions.TryGetValue(lookupName, out var abiFunction)
                    || !effectModel.Functions.TryGetValue(lookupName, out var effects))
                {
                    return null;
                }

                return new StarkPackageTypedMethodManifest(
                    declaration.Name[(containingTypeName.Length + 1)..],
                    $"{module.SyntaxModel.ModuleName}.{declaration.Name}",
                    abiFunction.SymbolName,
                    declaration.Function!.Kind.ToString().ToLowerInvariant(),
                    BuildTypeReference(function.ReturnType, module.SyntaxModel.ModuleName),
                    function.Parameters
                        .Select(parameter => new StarkPackageTypedParameterManifest(
                            parameter.Name,
                            BuildTypeReference(parameter.Type, module.SyntaxModel.ModuleName),
                            parameter.IsDisjoint,
                            parameter.IsConst,
                            parameter.RawPointerElementCountExpression))
                        .ToArray(),
                    effects.IsFfi,
                    effects.IsStrictFp,
                    effects.UseFastCallingConvention,
                    GenericParameters: declaration.Function.GenericParams.Count == 0 ? null : declaration.Function.GenericParams.ToArray(),
                    QualifiedResolvedName: QualifyPublishedResolvedName(module.SyntaxModel.ModuleName, lookupName),
                    PublishedOverloadKey: declaration.Function.PublishedOverloadKey ?? FunctionOverloadFacts.BuildOverloadKey(declaration.Function.Parameters),
                    HasGenericTemplateBody: GenericTemplatePublicationPolicy.ShouldPublishTypedTemplateBody(
                        declaration.Visibility,
                        declaration.Function,
                        function),
                    IsHot: declaration.Function.Modifiers.IsHot,
                    IsCold: declaration.Function.Modifiers.IsCold,
                    InlinePreference: RenderInlinePreference(declaration.Function.Modifiers.InlinePreference),
                    HasExplicitInlinePreference: declaration.Function.Modifiers.HasExplicitInlinePreference,
                    IsStatic: declaration.Function.IsStatic,
                    Visibility: declaration.Visibility.ToString().ToLowerInvariant(),
                    IsUnsafe: declaration.Function.Modifiers.IsUnsafe,
                    IsVarargs: effects.IsVarargs,
                    FfiAbi: effects.FfiAbi is { } ffiAbi ? StarkFfiAbiFacts.DisplayName(ffiAbi) : null,
                    BackendOptimizationMode: RenderBackendOptimizationMode(declaration.Function.BackendOptimizationMode),
                    DisjointParameterGroups: BuildParameterDisjointGroupManifests(function.DisjointGroups),
                    OverlapParameterGroups: BuildParameterOverlapGroupManifests(function.OverlapGroups),
                    SameParameterGroups: BuildParameterSameGroupManifests(function.SameGroups),
                    HasBody: declaration.Function.HasBody);
            })
            .Where(static manifest => manifest is not null)
            .Cast<StarkPackageTypedMethodManifest>()
            .ToArray();

        return methods.Length == 0 ? null : methods;
    }

    private static StarkPackageDestructorManifest? BuildTypeDestructorManifest(
        LoadedModuleDocument module,
        string containingTypeName)
    {
        var destructor = DeclaredDestructorSyntaxCollector.Collect(module)
            .FirstOrDefault(candidate => string.Equals(candidate.LocalTypeName, containingTypeName, StringComparison.Ordinal));
        if (destructor is null)
        {
            return null;
        }

        return new StarkPackageDestructorManifest(
            destructor.IsMutable,
            GetContextSourceText(module.ParseResult, destructor.Body));
    }

    private static string GetContextSourceText(ParseResult parseResult, ParserRuleContext context)
    {
        var startIndex = context.Start?.StartIndex ?? -1;
        var stopIndex = context.Stop?.StopIndex ?? -1;
        if (startIndex < 0
            || stopIndex < startIndex
            || stopIndex >= parseResult.SourceText.Length)
        {
            return context.GetText();
        }

        return parseResult.SourceText.Substring(startIndex, stopIndex - startIndex + 1);
    }

    private static string QualifyPublishedResolvedName(string moduleName, string lookupName)
    {
        if (string.IsNullOrWhiteSpace(moduleName)
            || lookupName.StartsWith($"{moduleName}.", StringComparison.Ordinal))
        {
            return lookupName;
        }

        return $"{moduleName}.{lookupName}";
    }

    private static string? RenderFieldVisibility(StarkVisibility visibility)
    {
        return visibility == StarkVisibility.Public
            ? null
            : visibility.ToString().ToLowerInvariant();
    }

    private static string? RenderStructLayoutKind(StructLayoutKind? layoutKind)
    {
        return layoutKind switch
        {
            StructLayoutKind.C => "C",
            StructLayoutKind.Explicit => "Explicit",
            _ => null
        };
    }

    private static bool ParameterHasPrefix(StarkParser.ParameterContext parameter, int tokenType)
    {
        return parameter.parameterContractPrefix()
            .Any(prefix => prefix.Start.Type == tokenType);
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterDisjointGroupManifests(
        IReadOnlyList<ParameterDisjointGroup> groups)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        var manifests = groups
            .Select(static group => new StarkPackageParameterDisjointGroupManifest(
                group.ParameterNames
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                group.HasSubregions
                    ? group.MemoryRegions
                        .Select(static region => new StarkPackageParameterMemoryRegionManifest(
                            region.ParameterName,
                            region.StartExpression,
                            region.CountExpression))
                        .ToArray()
                    : null))
            .Where(static group => group.ParameterNames.Count >= 2 || group.Regions is { Count: >= 2 })
            .ToArray();
        return manifests.Length == 0 ? null : manifests;
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterOverlapGroupManifests(
        IReadOnlyList<ParameterOverlapGroup> groups)
    {
        return BuildParameterRelationGroupManifests(groups.Select(static group => group.ParameterNames));
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterSameGroupManifests(
        IReadOnlyList<ParameterSameGroup> groups)
    {
        return BuildParameterRelationGroupManifests(groups.Select(static group => group.ParameterNames));
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterRelationGroupManifests(
        IEnumerable<IReadOnlyList<string>> groups)
    {
        var manifests = groups
            .Select(static group => new StarkPackageParameterDisjointGroupManifest(
                group
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .Where(static group => group.ParameterNames.Count >= 2)
            .ToArray();
        return manifests.Length == 0 ? null : manifests;
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterDisjointGroupManifests(
        StarkParser.ParameterListContext parameterList,
        IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> memoryContractClauses)
    {
        var groups = new List<StarkPackageParameterDisjointGroupManifest>();
        var prefixedParameters = parameterList.parameter()
            .Where(static parameter => ParameterHasPrefix(parameter, StarkParser.DISJOINT))
            .Select(static parameter => parameter.Identifier().GetText())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (prefixedParameters.Length >= 2)
        {
            groups.Add(new StarkPackageParameterDisjointGroupManifest(prefixedParameters));
        }

        foreach (var contract in memoryContractClauses
                     .SelectMany(static clause => clause.parameterMemoryContract())
                     .Select(static contract => contract.disjointContract())
                     .Where(static contract => contract is not null)!)
        {
            var regions = contract.expressionList()
                .expression()
                .Select(static expression => TryGetParameterMemoryRegion(expression.GetText()))
                .Where(static region => region is not null)
                .Select(static region => region!)
                .ToArray();
            var names = regions
                .Select(static region => region.ParameterName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var hasSubregions = regions.Any(static region => !region.IsWholeParameter);
            if (regions.Length >= 2 && (hasSubregions || names.Length >= 2))
            {
                groups.Add(new StarkPackageParameterDisjointGroupManifest(
                    names,
                    hasSubregions
                        ? regions
                            .Select(static region => new StarkPackageParameterMemoryRegionManifest(
                                region.ParameterName,
                                region.StartExpression,
                                region.CountExpression))
                            .ToArray()
                        : null));
            }
        }

        return groups.Count == 0 ? null : groups;
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterOverlapGroupManifests(
        IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> memoryContractClauses)
    {
        return BuildParameterRelationGroupManifests(
            memoryContractClauses,
            static contract => contract.overlapContract()?.expressionList());
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterSameGroupManifests(
        IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> memoryContractClauses)
    {
        return BuildParameterRelationGroupManifests(
            memoryContractClauses,
            static contract => contract.sameContract()?.expressionList());
    }

    private static IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? BuildParameterRelationGroupManifests(
        IReadOnlyList<StarkParser.ParameterMemoryContractClauseContext> memoryContractClauses,
        Func<StarkParser.ParameterMemoryContractContext, StarkParser.ExpressionListContext?> selectExpressionList)
    {
        var groups = new List<StarkPackageParameterDisjointGroupManifest>();
        foreach (var clause in memoryContractClauses)
        {
            foreach (var contract in clause.parameterMemoryContract())
            {
                var expressionList = selectExpressionList(contract);
                if (expressionList is null)
                {
                    continue;
                }

                var names = expressionList
                    .expression()
                    .Select(static expression => TryGetDisjointContractParameterName(expression.GetText()))
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (names.Length >= 2)
                {
                    groups.Add(new StarkPackageParameterDisjointGroupManifest(names));
                }
            }
        }

        return groups.Count == 0 ? null : groups;
    }

    private static string? TryGetDisjointContractParameterName(string operandText)
    {
        return TryGetParameterMemoryRegion(operandText)?.ParameterName;
    }

    private static ParameterMemoryRegion? TryGetParameterMemoryRegion(string operandText)
    {
        operandText = TrimOuterParentheses(operandText);
        if (!TryReadIdentifier(operandText, 0, out var identifier, out var position))
        {
            return null;
        }

        if (position == operandText.Length)
        {
            return new ParameterMemoryRegion(identifier);
        }

        if (operandText[position] != '[' || operandText[^1] != ']')
        {
            return null;
        }

        var regionText = operandText[(position + 1)..^1];
        var separator = regionText.IndexOf(',', StringComparison.Ordinal);
        if (separator <= 0 || separator >= regionText.Length - 1)
        {
            return null;
        }

        return new ParameterMemoryRegion(
            identifier,
            TrimOuterParentheses(regionText[..separator]),
            TrimOuterParentheses(regionText[(separator + 1)..]));
    }

    private static string TrimOuterParentheses(string text)
    {
        while (text.Length >= 2 && text[0] == '(' && text[^1] == ')')
        {
            text = text[1..^1];
        }

        return text;
    }

    private static bool TryReadIdentifier(string text, int start, out string identifier, out int end)
    {
        identifier = string.Empty;
        end = start;
        if (start >= text.Length || !(char.IsLetter(text[start]) || text[start] == '_'))
        {
            return false;
        }

        end = start + 1;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        identifier = text[start..end];
        return true;
    }
}
