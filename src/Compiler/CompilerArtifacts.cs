using System.Numerics;
using Stark.Parsing;

namespace Stark.Compiler;

public static class CompilerArtifactKeys
{
    public static readonly ArtifactKey<ParseResult> ParseResult = new("parse.result");
    public static readonly ArtifactKey<SyntaxModel> SyntaxModel = new("syntax.model");
    public static readonly ArtifactKey<DeclarationIndex> DeclarationIndex = new("declarations.index");
    public static readonly ArtifactKey<ModuleGraph> ModuleGraph = new("modules.graph");
    public static readonly ArtifactKey<LoadedModuleSet> LoadedModules = new("modules.loaded");
    public static readonly ArtifactKey<SymbolCatalog> SymbolCatalog = new("symbols.catalog");
    public static readonly ArtifactKey<FunctionEffectModel> FunctionEffects = new("semantics.function-effects");
    public static readonly ArtifactKey<ClosedWorldOptimizationModel> ClosedWorldOptimization = new("semantics.closed-world-optimization");
    public static readonly ArtifactKey<TypeCheckModel> TypeCheckModel = new("typing.model");
    public static readonly ArtifactKey<EnumLayoutModel> EnumLayoutModel = new("typing.enum-layout");
    public static readonly ArtifactKey<SemanticValidationModel> SemanticValidation = new("semantics.validation");
    public static readonly ArtifactKey<OwnershipValidationModel> OwnershipValidation = new("semantics.ownership");
    public static readonly ArtifactKey<HighLevelIrModule> HighLevelIr = new("lowering.hir");
    public static readonly ArtifactKey<MidLevelIrModule> MidLevelIr = new("lowering.mir");
    public static readonly ArtifactKey<SsaIrModule> SsaIr = new("lowering.ssa");
    public static readonly ArtifactKey<SsaIrModule> OptimizedSsaIr = new("lowering.ssa.optimized");
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
    bool IsFfi);

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

public sealed record ParameterModel(string Name, string TypeText);

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
    AsmFunctionModel? Asm = null);

public sealed record DestructorDeclarationModel(
    bool IsMutable);

public sealed record TopLevelDeclarationModel(
    string Name,
    DeclarationKind Kind,
    StarkVisibility Visibility,
    FunctionDeclarationModel? Function,
    DestructorDeclarationModel? Destructor = null);

public sealed record SyntaxModel(
    string ModuleName,
    IReadOnlyList<ImportDeclarationModel> Imports,
    IReadOnlyList<TopLevelDeclarationModel> Declarations);

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

    public bool HasModuleNamespace(string moduleNamePrefix)
    {
        var prefix = $"{moduleNamePrefix}.";
        return AccessibleModules.Any(module => module.StartsWith(prefix, StringComparison.Ordinal));
    }

    public bool ContainsLoadedModule(string moduleName) => Modules.ContainsKey(moduleName);
}

