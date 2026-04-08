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
        var sourceSurfaceOverloadKeysBySymbol = BuildSourceSurfaceOverloadKeyLookup(module.Module);
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
                builder.Append(string.Join(", ", type.PrimaryConstructorParameters.Select(static parameter => $"{parameter.Type} {parameter.Name}")));
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
                    builder.Append(field.Type);
                    builder.Append(' ');
                    builder.Append(field.Name);
                    builder.AppendLine(";");
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
                    TryGetGenericTemplateBody(
                        genericTemplateBodies,
                        sourceSurfaceOverloadKeysBySymbol,
                        $"{module.Module.ModuleName}.{type.Name}.{method.Name}",
                        method.SymbolName,
                        method.Parameters,
                        out var methodBodyText);
                    builder.Append("    ");
                    if (method.IsFfi)
                    {
                        builder.Append("ffi ");
                    }

                    if (method.IsStrictFp)
                    {
                        builder.Append("strictfp ");
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
                    builder.Append(string.Join(", ", method.Parameters.Select(static parameter => $"{parameter.Type} {parameter.Name}")));
                    builder.Append(')');
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

        if (globals.Count > 0)
        {
            builder.AppendLine();
        }

        foreach (var function in functions.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            TryGetGenericTemplateBody(
                genericTemplateBodies,
                sourceSurfaceOverloadKeysBySymbol,
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
            return typedInterface.Types.Select(ConvertTypeManifest).ToArray();
        }

        return module.EffectiveSourceSurface.Types ?? [];
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

    private static Dictionary<string, string> BuildSourceSurfaceOverloadKeyLookup(StarkPackageModuleManifest module)
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

    private static void EmitFunction(StringBuilder builder, StarkPackageFunctionManifest function, string? bodyText = null)
    {
        builder.Append(function.Visibility);
        builder.Append(' ');
        if (function.IsFfi)
        {
            builder.Append("ffi ");
        }

        if (function.IsStrictFp)
        {
            builder.Append("strictfp ");
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
        builder.Append(string.Join(", ", function.Parameters.Select(static parameter => $"{parameter.Type} {parameter.Name}")));
        builder.Append(')');

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
        bool isStrictFp,
        StarkPackageAsmManifest? asm,
        IReadOnlyList<string>? genericParameters,
        bool hasBody = false,
        string? publishedOverloadKey = null)
    {
        return new FunctionDeclarationModel(
            Name: name,
            Kind: functionKind,
            ReturnType: returnType,
            Parameters: parameters
                .Select(parameter => new ParameterModel(parameter.Name, RenderTypeReference(parameter.Type)))
                .ToArray(),
            Modifiers: new FunctionModifierSet(
                InlinePreference.InlineHint,
                HasExplicitInlinePreference: false,
                IsHot: false,
                IsCold: false,
                IsFfi: isFfi,
                IsStrictFp: isStrictFp),
            HasBody: hasBody,
            Asm: CreateAsmModel(asm),
            GenericParameterNames: genericParameters ?? [],
            PublishedOverloadKey: publishedOverloadKey);
    }

    private static Dictionary<string, string> BuildGenericTemplateBodyLookup(StarkPackageModuleManifest module)
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var template in module.EffectiveGenericTemplates?.Functions ?? [])
        {
            templates[BuildGenericTemplateLookupKey(template.QualifiedName, template.OverloadKey)] = template.BodyText;
        }

        return templates;
    }

    private static Dictionary<string, string> BuildRenderableGenericTemplateBodyLookup(StarkPackageModuleManifest module)
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var template in module.EffectiveGenericTemplates?.Functions ?? [])
        {
            // For the supported typed template-body subset, the bridge can stay declaration-only.
            if (template.TypedBody is not null
                && CanOmitBridgeBodyText(template.TypedBody))
            {
                continue;
            }

            templates[BuildGenericTemplateLookupKey(template.QualifiedName, template.OverloadKey)] = template.BodyText;
        }

        return templates;
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
        return (expression.Arguments ?? []).All(CanOmitBridgeBodyText);
    }

    private static bool TryGetGenericTemplateBody(
        IReadOnlyDictionary<string, string> genericTemplateBodies,
        IReadOnlyDictionary<string, string> sourceSurfaceOverloadKeysBySymbol,
        string qualifiedName,
        string symbolName,
        IReadOnlyList<StarkPackageParameterManifest> parameters,
        out string? bodyText)
    {
        if (sourceSurfaceOverloadKeysBySymbol.TryGetValue(symbolName, out var sourceSurfaceOverloadKey)
            && genericTemplateBodies.TryGetValue(
                BuildGenericTemplateLookupKey(qualifiedName, sourceSurfaceOverloadKey),
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
        IReadOnlyDictionary<string, string> sourceSurfaceOverloadKeysBySymbol,
        string qualifiedName,
        string symbolName,
        IReadOnlyList<StarkPackageTypedParameterManifest> parameters)
    {
        var genericTemplateBodies = BuildGenericTemplateBodyLookup(module);
        return sourceSurfaceOverloadKeysBySymbol.TryGetValue(symbolName, out var sourceSurfaceOverloadKey)
            && genericTemplateBodies.ContainsKey(BuildGenericTemplateLookupKey(qualifiedName, sourceSurfaceOverloadKey))
            || TryGetGenericTemplateBody(
                genericTemplateBodies,
                qualifiedName,
                parameters,
                out _);
    }

    private static bool TryGetGenericTemplateBody(
        IReadOnlyDictionary<string, string> genericTemplateBodies,
        IReadOnlyDictionary<string, string> sourceSurfaceOverloadKeysBySymbol,
        string qualifiedName,
        string symbolName,
        IReadOnlyList<StarkPackageTypedParameterManifest> parameters,
        out string? bodyText)
    {
        if (sourceSurfaceOverloadKeysBySymbol.TryGetValue(symbolName, out var sourceSurfaceOverloadKey)
            && genericTemplateBodies.TryGetValue(
                BuildGenericTemplateLookupKey(qualifiedName, sourceSurfaceOverloadKey),
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
                .Select(parameter => new StarkPackageParameterManifest(parameter.Name, RenderTypeReference(parameter.Type)))
                .ToArray(),
            function.IsFfi,
            function.IsStrictFp,
            function.UseFastCallingConvention,
            function.Asm,
            function.GenericParameters);
    }

    private static StarkPackageTypeManifest ConvertTypeManifest(StarkPackageTypedTypeManifest type)
    {
        return new StarkPackageTypeManifest(
            type.Name,
            type.QualifiedName,
            type.Visibility,
            type.Kind,
            type.Fields
                .Select(field => new StarkPackageFieldManifest(field.Name, RenderTypeReference(field.Type)))
                .ToArray(),
            type.GenericParameters,
            type.PrimaryConstructorParameters?.Select(parameter => new StarkPackageParameterManifest(
                parameter.Name,
                RenderTypeReference(parameter.Type)))
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
                    .Select(parameter => new StarkPackageParameterManifest(parameter.Name, RenderTypeReference(parameter.Type)))
                    .ToArray(),
                method.IsFfi,
                method.IsStrictFp,
                method.UseFastCallingConvention,
                method.GenericParameters))
                .ToArray(),
            type.Destructor);
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

}
