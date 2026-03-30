using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stark.Compiler;

internal sealed record StarkPackageManifest(
    string RootModule,
    string LibraryFileName,
    IReadOnlyList<StarkPackageModuleManifest> Modules)
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static StarkPackageManifest? FromJson(string json)
    {
        return JsonSerializer.Deserialize<StarkPackageManifest>(json, SerializerOptions);
    }
}

internal sealed record StarkPackageModuleManifest(
    string ModuleName,
    IReadOnlyList<StarkPackageReExportManifest> ReExports,
    IReadOnlyList<StarkPackageFunctionManifest> Functions,
    IReadOnlyList<StarkPackageTypeManifest> Types,
    IReadOnlyList<StarkPackageGlobalManifest> Globals);

internal sealed record StarkPackageReExportManifest(
    string ModuleName);

internal sealed record StarkPackageFunctionManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string SymbolName,
    string Kind,
    string ReturnType,
    IReadOnlyList<StarkPackageParameterManifest> Parameters,
    bool IsFfi,
    bool UseFastCallingConvention);

internal sealed record StarkPackageParameterManifest(
    string Name,
    string Type);

internal sealed record StarkPackageMethodManifest(
    string Name,
    string QualifiedName,
    string SymbolName,
    string Kind,
    string ReturnType,
    IReadOnlyList<StarkPackageParameterManifest> Parameters,
    bool IsFfi,
    bool UseFastCallingConvention);

internal sealed record StarkPackageTypeManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string Kind,
    IReadOnlyList<StarkPackageFieldManifest> Fields,
    IReadOnlyList<StarkPackageEnumVariantManifest>? Variants = null,
    IReadOnlyList<StarkPackageMethodManifest>? Methods = null);

internal sealed record StarkPackageEnumVariantManifest(
    string Name,
    bool UsesNamedFields,
    IReadOnlyList<StarkPackageFieldManifest> Fields);

internal sealed record StarkPackageFieldManifest(
    string Name,
    string Type);

internal sealed record StarkPackageGlobalManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string Kind,
    string Type,
    bool IsMutable);

internal static class PackageManifestBuilder
{
    public static StarkPackageManifest Create(
        CompilationResult result,
        string libraryOutputPath)
    {
        var loadedModules = result.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
        var typeModel = result.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
        var abiModel = result.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
        var effectModel = result.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);

        var modules = new List<StarkPackageModuleManifest>();

        foreach (var module in loadedModules.Modules.Values.OrderBy(static module => module.SyntaxModel.ModuleName, StringComparer.Ordinal))
        {
            var reExports = module.SyntaxModel.Imports
                .Where(static import => import.IsReExport)
                .OrderBy(static import => import.ModuleName, StringComparer.Ordinal)
                .Select(static import => new StarkPackageReExportManifest(import.ModuleName))
                .ToArray();

            var functions = new List<StarkPackageFunctionManifest>();
            var types = new List<StarkPackageTypeManifest>();
            var globals = new List<StarkPackageGlobalManifest>();

            foreach (var declaration in module.SyntaxModel.Declarations
                         .Where(static declaration => declaration.Visibility is StarkVisibility.Public or StarkVisibility.Export)
                         .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal))
            {
                var lookupName = LookupName(module.SyntaxModel.ModuleName, module.Reference.IsRoot, declaration.Name);
                var qualifiedName = $"{module.SyntaxModel.ModuleName}.{declaration.Name}";
                var visibility = declaration.Visibility.ToString().ToLowerInvariant();

                switch (declaration.Kind)
                {
                    case DeclarationKind.Function when declaration.Function is not null:
                        if (!declaration.Name.Contains('.', StringComparison.Ordinal)
                            && TryBuildFunctionManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                lookupName,
                                declaration.Function.Kind,
                                typeModel,
                                abiModel,
                                effectModel,
                                out var functionManifest))
                        {
                            functions.Add(functionManifest);
                        }

                        break;

                    case DeclarationKind.Struct:
                    case DeclarationKind.Record:
                    case DeclarationKind.Trait:
                    case DeclarationKind.Doctrine:
                        if (typeModel.NamedTypes.TryGetValue(lookupName, out var namedType))
                        {
                            types.Add(new StarkPackageTypeManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                declaration.Kind.ToString().ToLowerInvariant(),
                                namedType.OrderedFields.Select(static field => new StarkPackageFieldManifest(field.Name, field.Type.DisplayName)).ToArray(),
                                Variants: null,
                                Methods: BuildTypeMethodManifests(module, declaration.Name, typeModel, abiModel, effectModel)));
                        }

                        break;