public sealed record LoadedModuleDocument(
    ResolvedModuleReference Reference,
    ParseResult ParseResult,
    SyntaxModel SyntaxModel);

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
    bool IsHot,
    bool IsCold,
    InlinePreference InlinePreference);

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
    BigInteger? RangeMin = null,
    BigInteger? RangeMax = null,
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

    public static StarkTypeSymbol Integer(int bitWidth, BigInteger? rangeMin = null, BigInteger? rangeMax = null)
    {
        var displayName = rangeMin is null && rangeMax is null
            ? $"i{bitWidth}"
            : $"i{bitWidth}[{rangeMin} {rangeMax}]";
        return new StarkTypeSymbol(StarkTypeKind.Integer, displayName, BitWidth: bitWidth, RangeMin: rangeMin, RangeMax: rangeMax);
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

    public static StarkTypeSymbol FreezeReachableView(StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.Error)
        {
            return type;
        }

        if (type.Kind == StarkTypeKind.RawPointer && type.ElementType is not null)
        {
            return RawPointer(FreezeReachableView(type.ElementType), isMutable: false);
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
            StarkTypeKind.Integer => Integer(type.BitWidth ?? 32, type.RangeMin, type.RangeMax),
            StarkTypeKind.Float => Float(type.BitWidth ?? 32),
            StarkTypeKind.RawPointer when type.ElementType is not null => RawPointer(type.ElementType, type.IsMutablePointer),
            StarkTypeKind.FixedArray when type.ElementType is not null => FixedArray(type.ElementType, type.FixedLength),
            StarkTypeKind.Slice when type.ElementType is not null => Slice(type.ElementType),
            StarkTypeKind.Named when type.NamedType == OwnedAsciiName => OwnedAscii,
            StarkTypeKind.Named when type.NamedType == OwnedUnicodeName => OwnedUnicode,
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

public sealed record FieldSymbol(string Name, StarkTypeSymbol Type);

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

public sealed record TypedParameterSymbol(string Name, StarkTypeSymbol Type);

public sealed record TypedConstructorShape(
    string TypeName,
    IReadOnlyList<TypedParameterSymbol> Parameters,
    bool IsPrimaryShape)
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
    string? SourceName = null)
{
    public string DisplaySourceName => SourceName ?? Name;
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

public sealed record ObjectCreationTypingRecord(
    string ExpressionText,
    TypedConstructorShape? Constructor,
    SourceLocation Location);

public sealed record TypeCheckModel(
    string ModuleName,
    IReadOnlyDictionary<string, NamedTypeSymbol> NamedTypes,
    IReadOnlyDictionary<string, TypedFunctionSignature> Functions,
    IReadOnlyDictionary<string, TypedGlobalSymbol> Globals,
    IReadOnlyList<LiteralTypingRecord> Literals,
    IReadOnlyList<ObjectCreationTypingRecord> ObjectCreations,
    IReadOnlyDictionary<string, IReadOnlyList<TypedFunctionSignature>>? FunctionOverloads = null)
{
    public IReadOnlyDictionary<string, IReadOnlyList<TypedFunctionSignature>> Overloads =>
        FunctionOverloads
        ?? Functions.Values
            .GroupBy(static function => function.DisplaySourceName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<TypedFunctionSignature>)group.ToArray(),
                StringComparer.Ordinal);
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
    AbiParameterKind Kind);

public sealed record AbiFunctionSignature(
    string Name,
    string SymbolName,
    StarkTypeSymbol SourceReturnType,
    StarkTypeSymbol LlvmReturnType,
    IReadOnlyList<AbiParameterSymbol> Parameters,
    bool IsFfi,
    string? SourceName = null)
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

internal sealed record ConcreteTypeLayout(int SizeBytes, int AlignmentBytes);

internal static class ConcreteTypeLayoutHelper
{
    public static ConcreteTypeLayout? TryGetConcreteTypeLayout(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts = null)
    {
        return TryGetConcreteTypeLayout(type, namedTypes, enumLayouts, new HashSet<string>(StringComparer.Ordinal));
    }

    private static ConcreteTypeLayout? TryGetConcreteTypeLayout(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
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
            StarkTypeKind.FixedArray when concreteType.ElementType is not null && concreteType.FixedLength is int fixedLength =>
                TryGetFixedArrayLayout(concreteType.ElementType, fixedLength, namedTypes, enumLayouts, activeNamedTypes),
            StarkTypeKind.Named when concreteType.NamedType is not null
                                     && namedTypes.TryGetValue(concreteType.NamedType, out var namedType)
                                     && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record =>
                TryGetNamedTypeLayout(namedType, namedTypes, enumLayouts, activeNamedTypes),
            StarkTypeKind.Named when concreteType.NamedType is not null
                                     && namedTypes.TryGetValue(concreteType.NamedType, out var enumType)
                                     && enumType.Kind == DeclarationKind.Enum
                                     && enumLayouts is not null
                                     && enumLayouts.TryGetValue(concreteType.NamedType, out var enumLayout) =>
                TryGetEnumTypeLayout(enumLayout, namedTypes, enumLayouts, activeNamedTypes),
            _ => null
        };
    }

    private static ConcreteTypeLayout? TryGetFixedArrayLayout(
        StarkTypeSymbol elementType,
        int fixedLength,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        ISet<string> activeNamedTypes)
    {
        var elementLayout = TryGetConcreteTypeLayout(elementType, namedTypes, enumLayouts, activeNamedTypes);
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
                var fieldLayout = TryGetConcreteTypeLayout(field.Type, namedTypes, enumLayouts, activeNamedTypes);
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
                var fieldLayout = TryGetConcreteTypeLayout(field.Type, namedTypes, enumLayouts, activeNamedTypes);
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
            4 => new ConcreteTypeLayout(4, 4),
            8 => new ConcreteTypeLayout(8, 8),
            _ => new ConcreteTypeLayout(sizeBytes, 1)
        };
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
    bool CapturesArgumentMemory);

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

