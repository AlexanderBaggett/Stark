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

        var imports = GetImports(module.Module, includeSourceSurfaceImports: RequiresSourceSurfaceImports(module.Module))
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

                if (!TryParseVisibility(method.Visibility ?? type.Visibility, out var methodVisibility))
                {
                    return false;
                }

                var qualifiedMethodName = $"{type.Name}.{method.Name}";
                var publishedOverloadKey = method.PublishedOverloadKey
                    ?? TryGetPublishedOverloadKey(publishedOverloadKeysBySymbol, method.SymbolName);
                declarations.Add(new TopLevelDeclarationModel(
                    qualifiedMethodName,
                    DeclarationKind.Function,
                    methodVisibility,
                    CreateFunctionDeclarationModel(
                        qualifiedMethodName,
                        functionKind,
                        RenderTypeReference(method.ReturnType),
                        method.Parameters,
                        method.IsFfi,
                        method.IsStrictFp,
                        method.IsHot,
                        method.IsCold,
                        method.InlinePreference,
                        method.HasExplicitInlinePreference,
                        asm: null,
                        method.GenericParameters,
                        hasBody: method.HasGenericTemplateBody
                        || HasPublishedGenericTemplateBody(
                            module.Module,
                            module.Module.ModuleName,
                            qualifiedMethodName,
                            method.SymbolName,
                            publishedOverloadKey)
                        || HasGenericTemplateBody(
                            module.Module,
                            publishedOverloadKeysBySymbol,
                            $"{module.Module.ModuleName}.{qualifiedMethodName}",
                            method.SymbolName,
                            method.Parameters),
                        isStatic: method.IsStatic,
                        publishedOverloadKey: publishedOverloadKey)));
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

            var publishedOverloadKey = function.PublishedOverloadKey
                ?? TryGetPublishedOverloadKey(publishedOverloadKeysBySymbol, function.SymbolName);
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
                    function.IsHot,
                    function.IsCold,
                    function.InlinePreference,
                    function.HasExplicitInlinePreference,
                    function.Asm,
                    function.GenericParameters,
                    hasBody: function.HasGenericTemplateBody
                    || HasPublishedGenericTemplateBody(
                        module.Module,
                        module.Module.ModuleName,
                        function.QualifiedName,
                        function.SymbolName,
                        publishedOverloadKey)
                    || HasGenericTemplateBody(
                        module.Module,
                        publishedOverloadKeysBySymbol,
                        function.QualifiedName,
                        function.SymbolName,
                        function.Parameters),
                    publishedOverloadKey: publishedOverloadKey)));
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

        if (module.EffectiveTypedInterface is { } typedInterface)
        {
            foreach (var function in typedInterface.Functions)
            {
                if (!string.IsNullOrWhiteSpace(function.PublishedOverloadKey))
                {
                    lookup[function.SymbolName] = function.PublishedOverloadKey;
                }
            }

            foreach (var type in typedInterface.Types)
            {
                foreach (var method in type.Methods ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(method.PublishedOverloadKey))
                    {
                        lookup[method.SymbolName] = method.PublishedOverloadKey;
                    }
                }
            }
        }

        var sourceSurface = module.EffectiveSourceSurface;

        foreach (var function in sourceSurface.Functions ?? [])
        {
            lookup.TryAdd(
                function.SymbolName,
                FunctionOverloadFacts.BuildOverloadKey(function.Parameters.Select(static parameter => parameter.Type)));
        }

        foreach (var type in sourceSurface.Types ?? [])
        {
            foreach (var method in type.Methods ?? [])
            {
                lookup.TryAdd(
                    method.SymbolName,
                    FunctionOverloadFacts.BuildOverloadKey(method.Parameters.Select(static parameter => parameter.Type)));
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

    private static bool HasPublishedGenericTemplateBody(
        StarkPackageModuleManifest module,
        string moduleName,
        string qualifiedName,
        string symbolName,
        string? publishedOverloadKey)
    {
        var templates = module.EffectiveGenericTemplates?.Functions;
        if (templates is not { Count: > 0 })
        {
            return false;
        }

        var moduleQualifiedName = $"{moduleName}.{qualifiedName}";
        return templates.Any(template =>
            (string.IsNullOrWhiteSpace(publishedOverloadKey)
             || string.Equals(template.OverloadKey, publishedOverloadKey, StringComparison.Ordinal))
            && (string.Equals(template.QualifiedResolvedName, qualifiedName, StringComparison.Ordinal)
                || string.Equals(template.QualifiedResolvedName, moduleQualifiedName, StringComparison.Ordinal)
                || string.Equals(template.QualifiedName, qualifiedName, StringComparison.Ordinal)
                || string.Equals(template.QualifiedName, moduleQualifiedName, StringComparison.Ordinal)
                || string.Equals(template.QualifiedName, symbolName, StringComparison.Ordinal)));
    }
}
