using System.Numerics;
using Stark.Parsing;

namespace Stark.Compiler;

public static class CompilerArtifactKeys
{
    public static readonly ArtifactKey<ParseResult> ParseResult = new("parse.result");
    public static readonly ArtifactKey<SyntaxModel> SyntaxModel = new("syntax.model");
    public static readonly ArtifactKey<DeclarationIndex> DeclarationIndex = new("declarations.index");
    public static readonly ArtifactKey<ModuleGraph> ModuleGraph = new("modules.graph");
    public static readonly ArtifactKey<SourceModuleParseCache> SourceModuleParseCache = new("modules.source-parse-cache");
    public static readonly ArtifactKey<LoadedModuleSet> LoadedModules = new("modules.loaded");
    public static readonly ArtifactKey<SymbolCatalog> SymbolCatalog = new("symbols.catalog");
    public static readonly ArtifactKey<FunctionEffectModel> FunctionEffects = new("semantics.function-effects");
    public static readonly ArtifactKey<ClosedWorldOptimizationModel> ClosedWorldOptimization = new("semantics.closed-world-optimization");
    public static readonly ArtifactKey<TypeCheckModel> TypeCheckModel = new("typing.model");
    public static readonly ArtifactKey<InstantiationOwnershipModel> InstantiationOwnership = new("typing.instantiation-ownership");
    public static readonly ArtifactKey<MonomorphizationPlanModel> MonomorphizationPlan = new("typing.monomorphization-plan");
    public static readonly ArtifactKey<SpecializationPlanModel> SpecializationPlan = new("semantics.specialization-plan");
    public static readonly ArtifactKey<SpecializationCodegenStrategyModel> SpecializationCodegenStrategy = new("codegen.specialization-strategy");
    public static readonly ArtifactKey<EnumLayoutModel> EnumLayoutModel = new("typing.enum-layout");
    public static readonly ArtifactKey<SemanticValidationModel> SemanticValidation = new("semantics.validation");
    public static readonly ArtifactKey<OwnershipValidationModel> OwnershipValidation = new("semantics.ownership");
    public static readonly ArtifactKey<HighLevelIrModule> HighLevelIr = new("lowering.hir");
    public static readonly ArtifactKey<MidLevelIrModule> MidLevelIr = new("lowering.mir");
    public static readonly ArtifactKey<SsaIrModule> SsaIr = new("lowering.ssa");
    public static readonly ArtifactKey<SsaIrModule> OptimizedSsaIr = new("lowering.ssa.optimized");
    public static readonly ArtifactKey<SsaValueFactModel> SsaValueFacts = new("optimization.ssa.value-facts");
    public static readonly ArtifactKey<AbiModel> AbiModel = new("lowering.abi");
    public static readonly ArtifactKey<LlvmIrModule> LlvmIrModule = new("codegen.llvm-ir");
}

public enum DeclarationKind
{
    Function,
    Struct,
    Record,
    Enum,
    Trait,
    Doctrine,
    TypeAlias,
    GlobalConstant,
    GlobalVariable
}

public enum StarkVisibility
{
    Module,
    Internal,
    Public,
    Export
}

public enum StarkFunctionKind
{
    Fn,
    Finite,
    Law,
    FiniteLaw
}

internal static class FunctionKindFacts
{
    public static bool IsLaw(StarkFunctionKind kind)
    {
        return kind is StarkFunctionKind.Law or StarkFunctionKind.FiniteLaw;
    }

    public static bool IsFinite(StarkFunctionKind kind)
    {
        return kind is StarkFunctionKind.Finite or StarkFunctionKind.FiniteLaw;
    }

    public static StarkFunctionKind Combine(bool isLaw, bool isFinite)
    {
        return (isLaw, isFinite) switch
        {
            (true, true) => StarkFunctionKind.FiniteLaw,
            (true, false) => StarkFunctionKind.Law,
            (false, true) => StarkFunctionKind.Finite,
            _ => StarkFunctionKind.Fn
        };
    }

    public static int Rank(StarkFunctionKind kind)
    {
        return kind switch
        {
            StarkFunctionKind.Fn => 0,
            StarkFunctionKind.Finite => 1,
            StarkFunctionKind.Law => 2,
            StarkFunctionKind.FiniteLaw => 3,
            _ => 0
        };
    }
}

internal static class CallableValueFacts
{
    public static string BuildLambdaFunctionName(string enclosingFunctionName, SourceLocation location)
    {
        return $"{enclosingFunctionName}.__lambda_{location.Line}_{location.Column}";
    }

    public static TypedFunctionSignature BuildLambdaSignature(LambdaTypingRecord lambda)
    {
        var returnType = lambda.FunctionPointerType.FunctionPointerReturnType ?? StarkTypeSymbols.Error;
        var parameterTypes = lambda.FunctionPointerType.FunctionPointerParameterTypes ?? [];
        var parameters = parameterTypes
            .Select((type, index) => new TypedParameterSymbol(lambda.ParameterNames[index], type))
            .ToArray();

        return new TypedFunctionSignature(
            lambda.FunctionName,
            returnType,
            parameters,
            SourceName: lambda.FunctionName,
            Kind: lambda.FunctionPointerType.FunctionPointerKind ?? StarkFunctionKind.Fn);
    }

    public static FunctionEffectProfile BuildLambdaEffectProfile(LambdaTypingRecord lambda)
    {
        var kind = lambda.FunctionPointerType.FunctionPointerKind ?? StarkFunctionKind.Fn;
        var isLaw = FunctionKindFacts.IsLaw(kind);
        var isFinite = FunctionKindFacts.IsFinite(kind);

        return new FunctionEffectProfile(
            lambda.FunctionName,
            kind,
            ReadsArgumentMemory: false,
            IsPure: isLaw,
            NoSync: isLaw,
            NoFree: isLaw,
            NoUnwind: true,
            WillReturn: isFinite,
            MustProgress: isFinite,
            UseFastCallingConvention: true,
            IsFfi: false,
            IsVarargs: false,
            IsHot: false,
            IsCold: false,
            InlinePreference: InlinePreference.InlineHint,
            IsStrictFp: false);
    }
}

public enum InlinePreference
{
    InlineHint,
    Inline,
    NoInline
}

public sealed record FunctionModifierSet(
    InlinePreference InlinePreference,
    bool HasExplicitInlinePreference,
    bool IsHot,
    bool IsCold,
    bool IsFfi,
    bool IsVarargs,
    bool IsStrictFp,
    bool IsUnsafe = false);

public enum StarkAsmArchitecture
{
    Unknown,
    X86_64,
    AArch64,
    RiscV64,
    X86,
    Arm32
}

public sealed record AsmInputOperandModel(
    string RegisterName,
    string ValueName);

public sealed record AsmOutputOperandModel(
    string RegisterName,
    string ValueName,
    bool BindsReturnValue);

public sealed record AsmFunctionModel(
    StarkAsmArchitecture Architecture,
    string ArchitectureText,
    string TemplateText,
    IReadOnlyList<AsmInputOperandModel> Inputs,
    IReadOnlyList<AsmOutputOperandModel> Outputs,
    IReadOnlyList<string> Clobbers);

public sealed record ParameterModel(
    string Name,
    string TypeText,
    bool IsDisjoint = false,
    bool IsConst = false,
    string? RawPointerElementCountExpression = null);

public sealed record ParameterDisjointGroup(IReadOnlyList<string> ParameterNames);

public sealed record ImportDeclarationModel(
    string ModuleName,
    bool IsExported)
{
    public bool IsReExport => IsExported;
}

public sealed record FunctionDeclarationModel(
    string Name,
    StarkFunctionKind Kind,
    string ReturnType,
    IReadOnlyList<ParameterModel> Parameters,
    FunctionModifierSet Modifiers,
    bool HasBody,
    AsmFunctionModel? Asm = null,
    IReadOnlyList<string>? GenericParameterNames = null,
    string? PublishedOverloadKey = null,
    bool IsStatic = false,
    IReadOnlyList<ModuleAttributeModel>? Attributes = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    IReadOnlyList<ParameterDisjointGroup>? DisjointParameterGroups = null)
{
    public IReadOnlyList<string> GenericParams => GenericParameterNames ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 };
    public IReadOnlyList<ParameterDisjointGroup> DisjointGroups => DisjointParameterGroups ?? [];
}

public sealed record DestructorDeclarationModel(
    bool IsMutable,
    IReadOnlyList<ModuleAttributeModel>? Attributes = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default);

public sealed record TypeAliasDeclarationModel(
    string Name,
    string AliasedType,
    IReadOnlyList<string> GenericParameters);

public sealed record TypeAliasSymbol(
    string Name,
    string ModuleName,
    StarkVisibility Visibility,
    StarkTypeSymbol TargetType,
    IReadOnlyList<string>? GenericParameterNames = null,
    bool IsExternal = false)
{
    public IReadOnlyList<string> GenericParams => GenericParameterNames ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 };
}

public sealed record TopLevelDeclarationModel(
    string Name,
    DeclarationKind Kind,
    StarkVisibility Visibility,
    FunctionDeclarationModel? Function,
    DestructorDeclarationModel? Destructor = null,
    TypeAliasDeclarationModel? TypeAlias = null,
    IReadOnlyList<ModuleAttributeModel>? Attributes = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default);

public enum ModuleBackendOptimizationMode
{
    Default,
    Opaque
}

public sealed record ModuleAttributeModel(
    string Name,
    IReadOnlyList<string> Arguments);

public sealed record SyntaxModel(
    string ModuleName,
    IReadOnlyList<ImportDeclarationModel> Imports,
    IReadOnlyList<TopLevelDeclarationModel> Declarations,
    IReadOnlyList<ModuleAttributeModel>? ModuleAttributes = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default);

public sealed record DeclarationIndex(
    string ModuleName,
    IReadOnlyDictionary<string, IReadOnlyList<TopLevelDeclarationModel>> ByName,
    IReadOnlyList<TopLevelDeclarationModel> OrderedDeclarations);

public sealed record ResolvedModuleReference(
    string ModuleName,
    string? FilePath = null,
    bool IsExternal = false,
    bool IsRoot = false,
    string? ManifestPath = null,
    string? LibraryPath = null);

public sealed record ModuleImportEdge(
    string FromModule,
    string RequestedModule,
    bool IsResolved,
    ResolvedModuleReference? Target,
    bool IsExported);

