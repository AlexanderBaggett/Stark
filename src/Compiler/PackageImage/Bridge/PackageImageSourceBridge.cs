using System.Text;

namespace Stark.Compiler;

internal static partial class PackageImageLoader
{
    public static bool TryBuildModuleSource(ResolvedPackageModule module, out string sourceText)
    {
        if (module.Module is null)
        {
            sourceText = string.Empty;
            return false;
        }

        var builder = new StringBuilder();
        var genericTemplateBodies = BuildRenderableGenericTemplateBodyLookup(module.Module);
        var publishedOverloadKeysBySymbol = BuildPublishedOverloadKeyLookup(module.Module);
        var typeAliases = GetTypeAliases(module.Module);
        var types = GetTypes(module.Module);
        var globals = GetGlobals(module.Module);
        var functions = GetFunctions(module.Module);
        var imports = GetImports(module.Module, includeSourceSurfaceImports: RequiresSourceSurfaceImports(module.Module));

        foreach (var import in imports
                     .OrderBy(static item => item.ModuleName, StringComparer.Ordinal)
                     .ThenByDescending(static item => item.IsExported))
        {
            if (import.IsExported)
            {
                builder.Append("export ");
            }

            builder.Append("import ");
            builder.Append(import.ModuleName);
            builder.AppendLine();
        }

        if (imports.Count > 0)
        {
            builder.AppendLine();
        }

        if (module.Module.EffectiveCompilerFacts?.BackendOptimizationMode is { } backendOptimizationMode
            && string.Equals(backendOptimizationMode, "opaque", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("[Backend(Opaque)]");
        }

        builder.Append("module ");
        builder.AppendLine(module.Module.ModuleName);
        builder.AppendLine();

        foreach (var typeAlias in typeAliases.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(typeAlias.Visibility);
            builder.Append(" alias ");
            builder.Append(typeAlias.Name);
            if (typeAlias.GenericParameters is { Count: > 0 })
            {
                builder.Append('<');
                builder.Append(string.Join(", ", typeAlias.GenericParameters));
                builder.Append('>');
            }

            builder.Append(" = ");
            builder.Append(typeAlias.TargetType);
            builder.AppendLine(";");
        }

        if (typeAliases.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var type in types.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            EmitBackendAttribute(builder, string.Empty, type.BackendOptimizationMode);
            builder.Append(type.Visibility);
            builder.Append(' ');
            builder.Append(type.Kind);
            builder.Append(' ');
            builder.Append(type.Name);
            if (type.GenericParameters is { Count: > 0 })
            {
                builder.Append('<');
                builder.Append(string.Join(", ", type.GenericParameters));
                builder.Append('>');
            }

            if (string.Equals(type.Kind, "record", StringComparison.Ordinal)
                && type.PrimaryConstructorParameters is { Count: > 0 })
            {
                builder.Append('(');
                builder.Append(string.Join(", ", type.PrimaryConstructorParameters.Select(static parameter => RenderParameter(parameter))));
                builder.Append(')');
            }

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
                var primaryConstructorParameterNames = type.PrimaryConstructorParameters is { Count: > 0 }
                    ? type.PrimaryConstructorParameters.Select(static parameter => parameter.Name).ToHashSet(StringComparer.Ordinal)
                    : null;

                foreach (var field in type.Fields.Where(field => primaryConstructorParameterNames?.Contains(field.Name) != true))
                {
                    builder.Append("    ");
                    if (!string.IsNullOrWhiteSpace(field.Visibility)
                        && !string.Equals(field.Visibility, "public", StringComparison.Ordinal))
                    {
                        builder.Append(field.Visibility);
                        builder.Append(' ');
                    }

                    builder.Append(field.Type);
                    builder.Append(' ');
                    builder.Append(field.Name);
                    builder.AppendLine(";");
                }

                if (type.Constructors is { Count: > 0 })
                {
                    if (type.Fields.Count > 0)
                    {
                        builder.AppendLine();
                    }

                    foreach (var constructor in type.Constructors)
                    {
                        builder.Append("    ");
                        builder.Append(type.Name);
                        builder.Append('(');
                        builder.Append(string.Join(", ", constructor.Parameters.Select(static parameter => RenderParameter(parameter))));
                        builder.Append(") ");
                        builder.AppendLine(constructor.BodyText);
                        builder.AppendLine();
                    }
                }

                if (type.Destructor is not null)
                {
                    builder.Append("    ");
                    if (type.Destructor.IsMutable)
                    {
                        builder.Append("mut ");
                    }

                    builder.Append("drop ");
                    builder.Append(type.Destructor.BodyText);
                    builder.AppendLine();
                    builder.AppendLine();
                }

                foreach (var method in (type.Methods ?? []).OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    var supportsMemberVisibility = string.Equals(type.Kind, "struct", StringComparison.Ordinal)
                        || string.Equals(type.Kind, "record", StringComparison.Ordinal);
                    TryGetGenericTemplateBody(
                        genericTemplateBodies,
                        publishedOverloadKeysBySymbol,
                        $"{module.Module.ModuleName}.{type.Name}.{method.Name}",
                        method.SymbolName,
                        method.Parameters,
                        out var methodBodyText);
                    EmitBackendAttribute(builder, "    ", method.BackendOptimizationMode);
                    builder.Append("    ");
                    if (supportsMemberVisibility && !string.IsNullOrWhiteSpace(method.Visibility))
                    {
                        builder.Append(method.Visibility);
                        builder.Append(' ');
                    }

                    if (method.IsStatic)
                    {
                        builder.Append("static ");
                    }

                    if (method.IsStrictFp)
                    {
                        builder.Append("strictfp ");
                    }

                    if (method.IsHot)
                    {
                        builder.Append("hot ");
                    }

                    if (method.IsCold)
                    {
                        builder.Append("cold ");
                    }

                    if (method.HasExplicitInlinePreference)
                    {
                        builder.Append(RenderInlinePreference(ParseInlinePreferenceOrDefault(method.InlinePreference)));
                        builder.Append(' ');
                    }

                    if (method.IsUnsafe)
                    {
                        builder.Append("unsafe ");
                    }

                    if (method.IsFfi)
                    {
                        builder.Append("ffi ");
                    }

                    builder.Append(RenderFunctionKind(method.Kind));
                    builder.Append(' ');
                    builder.Append(method.ReturnType);
                    builder.Append(' ');
                    builder.Append(method.Name);
                    if (method.GenericParameters is { Count: > 0 })
                    {
                        builder.Append('<');
                        builder.Append(string.Join(", ", method.GenericParameters));
                        builder.Append('>');
                    }

                    builder.Append('(');
                    builder.Append(string.Join(", ", method.Parameters.Select(static parameter => RenderParameter(parameter))));
                    builder.Append(')');
                    AppendDisjointParameterGroups(builder, method.Parameters, method.DisjointParameterGroups);
                    if (methodBodyText is null)
                    {
                        builder.AppendLine(";");
                    }
                    else
                    {
                        builder.Append(' ');
                        builder.AppendLine(methodBodyText);
                    }
                }
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        foreach (var global in globals.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.Append(global.Visibility);
            builder.Append(' ');

            if (string.Equals(global.Kind, "globalconstant", StringComparison.Ordinal))
            {
                builder.Append("const ");
                builder.Append(RenderConstGlobalType(global.Type));
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

        if (globals.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var function in functions.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            TryGetGenericTemplateBody(
                genericTemplateBodies,
                publishedOverloadKeysBySymbol,
                function.QualifiedName,
                function.SymbolName,
                function.Parameters,
                out var functionBodyText);
            EmitFunction(builder, function, functionBodyText);
        }

        sourceText = builder.ToString();
        return true;
    }

    private static IReadOnlyList<StarkPackageImportManifest> GetImports(
        StarkPackageModuleManifest module,
        bool includeSourceSurfaceImports)
    {
        var typedInterface = module.EffectiveTypedInterface;
        if (typedInterface?.Imports is { } typedInterfaceImports)
        {
            return typedInterfaceImports;
        }

        if (includeSourceSurfaceImports
            && module.SourceSurface is { } explicitSourceSurface
            && explicitSourceSurface.Imports is { } explicitSourceSurfaceImports)
        {
            return MergeImportsAndReExports(explicitSourceSurfaceImports, explicitSourceSurface.ReExports);
        }

        if (module.Imports is { } legacyFlatImports)
        {
            return MergeImportsAndReExports(legacyFlatImports, module.ReExports);
        }

        if (module.SourceSurface?.ReExports is { } explicitSourceSurfaceReExports)
        {
            return ConvertReExports(explicitSourceSurfaceReExports);
        }

        if (module.ReExports.Count > 0)
        {
            return ConvertReExports(module.ReExports);
        }

        return [];
    }

    private static IReadOnlyList<StarkPackageImportManifest> MergeImportsAndReExports(
        IReadOnlyList<StarkPackageImportManifest> imports,
        IReadOnlyList<StarkPackageReExportManifest>? reExports)
    {
        if (reExports is null || reExports.Count == 0)
        {
            return imports;
        }

        var exportByModuleName = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var import in imports)
        {
            exportByModuleName[import.ModuleName] = import.IsExported;
        }

        foreach (var reExport in reExports)
        {
            exportByModuleName[reExport.ModuleName] = true;
        }

        return exportByModuleName
            .Select(static entry => new StarkPackageImportManifest(entry.Key, entry.Value))
            .ToArray();
    }

    private static IReadOnlyList<StarkPackageImportManifest> ConvertReExports(
        IReadOnlyList<StarkPackageReExportManifest> reExports)
    {
        return reExports
            .Select(static reExport => new StarkPackageImportManifest(reExport.ModuleName, IsExported: true))
            .ToArray();
    }

    private static bool RequiresSourceSurfaceImports(StarkPackageModuleManifest module)
    {
        return module.EffectiveTypedInterface is null
            || BuildRenderableGenericTemplateBodyLookup(module).Count != 0;
    }

    private static IReadOnlyList<StarkPackageTypeAliasManifest> GetTypeAliases(StarkPackageModuleManifest module)
    {
        var typedInterface = module.EffectiveTypedInterface;
        if (typedInterface?.TypeAliases is { } typedTypeAliases)
        {
            return typedTypeAliases.Select(ConvertTypeAliasManifest).ToArray();
        }

        return module.EffectiveSourceSurface.TypeAliases ?? [];
    }

    private static IReadOnlyList<StarkPackageTypeManifest> GetTypes(StarkPackageModuleManifest module)
    {
        var typedInterface = module.EffectiveTypedInterface;
        if (typedInterface is not null)
        {
            var sourceTypesByName = (module.EffectiveSourceSurface.Types ?? [])
                .ToDictionary(static type => type.Name, StringComparer.Ordinal);
            return typedInterface.Types
                .Select(type =>
                {
                    var converted = ConvertTypeManifest(type);
                    return sourceTypesByName.TryGetValue(converted.Name, out var sourceType)
                        ? MergeSourceSurfaceTypeDetails(converted, sourceType)
                        : converted;
                })
                .ToArray();
        }

        return module.EffectiveSourceSurface.Types ?? [];
    }

    private static StarkPackageTypeManifest MergeSourceSurfaceTypeDetails(
        StarkPackageTypeManifest typedType,
        StarkPackageTypeManifest sourceType)
    {
        return typedType with
        {
            PrimaryConstructorParameters = sourceType.PrimaryConstructorParameters ?? typedType.PrimaryConstructorParameters,
            Constructors = sourceType.Constructors ?? typedType.Constructors,
            Destructor = sourceType.Destructor ?? typedType.Destructor,
            Fields = sourceType.Fields.Count == typedType.Fields.Count
                ? sourceType.Fields
                : typedType.Fields,
            Variants = sourceType.Variants ?? typedType.Variants
        };
    }

    private static IReadOnlyList<StarkPackageGlobalManifest> GetGlobals(StarkPackageModuleManifest module)
    {
        var typedInterface = module.EffectiveTypedInterface;
        if (typedInterface is not null)
        {
            return typedInterface.Globals.Select(ConvertGlobalManifest).ToArray();
        }

        return module.EffectiveSourceSurface.Globals ?? [];
    }

    private static IReadOnlyList<StarkPackageFunctionManifest> GetFunctions(StarkPackageModuleManifest module)
    {
        var typedInterface = module.EffectiveTypedInterface;
        if (typedInterface is not null)
        {
            return typedInterface.Functions.Select(ConvertFunctionManifest).ToArray();
        }

        return module.EffectiveSourceSurface.Functions ?? [];
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

    private static string RenderInlinePreference(InlinePreference inlinePreference)
    {
        return inlinePreference switch
        {
            InlinePreference.Inline => "inline",
            InlinePreference.NoInline => "noinline",
            _ => "inlinehint"
        };
    }

    private static string RenderParameter(StarkPackageParameterManifest parameter)
    {
        var parts = new List<string>(4);
        if (parameter.IsDisjoint)
        {
            parts.Add("disjoint");
        }

        if (parameter.IsConst)
        {
            parts.Add("const");
        }

        parts.Add(RenderParameterType(parameter));
        parts.Add(parameter.Name);
        return string.Join(" ", parts);
    }

    private static string RenderParameterType(StarkPackageParameterManifest parameter)
    {
        return string.IsNullOrWhiteSpace(parameter.RawPointerElementCountExpression)
            || parameter.Type.Contains('[', StringComparison.Ordinal)
            ? parameter.Type
            : $"{parameter.Type}[{parameter.RawPointerElementCountExpression}]";
    }

    private static string RenderParameterType(StarkPackageTypedParameterManifest parameter)
    {
        var typeText = RenderTypeReference(parameter.Type);
        return string.IsNullOrWhiteSpace(parameter.RawPointerElementCountExpression)
            ? typeText
            : $"{typeText}[{parameter.RawPointerElementCountExpression}]";
    }

    private static void AppendDisjointParameterGroups(
        StringBuilder builder,
        IReadOnlyList<StarkPackageParameterManifest> parameters,
        IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? groups)
    {
        if (groups is null || groups.Count == 0)
        {
            return;
        }

        var prefixedParameters = parameters
            .Where(static parameter => parameter.IsDisjoint)
            .Select(static parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var names = group.ParameterNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length < 2 || names.All(prefixedParameters.Contains))
            {
                continue;
            }

            builder.Append(" where disjoint(");
            builder.Append(string.Join(", ", names));
            builder.Append(')');
        }
    }

    private static void EmitFunction(StringBuilder builder, StarkPackageFunctionManifest function, string? bodyText = null)
    {
        EmitBackendAttribute(builder, string.Empty, function.BackendOptimizationMode);
        builder.Append(function.Visibility);
        builder.Append(' ');
        if (function.IsStrictFp)
        {
            builder.Append("strictfp ");
        }

        if (function.IsHot)
        {
            builder.Append("hot ");
        }

        if (function.IsCold)
        {
            builder.Append("cold ");
        }

        if (function.HasExplicitInlinePreference)
        {
            builder.Append(RenderInlinePreference(ParseInlinePreferenceOrDefault(function.InlinePreference)));
            builder.Append(' ');
        }

        if (function.IsUnsafe)
        {
            builder.Append("unsafe ");
        }

        if (function.IsFfi)
        {
            builder.Append("ffi ");
        }

        if (function.IsVarargs)
        {
            builder.Append("varargs ");
        }

        if (function.Asm is not null)
        {
            builder.Append("asm(");
            builder.Append(function.Asm.ArchitectureText);
            builder.Append(") ");
        }

        builder.Append(RenderFunctionKind(function.Kind));
        builder.Append(' ');
        builder.Append(function.ReturnType);
        builder.Append(' ');
        builder.Append(function.Name);
        if (function.GenericParameters is { Count: > 0 })
        {
            builder.Append('<');
            builder.Append(string.Join(", ", function.GenericParameters));
            builder.Append('>');
        }

        builder.Append('(');
        builder.Append(string.Join(", ", function.Parameters.Select(static parameter => RenderParameter(parameter))));
        builder.Append(')');
        AppendDisjointParameterGroups(builder, function.Parameters, function.DisjointParameterGroups);

        if (function.Asm is null && bodyText is null)
        {
            builder.AppendLine(";");
            return;
        }

        if (bodyText is not null)
        {
            builder.Append(' ');
            builder.AppendLine(bodyText);
            return;
        }

        builder.AppendLine();

        var asm = function.Asm!;
        var clauses = new List<string>();
        clauses.AddRange(asm.Inputs.Select(static input => $"in(\"{EscapeStarkStringLiteral(input.RegisterName)}\") {input.ValueName}"));
        clauses.AddRange(asm.Outputs.Select(static output => output.BindsReturnValue
            ? $"out(\"{EscapeStarkStringLiteral(output.RegisterName)}\") return"
            : $"out(\"{EscapeStarkStringLiteral(output.RegisterName)}\") {output.ValueName}"));

        if (asm.Clobbers.Count != 0)
        {
            clauses.Add($"clobber({string.Join(", ", asm.Clobbers.Select(static register => $"\"{EscapeStarkStringLiteral(register)}\""))})");
        }

        for (var index = 0; index < clauses.Count; index++)
        {
            builder.Append("    ");
            builder.Append(clauses[index]);
            if (index + 1 < clauses.Count)
            {
                builder.Append(',');
            }

            builder.AppendLine();
        }

        builder.AppendLine("{");
        builder.Append("    \"");
        builder.Append(EscapeStarkStringLiteral(asm.TemplateText));
        builder.AppendLine("\"");
        builder.AppendLine("}");
    }

