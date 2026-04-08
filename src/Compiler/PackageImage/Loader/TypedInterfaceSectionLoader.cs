namespace Stark.Compiler;

internal static partial class PackageImageLoader
{
    public static bool TryBuildModuleSyntaxModel(ResolvedPackageModule module, out SyntaxModel syntaxModel)
    {
        syntaxModel = default!;

        if (module.Module.EffectiveTypedInterface is not { } typedInterface)
        {
            return false;
        }

        var imports = GetImports(module.Module)
            .OrderBy(static import => import.ModuleName, StringComparer.Ordinal)
            .ThenByDescending(static import => import.IsExported)
            .Select(static import => new ImportDeclarationModel(import.ModuleName, import.IsExported))
            .ToArray();
        var publishedOverloadKeysBySymbol = BuildPublishedOverloadKeyLookup(module.Module);
        var declarations = new List<TopLevelDeclarationModel>();

        foreach (var typeAlias in (typedInterface.TypeAliases ?? []).OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            if (!TryParseVisibility(typeAlias.Visibility, out var visibility))
            {
                return false;
            }

            declarations.Add(new TopLevelDeclarationModel(
                typeAlias.Name,
                DeclarationKind.TypeAlias,
                visibility,
                Function: null,
                TypeAlias: new TypeAliasDeclarationModel(
                    typeAlias.Name,
                    RenderTypeReference(typeAlias.TargetType),
                    typeAlias.GenericParameters ?? [])));
        }

        foreach (var type in typedInterface.Types.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            if (!TryParseVisibility(type.Visibility, out var visibility)
                || !TryParseTypeDeclarationKind(type.Kind, out var declarationKind))
            {
                return false;
            }

            declarations.Add(new TopLevelDeclarationModel(
                type.Name,
                declarationKind,
                visibility,
                Function: null,
                Destructor: type.Destructor is null
                    ? null
                    : new DestructorDeclarationModel(type.Destructor.IsMutable)));

            foreach (var method in (type.Methods ?? []).OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                if (!TryParseFunctionKind(method.Kind, out var functionKind))
                {
                    return false;
                }

                var qualifiedMethodName = $"{type.Name}.{method.Name}";
                declarations.Add(new TopLevelDeclarationModel(
                    qualifiedMethodName,
                    DeclarationKind.Function,
                    visibility,
                    CreateFunctionDeclarationModel(
                        qualifiedMethodName,
                        functionKind,
                        RenderTypeReference(method.ReturnType),
                        method.Parameters,
                        method.IsFfi,
                        method.IsStrictFp,
                        asm: null,
                        method.GenericParameters,
                        hasBody: HasGenericTemplateBody(
                            module.Module,
                            publishedOverloadKeysBySymbol,
                            $"{module.Module.ModuleName}.{qualifiedMethodName}",
                            method.SymbolName,
                            method.Parameters),
                        publishedOverloadKey: TryGetPublishedOverloadKey(publishedOverloadKeysBySymbol, method.SymbolName))));
            }
        }

        foreach (var global in typedInterface.Globals.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            if (!TryParseVisibility(global.Visibility, out var visibility)
                || !TryParseGlobalDeclarationKind(global.Kind, out var declarationKind))
            {
                return false;
            }

            declarations.Add(new TopLevelDeclarationModel(
                global.Name,
                declarationKind,
                visibility,
                Function: null));
        }

        foreach (var function in typedInterface.Functions.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            if (!TryParseVisibility(function.Visibility, out var visibility)
                || !TryParseFunctionKind(function.Kind, out var functionKind))
            {
                return false;
            }

            declarations.Add(new TopLevelDeclarationModel(
                function.Name,
                DeclarationKind.Function,
                visibility,
                CreateFunctionDeclarationModel(
                    function.Name,
                    functionKind,
                    RenderTypeReference(function.ReturnType),
                    function.Parameters,
                    function.IsFfi,
                    function.IsStrictFp,
                    function.Asm,
                    function.GenericParameters,
                    hasBody: HasGenericTemplateBody(
                        module.Module,
                        publishedOverloadKeysBySymbol,
                        function.QualifiedName,
                        function.SymbolName,
                        function.Parameters),
                    publishedOverloadKey: TryGetPublishedOverloadKey(publishedOverloadKeysBySymbol, function.SymbolName))));
        }

        syntaxModel = new SyntaxModel(
            module.Module.ModuleName,
            imports,
            declarations);
        return true;
    }

    private static Dictionary<string, string> BuildPublishedOverloadKeyLookup(StarkPackageModuleManifest module)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceSurface = module.EffectiveSourceSurface;

        foreach (var function in sourceSurface.Functions ?? [])
        {
            lookup[function.SymbolName] = FunctionOverloadFacts.BuildOverloadKey(function.Parameters.Select(static parameter => parameter.Type));
        }

        foreach (var type in sourceSurface.Types ?? [])
        {
            foreach (var method in type.Methods ?? [])
            {
                lookup[method.SymbolName] = FunctionOverloadFacts.BuildOverloadKey(method.Parameters.Select(static parameter => parameter.Type));
            }
        }

        return lookup;
    }

    private static string? TryGetPublishedOverloadKey(
        IReadOnlyDictionary<string, string> publishedOverloadKeysBySymbol,
        string symbolName)
    {
        return publishedOverloadKeysBySymbol.TryGetValue(symbolName, out var overloadKey)
            ? overloadKey
            : null;
    }
}