public sealed record ModuleGraph(
    string RootModuleName,
    IReadOnlyDictionary<string, ResolvedModuleReference> Modules,
    IReadOnlyList<ModuleImportEdge> Imports,
    IReadOnlySet<string> AccessibleModules)
{
    public bool HasModule(string moduleName) => AccessibleModules.Contains(moduleName);

    public IReadOnlySet<string> GetAccessibleModules(string fromModule)
    {
        return string.Equals(fromModule, RootModuleName, StringComparison.Ordinal)
            ? AccessibleModules
            : CollectAccessibleModules(fromModule);
    }

    public IEnumerable<string> EnumerateAccessibleModuleQualifiedNames(string fromModule, string localName)
    {
        if (string.IsNullOrWhiteSpace(fromModule)
            || string.IsNullOrWhiteSpace(localName)
            || localName.Contains('.', StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var moduleName in GetAccessibleModules(fromModule).OrderBy(static moduleName => moduleName, StringComparer.Ordinal))
        {
            yield return $"{moduleName}.{localName}";
        }
    }

    public bool HasModuleNamespace(string moduleNamePrefix)
    {
        var prefix = $"{moduleNamePrefix}.";
        return AccessibleModules.Any(module => module.StartsWith(prefix, StringComparison.Ordinal));
    }

    public bool CanAccessModule(string fromModule, string moduleName)
    {
        if (string.Equals(fromModule, RootModuleName, StringComparison.Ordinal))
        {
            return HasModule(moduleName);
        }

        if (string.Equals(fromModule, moduleName, StringComparison.Ordinal))
        {
            return true;
        }

        return CollectAccessibleModules(fromModule).Contains(moduleName);
    }

    public bool CanAccessModuleNamespace(string fromModule, string moduleNamePrefix)
    {
        if (string.Equals(fromModule, RootModuleName, StringComparison.Ordinal))
        {
            return HasModuleNamespace(moduleNamePrefix);
        }

        var prefix = $"{moduleNamePrefix}.";
        return CollectAccessibleModules(fromModule)
            .Any(module => module.StartsWith(prefix, StringComparison.Ordinal));
    }

    private IReadOnlySet<string> CollectAccessibleModules(string fromModule)
    {
        var accessibleModules = new HashSet<string>(StringComparer.Ordinal);
        var accessibleQueue = new Queue<string>();

        foreach (var edge in Imports.Where(edge => edge.IsResolved && string.Equals(edge.FromModule, fromModule, StringComparison.Ordinal)))
        {
            var targetModule = edge.Target!.ModuleName;
            if (accessibleModules.Add(targetModule))
            {
                accessibleQueue.Enqueue(targetModule);
            }
        }

        while (accessibleQueue.Count != 0)
        {
            var currentModule = accessibleQueue.Dequeue();

            foreach (var edge in Imports.Where(edge =>
                         edge.IsResolved
                         && edge.IsExported
                         && string.Equals(edge.FromModule, currentModule, StringComparison.Ordinal)))
            {
                var targetModule = edge.Target!.ModuleName;
                if (accessibleModules.Add(targetModule))
                {
                    accessibleQueue.Enqueue(targetModule);
                }
            }
        }

        return accessibleModules;
    }

    public bool ContainsLoadedModule(string moduleName) => Modules.ContainsKey(moduleName);
}

public sealed record SourceModuleParse(
    ResolvedModuleReference Reference,
    ParseResult ParseResult,
    SyntaxModel SyntaxModel);

public sealed record SourceModuleParseCache(
    IReadOnlyDictionary<string, SourceModuleParse> Modules)
{
    public bool TryGet(string moduleName, out SourceModuleParse? module) => Modules.TryGetValue(moduleName, out module);
}

public sealed record LoadedModuleDocument(
    ResolvedModuleReference Reference,
    ParseResult ParseResult,
    SyntaxModel SyntaxModel,
    LoadedPackageImageFacts? PackageImageFacts = null)
{
    public bool IsPackageImageImport => !Reference.IsRoot && PackageImageFacts is not null;

    public bool HasPublishedFunctionSemantics => PackageImageFacts?.HasPublishedFunctionSemantics == true;

    public bool HasPublishedTypedTemplateBodies => PackageImageFacts?.HasPublishedTypedTemplateBodies == true;
}

public sealed record ImportedFunctionTemplateSummary(
    int? TopLevelStatementCount,
    int? EstimatedBodyCost,
    ImportedFunctionSemanticSummary? SemanticSummary = null,
    ImportedTemplateTypedBodySummary? TypedBodySummary = null,
    IReadOnlyList<ImportedDeferredFunctionInstantiationSummary>? DeferredFunctionInstantiations = null,
    IReadOnlyList<ImportedDeferredTypeInstantiationSummary>? DeferredTypeInstantiations = null,
    IReadOnlyList<ImportedTemplateObjectCreationSummary>? ObjectCreationSummaries = null,
    IReadOnlyList<ImportedTemplateEnumConstructorSummary>? EnumConstructorSummaries = null,
    IReadOnlyList<ImportedTemplateEnumCallSummary>? EnumCallSummaries = null,
    IReadOnlyList<ImportedTemplateEnumValueSummary>? EnumValueSummaries = null,
    IReadOnlyList<ImportedTemplateEnumPatternSummary>? EnumPatternSummaries = null,
    IReadOnlyList<ImportedTemplateAggregatePatternSummary>? AggregatePatternSummaries = null,
    IReadOnlyList<ImportedTemplateLocalDeclarationSummary>? LocalDeclarationSummaries = null,
    IReadOnlyList<ImportedTemplateConversionSummary>? ConversionSummaries = null,
    IReadOnlyList<ImportedTemplateDirectCallSummary>? DirectCallSummaries = null,
    IReadOnlyList<ImportedTemplateFieldAccessSummary>? FieldAccessSummaries = null,
    IReadOnlyList<ImportedTemplateMemberCallSummary>? MemberCallSummaries = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default)
{
    public ImportedFunctionSemanticSummary? Semantics => SemanticSummary;

    public FunctionOptimizationSummary? OptimizationSummary => SemanticSummary?.OptimizationSummary;

    public ImportedTemplateTypedBodySummary? TypedBody => TypedBodySummary;

    public IReadOnlyList<string> CalledFunctions => SemanticSummary?.CalledFunctions ?? [];

    public FunctionMemoryEffectSummary? MemoryEffects => SemanticSummary?.MemoryEffects;

    public IReadOnlyList<ParameterMemoryEffectSummary> Parameters => SemanticSummary?.Parameters ?? [];

    public IReadOnlyList<CallMemoryEffectSummary> Calls => SemanticSummary?.Calls ?? [];

    public IReadOnlyList<ImportedDeferredFunctionInstantiationSummary> DeferredInstantiations =>
        DeferredFunctionInstantiations ?? [];

    public IReadOnlyList<ImportedDeferredTypeInstantiationSummary> DeferredTypes =>
        DeferredTypeInstantiations ?? [];

    public IReadOnlyList<ImportedTemplateObjectCreationSummary> ObjectCreations =>
        ObjectCreationSummaries ?? [];

    public IReadOnlyList<TypedConstructorShape?> Constructors =>
        ObjectCreations.Select(static objectCreation => objectCreation.Constructor).ToArray();

    public IReadOnlyList<ImportedTemplateEnumConstructorSummary> EnumConstructors =>
        EnumConstructorSummaries ?? [];

    public IReadOnlyList<ImportedTemplateEnumCallSummary> EnumCalls =>
        EnumCallSummaries ?? [];

    public IReadOnlyList<ImportedTemplateEnumValueSummary> EnumValues =>
        EnumValueSummaries ?? [];

    public IReadOnlyList<ImportedTemplateEnumPatternSummary> EnumPatterns =>
        EnumPatternSummaries ?? [];

    public IReadOnlyList<ImportedTemplateAggregatePatternSummary> AggregatePatterns =>
        AggregatePatternSummaries ?? [];

    public IReadOnlyList<ImportedTemplateLocalDeclarationSummary> LocalDeclarations =>
        LocalDeclarationSummaries ?? [];

    public IReadOnlyList<ImportedTemplateConversionSummary> Conversions =>
        ConversionSummaries ?? [];

    public IReadOnlyList<ImportedTemplateDirectCallSummary> DirectCalls =>
        DirectCallSummaries ?? [];

    public IReadOnlyList<ImportedTemplateFieldAccessSummary> FieldAccesses =>
        FieldAccessSummaries ?? [];

    public IReadOnlyList<ImportedTemplateMemberCallSummary> MemberCalls =>
        MemberCallSummaries ?? [];
}

public enum ImportedTemplateTypedBodyStatementKind
{
    Block,
    Empty,
    LocalVariableDeclaration,
    ExpressionStatement,
    Assignment,
    Switch,
    For,
    While,
    If,
    Break,
    Continue,
    Return
}

public enum ImportedTemplateTypedSwitchCaseKind
{
    Literal,
    MatchAll,
    Default,
    EnumPattern,
    AggregatePattern
}

public enum ImportedTemplateTypedSwitchFieldPatternKind
{
    Discard,
    Capture,
    Literal,
    EnumPattern,
    AggregatePattern
}

public enum ImportedTemplateTypedBodyExpressionKind
{
    NameReference,
    Literal,
    ArrayInitializer,
    ObjectInitializer,
    Assignment,
    Conversion,
    UnaryOperation,
    BinaryOperation,
    ComparisonChain,
    Conditional,
    ObjectCreation,
    EnumConstructor,
    EnumCall,
    EnumValue,
    DirectCall,
    IndexAccess,
    FieldAccess,
    MemberCall,
    TypeLayout
}

public sealed record ImportedTemplateTypedBodyExpressionSummary(
    ImportedTemplateTypedBodyExpressionKind Kind,
    string? Name = null,
    string? AssignmentOperator = null,
    int? Ordinal = null,
    IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary>? Arguments = null,
    IReadOnlyList<string>? MemberNames = null,
    string? LiteralText = null,
    StarkTypeSymbol? Type = null,
    ImportedTemplateTypedBodyExpressionSummary? TargetExpression = null,
    IReadOnlyList<string>? OperatorNames = null)
{
    public IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> Args =>
        Arguments ?? [];

    public IReadOnlyList<string> Members =>
        MemberNames ?? [];

    public IReadOnlyList<string> Operators =>
        OperatorNames ?? [];
}

public sealed record ImportedTemplateTypedSwitchFieldPatternSummary(
    ImportedTemplateTypedSwitchFieldPatternKind Kind,
    string? Name = null,
    int? Ordinal = null,
    ImportedTemplateTypedBodyExpressionSummary? Expression = null,
    IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary>? MemberPatterns = null)
{
    public IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> Members =>
        MemberPatterns ?? [];
}

public sealed record ImportedTemplateTypedSwitchCaseSummary(
    ImportedTemplateTypedSwitchCaseKind Kind,
    int? Ordinal = null,
    string? Name = null,
    ImportedTemplateTypedBodyExpressionSummary? Expression = null,
    ImportedTemplateTypedBodyExpressionSummary? GuardExpression = null,
    IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary>? MemberPatterns = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? StatementSummaries = null)
{
    public IReadOnlyList<ImportedTemplateTypedSwitchFieldPatternSummary> Members =>
        MemberPatterns ?? [];

    public IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> Statements =>
        StatementSummaries ?? [];
}

public sealed record ImportedTemplateTypedBodyStatementSummary(
    ImportedTemplateTypedBodyStatementKind Kind,
    ImportedTemplateTypedBodyExpressionSummary? Expression = null,
    string? Name = null,
    string? AssignmentOperator = null,
    string? StorageClass = null,
    bool IsMutable = false,
    bool IsConstant = false,
    StarkTypeSymbol? Type = null,
    string? LoopBehavior = null,
    IReadOnlyList<ImportedTemplateTypedSwitchCaseSummary>? SwitchCaseSummaries = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? InitializerStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? IteratorStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? BodyStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? ThenStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? ElseStatements = null,
    ImportedTemplateTypedBodyExpressionSummary? TargetExpression = null,
    IReadOnlyList<string>? LoopContracts = null)
{
    public IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> Initializer =>
        InitializerStatements ?? [];

    public IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> Iterator =>
        IteratorStatements ?? [];

    public IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> Body =>
        BodyStatements ?? [];

    public IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> ThenBranch =>
        ThenStatements ?? [];

    public IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> ElseBranch =>
        ElseStatements ?? [];

    public IReadOnlyList<ImportedTemplateTypedSwitchCaseSummary> SwitchCases =>
        SwitchCaseSummaries ?? [];

    public IReadOnlyList<string> LoopContractNames =>
        LoopContracts ?? [];
}

public sealed record ImportedTemplateTypedBodySummary(
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary> Statements);

public sealed record ImportedDeferredFunctionInstantiationSummary(
    string CalleeTemplateName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments);

public sealed record ImportedDeferredTypeInstantiationSummary(
    StarkTypeSymbol Type);

public sealed record ImportedTemplateLocalDeclarationSummary(
    string Kind,
    int Line,
    int Column,
    StarkTypeSymbol Type);

public sealed record ImportedTemplateObjectCreationSummary(
    StarkTypeSymbol CreatedType,
    TypedConstructorShape? Constructor,
    IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? InitializerMemberSummaries = null)
{
    public IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary> InitializerMembers =>
        InitializerMemberSummaries ?? [];
}

public sealed record ImportedTemplateObjectInitializerMemberSummary(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record ImportedTemplateEnumConstructorSummary(
    int Ordinal,
    StarkTypeSymbol EnumType,
    string VariantName,
    IReadOnlyList<ImportedTemplateEnumConstructorMemberSummary>? MemberSummaries = null)
{
    public IReadOnlyList<ImportedTemplateEnumConstructorMemberSummary> Members =>
        MemberSummaries ?? [];
}

public sealed record ImportedTemplateEnumConstructorMemberSummary(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record ImportedTemplateEnumCallSummary(
    int Ordinal,
    StarkTypeSymbol EnumType,
    string VariantName);

public sealed record ImportedTemplateEnumValueSummary(
    int Ordinal,
    StarkTypeSymbol EnumType,
    string VariantName);

public sealed record ImportedTemplateEnumPatternSummary(
    int Ordinal,
    StarkTypeSymbol EnumType,
    string VariantName,
    IReadOnlyList<ImportedTemplateEnumPatternMemberSummary>? MemberSummaries = null)
{
    public IReadOnlyList<ImportedTemplateEnumPatternMemberSummary> Members =>
        MemberSummaries ?? [];
}

public sealed record ImportedTemplateEnumPatternMemberSummary(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record ImportedTemplateAggregatePatternSummary(
    int Ordinal,
    StarkTypeSymbol Type);

public sealed record ImportedTemplateDirectCallSummary(
    int Ordinal,
    TypedFunctionSignature Signature);

public sealed record ImportedTemplateFieldAccessSummary(
    int Ordinal,
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record ImportedTemplateMemberCallSummary(
    int Ordinal,
    TypedFunctionSignature Signature);

public sealed record ImportedTemplateConversionSummary(
    int Ordinal,
    StarkTypeSymbol TargetType);

public sealed record LoadedPackageImageFacts(
    IReadOnlyDictionary<string, FunctionEffectProfile> FunctionEffects,
    IReadOnlyDictionary<string, TypeAliasSymbol> TypeAliases,
    IReadOnlyDictionary<string, TypedFunctionSignature> FunctionSignatures,
    IReadOnlyDictionary<string, TypedGlobalSymbol> Globals,
    IReadOnlyDictionary<string, NamedTypeSymbol> NamedTypes,
    IReadOnlyDictionary<string, IReadOnlyList<TypedConstructorShape>> Constructors,
    IReadOnlyDictionary<string, AbiFunctionSignature> AbiFunctions,
    IReadOnlyDictionary<string, ConcreteTypeLayout> ConcreteLayouts,
    IReadOnlyDictionary<string, EnumLayoutSymbol> EnumLayouts,
    IReadOnlyDictionary<string, ImportedFunctionSemanticSummary> FunctionSemantics,
    IReadOnlyDictionary<string, ImportedFunctionTemplateSummary> FunctionTemplates,
    PackageImageLinkageFacts? Linkage = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default)
{
    public bool HasPublishedFunctionSemantics => FunctionSemantics.Count > 0;

    public bool HasPublishedTypedTemplateBodies =>
        FunctionTemplates.Count > 0
        && FunctionTemplates.Values.All(static template => template.TypedBody is not null);
}

public sealed record PackageImageLinkageFacts(
    string ObjectFileName,
    IReadOnlySet<string> DefinedSymbols,
    IReadOnlySet<string> ReferencedSymbols);

public sealed record LoadedModuleSet(
    string RootModuleName,
    IReadOnlyDictionary<string, LoadedModuleDocument> Modules)
{
    public bool TryGet(string moduleName, out LoadedModuleDocument? module) => Modules.TryGetValue(moduleName, out module);

    public IEnumerable<LoadedModuleDocument> ImportedModules => Modules.Values
        .Where(module => !module.Reference.IsRoot);
}

public sealed record SymbolCatalog(
    string ModuleName,
    IReadOnlyList<string> ExportedNames,
    IReadOnlyList<string> PublicNames,
    IReadOnlyList<string> InternalNames,
    IReadOnlyList<string> ModulePrivateNames);

public sealed record FunctionEffectProfile(
    string Name,
    StarkFunctionKind Kind,
    bool ReadsArgumentMemory,
    bool IsPure,
    bool NoSync,
    bool NoFree,
    bool NoUnwind,
    bool WillReturn,
    bool MustProgress,
    bool UseFastCallingConvention,
    bool IsFfi,
    bool IsVarargs,
    bool IsHot,
    bool IsCold,
    InlinePreference InlinePreference,
    bool IsStrictFp,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default);

public sealed record FunctionEffectModel(
    string ModuleName,
    IReadOnlyDictionary<string, FunctionEffectProfile> Functions);

public enum ClosedWorldSealKind
{
    SealedByDefault,
    AbiBoundary
}

public enum ClosedWorldCallLoweringStrategy
{
    CompileTimeOnlyContract,
    DirectSharedBody,
    DirectAbiBoundary,
    LawCallerSpecializedClone
}

public enum ClosedWorldCodeGenerationMode
{
    NoRuntimeCode,
    SharedCode,
    CallerSpecializedClone,
    MonomorphizationDeferred
}

public sealed record ClosedWorldTypeOptimizationInfo(
    string Name,
    DeclarationKind Kind,
    ClosedWorldSealKind Seal,
    bool HasRuntimeDispatch);

public sealed record ClosedWorldFunctionOptimizationInfo(
    string Name,
    DeclarationKind Kind,
    ClosedWorldSealKind Seal,
    IReadOnlyList<ClosedWorldCallLoweringStrategy> SelectionOrder,
    ClosedWorldCodeGenerationMode CodeGenerationMode,
    bool CanDevirtualize);

public sealed record ClosedWorldOptimizationModel(
    string ModuleName,
    IReadOnlyDictionary<string, ClosedWorldTypeOptimizationInfo> Types,
    IReadOnlyDictionary<string, ClosedWorldFunctionOptimizationInfo> Functions);

public enum StarkBorrowKind
{
    None,
    Borrow,
    RetBorrow,
    StoreBorrow
}

public enum StarkAccessKind
{
    None,
    Shared,
    Frozen
}

public enum StarkInitializationKind
{
    None,
    Out,
    Init
}

public enum StarkTypeKind
{
    Error,
    Void,
    Bool,
    Ascii,
    Unicode,
    Integer,
    Float,
    RawPointer,
    FixedArray,
    Slice,
    Dynamic,
    FunctionPointer,
    Named,
    Null
}

public sealed record StarkTypeSymbol(
    StarkTypeKind Kind,
    string DisplayName,
    int? BitWidth = null,
    string? NamedType = null,
    StarkTypeSymbol? ElementType = null,
    int? FixedLength = null,
    StarkFunctionKind? FunctionPointerKind = null,
    StarkTypeSymbol? FunctionPointerReturnType = null,
    IReadOnlyList<StarkTypeSymbol>? FunctionPointerParameterTypes = null,
    BigInteger? RangeMin = null,
    BigInteger? RangeMax = null,
    bool IsUnsigned = false,
    bool IsMutablePointer = false,
    StarkBorrowKind BorrowKind = StarkBorrowKind.None,
    StarkAccessKind AccessKind = StarkAccessKind.None,
    StarkInitializationKind InitializationKind = StarkInitializationKind.None,
    bool IsMutableView = false,
    IReadOnlyList<StarkTypeSymbol>? TypeArguments = null);

public static class StarkTypeSymbols
{
    public const string OwnedAsciiName = "Ascii";
    public const string OwnedUnicodeName = "Unicode";

    public static readonly StarkTypeSymbol Error = new(StarkTypeKind.Error, "<error>");
    public static readonly StarkTypeSymbol Void = new(StarkTypeKind.Void, "void");
    public static readonly StarkTypeSymbol Bool = new(StarkTypeKind.Bool, "bool");
    public static readonly StarkTypeSymbol Ascii = new(StarkTypeKind.Ascii, "ascii");
    public static readonly StarkTypeSymbol Unicode = new(StarkTypeKind.Unicode, "unicode");
    public static readonly StarkTypeSymbol OwnedAscii = new(StarkTypeKind.Named, OwnedAsciiName, NamedType: OwnedAsciiName);
    public static readonly StarkTypeSymbol OwnedUnicode = new(StarkTypeKind.Named, OwnedUnicodeName, NamedType: OwnedUnicodeName);
    public static readonly StarkTypeSymbol Null = new(StarkTypeKind.Null, "null");
    private static readonly NamedTypeSymbol BuiltinOwnedAsciiNamedType = CreateOwnedTextNamedType(OwnedAsciiName, Integer(8));
    private static readonly NamedTypeSymbol BuiltinOwnedUnicodeNamedType = CreateOwnedTextNamedType(OwnedUnicodeName, Integer(32));

    public static IReadOnlyList<NamedTypeSymbol> BuiltinNamedTypes => [BuiltinOwnedAsciiNamedType, BuiltinOwnedUnicodeNamedType];

    public static StarkTypeSymbol Integer(int bitWidth, BigInteger? rangeMin = null, BigInteger? rangeMax = null, bool isUnsigned = false)
    {
        var prefix = isUnsigned ? "u" : "i";
        var displayName = rangeMin is null && rangeMax is null
            ? $"{prefix}{bitWidth}"
            : isUnsigned && IsFullUnsignedIntegerRange(bitWidth, rangeMin, rangeMax)
                ? $"u{bitWidth}"
                : !isUnsigned && IsFullSignedIntegerRange(bitWidth, rangeMin, rangeMax)
                    ? $"i{bitWidth}"
                    : $"{prefix}{bitWidth}[{rangeMin} {rangeMax}]";
        return new StarkTypeSymbol(
            StarkTypeKind.Integer,
            displayName,
            BitWidth: bitWidth,
            RangeMin: rangeMin,
            RangeMax: rangeMax,
            IsUnsigned: isUnsigned);
    }

    public static StarkTypeSymbol Float(int bitWidth) => new(StarkTypeKind.Float, $"f{bitWidth}", BitWidth: bitWidth);

    public static StarkTypeSymbol RawPointer(StarkTypeSymbol elementType, bool isMutable) =>
        new(
            StarkTypeKind.RawPointer,
            $"{(isMutable ? "rawmutptr" : "rawptr")}<{elementType.DisplayName}>",
            ElementType: elementType,
            IsMutablePointer: isMutable);

    public static StarkTypeSymbol FixedArray(StarkTypeSymbol elementType, int? fixedLength) =>
        new(
            StarkTypeKind.FixedArray,
            fixedLength is null ? $"{elementType.DisplayName}[?]" : $"{elementType.DisplayName}[{fixedLength}]",
            ElementType: elementType,
            FixedLength: fixedLength);

    public static StarkTypeSymbol Slice(StarkTypeSymbol elementType) =>
        new(StarkTypeKind.Slice, $"{elementType.DisplayName}[]", ElementType: elementType);

    public static StarkTypeSymbol Dynamic(StarkTypeSymbol elementType) =>
        new(StarkTypeKind.Dynamic, $"dynamic {elementType.DisplayName}", ElementType: elementType);

    public static StarkTypeSymbol FunctionPointer(
        StarkFunctionKind functionKind,
        StarkTypeSymbol returnType,
        IReadOnlyList<StarkTypeSymbol> parameterTypes)
    {
        var displayKind = functionKind switch
        {
            StarkFunctionKind.FiniteLaw => "finite law",
            StarkFunctionKind.Finite => "finite",
            StarkFunctionKind.Law => "law",
            _ => "fn"
        };
        var displayName = $"fnptr<{displayKind} {returnType.DisplayName}({string.Join(", ", parameterTypes.Select(static parameter => parameter.DisplayName))})>";
        return new StarkTypeSymbol(
            StarkTypeKind.FunctionPointer,
            displayName,
            FunctionPointerKind: functionKind,
            FunctionPointerReturnType: returnType,
            FunctionPointerParameterTypes: parameterTypes.ToArray());
    }

    public static StarkTypeSymbol Named(string name) => new(StarkTypeKind.Named, name, NamedType: name);

    public static StarkTypeSymbol GenericInstantiation(string templateName, IReadOnlyList<StarkTypeSymbol> typeArgs)
    {
        var displayName = $"{templateName}<{string.Join(", ", typeArgs.Select(static t => t.DisplayName))}>";
        var key = $"{templateName}<{string.Join(",", typeArgs.Select(static t => t.NamedType ?? t.DisplayName))}>";
        return new StarkTypeSymbol(StarkTypeKind.Named, displayName, NamedType: key, TypeArguments: typeArgs);
    }

    public static string GetGenericBaseName(string key)
    {
        var angle = key.IndexOf('<');
        return angle >= 0 ? key[..angle] : key;
    }

    public static bool IsFullSignedIntegerRange(int bitWidth, BigInteger? rangeMin, BigInteger? rangeMax)
    {
        if (bitWidth <= 0 || rangeMin is null || rangeMax is null)
        {
            return false;
        }

        var min = -(BigInteger.One << (bitWidth - 1));
        var max = (BigInteger.One << (bitWidth - 1)) - BigInteger.One;
        return rangeMin.Value == min && rangeMax.Value == max;
    }

    public static bool IsFullUnsignedIntegerRange(int bitWidth, BigInteger? rangeMin, BigInteger? rangeMax)
    {
        if (bitWidth <= 0 || rangeMin is null || rangeMax is null)
        {
            return false;
        }

        var max = (BigInteger.One << bitWidth) - BigInteger.One;
        return rangeMin.Value == BigInteger.Zero && rangeMax.Value == max;
    }

    public static bool IsGenericInstantiation(StarkTypeSymbol type)
        => type.Kind == StarkTypeKind.Named && type.TypeArguments is { Count: > 0 };

    public static bool TryGetBuiltinNamedType(string name, out NamedTypeSymbol namedType)
    {
        switch (name)
        {
            case OwnedAsciiName:
                namedType = BuiltinOwnedAsciiNamedType;
                return true;
            case OwnedUnicodeName:
                namedType = BuiltinOwnedUnicodeNamedType;
                return true;
            default:
                namedType = null!;
                return false;
        }
    }

    public static StarkTypeSymbol ApplyQualifiers(
        StarkTypeSymbol type,
        StarkBorrowKind borrowKind = StarkBorrowKind.None,
        StarkAccessKind accessKind = StarkAccessKind.None,
        StarkInitializationKind initializationKind = StarkInitializationKind.None,
        bool isMutableView = false)
    {
        if (type.Kind == StarkTypeKind.Error)
        {
            return type;
        }

        if (borrowKind == StarkBorrowKind.None
            && accessKind == StarkAccessKind.None
            && initializationKind == StarkInitializationKind.None
            && !isMutableView)
        {
            return type;
        }

        var qualifiers = new List<string>();

        switch (borrowKind)
        {
            case StarkBorrowKind.Borrow:
                qualifiers.Add("borrow");
                break;
            case StarkBorrowKind.RetBorrow:
                qualifiers.Add("retborrow");
                break;
            case StarkBorrowKind.StoreBorrow:
                qualifiers.Add("storeborrow");
                break;
        }

        switch (accessKind)
        {
            case StarkAccessKind.Shared:
                qualifiers.Add("shared");
                break;
            case StarkAccessKind.Frozen:
                qualifiers.Add("frozen");
                break;
        }

        switch (initializationKind)
        {
            case StarkInitializationKind.Out:
                qualifiers.Add("out");
                break;
            case StarkInitializationKind.Init:
                qualifiers.Add("init");
                break;
        }

        if (isMutableView)
        {
            qualifiers.Add("mut");
        }

        return type with
        {
            DisplayName = $"{string.Join(" ", qualifiers)} {type.DisplayName}",
            BorrowKind = borrowKind,
            AccessKind = accessKind,
            InitializationKind = initializationKind,
            IsMutableView = isMutableView
        };
    }

    public static StarkTypeSymbol WithQualifiers(
        StarkTypeSymbol type,
        StarkBorrowKind? borrowKind = null,
        StarkAccessKind? accessKind = null,
        StarkInitializationKind? initializationKind = null,
        bool? isMutableView = null)
    {
        if (type.Kind == StarkTypeKind.Error)
        {
            return type;
        }

        var rebuilt = RebuildWithoutTopLevelQualifiers(type);
        return ApplyQualifiers(
            rebuilt,
            borrowKind ?? type.BorrowKind,
            accessKind ?? type.AccessKind,
            initializationKind ?? type.InitializationKind,
            isMutableView ?? type.IsMutableView);
    }

    public static bool IsDirectBorrowViewType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    public static bool IsPointerBackedBorrowReturn(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None && !IsDirectBorrowViewType(type);
    }

    public static StarkTypeSymbol BorrowReturnValueType(StarkTypeSymbol type)
    {
        return WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: IsPointerBackedBorrowReturn(type) ? false : type.IsMutableView);
    }

    public static StarkTypeSymbol BorrowReturnRuntimeType(StarkTypeSymbol type)
    {
        if (type.BorrowKind == StarkBorrowKind.None)
        {
            return type;
        }

        var valueType = BorrowReturnValueType(type);
        if (IsDirectBorrowViewType(valueType))
        {
            return valueType;
        }

        var pointeeType = WithQualifiers(valueType, isMutableView: false);
        return RawPointer(pointeeType, type.IsMutableView);
    }

    public static StarkTypeSymbol FreezeReachableView(StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.Error)
        {
            return type;
        }

        if (type.Kind is
            StarkTypeKind.Void or
            StarkTypeKind.Bool or
            StarkTypeKind.Ascii or
            StarkTypeKind.Unicode or
            StarkTypeKind.Integer or
            StarkTypeKind.Float or
            StarkTypeKind.FunctionPointer or
            StarkTypeKind.Null)
        {
            return WithQualifiers(type, accessKind: StarkAccessKind.None, isMutableView: false);
        }

        if (type.Kind == StarkTypeKind.RawPointer && type.ElementType is not null)
        {
            // Frozen projections keep pointer-backed reachable memory readonly,
            // even when top-level scalar reads remain plain values.
            return RawPointer(FreezeAddressPointeeType(type.ElementType), isMutable: false);
        }

        return WithQualifiers(type, accessKind: StarkAccessKind.Frozen, isMutableView: false);
    }

    public static StarkTypeSymbol FreezeAddressPointeeType(StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.Error)
        {
            return type;
        }

        if (type.Kind == StarkTypeKind.RawPointer && type.ElementType is not null)
        {
            return RawPointer(FreezeAddressPointeeType(type.ElementType), isMutable: false);
        }

        return WithQualifiers(type, accessKind: StarkAccessKind.Frozen, isMutableView: false);
    }

    private static StarkTypeSymbol RebuildWithoutTopLevelQualifiers(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Void => Void,
            StarkTypeKind.Bool => Bool,
            StarkTypeKind.Ascii => Ascii,
            StarkTypeKind.Unicode => Unicode,
            StarkTypeKind.Null => Null,
            StarkTypeKind.Integer => Integer(type.BitWidth ?? 32, type.RangeMin, type.RangeMax, type.IsUnsigned),
            StarkTypeKind.Float => Float(type.BitWidth ?? 32),
            StarkTypeKind.RawPointer when type.ElementType is not null => RawPointer(type.ElementType, type.IsMutablePointer),
            StarkTypeKind.FixedArray when type.ElementType is not null => FixedArray(type.ElementType, type.FixedLength),
            StarkTypeKind.Slice when type.ElementType is not null => Slice(type.ElementType),
            StarkTypeKind.Dynamic when type.ElementType is not null => Dynamic(type.ElementType),
            StarkTypeKind.FunctionPointer when type.FunctionPointerKind is { } functionKind
                                               && type.FunctionPointerReturnType is { } returnType
                                               && type.FunctionPointerParameterTypes is { } parameterTypes
                => FunctionPointer(functionKind, returnType, parameterTypes),
            StarkTypeKind.Named when type.NamedType == OwnedAsciiName => OwnedAscii,
            StarkTypeKind.Named when type.NamedType == OwnedUnicodeName => OwnedUnicode,
            StarkTypeKind.Named when type.TypeArguments is { Count: > 0 } && type.NamedType is not null
                => GenericInstantiation(GetGenericBaseName(type.NamedType), type.TypeArguments),
            StarkTypeKind.Named when type.NamedType is not null => Named(type.NamedType),
            _ => type
        };
    }

    private static NamedTypeSymbol CreateOwnedTextNamedType(string name, StarkTypeSymbol unitType)
    {
        var fields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
        var orderedFields = new List<FieldSymbol>
        {
            new("Data", RawPointer(unitType, isMutable: true)),
            new("Length", Integer(64)),
            new("Capacity", Integer(64))
        };

        foreach (var field in orderedFields)
        {
            fields[field.Name] = field;
        }

        return new NamedTypeSymbol(name, DeclarationKind.Struct, fields, orderedFields);
    }
}

public sealed record FieldSymbol(
    string Name,
    StarkTypeSymbol Type,
    StarkVisibility Visibility = StarkVisibility.Public,
    string? DeclaringModuleName = null);

public sealed record EnumVariantFieldSymbol(
    int Position,
    string? Name,
    StarkTypeSymbol Type);

public sealed record EnumVariantSymbol(
    string Name,
    bool UsesNamedFields,
    IReadOnlyList<EnumVariantFieldSymbol> Fields)
{
    public bool IsUnit => Fields.Count == 0;
}

public sealed record NamedTypeSymbol(
    string Name,
    DeclarationKind Kind,
    IReadOnlyDictionary<string, FieldSymbol> Fields,
    IReadOnlyList<FieldSymbol> OrderedFields,
    IReadOnlyList<EnumVariantSymbol>? EnumVariants = null,
    IReadOnlyList<string>? GenericParameterNames = null)
{
    public bool TryGetField(string name, out FieldSymbol field, out int index)
    {
        if (!Fields.TryGetValue(name, out field!))
        {
            index = -1;
            return false;
        }

        index = -1;
        for (var candidate = 0; candidate < OrderedFields.Count; candidate++)
        {
            if (string.Equals(OrderedFields[candidate].Name, name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        return index >= 0;
    }

    public IReadOnlyList<string> GenericParams => GenericParameterNames ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 };

    public IReadOnlyList<EnumVariantSymbol> Variants => EnumVariants ?? [];

    public bool TryGetVariant(string name, out EnumVariantSymbol variant, out int index)
    {
        var variants = Variants;
        for (var candidate = 0; candidate < variants.Count; candidate++)
        {
            if (string.Equals(variants[candidate].Name, name, StringComparison.Ordinal))
            {
                variant = variants[candidate];
                index = candidate;
                return true;
            }
        }

        variant = null!;
        index = -1;
        return false;
    }
}

public enum EnumLayoutKind
{
    DirectTag
}

public sealed record EnumVariantLayoutFieldSymbol(
    int SourcePosition,
    string? SourceFieldName,
    string StorageFieldName,
    int StorageFieldIndex,
    StarkTypeSymbol Type);

public sealed record EnumVariantLayoutSymbol(
    string Name,
    int TagValue,
    bool UsesNamedFields,
    IReadOnlyList<EnumVariantLayoutFieldSymbol> Fields);

public sealed record EnumLayoutSymbol(
    string EnumName,
    EnumLayoutKind Kind,
    FieldSymbol TagField,
    IReadOnlyList<FieldSymbol> OrderedFields,
    IReadOnlyDictionary<string, EnumVariantLayoutSymbol> Variants)
{
    public bool TryGetVariant(string name, out EnumVariantLayoutSymbol variant)
    {
        return Variants.TryGetValue(name, out variant!);
    }
}

public sealed record TypedParameterSymbol(
    string Name,
    StarkTypeSymbol Type,
    bool IsDisjoint = false,
    bool IsConst = false,
    string? RawPointerElementCountExpression = null);

public sealed record TypedConstructorShape(
    string TypeName,
    IReadOnlyList<TypedParameterSymbol> Parameters,
    bool IsPrimaryShape,
    string? BodyKey = null)
{
    public ISet<string>? InitializedMembers =>
        IsPrimaryShape
            ? Parameters.Select(static parameter => parameter.Name).ToHashSet(StringComparer.Ordinal)
            : null;
}

public sealed record TypedFunctionSignature(
    string Name,
    StarkTypeSymbol ReturnType,
    IReadOnlyList<TypedParameterSymbol> Parameters,
    string? SourceName = null,
    IReadOnlyList<string>? GenericParameterNames = null,
    string? TemplateName = null,
    IReadOnlyList<StarkTypeSymbol>? TypeArguments = null,
    bool IsStatic = false,
    StarkFunctionKind Kind = StarkFunctionKind.Fn,
    bool IsUnsafe = false,
    bool IsVarargs = false,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    IReadOnlyList<ParameterDisjointGroup>? DisjointParameterGroups = null)
{
    public string DisplaySourceName => SourceName ?? Name;
    public IReadOnlyList<string> GenericParams => GenericParameterNames ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 };
    public bool IsGenericInstantiation => TemplateName is not null && TypeArguments is { Count: > 0 };
    public IReadOnlyList<ParameterDisjointGroup> DisjointGroups => DisjointParameterGroups ?? [];
}

public enum GlobalBindingKind
{
    Const,
    Immutable,
    Mutable
}

public sealed record TypedGlobalSymbol(
    string Name,
    StarkTypeSymbol Type,
    GlobalBindingKind BindingKind)
{
    public bool IsMutable => BindingKind == GlobalBindingKind.Mutable;

    public bool IsConst => BindingKind == GlobalBindingKind.Const;
}

public sealed record LiteralTypingRecord(
    string LiteralText,
    StarkTypeSymbol Type,
    SourceLocation Location);

public sealed record TypeLayoutExpressionTypingRecord(
    string Kind,
    StarkTypeSymbol TargetType,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record LocalDeclarationTypingRecord(
    string Kind,
    StarkTypeSymbol Type,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record DirectCallTypingRecord(
    TypedFunctionSignature Signature,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record FunctionPointerPromotionTypingRecord(
    TypedFunctionSignature Signature,
    StarkTypeSymbol TargetType,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record AddressTakenFunctionTypingRecord(
    TypedFunctionSignature Signature,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record IndirectCallTypingRecord(
    StarkTypeSymbol FunctionPointerType,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record LambdaTypingRecord(
    string FunctionName,
    StarkTypeSymbol FunctionPointerType,
    IReadOnlyList<string> ParameterNames,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record LambdaCaptureTypingRecord(
    string Name,
    string Mode,
    bool IsUnsafe,
    StarkTypeSymbol Type,
    SourceLocation Location,
    SourceLocation LambdaLocation,
    string? EnclosingFunctionName = null);

public sealed record ConversionTypingRecord(
    StarkTypeSymbol TargetType,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record FieldAccessTypingRecord(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record MemberCallTypingRecord(
    TypedFunctionSignature Signature,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record ObjectInitializerMemberTypingRecord(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record EnumConstructorMemberTypingRecord(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record ObjectCreationTypingRecord(
    string ExpressionText,
    StarkTypeSymbol CreatedType,
    TypedConstructorShape? Constructor,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    IReadOnlyList<ObjectInitializerMemberTypingRecord>? InitializerMembers = null)
{
    public IReadOnlyList<ObjectInitializerMemberTypingRecord> Members =>
        InitializerMembers ?? [];
}

public sealed record EnumConstructorTypingRecord(
    StarkTypeSymbol EnumType,
    string VariantName,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    IReadOnlyList<EnumConstructorMemberTypingRecord>? MemberRecords = null)
{
    public IReadOnlyList<EnumConstructorMemberTypingRecord> Members =>
        MemberRecords ?? [];
}

public sealed record EnumCallTypingRecord(
    StarkTypeSymbol EnumType,
    string VariantName,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record EnumValueTypingRecord(
    StarkTypeSymbol EnumType,
    string VariantName,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record EnumPatternTypingRecord(
    StarkTypeSymbol EnumType,
    string VariantName,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    IReadOnlyList<EnumPatternMemberTypingRecord>? MemberRecords = null)
{
    public IReadOnlyList<EnumPatternMemberTypingRecord> Members =>
        MemberRecords ?? [];
}

public sealed record EnumPatternMemberTypingRecord(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record AggregatePatternTypingRecord(
    StarkTypeSymbol Type,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record FunctionInstantiationTriggerRecord(
    string FunctionName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    TypedFunctionSignature Signature,
    SourceLocation Location);

public sealed record DeferredFunctionInstantiationTriggerRecord(
    string EnclosingFunctionName,
    TypedFunctionSignature Signature,
    SourceLocation Location);

public sealed record DeferredTypeInstantiationTriggerRecord(
    string EnclosingFunctionName,
    StarkTypeSymbol Type,
    SourceLocation Location);

public sealed record TypeInstantiationTriggerRecord(
    string TypeName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    SourceLocation Location);

public sealed record TypeCheckModel(
    string ModuleName,
    IReadOnlyDictionary<string, NamedTypeSymbol> NamedTypes,
    IReadOnlyDictionary<string, TypeAliasSymbol> TypeAliases,
    IReadOnlyDictionary<string, TypedFunctionSignature> Functions,
    IReadOnlyDictionary<string, TypedGlobalSymbol> Globals,
    IReadOnlyList<LiteralTypingRecord> Literals,
    IReadOnlyList<ObjectCreationTypingRecord> ObjectCreations,
    IReadOnlyDictionary<string, IReadOnlyList<TypedFunctionSignature>>? FunctionOverloads = null,
    IReadOnlyList<FunctionInstantiationTriggerRecord>? FunctionInstantiationTriggers = null,
    IReadOnlyList<DeferredFunctionInstantiationTriggerRecord>? DeferredFunctionInstantiationTriggers = null,
    IReadOnlyList<DeferredTypeInstantiationTriggerRecord>? DeferredTypeInstantiationTriggers = null,
    IReadOnlyList<TypeInstantiationTriggerRecord>? TypeInstantiationTriggers = null,
    IReadOnlyList<EnumConstructorTypingRecord>? EnumConstructorRecords = null,
    IReadOnlyList<EnumCallTypingRecord>? EnumCallRecords = null,
    IReadOnlyList<EnumValueTypingRecord>? EnumValueRecords = null,
    IReadOnlyList<EnumPatternTypingRecord>? EnumPatternRecords = null,
    IReadOnlyList<AggregatePatternTypingRecord>? AggregatePatternRecords = null,
    IReadOnlyList<LocalDeclarationTypingRecord>? LocalDeclarationRecords = null,
    IReadOnlyList<ConversionTypingRecord>? ConversionRecords = null,
    IReadOnlyList<DirectCallTypingRecord>? DirectCallRecords = null,
    IReadOnlyList<FunctionPointerPromotionTypingRecord>? FunctionPointerPromotionRecords = null,
    IReadOnlyList<IndirectCallTypingRecord>? IndirectCallRecords = null,
    IReadOnlyList<FieldAccessTypingRecord>? FieldAccessRecords = null,
    IReadOnlyList<MemberCallTypingRecord>? MemberCallRecords = null,
    IReadOnlyList<TypeLayoutExpressionTypingRecord>? TypeLayoutExpressionRecords = null,
    IReadOnlyList<LambdaTypingRecord>? LambdaRecords = null,
    IReadOnlyList<LambdaCaptureTypingRecord>? LambdaCaptureRecords = null,
    IReadOnlyList<AddressTakenFunctionTypingRecord>? AddressTakenFunctionRecords = null)
{
    public IReadOnlyDictionary<string, IReadOnlyList<TypedFunctionSignature>> Overloads =>
        FunctionOverloads
        ?? Functions.Values
            .GroupBy(static function => function.DisplaySourceName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<TypedFunctionSignature>)group.ToArray(),
                StringComparer.Ordinal);

    public IReadOnlyList<FunctionInstantiationTriggerRecord> InstantiationTriggers =>
        FunctionInstantiationTriggers ?? [];

    public IReadOnlyList<DeferredFunctionInstantiationTriggerRecord> DeferredInstantiationTriggers =>
        DeferredFunctionInstantiationTriggers ?? [];

    public IReadOnlyList<DeferredTypeInstantiationTriggerRecord> DeferredTypeTriggers =>
        DeferredTypeInstantiationTriggers ?? [];

    public IReadOnlyList<TypeInstantiationTriggerRecord> TypeTriggers =>
        TypeInstantiationTriggers ?? [];

    public IReadOnlyList<EnumConstructorTypingRecord> EnumConstructors =>
        EnumConstructorRecords ?? [];

    public IReadOnlyList<EnumCallTypingRecord> EnumCalls =>
        EnumCallRecords ?? [];

    public IReadOnlyList<EnumValueTypingRecord> EnumValues =>
        EnumValueRecords ?? [];

    public IReadOnlyList<EnumPatternTypingRecord> EnumPatterns =>
        EnumPatternRecords ?? [];

    public IReadOnlyList<AggregatePatternTypingRecord> AggregatePatterns =>
        AggregatePatternRecords ?? [];

    public IReadOnlyList<LocalDeclarationTypingRecord> LocalDeclarations =>
        LocalDeclarationRecords ?? [];

    public IReadOnlyList<ConversionTypingRecord> Conversions =>
        ConversionRecords ?? [];

    public IReadOnlyList<DirectCallTypingRecord> DirectCalls =>
        DirectCallRecords ?? [];

    public IReadOnlyList<FunctionPointerPromotionTypingRecord> FunctionPointerPromotions =>
        FunctionPointerPromotionRecords ?? [];

    public IReadOnlyList<AddressTakenFunctionTypingRecord> AddressTakenFunctions =>
        AddressTakenFunctionRecords ?? [];

    public IReadOnlyList<IndirectCallTypingRecord> IndirectCalls =>
        IndirectCallRecords ?? [];

    public IReadOnlyList<LambdaTypingRecord> Lambdas =>
        LambdaRecords ?? [];

    public IReadOnlyList<LambdaCaptureTypingRecord> LambdaCaptures =>
        LambdaCaptureRecords ?? [];

    public IReadOnlyList<FieldAccessTypingRecord> FieldAccesses =>
        FieldAccessRecords ?? [];

    public IReadOnlyList<MemberCallTypingRecord> MemberCalls =>
        MemberCallRecords ?? [];

    public IReadOnlyList<TypeLayoutExpressionTypingRecord> TypeLayoutExpressions =>
        TypeLayoutExpressionRecords ?? [];
}

internal static class TemplateLocalDeclarationFacts
{
    public const string ConstantKind = "const";
    public const string VariableKind = "var";
    public const string ForVariableKind = "forvar";

    public static string BuildLookupKey(string kind, int line, int column)
    {
        return $"{kind}|{line}:{column}";
    }

    public static string BuildLookupKey(string kind, SourceLocation location)
    {
        return BuildLookupKey(kind, location.Line, location.Column);
    }
}

internal static class TemplateDirectCallFacts
{
    public static string BuildLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    public static string BuildLookupKey(SourceLocation location)
    {
        return BuildLookupKey(location.Line, location.Column);
    }
}

internal static class TemplateFieldAccessFacts
{
    public static string BuildLookupKey(int line, int column)
    {
        return $"{line}:{column}";
    }

    public static string BuildLookupKey(SourceLocation location)
    {
        return BuildLookupKey(location.Line, location.Column);
    }
}

public sealed record FunctionInstantiationOwnership(
    string TemplateName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    TypedFunctionSignature Signature,
    string DeclaringModuleName,
    string OwnerModuleName,
    bool IsDeclaringModuleSourceBacked,
    SourceLocation FirstUseLocation)
{
    public bool RequiresConsumerOwnership =>
        !string.Equals(DeclaringModuleName, OwnerModuleName, StringComparison.Ordinal);
}

public sealed record TypeInstantiationOwnership(
    string TemplateName,
    string InstantiatedTypeName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    string DeclaringModuleName,
    string OwnerModuleName,
    bool IsDeclaringModuleSourceBacked,
    SourceLocation FirstUseLocation)
{
    public bool RequiresConsumerOwnership =>
        !string.Equals(DeclaringModuleName, OwnerModuleName, StringComparison.Ordinal);
}

public sealed record InstantiationOwnershipModel(
    string RootModuleName,
    IReadOnlyList<FunctionInstantiationOwnership> Functions,
    IReadOnlyList<TypeInstantiationOwnership> Types);

public sealed record MonomorphizedFunctionPlan(
    string TemplateName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    string DeclaringModuleName,
    string OwnerModuleName,
    bool IsDeclaringModuleSourceBacked,
    MonomorphizationCodeSizeHeuristic CodeSizeHeuristic,
    int? EstimatedTopLevelStatementCount,
    int? EstimatedBodyCost,
    MonomorphizationLinkageKind Linkage,
    string SymbolName,
    SourceLocation FirstUseLocation);

public sealed record MonomorphizedTypePlan(
    string TemplateName,
    string InstantiatedTypeName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    string DeclaringModuleName,
    string OwnerModuleName,
    bool IsDeclaringModuleSourceBacked,
    MonomorphizationLinkageKind Linkage,
    string SymbolName,
    SourceLocation FirstUseLocation);

public sealed record MonomorphizationPlanModel(
    string RootModuleName,
    IReadOnlyList<MonomorphizedFunctionPlan> Functions,
    IReadOnlyList<MonomorphizedTypePlan> Types);

public enum FunctionSpecializationStrategy
{
    OwnedConcreteBody,
    LawCallerSpecializedClone,
    DirectAbiBoundaryFallback
}

public enum FunctionSpecializationCodeGenerationMode
{
    AbiBoundaryOnly,
    SingleOwnerConcreteBody,
    CallerSpecializedClone
}

public sealed record FunctionSpecializationPlan(
    string TemplateName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    string DeclaringModuleName,
    string OwnerModuleName,
    string SymbolName,
    IReadOnlyList<FunctionSpecializationStrategy> SelectionOrder,
    FunctionSpecializationCodeGenerationMode CodeGenerationMode,
    SourceLocation FirstUseLocation);

public sealed record SpecializationPlanModel(
    string RootModuleName,
    IReadOnlyList<FunctionSpecializationPlan> Functions);

public enum FunctionSpecializationCodegenStrategyKind
{
    AbiFallbackOnly,
    EmitOwnedConcreteBody,
    EmitOwnedConcreteBodyAndPreferLawCallerClone
}

public sealed record FunctionSpecializationCodegenStrategy(
    string TemplateName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    string DeclaringModuleName,
    string OwnerModuleName,
    bool IsDeclaringModuleSourceBacked,
    string SymbolName,
    MonomorphizationLinkageKind Linkage,
    FunctionSpecializationCodegenStrategyKind StrategyKind,
    bool SupportsAbiFallback,
    SourceLocation FirstUseLocation);

public sealed record SpecializationCodegenStrategyModel(
    string RootModuleName,
    IReadOnlyList<FunctionSpecializationCodegenStrategy> Functions);

public enum MonomorphizationCodeSizeHeuristic
{
    DeclarationOnly,
    InlineSmallBody,
    SpecializeDefault,
    ReduceCodeSize
}

public enum MonomorphizationLinkageKind
{
    InternalSingleOwner,
    LinkOnceOdrComdat
}

public sealed record EnumLayoutModel(
    string ModuleName,
    IReadOnlyDictionary<string, EnumLayoutSymbol> Layouts);

public enum AbiParameterKind
{
    Direct,
    IndirectIn,
    SRet
}

public sealed record AbiParameterSymbol(
    string SourceName,
    string LlvmName,
    StarkTypeSymbol SourceType,
    StarkTypeSymbol LlvmType,
    AbiParameterKind Kind,
    string? RawPointerElementCountExpression = null);

public sealed record AbiFunctionSignature(
    string Name,
    string SymbolName,
    StarkTypeSymbol SourceReturnType,
    StarkTypeSymbol LlvmReturnType,
    IReadOnlyList<AbiParameterSymbol> Parameters,
    bool IsFfi,
    string? SourceName = null,
    bool UsesFastCallingConvention = false,
    bool IsVarargs = false)
{
    public string DisplaySourceName => SourceName ?? Name;

    public bool ReturnsIndirect => Parameters.Any(static parameter => parameter.Kind == AbiParameterKind.SRet);

    public AbiParameterSymbol? ReturnBufferParameter => Parameters.FirstOrDefault(static parameter => parameter.Kind == AbiParameterKind.SRet);

    public IReadOnlyList<AbiParameterSymbol> UserParameters => Parameters
        .Where(static parameter => parameter.Kind != AbiParameterKind.SRet)
        .ToArray();
}

public sealed record AbiModel(
    string ModuleName,
    IReadOnlyDictionary<string, AbiFunctionSignature> Functions);

public enum ParameterCaptureKind
{
    None,
    Return,
    Escape
}

public sealed record ParameterMemoryEffectSummary(
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
    ParameterCaptureKind CaptureKind);

public sealed record ConcreteTypeLayout(int SizeBytes, int AlignmentBytes);

internal static class ConcreteTypeLayoutHelper
{
    public static ConcreteTypeLayout? TryGetConcreteTypeLayout(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts = null)
    {
        return TryGetConcreteTypeLayout(
            type,
            namedTypes,
            enumLayouts,
            publishedConcreteLayouts: null,
            new HashSet<string>(StringComparer.Ordinal));
    }

    public static ConcreteTypeLayout? TryGetConcreteTypeLayout(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout> publishedConcreteLayouts)
    {
        return TryGetConcreteTypeLayout(
            type,
            namedTypes,
            enumLayouts,
            publishedConcreteLayouts,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static ConcreteTypeLayout? TryGetConcreteTypeLayout(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        ISet<string> activeNamedTypes)
    {
        var concreteType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        return concreteType.Kind switch
        {
            StarkTypeKind.Bool => new ConcreteTypeLayout(1, 1),
            StarkTypeKind.Integer when concreteType.BitWidth is int bitWidth =>
                TryGetScalarLayout((bitWidth + 7) / 8),
            StarkTypeKind.Float when concreteType.BitWidth is int floatWidth =>
                TryGetScalarLayout((floatWidth + 7) / 8),
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer or StarkTypeKind.Null =>
                TryGetPointerLayout(),
            StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode =>
                TryGetViewLayout(),
            StarkTypeKind.Dynamic =>
                TryGetDynamicStorageLayout(),
            StarkTypeKind.FixedArray when concreteType.ElementType is not null && concreteType.FixedLength is int fixedLength =>
                TryGetFixedArrayLayout(concreteType.ElementType, fixedLength, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes),
            StarkTypeKind.Named when concreteType.NamedType is not null
                                     && concreteType.TypeArguments is not { Count: > 0 }
                                     && publishedConcreteLayouts is not null
                                     && publishedConcreteLayouts.TryGetValue(concreteType.NamedType, out var publishedLayout) =>
                publishedLayout,
            StarkTypeKind.Named when concreteType.NamedType is not null
                                     && namedTypes.TryGetValue(concreteType.NamedType, out var namedType)
                                     && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record =>
                TryGetNamedTypeLayout(namedType, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes),
            StarkTypeKind.Named when concreteType.NamedType is not null
                                     && namedTypes.TryGetValue(concreteType.NamedType, out var enumType)
                                     && enumType.Kind == DeclarationKind.Enum
                                     && enumLayouts is not null
                                     && enumLayouts.TryGetValue(concreteType.NamedType, out var enumLayout) =>
                TryGetEnumTypeLayout(enumLayout, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes),
            _ => null
        };
    }

    private static ConcreteTypeLayout? TryGetFixedArrayLayout(
        StarkTypeSymbol elementType,
        int fixedLength,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        ISet<string> activeNamedTypes)
    {
        var elementLayout = TryGetConcreteTypeLayout(
            elementType,
            namedTypes,
            enumLayouts,
            publishedConcreteLayouts,
            activeNamedTypes);
        if (elementLayout is null)
        {
            return null;
        }

        try
        {
            var sizeBytes = checked(elementLayout.SizeBytes * fixedLength);
            return new ConcreteTypeLayout(sizeBytes, fixedLength == 0 ? 1 : elementLayout.AlignmentBytes);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static ConcreteTypeLayout? TryGetNamedTypeLayout(
        NamedTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        ISet<string> activeNamedTypes)
    {
        if (!activeNamedTypes.Add(type.Name))
        {
            return null;
        }

        try
        {
            var sizeBytes = 0;
            var alignmentBytes = 1;

            foreach (var field in type.OrderedFields)
            {
                var fieldLayout = TryGetConcreteTypeLayout(
                    field.Type,
                    namedTypes,
                    enumLayouts,
                    publishedConcreteLayouts,
                    activeNamedTypes);
                if (fieldLayout is null)
                {
                    return null;
                }

                sizeBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                sizeBytes = checked(sizeBytes + fieldLayout.SizeBytes);
                alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
            }

            sizeBytes = AlignTo(sizeBytes, alignmentBytes);
            return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
        }
        catch (OverflowException)
        {
            return null;
        }
        finally
        {
            activeNamedTypes.Remove(type.Name);
        }
    }

    private static ConcreteTypeLayout? TryGetEnumTypeLayout(
        EnumLayoutSymbol layout,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        ISet<string> activeNamedTypes)
    {
        if (!activeNamedTypes.Add(layout.EnumName))
        {
            return null;
        }

        try
        {
            var sizeBytes = 0;
            var alignmentBytes = 1;

            foreach (var field in layout.OrderedFields)
            {
                var fieldLayout = TryGetConcreteTypeLayout(
                    field.Type,
                    namedTypes,
                    enumLayouts,
                    publishedConcreteLayouts,
                    activeNamedTypes);
                if (fieldLayout is null)
                {
                    return null;
                }

                sizeBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                sizeBytes = checked(sizeBytes + fieldLayout.SizeBytes);
                alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
            }

            sizeBytes = AlignTo(sizeBytes, alignmentBytes);
            return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
        }
        catch (OverflowException)
        {
            return null;
        }
        finally
        {
            activeNamedTypes.Remove(layout.EnumName);
        }
    }

    private static ConcreteTypeLayout? TryGetScalarLayout(int sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return new ConcreteTypeLayout(0, 1);
        }

        return sizeBytes switch
        {
            1 => new ConcreteTypeLayout(1, 1),
            2 => new ConcreteTypeLayout(2, 2),
            <= 4 => new ConcreteTypeLayout(sizeBytes, 4),
            <= 8 => new ConcreteTypeLayout(sizeBytes, 8),
            _ => new ConcreteTypeLayout(sizeBytes, 16)
        };
    }

    private static ConcreteTypeLayout TryGetPointerLayout()
    {
        return new ConcreteTypeLayout(8, 8);
    }

    private static ConcreteTypeLayout TryGetViewLayout()
    {
        var pointerLayout = TryGetPointerLayout();
        var lengthLayout = TryGetScalarLayout(8) ?? throw new InvalidOperationException("i64 layout must be available.");
        var alignmentBytes = Math.Max(pointerLayout.AlignmentBytes, lengthLayout.AlignmentBytes);
        var sizeBytes = AlignTo(pointerLayout.SizeBytes, lengthLayout.AlignmentBytes);
        sizeBytes = checked(sizeBytes + lengthLayout.SizeBytes);
        sizeBytes = AlignTo(sizeBytes, alignmentBytes);
        return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
    }

    private static ConcreteTypeLayout TryGetDynamicStorageLayout()
    {
        var pointerLayout = TryGetPointerLayout();
        var lengthLayout = TryGetScalarLayout(8) ?? throw new InvalidOperationException("i64 layout must be available.");
        var alignmentBytes = Math.Max(pointerLayout.AlignmentBytes, lengthLayout.AlignmentBytes);
        var sizeBytes = AlignTo(pointerLayout.SizeBytes, lengthLayout.AlignmentBytes);
        sizeBytes = checked(sizeBytes + lengthLayout.SizeBytes);
        sizeBytes = AlignTo(sizeBytes, lengthLayout.AlignmentBytes);
        sizeBytes = checked(sizeBytes + lengthLayout.SizeBytes);
        sizeBytes = AlignTo(sizeBytes, alignmentBytes);
        return new ConcreteTypeLayout(sizeBytes, alignmentBytes);
    }

    private static int AlignTo(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        if (remainder == 0)
        {
            return value;
        }

        return checked(value + (alignment - remainder));
    }
}

public sealed record FunctionMemoryEffectSummary(
    bool ReadsArgumentMemory,
    bool WritesArgumentMemory,
    bool CapturesArgumentMemory,
    bool ReadsOtherMemory = false,
    bool WritesOtherMemory = false)
{
    public bool ReadsMemory => ReadsArgumentMemory || ReadsOtherMemory;

    public bool WritesMemory => WritesArgumentMemory || WritesOtherMemory;
}

public sealed record CallArgumentMemoryEffectSummary(
    int ArgumentIndex,
    string? CallerParameterName,
    string? CalleeParameterName,
    bool Reads,
    bool Writes,
    ParameterCaptureKind CaptureKind);

public sealed record CallMemoryEffectSummary(
    string CalleeName,
    FunctionMemoryEffectSummary MemoryEffects,
    IReadOnlyList<CallArgumentMemoryEffectSummary> Arguments);

public sealed record FunctionOptimizationSummary(
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
    bool IsTerminalSelectionWrapper)
{
    public bool IsSingleReturnCallForwarder => IsSingleReturnDirectCallForwarder || IsSingleReturnMemberCallForwarder;

    public bool IsSingleReturnAccessWrapper => IsSingleReturnFieldAccessWrapper || IsSingleReturnIndexAccessWrapper;

    public bool IsSingleReturnUnaryOrConversionWrapper =>
        IsSingleReturnConversionWrapper || IsSingleReturnAddressOfWrapper || IsSingleReturnDereferenceWrapper;

    public bool IsSingleReturnOperatorWrapper =>
        IsSingleReturnBinaryOperatorWrapper || IsSingleReturnComparisonWrapper;

    public bool IsSingleReturnInlineWrapper =>
        IsSingleReturnCallForwarder
        || IsSingleReturnAccessWrapper
        || IsSingleReturnUnaryOrConversionWrapper
        || IsSingleReturnOperatorWrapper
        || IsSingleReturnAggregateConstructionWrapper;

    public bool IsInlineWrapperLike =>
        IsSingleReturnInlineWrapper
        || IsSimpleLocalUpdateWrapper
        || IsTerminalSelectionWrapper;
}

public sealed record FunctionValidationSummary(
    string Name,
    StarkFunctionKind DeclaredKind,
    StarkFunctionKind EffectiveKind,
    bool EffectsValid,
    bool BorrowingValid,
    IReadOnlyList<string> CalledFunctions,
    FunctionMemoryEffectSummary? MemoryEffects = null,
    IReadOnlyList<ParameterMemoryEffectSummary>? Parameters = null,
    IReadOnlyList<CallMemoryEffectSummary>? Calls = null,
    FunctionOptimizationSummary? OptimizationSummary = null)
{
    public bool CanStrengthenKind => FunctionKindFacts.Rank(EffectiveKind) > FunctionKindFacts.Rank(DeclaredKind);
}

public sealed record ImportedFunctionSemanticSummary(
    string Name,
    StarkFunctionKind DeclaredKind,
    StarkFunctionKind EffectiveKind,
    IReadOnlyList<string> CalledFunctions,
    FunctionMemoryEffectSummary? MemoryEffects = null,
    IReadOnlyList<ParameterMemoryEffectSummary>? Parameters = null,
    IReadOnlyList<CallMemoryEffectSummary>? CallSummaries = null,
    FunctionOptimizationSummary? OptimizationSummary = null)
{
    public IReadOnlyList<CallMemoryEffectSummary> Calls => CallSummaries ?? [];

    public bool CanStrengthenKind => FunctionKindFacts.Rank(EffectiveKind) > FunctionKindFacts.Rank(DeclaredKind);
}

public sealed record SemanticValidationModel(
    string ModuleName,
    IReadOnlyDictionary<string, FunctionValidationSummary> Functions);

public sealed record FunctionOwnershipSummary(
    string Name,
    bool OwnershipValid,
    IReadOnlyList<string> ImplicitDrops,
    IReadOnlyList<string> Moves);

public sealed record OwnershipValidationModel(
    string ModuleName,
    IReadOnlyDictionary<string, FunctionOwnershipSummary> Functions);

public enum FunctionBodyLoweringKind
{
    DeclarationOnly,
    StarkCfg,
    AsmBypass
}

public sealed record HighLevelIrFunction(
    string Name,
    TypedFunctionSignature Signature,
    bool HasBody,
    FunctionBodyLoweringKind BodyLoweringKind,
    FunctionEffectProfile Effects,
    string? BodyTemplateName = null,
    IReadOnlyDictionary<string, StarkTypeSymbol>? GenericTypeSubstitution = null);

public sealed record HighLevelIrModule(
    string ModuleName,
    IReadOnlyList<HighLevelIrFunction> Functions,
    IReadOnlyList<string>? AddressTakenFunctionRecords = null)
{
    public IReadOnlyList<string> AddressTakenFunctions =>
        AddressTakenFunctionRecords ?? [];
}

public enum MidLevelIrStatementKind
{
    StorageLive,
    StorageDead,
    Assign,
    StoreIndirect,
    Evaluate
}

public enum MidLevelIrUnaryOperator
{
    Negate,
    LogicalNot,
    BitwiseNot
}

public enum MidLevelIrBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    WrappingAdd,
    WrappingSubtract,
    WrappingMultiply,
    SaturatingAdd,
    SaturatingSubtract,
    SaturatingMultiply,
    Divide,
    Modulo,
    BitwiseAnd,
    BitwiseXor,
    BitwiseOr,
    Exponent,
    ShiftLeft,
    ShiftRight,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

public enum MidLevelIrTerminatorKind
{
    Goto,
    Branch,
    Switch,
    Return,
    Unreachable
}

public sealed record MidLevelIrLocal(
    string Name,
    StarkTypeSymbol Type,
    string StorageClass,
    bool IsMutable,
    bool IsConstant,
    bool IsAddressable = false,
    SourceLocation? Location = null,
    bool HasConstProvenance = false);

public abstract record MidLevelIrOperand(StarkTypeSymbol Type, string Text);

public sealed record MidLevelIrLocalOperand(string Name, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, Name);

public sealed record MidLevelIrParameterOperand(string Name, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, Name);

public sealed record MidLevelIrGlobalOperand(string Name, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, Name);

public sealed record MidLevelIrGlobalAddressOperand(string Name, StarkTypeSymbol PointeeType, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, $"&{Name}");

public sealed record MidLevelIrFunctionAddressOperand(string FunctionName, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, FunctionName);

public sealed record MidLevelIrIntegerConstantOperand(BigInteger Value, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, Value.ToString());

public sealed record MidLevelIrFloatConstantOperand(string LiteralText, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, LiteralText);

public sealed record MidLevelIrStringConstantOperand(string LiteralText, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, LiteralText);

public sealed record MidLevelIrBoolConstantOperand(bool Value)
    : MidLevelIrOperand(StarkTypeSymbols.Bool, Value ? "true" : "false");

public sealed record MidLevelIrNullOperand(StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, "null");

public sealed record MidLevelIrZeroInitializerOperand(StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, "zeroinitializer");

public abstract record MidLevelIrRValue(StarkTypeSymbol Type, string Text);

public sealed record MidLevelIrUseRValue(MidLevelIrOperand Operand)
    : MidLevelIrRValue(Operand.Type, Operand.Text);

public sealed record MidLevelIrUnaryRValue(
    MidLevelIrUnaryOperator Operator,
    MidLevelIrOperand Operand,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrBinaryRValue(
    MidLevelIrBinaryOperator Operator,
    MidLevelIrOperand Left,
    MidLevelIrOperand Right,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrCallRValue(
    string FunctionName,
    IReadOnlyList<MidLevelIrOperand> Arguments,
    StarkTypeSymbol Type,
    string Text,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<MidLevelIrOperand?>? IndirectArgumentAddresses = null)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrIndirectCallRValue(
    MidLevelIrOperand Target,
    IReadOnlyList<MidLevelIrOperand> Arguments,
    StarkTypeSymbol Type,
    string Text,
    StarkTypeSymbol? SourceReturnType = null)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrConvertRValue(
    MidLevelIrOperand Operand,
    StarkTypeSymbol TargetType,
    string Text)
    : MidLevelIrRValue(TargetType, Text);

public sealed record MidLevelIrExtractFieldRValue(
    MidLevelIrOperand Target,
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrInsertFieldRValue(
    MidLevelIrOperand Target,
    string FieldName,
    int FieldIndex,
    MidLevelIrOperand Value,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrExtractIndexRValue(
    MidLevelIrOperand Target,
    int ElementIndex,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrInsertIndexRValue(
    MidLevelIrOperand Target,
    int ElementIndex,
    MidLevelIrOperand Value,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrMakeSliceFromLocalRValue(
    string LocalName,
    StarkTypeSymbol SourceType,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrMakeSliceFromPointerRValue(
    MidLevelIrOperand Pointer,
    MidLevelIrOperand Length,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrDynamicStorageAllocationRValue(
    MidLevelIrOperand Capacity,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrDynamicStorageFreeRValue(
    MidLevelIrOperand Storage,
    string Text)
    : MidLevelIrRValue(StarkTypeSymbols.Void, Text);

public sealed record MidLevelIrDynamicStorageReserveRValue(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    MidLevelIrOperand AdditionalCapacity,
    string Text)
    : MidLevelIrRValue(StarkTypeSymbols.Void, Text);

public sealed record MidLevelIrDynamicStorageTryReserveRValue(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    MidLevelIrOperand AdditionalCapacity,
    string Text)
    : MidLevelIrRValue(StarkTypeSymbols.Bool, Text);

public sealed record MidLevelIrDynamicStorageMoveLastRValue(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrDynamicStorageMoveAtRValue(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    MidLevelIrOperand Index,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrLoadSliceElementRValue(
    MidLevelIrOperand Slice,
    MidLevelIrOperand Index,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrTextSliceRValue(
    MidLevelIrOperand TextValue,
    MidLevelIrOperand Start,
    MidLevelIrOperand Length,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrAddressOfLocalRValue(
    string LocalName,
    StarkTypeSymbol PointeeType,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrAddressOfParameterRValue(
    string ParameterName,
    StarkTypeSymbol PointeeType,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrFieldAddressRValue(
    MidLevelIrOperand Address,
    StarkTypeSymbol AggregateType,
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrElementAddressRValue(
    MidLevelIrOperand Address,
    StarkTypeSymbol AggregateType,
    MidLevelIrOperand? Index,
    int? ConstantIndex,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrSliceElementAddressRValue(
    MidLevelIrOperand Slice,
    MidLevelIrOperand Index,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrLoadIndirectRValue(
    MidLevelIrOperand Address,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record ScopedNoAliasGroup(
    string ScopeId,
    IReadOnlyList<string> RootKeys);

public sealed record MidLevelIrStatement(
    MidLevelIrStatementKind Kind,
    string Text,
    string? TargetName = null,
    StarkTypeSymbol? TargetType = null,
    MidLevelIrOperand? Address = null,
    MidLevelIrRValue? Value = null,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null);

public sealed record MidLevelIrSwitchCase(
    string Label,
    int TargetBlockId,
    MidLevelIrOperand? MatchValue = null,
    bool IsDefault = false);

public sealed record MidLevelIrTerminator(
    MidLevelIrTerminatorKind Kind,
    IReadOnlyList<int> Targets,
    string? ConditionText = null,
    string? ValueText = null,
    MidLevelIrOperand? Condition = null,
    MidLevelIrOperand? Value = null,
    IReadOnlyList<MidLevelIrSwitchCase>? SwitchCases = null,
    int? DefaultTarget = null,
    SourceLocation? Location = null,
    IReadOnlyList<int>? BranchWeights = null,
    IReadOnlyList<string>? LoopContracts = null,
    IReadOnlyList<string>? LoopAccessGroups = null);

public sealed record MidLevelIrBasicBlock(
    int Id,
    string Label,
    IReadOnlyList<MidLevelIrStatement> Statements,
    MidLevelIrTerminator Terminator);

public sealed record MidLevelIrFunction(
    string Name,
    string Signature,
    StarkTypeSymbol ReturnType,
    IReadOnlyList<TypedParameterSymbol> Parameters,
    bool HasBody,
    bool SupportsDirectCodeGeneration,
    int EntryBlockId,
    IReadOnlyList<MidLevelIrLocal> Locals,
    IReadOnlyList<MidLevelIrBasicBlock> Blocks,
    FunctionBodyLoweringKind BodyLoweringKind = FunctionBodyLoweringKind.DeclarationOnly,
    SourceLocation? Location = null);

public sealed record MidLevelIrModule(
    string ModuleName,
    IReadOnlyList<MidLevelIrFunction> Functions,
    IReadOnlyList<string>? AddressTakenFunctionRecords = null)
{
    public IReadOnlyList<string> AddressTakenFunctions =>
        AddressTakenFunctionRecords ?? [];
}

public abstract record SsaValue(StarkTypeSymbol Type, string Text);

public sealed record SsaValueReference(string Name, StarkTypeSymbol Type)
    : SsaValue(Type, Name);

public sealed record SsaIntegerConstant(BigInteger Value, StarkTypeSymbol Type)
    : SsaValue(Type, Value.ToString());

public sealed record SsaFloatConstant(string LiteralText, StarkTypeSymbol Type)
    : SsaValue(Type, LiteralText);

public sealed record SsaStringConstant(string LiteralText, StarkTypeSymbol Type)
    : SsaValue(Type, LiteralText);

public sealed record SsaTextDataAddressValue(
    string LiteralText,
    StarkTypeSymbol TextType,
    StarkTypeSymbol Type)
    : SsaValue(Type, $"&{LiteralText}.data");

public sealed record SsaBoolConstant(bool Value)
    : SsaValue(StarkTypeSymbols.Bool, Value ? "true" : "false");

public sealed record SsaNullConstant(StarkTypeSymbol Type)
    : SsaValue(Type, "null");

public sealed record SsaGlobalAddressValue(string GlobalName, StarkTypeSymbol PointeeType, StarkTypeSymbol Type)
    : SsaValue(Type, $"&{GlobalName}");

public sealed record SsaFunctionAddressValue(string FunctionName, StarkTypeSymbol Type)
    : SsaValue(Type, FunctionName);

public sealed record SsaUndefValue(StarkTypeSymbol Type)
    : SsaValue(Type, "undef");

public sealed record SsaZeroInitializerValue(StarkTypeSymbol Type)
    : SsaValue(Type, "zeroinitializer");

public enum SsaUnaryOperator
{
    Negate,
    LogicalNot,
    BitwiseNot
}

public enum SsaBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    WrappingAdd,
    WrappingSubtract,
    WrappingMultiply,
    SaturatingAdd,
    SaturatingSubtract,
    SaturatingMultiply,
    Divide,
    Modulo,
    BitwiseAnd,
    BitwiseXor,
    BitwiseOr,
    Exponent,
    ShiftLeft,
    ShiftRight,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

public abstract record SsaInstruction;

public abstract record SsaRValue(StarkTypeSymbol Type, string Text);

public sealed record SsaUseRValue(SsaValue Value)
    : SsaRValue(Value.Type, Value.Text);

public sealed record SsaUnaryRValue(
    SsaUnaryOperator Operator,
    SsaValue Operand,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaBinaryRValue(
    SsaBinaryOperator Operator,
    SsaValue Left,
    SsaValue Right,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaSelectRValue(
    SsaValue Condition,
    SsaValue WhenTrue,
    SsaValue WhenFalse,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaCallRValue(
    string FunctionName,
    IReadOnlyList<SsaValue> Arguments,
    StarkTypeSymbol Type,
    string Text,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<SsaValue?>? IndirectArgumentAddresses = null)
    : SsaRValue(Type, Text);

public sealed record SsaIndirectCallRValue(
    SsaValue Target,
    IReadOnlyList<SsaValue> Arguments,
    StarkTypeSymbol Type,
    string Text,
    StarkTypeSymbol? SourceReturnType = null)
    : SsaRValue(Type, Text);

public sealed record SsaConvertRValue(
    SsaValue Operand,
    StarkTypeSymbol TargetType,
    string Text)
    : SsaRValue(TargetType, Text);

public sealed record SsaExtractFieldRValue(
    SsaValue Target,
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaInsertFieldRValue(
    SsaValue Target,
    string FieldName,
    int FieldIndex,
    SsaValue Value,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaExtractIndexRValue(
    SsaValue Target,
    int ElementIndex,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaInsertIndexRValue(
    SsaValue Target,
    int ElementIndex,
    SsaValue Value,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaMakeSliceFromLocalRValue(
    string LocalName,
    StarkTypeSymbol SourceType,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaMakeSliceFromPointerRValue(
    SsaValue Pointer,
    SsaValue Length,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaDynamicStorageAllocationRValue(
    SsaValue Capacity,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaDynamicStorageFreeRValue(
    SsaValue Storage,
    string Text)
    : SsaRValue(StarkTypeSymbols.Void, Text);

public sealed record SsaDynamicStorageReserveRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    SsaValue AdditionalCapacity,
    string Text)
    : SsaRValue(StarkTypeSymbols.Void, Text);

public sealed record SsaDynamicStorageTryReserveRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    SsaValue AdditionalCapacity,
    string Text)
    : SsaRValue(StarkTypeSymbols.Bool, Text);

public sealed record SsaDynamicStorageMoveLastRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaDynamicStorageMoveAtRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    SsaValue Index,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaLoadSliceElementRValue(
    SsaValue Slice,
    SsaValue Index,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaTextSliceRValue(
    SsaValue TextValue,
    SsaValue Start,
    SsaValue Length,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaAddressOfLocalRValue(
    string LocalName,
    StarkTypeSymbol PointeeType,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaAddressOfParameterRValue(
    string ParameterName,
    StarkTypeSymbol PointeeType,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaFieldAddressRValue(
    SsaValue Address,
    StarkTypeSymbol AggregateType,
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaElementAddressRValue(
    SsaValue Address,
    StarkTypeSymbol AggregateType,
    SsaValue? Index,
    int? ConstantIndex,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaSliceElementAddressRValue(
    SsaValue Slice,
    SsaValue Index,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaLoadIndirectRValue(
    SsaValue Address,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaLoadGlobalRValue(
    string GlobalName,
    StarkTypeSymbol Type)
    : SsaRValue(Type, $"load {GlobalName}");

public sealed record SsaLoadLocalRValue(
    string LocalName,
    StarkTypeSymbol Type)
    : SsaRValue(Type, $"load {LocalName}");

public sealed record SsaPhiIncoming(
    int PredecessorBlockId,
    SsaValue Value);

public sealed record SsaSwitchCase(
    string Label,
    int TargetBlockId,
    SsaValue MatchValue);

public sealed record SsaPhi(
    string ResultName,
    string VariableName,
    StarkTypeSymbol Type,
    IReadOnlyList<SsaPhiIncoming> Incomings,
    SourceLocation? Location = null);

public sealed record SsaValueInstruction(
    string ResultName,
    SsaRValue Value,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null)
    : SsaInstruction;

public sealed record SsaAllocateLocalInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    string StorageClass = "stack",
    SourceLocation? Location = null,
    bool IsImmutable = false,
    bool HasConstProvenance = false)
    : SsaInstruction;

public sealed record SsaLifetimeStartInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    SourceLocation? Location = null)
    : SsaInstruction;

public sealed record SsaLifetimeEndInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    SourceLocation? Location = null)
    : SsaInstruction;

public sealed record SsaDeallocateLocalInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    string StorageClass = "heap",
    SourceLocation? Location = null)
    : SsaInstruction;

public sealed record SsaStoreLocalInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    SsaValue Value,
    SourceLocation? Location = null)
    : SsaInstruction;

public sealed record SsaStoreIndirectInstruction(
    SsaValue Address,
    StarkTypeSymbol ValueType,
    SsaValue Value,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null)
    : SsaInstruction;

public enum SsaMemoryTransferKind
{
    Copy,
    Move
}

public sealed record SsaCopyMemoryInstruction(
    SsaValue DestinationAddress,
    SsaValue SourceAddress,
    StarkTypeSymbol CopyType,
    SsaMemoryTransferKind TransferKind = SsaMemoryTransferKind.Copy,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null)
    : SsaInstruction;

public sealed record SsaStoreGlobalInstruction(
    string GlobalName,
    StarkTypeSymbol GlobalType,
    SsaValue Value,
    SourceLocation? Location = null)
    : SsaInstruction;

public enum SsaTerminatorKind
{
    Goto,
    Branch,
    Switch,
    Return,
    Unreachable
}

public sealed record SsaTerminator(
    SsaTerminatorKind Kind,
    IReadOnlyList<int> Targets,
    SsaValue? Condition = null,
    SsaValue? Value = null,
    IReadOnlyList<SsaSwitchCase>? SwitchCases = null,
    int? DefaultTarget = null,
    SourceLocation? Location = null,
    IReadOnlyList<int>? BranchWeights = null,
    IReadOnlyList<string>? LoopContracts = null,
    IReadOnlyList<string>? LoopAccessGroups = null);

public sealed record SsaBasicBlock(
    int Id,
    string Label,
    IReadOnlyList<SsaPhi> Phis,
    IReadOnlyList<SsaInstruction> Instructions,
    SsaTerminator Terminator);

public sealed record SsaFunction(
    string Name,
    StarkTypeSymbol ReturnType,
    IReadOnlyList<TypedParameterSymbol> Parameters,
    bool HasBody,
    bool SupportsDirectCodeGeneration,
    int EntryBlockId,
    IReadOnlyList<SsaBasicBlock> Blocks,
    FunctionBodyLoweringKind BodyLoweringKind = FunctionBodyLoweringKind.DeclarationOnly,
    SourceLocation? Location = null);

public sealed record SsaIrModule(
    string ModuleName,
    IReadOnlyList<SsaFunction> Functions,
    IReadOnlyList<string>? AddressTakenFunctionRecords = null)
{
    public IReadOnlyList<string> AddressTakenFunctions =>
        AddressTakenFunctionRecords ?? [];
}

public enum SsaFactLatticeKind
{
    Unknown,
    Known,
    Overdefined
}

public enum SsaNullabilityFactKind
{
    Unknown,
    Null,
    NonNull,
    Overdefined
}

public sealed record SsaIntegerRangeFact(BigInteger Min, BigInteger Max);

public sealed record SsaKnownBitsFact(BigInteger KnownZeroBits, BigInteger KnownOneBits);

public sealed record SsaTextLiteralPayloadFact(
    string DecodedText,
    string Utf8PayloadHex,
    string Utf32PayloadHex,
    bool IsAsciiOnly,
    int Utf8Length,
    int Utf32Length);

public sealed record SsaValueFacts(
    string ValueName,
    StarkTypeSymbol Type,
    SsaFactLatticeKind IntegerRangeKind = SsaFactLatticeKind.Unknown,
    SsaIntegerRangeFact? IntegerRange = null,
    SsaFactLatticeKind KnownBitsKind = SsaFactLatticeKind.Unknown,
    SsaKnownBitsFact? KnownBits = null,
    SsaFactLatticeKind BooleanKind = SsaFactLatticeKind.Unknown,
    bool? BooleanConstant = null,
    SsaNullabilityFactKind Nullability = SsaNullabilityFactKind.Unknown,
    SsaFactLatticeKind PointerAlignmentKind = SsaFactLatticeKind.Unknown,
    int? PointerAlignmentBytes = null,
    SsaFactLatticeKind LengthKind = SsaFactLatticeKind.Unknown,
    SsaIntegerRangeFact? LengthRange = null,
    SsaFactLatticeKind TextLiteralPayloadKind = SsaFactLatticeKind.Unknown,
    SsaTextLiteralPayloadFact? TextLiteralPayload = null);

public sealed record SsaFunctionFactModel(
    string FunctionName,
    IReadOnlyDictionary<string, SsaValueFacts> Values,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueFacts>>? BlockEntryValueFacts = null,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, SsaValueFacts>>? BlockExitValueFacts = null);

public sealed record SsaValueFactModel(
    string ModuleName,
    IReadOnlyDictionary<string, SsaFunctionFactModel> Functions);

public sealed record LlvmIrModule(
    string ModuleName,
    string Text,
    IReadOnlyList<string>? AddressTakenFunctionRecords = null)
{
    public IReadOnlyList<string> AddressTakenFunctions =>
        AddressTakenFunctionRecords ?? [];
}
