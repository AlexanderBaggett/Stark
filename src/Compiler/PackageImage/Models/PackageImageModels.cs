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
    IReadOnlyList<StarkPackageGlobalManifest> Globals,
    IReadOnlyList<StarkPackageTypeAliasManifest>? TypeAliases = null,
    StarkPackageTypedInterfaceSection? TypedInterface = null,
    StarkPackageCompilerFactsSection? CompilerFacts = null,
    StarkPackageGenericTemplateSection? GenericTemplates = null,
    IReadOnlyList<StarkPackageImportManifest>? Imports = null,
    StarkPackageSourceSurfaceSection? SourceSurface = null,
    StarkPackageCompilerSectionsManifest? CompilerSections = null)
{
    public StarkPackageTypedInterfaceSection? EffectiveTypedInterface =>
        CompilerSections?.TypedInterface ?? TypedInterface;

    public StarkPackageCompilerFactsSection? EffectiveCompilerFacts =>
        CompilerSections?.CompilerFacts ?? CompilerFacts;

    public StarkPackageGenericTemplateSection? EffectiveGenericTemplates =>
        CompilerSections?.GenericTemplates ?? GenericTemplates;

    public StarkPackageSourceSurfaceSection EffectiveSourceSurface =>
        SourceSurface
        ?? new StarkPackageSourceSurfaceSection(
            Imports,
            ReExports,
            Functions,
            Types,
            Globals,
            TypeAliases);
}

internal sealed record StarkPackageSourceSurfaceSection(
    IReadOnlyList<StarkPackageImportManifest>? Imports = null,
    IReadOnlyList<StarkPackageReExportManifest>? ReExports = null,
    IReadOnlyList<StarkPackageFunctionManifest>? Functions = null,
    IReadOnlyList<StarkPackageTypeManifest>? Types = null,
    IReadOnlyList<StarkPackageGlobalManifest>? Globals = null,
    IReadOnlyList<StarkPackageTypeAliasManifest>? TypeAliases = null);

internal sealed record StarkPackageCompilerSectionsManifest(
    StarkPackageTypedInterfaceSection? TypedInterface = null,
    StarkPackageCompilerFactsSection? CompilerFacts = null,
    StarkPackageGenericTemplateSection? GenericTemplates = null);

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
    IReadOnlyList<string>? GenericParameters = null,
    bool IsHot = false,
    bool IsCold = false,
    string InlinePreference = "inlinehint",
    bool HasExplicitInlinePreference = false);

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
    IReadOnlyList<string>? GenericParameters = null,
    bool IsHot = false,
    bool IsCold = false,
    string InlinePreference = "inlinehint",
    bool HasExplicitInlinePreference = false,
    bool IsStatic = false,
    string? Visibility = null);

internal sealed record StarkPackageDestructorManifest(
    bool IsMutable,
    string BodyText);

internal sealed record StarkPackageConstructorManifest(
    IReadOnlyList<StarkPackageParameterManifest> Parameters,
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
    StarkPackageDestructorManifest? Destructor = null,
    IReadOnlyList<StarkPackageConstructorManifest>? Constructors = null);

internal sealed record StarkPackageEnumVariantManifest(
    string Name,
    bool UsesNamedFields,
    IReadOnlyList<StarkPackageFieldManifest> Fields);

internal sealed record StarkPackageFieldManifest(
    string Name,
    string Type,
    string? Visibility = null);

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
    IReadOnlyList<StarkPackageTypedTypeAliasManifest>? TypeAliases = null,
    IReadOnlyList<StarkPackageImportManifest>? Imports = null);

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

internal sealed record StarkPackageTypedConstructorManifest(
    IReadOnlyList<StarkPackageTypedParameterManifest> Parameters);

internal sealed record StarkPackageTypedFieldManifest(
    string Name,
    StarkPackageTypeReference Type,
    string? Visibility = null);

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
    IReadOnlyList<string>? GenericParameters = null,
    string? QualifiedResolvedName = null,
    string? PublishedOverloadKey = null,
    bool HasGenericTemplateBody = false,
    bool IsHot = false,
    bool IsCold = false,
    string InlinePreference = "inlinehint",
    bool HasExplicitInlinePreference = false);

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
    IReadOnlyList<string>? GenericParameters = null,
    string? QualifiedResolvedName = null,
    string? PublishedOverloadKey = null,
    bool HasGenericTemplateBody = false,
    bool IsHot = false,
    bool IsCold = false,
    string InlinePreference = "inlinehint",
    bool HasExplicitInlinePreference = false,
    bool IsStatic = false,
    string? Visibility = null);

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
    StarkPackageDestructorManifest? Destructor = null,
    IReadOnlyList<StarkPackageTypedConstructorManifest>? Constructors = null);

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
    string? AssignmentOperator = null,
    int? Ordinal = null,
    IReadOnlyList<StarkPackageTypedTemplateExpressionManifest>? Arguments = null,
    IReadOnlyList<string>? MemberNames = null,
    string? LiteralText = null,
    StarkPackageTypeReference? Type = null,
    StarkPackageTypedTemplateExpressionManifest? TargetExpression = null,
    IReadOnlyList<string>? OperatorNames = null);