                    case DeclarationKind.Enum:
                        if (typeModel.NamedTypes.TryGetValue(lookupName, out var enumType))
                        {
                            types.Add(new StarkPackageTypeManifest(
                                declaration.Name,
                                qualifiedName,
                                visibility,
                                declaration.Kind.ToString().ToLowerInvariant(),
                                [],
                                enumType.Variants
                                    .Select(static variant => new StarkPackageEnumVariantManifest(
                                        variant.Name,
                                        variant.UsesNamedFields,
                                        variant.Fields
                                            .Select(static field => new StarkPackageFieldManifest(
                                                field.Name ?? $"Item{field.Position}",
                                                field.Type.DisplayName))
                                            .ToArray()))
                                    .ToArray()));
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
                                globalType.Type.DisplayName,
                                globalType.IsMutable));
                        }

                        break;
                }
            }

            if (reExports.Length == 0
                && functions.Count == 0
                && types.Count == 0
                && globals.Count == 0)
            {
                continue;
            }

            modules.Add(new StarkPackageModuleManifest(
                module.SyntaxModel.ModuleName,
                reExports,
                functions,
                types,
                globals));
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

    private static bool TryBuildFunctionManifest(
        string name,
        string qualifiedName,
        string visibility,
        string lookupName,
        StarkFunctionKind kind,
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
            kind.ToString().ToLowerInvariant(),
            function.ReturnType.DisplayName,
            function.Parameters.Select(static parameter => new StarkPackageParameterManifest(parameter.Name, parameter.Type.DisplayName)).ToArray(),
            effects.IsFfi,
            effects.UseFastCallingConvention);
        return true;
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
                var lookupName = LookupName(module.SyntaxModel.ModuleName, module.Reference.IsRoot, declaration.Name);
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
                    function.ReturnType.DisplayName,
                    function.Parameters.Select(static parameter => new StarkPackageParameterManifest(parameter.Name, parameter.Type.DisplayName)).ToArray(),
                    effects.IsFfi,
                    effects.UseFastCallingConvention);
            })
            .Where(static manifest => manifest is not null)
            .Cast<StarkPackageMethodManifest>()
            .ToArray();

        return methods.Length == 0 ? null : methods;
    }
}

internal sealed record ResolvedPackageModule(
    string ManifestPath,
    string LibraryPath,
    StarkPackageManifest Manifest,
    StarkPackageModuleManifest Module);

internal static class PackageManifestLoader
{
    public static bool TryLoadManifest(string manifestPath, out StarkPackageManifest manifest)
    {
        manifest = default!;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var parsed = StarkPackageManifest.FromJson(json);
            if (parsed is null)
            {
                return false;
            }

            manifest = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryBuildModuleSource(ResolvedPackageModule module, out string sourceText)
    {
        if (module.Module is null)
        {
            sourceText = string.Empty;
            return false;
        }

        var builder = new StringBuilder();

        foreach (var reExport in module.Module.ReExports.OrderBy(static item => item.ModuleName, StringComparer.Ordinal))
        {
            builder.Append("export import ");
            builder.Append(reExport.ModuleName);
            builder.AppendLine();
        }

        builder.Append("module ");
        builder.AppendLine(module.Module.ModuleName);
        builder.AppendLine();

        foreach (var type in module.Module.Types.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(type.Visibility);
            builder.Append(' ');
            builder.Append(type.Kind);
            builder.Append(' ');
            builder.Append(type.Name);
            builder.AppendLine(" {");

            if (string.Equals(type.Kind, "enum", StringComparison.Ordinal))
            {
                foreach (var variant in type.Variants ?? [])
                {
                    builder.Append("    ");
                    builder.Append(variant.Name);

                    if (variant.Fields.Count != 0)
                    {
                        if (variant.UsesNamedFields)
                        {
                            builder.Append(" { ");
                            builder.Append(string.Join(", ", variant.Fields.Select(static field => $"{field.Name}: {field.Type}")));
                            builder.Append(" }");
                        }
                        else
                        {
                            builder.Append('(');
                            builder.Append(string.Join(", ", variant.Fields.Select(static field => field.Type)));
                            builder.Append(')');
                        }
                    }

                    builder.AppendLine(",");
                }
            }
            else
            {
                foreach (var field in type.Fields)
                {
                    builder.Append("    ");
                    builder.Append(field.Type);
                    builder.Append(' ');
                    builder.Append(field.Name);
                    builder.AppendLine(";");
                }

                foreach (var method in (type.Methods ?? []).OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    builder.Append("    ");
                    if (method.IsFfi)
                    {
                        builder.Append("ffi ");
                    }

                    builder.Append(RenderFunctionKind(method.Kind));
                    builder.Append(' ');
                    builder.Append(method.ReturnType);
                    builder.Append(' ');
                    builder.Append(method.Name);
                    builder.Append('(');
                    builder.Append(string.Join(", ", method.Parameters.Select(static parameter => $"{parameter.Type} {parameter.Name}")));
                    builder.AppendLine(");");
                }
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        foreach (var global in module.Module.Globals.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(global.Visibility);
            builder.Append(' ');

            if (string.Equals(global.Kind, "globalconstant", StringComparison.Ordinal))
            {
                builder.Append("const ");
                builder.Append(global.Type);
                builder.Append(' ');
                builder.Append(global.Name);
                builder.AppendLine(" = 0;");
            }
            else
            {
                builder.Append("static ");
                if (global.IsMutable)
                {
                    builder.Append("mut ");
                }

                builder.Append(global.Type);
                builder.Append(' ');
                builder.Append(global.Name);
                builder.AppendLine(";");
            }
        }

        if (module.Module.Globals.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var function in module.Module.Functions.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(function.Visibility);
            builder.Append(' ');
            if (function.IsFfi)
            {
                builder.Append("ffi ");
            }

            builder.Append(RenderFunctionKind(function.Kind));
            builder.Append(' ');
            builder.Append(function.ReturnType);
            builder.Append(' ');
            builder.Append(function.Name);
            builder.Append('(');
            builder.Append(string.Join(", ", function.Parameters.Select(static parameter => $"{parameter.Type} {parameter.Name}")));
            builder.AppendLine(");");
        }

        sourceText = builder.ToString();
        return true;
    }

    private static string RenderFunctionKind(string kind)
    {
        return kind switch
        {
            "fn" => "fn",
            "finite" => "finite",
            "law" => "law",
            "finitelaw" => "finite law",
            _ => "fn"
        };
    }
}