    private static string EscapeStarkStringLiteral(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString()
            });
        }

        return builder.ToString();
    }

    private static FunctionDeclarationModel CreateFunctionDeclarationModel(
        string name,
        StarkFunctionKind functionKind,
        string returnType,
        IReadOnlyList<StarkPackageTypedParameterManifest> parameters,
        bool isFfi,
        bool isVarargs,
        bool isStrictFp,
        bool isHot,
        bool isCold,
        string inlinePreference,
        bool hasExplicitInlinePreference,
        StarkPackageAsmManifest? asm,
        IReadOnlyList<string>? genericParameters,
        bool hasBody = false,
        bool isStatic = false,
        string? publishedOverloadKey = null,
        bool isUnsafe = false,
        ModuleBackendOptimizationMode backendOptimizationMode = ModuleBackendOptimizationMode.Default,
        IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? disjointParameterGroups = null)
    {
        var parsedInlinePreference = ParseInlinePreferenceOrDefault(inlinePreference);
        return new FunctionDeclarationModel(
            Name: name,
            Kind: functionKind,
            ReturnType: returnType,
            Parameters: parameters
                .Select(parameter => new ParameterModel(
                    parameter.Name,
                    RenderParameterType(parameter),
                    parameter.IsDisjoint,
                    parameter.IsConst,
                    parameter.RawPointerElementCountExpression))
                .ToArray(),
            Modifiers: new FunctionModifierSet(
                parsedInlinePreference,
                HasExplicitInlinePreference: hasExplicitInlinePreference,
                IsHot: isHot,
                IsCold: isCold,
                IsFfi: isFfi,
                IsVarargs: isVarargs,
                IsStrictFp: isStrictFp,
                IsUnsafe: isUnsafe),
            HasBody: hasBody,
            Asm: CreateAsmModel(asm),
            GenericParameterNames: genericParameters ?? [],
            PublishedOverloadKey: publishedOverloadKey,
            IsStatic: isStatic,
            Attributes: BuildBackendAttributes(backendOptimizationMode),
            BackendOptimizationMode: backendOptimizationMode,
            DisjointParameterGroups: BuildParameterDisjointGroups(parameters, disjointParameterGroups));
    }

    private static IReadOnlyList<ParameterDisjointGroup>? BuildParameterDisjointGroups(
        IReadOnlyList<StarkPackageTypedParameterManifest> parameters,
        IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? groups)
    {
        var result = new List<ParameterDisjointGroup>();
        var prefixedParameters = parameters
            .Where(static parameter => parameter.IsDisjoint)
            .Select(static parameter => parameter.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (prefixedParameters.Length >= 2)
        {
            result.Add(new ParameterDisjointGroup(prefixedParameters));
        }

        foreach (var group in groups ?? [])
        {
            var names = group.ParameterNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length >= 2 && !result.Any(existing => ParameterDisjointGroupsEqual(existing.ParameterNames, names)))
            {
                result.Add(new ParameterDisjointGroup(names));
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool ParameterDisjointGroupsEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.Count == right.Count
            && left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
    }

    private static void EmitBackendAttribute(StringBuilder builder, string indent, string? backendOptimizationMode)
    {
        if (string.Equals(backendOptimizationMode, "opaque", StringComparison.Ordinal))
        {
            builder.Append(indent);
            builder.AppendLine("[Backend(Opaque)]");
        }
    }

    private static InlinePreference ParseInlinePreferenceOrDefault(string inlinePreference)
    {
        return TryParseInlinePreference(inlinePreference, out var parsed)
            ? parsed
            : InlinePreference.InlineHint;
    }

    private static Dictionary<string, string> BuildGenericTemplateBodyLookup(StarkPackageModuleManifest module)
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var template in module.EffectiveGenericTemplates?.Functions ?? [])
        {
            if (template.TypedBody is not null)
            {
                if (CanOmitBridgeBodyText(template.TypedBody))
                {
                    continue;
                }

                if (TryRenderGenericTemplateBody(template, out var renderedBodyText))
                {
                    templates[BuildGenericTemplateLookupKey(template.QualifiedName, template.OverloadKey)] = renderedBodyText;
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(template.BodyText))
            {
                templates[BuildGenericTemplateLookupKey(template.QualifiedName, template.OverloadKey)] = template.BodyText;
            }
        }

        return templates;
    }

    private static Dictionary<string, string> BuildRenderableGenericTemplateBodyLookup(StarkPackageModuleManifest module)
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var template in module.EffectiveGenericTemplates?.Functions ?? [])
        {
            if (template.TypedBody is not null)
            {
                if (CanOmitBridgeBodyText(template.TypedBody))
                {
                    continue;
                }

                if (TryRenderGenericTemplateBody(template, out var renderedBodyText))
                {
                    templates[BuildGenericTemplateLookupKey(template.QualifiedName, template.OverloadKey)] = renderedBodyText;
                }

                continue;
            }

            if (!string.IsNullOrEmpty(template.BodyText))
            {
                templates[BuildGenericTemplateLookupKey(template.QualifiedName, template.OverloadKey)] = template.BodyText;
            }
        }

        return templates;
    }

    private static bool TryRenderGenericTemplateBody(
        StarkPackageFunctionTemplateManifest template,
        out string bodyText)
    {
        bodyText = string.Empty;

        if (template.TypedBody is null
            || !TryBuildImportedTypedTemplateBody(template.TypedBody, out var typedBody))
        {
            return false;
        }

        var objectCreationsByOrdinal = (template.ObjectCreations ?? [])
            .Select((item, ordinal) => (item, ordinal))
            .ToDictionary(static item => item.ordinal, static item => item.item);
        var enumConstructorsByOrdinal = (template.EnumConstructors ?? [])
            .ToDictionary(static item => item.Ordinal);
        var enumCallsByOrdinal = (template.EnumCalls ?? [])
            .ToDictionary(static item => item.Ordinal);
        var enumValuesByOrdinal = (template.EnumValues ?? [])
            .ToDictionary(static item => item.Ordinal);
        var enumPatternsByOrdinal = (template.EnumPatterns ?? [])
            .ToDictionary(static item => item.Ordinal);
        var aggregatePatternsByOrdinal = (template.AggregatePatterns ?? [])
            .ToDictionary(static item => item.Ordinal);
        var directCallsByOrdinal = (template.DirectCalls ?? [])
            .ToDictionary(static item => item.Ordinal);
        var fieldAccessesByOrdinal = (template.FieldAccesses ?? [])
            .ToDictionary(static item => item.Ordinal);
        var memberCallsByOrdinal = (template.MemberCalls ?? [])
            .ToDictionary(static item => item.Ordinal);

        var builder = new StringBuilder();
        if (!TryRenderImportedTypedTemplateBody(
                builder,
                typedBody,
                objectCreationsByOrdinal,
                enumConstructorsByOrdinal,
                enumCallsByOrdinal,
                enumValuesByOrdinal,
                enumPatternsByOrdinal,
                aggregatePatternsByOrdinal,
                directCallsByOrdinal,
                fieldAccessesByOrdinal,
                memberCallsByOrdinal))
        {
            return false;
        }

        bodyText = builder.ToString();
        return true;
    }

    private static bool TryRenderImportedTypedTemplateBody(
        StringBuilder builder,
        ImportedTemplateTypedBodySummary typedBody,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumPatternManifest> enumPatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateAggregatePatternManifest> aggregatePatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal)
    {
        builder.AppendLine("{");
        if (!TryRenderImportedTypedTemplateStatementList(
                builder,
                typedBody.Statements,
                objectCreationsByOrdinal,
                enumConstructorsByOrdinal,
                enumCallsByOrdinal,
                enumValuesByOrdinal,
                enumPatternsByOrdinal,
                aggregatePatternsByOrdinal,
                directCallsByOrdinal,
                fieldAccessesByOrdinal,
                memberCallsByOrdinal,
                indentLevel: 1))
        {
            return false;
        }

        builder.AppendLine("}");
        return true;
    }

    private static bool TryRenderImportedTypedTemplateStatementList(
        StringBuilder builder,
        IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> statements,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumPatternManifest> enumPatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateAggregatePatternManifest> aggregatePatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal,
        int indentLevel)
    {
        foreach (var statement in statements)
        {
            if (!TryRenderImportedTypedTemplateStatement(
                    builder,
                    statement,
                    objectCreationsByOrdinal,
                    enumConstructorsByOrdinal,
                    enumCallsByOrdinal,
                    enumValuesByOrdinal,
                    enumPatternsByOrdinal,
                    aggregatePatternsByOrdinal,
                    directCallsByOrdinal,
                    fieldAccessesByOrdinal,
                    memberCallsByOrdinal,
                    indentLevel))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryRenderImportedTypedTemplateStatement(
        StringBuilder builder,
        ImportedTemplateTypedBodyStatementSummary statement,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumPatternManifest> enumPatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateAggregatePatternManifest> aggregatePatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal,
        int indentLevel)
    {
        switch (statement.Kind)
        {
            case ImportedTemplateTypedBodyStatementKind.Block:
                AppendIndent(builder, indentLevel);
                if (!TryRenderImportedTypedTemplateStatementBlock(
                        builder,
                        statement.Body,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        enumPatternsByOrdinal,
                        aggregatePatternsByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        indentLevel))
                {
                    return false;
                }

                builder.AppendLine();
                return true;

            case ImportedTemplateTypedBodyStatementKind.Empty:
                AppendIndent(builder, indentLevel);
                builder.AppendLine(";");
                return true;

            case ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration:
            case ImportedTemplateTypedBodyStatementKind.ExpressionStatement:
            case ImportedTemplateTypedBodyStatementKind.Assignment:
            case ImportedTemplateTypedBodyStatementKind.Return:
            case ImportedTemplateTypedBodyStatementKind.Break:
            case ImportedTemplateTypedBodyStatementKind.Continue:
                if (!TryRenderImportedTypedTemplateSimpleStatementText(
                        statement,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var simpleStatementText))
                {
                    return false;
                }

                AppendIndent(builder, indentLevel);
                builder.Append(simpleStatementText);
                builder.AppendLine(";");
                return true;

            case ImportedTemplateTypedBodyStatementKind.If:
                if (statement.Expression is null
                    || !TryRenderImportedTypedTemplateExpression(
                        statement.Expression,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var ifConditionText))
                {
                    return false;
                }

                AppendIndent(builder, indentLevel);
                builder.Append("if (");
                builder.Append(ifConditionText);
                builder.Append(") ");
                if (!TryRenderImportedTypedTemplateStatementBlock(
                        builder,
                        statement.ThenBranch,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        enumPatternsByOrdinal,
                        aggregatePatternsByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        indentLevel))
                {
                    return false;
                }

                if (statement.ElseBranch.Count > 0)
                {
                    builder.Append(" else ");
                    if (!TryRenderImportedTypedTemplateStatementBlock(
                            builder,
                            statement.ElseBranch,
                            objectCreationsByOrdinal,
                            enumConstructorsByOrdinal,
                            enumCallsByOrdinal,
                            enumValuesByOrdinal,
                            enumPatternsByOrdinal,
                            aggregatePatternsByOrdinal,
                            directCallsByOrdinal,
                            fieldAccessesByOrdinal,
                            memberCallsByOrdinal,
                            indentLevel))
                    {
                        return false;
                    }
                }

                builder.AppendLine();
                return true;

            case ImportedTemplateTypedBodyStatementKind.While:
                if (statement.Expression is null
                    || string.IsNullOrWhiteSpace(statement.LoopBehavior)
                    || !TryRenderImportedTypedTemplateExpression(
                        statement.Expression,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var whileConditionText))
                {
                    return false;
                }

                AppendIndent(builder, indentLevel);
                builder.Append("while ");
                builder.Append(statement.LoopBehavior);
                AppendLoopContracts(builder, statement.LoopContractNames);
                builder.Append(" (");
                builder.Append(whileConditionText);
                builder.Append(") ");
                if (!TryRenderImportedTypedTemplateStatementBlock(
                        builder,
                        statement.Body,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        enumPatternsByOrdinal,
                        aggregatePatternsByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        indentLevel))
                {
                    return false;
                }

                builder.AppendLine();
                return true;

            case ImportedTemplateTypedBodyStatementKind.For:
                if (string.IsNullOrWhiteSpace(statement.LoopBehavior)
                    || !TryRenderImportedTypedTemplateForHeaderText(
                        statement.Initializer,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var initializerText)
                    || !TryRenderImportedTypedTemplateForHeaderText(
                        statement.Iterator,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var iteratorText))
                {
                    return false;
                }

                AppendIndent(builder, indentLevel);
                builder.Append("for ");
                builder.Append(statement.LoopBehavior);
                AppendLoopContracts(builder, statement.LoopContractNames);
                builder.Append(" (");
                builder.Append(initializerText);
                builder.Append("; ");
                if (statement.Expression is not null)
                {
                    if (!TryRenderImportedTypedTemplateExpression(
                            statement.Expression,
                            objectCreationsByOrdinal,
                            enumConstructorsByOrdinal,
                            enumCallsByOrdinal,
                            enumValuesByOrdinal,
                            directCallsByOrdinal,
                            fieldAccessesByOrdinal,
                            memberCallsByOrdinal,
                            out var forConditionText))
                    {
                        return false;
                    }

                    builder.Append(forConditionText);
                }
                builder.Append("; ");
                builder.Append(iteratorText);
                builder.Append(") ");
                if (!TryRenderImportedTypedTemplateStatementBlock(
                        builder,
                        statement.Body,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        enumPatternsByOrdinal,
                        aggregatePatternsByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        indentLevel))
                {
                    return false;
                }

                builder.AppendLine();
                return true;

            default:
                return false;
        }
    }

    private static bool TryRenderImportedTypedTemplateStatementBlock(
        StringBuilder builder,
        IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> statements,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumPatternManifest> enumPatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateAggregatePatternManifest> aggregatePatternsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal,
        int indentLevel)
    {
        builder.AppendLine("{");
        if (!TryRenderImportedTypedTemplateStatementList(
                builder,
                statements,
                objectCreationsByOrdinal,
                enumConstructorsByOrdinal,
                enumCallsByOrdinal,
                enumValuesByOrdinal,
                enumPatternsByOrdinal,
                aggregatePatternsByOrdinal,
                directCallsByOrdinal,
                fieldAccessesByOrdinal,
                memberCallsByOrdinal,
                indentLevel + 1))
        {
            return false;
        }

        AppendIndent(builder, indentLevel);
        builder.Append('}');
        return true;
    }

    private static bool TryRenderImportedTypedTemplateSimpleStatementText(
        ImportedTemplateTypedBodyStatementSummary statement,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal,
        out string text)
    {
        text = string.Empty;

        switch (statement.Kind)
        {
            case ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration:
                if (statement.Name is null || statement.Type is null || statement.Expression is null)
                {
                    return false;
                }

                if (!TryRenderImportedTypedTemplateExpression(
                        statement.Expression,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var initializerText))
                {
                    return false;
                }

                var localBuilder = new StringBuilder();
                if (statement.IsConstant)
                {
                    localBuilder.Append("const ");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(statement.StorageClass))
                    {
                        return false;
                    }

                    localBuilder.Append(statement.StorageClass);
                    localBuilder.Append(' ');
                    if (statement.IsMutable)
                    {
                        localBuilder.Append("mut ");
                    }
                }

                localBuilder.Append(statement.IsConstant
                    ? RenderConstLocalType(statement.Type)
                    : statement.Type.DisplayName);
                localBuilder.Append(' ');
                localBuilder.Append(statement.Name);
                localBuilder.Append(" = ");
                localBuilder.Append(initializerText);
                text = localBuilder.ToString();
                return true;

            case ImportedTemplateTypedBodyStatementKind.ExpressionStatement:
                return statement.Expression is not null
                    && TryRenderImportedTypedTemplateExpression(
                        statement.Expression,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out text);

            case ImportedTemplateTypedBodyStatementKind.Assignment:
                if (statement.Expression is null
                    || (statement.Name is null && statement.TargetExpression is null)
                    || !TryRenderImportedTypedTemplateExpression(
                        statement.Expression,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var assignmentValueText))
                {
                    return false;
                }

                var assignmentTargetText = statement.TargetExpression is null
                    ? statement.Name!
                    : TryRenderImportedTypedTemplateExpression(
                        statement.TargetExpression,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var targetText)
                        ? targetText
                        : null;
                if (assignmentTargetText is null)
                {
                    return false;
                }

                var assignmentOperator = string.IsNullOrEmpty(statement.AssignmentOperator)
                    ? "="
                    : statement.AssignmentOperator;
                text = string.Equals(assignmentOperator, "init =", StringComparison.Ordinal)
                    ? $"init {assignmentTargetText} = {assignmentValueText}"
                    : $"{assignmentTargetText} {assignmentOperator} {assignmentValueText}";
                return true;

            case ImportedTemplateTypedBodyStatementKind.Return:
                if (statement.Expression is null)
                {
                    text = "return";
                    return true;
                }

                if (!TryRenderImportedTypedTemplateExpression(
                        statement.Expression,
                        objectCreationsByOrdinal,
                        enumConstructorsByOrdinal,
                        enumCallsByOrdinal,
                        enumValuesByOrdinal,
                        directCallsByOrdinal,
                        fieldAccessesByOrdinal,
                        memberCallsByOrdinal,
                        out var returnText))
                {
                    return false;
                }

                text = $"return {returnText}";
                return true;

            case ImportedTemplateTypedBodyStatementKind.Break:
                text = "break";
                return true;

            case ImportedTemplateTypedBodyStatementKind.Continue:
                text = "continue";
                return true;

            default:
                return false;
        }
    }

    private static bool TryRenderImportedTypedTemplateForHeaderText(
        IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> statements,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal,
        out string text)
    {
        if (statements.Count == 0)
        {
            text = string.Empty;
            return true;
        }

        var parts = new List<string>(statements.Count);
        foreach (var statement in statements)
        {
            if (statement.Kind is not ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration
                and not ImportedTemplateTypedBodyStatementKind.ExpressionStatement
                and not ImportedTemplateTypedBodyStatementKind.Assignment
                || !TryRenderImportedTypedTemplateSimpleStatementText(
                    statement,
                    objectCreationsByOrdinal,
                    enumConstructorsByOrdinal,
                    enumCallsByOrdinal,
                    enumValuesByOrdinal,
                    directCallsByOrdinal,
                    fieldAccessesByOrdinal,
                    memberCallsByOrdinal,
                    out var part))
            {
                text = string.Empty;
                return false;
            }

            parts.Add(part);
        }

        text = string.Join(", ", parts);
        return true;
    }

    private static bool TryRenderImportedTypedTemplateExpression(
        ImportedTemplateTypedBodyExpressionSummary expression,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal,
        out string text)
    {
        text = expression.Kind switch
        {
            ImportedTemplateTypedBodyExpressionKind.NameReference => expression.Name ?? string.Empty,
            ImportedTemplateTypedBodyExpressionKind.Literal => expression.LiteralText ?? string.Empty,
            ImportedTemplateTypedBodyExpressionKind.ArrayInitializer => expression.Args.Count == 0
                ? "{ }"
                : $"{{ {string.Join(", ", expression.Args.Select(argument => RenderImportedTypedTemplateExpression(argument, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)))} }}",
            ImportedTemplateTypedBodyExpressionKind.ObjectInitializer => RenderImportedTypedTemplateObjectInitializer(
                expression,
                objectCreationsByOrdinal,
                enumConstructorsByOrdinal,
                enumCallsByOrdinal,
                enumValuesByOrdinal,
                directCallsByOrdinal,
                fieldAccessesByOrdinal,
                memberCallsByOrdinal),
            ImportedTemplateTypedBodyExpressionKind.Assignment => RenderImportedTypedTemplateAssignmentExpression(
                expression,
                objectCreationsByOrdinal,
                enumConstructorsByOrdinal,
                enumCallsByOrdinal,
                enumValuesByOrdinal,
                directCallsByOrdinal,
                fieldAccessesByOrdinal,
                memberCallsByOrdinal),
            ImportedTemplateTypedBodyExpressionKind.Conversion => expression.Type is { } conversionType && expression.Args.Count == 1
                ? $"({conversionType.DisplayName}){RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.UnaryOperation => expression.Name is { } unaryOperator && expression.Args.Count == 1
                ? $"{unaryOperator}{RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.BinaryOperation => expression.Name is { } binaryOperator && expression.Args.Count == 2
                ? $"{RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)} {binaryOperator} {RenderImportedTypedTemplateExpression(expression.Args[1], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.ComparisonChain => RenderImportedTypedTemplateComparisonChain(
                expression,
                objectCreationsByOrdinal,
                enumConstructorsByOrdinal,
                enumCallsByOrdinal,
                enumValuesByOrdinal,
                directCallsByOrdinal,
                fieldAccessesByOrdinal,
                memberCallsByOrdinal),
            ImportedTemplateTypedBodyExpressionKind.Conditional => expression.Args.Count == 3
                ? $"{RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)} ? {RenderImportedTypedTemplateExpression(expression.Args[1], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)} : {RenderImportedTypedTemplateExpression(expression.Args[2], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.TypeLayout => expression.Type is not null && expression.Name is not null
                ? $"{expression.Name}({expression.Type.DisplayName})"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.ObjectCreation => expression.Ordinal is { } objectCreationOrdinal && objectCreationsByOrdinal.TryGetValue(objectCreationOrdinal, out var objectCreation)
                ? RenderObjectCreation(objectCreation, expression, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.EnumConstructor => expression.Ordinal is { } enumConstructorOrdinal && enumConstructorsByOrdinal.TryGetValue(enumConstructorOrdinal, out var enumConstructor)
                ? $"{RenderTypeReference(enumConstructor.EnumType)}.{enumConstructor.VariantName}({string.Join(", ", expression.Args.Select(argument => RenderImportedTypedTemplateExpression(argument, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)))})"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.EnumCall => expression.Ordinal is { } enumCallOrdinal && enumCallsByOrdinal.TryGetValue(enumCallOrdinal, out var enumCall)
                ? $"{RenderTypeReference(enumCall.EnumType)}.{enumCall.VariantName}({string.Join(", ", expression.Args.Select(argument => RenderImportedTypedTemplateExpression(argument, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)))})"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.EnumValue => expression.Ordinal is { } enumValueOrdinal && enumValuesByOrdinal.TryGetValue(enumValueOrdinal, out var enumValue)
                ? $"{RenderTypeReference(enumValue.EnumType)}.{enumValue.VariantName}"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.DirectCall => expression.Ordinal is { } directCallOrdinal && directCallsByOrdinal.TryGetValue(directCallOrdinal, out var directCall)
                ? RenderDirectCall(directCall, expression, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.IndexAccess => expression.Args.Count >= 1
                ? $"{RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}[{string.Join(", ", expression.Args.Skip(1).Select(argument => RenderImportedTypedTemplateExpression(argument, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)))}]"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.FieldAccess => expression.Ordinal is { } fieldOrdinal
                && fieldAccessesByOrdinal.TryGetValue(fieldOrdinal, out var fieldAccess)
                ? $"{RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}.{fieldAccess.FieldName}"
                : string.Empty,
            ImportedTemplateTypedBodyExpressionKind.MemberCall => expression.Ordinal is { } memberCallOrdinal && memberCallsByOrdinal.TryGetValue(memberCallOrdinal, out var memberCall)
                ? $"{RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}.{GetMemberCallName(memberCall)}({string.Join(", ", expression.Args.Skip(1).Select(argument => RenderImportedTypedTemplateExpression(argument, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)))})"
                : string.Empty,
            _ => string.Empty
        };

        return !string.IsNullOrEmpty(text);
    }

    private static string RenderImportedTypedTemplateObjectInitializer(
        ImportedTemplateTypedBodyExpressionSummary expression,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal)
    {
        if (expression.Members.Count != expression.Args.Count)
        {
            return string.Empty;
        }

        var parts = new string[expression.Members.Count];
        for (var index = 0; index < expression.Members.Count; index++)
        {
            parts[index] =
                $"{expression.Members[index]} = {RenderImportedTypedTemplateExpression(expression.Args[index], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}";
        }

        return $"{{ {string.Join(", ", parts)} }}";
    }

    private static string RenderImportedTypedTemplateAssignmentExpression(
        ImportedTemplateTypedBodyExpressionSummary expression,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal)
    {
        if (expression.Args.Count != 1)
        {
            return string.Empty;
        }

        var targetText = expression.TargetExpression is not null
            ? RenderImportedTypedTemplateExpression(expression.TargetExpression, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)
            : expression.Name;
        if (string.IsNullOrEmpty(targetText))
        {
            return string.Empty;
        }

        var assignmentOperator = string.IsNullOrEmpty(expression.AssignmentOperator)
            ? "="
            : expression.AssignmentOperator;
        var valueText = RenderImportedTypedTemplateExpression(expression.Args[0], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal);
        return string.Equals(assignmentOperator, "init =", StringComparison.Ordinal)
            ? $"init {targetText} = {valueText}"
            : $"{targetText} {assignmentOperator} {valueText}";
    }

    private static string RenderImportedTypedTemplateComparisonChain(
        ImportedTemplateTypedBodyExpressionSummary expression,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal)
    {
        if (expression.Args.Count < 2 || expression.Operators.Count != expression.Args.Count - 1)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(
            RenderImportedTypedTemplateExpression(
                expression.Args[0],
                objectCreationsByOrdinal,
                enumConstructorsByOrdinal,
                enumCallsByOrdinal,
                enumValuesByOrdinal,
                directCallsByOrdinal,
                fieldAccessesByOrdinal,
                memberCallsByOrdinal));

        for (var index = 0; index < expression.Operators.Count; index++)
        {
            builder.Append(' ');
            builder.Append(expression.Operators[index]);
            builder.Append(' ');
            builder.Append(
                RenderImportedTypedTemplateExpression(
                    expression.Args[index + 1],
                    objectCreationsByOrdinal,
                    enumConstructorsByOrdinal,
                    enumCallsByOrdinal,
                    enumValuesByOrdinal,
                    directCallsByOrdinal,
                    fieldAccessesByOrdinal,
                    memberCallsByOrdinal));
        }

        return builder.ToString();
    }

    private static string RenderImportedTypedTemplateExpression(
        ImportedTemplateTypedBodyExpressionSummary expression,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal)
    {
        return TryRenderImportedTypedTemplateExpression(
            expression,
            objectCreationsByOrdinal,
            enumConstructorsByOrdinal,
            enumCallsByOrdinal,
            enumValuesByOrdinal,
            directCallsByOrdinal,
            fieldAccessesByOrdinal,
            memberCallsByOrdinal,
            out var text)
            ? text
            : string.Empty;
    }

    private static string RenderObjectCreation(
        StarkPackageTemplateObjectCreationManifest objectCreation,
        ImportedTemplateTypedBodyExpressionSummary expression,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal)
    {
        var arguments = string.Join(", ", expression.Args.Select(argument => RenderImportedTypedTemplateExpression(argument, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)));

        if (objectCreation.Constructor is not null)
        {
            return $"new {RenderTypeReference(objectCreation.CreatedType)}({arguments})";
        }

        if (objectCreation.InitializerMembers is { Count: > 0 })
        {
            return $"new {RenderTypeReference(objectCreation.CreatedType)}() {{ {string.Join(", ", objectCreation.InitializerMembers.Select((member, index) => $"{member.FieldName} = {RenderImportedTypedTemplateExpression(expression.Args[index], objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)}"))} }}";
        }

        return $"new {RenderTypeReference(objectCreation.CreatedType)}({arguments})";
    }

    private static string RenderDirectCall(
        StarkPackageTemplateDirectCallManifest directCall,
        ImportedTemplateTypedBodyExpressionSummary expression,
        IReadOnlyDictionary<int, StarkPackageTemplateObjectCreationManifest> objectCreationsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumConstructorManifest> enumConstructorsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumCallManifest> enumCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateEnumValueManifest> enumValuesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateDirectCallManifest> directCallsByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateFieldAccessManifest> fieldAccessesByOrdinal,
        IReadOnlyDictionary<int, StarkPackageTemplateMemberCallManifest> memberCallsByOrdinal)
    {
        var target = directCall.QualifiedSourceName ?? directCall.QualifiedTemplateName ?? directCall.QualifiedResolvedName;
        if (directCall.TypeArguments is { Count: > 0 })
        {
            target = $"{target}<{string.Join(", ", directCall.TypeArguments.Select(RenderTypeReference))}>";
        }

        return $"{target}({string.Join(", ", expression.Args.Select(argument => RenderImportedTypedTemplateExpression(argument, objectCreationsByOrdinal, enumConstructorsByOrdinal, enumCallsByOrdinal, enumValuesByOrdinal, directCallsByOrdinal, fieldAccessesByOrdinal, memberCallsByOrdinal)))})";
    }

    private static void AppendLoopContracts(StringBuilder builder, IReadOnlyList<string> loopContracts)
    {
        foreach (var loopContract in loopContracts)
        {
            if (string.IsNullOrWhiteSpace(loopContract))
            {
                continue;
            }

            builder.Append(' ');
            builder.Append(loopContract);
        }
    }

    private static void AppendIndent(StringBuilder builder, int indentLevel)
    {
        for (var index = 0; index < indentLevel; index++)
        {
            builder.Append("    ");
        }
    }

    private static string GetMemberCallName(StarkPackageTemplateMemberCallManifest memberCall)
    {
        var sourceName = memberCall.QualifiedSourceName ?? memberCall.QualifiedResolvedName;
        var lastDot = sourceName.LastIndexOf('.');
        return lastDot >= 0 ? sourceName[(lastDot + 1)..] : sourceName;
    }

    private static bool CanOmitBridgeBodyText(StarkPackageTypedTemplateBodyManifest typedBody)
    {
        return typedBody.Statements.All(CanOmitBridgeBodyText);
    }

    private static bool CanOmitBridgeBodyText(StarkPackageTypedTemplateStatementManifest statement)
    {
        if (statement.Expression is not null
            && !CanOmitBridgeBodyText(statement.Expression))
        {
            return false;
        }

        if (statement.TargetExpression is not null
            && !CanOmitBridgeBodyText(statement.TargetExpression))
        {
            return false;
        }

        return (statement.BodyStatements ?? []).All(CanOmitBridgeBodyText)
            && (statement.InitializerStatements ?? []).All(CanOmitBridgeBodyText)
            && (statement.IteratorStatements ?? []).All(CanOmitBridgeBodyText)
            && (statement.SwitchCases ?? []).All(static switchCase => (switchCase.Statements ?? []).All(CanOmitBridgeBodyText))
            && (statement.ThenStatements ?? []).All(CanOmitBridgeBodyText)
            && (statement.ElseStatements ?? []).All(CanOmitBridgeBodyText);
    }

    private static bool CanOmitBridgeBodyText(StarkPackageTypedTemplateExpressionManifest expression)
    {
        return (expression.Arguments ?? []).All(CanOmitBridgeBodyText)
            && (expression.TargetExpression is null || CanOmitBridgeBodyText(expression.TargetExpression));
    }

    private static bool TryGetGenericTemplateBody(
        IReadOnlyDictionary<string, string> genericTemplateBodies,
        IReadOnlyDictionary<string, string> publishedOverloadKeysBySymbol,
        string qualifiedName,
        string symbolName,
        IReadOnlyList<StarkPackageParameterManifest> parameters,
        out string? bodyText)
    {
        if (publishedOverloadKeysBySymbol.TryGetValue(symbolName, out var publishedOverloadKey)
            && genericTemplateBodies.TryGetValue(
                BuildGenericTemplateLookupKey(qualifiedName, publishedOverloadKey),
                out bodyText))
        {
            return true;
        }

        return TryGetGenericTemplateBody(
            genericTemplateBodies,
            qualifiedName,
            parameters,
            out bodyText);
    }

    private static bool HasGenericTemplateBody(
        StarkPackageModuleManifest module,
        IReadOnlyDictionary<string, string> publishedOverloadKeysBySymbol,
        string qualifiedName,
        string symbolName,
        IReadOnlyList<StarkPackageTypedParameterManifest> parameters)
    {
        var genericTemplateBodies = BuildGenericTemplateBodyLookup(module);
        return publishedOverloadKeysBySymbol.TryGetValue(symbolName, out var publishedOverloadKey)
            && genericTemplateBodies.ContainsKey(BuildGenericTemplateLookupKey(qualifiedName, publishedOverloadKey))
            || TryGetGenericTemplateBody(
                genericTemplateBodies,
                qualifiedName,
                parameters,
                out _);
    }

    private static bool TryGetGenericTemplateBody(
        IReadOnlyDictionary<string, string> genericTemplateBodies,
        IReadOnlyDictionary<string, string> publishedOverloadKeysBySymbol,
        string qualifiedName,
        string symbolName,
        IReadOnlyList<StarkPackageTypedParameterManifest> parameters,
        out string? bodyText)
    {
        if (publishedOverloadKeysBySymbol.TryGetValue(symbolName, out var publishedOverloadKey)
            && genericTemplateBodies.TryGetValue(
                BuildGenericTemplateLookupKey(qualifiedName, publishedOverloadKey),
                out bodyText))
        {
            return true;
        }

        return TryGetGenericTemplateBody(
            genericTemplateBodies,
            qualifiedName,
            parameters,
            out bodyText);
    }

    private static bool TryGetGenericTemplateBody(
        IReadOnlyDictionary<string, string> genericTemplateBodies,
        string qualifiedName,
        IReadOnlyList<StarkPackageTypedParameterManifest> parameters,
        out string? bodyText)
    {
        return TryGetGenericTemplateBody(
            genericTemplateBodies,
            qualifiedName,
            parameters.Select(static parameter => RenderTypeReference(parameter.Type)),
            out bodyText);
    }

    private static bool TryGetGenericTemplateBody(
        IReadOnlyDictionary<string, string> genericTemplateBodies,
        string qualifiedName,
        IReadOnlyList<StarkPackageParameterManifest> parameters,
        out string? bodyText)
    {
        return TryGetGenericTemplateBody(
            genericTemplateBodies,
            qualifiedName,
            parameters.Select(static parameter => parameter.Type),
            out bodyText);
    }

    private static bool TryGetGenericTemplateBody(
        IReadOnlyDictionary<string, string> genericTemplateBodies,
        string qualifiedName,
        IEnumerable<string> parameterTypes,
        out string? bodyText)
    {
        return genericTemplateBodies.TryGetValue(
            BuildGenericTemplateLookupKey(
                qualifiedName,
                FunctionOverloadFacts.BuildOverloadKey(parameterTypes)),
            out bodyText);
    }

    private static string BuildGenericTemplateLookupKey(string qualifiedName, string overloadKey)
    {
        return $"{qualifiedName}#{overloadKey}";
    }

    private static AsmFunctionModel? CreateAsmModel(StarkPackageAsmManifest? asm)
    {
        if (asm is null)
        {
            return null;
        }

        var architecture = StarkAsmArchitectureFacts.TryParseArchitectureName(asm.ArchitectureText, out var parsedArchitecture)
            ? parsedArchitecture
            : StarkAsmArchitecture.Unknown;

        return new AsmFunctionModel(
            architecture,
            asm.ArchitectureText,
            asm.TemplateText,
            asm.Inputs.Select(static input => new AsmInputOperandModel(input.RegisterName, input.ValueName)).ToArray(),
            asm.Outputs.Select(static output => new AsmOutputOperandModel(output.RegisterName, output.ValueName, output.BindsReturnValue)).ToArray(),
            asm.Clobbers);
    }

    private static bool TryParseVisibility(string visibility, out StarkVisibility parsed)
    {
        switch (visibility)
        {
            case "module":
                parsed = StarkVisibility.Module;
                return true;
            case "internal":
                parsed = StarkVisibility.Internal;
                return true;
            case "public":
                parsed = StarkVisibility.Public;
                return true;
            case "export":
                parsed = StarkVisibility.Export;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseTypeDeclarationKind(string kind, out DeclarationKind parsed)
    {
        switch (kind)
        {
            case "struct":
                parsed = DeclarationKind.Struct;
                return true;
            case "record":
                parsed = DeclarationKind.Record;
                return true;
            case "enum":
                parsed = DeclarationKind.Enum;
                return true;
            case "trait":
                parsed = DeclarationKind.Trait;
                return true;
            case "doctrine":
                parsed = DeclarationKind.Doctrine;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseGlobalDeclarationKind(string kind, out DeclarationKind parsed)
    {
        switch (kind)
        {
            case "globalconstant":
                parsed = DeclarationKind.GlobalConstant;
                return true;
            case "globalvariable":
                parsed = DeclarationKind.GlobalVariable;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseFunctionKind(string kind, out StarkFunctionKind parsed)
    {
        switch (kind)
        {
            case "fn":
                parsed = StarkFunctionKind.Fn;
                return true;
            case "finite":
                parsed = StarkFunctionKind.Finite;
                return true;
            case "law":
                parsed = StarkFunctionKind.Law;
                return true;
            case "finitelaw":
                parsed = StarkFunctionKind.FiniteLaw;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseInlinePreference(string inlinePreference, out InlinePreference parsed)
    {
        switch (inlinePreference)
        {
            case "inline":
                parsed = InlinePreference.Inline;
                return true;
            case "noinline":
                parsed = InlinePreference.NoInline;
                return true;
            case "inlinehint":
                parsed = InlinePreference.InlineHint;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseAbiParameterKind(string kind, out AbiParameterKind parsed)
    {
        switch (kind)
        {
            case "direct":
                parsed = AbiParameterKind.Direct;
                return true;
            case "indirectin":
                parsed = AbiParameterKind.IndirectIn;
                return true;
            case "sret":
                parsed = AbiParameterKind.SRet;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseEnumLayoutKind(string kind, out EnumLayoutKind parsed)
    {
        switch (kind)
        {
            case "directtag":
                parsed = EnumLayoutKind.DirectTag;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseParameterCaptureKind(string kind, out ParameterCaptureKind parsed)
    {
        switch (kind)
        {
            case "none":
                parsed = ParameterCaptureKind.None;
                return true;
            case "return":
                parsed = ParameterCaptureKind.Return;
                return true;
            case "escape":
                parsed = ParameterCaptureKind.Escape;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static StarkPackageFunctionManifest ConvertFunctionManifest(StarkPackageTypedFunctionManifest function)
    {
        return new StarkPackageFunctionManifest(
            function.Name,
            function.QualifiedName,
            function.Visibility,
            function.SymbolName,
            function.Kind,
            RenderTypeReference(function.ReturnType),
            function.Parameters
                .Select(parameter => new StarkPackageParameterManifest(
                    parameter.Name,
                    RenderParameterType(parameter),
                    parameter.IsDisjoint,
                    parameter.IsConst,
                    parameter.RawPointerElementCountExpression))
                .ToArray(),
            function.IsFfi,
            function.IsStrictFp,
            function.UseFastCallingConvention,
            function.Asm,
            function.GenericParameters,
            function.IsHot,
            function.IsCold,
            function.InlinePreference,
            function.HasExplicitInlinePreference,
            function.IsUnsafe,
            function.IsVarargs,
            function.BackendOptimizationMode,
            DisjointParameterGroups: function.DisjointParameterGroups);
    }

    private static StarkPackageTypeManifest ConvertTypeManifest(StarkPackageTypedTypeManifest type)
    {
        return new StarkPackageTypeManifest(
            type.Name,
            type.QualifiedName,
            type.Visibility,
            type.Kind,
            type.Fields
                .Select(field => new StarkPackageFieldManifest(field.Name, RenderTypeReference(field.Type), field.Visibility))
                .ToArray(),
            type.GenericParameters,
            type.PrimaryConstructorParameters?.Select(parameter => new StarkPackageParameterManifest(
                parameter.Name,
                RenderParameterType(parameter),
                parameter.IsDisjoint,
                parameter.IsConst,
                parameter.RawPointerElementCountExpression))
                .ToArray(),
            type.Variants?.Select(variant => new StarkPackageEnumVariantManifest(
                variant.Name,
                variant.UsesNamedFields,
                variant.Fields
                    .Select(field => new StarkPackageFieldManifest(field.Name, RenderTypeReference(field.Type)))
                    .ToArray()))
                .ToArray(),
            type.Methods?.Select(method => new StarkPackageMethodManifest(
                method.Name,
                method.QualifiedName,
                method.SymbolName,
                method.Kind,
                RenderTypeReference(method.ReturnType),
                method.Parameters
                    .Select(parameter => new StarkPackageParameterManifest(
                        parameter.Name,
                        RenderParameterType(parameter),
                        parameter.IsDisjoint,
                        parameter.IsConst,
                        parameter.RawPointerElementCountExpression))
                    .ToArray(),
                method.IsFfi,
                method.IsStrictFp,
                method.UseFastCallingConvention,
                method.GenericParameters,
                method.IsHot,
                method.IsCold,
                method.InlinePreference,
                method.HasExplicitInlinePreference,
                method.IsStatic,
                method.Visibility,
                method.IsUnsafe,
                method.IsVarargs,
                method.BackendOptimizationMode,
                DisjointParameterGroups: method.DisjointParameterGroups))
                .ToArray(),
            type.Destructor,
            BackendOptimizationMode: type.BackendOptimizationMode);
    }

    private static StarkPackageGlobalManifest ConvertGlobalManifest(StarkPackageTypedGlobalManifest global)
    {
        return new StarkPackageGlobalManifest(
            global.Name,
            global.QualifiedName,
            global.Visibility,
            global.Kind,
            RenderTypeReference(global.Type),
            global.IsMutable);
    }

    private static StarkPackageTypeAliasManifest ConvertTypeAliasManifest(StarkPackageTypedTypeAliasManifest typeAlias)
    {
        return new StarkPackageTypeAliasManifest(
            typeAlias.Name,
            typeAlias.QualifiedName,
            typeAlias.Visibility,
            RenderTypeReference(typeAlias.TargetType),
            typeAlias.GenericParameters);
    }

    private static string RenderConstGlobalType(string typeText)
    {
        var rangeStart = typeText.IndexOf('[');
        if (rangeStart <= 1
            || typeText[0] is not ('i' or 'u')
            || !typeText[..rangeStart].Skip(1).All(char.IsDigit)
            || typeText.IndexOf(']', rangeStart + 1) != typeText.Length - 1)
        {
            return typeText;
        }

        return typeText[..rangeStart];
    }

    private static string RenderConstLocalType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Integer
            && type.BitWidth is int width
            && type.ElementType is null
            ? $"{(type.IsUnsigned ? "u" : "i")}{width}"
            : type.DisplayName;
    }

}
