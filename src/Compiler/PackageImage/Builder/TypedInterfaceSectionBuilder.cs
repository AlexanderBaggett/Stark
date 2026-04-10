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
                    RenderManifestTypeText(parameter.Type, ModuleNameFromQualifiedName(qualifiedName))))
                .ToArray(),
            effects.IsFfi,
            effects.IsStrictFp,
            effects.UseFastCallingConvention,
            BuildAsmManifest(declarationFunction.Asm),
            GenericParameters: declarationFunction.GenericParams.Count == 0 ? null : declarationFunction.GenericParams.ToArray(),
            IsHot: declarationFunction.Modifiers.IsHot,
            IsCold: declarationFunction.Modifiers.IsCold,
            InlinePreference: RenderInlinePreference(declarationFunction.Modifiers.InlinePreference),
            HasExplicitInlinePreference: declarationFunction.Modifiers.HasExplicitInlinePreference);
        return true;
    }

    private static StarkPackageTypedFunctionManifest BuildTypedFunctionManifest(
        FunctionDeclarationModel declarationFunction,
        StarkPackageFunctionManifest manifest,
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
                    BuildTypeReference(parameter.Type, moduleName)))
                .ToArray(),
            manifest.IsFfi,
            manifest.IsStrictFp,
            manifest.UseFastCallingConvention,
            manifest.Asm,
            manifest.GenericParameters,
            QualifiedResolvedName: QualifyPublishedResolvedName(moduleName, lookupName),
            PublishedOverloadKey: declarationFunction.PublishedOverloadKey ?? FunctionOverloadFacts.BuildOverloadKey(declarationFunction.Parameters),
            HasGenericTemplateBody: declarationFunction.HasBody && function.IsGeneric,
            IsHot: manifest.IsHot,
            IsCold: manifest.IsCold,
            InlinePreference: manifest.InlinePreference,
            HasExplicitInlinePreference: manifest.HasExplicitInlinePreference);
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
                        RenderManifestTypeText(field.Type, module.SyntaxModel.ModuleName)))
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
                            .ToArray()))
                    .ToArray(),
            Methods: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypeMethodManifests(module, declaration.Name, typeModel, abiModel, effectModel),
            Destructor: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypeDestructorManifest(module, declaration.Name));
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
                        BuildTypeReference(field.Type, module.SyntaxModel.ModuleName)))
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
                            .ToArray()))
                    .ToArray(),
            Methods: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypedTypeMethodManifests(module, declaration.Name, typeModel, abiModel, effectModel),
            Destructor: declaration.Kind == DeclarationKind.Enum
                ? null
                : BuildTypeDestructorManifest(module, declaration.Name),
            Constructors: declaration.Kind is DeclarationKind.Struct or DeclarationKind.Record
                ? BuildTypedTypeConstructors(module, declaration.Name, namedType, typeModel, moduleGraph)
                : null);
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
            .Select(parameter => parameter.Identifier().GetText())
            .Where(parameterName => namedType.TryGetField(parameterName, out _, out _))
            .Select(parameterName =>
            {
                namedType.TryGetField(parameterName, out var field, out _);
                return new StarkPackageParameterManifest(
                    parameterName,
                    RenderManifestTypeText(field.Type, module.SyntaxModel.ModuleName));
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
            .Select(parameter => parameter.Identifier().GetText())
            .Where(parameterName => namedType.TryGetField(parameterName, out _, out _))
            .Select(parameterName =>
            {
                namedType.TryGetField(parameterName, out var field, out _);
                return new StarkPackageTypedParameterManifest(
                    parameterName,
                    BuildTypeReference(field.Type, module.SyntaxModel.ModuleName));
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
                            resolver.ResolveType(parameter.type_(), genericParameters, module.SyntaxModel.ModuleName),
                            module.SyntaxModel.ModuleName)))
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
                            RenderManifestTypeText(parameter.Type, module.SyntaxModel.ModuleName)))
                        .ToArray(),
                    effects.IsFfi,
                    effects.IsStrictFp,
                    effects.UseFastCallingConvention,
                    GenericParameters: declaration.Function.GenericParams.Count == 0 ? null : declaration.Function.GenericParams.ToArray(),
                    IsHot: declaration.Function.Modifiers.IsHot,
                    IsCold: declaration.Function.Modifiers.IsCold,
                    InlinePreference: RenderInlinePreference(declaration.Function.Modifiers.InlinePreference),
                    HasExplicitInlinePreference: declaration.Function.Modifiers.HasExplicitInlinePreference);
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
                            BuildTypeReference(parameter.Type, module.SyntaxModel.ModuleName)))
                        .ToArray(),
                    effects.IsFfi,
                    effects.IsStrictFp,
                    effects.UseFastCallingConvention,
                    GenericParameters: declaration.Function.GenericParams.Count == 0 ? null : declaration.Function.GenericParams.ToArray(),
                    QualifiedResolvedName: QualifyPublishedResolvedName(module.SyntaxModel.ModuleName, lookupName),
                    PublishedOverloadKey: declaration.Function.PublishedOverloadKey ?? FunctionOverloadFacts.BuildOverloadKey(declaration.Function.Parameters),
                    HasGenericTemplateBody: declaration.Function.HasBody && function.IsGeneric,
                    IsHot: declaration.Function.Modifiers.IsHot,
                    IsCold: declaration.Function.Modifiers.IsCold,
                    InlinePreference: RenderInlinePreference(declaration.Function.Modifiers.InlinePreference),
                    HasExplicitInlinePreference: declaration.Function.Modifiers.HasExplicitInlinePreference);
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
}
