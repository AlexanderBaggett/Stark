using Antlr4.Runtime;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stark.Parsing;

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
    IReadOnlyList<StarkPackageGlobalManifest> Globals,
    IReadOnlyList<StarkPackageTypeAliasManifest>? TypeAliases = null,
    StarkPackageTypedInterfaceSection? TypedInterface = null,
    StarkPackageCompilerFactsSection? CompilerFacts = null,
    StarkPackageGenericTemplateSection? GenericTemplates = null,
    IReadOnlyList<StarkPackageImportManifest>? Imports = null);

internal sealed record StarkPackageImportManifest(
    string ModuleName,
    bool IsExported);

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
    bool IsStrictFp,
    bool UseFastCallingConvention,
    StarkPackageAsmManifest? Asm = null,
    IReadOnlyList<string>? GenericParameters = null);

internal sealed record StarkPackageParameterManifest(
    string Name,
    string Type);

internal sealed record StarkPackageAsmManifest(
    string ArchitectureText,
    string TemplateText,
    IReadOnlyList<StarkPackageAsmInputManifest> Inputs,
    IReadOnlyList<StarkPackageAsmOutputManifest> Outputs,
    IReadOnlyList<string> Clobbers);

internal sealed record StarkPackageAsmInputManifest(
    string RegisterName,
    string ValueName);

internal sealed record StarkPackageAsmOutputManifest(
    string RegisterName,
    string ValueName,
    bool BindsReturnValue);

internal sealed record StarkPackageMethodManifest(
    string Name,
    string QualifiedName,
    string SymbolName,
    string Kind,
    string ReturnType,
    IReadOnlyList<StarkPackageParameterManifest> Parameters,
    bool IsFfi,
    bool IsStrictFp,
    bool UseFastCallingConvention,
    IReadOnlyList<string>? GenericParameters = null);

internal sealed record StarkPackageDestructorManifest(
    bool IsMutable,
    string BodyText);

internal sealed record StarkPackageTypeManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string Kind,
    IReadOnlyList<StarkPackageFieldManifest> Fields,
    IReadOnlyList<string>? GenericParameters = null,
    IReadOnlyList<StarkPackageParameterManifest>? PrimaryConstructorParameters = null,
    IReadOnlyList<StarkPackageEnumVariantManifest>? Variants = null,
    IReadOnlyList<StarkPackageMethodManifest>? Methods = null,
    StarkPackageDestructorManifest? Destructor = null);

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

internal sealed record StarkPackageTypeAliasManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string TargetType,
    IReadOnlyList<string>? GenericParameters = null);

internal sealed record StarkPackageTypedInterfaceSection(
    IReadOnlyList<StarkPackageTypedFunctionManifest> Functions,
    IReadOnlyList<StarkPackageTypedTypeManifest> Types,
    IReadOnlyList<StarkPackageTypedGlobalManifest> Globals,
    IReadOnlyList<StarkPackageTypedTypeAliasManifest>? TypeAliases = null);

internal sealed record StarkPackageTypeReference(
    string Kind,
    string? Name = null,
    int? BitWidth = null,
    string? RangeMin = null,
    string? RangeMax = null,
    bool IsMutablePointer = false,
    string? BorrowKind = null,
    string? AccessKind = null,
    string? InitializationKind = null,
    bool IsMutableView = false,
    int? FixedLength = null,
    StarkPackageTypeReference? ElementType = null,
    IReadOnlyList<StarkPackageTypeReference>? TypeArguments = null);

internal sealed record StarkPackageTypedParameterManifest(
    string Name,
    StarkPackageTypeReference Type);

internal sealed record StarkPackageTypedFieldManifest(
    string Name,
    StarkPackageTypeReference Type);

internal sealed record StarkPackageTypedFunctionManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string SymbolName,
    string Kind,
    StarkPackageTypeReference ReturnType,
    IReadOnlyList<StarkPackageTypedParameterManifest> Parameters,
    bool IsFfi,
    bool IsStrictFp,
    bool UseFastCallingConvention,
    StarkPackageAsmManifest? Asm = null,
    IReadOnlyList<string>? GenericParameters = null);

internal sealed record StarkPackageTypedMethodManifest(
    string Name,
    string QualifiedName,
    string SymbolName,
    string Kind,
    StarkPackageTypeReference ReturnType,
    IReadOnlyList<StarkPackageTypedParameterManifest> Parameters,
    bool IsFfi,
    bool IsStrictFp,
    bool UseFastCallingConvention,
    IReadOnlyList<string>? GenericParameters = null);

internal sealed record StarkPackageTypedEnumVariantManifest(
    string Name,
    bool UsesNamedFields,
    IReadOnlyList<StarkPackageTypedFieldManifest> Fields);

internal sealed record StarkPackageTypedTypeManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string Kind,
    IReadOnlyList<StarkPackageTypedFieldManifest> Fields,
    IReadOnlyList<string>? GenericParameters = null,
    IReadOnlyList<StarkPackageTypedParameterManifest>? PrimaryConstructorParameters = null,
    IReadOnlyList<StarkPackageTypedEnumVariantManifest>? Variants = null,
    IReadOnlyList<StarkPackageTypedMethodManifest>? Methods = null,
    StarkPackageDestructorManifest? Destructor = null);

internal sealed record StarkPackageTypedGlobalManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    string Kind,
    StarkPackageTypeReference Type,
    bool IsMutable);

internal sealed record StarkPackageTypedTypeAliasManifest(
    string Name,
    string QualifiedName,
    string Visibility,
    StarkPackageTypeReference TargetType,
    IReadOnlyList<string>? GenericParameters = null);

internal sealed record StarkPackageGenericTemplateSection(
    IReadOnlyList<StarkPackageFunctionTemplateManifest> Functions);

internal sealed record StarkPackageDeferredFunctionInstantiationManifest(
    string CalleeTemplateName,
    IReadOnlyList<StarkPackageTypeReference> TypeArguments);

internal sealed record StarkPackageDeferredTypeInstantiationManifest(
    StarkPackageTypeReference Type);

internal sealed record StarkPackagePublishedConstructorShapeManifest(
    string TypeName,
    IReadOnlyList<StarkPackageTypedParameterManifest> Parameters,
    bool IsPrimaryShape);

internal sealed record StarkPackageTemplateObjectInitializerMemberManifest(
    string FieldName,
    int FieldIndex,
    StarkPackageTypeReference FieldType);

internal sealed record StarkPackageTemplateObjectCreationManifest(
    StarkPackageTypeReference CreatedType,
    StarkPackagePublishedConstructorShapeManifest? Constructor,
    IReadOnlyList<StarkPackageTemplateObjectInitializerMemberManifest>? InitializerMembers = null);

internal sealed record StarkPackageTemplateEnumConstructorMemberManifest(
    string FieldName,
    int FieldIndex,
    StarkPackageTypeReference FieldType);

internal sealed record StarkPackageTemplateEnumConstructorManifest(
    int Ordinal,
    StarkPackageTypeReference EnumType,
    string VariantName,
    IReadOnlyList<StarkPackageTemplateEnumConstructorMemberManifest>? Members = null);

internal sealed record StarkPackageTemplateEnumCallManifest(
    int Ordinal,
    StarkPackageTypeReference EnumType,
    string VariantName);

internal sealed record StarkPackageTemplateEnumValueManifest(
    int Ordinal,
    StarkPackageTypeReference EnumType,
    string VariantName);

internal sealed record StarkPackageTemplateEnumPatternManifest(
    int Ordinal,
    StarkPackageTypeReference EnumType,
    string VariantName,
    IReadOnlyList<StarkPackageTemplateEnumPatternMemberManifest>? Members = null);

internal sealed record StarkPackageTemplateEnumPatternMemberManifest(
    string FieldName,
    int FieldIndex,
    StarkPackageTypeReference FieldType);

internal sealed record StarkPackageTemplateAggregatePatternManifest(
    int Ordinal,
    StarkPackageTypeReference Type);

internal sealed record StarkPackageTypedTemplateExpressionManifest(
    string Kind,
    string? Name = null,
    int? Ordinal = null,
    IReadOnlyList<StarkPackageTypedTemplateExpressionManifest>? Arguments = null,
    string? LiteralText = null,
    StarkPackageTypeReference? Type = null);

internal sealed record StarkPackageTypedTemplateStatementManifest(
    string Kind,
    StarkPackageTypedTemplateExpressionManifest Expression,
    string? Name = null,
    string? StorageClass = null,
    bool IsMutable = false,
    StarkPackageTypeReference? Type = null);

internal sealed record StarkPackageTypedTemplateBodyManifest(
    IReadOnlyList<StarkPackageTypedTemplateStatementManifest> Statements);

internal sealed record StarkPackageTemplateLocalDeclarationManifest(
    string Kind,
    int Line,
    int Column,
    StarkPackageTypeReference Type);

internal sealed record StarkPackageTemplateConversionManifest(
    int Ordinal,
    StarkPackageTypeReference TargetType);

internal sealed record StarkPackageTemplateDirectCallManifest(
    int Ordinal,
    string QualifiedResolvedName,
    StarkPackageTypeReference ReturnType,
    IReadOnlyList<StarkPackageTypedParameterManifest> Parameters,
    string? QualifiedSourceName = null,
    string? QualifiedTemplateName = null,
    IReadOnlyList<StarkPackageTypeReference>? TypeArguments = null);

internal sealed record StarkPackageTemplateFieldAccessManifest(
    int Ordinal,
    string FieldName,
    int FieldIndex,
    StarkPackageTypeReference FieldType);

internal sealed record StarkPackageTemplateMemberCallManifest(
    int Ordinal,
    string QualifiedResolvedName,
    StarkPackageTypeReference ReturnType,
    IReadOnlyList<StarkPackageTypedParameterManifest> Parameters,
    string? QualifiedSourceName = null,
    string? QualifiedTemplateName = null,
    IReadOnlyList<StarkPackageTypeReference>? TypeArguments = null);

internal sealed record StarkPackageFunctionTemplateManifest(
    string QualifiedResolvedName,
    string QualifiedName,
    string OverloadKey,
    string BodyText,
    int? TopLevelStatementCount = null,
    StarkPackageTypedTemplateBodyManifest? TypedBody = null,
    IReadOnlyList<StarkPackageDeferredFunctionInstantiationManifest>? DeferredFunctionInstantiations = null,
    IReadOnlyList<StarkPackageDeferredTypeInstantiationManifest>? DeferredTypeInstantiations = null,
    IReadOnlyList<StarkPackageTemplateObjectCreationManifest>? ObjectCreations = null,
    IReadOnlyList<StarkPackageTemplateEnumConstructorManifest>? EnumConstructors = null,
    IReadOnlyList<StarkPackageTemplateEnumCallManifest>? EnumCalls = null,
    IReadOnlyList<StarkPackageTemplateEnumValueManifest>? EnumValues = null,
    IReadOnlyList<StarkPackageTemplateEnumPatternManifest>? EnumPatterns = null,
    IReadOnlyList<StarkPackageTemplateAggregatePatternManifest>? AggregatePatterns = null,
    IReadOnlyList<StarkPackageTemplateLocalDeclarationManifest>? LocalDeclarations = null,
    IReadOnlyList<StarkPackageTemplateConversionManifest>? Conversions = null,
    IReadOnlyList<StarkPackageTemplateDirectCallManifest>? DirectCalls = null,
    IReadOnlyList<StarkPackageTemplateFieldAccessManifest>? FieldAccesses = null,
    IReadOnlyList<StarkPackageTemplateMemberCallManifest>? MemberCalls = null);

internal sealed record StarkPackageCompilerFactsSection(
    IReadOnlyList<StarkPackageFunctionEffectManifest> FunctionEffects,
    IReadOnlyList<StarkPackageAbiFunctionManifest>? AbiFunctions = null,
    IReadOnlyList<StarkPackageConcreteTypeLayoutManifest>? ConcreteLayouts = null,
    IReadOnlyList<StarkPackageEnumLayoutManifest>? EnumLayouts = null,
    IReadOnlyList<StarkPackageFunctionSemanticManifest>? FunctionSemantics = null);