internal sealed record StarkPackageTypedTemplatePatternManifest(
    string Kind,
    string? Name = null,
    int? Ordinal = null,
    StarkPackageTypedTemplateExpressionManifest? Expression = null,
    IReadOnlyList<StarkPackageTypedTemplatePatternManifest>? Members = null);

internal sealed record StarkPackageTypedTemplateSwitchCaseManifest(
    string Kind,
    int? Ordinal = null,
    string? Name = null,
    StarkPackageTypedTemplateExpressionManifest? Expression = null,
    StarkPackageTypedTemplateExpressionManifest? GuardExpression = null,
    IReadOnlyList<StarkPackageTypedTemplatePatternManifest>? Members = null,
    IReadOnlyList<StarkPackageTypedTemplateStatementManifest>? Statements = null);

internal sealed record StarkPackageTypedTemplateStatementManifest(
    string Kind,
    StarkPackageTypedTemplateExpressionManifest Expression = null!,
    string? Name = null,
    string? AssignmentOperator = null,
    string? StorageClass = null,
    bool IsMutable = false,
    bool IsConstant = false,
    StarkPackageTypeReference? Type = null,
    string? LoopBehavior = null,
    IReadOnlyList<StarkPackageTypedTemplateSwitchCaseManifest>? SwitchCases = null,
    IReadOnlyList<StarkPackageTypedTemplateStatementManifest>? InitializerStatements = null,
    IReadOnlyList<StarkPackageTypedTemplateStatementManifest>? IteratorStatements = null,
    IReadOnlyList<StarkPackageTypedTemplateStatementManifest>? BodyStatements = null,
    IReadOnlyList<StarkPackageTypedTemplateStatementManifest>? ThenStatements = null,
    IReadOnlyList<StarkPackageTypedTemplateStatementManifest>? ElseStatements = null,
    StarkPackageTypedTemplateExpressionManifest? TargetExpression = null);

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
    string? BodyText,
    int? TopLevelStatementCount = null,
    int? EstimatedBodyCost = null,
    StarkPackageFunctionSemanticManifest? Semantics = null,
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

internal sealed record StarkPackageCallArgumentMemoryEffectsManifest(
    int ArgumentIndex,
    string? CallerParameterName,
    string? CalleeParameterName,
    bool Reads,
    bool Writes,
    string CaptureKind);

internal sealed record StarkPackageFunctionCallManifest(
    string CalleeName,
    StarkPackageFunctionMemoryEffectsManifest MemoryEffects,
    IReadOnlyList<StarkPackageCallArgumentMemoryEffectsManifest> Arguments);

internal sealed record StarkPackageFunctionSemanticManifest(
    string QualifiedResolvedName,
    string DeclaredKind,
    string EffectiveKind,
    IReadOnlyList<string> CalledFunctions,
    StarkPackageFunctionMemoryEffectsManifest? MemoryEffects = null,
    IReadOnlyList<StarkPackageParameterMemoryEffectsManifest>? Parameters = null,
    IReadOnlyList<StarkPackageFunctionCallManifest>? Calls = null,
    StarkPackageFunctionOptimizationManifest? Optimization = null);

internal sealed record StarkPackageFunctionOptimizationManifest(
    int DirectCallCount,
    int MemberCallCount,
    int FieldAccessCount,
    int IndexAccessCount,
    int BranchStatementCount,
    int LoopStatementCount,
    int ObjectCreationCount,
    bool IsSingleReturnDirectCallForwarder,
    bool IsSingleReturnMemberCallForwarder,
    bool IsSingleReturnFieldAccessWrapper,
    bool IsSingleReturnIndexAccessWrapper,
    bool IsSingleReturnConversionWrapper,
    bool IsSingleReturnAddressOfWrapper,
    bool IsSingleReturnDereferenceWrapper,
    bool IsSingleReturnBinaryOperatorWrapper,
    bool IsSingleReturnComparisonWrapper,
    bool IsSingleReturnAggregateConstructionWrapper,
    bool IsSimpleLocalUpdateWrapper,
    bool IsTerminalSelectionWrapper);

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

internal sealed record ResolvedPackageModule(
    string ManifestPath,
    string LibraryPath,
    StarkPackageManifest Manifest,
    StarkPackageModuleManifest Module);