public sealed record FunctionValidationSummary(
    string Name,
    StarkFunctionKind DeclaredKind,
    StarkFunctionKind EffectiveKind,
    bool EffectsValid,
    bool BorrowingValid,
    IReadOnlyList<string> CalledFunctions,
    FunctionMemoryEffectSummary? MemoryEffects = null,
    IReadOnlyList<ParameterMemoryEffectSummary>? Parameters = null,
    IReadOnlyList<CallMemoryEffectSummary>? Calls = null)
{
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
    FunctionEffectProfile Effects);

public sealed record HighLevelIrModule(
    string ModuleName,
    IReadOnlyList<HighLevelIrFunction> Functions);

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
    bool IsAddressable = false);

public abstract record MidLevelIrOperand(StarkTypeSymbol Type, string Text);

public sealed record MidLevelIrLocalOperand(string Name, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, Name);

public sealed record MidLevelIrParameterOperand(string Name, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, Name);

public sealed record MidLevelIrGlobalOperand(string Name, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, Name);

public sealed record MidLevelIrGlobalAddressOperand(string Name, StarkTypeSymbol PointeeType, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, $"&{Name}");

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
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null)
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

public sealed record MidLevelIrStatement(
    MidLevelIrStatementKind Kind,
    string Text,
    string? TargetName = null,
    StarkTypeSymbol? TargetType = null,
    MidLevelIrOperand? Address = null,
    MidLevelIrRValue? Value = null);

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
    int? DefaultTarget = null);

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
    FunctionBodyLoweringKind BodyLoweringKind = FunctionBodyLoweringKind.DeclarationOnly);

public sealed record MidLevelIrModule(
    string ModuleName,
    IReadOnlyList<MidLevelIrFunction> Functions);

public abstract record SsaValue(StarkTypeSymbol Type, string Text);

public sealed record SsaValueReference(string Name, StarkTypeSymbol Type)
    : SsaValue(Type, Name);

public sealed record SsaIntegerConstant(BigInteger Value, StarkTypeSymbol Type)
    : SsaValue(Type, Value.ToString());

public sealed record SsaFloatConstant(string LiteralText, StarkTypeSymbol Type)
    : SsaValue(Type, LiteralText);

public sealed record SsaStringConstant(string LiteralText, StarkTypeSymbol Type)
    : SsaValue(Type, LiteralText);

public sealed record SsaBoolConstant(bool Value)
    : SsaValue(StarkTypeSymbols.Bool, Value ? "true" : "false");

public sealed record SsaNullConstant(StarkTypeSymbol Type)
    : SsaValue(Type, "null");

public sealed record SsaGlobalAddressValue(string GlobalName, StarkTypeSymbol PointeeType, StarkTypeSymbol Type)
    : SsaValue(Type, $"&{GlobalName}");

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

public sealed record SsaCallRValue(
    string FunctionName,
    IReadOnlyList<SsaValue> Arguments,
    StarkTypeSymbol Type,
    string Text,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null)
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
    IReadOnlyList<SsaPhiIncoming> Incomings);

public sealed record SsaValueInstruction(
    string ResultName,
    SsaRValue Value)
    : SsaInstruction;

public sealed record SsaAllocateLocalInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    string StorageClass = "stack")
    : SsaInstruction;

public sealed record SsaLifetimeStartInstruction(
    string LocalName,
    StarkTypeSymbol LocalType)
    : SsaInstruction;

public sealed record SsaLifetimeEndInstruction(
    string LocalName,
    StarkTypeSymbol LocalType)
    : SsaInstruction;

public sealed record SsaStoreLocalInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    SsaValue Value)
    : SsaInstruction;

public sealed record SsaStoreIndirectInstruction(
    SsaValue Address,
    StarkTypeSymbol ValueType,
    SsaValue Value)
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
    SsaMemoryTransferKind TransferKind = SsaMemoryTransferKind.Copy)
    : SsaInstruction;

public sealed record SsaStoreGlobalInstruction(
    string GlobalName,
    StarkTypeSymbol GlobalType,
    SsaValue Value)
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
    int? DefaultTarget = null);

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
    FunctionBodyLoweringKind BodyLoweringKind = FunctionBodyLoweringKind.DeclarationOnly);

public sealed record SsaIrModule(
    string ModuleName,
    IReadOnlyList<SsaFunction> Functions);

public sealed record LlvmIrModule(string ModuleName, string Text);