internal sealed record StarkPackageFunctionEffectManifest(
    string QualifiedResolvedName,
    string Kind,
    bool ReadsArgumentMemory,
    bool IsPure,
    bool NoSync,
    bool NoFree,
    bool NoUnwind,
    bool WillReturn,
    bool MustProgress,
    bool UseFastCallingConvention,
    bool IsFfi,
    bool IsHot,
    bool IsCold,
    string InlinePreference,
    bool IsStrictFp);

internal sealed record StarkPackageAbiParameterManifest(
    string SourceName,
    string LlvmName,
    StarkPackageTypeReference SourceType,
    StarkPackageTypeReference LlvmType,
    string Kind);

internal sealed record StarkPackageAbiFunctionManifest(
    string QualifiedResolvedName,
    string SymbolName,
    StarkPackageTypeReference SourceReturnType,
    StarkPackageTypeReference LlvmReturnType,
    IReadOnlyList<StarkPackageAbiParameterManifest> Parameters,
    bool IsFfi,
    string? SourceName = null,
    bool UsesFastCallingConvention = false);

internal sealed record StarkPackageFunctionMemoryEffectsManifest(
    bool ReadsArgumentMemory,
    bool WritesArgumentMemory,
    bool CapturesArgumentMemory,
    bool ReadsOtherMemory,
    bool WritesOtherMemory);

internal sealed record StarkPackageParameterMemoryEffectsManifest(
    string Name,
    string Type,
    bool IsMemoryBacked,
    bool GuaranteedNonNull,
    bool GuaranteedReadOnly,
    bool GuaranteedWriteOnly,
    bool GuaranteedNoAlias,
    int? DereferenceableBytes,
    int? AlignmentBytes,
    bool Reads,
    bool Writes,
    string CaptureKind);

internal sealed record StarkPackageFunctionSemanticManifest(
    string QualifiedResolvedName,
    IReadOnlyList<string> CalledFunctions,
    StarkPackageFunctionMemoryEffectsManifest? MemoryEffects = null,
    IReadOnlyList<StarkPackageParameterMemoryEffectsManifest>? Parameters = null);

internal sealed record StarkPackageConcreteTypeLayoutManifest(
    string QualifiedTypeName,
    int SizeBytes,
    int AlignmentBytes);

internal sealed record StarkPackageEnumLayoutFieldManifest(
    string Name,
    StarkPackageTypeReference Type);

internal sealed record StarkPackageEnumVariantLayoutFieldManifest(
    int SourcePosition,
    string? SourceFieldName,
    string StorageFieldName,
    int StorageFieldIndex,
    StarkPackageTypeReference Type);

internal sealed record StarkPackageEnumVariantLayoutManifest(
    string Name,
    int TagValue,
    bool UsesNamedFields,
    IReadOnlyList<StarkPackageEnumVariantLayoutFieldManifest> Fields);

internal sealed record StarkPackageEnumLayoutManifest(
    string QualifiedTypeName,
    string Kind,
    StarkPackageEnumLayoutFieldManifest TagField,
    IReadOnlyList<StarkPackageEnumLayoutFieldManifest> OrderedFields,
    IReadOnlyList<StarkPackageEnumVariantLayoutManifest> Variants);

internal static class PackageManifestBuilder
{
    public static StarkPackageManifest Create(
        CompilationResult result,
        string libraryOutputPath)
    {
        var loadedModules = result.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules);
        var typeModel = result.Artifacts.GetRequired(CompilerArtifactKeys.TypeCheckModel);
        var enumLayoutModel = result.Artifacts.GetRequired(CompilerArtifactKeys.EnumLayoutModel);
        var abiModel = result.Artifacts.GetRequired(CompilerArtifactKeys.AbiModel);
        var effectModel = result.Artifacts.GetRequired(CompilerArtifactKeys.FunctionEffects);
        result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validationModel);

        var modules = new List<StarkPackageModuleManifest>();

        foreach (var module in loadedModules.Modules.Values.OrderBy(static module => module.SyntaxModel.ModuleName, StringComparer.Ordinal))
        {
            var reExports = module.SyntaxModel.Imports
                .Where(static import => import.IsReExport)
                .OrderBy(static import => import.ModuleName, StringComparer.Ordinal)
                .Select(static import => new StarkPackageReExportManifest(import.ModuleName))
                .ToArray();
            var imports = module.SyntaxModel.Imports
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
                         .Where(static declaration => declaration.Visibility is StarkVisibility.Public or StarkVisibility.Export)
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
                            typedFunctions.Add(BuildTypedFunctionManifest(functionManifest, resolvedLookupName, typeModel, module.SyntaxModel.ModuleName));
                        }

                        break;

                    case DeclarationKind.Struct:
                    case DeclarationKind.Record:
                    case DeclarationKind.Trait:
                    case DeclarationKind.Doctrine:
                        if (typeModel.NamedTypes.TryGetValue(lookupName, out var namedType))
                        {
                            types.Add(BuildTypeManifest(module, declaration, qualifiedName, visibility, namedType, typeModel, abiModel, effectModel));
                            typedTypes.Add(BuildTypedTypeManifest(module, declaration, qualifiedName, visibility, namedType, typeModel, abiModel, effectModel));
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
                            typedTypes.Add(BuildTypedTypeManifest(module, declaration, qualifiedName, visibility, enumType, typeModel, abiModel, effectModel));
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

            foreach (var genericTemplate in BuildGenericFunctionTemplates(module, typeModel))
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
                reExports,
                functions,
                types,
                globals,
                TypeAliases: typeAliases,
                TypedInterface: new StarkPackageTypedInterfaceSection(
                    typedFunctions,
                    typedTypes,
                    typedGlobals,
                    TypeAliases: typedTypeAliases),
                CompilerFacts: new StarkPackageCompilerFactsSection(
                    functionEffects,
                    AbiFunctions: abiFunctions,
                    ConcreteLayouts: concreteLayouts,
                    EnumLayouts: enumLayouts,
                    FunctionSemantics: functionSemantics),
                GenericTemplates: genericTemplates.Count == 0
                    ? null
                    : new StarkPackageGenericTemplateSection(genericTemplates),
                Imports: imports.Length == 0 ? null : imports));
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

    private static bool TryBuildFunctionEffectManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        FunctionEffectModel effectModel,
        out StarkPackageFunctionEffectManifest manifest)
    {
        manifest = default!;

        var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
        var lookupName = FunctionOverloadFacts.QualifyResolvedName(module, resolvedLocalName);
        if (!effectModel.Functions.TryGetValue(lookupName, out var effects))
        {
            return false;
        }

        manifest = new StarkPackageFunctionEffectManifest(
            QualifiedResolvedName: $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}",
            Kind: effects.Kind.ToString().ToLowerInvariant(),
            ReadsArgumentMemory: effects.ReadsArgumentMemory,
            IsPure: effects.IsPure,
            NoSync: effects.NoSync,
            NoFree: effects.NoFree,
            NoUnwind: effects.NoUnwind,
            WillReturn: effects.WillReturn,
            MustProgress: effects.MustProgress,
            UseFastCallingConvention: effects.UseFastCallingConvention,
            IsFfi: effects.IsFfi,
            IsHot: effects.IsHot,
            IsCold: effects.IsCold,
            InlinePreference: RenderInlinePreference(effects.InlinePreference),
            IsStrictFp: effects.IsStrictFp);
        return true;
    }

    private static bool TryBuildAbiFunctionManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        AbiModel abiModel,
        out StarkPackageAbiFunctionManifest manifest)
    {
        manifest = default!;

        var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
        var lookupName = FunctionOverloadFacts.QualifyResolvedName(module, resolvedLocalName);
        if (!abiModel.Functions.TryGetValue(lookupName, out var abiFunction))
        {
            return false;
        }

        manifest = new StarkPackageAbiFunctionManifest(
            QualifiedResolvedName: $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}",
            SymbolName: ComputePublishedPackageAbiSymbolName(
                module.SyntaxModel.ModuleName,
                declaration,
                resolvedLocalName,
                abiFunction.IsFfi),
            SourceReturnType: BuildPublishedAbiTypeReference(abiFunction.SourceReturnType, module),
            LlvmReturnType: BuildPublishedAbiTypeReference(abiFunction.LlvmReturnType, module),
            Parameters: abiFunction.Parameters
                .Select(parameter => new StarkPackageAbiParameterManifest(
                    parameter.SourceName,
                    parameter.LlvmName,
                    BuildPublishedAbiTypeReference(parameter.SourceType, module),
                    BuildPublishedAbiTypeReference(parameter.LlvmType, module),
                    parameter.Kind.ToString().ToLowerInvariant()))
                .ToArray(),
            IsFfi: abiFunction.IsFfi,
            SourceName: abiFunction.SourceName,
            UsesFastCallingConvention: abiFunction.UsesFastCallingConvention);
        return true;
    }

    private static bool TryBuildConcreteLayoutManifest(
        NamedTypeSymbol namedType,
        string qualifiedTypeName,
        TypeCheckModel typeModel,
        EnumLayoutModel enumLayoutModel,
        out StarkPackageConcreteTypeLayoutManifest manifest)
    {
        manifest = default!;

        var concreteType = StarkTypeSymbols.Named(namedType.Name);
        if (ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(concreteType, typeModel.NamedTypes, enumLayoutModel.Layouts) is not { } layout)
        {
            return false;
        }

        manifest = new StarkPackageConcreteTypeLayoutManifest(
            qualifiedTypeName,
            layout.SizeBytes,
            layout.AlignmentBytes);
        return true;
    }

    private static bool TryBuildFunctionSemanticManifest(
        LoadedModuleDocument module,
        TopLevelDeclarationModel declaration,
        SemanticValidationModel validationModel,
        out StarkPackageFunctionSemanticManifest manifest)
    {
        manifest = default!;

        var resolvedLocalName = FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration);
        var lookupName = FunctionOverloadFacts.QualifyResolvedName(module, resolvedLocalName);
        if (!validationModel.Functions.TryGetValue(lookupName, out var validation))
        {
            return false;
        }

        manifest = new StarkPackageFunctionSemanticManifest(
            QualifiedResolvedName: $"{module.SyntaxModel.ModuleName}.{resolvedLocalName}",
            CalledFunctions: validation.CalledFunctions
                .Select(callee => QualifyPublishedCalledFunctionName(module, callee))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static callee => callee, StringComparer.Ordinal)
                .ToArray(),
            MemoryEffects: validation.MemoryEffects is null
                ? null
                : new StarkPackageFunctionMemoryEffectsManifest(
                    validation.MemoryEffects.ReadsArgumentMemory,
                    validation.MemoryEffects.WritesArgumentMemory,
                    validation.MemoryEffects.CapturesArgumentMemory,
                    validation.MemoryEffects.ReadsOtherMemory,
                    validation.MemoryEffects.WritesOtherMemory),
            Parameters: validation.Parameters?
                .Select(parameter => new StarkPackageParameterMemoryEffectsManifest(
                    parameter.Name,
                    parameter.Type,
                    parameter.IsMemoryBacked,
                    parameter.GuaranteedNonNull,
                    parameter.GuaranteedReadOnly,
                    parameter.GuaranteedWriteOnly,
                    parameter.GuaranteedNoAlias,
                    parameter.DereferenceableBytes,
                    parameter.AlignmentBytes,
                    parameter.Reads,
                    parameter.Writes,
                    parameter.CaptureKind.ToString().ToLowerInvariant()))
                .ToArray());
        return true;
    }

    private static IReadOnlyList<StarkPackageFunctionTemplateManifest> BuildGenericFunctionTemplates(
        LoadedModuleDocument module,
        TypeCheckModel typeModel)
    {
        var literalsByLocation = typeModel.Literals
            .Where(record => string.Equals(record.Location.FilePath, module.Reference.FilePath, StringComparison.Ordinal))
            .GroupBy(static record => BuildTemplateLiteralLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var deferredTriggersByFunction = typeModel.DeferredInstantiationTriggers
            .GroupBy(static trigger => trigger.EnclosingFunctionName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DeferredFunctionInstantiationTriggerRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var deferredTypeTriggersByFunction = typeModel.DeferredTypeTriggers
            .GroupBy(static trigger => trigger.EnclosingFunctionName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DeferredTypeInstantiationTriggerRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var objectCreationsByFunction = typeModel.ObjectCreations
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ObjectCreationTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumConstructorsByFunction = typeModel.EnumConstructors
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumConstructorTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumCallsByFunction = typeModel.EnumCalls
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumCallTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumValuesByFunction = typeModel.EnumValues
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumValueTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var enumPatternsByFunction = typeModel.EnumPatterns
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<EnumPatternTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var aggregatePatternsByFunction = typeModel.AggregatePatterns
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<AggregatePatternTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var localDeclarationsByFunction = typeModel.LocalDeclarations
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<LocalDeclarationTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var conversionsByFunction = typeModel.Conversions
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ConversionTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var directCallsByFunction = typeModel.DirectCalls
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DirectCallTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var fieldAccessesByFunction = typeModel.FieldAccesses
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<FieldAccessTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);
        var memberCallsByFunction = typeModel.MemberCalls
            .Where(static record => record.EnclosingFunctionName is not null)
            .GroupBy(static record => record.EnclosingFunctionName!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<MemberCallTypingRecord>)group.ToArray(),
                StringComparer.Ordinal);

        return DeclaredFunctionSyntaxCollector.Collect(module.ParseResult, module.SyntaxModel)
            .Where(static function => function.HasBody && function.TypeParameters is not null && function.Visibility is StarkVisibility.Public or StarkVisibility.Export)
            .Select(function =>
            {
                var qualifiedResolvedName = $"{module.SyntaxModel.ModuleName}.{function.Name}";
                var lookupName = LookupName(module.SyntaxModel.ModuleName, module.Reference.IsRoot, function.Name);
                deferredTriggersByFunction.TryGetValue(lookupName, out var deferredTriggers);
                deferredTypeTriggersByFunction.TryGetValue(lookupName, out var deferredTypeTriggers);
                objectCreationsByFunction.TryGetValue(lookupName, out var objectCreations);
                enumConstructorsByFunction.TryGetValue(lookupName, out var enumConstructors);
                enumCallsByFunction.TryGetValue(lookupName, out var enumCalls);
                enumValuesByFunction.TryGetValue(lookupName, out var enumValues);
                enumPatternsByFunction.TryGetValue(lookupName, out var enumPatterns);
                aggregatePatternsByFunction.TryGetValue(lookupName, out var aggregatePatterns);
                localDeclarationsByFunction.TryGetValue(lookupName, out var localDeclarations);
                conversionsByFunction.TryGetValue(lookupName, out var conversions);
                directCallsByFunction.TryGetValue(lookupName, out var directCalls);
                fieldAccessesByFunction.TryGetValue(lookupName, out var fieldAccesses);
                memberCallsByFunction.TryGetValue(lookupName, out var memberCalls);

                return new StarkPackageFunctionTemplateManifest(
                    QualifiedResolvedName: qualifiedResolvedName,
                    QualifiedName: $"{module.SyntaxModel.ModuleName}.{function.DisplaySourceName}",
                    OverloadKey: FunctionOverloadFacts.BuildOverloadKey(function.ParameterList),
                    BodyText: GetContextSourceText(module.ParseResult, function.Body),
                    TopLevelStatementCount: function.Body.block()?.statement().Length,
                    TypedBody: BuildPublishedTypedTemplateBody(module, function.Body, literalsByLocation, objectCreations, enumConstructors, enumCalls, enumValues, localDeclarations, directCalls, memberCalls),
                    DeferredFunctionInstantiations: deferredTriggers is { Count: > 0 }
                        ? deferredTriggers
                            .Where(static trigger => trigger.Signature.TemplateName is not null && trigger.Signature.TypeArguments is { Count: > 0 })
                            .Select(trigger => new StarkPackageDeferredFunctionInstantiationManifest(
                                QualifyPublishedCalledFunctionName(module, trigger.Signature.TemplateName!),
                                trigger.Signature.TypeArguments!
                                    .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                                    .ToArray()))
                            .ToArray()
                        : null,
                    DeferredTypeInstantiations: deferredTypeTriggers is { Count: > 0 }
                        ? deferredTypeTriggers
                            .Select(trigger => new StarkPackageDeferredTypeInstantiationManifest(
                                BuildPublishedAbiTypeReference(trigger.Type, module)))
                            .ToArray()
                        : null,
                    ObjectCreations: BuildPublishedTemplateObjectCreations(module, function.Body, objectCreations),
                    EnumConstructors: BuildPublishedTemplateEnumConstructors(module, function.Body, enumConstructors),
                    EnumCalls: BuildPublishedTemplateEnumCalls(module, function.Body, enumCalls),
                    EnumValues: BuildPublishedTemplateEnumValues(module, function.Body, enumValues),
                    EnumPatterns: BuildPublishedTemplateEnumPatterns(module, function.Body, enumPatterns),
                    AggregatePatterns: BuildPublishedTemplateAggregatePatterns(module, function.Body, aggregatePatterns),
                    LocalDeclarations: BuildPublishedTemplateLocalDeclarations(module, localDeclarations),
                    Conversions: BuildPublishedTemplateConversions(module, function.Body, conversions),
                    DirectCalls: BuildPublishedTemplateDirectCalls(module, function.Body, directCalls),
                    FieldAccesses: BuildPublishedTemplateFieldAccesses(module, function.Body, fieldAccesses),
                    MemberCalls: BuildPublishedTemplateMemberCalls(module, function.Body, memberCalls));
            })
            .OrderBy(static template => template.QualifiedResolvedName, StringComparer.Ordinal)
            .ThenBy(static template => template.OverloadKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static StarkPackageTypedTemplateBodyManifest? BuildPublishedTypedTemplateBody(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyList<ObjectCreationTypingRecord>? objectCreations,
        IReadOnlyList<EnumConstructorTypingRecord>? enumConstructors,
        IReadOnlyList<EnumCallTypingRecord>? enumCalls,
        IReadOnlyList<EnumValueTypingRecord>? enumValues,
        IReadOnlyList<LocalDeclarationTypingRecord>? localDeclarations,
        IReadOnlyList<DirectCallTypingRecord>? directCalls,
        IReadOnlyList<MemberCallTypingRecord>? memberCalls)
    {
        var block = functionBody switch
        {
            StarkParser.FunctionBodyContext functionBodyContext => functionBodyContext.block(),
            StarkParser.BlockContext directBlock => directBlock,
            _ => null
        };
        if (block is null)
        {
            return null;
        }

        var statements = block.statement();
        if (statements.Length == 0 || statements.Length > 2)
        {
            return null;
        }

        var localDeclarationsByLocation = (localDeclarations ?? [])
            .GroupBy(static record => TemplateLocalDeclarationFacts.BuildLookupKey(record.Kind, record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var objectCreationOrdinals = CollectTrackedTemplateObjectCreations(functionBody)
            .Select((objectCreation, ordinal) => (objectCreation, ordinal))
            .ToDictionary(static item => item.objectCreation, static item => item.ordinal);
        var enumConstructorsByLocation = (enumConstructors ?? [])
            .GroupBy(static record => BuildTemplateEnumConstructorLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var enumConstructorOrdinals = CollectTemplateEnumConstructorExpressions(functionBody)
            .Select((enumConstructor, ordinal) => (enumConstructor, ordinal))
            .Where(item => enumConstructorsByLocation.ContainsKey(
                BuildTemplateEnumConstructorLookupKey(item.enumConstructor.Start.Line, item.enumConstructor.Start.Column + 1)))
            .ToDictionary(static item => item.enumConstructor, static item => item.ordinal);
        var enumCallsByLocation = (enumCalls ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var enumCallOrdinals = CollectTemplateDirectCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => (argumentList, ordinal))
            .Where(item => enumCallsByLocation.ContainsKey(
                TemplateDirectCallFacts.BuildLookupKey(item.argumentList.Start.Line, item.argumentList.Start.Column + 1)))
            .ToDictionary(static item => item.argumentList, static item => item.ordinal);
        var enumValuesByLocation = (enumValues ?? [])
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var enumValueOrdinals = CollectTemplateEnumValuePrimaryExpressions(functionBody)
            .Select((primaryExpression, ordinal) => (primaryExpression, ordinal))
            .Where(item => enumValuesByLocation.ContainsKey(
                TemplateDirectCallFacts.BuildLookupKey(item.primaryExpression.Start.Line, item.primaryExpression.Start.Column + 1)))
            .ToDictionary(static item => item.primaryExpression, static item => item.ordinal);
        var directCallOrdinals = CollectTemplateDirectCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => (argumentList, ordinal))
            .ToDictionary(static item => item.argumentList, static item => item.ordinal);
        var memberCallOrdinals = CollectTemplateMemberCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => (argumentList, ordinal))
            .ToDictionary(static item => item.argumentList, static item => item.ordinal);
        var fieldAccessOrdinals = CollectTemplateMemberAccessParts(functionBody)
            .Select((postfixPart, ordinal) => (postfixPart, ordinal))
            .ToDictionary(static item => item.postfixPart, static item => item.ordinal);
        var publishedStatements = new List<StarkPackageTypedTemplateStatementManifest>(statements.Length);

        foreach (var statement in statements)
        {
            if (!TryBuildPublishedTypedTemplateStatement(
                    module,
                    statement,
                    literalsByLocation,
                    localDeclarationsByLocation,
                    objectCreationOrdinals,
                    enumConstructorOrdinals,
                    enumCallOrdinals,
                    enumValueOrdinals,
                    directCallOrdinals,
                    memberCallOrdinals,
                    fieldAccessOrdinals,
                    out var publishedStatement))
            {
                return null;
            }

            publishedStatements.Add(publishedStatement);
        }

        if (publishedStatements.Count == 1
            && string.Equals(publishedStatements[0].Kind, "return", StringComparison.Ordinal))
        {
            return new StarkPackageTypedTemplateBodyManifest(publishedStatements);
        }

        if (publishedStatements.Count == 2
            && string.Equals(publishedStatements[0].Kind, "local-variable", StringComparison.Ordinal)
            && string.Equals(publishedStatements[1].Kind, "return", StringComparison.Ordinal))
        {
            return new StarkPackageTypedTemplateBodyManifest(publishedStatements);
        }

        return null;
    }

    private static bool TryBuildPublishedTypedTemplateStatement(
        LoadedModuleDocument module,
        StarkParser.StatementContext statement,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<string, LocalDeclarationTypingRecord> localDeclarationsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, int> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<StarkParser.PrimaryExpressionContext, int> enumValueOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateStatementManifest publishedStatement)
    {
        publishedStatement = null!;

        if (statement.localVariableDeclaration() is { } localVariable)
        {
            var declarators = localVariable.variableDeclarators().variableDeclarator();
            if (declarators.Length != 1
                || declarators[0].variableInitializer()?.expression() is not { } initializerExpression
                || !localDeclarationsByLocation.TryGetValue(
                    TemplateLocalDeclarationFacts.BuildLookupKey(
                    TemplateLocalDeclarationFacts.VariableKind,
                    localVariable.Start.Line,
                    localVariable.Start.Column + 1),
                    out var localDeclaration)
                || !TryBuildPublishedTypedTemplateExpression(module, initializerExpression, literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var initializer))
            {
                return false;
            }

            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "local-variable",
                Expression: initializer,
                Name: declarators[0].Identifier().GetText(),
                StorageClass: localVariable.storageClass().GetText(),
                IsMutable: localVariable.MUT() is not null,
                Type: BuildPublishedAbiTypeReference(localDeclaration.Type, module));
            return true;
        }

        if (statement.returnStatement() is { } returnStatement
            && returnStatement.expression() is { } expression
            && TryBuildPublishedTypedTemplateExpression(module, expression, literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var returnExpression))
        {
            publishedStatement = new StarkPackageTypedTemplateStatementManifest(
                Kind: "return",
                Expression: returnExpression);
            return true;
        }

        return false;
    }

    private static bool TryBuildPublishedTypedTemplateExpression(
        LoadedModuleDocument module,
        StarkParser.ExpressionContext expression,
        IReadOnlyDictionary<string, LiteralTypingRecord> literalsByLocation,
        IReadOnlyDictionary<StarkParser.ObjectCreationExpressionContext, int> objectCreationOrdinals,
        IReadOnlyDictionary<StarkParser.EnumConstructorExpressionContext, int> enumConstructorOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> enumCallOrdinals,
        IReadOnlyDictionary<StarkParser.PrimaryExpressionContext, int> enumValueOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> directCallOrdinals,
        IReadOnlyDictionary<StarkParser.ArgumentListContext, int> memberCallOrdinals,
        IReadOnlyDictionary<StarkParser.PostfixPartContext, int> fieldAccessOrdinals,
        out StarkPackageTypedTemplateExpressionManifest publishedExpression)
    {
        publishedExpression = null!;

        var postfixExpression = TryGetSimplePostfixExpression(expression);
        if (postfixExpression?.primaryExpression().objectCreationExpression() is { } objectCreationExpression
            && objectCreationOrdinals.TryGetValue(objectCreationExpression, out var objectCreationOrdinal))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>();
            if (objectCreationExpression.argumentList() is { } objectCreationArgumentList)
            {
                foreach (var argument in objectCreationArgumentList.argument())
                {
                    if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                    {
                        return false;
                    }

                    arguments.Add(publishedArgument);
                }
            }

            if (objectCreationExpression.objectInitializer() is { } objectInitializer)
            {
                foreach (var memberInitializer in objectInitializer.memberInitializer())
                {
                    if (memberInitializer.variableInitializer()?.expression() is not { } initializerExpression
                        || !TryBuildPublishedTypedTemplateExpression(module, initializerExpression, literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                    {
                        return false;
                    }

                    arguments.Add(publishedArgument);
                }
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "object-creation",
                Ordinal: objectCreationOrdinal,
                Arguments: arguments);
            return true;
        }

        if (postfixExpression?.primaryExpression().enumConstructorExpression() is { } enumConstructorExpression
            && enumConstructorOrdinals.TryGetValue(enumConstructorExpression, out var enumConstructorOrdinal))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(
                enumConstructorExpression.enumConstructorInitializer().enumConstructorMember().Length);
            foreach (var member in enumConstructorExpression.enumConstructorInitializer().enumConstructorMember())
            {
                if (!TryBuildPublishedTypedTemplateExpression(module, member.expression(), literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                {
                    return false;
                }

                arguments.Add(publishedArgument);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "enum-constructor",
                Ordinal: enumConstructorOrdinal,
                Arguments: arguments);
            return true;
        }

        if (postfixExpression is null)
        {
            return false;
        }

        if (postfixExpression.postfixPart().Length == 0
            && postfixExpression.primaryExpression().literal() is { } literal
            && literalsByLocation.TryGetValue(
                BuildTemplateLiteralLookupKey(literal.Start.Line, literal.Start.Column + 1),
                out var literalRecord))
        {
            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "literal",
                LiteralText: literal.GetText(),
                Type: BuildPublishedAbiTypeReference(literalRecord.Type, module));
            return true;
        }

        if (postfixExpression.postfixPart().Length == 0
            && enumValueOrdinals.TryGetValue(postfixExpression.primaryExpression(), out var enumValueOrdinal))
        {
            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "enum-value",
                Ordinal: enumValueOrdinal);
            return true;
        }

        if (postfixExpression.postfixPart().Length == 1
            && postfixExpression.postfixPart()[0].argumentList() is { } enumArgumentList
            && enumCallOrdinals.TryGetValue(enumArgumentList, out var enumCallOrdinal))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(enumArgumentList.argument().Length);
            foreach (var argument in enumArgumentList.argument())
            {
                if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                {
                    return false;
                }

                arguments.Add(publishedArgument);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "enum-call",
                Ordinal: enumCallOrdinal,
                Arguments: arguments);
            return true;
        }

        var name = postfixExpression.primaryExpression().Identifier()?.GetText()
            ?? postfixExpression.primaryExpression().qualifiedName()?.GetText();
        if (name is null)
        {
            return false;
        }

        if (postfixExpression.postfixPart().Length == 0)
        {
            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "name",
                Name: name);
            return true;
        }

        if (postfixExpression.postfixPart().Length == 1
            && postfixExpression.postfixPart()[0].argumentList() is { } argumentList
            && directCallOrdinals.TryGetValue(argumentList, out var directCallOrdinal))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(argumentList.argument().Length);
            foreach (var argument in argumentList.argument())
            {
                if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                {
                    return false;
                }

                arguments.Add(publishedArgument);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "direct-call",
                Ordinal: directCallOrdinal,
                Arguments: arguments);
            return true;
        }

        if (postfixExpression.postfixPart().Length == 2
            && postfixExpression.postfixPart()[0].Identifier() is not null
            && postfixExpression.postfixPart()[1].argumentList() is { } memberArgumentList
            && memberCallOrdinals.TryGetValue(memberArgumentList, out var memberCallOrdinal))
        {
            var arguments = new List<StarkPackageTypedTemplateExpressionManifest>(memberArgumentList.argument().Length + 1)
            {
                new StarkPackageTypedTemplateExpressionManifest(
                    Kind: "name",
                    Name: name)
            };

            foreach (var argument in memberArgumentList.argument())
            {
                if (!TryBuildPublishedTypedTemplateExpression(module, argument.expression(), literalsByLocation, objectCreationOrdinals, enumConstructorOrdinals, enumCallOrdinals, enumValueOrdinals, directCallOrdinals, memberCallOrdinals, fieldAccessOrdinals, out var publishedArgument))
                {
                    return false;
                }

                arguments.Add(publishedArgument);
            }

            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "member-call",
                Ordinal: memberCallOrdinal,
                Arguments: arguments);
            return true;
        }

        if (postfixExpression.postfixPart().Length == 1
            && postfixExpression.postfixPart()[0].argumentList() is null
            && postfixExpression.postfixPart()[0].Identifier() is not null
            && fieldAccessOrdinals.TryGetValue(postfixExpression.postfixPart()[0], out var fieldAccessOrdinal))
        {
            publishedExpression = new StarkPackageTypedTemplateExpressionManifest(
                Kind: "field-access",
                Ordinal: fieldAccessOrdinal,
                Arguments:
                [
                    new StarkPackageTypedTemplateExpressionManifest(
                        Kind: "name",
                        Name: name)
                ]);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<StarkPackageTemplateObjectCreationManifest>? BuildPublishedTemplateObjectCreations(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<ObjectCreationTypingRecord>? objectCreations)
    {
        if (objectCreations is not { Count: > 0 })
        {
            return null;
        }

        var objectCreationsByKey = objectCreations
            .GroupBy(static record => BuildTemplateObjectCreationLookupKey(record.ExpressionText, record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTrackedTemplateObjectCreations(functionBody)
            .Select(objectCreation => objectCreationsByKey.TryGetValue(
                    BuildTemplateObjectCreationLookupKey(
                        objectCreation.GetText(),
                        objectCreation.Start.Line,
                        objectCreation.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateObjectCreationManifest(
                    BuildPublishedAbiTypeReference(record.CreatedType, module),
                    BuildPublishedConstructorShape(module, record.Constructor),
                    record.Members.Count == 0
                        ? null
                        : record.Members
                            .Select(member => new StarkPackageTemplateObjectInitializerMemberManifest(
                                member.FieldName,
                                member.FieldIndex,
                                BuildPublishedAbiTypeReference(member.FieldType, module)))
                            .ToArray())
                : new StarkPackageTemplateObjectCreationManifest(
                    CreatedType: BuildPublishedAbiTypeReference(StarkTypeSymbols.Error, module),
                    Constructor: null))
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumConstructorManifest>? BuildPublishedTemplateEnumConstructors(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<EnumConstructorTypingRecord>? enumConstructors)
    {
        if (enumConstructors is not { Count: > 0 })
        {
            return null;
        }

        var enumConstructorsByLocation = enumConstructors
            .GroupBy(static record => BuildTemplateEnumConstructorLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumConstructorExpressions(functionBody)
            .Select((enumConstructor, ordinal) => enumConstructorsByLocation.TryGetValue(
                    BuildTemplateEnumConstructorLookupKey(enumConstructor.Start.Line, enumConstructor.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateEnumConstructorManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName,
                    record.Members.Count == 0
                        ? null
                        : record.Members
                            .Select(member => new StarkPackageTemplateEnumConstructorMemberManifest(
                                member.FieldName,
                                member.FieldIndex,
                                BuildPublishedAbiTypeReference(member.FieldType, module)))
                            .ToArray())
                : null)
            .Where(static enumConstructor => enumConstructor is not null)
            .Cast<StarkPackageTemplateEnumConstructorManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumCallManifest>? BuildPublishedTemplateEnumCalls(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<EnumCallTypingRecord>? enumCalls)
    {
        if (enumCalls is not { Count: > 0 })
        {
            return null;
        }

        var enumCallsByLocation = enumCalls
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateDirectCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => enumCallsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(argumentList.Start.Line, argumentList.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateEnumCallManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName)
                : null)
            .Where(static enumCall => enumCall is not null)
            .Cast<StarkPackageTemplateEnumCallManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumValueManifest>? BuildPublishedTemplateEnumValues(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<EnumValueTypingRecord>? enumValues)
    {
        if (enumValues is not { Count: > 0 })
        {
            return null;
        }

        var enumValuesByLocation = enumValues
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumValuePrimaryExpressions(functionBody)
            .Select((primaryExpression, ordinal) => enumValuesByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(primaryExpression.Start.Line, primaryExpression.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateEnumValueManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName)
                : null)
            .Where(static enumValue => enumValue is not null)
            .Cast<StarkPackageTemplateEnumValueManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateEnumPatternManifest>? BuildPublishedTemplateEnumPatterns(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<EnumPatternTypingRecord>? enumPatterns)
    {
        if (enumPatterns is not { Count: > 0 })
        {
            return null;
        }

        var enumPatternsByLocation = enumPatterns
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumPatternContexts(functionBody)
            .Select((patternContext, ordinal) => enumPatternsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(patternContext.Start.Line, patternContext.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateEnumPatternManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.EnumType, module),
                    record.VariantName,
                    record.Members.Count == 0
                        ? null
                        : record.Members
                            .Select(member => new StarkPackageTemplateEnumPatternMemberManifest(
                                member.FieldName,
                                member.FieldIndex,
                                BuildPublishedAbiTypeReference(member.FieldType, module)))
                            .ToArray())
                : null)
            .Where(static enumPattern => enumPattern is not null)
            .Cast<StarkPackageTemplateEnumPatternManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateAggregatePatternManifest>? BuildPublishedTemplateAggregatePatterns(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<AggregatePatternTypingRecord>? aggregatePatterns)
    {
        if (aggregatePatterns is not { Count: > 0 })
        {
            return null;
        }

        var aggregatePatternsByLocation = aggregatePatterns
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateEnumPatternContexts(functionBody)
            .Select((patternContext, ordinal) => patternContext is StarkParser.AggregatePatternContext aggregatePattern
                    && aggregatePatternsByLocation.TryGetValue(
                        TemplateDirectCallFacts.BuildLookupKey(aggregatePattern.Start.Line, aggregatePattern.Start.Column + 1),
                        out var record)
                ? new StarkPackageTemplateAggregatePatternManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.Type, module))
                : null)
            .Where(static aggregatePattern => aggregatePattern is not null)
            .Cast<StarkPackageTemplateAggregatePatternManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static StarkPackagePublishedConstructorShapeManifest? BuildPublishedConstructorShape(
        LoadedModuleDocument module,
        TypedConstructorShape? constructor)
    {
        return constructor is null
            ? null
            : new StarkPackagePublishedConstructorShapeManifest(
                constructor.TypeName,
                constructor.Parameters
                    .Select(parameter => new StarkPackageTypedParameterManifest(
                        parameter.Name,
                        BuildPublishedAbiTypeReference(parameter.Type, module)))
                    .ToArray(),
                constructor.IsPrimaryShape);
    }

    private static IReadOnlyList<StarkPackageTemplateLocalDeclarationManifest>? BuildPublishedTemplateLocalDeclarations(
        LoadedModuleDocument module,
        IReadOnlyList<LocalDeclarationTypingRecord>? localDeclarations)
    {
        if (localDeclarations is not { Count: > 0 })
        {
            return null;
        }

        return localDeclarations
            .OrderBy(static record => record.Location.Line)
            .ThenBy(static record => record.Location.Column)
            .Select(record => new StarkPackageTemplateLocalDeclarationManifest(
                record.Kind,
                record.Location.Line,
                record.Location.Column,
                BuildPublishedAbiTypeReference(record.Type, module)))
            .ToArray();
    }

    private static IReadOnlyList<StarkPackageTemplateConversionManifest>? BuildPublishedTemplateConversions(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<ConversionTypingRecord>? conversions)
    {
        if (conversions is not { Count: > 0 })
        {
            return null;
        }

        var conversionsByLocation = conversions
            .GroupBy(static record => BuildTemplateConversionLookupKey(record.Location.Line, record.Location.Column))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateConversionExpressions(functionBody)
            .Select((unaryExpression, ordinal) => conversionsByLocation.TryGetValue(
                    BuildTemplateConversionLookupKey(unaryExpression.Start.Line, unaryExpression.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateConversionManifest(
                    ordinal,
                    BuildPublishedAbiTypeReference(record.TargetType, module))
                : null)
            .Where(static conversion => conversion is not null)
            .Cast<StarkPackageTemplateConversionManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateDirectCallManifest>? BuildPublishedTemplateDirectCalls(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<DirectCallTypingRecord>? directCalls)
    {
        if (directCalls is not { Count: > 0 })
        {
            return null;
        }

        var directCallsByLocation = directCalls
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateDirectCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => directCallsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(argumentList.Start.Line, argumentList.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateDirectCallManifest(
                    ordinal,
                    QualifyPublishedCalledFunctionName(module, record.Signature.Name),
                    BuildPublishedAbiTypeReference(record.Signature.ReturnType, module),
                    record.Signature.Parameters
                        .Select(parameter => new StarkPackageTypedParameterManifest(
                            parameter.Name,
                            BuildPublishedAbiTypeReference(parameter.Type, module)))
                        .ToArray(),
                    QualifiedSourceName: record.Signature.SourceName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.SourceName),
                    QualifiedTemplateName: record.Signature.TemplateName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.TemplateName),
                    TypeArguments: record.Signature.TypeArguments is { Count: > 0 }
                        ? record.Signature.TypeArguments
                            .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                            .ToArray()
                        : null)
                : null)
            .Where(static directCall => directCall is not null)
            .Cast<StarkPackageTemplateDirectCallManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateFieldAccessManifest>? BuildPublishedTemplateFieldAccesses(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<FieldAccessTypingRecord>? fieldAccesses)
    {
        if (fieldAccesses is not { Count: > 0 })
        {
            return null;
        }

        var fieldAccessesByLocation = fieldAccesses
            .GroupBy(static record => TemplateFieldAccessFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateMemberAccessParts(functionBody)
            .Select((postfixPart, ordinal) => fieldAccessesByLocation.TryGetValue(
                    TemplateFieldAccessFacts.BuildLookupKey(postfixPart.Start.Line, postfixPart.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateFieldAccessManifest(
                    ordinal,
                    record.FieldName,
                    record.FieldIndex,
                    BuildPublishedAbiTypeReference(record.FieldType, module))
                : null)
            .Where(static fieldAccess => fieldAccess is not null)
            .Cast<StarkPackageTemplateFieldAccessManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkPackageTemplateMemberCallManifest>? BuildPublishedTemplateMemberCalls(
        LoadedModuleDocument module,
        ParserRuleContext functionBody,
        IReadOnlyList<MemberCallTypingRecord>? memberCalls)
    {
        if (memberCalls is not { Count: > 0 })
        {
            return null;
        }

        var memberCallsByLocation = memberCalls
            .GroupBy(static record => TemplateDirectCallFacts.BuildLookupKey(record.Location))
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var published = CollectTemplateMemberCallArgumentLists(functionBody)
            .Select((argumentList, ordinal) => memberCallsByLocation.TryGetValue(
                    TemplateDirectCallFacts.BuildLookupKey(argumentList.Start.Line, argumentList.Start.Column + 1),
                    out var record)
                ? new StarkPackageTemplateMemberCallManifest(
                    ordinal,
                    QualifyPublishedCalledFunctionName(module, record.Signature.Name),
                    BuildPublishedAbiTypeReference(record.Signature.ReturnType, module),
                    record.Signature.Parameters
                        .Select(parameter => new StarkPackageTypedParameterManifest(
                            parameter.Name,
                            BuildPublishedAbiTypeReference(parameter.Type, module)))
                        .ToArray(),
                    QualifiedSourceName: record.Signature.SourceName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.SourceName),
                    QualifiedTemplateName: record.Signature.TemplateName is null
                        ? null
                        : QualifyPublishedCalledFunctionName(module, record.Signature.TemplateName),
                    TypeArguments: record.Signature.TypeArguments is { Count: > 0 }
                        ? record.Signature.TypeArguments
                            .Select(typeArgument => BuildPublishedAbiTypeReference(typeArgument, module))
                            .ToArray()
                        : null)
                : null)
            .Where(static memberCall => memberCall is not null)
            .Cast<StarkPackageTemplateMemberCallManifest>()
            .ToArray();

        return published.Length == 0 ? null : published;
    }

    private static IReadOnlyList<StarkParser.ObjectCreationExpressionContext> CollectTrackedTemplateObjectCreations(ParserRuleContext node)
    {
        var objectCreations = new List<StarkParser.ObjectCreationExpressionContext>();
        Collect(node, objectCreations);
        return objectCreations;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.ObjectCreationExpressionContext> accumulator)
        {
            if (current is StarkParser.ObjectCreationExpressionContext objectCreation
                && (objectCreation.objectInitializer() is not null
                    || objectCreation.argumentList() is { } argumentList && argumentList.argument().Length > 0))
            {
                accumulator.Add(objectCreation);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.ArgumentListContext> CollectTemplateDirectCallArgumentLists(ParserRuleContext node)
    {
        var directCalls = new List<StarkParser.ArgumentListContext>();
        Collect(node, directCalls);
        return directCalls;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.ArgumentListContext> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression
                && postfixExpression.postfixPart().Length > 0
                && postfixExpression.postfixPart()[0].argumentList() is { } argumentList)
            {
                accumulator.Add(argumentList);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.ExpressionContext expression)
    {
        var assignment = expression.assignmentExpression();
        if (assignment.assignmentOperator() is not null || assignment.conditionalExpression() is not { } conditional)
        {
            return null;
        }

        if (conditional.expression().Length != 0)
        {
            return null;
        }

        var logicalOr = conditional.logicalOrExpression();
        if (logicalOr.logicalAndExpression().Length != 1)
        {
            return null;
        }

        var logicalAnd = logicalOr.logicalAndExpression(0);
        if (logicalAnd.bitwiseOrExpression().Length != 1)
        {
            return null;
        }

        var bitwiseOr = logicalAnd.bitwiseOrExpression(0);
        if (bitwiseOr.bitwiseXorExpression().Length != 1)
        {
            return null;
        }

        var bitwiseXor = bitwiseOr.bitwiseXorExpression(0);
        if (bitwiseXor.bitwiseAndExpression().Length != 1)
        {
            return null;
        }

        var bitwiseAnd = bitwiseXor.bitwiseAndExpression(0);
        if (bitwiseAnd.equalityExpression().Length != 1)
        {
            return null;
        }

        var equality = bitwiseAnd.equalityExpression(0);
        if (equality.relationalExpression().Length != 1)
        {
            return null;
        }

        var relational = equality.relationalExpression(0);
        if (relational.shiftExpression().Length != 1)
        {
            return null;
        }

        var shift = relational.shiftExpression(0);
        if (shift.additiveExpression().Length != 1)
        {
            return null;
        }

        var additive = shift.additiveExpression(0);
        if (additive.multiplicativeExpression().Length != 1)
        {
            return null;
        }

        var multiplicative = additive.multiplicativeExpression(0);
        if (multiplicative.unaryExpression().Length != 1)
        {
            return null;
        }

        return TryGetSimplePostfixExpression(multiplicative.unaryExpression(0));
    }

    private static StarkParser.PostfixExpressionContext? TryGetSimplePostfixExpression(StarkParser.UnaryExpressionContext expression)
    {
        if (expression.powerExpression() is not { } powerExpression
            || powerExpression.unaryExpression() is not null)
        {
            return null;
        }

        return powerExpression.postfixExpression();
    }

    private static string BuildTemplateEnumConstructorLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    private static string BuildTemplateLiteralLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    private static IReadOnlyList<StarkParser.EnumConstructorExpressionContext> CollectTemplateEnumConstructorExpressions(ParserRuleContext node)
    {
        var enumConstructors = new List<StarkParser.EnumConstructorExpressionContext>();
        Collect(node, enumConstructors);
        return enumConstructors;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.EnumConstructorExpressionContext> accumulator)
        {
            if (current is StarkParser.EnumConstructorExpressionContext enumConstructor)
            {
                accumulator.Add(enumConstructor);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.PrimaryExpressionContext> CollectTemplateEnumValuePrimaryExpressions(ParserRuleContext node)
    {
        var enumValues = new List<StarkParser.PrimaryExpressionContext>();
        Collect(node, enumValues);
        return enumValues;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.PrimaryExpressionContext> accumulator)
        {
            if (current is StarkParser.PrimaryExpressionContext primaryExpression
                && (primaryExpression.genericEnumCaseReference() is not null
                    || primaryExpression.qualifiedName() is not null))
            {
                accumulator.Add(primaryExpression);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<ParserRuleContext> CollectTemplateEnumPatternContexts(ParserRuleContext node)
    {
        var enumPatterns = new List<ParserRuleContext>();
        Collect(node, enumPatterns);
        return enumPatterns;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<ParserRuleContext> accumulator)
        {
            switch (current)
            {
                case StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern:
                    accumulator.Add(enumNamedFieldPattern);
                    break;
                case StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern:
                    accumulator.Add(genericEnumAggregatePattern);
                    break;
                case StarkParser.AggregatePatternContext aggregatePattern:
                    accumulator.Add(aggregatePattern);
                    break;
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static string BuildTemplateConversionLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    private static IReadOnlyList<StarkParser.UnaryExpressionContext> CollectTemplateConversionExpressions(ParserRuleContext node)
    {
        var conversions = new List<StarkParser.UnaryExpressionContext>();
        Collect(node, conversions);
        return conversions;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.UnaryExpressionContext> accumulator)
        {
            if (current is StarkParser.UnaryExpressionContext unaryExpression
                && unaryExpression.conversionType() is not null)
            {
                accumulator.Add(unaryExpression);
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.PostfixPartContext> CollectTemplateMemberAccessParts(ParserRuleContext node)
    {
        var memberAccesses = new List<StarkParser.PostfixPartContext>();
        Collect(node, memberAccesses);
        return memberAccesses;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.PostfixPartContext> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression)
            {
                foreach (var postfixPart in postfixExpression.postfixPart())
                {
                    if (postfixPart.Identifier() is not null)
                    {
                        accumulator.Add(postfixPart);
                    }
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static IReadOnlyList<StarkParser.ArgumentListContext> CollectTemplateMemberCallArgumentLists(ParserRuleContext node)
    {
        var memberCalls = new List<StarkParser.ArgumentListContext>();
        Collect(node, memberCalls);
        return memberCalls;

        static void Collect(
            Antlr4.Runtime.Tree.IParseTree current,
            List<StarkParser.ArgumentListContext> accumulator)
        {
            if (current is StarkParser.PostfixExpressionContext postfixExpression)
            {
                var postfixParts = postfixExpression.postfixPart();
                for (var index = 0; index + 1 < postfixParts.Length; index++)
                {
                    if (postfixParts[index].Identifier() is not null
                        && postfixParts[index + 1].argumentList() is { } argumentList)
                    {
                        accumulator.Add(argumentList);
                    }
                }
            }

            for (var index = 0; index < current.ChildCount; index++)
            {
                Collect(current.GetChild(index), accumulator);
            }
        }
    }

    private static string BuildTemplateObjectCreationLookupKey(string expressionText, int line, int column)
    {
        return $"{line}:{column}:{expressionText}";
    }

    private static StarkPackageEnumLayoutManifest BuildEnumLayoutManifest(
        LoadedModuleDocument module,
        string qualifiedTypeName,
        EnumLayoutSymbol enumLayout)
    {
        return new StarkPackageEnumLayoutManifest(
            qualifiedTypeName,
            enumLayout.Kind.ToString().ToLowerInvariant(),
            new StarkPackageEnumLayoutFieldManifest(
                enumLayout.TagField.Name,
                BuildPublishedAbiTypeReference(enumLayout.TagField.Type, module)),
            enumLayout.OrderedFields
                .Select(field => new StarkPackageEnumLayoutFieldManifest(
                    field.Name,
                    BuildPublishedAbiTypeReference(field.Type, module)))
                .ToArray(),
            enumLayout.Variants.Values
                .OrderBy(static variant => variant.TagValue)
                .Select(variant => new StarkPackageEnumVariantLayoutManifest(
                    variant.Name,
                    variant.TagValue,
                    variant.UsesNamedFields,
                    variant.Fields
                        .Select(field => new StarkPackageEnumVariantLayoutFieldManifest(
                            field.SourcePosition,
                            field.SourceFieldName,
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            BuildPublishedAbiTypeReference(field.Type, module)))
                        .ToArray()))
                .ToArray());
    }

    private static StarkPackageTypeReference BuildPublishedAbiTypeReference(StarkTypeSymbol type, LoadedModuleDocument module)
    {
        return BuildPublishedAbiTypeReference(type, module.SyntaxModel.ModuleName, GetModuleLocalNamedTypes(module));
    }

    private static StarkPackageTypeReference BuildPublishedAbiTypeReference(
        StarkTypeSymbol type,
        string moduleName,
        ISet<string> localNamedTypes)
    {
        var normalizedNamedType = type.NamedType is null
            ? null
            : QualifyModuleLocalNamedType(type, moduleName, localNamedTypes);
        return new StarkPackageTypeReference(
            type.Kind.ToString().ToLowerInvariant(),
            Name: normalizedNamedType,
            BitWidth: type.BitWidth,
            RangeMin: type.RangeMin?.ToString(),
            RangeMax: type.RangeMax?.ToString(),
            IsMutablePointer: type.IsMutablePointer,
            BorrowKind: type.BorrowKind == StarkBorrowKind.None ? null : type.BorrowKind.ToString().ToLowerInvariant(),
            AccessKind: type.AccessKind == StarkAccessKind.None ? null : type.AccessKind.ToString().ToLowerInvariant(),
            InitializationKind: type.InitializationKind == StarkInitializationKind.None ? null : type.InitializationKind.ToString().ToLowerInvariant(),
            IsMutableView: type.IsMutableView,
            FixedLength: type.FixedLength,
            ElementType: type.ElementType is null ? null : BuildPublishedAbiTypeReference(type.ElementType, moduleName, localNamedTypes),
            TypeArguments: type.TypeArguments is { Count: > 0 }
                ? type.TypeArguments.Select(argument => BuildPublishedAbiTypeReference(argument, moduleName, localNamedTypes)).ToArray()
                : null);
    }

    private static string ComputePublishedPackageAbiSymbolName(
        string moduleName,
        TopLevelDeclarationModel declaration,
        string resolvedLocalName,
        bool isFfi)
    {
        if (isFfi)
        {
            return declaration.Name;
        }

        var qualifiedResolvedName = $"{moduleName}.{resolvedLocalName}";
        if (qualifiedResolvedName.StartsWith("__stark_", StringComparison.Ordinal))
        {
            return qualifiedResolvedName;
        }

        if (!string.Equals(resolvedLocalName, declaration.Name, StringComparison.Ordinal))
        {
            return qualifiedResolvedName;
        }

        if (declaration.Visibility == StarkVisibility.Export
            && !declaration.Name.Contains('.', StringComparison.Ordinal))
        {
            return declaration.Name;
        }

        return $"{moduleName}.{declaration.Name}";
    }

    private static string QualifyPublishedCalledFunctionName(LoadedModuleDocument module, string callee)
    {
        if (string.IsNullOrWhiteSpace(callee)
            || callee.StartsWith("__stark_", StringComparison.Ordinal))
        {
            return callee;
        }

        if (callee.StartsWith($"{module.SyntaxModel.ModuleName}.", StringComparison.Ordinal))
        {
            return callee;
        }

        return module.SyntaxModel.Declarations.Any(declaration =>
            declaration.Function is not null
            && string.Equals(
                FunctionOverloadFacts.GetResolvedLocalName(module.SyntaxModel, declaration),
                callee,
                StringComparison.Ordinal))
            ? $"{module.SyntaxModel.ModuleName}.{callee}"
            : callee;
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
            GenericParameters: declarationFunction.GenericParams.Count == 0 ? null : declarationFunction.GenericParams.ToArray());
        return true;
    }

    private static StarkPackageTypedFunctionManifest BuildTypedFunctionManifest(
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
            manifest.GenericParameters);
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
        FunctionEffectModel effectModel)
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
                : BuildTypeDestructorManifest(module, declaration.Name));
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
                    GenericParameters: declaration.Function.GenericParams.Count == 0 ? null : declaration.Function.GenericParams.ToArray());
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
                    GenericParameters: declaration.Function.GenericParams.Count == 0 ? null : declaration.Function.GenericParams.ToArray());
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

    private static string RenderManifestTypeText(StarkTypeSymbol type, string moduleName)
    {
        var displayName = string.IsNullOrEmpty(moduleName)
            ? type.DisplayName
            : type.DisplayName.Replace($"{moduleName}.", string.Empty, StringComparison.Ordinal);

        return CanonicalizeManifestTypeText(displayName);
    }

    private static StarkPackageTypeReference BuildTypeReference(
        StarkTypeSymbol type,
        string moduleName,
        bool stripCurrentModulePrefix = true)
    {
        var normalizedNamedType = type.NamedType is null
            ? null
            : NormalizeNamedType(type, moduleName, stripCurrentModulePrefix);
        return new StarkPackageTypeReference(
            type.Kind.ToString().ToLowerInvariant(),
            Name: normalizedNamedType,
            BitWidth: type.BitWidth,
            RangeMin: type.RangeMin?.ToString(),
            RangeMax: type.RangeMax?.ToString(),
            IsMutablePointer: type.IsMutablePointer,
            BorrowKind: type.BorrowKind == StarkBorrowKind.None ? null : type.BorrowKind.ToString().ToLowerInvariant(),
            AccessKind: type.AccessKind == StarkAccessKind.None ? null : type.AccessKind.ToString().ToLowerInvariant(),
            InitializationKind: type.InitializationKind == StarkInitializationKind.None ? null : type.InitializationKind.ToString().ToLowerInvariant(),
            IsMutableView: type.IsMutableView,
            FixedLength: type.FixedLength,
            ElementType: type.ElementType is null ? null : BuildTypeReference(type.ElementType, moduleName, stripCurrentModulePrefix),
            TypeArguments: type.TypeArguments is { Count: > 0 }
                ? type.TypeArguments.Select(argument => BuildTypeReference(argument, moduleName, stripCurrentModulePrefix)).ToArray()
                : null);
    }

    private static string NormalizeNamedType(StarkTypeSymbol type, string moduleName, bool stripCurrentModulePrefix)
    {
        var name = type.TypeArguments is { Count: > 0 }
            ? StarkTypeSymbols.GetGenericBaseName(type.NamedType!)
            : type.NamedType!;
        return stripCurrentModulePrefix
            ? StripCurrentModulePrefix(name, moduleName)
            : name;
    }

    private static HashSet<string> GetModuleLocalNamedTypes(LoadedModuleDocument module)
    {
        return module.SyntaxModel.Declarations
            .Where(static declaration => declaration.Kind is DeclarationKind.Struct or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Trait or DeclarationKind.Doctrine or DeclarationKind.TypeAlias)
            .Select(static declaration => declaration.Name)
            .Where(static name => !name.Contains('.', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string QualifyModuleLocalNamedType(
        StarkTypeSymbol type,
        string moduleName,
        ISet<string> localNamedTypes)
    {
        var name = type.TypeArguments is { Count: > 0 }
            ? StarkTypeSymbols.GetGenericBaseName(type.NamedType!)
            : type.NamedType!;

        if (string.IsNullOrEmpty(moduleName)
            || name.Contains('.', StringComparison.Ordinal)
            || !localNamedTypes.Contains(name))
        {
            return name;
        }

        return $"{moduleName}.{name}";
    }

    private static string StripCurrentModulePrefix(string name, string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName))
        {
            return name;
        }

        return name.Replace($"{moduleName}.", string.Empty, StringComparison.Ordinal);
    }

    private static string CanonicalizeManifestTypeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return text;
        }

        var qualifiers = new HashSet<string>(StringComparer.Ordinal);
        var qualifierCount = 0;
        while (qualifierCount < parts.Length && IsManifestTypeQualifier(parts[qualifierCount]))
        {
            qualifiers.Add(parts[qualifierCount]);
            qualifierCount++;
        }

        if (qualifierCount == 0)
        {
            return text;
        }

        var builder = new List<string>(8);
        if (qualifiers.Contains("mut"))
        {
            builder.Add("mut");
        }

        if (qualifiers.Contains("borrow"))
        {
            builder.Add("borrow");
        }

        if (qualifiers.Contains("retborrow"))
        {
            builder.Add("retborrow");
        }

        if (qualifiers.Contains("storeborrow"))
        {
            builder.Add("storeborrow");
        }

        if (qualifiers.Contains("shared"))
        {
            builder.Add("shared");
        }

        if (qualifiers.Contains("frozen"))
        {
            builder.Add("frozen");
        }

        if (qualifiers.Contains("out"))
        {
            builder.Add("out");
        }

        if (qualifiers.Contains("init"))
        {
            builder.Add("init");
        }

        builder.Add(string.Join(" ", parts.Skip(qualifierCount)));
        return string.Join(" ", builder);
    }

    private static bool IsManifestTypeQualifier(string text)
    {
        return text is "mut"
            or "borrow"
            or "retborrow"
            or "storeborrow"
            or "shared"
            or "frozen"
            or "out"
            or "init";
    }

    private static string ModuleNameFromQualifiedName(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        return separator < 0 ? string.Empty : qualifiedName[..separator];
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

    public static bool TryBuildModuleDocument(ResolvedPackageModule module, out LoadedModuleDocument document)
    {
        document = default!;

        if (module.Module.TypedInterface?.Functions.Any(static function => function.Asm is not null) == true
            || !TryBuildModuleSyntaxModel(module, out var syntaxModel)
            || !TryBuildModuleSource(module, out var sourceText))
        {
            return false;
        }

        var parseResult = StarkSyntax.ParseCompilationUnit(sourceText);
        document = new LoadedModuleDocument(
            new ResolvedModuleReference(
                module.Module.ModuleName,
                module.ManifestPath,
                IsExternal: false,
                IsRoot: false,
                ManifestPath: module.ManifestPath,
                LibraryPath: module.LibraryPath),
            parseResult,
            syntaxModel,
            TryBuildLoadedPackageImageFacts(module, out var packageImageFacts) ? packageImageFacts : null);
        return true;
    }

    public static bool TryBuildModuleSyntaxModel(ResolvedPackageModule module, out SyntaxModel syntaxModel)
    {
        syntaxModel = default!;

        if (module.Module.TypedInterface is not { } typedInterface)
        {
            return false;
        }

        var imports = GetImports(module.Module)
            .OrderBy(static import => import.ModuleName, StringComparer.Ordinal)
            .ThenByDescending(static import => import.IsExported)
            .Select(static import => new ImportDeclarationModel(import.ModuleName, import.IsExported))
            .ToArray();
        var genericTemplateBodies = BuildGenericTemplateBodyLookup(module.Module);
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
                        hasBody: TryGetGenericTemplateBody(
                            genericTemplateBodies,
                            $"{module.Module.ModuleName}.{qualifiedMethodName}",
                            method.Parameters,
                            out _))));
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
                    hasBody: TryGetGenericTemplateBody(
                        genericTemplateBodies,
                        function.QualifiedName,
                        function.Parameters,
                        out _))));
        }

        syntaxModel = new SyntaxModel(
            module.Module.ModuleName,
            imports,
            declarations);
        return true;
    }

    private static bool TryBuildImportedTypedTemplateBody(
        StarkPackageTypedTemplateBodyManifest? manifest,
        out ImportedTemplateTypedBodySummary summary)
    {
        summary = null!;

        if (manifest is null)
        {
            return false;
        }

        var statements = new List<ImportedTemplateTypedBodyStatementSummary>(manifest.Statements.Count);
        foreach (var statement in manifest.Statements)
        {
            if (!TryBuildImportedTypedTemplateStatement(statement, out var builtStatement))
            {
                return false;
            }

            statements.Add(builtStatement);
        }

        summary = new ImportedTemplateTypedBodySummary(statements);
        return true;
    }

    private static bool TryBuildImportedTypedTemplateStatement(
        StarkPackageTypedTemplateStatementManifest manifest,
        out ImportedTemplateTypedBodyStatementSummary summary)
    {
        summary = null!;

        if (!TryBuildImportedTypedTemplateExpression(manifest.Expression, out var expression))
        {
            return false;
        }

        if (string.Equals(manifest.Kind, "local-variable", StringComparison.Ordinal))
        {
            if (manifest.Name is null || manifest.StorageClass is null || manifest.Type is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.LocalVariableDeclaration,
                expression,
                Name: manifest.Name,
                StorageClass: manifest.StorageClass,
                IsMutable: manifest.IsMutable,
                Type: BuildTypeSymbol(manifest.Type));
            return true;
        }

        if (string.Equals(manifest.Kind, "return", StringComparison.Ordinal))
        {
            summary = new ImportedTemplateTypedBodyStatementSummary(
                ImportedTemplateTypedBodyStatementKind.Return,
                expression);
            return true;
        }

        return false;
    }

    private static bool TryBuildImportedTypedTemplateExpression(
        StarkPackageTypedTemplateExpressionManifest manifest,
        out ImportedTemplateTypedBodyExpressionSummary summary)
    {
        summary = null!;

        if (string.Equals(manifest.Kind, "name", StringComparison.Ordinal))
        {
            if (manifest.Name is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.NameReference,
                Name: manifest.Name);
            return true;
        }

        if (string.Equals(manifest.Kind, "literal", StringComparison.Ordinal))
        {
            if (manifest.LiteralText is null || manifest.Type is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.Literal,
                LiteralText: manifest.LiteralText,
                Type: BuildTypeSymbol(manifest.Type));
            return true;
        }

        if (string.Equals(manifest.Kind, "object-creation", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.ObjectCreation,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "enum-constructor", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.EnumConstructor,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "enum-call", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.EnumCall,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "enum-value", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.EnumValue,
                Ordinal: manifest.Ordinal);
            return true;
        }

        if (string.Equals(manifest.Kind, "direct-call", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null)
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>((manifest.Arguments ?? []).Count);
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.DirectCall,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        if (string.Equals(manifest.Kind, "field-access", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null || manifest.Arguments is not { Count: 1 })
            {
                return false;
            }

            if (!TryBuildImportedTypedTemplateExpression(manifest.Arguments[0], out var receiver))
            {
                return false;
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.FieldAccess,
                Ordinal: manifest.Ordinal,
                Arguments: [receiver]);
            return true;
        }

        if (string.Equals(manifest.Kind, "member-call", StringComparison.Ordinal))
        {
            if (manifest.Ordinal is null || manifest.Arguments is not { Count: > 0 })
            {
                return false;
            }

            var arguments = new List<ImportedTemplateTypedBodyExpressionSummary>(manifest.Arguments.Count);
            foreach (var argument in manifest.Arguments)
            {
                if (!TryBuildImportedTypedTemplateExpression(argument, out var builtArgument))
                {
                    return false;
                }

                arguments.Add(builtArgument);
            }

            summary = new ImportedTemplateTypedBodyExpressionSummary(
                ImportedTemplateTypedBodyExpressionKind.MemberCall,
                Ordinal: manifest.Ordinal,
                Arguments: arguments);
            return true;
        }

        return false;
    }

    public static bool TryBuildLoadedPackageImageFacts(ResolvedPackageModule module, out LoadedPackageImageFacts facts)
    {
        facts = default!;

        var loadedFunctionEffects = new Dictionary<string, FunctionEffectProfile>(StringComparer.Ordinal);
        var loadedAbiFunctions = new Dictionary<string, AbiFunctionSignature>(StringComparer.Ordinal);
        var loadedConcreteLayouts = new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);
        var loadedEnumLayouts = new Dictionary<string, EnumLayoutSymbol>(StringComparer.Ordinal);
        var loadedFunctionSemantics = new Dictionary<string, ImportedFunctionSemanticSummary>(StringComparer.Ordinal);
        var loadedFunctionTemplates = new Dictionary<string, ImportedFunctionTemplateSummary>(StringComparer.Ordinal);

        if (module.Module.CompilerFacts is { } compilerFacts)
        {
            foreach (var functionEffect in compilerFacts.FunctionEffects)
            {
                if (!TryParseFunctionKind(functionEffect.Kind, out var kind)
                    || !TryParseInlinePreference(functionEffect.InlinePreference, out var inlinePreference))
                {
                    return false;
                }

                loadedFunctionEffects[functionEffect.QualifiedResolvedName] = new FunctionEffectProfile(
                    Name: functionEffect.QualifiedResolvedName,
                    Kind: kind,
                    ReadsArgumentMemory: functionEffect.ReadsArgumentMemory,
                    IsPure: functionEffect.IsPure,
                    NoSync: functionEffect.NoSync,
                    NoFree: functionEffect.NoFree,
                    NoUnwind: functionEffect.NoUnwind,
                    WillReturn: functionEffect.WillReturn,
                    MustProgress: functionEffect.MustProgress,
                    UseFastCallingConvention: functionEffect.UseFastCallingConvention,
                    IsFfi: functionEffect.IsFfi,
                    IsHot: functionEffect.IsHot,
                    IsCold: functionEffect.IsCold,
                    InlinePreference: inlinePreference,
                    IsStrictFp: functionEffect.IsStrictFp);
            }

            foreach (var abiFunction in compilerFacts.AbiFunctions ?? [])
            {
                if (!TryBuildAbiFunctionSignature(abiFunction, out var abiSignature))
                {
                    return false;
                }

                loadedAbiFunctions[abiFunction.QualifiedResolvedName] = abiSignature;
            }

            foreach (var concreteLayout in compilerFacts.ConcreteLayouts ?? [])
            {
                loadedConcreteLayouts[concreteLayout.QualifiedTypeName] = new ConcreteTypeLayout(
                    concreteLayout.SizeBytes,
                    concreteLayout.AlignmentBytes);
            }

            foreach (var enumLayout in compilerFacts.EnumLayouts ?? [])
            {
                if (!TryBuildEnumLayoutSymbol(enumLayout, out var layout))
                {
                    return false;
                }

                loadedEnumLayouts[enumLayout.QualifiedTypeName] = layout;
            }

            foreach (var functionSemantic in compilerFacts.FunctionSemantics ?? [])
            {
                if (!TryBuildImportedFunctionSemanticSummary(functionSemantic, out var summary))
                {
                    return false;
                }

                loadedFunctionSemantics[functionSemantic.QualifiedResolvedName] = summary;
            }
        }

        foreach (var functionTemplate in module.Module.GenericTemplates?.Functions ?? [])
        {
            loadedFunctionTemplates[functionTemplate.QualifiedResolvedName] = new ImportedFunctionTemplateSummary(
                TopLevelStatementCount: functionTemplate.TopLevelStatementCount,
                TypedBodySummary: TryBuildImportedTypedTemplateBody(functionTemplate.TypedBody, out var typedBody)
                    ? typedBody
                    : null,
                DeferredFunctionInstantiations: functionTemplate.DeferredFunctionInstantiations?
                    .Select(trigger => new ImportedDeferredFunctionInstantiationSummary(
                        trigger.CalleeTemplateName,
                        trigger.TypeArguments.Select(BuildTypeSymbol).ToArray()))
                    .ToArray(),
                DeferredTypeInstantiations: functionTemplate.DeferredTypeInstantiations?
                    .Select(trigger => new ImportedDeferredTypeInstantiationSummary(
                        BuildTypeSymbol(trigger.Type)))
                    .ToArray(),
                ObjectCreationSummaries: functionTemplate.ObjectCreations?
                    .Select(objectCreation => new ImportedTemplateObjectCreationSummary(
                        BuildTypeSymbol(objectCreation.CreatedType),
                        objectCreation.Constructor is null
                            ? null
                            : new TypedConstructorShape(
                                objectCreation.Constructor.TypeName,
                                objectCreation.Constructor.Parameters
                                    .Select(parameter => new TypedParameterSymbol(
                                        parameter.Name,
                                        BuildTypeSymbol(parameter.Type)))
                                    .ToArray(),
                                objectCreation.Constructor.IsPrimaryShape),
                        objectCreation.InitializerMembers?
                            .Select(initializerMember => new ImportedTemplateObjectInitializerMemberSummary(
                                initializerMember.FieldName,
                                initializerMember.FieldIndex,
                                BuildTypeSymbol(initializerMember.FieldType)))
                            .ToArray()))
                    .ToArray(),
                EnumConstructorSummaries: functionTemplate.EnumConstructors?
                    .Select(enumConstructor => new ImportedTemplateEnumConstructorSummary(
                        enumConstructor.Ordinal,
                        BuildTypeSymbol(enumConstructor.EnumType),
                        enumConstructor.VariantName,
                        enumConstructor.Members?
                            .Select(member => new ImportedTemplateEnumConstructorMemberSummary(
                                member.FieldName,
                                member.FieldIndex,
                                BuildTypeSymbol(member.FieldType)))
                            .ToArray()))
                    .ToArray(),
                EnumCallSummaries: functionTemplate.EnumCalls?
                    .Select(enumCall => new ImportedTemplateEnumCallSummary(
                        enumCall.Ordinal,
                        BuildTypeSymbol(enumCall.EnumType),
                        enumCall.VariantName))
                    .ToArray(),
                EnumValueSummaries: functionTemplate.EnumValues?
                    .Select(enumValue => new ImportedTemplateEnumValueSummary(
                        enumValue.Ordinal,
                        BuildTypeSymbol(enumValue.EnumType),
                        enumValue.VariantName))
                    .ToArray(),
                EnumPatternSummaries: functionTemplate.EnumPatterns?
                    .Select(enumPattern => new ImportedTemplateEnumPatternSummary(
                        enumPattern.Ordinal,
                        BuildTypeSymbol(enumPattern.EnumType),
                        enumPattern.VariantName,
                        enumPattern.Members?
                            .Select(member => new ImportedTemplateEnumPatternMemberSummary(
                                member.FieldName,
                                member.FieldIndex,
                                BuildTypeSymbol(member.FieldType)))
                            .ToArray()))
                    .ToArray(),
                AggregatePatternSummaries: functionTemplate.AggregatePatterns?
                    .Select(aggregatePattern => new ImportedTemplateAggregatePatternSummary(
                        aggregatePattern.Ordinal,
                        BuildTypeSymbol(aggregatePattern.Type)))
                    .ToArray(),
                LocalDeclarationSummaries: functionTemplate.LocalDeclarations?
                    .Select(local => new ImportedTemplateLocalDeclarationSummary(
                        local.Kind,
                        local.Line,
                        local.Column,
                        BuildTypeSymbol(local.Type)))
                    .ToArray(),
                ConversionSummaries: functionTemplate.Conversions?
                    .Select(conversion => new ImportedTemplateConversionSummary(
                        conversion.Ordinal,
                        BuildTypeSymbol(conversion.TargetType)))
                    .ToArray(),
                DirectCallSummaries: functionTemplate.DirectCalls?
                    .Select(directCall => new ImportedTemplateDirectCallSummary(
                        directCall.Ordinal,
                        new TypedFunctionSignature(
                            directCall.QualifiedResolvedName,
                            BuildTypeSymbol(directCall.ReturnType),
                            directCall.Parameters
                                .Select(parameter => new TypedParameterSymbol(
                                    parameter.Name,
                                    BuildTypeSymbol(parameter.Type)))
                                .ToArray(),
                            SourceName: directCall.QualifiedSourceName,
                            TemplateName: directCall.QualifiedTemplateName,
                            TypeArguments: directCall.TypeArguments?.Select(BuildTypeSymbol).ToArray())))
                    .ToArray(),
                FieldAccessSummaries: functionTemplate.FieldAccesses?
                    .Select(fieldAccess => new ImportedTemplateFieldAccessSummary(
                        fieldAccess.Ordinal,
                        fieldAccess.FieldName,
                        fieldAccess.FieldIndex,
                        BuildTypeSymbol(fieldAccess.FieldType)))
                    .ToArray(),
                MemberCallSummaries: functionTemplate.MemberCalls?
                    .Select(memberCall => new ImportedTemplateMemberCallSummary(
                        memberCall.Ordinal,
                        new TypedFunctionSignature(
                            memberCall.QualifiedResolvedName,
                            BuildTypeSymbol(memberCall.ReturnType),
                            memberCall.Parameters
                                .Select(parameter => new TypedParameterSymbol(
                                    parameter.Name,
                                    BuildTypeSymbol(parameter.Type)))
                                .ToArray(),
                            SourceName: memberCall.QualifiedSourceName,
                            TemplateName: memberCall.QualifiedTemplateName,
                            TypeArguments: memberCall.TypeArguments?.Select(BuildTypeSymbol).ToArray())))
                    .ToArray());
        }

        if (loadedFunctionEffects.Count == 0
            && loadedAbiFunctions.Count == 0
            && loadedConcreteLayouts.Count == 0
            && loadedEnumLayouts.Count == 0
            && loadedFunctionSemantics.Count == 0
            && loadedFunctionTemplates.Count == 0)
        {
            return false;
        }

        facts = new LoadedPackageImageFacts(
            loadedFunctionEffects,
            loadedAbiFunctions,
            loadedConcreteLayouts,
            loadedEnumLayouts,
            loadedFunctionSemantics,
            loadedFunctionTemplates);
        return true;
    }

    private static bool TryBuildAbiFunctionSignature(
        StarkPackageAbiFunctionManifest abiFunction,
        out AbiFunctionSignature signature)
    {
        signature = default!;

        var parameters = new List<AbiParameterSymbol>(abiFunction.Parameters.Count);
        foreach (var parameter in abiFunction.Parameters)
        {
            if (!TryParseAbiParameterKind(parameter.Kind, out var kind))
            {
                return false;
            }

            parameters.Add(new AbiParameterSymbol(
                parameter.SourceName,
                parameter.LlvmName,
                BuildTypeSymbol(parameter.SourceType),
                BuildTypeSymbol(parameter.LlvmType),
                kind));
        }

        signature = new AbiFunctionSignature(
            abiFunction.QualifiedResolvedName,
            abiFunction.SymbolName,
            BuildTypeSymbol(abiFunction.SourceReturnType),
            BuildTypeSymbol(abiFunction.LlvmReturnType),
            parameters,
            abiFunction.IsFfi,
            SourceName: abiFunction.SourceName,
            UsesFastCallingConvention: abiFunction.UsesFastCallingConvention);
        return true;
    }

    private static bool TryBuildImportedFunctionSemanticSummary(
        StarkPackageFunctionSemanticManifest functionSemantic,
        out ImportedFunctionSemanticSummary summary)
    {
        summary = default!;

        var parameters = functionSemantic.Parameters is null
            ? null
            : new List<ParameterMemoryEffectSummary>(functionSemantic.Parameters.Count);
        if (parameters is not null)
        {
            foreach (var parameter in functionSemantic.Parameters!)
            {
                if (!TryParseParameterCaptureKind(parameter.CaptureKind, out var captureKind))
                {
                    return false;
                }

                parameters.Add(new ParameterMemoryEffectSummary(
                    parameter.Name,
                    parameter.Type,
                    parameter.IsMemoryBacked,
                    parameter.GuaranteedNonNull,
                    parameter.GuaranteedReadOnly,
                    parameter.GuaranteedWriteOnly,
                    parameter.GuaranteedNoAlias,
                    parameter.DereferenceableBytes,
                    parameter.AlignmentBytes,
                    parameter.Reads,
                    parameter.Writes,
                    captureKind));
            }
        }

        var memoryEffects = functionSemantic.MemoryEffects is null
            ? null
            : new FunctionMemoryEffectSummary(
                functionSemantic.MemoryEffects.ReadsArgumentMemory,
                functionSemantic.MemoryEffects.WritesArgumentMemory,
                functionSemantic.MemoryEffects.CapturesArgumentMemory,
                functionSemantic.MemoryEffects.ReadsOtherMemory,
                functionSemantic.MemoryEffects.WritesOtherMemory);

        summary = new ImportedFunctionSemanticSummary(
            functionSemantic.QualifiedResolvedName,
            functionSemantic.CalledFunctions,
            memoryEffects,
            parameters);
        return true;
    }

    private static bool TryBuildEnumLayoutSymbol(
        StarkPackageEnumLayoutManifest enumLayout,
        out EnumLayoutSymbol layout)
    {
        layout = default!;

        if (!TryParseEnumLayoutKind(enumLayout.Kind, out var kind))
        {
            return false;
        }

        var tagField = new FieldSymbol(
            enumLayout.TagField.Name,
            BuildTypeSymbol(enumLayout.TagField.Type));
        var orderedFields = enumLayout.OrderedFields
            .Select(field => new FieldSymbol(field.Name, BuildTypeSymbol(field.Type)))
            .ToArray();
        var variants = new Dictionary<string, EnumVariantLayoutSymbol>(StringComparer.Ordinal);

        foreach (var variant in enumLayout.Variants)
        {
            variants[variant.Name] = new EnumVariantLayoutSymbol(
                variant.Name,
                variant.TagValue,
                variant.UsesNamedFields,
                variant.Fields
                    .Select(field => new EnumVariantLayoutFieldSymbol(
                        field.SourcePosition,
                        field.SourceFieldName,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        BuildTypeSymbol(field.Type)))
                    .ToArray());
        }

        layout = new EnumLayoutSymbol(
            enumLayout.QualifiedTypeName,
            kind,
            tagField,
            orderedFields,
            variants);
        return true;
    }

    private static StarkTypeSymbol BuildTypeSymbol(StarkPackageTypeReference type)
    {
        StarkTypeSymbol core = type.Kind switch
        {
            "error" => StarkTypeSymbols.Error,
            "void" => StarkTypeSymbols.Void,
            "bool" => StarkTypeSymbols.Bool,
            "ascii" => StarkTypeSymbols.Ascii,
            "unicode" => StarkTypeSymbols.Unicode,
            "null" => StarkTypeSymbols.Null,
            "integer" => StarkTypeSymbols.Integer(
                type.BitWidth ?? 32,
                type.RangeMin is null ? null : BigInteger.Parse(type.RangeMin, System.Globalization.CultureInfo.InvariantCulture),
                type.RangeMax is null ? null : BigInteger.Parse(type.RangeMax, System.Globalization.CultureInfo.InvariantCulture)),
            "float" => StarkTypeSymbols.Float(type.BitWidth ?? 32),
            "rawpointer" => StarkTypeSymbols.RawPointer(BuildTypeSymbol(type.ElementType!), type.IsMutablePointer),
            "fixedarray" => StarkTypeSymbols.FixedArray(BuildTypeSymbol(type.ElementType!), type.FixedLength),
            "slice" => StarkTypeSymbols.Slice(BuildTypeSymbol(type.ElementType!)),
            "named" when type.TypeArguments is { Count: > 0 } => StarkTypeSymbols.GenericInstantiation(
                type.Name ?? "<unnamed>",
                type.TypeArguments.Select(BuildTypeSymbol).ToArray()),
            "named" => StarkTypeSymbols.Named(type.Name ?? "<unnamed>"),
            _ => StarkTypeSymbols.Error
        };

        return StarkTypeSymbols.ApplyQualifiers(
            core,
            borrowKind: ParseBorrowKind(type.BorrowKind),
            accessKind: ParseAccessKind(type.AccessKind),
            initializationKind: ParseInitializationKind(type.InitializationKind),
            isMutableView: type.IsMutableView);
    }

    public static bool TryBuildModuleSource(ResolvedPackageModule module, out string sourceText)
    {
        if (module.Module is null)
        {
            sourceText = string.Empty;
            return false;
        }

        var builder = new StringBuilder();
        var genericTemplateBodies = BuildGenericTemplateBodyLookup(module.Module);
        var typedInterface = module.Module.TypedInterface;
        var typeAliases = typedInterface?.TypeAliases?.Select(ConvertTypeAliasManifest).ToArray()
            ?? (module.Module.TypeAliases ?? []);
        var types = typedInterface?.Types.Select(ConvertTypeManifest).ToArray()
            ?? module.Module.Types;
        var globals = typedInterface?.Globals.Select(ConvertGlobalManifest).ToArray()
            ?? module.Module.Globals;
        var functions = typedInterface?.Functions.Select(ConvertFunctionManifest).ToArray()
            ?? module.Module.Functions;
        var imports = GetImports(module.Module);

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
                        $"{module.Module.ModuleName}.{type.Name}.{method.Name}",
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
            TryGetGenericTemplateBody(genericTemplateBodies, function.QualifiedName, function.Parameters, out var functionBodyText);
            EmitFunction(builder, function, functionBodyText);
        }

        sourceText = builder.ToString();
        return true;
    }

    private static IReadOnlyList<StarkPackageImportManifest> GetImports(StarkPackageModuleManifest module)
    {
        if (module.Imports is { Count: > 0 })
        {
            return module.Imports;
        }

        return module.ReExports
            .Select(static reExport => new StarkPackageImportManifest(reExport.ModuleName, IsExported: true))
            .ToArray();
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
        bool hasBody = false)
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
            GenericParameterNames: genericParameters ?? []);
    }

    private static Dictionary<string, string> BuildGenericTemplateBodyLookup(StarkPackageModuleManifest module)
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var template in module.GenericTemplates?.Functions ?? [])
        {
            templates[BuildGenericTemplateLookupKey(template.QualifiedName, template.OverloadKey)] = template.BodyText;
        }

        return templates;
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

    private static StarkBorrowKind ParseBorrowKind(string? borrowKind)
    {
        return borrowKind switch
        {
            "borrow" => StarkBorrowKind.Borrow,
            "retborrow" => StarkBorrowKind.RetBorrow,
            "storeborrow" => StarkBorrowKind.StoreBorrow,
            _ => StarkBorrowKind.None
        };
    }

    private static StarkAccessKind ParseAccessKind(string? accessKind)
    {
        return accessKind switch
        {
            "shared" => StarkAccessKind.Shared,
            "frozen" => StarkAccessKind.Frozen,
            _ => StarkAccessKind.None
        };
    }

    private static StarkInitializationKind ParseInitializationKind(string? initializationKind)
    {
        return initializationKind switch
        {
            "out" => StarkInitializationKind.Out,
            "init" => StarkInitializationKind.Init,
            _ => StarkInitializationKind.None
        };
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

    private static string RenderTypeReference(StarkPackageTypeReference type)
    {
        var qualifiers = new List<string>(8);
        if (type.IsMutableView)
        {
            qualifiers.Add("mut");
        }

        if (!string.IsNullOrWhiteSpace(type.BorrowKind))
        {
            qualifiers.Add(type.BorrowKind);
        }

        if (!string.IsNullOrWhiteSpace(type.AccessKind))
        {
            qualifiers.Add(type.AccessKind);
        }

        if (!string.IsNullOrWhiteSpace(type.InitializationKind))
        {
            qualifiers.Add(type.InitializationKind);
        }

        var core = type.Kind switch
        {
            "error" => "<error>",
            "void" => "void",
            "bool" => "bool",
            "ascii" => "ascii",
            "unicode" => "unicode",
            "null" => "null",
            "integer" => type.RangeMin is not null && type.RangeMax is not null
                ? $"i{type.BitWidth}[{type.RangeMin} {type.RangeMax}]"
                : $"i{type.BitWidth}",
            "float" => $"f{type.BitWidth}",
            "rawpointer" => $"{(type.IsMutablePointer ? "rawmutptr" : "rawptr")}<{RenderTypeReference(type.ElementType!)}>",
            "fixedarray" => $"{RenderTypeReference(type.ElementType!)}[{(type.FixedLength is { } fixedLength ? fixedLength.ToString() : "?")}]",
            "slice" => $"{RenderTypeReference(type.ElementType!)}[]",
            "named" when type.TypeArguments is { Count: > 0 } => $"{type.Name}<{string.Join(", ", type.TypeArguments.Select(RenderTypeReference))}>",
            "named" => type.Name ?? "<unnamed>",
            _ => type.Name ?? type.Kind
        };

        return qualifiers.Count == 0
            ? core
            : $"{string.Join(" ", qualifiers)} {core}";
    }
}
