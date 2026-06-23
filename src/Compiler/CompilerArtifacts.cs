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
    public static readonly ArtifactKey<LoweringContractValidationModel> LoweringContractValidation = new("lowering.contract-validation");
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

public enum StarkFfiAbi
{
    C,
    CDecl,
    StdCall,
    FastCall,
    ThisCall,
    VectorCall,
    SysV,
    Win64,
    Aapcs,
    Aapcs64
}

internal static class StarkFfiAbiFacts
{
    public static string DisplayName(StarkFfiAbi abi)
    {
        return abi switch
        {
            StarkFfiAbi.C => "c",
            StarkFfiAbi.CDecl => "cdecl",
            StarkFfiAbi.StdCall => "stdcall",
            StarkFfiAbi.FastCall => "fastcall",
            StarkFfiAbi.ThisCall => "thiscall",
            StarkFfiAbi.VectorCall => "vectorcall",
            StarkFfiAbi.SysV => "sysv",
            StarkFfiAbi.Win64 => "win64",
            StarkFfiAbi.Aapcs => "aapcs",
            StarkFfiAbi.Aapcs64 => "aapcs64",
            _ => "c"
        };
    }

    public static bool TryParse(string? text, out StarkFfiAbi abi)
    {
        abi = text?.Trim().ToLowerInvariant() switch
        {
            "c" => StarkFfiAbi.C,
            "cdecl" => StarkFfiAbi.CDecl,
            "stdcall" => StarkFfiAbi.StdCall,
            "fastcall" => StarkFfiAbi.FastCall,
            "thiscall" => StarkFfiAbi.ThisCall,
            "vectorcall" => StarkFfiAbi.VectorCall,
            "sysv" => StarkFfiAbi.SysV,
            "win64" => StarkFfiAbi.Win64,
            "aapcs" => StarkFfiAbi.Aapcs,
            "aapcs64" => StarkFfiAbi.Aapcs64,
            _ => StarkFfiAbi.C
        };

        return text?.Trim().ToLowerInvariant() is "c"
            or "cdecl"
            or "stdcall"
            or "fastcall"
            or "thiscall"
            or "vectorcall"
            or "sysv"
            or "win64"
            or "aapcs"
            or "aapcs64";
    }

    public static string? LlvmCallingConventionName(StarkFfiAbi? abi)
    {
        return abi switch
        {
            null or StarkFfiAbi.C => null,
            StarkFfiAbi.CDecl => null,
            StarkFfiAbi.StdCall => "x86_stdcallcc",
            StarkFfiAbi.FastCall => "x86_fastcallcc",
            StarkFfiAbi.ThisCall => "x86_thiscallcc",
            StarkFfiAbi.VectorCall => "x86_vectorcallcc",
            StarkFfiAbi.SysV => "x86_64_sysvcc",
            StarkFfiAbi.Win64 => "win64cc",
            StarkFfiAbi.Aapcs => "aapcscc",
            StarkFfiAbi.Aapcs64 => "aarch64_aapcscc",
            _ => null
        };
    }
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

    public static bool IsCompileTimeCallable(StarkFunctionKind kind)
    {
        return IsFinite(kind) || IsLaw(kind);
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
    public const string ClosureEnvironmentParameterName = "$env";

    public static string BuildLambdaFunctionName(string enclosingFunctionName, SourceLocation location)
    {
        return $"{enclosingFunctionName}.__lambda_{location.Line}_{location.Column}";
    }

    public static string BuildClosureEnvironmentTypeName(string lambdaFunctionName)
    {
        return $"{lambdaFunctionName}.__env";
    }

    public static string BuildClosureDropFunctionName(string lambdaFunctionName)
    {
        return $"{lambdaFunctionName}.__drop";
    }

    public static string BuildClosureFunctionAdapterName(string enclosingFunctionName, SourceLocation location)
    {
        return $"{enclosingFunctionName}.__closure_adapter_{location.Line}_{location.Column}";
    }

    public static string EmptyClosureDropFunctionName => "__stark_closure_drop_empty";

    public static TypedFunctionSignature BuildLambdaSignature(LambdaTypingRecord lambda)
    {
        var returnType = lambda.FunctionPointerType.FunctionPointerReturnType ?? StarkTypeSymbols.Error;
        var parameterTypes = lambda.FunctionPointerType.FunctionPointerParameterTypes ?? [];
        var parameters = parameterTypes
            .Select((type, index) => new TypedParameterSymbol(
                lambda.ParameterNames[index],
                type,
                RawPointerElementCountExpression: MapFunctionPointerRawPointerElementCountExpressionToLambdaParameterNames(
                    StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(
                        lambda.FunctionPointerType,
                        index),
                    lambda.ParameterNames)))
            .ToArray();

        return new TypedFunctionSignature(
            lambda.FunctionName,
            returnType,
            parameters,
            SourceName: lambda.FunctionName,
            Kind: lambda.FunctionPointerType.FunctionPointerKind ?? StarkFunctionKind.Fn,
            PointeeDeadOnReturnParameterNames: MapFunctionPointerParameterNamesToLambdaParameterNames(
                lambda.FunctionPointerType.FunctionPointerPointeeDeadOnReturnParameterNames,
                lambda.ParameterNames));
    }

    public static TypedFunctionSignature BuildClosureLambdaSignature(ClosureLambdaTypingRecord lambda)
    {
        var returnType = lambda.ClosureType.ClosureReturnType ?? StarkTypeSymbols.Error;
        var parameterTypes = lambda.ClosureType.ClosureParameterTypes ?? [];
        var parameters = new List<TypedParameterSymbol>(parameterTypes.Count + 1)
        {
            new(
                ClosureEnvironmentParameterName,
                lambda.EnvironmentParameterType)
        };

        parameters.AddRange(parameterTypes.Select((type, index) => new TypedParameterSymbol(
                lambda.ParameterNames[index],
                type,
                RawPointerElementCountExpression: MapFunctionPointerRawPointerElementCountExpressionToLambdaParameterNames(
                    StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(
                        lambda.ClosureType,
                        index),
                    lambda.ParameterNames))));

        return new TypedFunctionSignature(
            lambda.FunctionName,
            returnType,
            parameters.ToArray(),
            SourceName: lambda.FunctionName,
            Kind: lambda.ClosureType.ClosureFunctionKind ?? StarkFunctionKind.Fn,
            PointeeDeadOnReturnParameterNames: MapFunctionPointerParameterNamesToLambdaParameterNames(
                lambda.ClosureType.ClosurePointeeDeadOnReturnParameterNames,
                lambda.ParameterNames));
    }

    public static TypedFunctionSignature BuildClosureFunctionAdapterSignature(ClosureFunctionPromotionTypingRecord adapter)
    {
        var returnType = adapter.ClosureType.ClosureReturnType ?? StarkTypeSymbols.Error;
        var parameterTypes = adapter.ClosureType.ClosureParameterTypes ?? [];
        var parameters = new List<TypedParameterSymbol>(parameterTypes.Count + 1)
        {
            new(
                ClosureEnvironmentParameterName,
                BuildClosureEnvironmentPointerType(adapter.ClosureType))
        };

        parameters.AddRange(parameterTypes.Select((parameterType, index) => new TypedParameterSymbol(
            $"arg{index}",
            parameterType,
            RawPointerElementCountExpression: StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(
                adapter.ClosureType,
                index))));

        return new TypedFunctionSignature(
            adapter.AdapterFunctionName,
            returnType,
            parameters,
            SourceName: adapter.AdapterFunctionName,
            Kind: adapter.ClosureType.ClosureFunctionKind ?? StarkFunctionKind.Fn,
            PointeeDeadOnReturnParameterNames: ShiftPointeeDeadOnReturnParameters(
                adapter.ClosureType.ClosurePointeeDeadOnReturnParameterNames ?? [],
                offset: 1));
    }

    public static TypedFunctionSignature BuildClosureDropSignature(string functionName)
    {
        return new TypedFunctionSignature(
            functionName,
            StarkTypeSymbols.Void,
            [
                new TypedParameterSymbol(
                    ClosureEnvironmentParameterName,
                    BuildClosureDropEnvironmentPointerType())
            ],
            SourceName: functionName,
            Kind: StarkFunctionKind.Fn);
    }

    public static FunctionEffectProfile BuildClosureDropEffectProfile(string functionName)
    {
        return new FunctionEffectProfile(
            functionName,
            StarkFunctionKind.Fn,
            ReadsArgumentMemory: true,
            IsPure: false,
            NoSync: true,
            NoFree: false,
            NoUnwind: true,
            WillReturn: true,
            MustProgress: true,
            UseFastCallingConvention: false,
            IsFfi: false,
            IsVarargs: false,
            IsHot: false,
            IsCold: true,
            InlinePreference: InlinePreference.InlineHint,
            IsStrictFp: false);
    }

    public static StarkTypeSymbol BuildClosureEnvironmentPointerType(StarkTypeSymbol closureType)
    {
        return StarkTypeSymbols.RawPointer(
            StarkTypeSymbols.Integer(8),
            isMutable: closureType.ClosureCallCapability is StarkClosureCallCapability.Mut or StarkClosureCallCapability.Once);
    }

    public static StarkTypeSymbol BuildClosureDropEnvironmentPointerType()
    {
        return StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true);
    }

    public static StarkTypeSymbol BuildClosureDropFunctionPointerType()
    {
        return StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            StarkTypeSymbols.Void,
            [BuildClosureDropEnvironmentPointerType()]);
    }

    public static NamedTypeSymbol BuildClosureEnvironmentNamedType(
        string environmentTypeName,
        IReadOnlyList<ClosureCaptureFieldSymbol> captureFields)
    {
        var orderedFields = captureFields
            .Select(static capture => new FieldSymbol(capture.FieldName, capture.FieldType))
            .ToArray();
        var fields = orderedFields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        return new NamedTypeSymbol(environmentTypeName, DeclarationKind.Struct, fields, orderedFields);
    }

    public static ClosureCaptureStorageKind GetLambdaCaptureStorageKind(string mode)
    {
        return mode is "read" or "mut" or "out" or "init"
            ? ClosureCaptureStorageKind.Address
            : ClosureCaptureStorageKind.Value;
    }

    public static StarkTypeSymbol GetLambdaCaptureBodyType(StarkTypeSymbol type, string mode)
    {
        return mode switch
        {
            "addr" => StarkTypeSymbols.RawPointer(StarkTypeSymbols.FreezeAddressPointeeType(type), isMutable: false),
            "shared" => StarkTypeSymbols.WithQualifiers(type, accessKind: StarkAccessKind.Shared, isMutableView: false),
            "out" => StarkTypeSymbols.WithQualifiers(type, initializationKind: StarkInitializationKind.Out),
            "init" => StarkTypeSymbols.WithQualifiers(type, initializationKind: StarkInitializationKind.Init),
            _ => type
        };
    }

    public static StarkTypeSymbol GetLambdaCaptureFieldType(StarkTypeSymbol type, string mode)
    {
        var bodyType = GetLambdaCaptureBodyType(type, mode);
        return GetLambdaCaptureStorageKind(mode) == ClosureCaptureStorageKind.Address
            ? StarkTypeSymbols.RawPointer(GetLambdaCaptureAddressPointeeType(bodyType), isMutable: LambdaCaptureModeExposesWritableBinding(mode))
            : bodyType;
    }

    public static StarkTypeSymbol GetLambdaCaptureAddressPointeeType(StarkTypeSymbol bodyType)
    {
        return StarkTypeSymbols.WithQualifiers(
            bodyType,
            borrowKind: StarkBorrowKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    public static bool LambdaCaptureModeExposesWritableBinding(string mode)
    {
        return mode is "mut" or "out" or "init";
    }

    public static StarkTypeSymbol BuildClosureInvokeFunctionPointerType(StarkTypeSymbol closureType)
    {
        var environmentPointerType = BuildClosureEnvironmentPointerType(closureType);
        var sourceParameterTypes = closureType.ClosureParameterTypes ?? [];
        var parameterTypes = new List<StarkTypeSymbol>(sourceParameterTypes.Count + 1)
        {
            environmentPointerType
        };
        parameterTypes.AddRange(sourceParameterTypes);

        var rawPointerBounds = new string?[parameterTypes.Count];
        for (var index = 0; index < sourceParameterTypes.Count; index++)
        {
            rawPointerBounds[index + 1] = ShiftSyntheticArgumentExpression(
                StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(closureType, index),
                offset: 1);
        }

        return StarkTypeSymbols.FunctionPointer(
            closureType.ClosureFunctionKind ?? StarkFunctionKind.Fn,
            closureType.ClosureReturnType ?? StarkTypeSymbols.Error,
            parameterTypes,
            ShiftDisjointGroups(closureType.ClosureDisjointParameterGroups ?? [], offset: 1),
            ShiftOverlapGroups(closureType.ClosureOverlapParameterGroups ?? [], offset: 1),
            ShiftSameGroups(closureType.ClosureSameParameterGroups ?? [], offset: 1),
            rawPointerBounds,
            isTailCallable: closureType.ClosureIsTailCallable,
            pointeeDeadOnReturnParameterNames: ShiftPointeeDeadOnReturnParameters(
                closureType.ClosurePointeeDeadOnReturnParameterNames ?? [],
                offset: 1));
    }

    private static IReadOnlyList<ParameterDisjointGroup> ShiftDisjointGroups(
        IReadOnlyList<ParameterDisjointGroup> groups,
        int offset)
    {
        return groups
            .Select(group => new ParameterDisjointGroup(
                group.ParameterNames.Select(name => ShiftSyntheticArgumentName(name, offset)).ToArray(),
                group.MemoryRegions
                    .Select(region => new ParameterMemoryRegion(
                        ShiftSyntheticArgumentName(region.ParameterName, offset),
                        ShiftSyntheticArgumentExpression(region.StartExpression, offset),
                        ShiftSyntheticArgumentExpression(region.CountExpression, offset)))
                    .ToArray()))
            .ToArray();
    }

    private static IReadOnlyList<ParameterOverlapGroup> ShiftOverlapGroups(
        IReadOnlyList<ParameterOverlapGroup> groups,
        int offset)
    {
        return groups
            .Select(group => new ParameterOverlapGroup(group.ParameterNames.Select(name => ShiftSyntheticArgumentName(name, offset)).ToArray()))
            .ToArray();
    }

    private static IReadOnlyList<ParameterSameGroup> ShiftSameGroups(
        IReadOnlyList<ParameterSameGroup> groups,
        int offset)
    {
        return groups
            .Select(group => new ParameterSameGroup(group.ParameterNames.Select(name => ShiftSyntheticArgumentName(name, offset)).ToArray()))
            .ToArray();
    }

    private static IReadOnlyList<string> ShiftPointeeDeadOnReturnParameters(
        IReadOnlyList<string> parameters,
        int offset)
    {
        return parameters
            .Select(name => ShiftSyntheticArgumentName(name, offset))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ShiftSyntheticArgumentName(string name, int offset)
    {
        return name.StartsWith("arg", StringComparison.Ordinal)
            && int.TryParse(name[3..], out var index)
            && index >= 0
                ? $"arg{index + offset}"
                : name;
    }

    private static string? ShiftSyntheticArgumentExpression(string? expression, int offset)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return expression;
        }

        return expression.StartsWith("arg", StringComparison.Ordinal)
            && int.TryParse(expression[3..], out var index)
            && index >= 0
                ? $"arg{index + offset}"
                : expression;
    }

    private static string? MapFunctionPointerRawPointerElementCountExpressionToLambdaParameterNames(
        string? expression,
        IReadOnlyList<string> parameterNames)
    {
        if (string.IsNullOrWhiteSpace(expression)
            || !expression.StartsWith("arg", StringComparison.Ordinal)
            || !int.TryParse(expression[3..], out var parameterIndex)
            || parameterIndex < 0
            || parameterIndex >= parameterNames.Count)
        {
            return expression;
        }

        return parameterNames[parameterIndex];
    }

    private static IReadOnlyList<string> MapFunctionPointerParameterNamesToLambdaParameterNames(
        IReadOnlyList<string>? names,
        IReadOnlyList<string> parameterNames)
    {
        return (names ?? [])
            .Select(name => name.StartsWith("arg", StringComparison.Ordinal)
                && int.TryParse(name[3..], out var parameterIndex)
                && parameterIndex >= 0
                && parameterIndex < parameterNames.Count
                    ? parameterNames[parameterIndex]
                    : name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static FunctionEffectProfile BuildLambdaEffectProfile(LambdaTypingRecord lambda)
    {
        var kind = lambda.FunctionPointerType.FunctionPointerKind ?? StarkFunctionKind.Fn;
        return BuildLambdaEffectProfile(
            lambda.FunctionName,
            kind,
            lambda.FunctionPointerType.FunctionPointerIsTailCallable);
    }

    public static FunctionEffectProfile BuildClosureLambdaEffectProfile(ClosureLambdaTypingRecord lambda)
    {
        var kind = lambda.ClosureType.ClosureFunctionKind ?? StarkFunctionKind.Fn;
        return BuildLambdaEffectProfile(
            lambda.FunctionName,
            kind,
            lambda.ClosureType.ClosureIsTailCallable);
    }

    public static FunctionEffectProfile BuildClosureFunctionAdapterEffectProfile(ClosureFunctionPromotionTypingRecord adapter)
    {
        var kind = adapter.ClosureType.ClosureFunctionKind ?? adapter.Signature.Kind;
        return BuildLambdaEffectProfile(
            adapter.AdapterFunctionName,
            kind,
            adapter.ClosureType.ClosureIsTailCallable) with
        {
            InlinePreference = InlinePreference.Inline
        };
    }

    private static FunctionEffectProfile BuildLambdaEffectProfile(
        string functionName,
        StarkFunctionKind kind,
        bool isTailCallable)
    {
        var isLaw = FunctionKindFacts.IsLaw(kind);
        var isFinite = FunctionKindFacts.IsFinite(kind);

        return new FunctionEffectProfile(
            functionName,
            kind,
            ReadsArgumentMemory: false,
            IsPure: isLaw,
            NoSync: isLaw,
            NoFree: isLaw,
            NoUnwind: true,
            WillReturn: isFinite,
            MustProgress: isFinite,
            UseFastCallingConvention: !isTailCallable,
            IsTailCallable: isTailCallable,
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
    bool IsUnsafe = false,
    StarkFfiAbi? FfiAbi = null,
    bool IsTailCallable = false);

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

public sealed record ComptimeGenericParameterSymbol(
    string Name,
    StarkTypeSymbol Type);

public sealed record ComptimeValueArgumentSymbol(
    string ParameterName,
    BigInteger IntegerValue,
    StarkTypeSymbol Type,
    bool IsSymbolic = false,
    string? SymbolicSourceName = null)
{
    public string SourceName => SymbolicSourceName ?? ParameterName;
    public string DisplayName => IsSymbolic
        ? string.Equals(ParameterName, SourceName, StringComparison.Ordinal)
            ? ParameterName
            : $"{ParameterName}={SourceName}"
        : $"{ParameterName}={IntegerValue}";
}

public sealed record ParameterMemoryRegion(
    string ParameterName,
    string? StartExpression = null,
    string? CountExpression = null)
{
    public bool IsWholeParameter => StartExpression is null && CountExpression is null;

    public string DisplayText => IsWholeParameter
        ? ParameterName
        : $"{ParameterName}[{StartExpression}, {CountExpression}]";
}

public sealed record ParameterDisjointGroup(
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<ParameterMemoryRegion>? Regions = null)
{
    public IReadOnlyList<ParameterMemoryRegion> MemoryRegions =>
        Regions ?? ParameterNames.Select(static name => new ParameterMemoryRegion(name)).ToArray();

    public bool HasSubregions => MemoryRegions.Any(static region => !region.IsWholeParameter);
}

public sealed record ParameterOverlapGroup(IReadOnlyList<string> ParameterNames);

public sealed record ParameterSameGroup(IReadOnlyList<string> ParameterNames);

public sealed record ParameterValueContract(string LeftText, string OperatorText, string RightText)
{
    public string DisplayText => $"{LeftText} {OperatorText} {RightText}";
}

public enum ThreadSafetyLawAttributeKind
{
    Grant,
    Deny
}

public sealed record ThreadSafetyLawPredicateModel(
    string LawName,
    string TypeText);

public sealed record ThreadSafetyLawAttributeModel(
    ThreadSafetyLawAttributeKind Kind,
    string LawName,
    ThreadSafetyLawPredicateModel? Condition = null);

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
    IReadOnlyList<ComptimeGenericParameterSymbol>? ComptimeGenericParameterNames = null,
    string? PublishedOverloadKey = null,
    bool IsStatic = false,
    IReadOnlyList<ModuleAttributeModel>? Attributes = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    IReadOnlyList<ParameterDisjointGroup>? DisjointParameterGroups = null,
    IReadOnlyList<ParameterOverlapGroup>? OverlapParameterGroups = null,
    IReadOnlyList<ParameterSameGroup>? SameParameterGroups = null,
    IReadOnlyList<string>? PointeeDeadOnReturnParameterNames = null,
    IReadOnlyList<ThreadSafetyLawPredicateModel>? ThreadSafetyLawPredicates = null,
    IReadOnlyList<ParameterValueContract>? ValueParameterContracts = null,
    string? ExternalLinkName = null)
{
    public IReadOnlyList<string> GenericParams => GenericParameterNames ?? [];
    public IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeGenericParams => ComptimeGenericParameterNames ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 } || ComptimeGenericParameterNames is { Count: > 0 };
    public IReadOnlyList<ParameterDisjointGroup> DisjointGroups => DisjointParameterGroups ?? [];
    public IReadOnlyList<ParameterOverlapGroup> OverlapGroups => OverlapParameterGroups ?? [];
    public IReadOnlyList<ParameterSameGroup> SameGroups => SameParameterGroups ?? [];
    public IReadOnlyList<string> PointeeDeadOnReturnParameters => PointeeDeadOnReturnParameterNames ?? [];
    public IReadOnlyList<ThreadSafetyLawPredicateModel> ThreadSafetyLaws => ThreadSafetyLawPredicates ?? [];
    public IReadOnlyList<ParameterValueContract> ValueContracts => ValueParameterContracts ?? [];
}

public sealed record DestructorDeclarationModel(
    bool IsMutable,
    IReadOnlyList<ModuleAttributeModel>? Attributes = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default);

public sealed record TypeAliasDeclarationModel(
    string Name,
    string AliasedType,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<ComptimeGenericParameterSymbol>? ComptimeGenericParameterNames = null)
{
    public IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeGenericParams => ComptimeGenericParameterNames ?? [];
}

public sealed record TypeAliasSymbol(
    string Name,
    string ModuleName,
    StarkVisibility Visibility,
    StarkTypeSymbol TargetType,
    IReadOnlyList<string>? GenericParameterNames = null,
    IReadOnlyList<ComptimeGenericParameterSymbol>? ComptimeGenericParameterNames = null,
    bool IsExternal = false)
{
    public IReadOnlyList<string> GenericParams => GenericParameterNames ?? [];
    public IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeGenericParams => ComptimeGenericParameterNames ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 } || ComptimeGenericParameterNames is { Count: > 0 };
}

public sealed record AssociatedTypeSymbol(
    string Name,
    StarkTypeSymbol? TargetType = null)
{
    public bool IsRequired => TargetType is null;
}

public sealed record TopLevelDeclarationModel(
    string Name,
    DeclarationKind Kind,
    StarkVisibility Visibility,
    FunctionDeclarationModel? Function,
    DestructorDeclarationModel? Destructor = null,
    TypeAliasDeclarationModel? TypeAlias = null,
    IReadOnlyList<ModuleAttributeModel>? Attributes = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    IReadOnlyList<ThreadSafetyLawAttributeModel>? ThreadSafetyLawAttributes = null)
{
    public IReadOnlyList<ThreadSafetyLawAttributeModel> ThreadSafetyLaws => ThreadSafetyLawAttributes ?? [];
}

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

/// <summary>
/// Shares parsed source modules across the sequential pipeline runs of a single compiler
/// invocation (the root compile plus its per-dependency compiles). Only diagnostic-free
/// parses of plain source files are cached so error reporting and package-image loading
/// behave exactly as uncached compilation.
/// </summary>
public sealed class SharedSourceModuleParseCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SourceModuleParse> _modules = new(StringComparer.Ordinal);

    public bool TryGet(string moduleName, string? filePath, out SourceModuleParse module)
    {
        if (filePath is not null)
        {
            lock (_lock)
            {
                if (_modules.TryGetValue(moduleName, out var cached)
                    && string.Equals(cached.Reference.FilePath, filePath, StringComparison.Ordinal))
                {
                    module = cached;
                    return true;
                }
            }
        }

        module = default!;
        return false;
    }

    public void Add(SourceModuleParse module)
    {
        if (module.Reference.FilePath is not null)
        {
            lock (_lock)
            {
                _modules[module.Reference.ModuleName] = module;
            }
        }
    }
}

public sealed record LoadedModuleDocument(
    ResolvedModuleReference Reference,
    ParseResult ParseResult,
    SyntaxModel SyntaxModel,
    LoadedPackageImageFacts? PackageImageFacts = null,
    LlvmTargetInfo? TargetInfo = null)
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
    IReadOnlyList<ImportedTemplateFunctionAddressSummary>? FunctionAddressSummaries = null,
    IReadOnlyList<ImportedTemplateBoundOperationSummary>? BoundOperationSummaries = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    IReadOnlyList<ImportedTemplateTryPropagationSummary>? TryPropagationSummaries = null)
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

    public IReadOnlyList<ImportedTemplateTryPropagationSummary> TryPropagations =>
        TryPropagationSummaries ?? [];

    public IReadOnlyList<ImportedTemplateDirectCallSummary> DirectCalls =>
        DirectCallSummaries ?? [];

    public IReadOnlyList<ImportedTemplateFieldAccessSummary> FieldAccesses =>
        FieldAccessSummaries ?? [];

    public IReadOnlyList<ImportedTemplateMemberCallSummary> MemberCalls =>
        MemberCallSummaries ?? [];

    public IReadOnlyList<ImportedTemplateFunctionAddressSummary> FunctionAddresses =>
        FunctionAddressSummaries ?? [];

    public IReadOnlyList<ImportedTemplateBoundOperationSummary> BoundOperations =>
        BoundOperationSummaries ?? [];
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
    ForTraversal,
    While,
    If,
    Break,
    Continue,
    Return
}

public enum ImportedTemplateTypedSwitchCaseKind
{
    Literal,
    Range,
    MatchAll,
    Default,
    EnumPattern,
    AggregatePattern,
    ListPattern
}

public enum ImportedTemplateTypedSwitchFieldPatternKind
{
    Discard,
    Capture,
    Literal,
    Range,
    EnumPattern,
    AggregatePattern,
    ListPattern
}

public enum ImportedTemplateTypedBodyExpressionKind
{
    NameReference,
    Literal,
    ArrayInitializer,
    ObjectInitializer,
    Assignment,
    Conversion,
    TryPropagation,
    UnaryOperation,
    BinaryOperation,
    ComparisonChain,
    Conditional,
    Comptime,
    ObjectCreation,
    EnumConstructor,
    EnumCall,
    EnumValue,
    DirectCall,
    ClosureCall,
    IndexAccess,
    FieldAccess,
    MemberCall,
    FunctionAddress,
    DynamicStorageOperation,
    DynTraitFromParts,
    TextInterpolation,
    TextBuild,
    TypeLayout,
    StructuralFact
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
    IReadOnlyList<StarkTypeSymbol>? TypeArguments = null,
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments = null,
    ImportedTemplateTypedBodyExpressionSummary? TargetExpression = null,
    IReadOnlyList<string>? OperatorNames = null)
{
    public IReadOnlyList<ImportedTemplateTypedBodyExpressionSummary> Args =>
        Arguments ?? [];

    public IReadOnlyList<string> Members =>
        MemberNames ?? [];

    public IReadOnlyList<string> Operators =>
        OperatorNames ?? [];

    public IReadOnlyList<StarkTypeSymbol> TypeArgs =>
        TypeArguments ?? [];

    public IReadOnlyList<ComptimeValueArgumentSymbol> ComptimeValueArgs =>
        ComptimeValueArguments ?? [];
}

public sealed record ImportedTemplateTypedSwitchFieldPatternSummary(
    ImportedTemplateTypedSwitchFieldPatternKind Kind,
    string? Name = null,
    int? Ordinal = null,
    ImportedTemplateTypedBodyExpressionSummary? Expression = null,
    ImportedTemplateTypedBodyExpressionSummary? EndExpression = null,
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
    ImportedTemplateTypedBodyExpressionSummary? EndExpression = null,
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
    int? StorageCapacity = null,
    string? LoopBehavior = null,
    IReadOnlyList<ImportedTemplateTypedSwitchCaseSummary>? SwitchCaseSummaries = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? InitializerStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? IteratorStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? BodyStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? ThenStatements = null,
    IReadOnlyList<ImportedTemplateTypedBodyStatementSummary>? ElseStatements = null,
    ImportedTemplateTypedSwitchFieldPatternSummary? ConditionPattern = null,
    ImportedTemplateTypedBodyExpressionSummary? TargetExpression = null,
    IReadOnlyList<string>? LoopContracts = null,
    ConstProvenanceKind ConstProvenance = ConstProvenanceKind.None,
    ImportedTemplateTypedBodyExpressionSummary? TraversalSourceExpression = null,
    string? TraversalIndexName = null,
    string? TraversalIndexStorageClass = null,
    StarkTypeSymbol? TraversalIndexType = null,
    string? TraversalElementName = null,
    StarkTypeSymbol? TraversalElementType = null)
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
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments = null);

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
    ObjectCreationStorageSelector StorageSelector = ObjectCreationStorageSelector.Default,
    IReadOnlyList<ImportedTemplateObjectInitializerMemberSummary>? InitializerMemberSummaries = null,
    string? ExpressionText = null)
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
    StarkTypeSymbol Type,
    IReadOnlyList<ImportedTemplateAggregatePatternMemberSummary>? MemberSummaries = null)
{
    public IReadOnlyList<ImportedTemplateAggregatePatternMemberSummary> Members =>
        MemberSummaries ?? [];
}

public sealed record ImportedTemplateAggregatePatternMemberSummary(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

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

public sealed record ImportedTemplateFunctionAddressSummary(
    int Ordinal,
    TypedFunctionSignature Signature,
    StarkTypeSymbol TargetType);

public sealed record ImportedTemplateConversionSummary(
    int Ordinal,
    StarkTypeSymbol TargetType);

/// <summary>
/// Published `try` propagation facts for one `try` expression inside an exported generic
/// template body. Downstream packages never re-type-check imported template bodies, so the
/// roles/funnel resolution computed by the producing package is republished here
/// (ordinal-keyed in body order) for the consumer's MIR lowering to consume directly.
/// Types may reference the template's generic parameters; the consumer substitutes them
/// per specialization.
/// </summary>
public sealed record ImportedTemplateTryPropagationSummary(
    int Ordinal,
    StarkTypeSymbol OperandType,
    string OperandOkVariantName,
    string OperandErrVariantName,
    StarkTypeSymbol? SuccessPayloadType,
    StarkTypeSymbol? OperandFailurePayloadType,
    StarkTypeSymbol ReturnType,
    string EnclosingErrVariantName,
    StarkTypeSymbol? EnclosingFailurePayloadType,
    string? ConversionFunnelVariant);

public sealed record ImportedTemplateBoundOperationSummary(
    int? Ordinal,
    BoundOperation Operation);

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
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    StarkPackageTargetManifest? Target = null,
    StarkPackageBuildProfileManifest? BuildProfile = null)
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
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    StarkFfiAbi? FfiAbi = null,
    // Whether the function accesses memory beyond its own arguments (e.g. globals,
    // or the object behind a `dyn` trait object's data pointer). Optimizers that
    // compute a precise per-call local read/write set must treat these as a barrier.
    bool ReadsOtherMemory = false,
    bool WritesOtherMemory = false,
    bool NoRecurse = false,
    bool IsTailCallable = false);

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

public enum StarkClosureStorageKind
{
    Unspecified,
    Inline,
    Heap
}

public enum StarkClosureCallCapability
{
    None,
    Mut,
    Once
}

public enum StarkDynTraitStorageKind
{
    // Non-owning fat view (data borrow + shared vtable). No allocation. The
    // borrow/mut-borrow distinction comes from the outer type qualifier.
    View,
    // Owned, heap-boxed trait object. The only allocating form; dropped via the
    // vtable Drop slot at scope exit.
    Heap
}

public enum StarkTypeKind
{
    Error,
    Void,
    Bool,
    Ascii,
    Unicode,
    CVoid,
    Integer,
    Float,
    RawPointer,
    LlvmVector,
    LlvmStruct,
    FixedArray,
    Slice,
    Dynamic,
    FunctionPointer,
    Closure,
    Named,
    Null,
    DynTrait,
    AssociatedType
}

public sealed record StarkTypeSymbol(
    StarkTypeKind Kind,
    string DisplayName,
    int? BitWidth = null,
    string? NamedType = null,
    StarkTypeSymbol? ElementType = null,
    int? FixedLength = null,
    string? FixedLengthParameterName = null,
    StarkFunctionKind? FunctionPointerKind = null,
    bool FunctionPointerIsTailCallable = false,
    StarkFfiAbi? FunctionPointerAbi = null,
    bool FunctionPointerIsUnsafe = false,
    StarkTypeSymbol? FunctionPointerReturnType = null,
    IReadOnlyList<StarkTypeSymbol>? FunctionPointerParameterTypes = null,
    IReadOnlyList<string?>? FunctionPointerParameterRawPointerElementCountExpressions = null,
    IReadOnlyList<ParameterDisjointGroup>? FunctionPointerDisjointParameterGroups = null,
    IReadOnlyList<ParameterOverlapGroup>? FunctionPointerOverlapParameterGroups = null,
    IReadOnlyList<ParameterSameGroup>? FunctionPointerSameParameterGroups = null,
    IReadOnlyList<string>? FunctionPointerPointeeDeadOnReturnParameterNames = null,
    StarkClosureStorageKind ClosureStorageKind = StarkClosureStorageKind.Unspecified,
    StarkClosureCallCapability ClosureCallCapability = StarkClosureCallCapability.None,
    StarkFunctionKind? ClosureFunctionKind = null,
    bool ClosureIsTailCallable = false,
    StarkTypeSymbol? ClosureReturnType = null,
    IReadOnlyList<StarkTypeSymbol>? ClosureParameterTypes = null,
    IReadOnlyList<string?>? ClosureParameterRawPointerElementCountExpressions = null,
    IReadOnlyList<ParameterDisjointGroup>? ClosureDisjointParameterGroups = null,
    IReadOnlyList<ParameterOverlapGroup>? ClosureOverlapParameterGroups = null,
    IReadOnlyList<ParameterSameGroup>? ClosureSameParameterGroups = null,
    IReadOnlyList<string>? ClosurePointeeDeadOnReturnParameterNames = null,
    BigInteger? RangeMin = null,
    BigInteger? RangeMax = null,
    bool IsUnsigned = false,
    bool IsMutablePointer = false,
    StarkBorrowKind BorrowKind = StarkBorrowKind.None,
    StarkAccessKind AccessKind = StarkAccessKind.None,
    StarkInitializationKind InitializationKind = StarkInitializationKind.None,
    bool IsMutableView = false,
    IReadOnlyList<StarkTypeSymbol>? TypeArguments = null,
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments = null,
    string? DynTraitName = null,
    StarkDynTraitStorageKind DynTraitStorageKind = StarkDynTraitStorageKind.View,
    StarkTypeSymbol? AssociatedTypeOwner = null,
    string? AssociatedTypeName = null,
    string? CSourceAliasName = null);

public static class StarkTypeSymbols
{
    public const string OwnedAsciiName = "Ascii";
    public const string OwnedUnicodeName = "Unicode";
    public const string DynTraitVtableMemberName = "Vtable";

    public static readonly StarkTypeSymbol Error = new(StarkTypeKind.Error, "<error>");
    public static readonly StarkTypeSymbol Void = new(StarkTypeKind.Void, "void");
    public static readonly StarkTypeSymbol Bool = new(StarkTypeKind.Bool, "bool");
    public static readonly StarkTypeSymbol CompileTimeInteger = new(StarkTypeKind.Integer, "integer");
    public static readonly StarkTypeSymbol Ascii = new(StarkTypeKind.Ascii, "ascii");
    public static readonly StarkTypeSymbol Unicode = new(StarkTypeKind.Unicode, "unicode");
    public static readonly StarkTypeSymbol CVoid = new(
        StarkTypeKind.CVoid,
        "System.C.c_void",
        CSourceAliasName: "System.C.c_void");
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

    public static StarkTypeSymbol WithCSourceAlias(StarkTypeSymbol type, string? aliasName)
    {
        if (type.Kind == StarkTypeKind.Error || string.IsNullOrWhiteSpace(aliasName))
        {
            return type;
        }

        return type with { CSourceAliasName = aliasName };
    }

    public static bool IsCompileTimeInteger(StarkTypeSymbol type) =>
        type.Kind == StarkTypeKind.Integer && type.BitWidth is null;

    public static bool TryGetIntegerStorageBounds(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        if (type.Kind != StarkTypeKind.Integer || type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            min = BigInteger.Zero;
            max = BigInteger.Zero;
            return false;
        }

        if (type.IsUnsigned)
        {
            min = BigInteger.Zero;
            max = (BigInteger.One << bitWidth) - BigInteger.One;
            return true;
        }

        min = -(BigInteger.One << (bitWidth - 1));
        max = (BigInteger.One << (bitWidth - 1)) - BigInteger.One;
        return true;
    }

    public static bool TryGetEffectiveIntegerBounds(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        if (!TryGetIntegerStorageBounds(type, out min, out max))
        {
            return false;
        }

        if (type.RangeMin is { } rangeMin && type.RangeMax is { } rangeMax)
        {
            if (rangeMin > rangeMax || rangeMin < min || rangeMax > max)
            {
                min = BigInteger.Zero;
                max = BigInteger.Zero;
                return false;
            }

            min = rangeMin;
            max = rangeMax;
            return true;
        }

        return true;
    }

    public static bool IntegerValueFitsEffectiveRange(BigInteger value, StarkTypeSymbol type)
    {
        return TryGetEffectiveIntegerBounds(type, out var min, out var max)
               && value >= min
               && value <= max;
    }

    public static bool IntegerValueFitsStorage(BigInteger value, StarkTypeSymbol type)
    {
        return TryGetIntegerStorageBounds(type, out var min, out var max)
               && value >= min
               && value <= max;
    }

    public static StarkTypeSymbol Float(int bitWidth) => new(StarkTypeKind.Float, $"f{bitWidth}", BitWidth: bitWidth);

    public static StarkTypeSymbol RawPointer(StarkTypeSymbol elementType, bool isMutable) =>
        new(
            StarkTypeKind.RawPointer,
            $"{(isMutable ? "rawmutptr" : "rawptr")}<{elementType.DisplayName}>",
            ElementType: elementType,
            IsMutablePointer: isMutable);

    public static StarkTypeSymbol LlvmVector(StarkTypeSymbol elementType, int elementCount) =>
        new(
            StarkTypeKind.LlvmVector,
            $"llvmvector<{elementCount} x {elementType.DisplayName}>",
            ElementType: elementType,
            FixedLength: elementCount);

    public static StarkTypeSymbol LlvmStruct(IReadOnlyList<StarkTypeSymbol> fieldTypes) =>
        new(
            StarkTypeKind.LlvmStruct,
            $"llvmstruct<{string.Join(", ", fieldTypes.Select(static field => field.DisplayName))}>",
            TypeArguments: fieldTypes.ToArray());

    public static StarkTypeSymbol FixedArray(StarkTypeSymbol elementType, int? fixedLength, string? fixedLengthParameterName = null) =>
        new(
            StarkTypeKind.FixedArray,
            fixedLength is null
                ? !string.IsNullOrWhiteSpace(fixedLengthParameterName)
                    ? $"{elementType.DisplayName}[{fixedLengthParameterName}]"
                    : $"{elementType.DisplayName}[?]"
                : $"{elementType.DisplayName}[{fixedLength}]",
            ElementType: elementType,
            FixedLength: fixedLength,
            FixedLengthParameterName: fixedLengthParameterName);

    public static StarkTypeSymbol Slice(StarkTypeSymbol elementType) =>
        new(StarkTypeKind.Slice, $"{elementType.DisplayName}[]", ElementType: elementType);

    public static StarkTypeSymbol Dynamic(StarkTypeSymbol elementType) =>
        new(StarkTypeKind.Dynamic, $"dynamic {elementType.DisplayName}", ElementType: elementType);

    public static StarkTypeSymbol FunctionPointer(
        StarkFunctionKind functionKind,
        StarkTypeSymbol returnType,
        IReadOnlyList<StarkTypeSymbol> parameterTypes,
        IReadOnlyList<ParameterDisjointGroup>? disjointGroups = null,
        IReadOnlyList<ParameterOverlapGroup>? overlapGroups = null,
        IReadOnlyList<ParameterSameGroup>? sameGroups = null,
        IReadOnlyList<string?>? parameterRawPointerElementCountExpressions = null,
        StarkFfiAbi? ffiAbi = null,
        bool isUnsafe = false,
        bool isTailCallable = false,
        IReadOnlyList<string>? pointeeDeadOnReturnParameterNames = null)
    {
        var displayKind = FormatCallableFunctionKind(functionKind);
        var abiPrefix = ffiAbi is { } abi
            ? $"ffi({StarkFfiAbiFacts.DisplayName(abi)}) "
            : string.Empty;
        var unsafePrefix = isUnsafe ? "unsafe " : string.Empty;
        var tailPrefix = isTailCallable ? "tail " : string.Empty;
        var effectiveRawPointerElementCountExpressions =
            NormalizeFunctionPointerParameterRawPointerElementCountExpressions(
                parameterTypes,
                parameterRawPointerElementCountExpressions);
        var parameters = parameterTypes
            .Select((parameter, index) => new TypedParameterSymbol(
                $"arg{index}",
                parameter,
                RawPointerElementCountExpression: GetFunctionPointerParameterRawPointerElementCountExpression(
                    effectiveRawPointerElementCountExpressions,
                    index)))
            .ToArray();
        var effectiveOverlapGroups = overlapGroups ?? [];
        var effectiveSameGroups = sameGroups ?? [];
        var effectivePointeeDeadOnReturnParameters = NormalizePointeeDeadOnReturnParameters(
            pointeeDeadOnReturnParameterNames,
            parameterTypes.Count);
        var effectiveDisjointGroups = disjointGroups
            ?? ParameterMemoryContractFacts.BuildEffectiveDisjointGroups(
                parameters,
                explicitDisjointGroups: [],
                overlapGroups: effectiveOverlapGroups,
                sameGroups: effectiveSameGroups,
                applyDefaultNonOverlap: true);
        var displayName = $"fnptr<{unsafePrefix}{abiPrefix}{tailPrefix}{displayKind} {returnType.DisplayName}({string.Join(", ", parameterTypes.Select((parameter, index) => FormatFunctionPointerParameterDisplayName(parameter, effectiveRawPointerElementCountExpressions, index)))}){FormatFunctionPointerMemoryContracts(effectiveOverlapGroups, effectiveSameGroups, effectivePointeeDeadOnReturnParameters)}>";
        return new StarkTypeSymbol(
            StarkTypeKind.FunctionPointer,
            displayName,
            FunctionPointerKind: functionKind,
            FunctionPointerIsTailCallable: isTailCallable,
            FunctionPointerAbi: ffiAbi,
            FunctionPointerIsUnsafe: isUnsafe,
            FunctionPointerReturnType: returnType,
            FunctionPointerParameterTypes: parameterTypes.ToArray(),
            FunctionPointerParameterRawPointerElementCountExpressions: effectiveRawPointerElementCountExpressions,
            FunctionPointerDisjointParameterGroups: effectiveDisjointGroups,
            FunctionPointerOverlapParameterGroups: effectiveOverlapGroups,
            FunctionPointerSameParameterGroups: effectiveSameGroups,
            FunctionPointerPointeeDeadOnReturnParameterNames: effectivePointeeDeadOnReturnParameters);
    }

    public static StarkTypeSymbol Closure(
        StarkClosureStorageKind storageKind,
        StarkClosureCallCapability callCapability,
        StarkFunctionKind functionKind,
        StarkTypeSymbol returnType,
        IReadOnlyList<StarkTypeSymbol> parameterTypes,
        IReadOnlyList<ParameterDisjointGroup>? disjointGroups = null,
        IReadOnlyList<ParameterOverlapGroup>? overlapGroups = null,
        IReadOnlyList<ParameterSameGroup>? sameGroups = null,
        IReadOnlyList<string?>? parameterRawPointerElementCountExpressions = null,
        bool isTailCallable = false,
        IReadOnlyList<string>? pointeeDeadOnReturnParameterNames = null)
    {
        var displayKind = FormatCallableFunctionKind(functionKind);
        var storagePrefix = storageKind switch
        {
            StarkClosureStorageKind.Inline => "inline ",
            StarkClosureStorageKind.Heap => "heap ",
            _ => string.Empty
        };
        var capabilityPrefix = callCapability switch
        {
            StarkClosureCallCapability.Mut => "mut ",
            StarkClosureCallCapability.Once => "once ",
            _ => string.Empty
        };
        var tailPrefix = isTailCallable ? "tail " : string.Empty;
        var effectiveRawPointerElementCountExpressions =
            NormalizeFunctionPointerParameterRawPointerElementCountExpressions(
                parameterTypes,
                parameterRawPointerElementCountExpressions);
        var parameters = parameterTypes
            .Select((parameter, index) => new TypedParameterSymbol(
                $"arg{index}",
                parameter,
                RawPointerElementCountExpression: GetFunctionPointerParameterRawPointerElementCountExpression(
                    effectiveRawPointerElementCountExpressions,
                    index)))
            .ToArray();
        var effectiveOverlapGroups = overlapGroups ?? [];
        var effectiveSameGroups = sameGroups ?? [];
        var effectivePointeeDeadOnReturnParameters = NormalizePointeeDeadOnReturnParameters(
            pointeeDeadOnReturnParameterNames,
            parameterTypes.Count);
        var effectiveDisjointGroups = disjointGroups
            ?? ParameterMemoryContractFacts.BuildEffectiveDisjointGroups(
                parameters,
                explicitDisjointGroups: [],
                overlapGroups: effectiveOverlapGroups,
                sameGroups: effectiveSameGroups,
                applyDefaultNonOverlap: true);
        var displayName = $"{storagePrefix}closure<{capabilityPrefix}{tailPrefix}{displayKind} {returnType.DisplayName}({string.Join(", ", parameterTypes.Select((parameter, index) => FormatFunctionPointerParameterDisplayName(parameter, effectiveRawPointerElementCountExpressions, index)))}){FormatFunctionPointerMemoryContracts(effectiveOverlapGroups, effectiveSameGroups, effectivePointeeDeadOnReturnParameters)}>";
        return new StarkTypeSymbol(
            StarkTypeKind.Closure,
            displayName,
            ClosureStorageKind: storageKind,
            ClosureCallCapability: callCapability,
            ClosureFunctionKind: functionKind,
            ClosureIsTailCallable: isTailCallable,
            ClosureReturnType: returnType,
            ClosureParameterTypes: parameterTypes.ToArray(),
            ClosureParameterRawPointerElementCountExpressions: effectiveRawPointerElementCountExpressions,
            ClosureDisjointParameterGroups: effectiveDisjointGroups,
            ClosureOverlapParameterGroups: effectiveOverlapGroups,
            ClosureSameParameterGroups: effectiveSameGroups,
            ClosurePointeeDeadOnReturnParameterNames: effectivePointeeDeadOnReturnParameters);
    }

    // A `dyn Trait` trait object: a two-word fat pointer { data_ptr, vtable_ptr }.
    // `heap dyn Trait` owns its data (boxed, dropped via the vtable); a plain
    // `dyn Trait` is a borrowed view whose ownership comes from the outer
    // borrow/mut-borrow qualifier. The vtable is a real, nameable representation.
    public static StarkTypeSymbol DynTrait(
        string traitName,
        StarkDynTraitStorageKind storageKind = StarkDynTraitStorageKind.View,
        IReadOnlyList<StarkTypeSymbol>? typeArguments = null)
    {
        var storagePrefix = storageKind == StarkDynTraitStorageKind.Heap ? "heap " : string.Empty;
        var typeArgumentSuffix = typeArguments is { Count: > 0 }
            ? $"<{string.Join(", ", typeArguments.Select(argument => argument.DisplayName))}>"
            : string.Empty;
        return new StarkTypeSymbol(
            StarkTypeKind.DynTrait,
            $"{storagePrefix}dyn {traitName}{typeArgumentSuffix}",
            DynTraitName: traitName,
            DynTraitStorageKind: storageKind,
            TypeArguments: typeArguments);
    }

    public static StarkTypeSymbol DynTraitVtable(
        string traitName,
        IReadOnlyList<StarkTypeSymbol>? typeArguments = null,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArguments = null)
    {
        var templateName = $"{traitName}.{DynTraitVtableMemberName}";
        return typeArguments is { Count: > 0 } || valueArguments is { Count: > 0 }
            ? GenericInstantiation(templateName, typeArguments, valueArguments)
            : Named(templateName);
    }

    public static StarkTypeSymbol DynTraitVtableForTraitObject(StarkTypeSymbol dynTraitType)
    {
        return dynTraitType.DynTraitName is { } traitName
            ? DynTraitVtable(traitName, dynTraitType.TypeArguments, dynTraitType.ComptimeValueArguments)
            : Named($"<error>.{DynTraitVtableMemberName}");
    }

    public static StarkTypeSymbol DynTraitVtablePointerForTraitObject(StarkTypeSymbol dynTraitType) =>
        RawPointer(DynTraitVtableForTraitObject(dynTraitType), isMutable: false);

    public static bool IsDynTraitVtableType(StarkTypeSymbol type)
    {
        var coreType = WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        if (coreType.Kind != StarkTypeKind.Named || coreType.NamedType is not { } namedType)
        {
            return false;
        }

        var baseName = GetGenericBaseName(namedType);
        return baseName.EndsWith($".{DynTraitVtableMemberName}", StringComparison.Ordinal);
    }

    private static string FormatCallableFunctionKind(StarkFunctionKind functionKind)
    {
        return functionKind switch
        {
            StarkFunctionKind.FiniteLaw => "finite law",
            StarkFunctionKind.Finite => "finite",
            StarkFunctionKind.Law => "law",
            _ => "fn"
        };
    }

    public static string? GetFunctionPointerParameterRawPointerElementCountExpression(
        StarkTypeSymbol functionPointerType,
        int parameterIndex)
    {
        return GetFunctionPointerParameterRawPointerElementCountExpression(
            functionPointerType.FunctionPointerParameterRawPointerElementCountExpressions,
            parameterIndex);
    }

    public static string? GetClosureParameterRawPointerElementCountExpression(
        StarkTypeSymbol closureType,
        int parameterIndex)
    {
        return GetFunctionPointerParameterRawPointerElementCountExpression(
            closureType.ClosureParameterRawPointerElementCountExpressions,
            parameterIndex);
    }

    private static IReadOnlyList<string?>? NormalizeFunctionPointerParameterRawPointerElementCountExpressions(
        IReadOnlyList<StarkTypeSymbol> parameterTypes,
        IReadOnlyList<string?>? parameterRawPointerElementCountExpressions)
    {
        if (parameterRawPointerElementCountExpressions is null
            || parameterRawPointerElementCountExpressions.All(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        var normalized = new string?[parameterTypes.Count];
        for (var index = 0; index < normalized.Length; index++)
        {
            normalized[index] = index < parameterRawPointerElementCountExpressions.Count
                && parameterTypes[index].Kind == StarkTypeKind.RawPointer
                && !string.IsNullOrWhiteSpace(parameterRawPointerElementCountExpressions[index])
                    ? parameterRawPointerElementCountExpressions[index]
                    : null;
        }

        return normalized.Any(static expression => !string.IsNullOrWhiteSpace(expression))
            ? normalized
            : null;
    }

    private static string? GetFunctionPointerParameterRawPointerElementCountExpression(
        IReadOnlyList<string?>? parameterRawPointerElementCountExpressions,
        int parameterIndex)
    {
        return parameterRawPointerElementCountExpressions is not null
               && parameterIndex >= 0
               && parameterIndex < parameterRawPointerElementCountExpressions.Count
               && !string.IsNullOrWhiteSpace(parameterRawPointerElementCountExpressions[parameterIndex])
            ? parameterRawPointerElementCountExpressions[parameterIndex]
            : null;
    }

    private static string FormatFunctionPointerParameterDisplayName(
        StarkTypeSymbol parameterType,
        IReadOnlyList<string?>? parameterRawPointerElementCountExpressions,
        int parameterIndex)
    {
        var countExpression = GetFunctionPointerParameterRawPointerElementCountExpression(
            parameterRawPointerElementCountExpressions,
            parameterIndex);
        return countExpression is null
            ? parameterType.DisplayName
            : $"{parameterType.DisplayName}[{countExpression}]";
    }

    private static string FormatFunctionPointerMemoryContracts(
        IReadOnlyList<ParameterOverlapGroup> overlapGroups,
        IReadOnlyList<ParameterSameGroup> sameGroups,
        IReadOnlyList<string> pointeeDeadOnReturnParameters)
    {
        var clauses = overlapGroups
            .Select(static group => $"overlap({string.Join(", ", group.ParameterNames)})")
            .Concat(sameGroups.Select(static group => $"same({string.Join(", ", group.ParameterNames)})"))
            .Concat(pointeeDeadOnReturnParameters.Select(static name => $"dead_on_return({name})"))
            .ToArray();
        return clauses.Length == 0
            ? string.Empty
            : $" where {string.Join(", ", clauses)}";
    }

    private static IReadOnlyList<string> NormalizePointeeDeadOnReturnParameters(
        IReadOnlyList<string>? parameterNames,
        int parameterCount)
    {
        if (parameterNames is not { Count: > 0 })
        {
            return [];
        }

        return parameterNames
            .Where(name => TryParseSyntheticArgumentIndex(name, out var index)
                && index >= 0
                && index < parameterCount)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryParseSyntheticArgumentIndex(string name, out int index)
    {
        index = -1;
        return name.StartsWith("arg", StringComparison.Ordinal)
            && int.TryParse(name[3..], out index);
    }

    public static StarkTypeSymbol Named(string name) => new(StarkTypeKind.Named, name, NamedType: name);

    public static StarkTypeSymbol AssociatedType(StarkTypeSymbol ownerType, string associatedTypeName)
    {
        var ownerCore = WithQualifiers(
            ownerType,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        return new StarkTypeSymbol(
            StarkTypeKind.AssociatedType,
            $"{ownerCore.DisplayName}.{associatedTypeName}",
            AssociatedTypeOwner: ownerCore,
            AssociatedTypeName: associatedTypeName);
    }

    public static StarkTypeSymbol GenericInstantiation(
        string templateName,
        IReadOnlyList<StarkTypeSymbol>? typeArgs,
        IReadOnlyList<ComptimeValueArgumentSymbol>? valueArgs = null)
    {
        typeArgs ??= [];
        valueArgs ??= [];
        var displayParts = typeArgs
            .Select(static t => t.DisplayName)
            .Concat(valueArgs.Select(static value => value.IsSymbolic ? value.SourceName : value.IntegerValue.ToString()))
            .ToArray();
        var keyParts = typeArgs
            .Select(static t => t.NamedType ?? t.DisplayName)
            .Concat(valueArgs.Select(static value => value.IsSymbolic ? value.DisplayName : $"{value.ParameterName}={value.IntegerValue}"))
            .ToArray();
        var displayName = $"{templateName}<{string.Join(", ", displayParts)}>";
        var key = $"{templateName}<{string.Join(",", keyParts)}>";
        return new StarkTypeSymbol(
            StarkTypeKind.Named,
            displayName,
            NamedType: key,
            TypeArguments: typeArgs.Count == 0 ? null : typeArgs.ToArray(),
            ComptimeValueArguments: valueArgs.Count == 0 ? null : valueArgs.ToArray());
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
        => type.Kind == StarkTypeKind.Named
            && (type.TypeArguments is { Count: > 0 } || type.ComptimeValueArguments is { Count: > 0 });

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
        return IsPointerBackedBorrowType(type);
    }

    public static bool IsPointerBackedBorrowType(StarkTypeSymbol type)
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
            StarkTypeKind.CVoid or
            StarkTypeKind.Integer or
            StarkTypeKind.Float or
            StarkTypeKind.FunctionPointer or
            StarkTypeKind.Closure or
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
            StarkTypeKind.CVoid => WithCSourceAlias(CVoid, type.CSourceAliasName),
            StarkTypeKind.Null => Null,
            StarkTypeKind.Integer => WithCSourceAlias(
                Integer(type.BitWidth ?? 32, type.RangeMin, type.RangeMax, type.IsUnsigned),
                type.CSourceAliasName),
            StarkTypeKind.Float => Float(type.BitWidth ?? 32),
            StarkTypeKind.RawPointer when type.ElementType is not null => RawPointer(type.ElementType, type.IsMutablePointer),
            StarkTypeKind.FixedArray when type.ElementType is not null => FixedArray(type.ElementType, type.FixedLength, type.FixedLengthParameterName),
            StarkTypeKind.Slice when type.ElementType is not null => Slice(type.ElementType),
            StarkTypeKind.Dynamic when type.ElementType is not null => Dynamic(type.ElementType),
            StarkTypeKind.FunctionPointer when type.FunctionPointerKind is { } functionKind
                                               && type.FunctionPointerReturnType is { } returnType
                                               && type.FunctionPointerParameterTypes is { } parameterTypes
                => FunctionPointer(
                    functionKind,
                    returnType,
                    parameterTypes,
                    type.FunctionPointerDisjointParameterGroups,
                    type.FunctionPointerOverlapParameterGroups,
                    type.FunctionPointerSameParameterGroups,
                    type.FunctionPointerParameterRawPointerElementCountExpressions,
                    type.FunctionPointerAbi,
                    type.FunctionPointerIsUnsafe,
                    type.FunctionPointerIsTailCallable,
                    type.FunctionPointerPointeeDeadOnReturnParameterNames),
            StarkTypeKind.Closure when type.ClosureFunctionKind is { } closureFunctionKind
                                       && type.ClosureReturnType is { } closureReturnType
                                       && type.ClosureParameterTypes is { } closureParameterTypes
                => Closure(
                    type.ClosureStorageKind,
                    type.ClosureCallCapability,
                    closureFunctionKind,
                    closureReturnType,
                    closureParameterTypes,
                    type.ClosureDisjointParameterGroups,
                    type.ClosureOverlapParameterGroups,
                    type.ClosureSameParameterGroups,
                    type.ClosureParameterRawPointerElementCountExpressions,
                    type.ClosureIsTailCallable,
                    type.ClosurePointeeDeadOnReturnParameterNames),
            StarkTypeKind.Named when type.NamedType == OwnedAsciiName => OwnedAscii,
            StarkTypeKind.Named when type.NamedType == OwnedUnicodeName => OwnedUnicode,
            StarkTypeKind.Named when (type.TypeArguments is { Count: > 0 } || type.ComptimeValueArguments is { Count: > 0 })
                                     && type.NamedType is not null
                => GenericInstantiation(GetGenericBaseName(type.NamedType), type.TypeArguments, type.ComptimeValueArguments),
            StarkTypeKind.Named when type.NamedType is not null => Named(type.NamedType),
            StarkTypeKind.AssociatedType when type.AssociatedTypeOwner is not null
                                               && type.AssociatedTypeName is not null
                => AssociatedType(type.AssociatedTypeOwner, type.AssociatedTypeName),
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
    string? DeclaringModuleName = null,
    int? ExplicitOffsetBytes = null,
    IReadOnlyList<ThreadSafetyLawAttributeSymbol>? ThreadSafetyLawAttributes = null)
{
    public IReadOnlyList<ThreadSafetyLawAttributeSymbol> ThreadSafetyLaws => ThreadSafetyLawAttributes ?? [];
}

public sealed record ThreadSafetyLawPredicateSymbol(
    string LawName,
    StarkTypeSymbol Type);

public sealed record ThreadSafetyLawAttributeSymbol(
    ThreadSafetyLawAttributeKind Kind,
    string LawName,
    ThreadSafetyLawPredicateSymbol? Condition = null);

public enum ThreadSafetyLawFailureKind
{
    DeniedByTypeAttribute,
    DeniedByFieldAttribute,
    ConflictingAttributes,
    RawPointer,
    StoredBorrow,
    StructuralFieldFailure,
    UnknownType
}

public sealed record ThreadSafetyLawRequirement(
    string LawName,
    StarkTypeSymbol Type);

public sealed record ThreadSafetyLawFailure(
    ThreadSafetyLawFailureKind Kind,
    string Message,
    StarkTypeSymbol? Type = null,
    IReadOnlyList<string>? FieldPath = null)
{
    public IReadOnlyList<string> Path => FieldPath ?? [];
}

public sealed record ThreadSafetyLawFact(
    string LawName,
    StarkTypeSymbol Type,
    bool Holds,
    IReadOnlyList<ThreadSafetyLawRequirement>? Requirements = null,
    IReadOnlyList<ThreadSafetyLawFailure>? Failures = null)
{
    public IReadOnlyList<ThreadSafetyLawRequirement> RequiredPredicates => Requirements ?? [];
    public IReadOnlyList<ThreadSafetyLawFailure> FailureReasons => Failures ?? [];
    public bool IsConditional => Holds && RequiredPredicates.Count > 0;
}

public sealed record ThreadSafetyLawTypeFacts(
    StarkTypeSymbol Type,
    ThreadSafetyLawFact Transferable,
    ThreadSafetyLawFact Shareable)
{
    public bool TryGet(string lawName, out ThreadSafetyLawFact fact)
    {
        if (string.Equals(lawName, ThreadSafetyLawNames.Transferable, StringComparison.Ordinal))
        {
            fact = Transferable;
            return true;
        }

        if (string.Equals(lawName, ThreadSafetyLawNames.Shareable, StringComparison.Ordinal))
        {
            fact = Shareable;
            return true;
        }

        fact = null!;
        return false;
    }
}

public static class ThreadSafetyLawNames
{
    public const string Transferable = "Transferable";
    public const string Shareable = "Shareable";
    public const string Copyable = "Copyable";
}

public enum StructLayoutKind
{
    Auto,
    C,
    Explicit
}

public sealed record StructLayoutMetadata(
    StructLayoutKind Kind,
    int? PackBytes = null,
    int? AlignBytes = null);

public sealed record EnumVariantFieldSymbol(
    int Position,
    string? Name,
    StarkTypeSymbol Type);

/// <summary>
/// The propagation role a variant plays for <c>try</c>, declared with the innate
/// <c>[Ok]</c> / <c>[Err]</c> variant attributes (doc 11 v2). <see cref="None"/>
/// means the variant has no role. An enum is propagatable only when it has exactly
/// two variants, one <see cref="Ok"/> and one <see cref="Err"/>; the roles — never
/// type names and never stdlib identity — are what `try` consults.
/// </summary>
public enum EnumVariantRole
{
    None,
    Ok,
    Err,
}

public sealed record EnumVariantSymbol(
    string Name,
    bool UsesNamedFields,
    IReadOnlyList<EnumVariantFieldSymbol> Fields,
    StarkTypeSymbol? AbsorbsErrorType = null,
    EnumVariantRole Role = EnumVariantRole.None)
{
    public bool IsUnit => Fields.Count == 0;

    /// <summary>
    /// True when the variant was declared with the `from` payload form
    /// (e.g. <c>Io from IoError</c>), marking it as the canonical funnel that a
    /// <c>try</c> expression uses to wrap <see cref="AbsorbsErrorType"/> into this
    /// enum. Such a variant carries exactly one positional field whose type equals
    /// <see cref="AbsorbsErrorType"/>; it is otherwise an ordinary single-payload variant.
    /// </summary>
    public bool IsErrorFunnel => AbsorbsErrorType is not null;
}

public sealed record NamedTypeSymbol(
    string Name,
    DeclarationKind Kind,
    IReadOnlyDictionary<string, FieldSymbol> Fields,
    IReadOnlyList<FieldSymbol> OrderedFields,
    IReadOnlyList<EnumVariantSymbol>? EnumVariants = null,
    IReadOnlyList<string>? GenericParameterNames = null,
    IReadOnlyList<ComptimeGenericParameterSymbol>? ComptimeGenericParameterNames = null,
    IReadOnlyList<string>? ImplementedTraitNames = null,
    IReadOnlyList<StarkTypeSymbol>? ImplementedTraitTypeSymbols = null,
    IReadOnlyDictionary<string, AssociatedTypeSymbol>? AssociatedTypeMembers = null,
    bool IsDynTrait = false,
    StructLayoutMetadata? Layout = null,
    IReadOnlyList<ThreadSafetyLawAttributeSymbol>? ThreadSafetyLawAttributes = null,
    string? DeclaringModuleName = null,
    StarkVisibility Visibility = StarkVisibility.Module,
    bool HasDestructor = false)
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
    public IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeGenericParams => ComptimeGenericParameterNames ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 } || ComptimeGenericParameterNames is { Count: > 0 };

    public IReadOnlyList<string> ImplementedTraits => ImplementedTraitNames ?? [];
    public IReadOnlyList<StarkTypeSymbol> ImplementedTraitTypes => ImplementedTraitTypeSymbols ?? [];

    public IReadOnlyDictionary<string, AssociatedTypeSymbol> AssociatedTypes =>
        AssociatedTypeMembers ?? EmptyAssociatedTypes;

    private static IReadOnlyDictionary<string, AssociatedTypeSymbol> EmptyAssociatedTypes { get; } =
        new Dictionary<string, AssociatedTypeSymbol>(StringComparer.Ordinal);

    public IReadOnlyList<EnumVariantSymbol> Variants => EnumVariants ?? [];
    public IReadOnlyList<ThreadSafetyLawAttributeSymbol> ThreadSafetyLaws => ThreadSafetyLawAttributes ?? [];

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

// A `where T: Trait` bound on a generic function: the type parameter and the
// traits it must implement. Used to enforce constraints at instantiation sites
// and to resolve trait-method calls on the parameter inside the body.
public sealed record TypeParameterConstraint(
    string ParameterName,
    IReadOnlyList<StarkTypeSymbol> BoundTraits);

public sealed record TypedFunctionSignature(
    string Name,
    StarkTypeSymbol ReturnType,
    IReadOnlyList<TypedParameterSymbol> Parameters,
    string? SourceName = null,
    IReadOnlyList<string>? GenericParameterNames = null,
    IReadOnlyList<ComptimeGenericParameterSymbol>? ComptimeGenericParameterNames = null,
    string? TemplateName = null,
    IReadOnlyList<StarkTypeSymbol>? TypeArguments = null,
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments = null,
    bool IsStatic = false,
    StarkFunctionKind Kind = StarkFunctionKind.Fn,
    bool IsUnsafe = false,
    bool IsVarargs = false,
    StarkFfiAbi? FfiAbi = null,
    ModuleBackendOptimizationMode BackendOptimizationMode = ModuleBackendOptimizationMode.Default,
    IReadOnlyList<ParameterDisjointGroup>? DisjointParameterGroups = null,
    IReadOnlyList<ParameterOverlapGroup>? OverlapParameterGroups = null,
    IReadOnlyList<ParameterSameGroup>? SameParameterGroups = null,
    IReadOnlyList<string>? PointeeDeadOnReturnParameterNames = null,
    IReadOnlyList<TypeParameterConstraint>? TypeParameterConstraints = null,
    bool HasBody = true,
    IReadOnlyList<ThreadSafetyLawPredicateSymbol>? ThreadSafetyLawPredicates = null,
    StarkVisibility Visibility = StarkVisibility.Module,
    IReadOnlyList<ParameterValueContract>? ValueParameterContracts = null,
    bool IsTailCallable = false,
    SourceLocation? DeclarationLocation = null,
    string? ExternalLinkName = null)
{
    public string DisplaySourceName => SourceName ?? Name;
    public IReadOnlyList<string> GenericParams => GenericParameterNames ?? [];
    public IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeGenericParams => ComptimeGenericParameterNames ?? [];
    public IReadOnlyList<ComptimeValueArgumentSymbol> ComptimeValues => ComptimeValueArguments ?? [];
    public IReadOnlyList<TypeParameterConstraint> Constraints => TypeParameterConstraints ?? [];
    public bool IsGeneric => GenericParameterNames is { Count: > 0 } || ComptimeGenericParameterNames is { Count: > 0 };
    public bool IsGenericInstantiation => TemplateName is not null
        && (TypeArguments is { Count: > 0 } || ComptimeValueArguments is { Count: > 0 });
    public IReadOnlyList<ParameterDisjointGroup> DisjointGroups => DisjointParameterGroups ?? [];
    public IReadOnlyList<ParameterOverlapGroup> OverlapGroups => OverlapParameterGroups ?? [];
    public IReadOnlyList<ParameterSameGroup> SameGroups => SameParameterGroups ?? [];
    public IReadOnlyList<string> PointeeDeadOnReturnParameters => PointeeDeadOnReturnParameterNames ?? [];
    public IReadOnlyList<ThreadSafetyLawPredicateSymbol> ThreadSafetyLaws => ThreadSafetyLawPredicates ?? [];
    public IReadOnlyList<ParameterValueContract> ValueContracts => ValueParameterContracts ?? [];
}

public enum GlobalBindingKind
{
    Const,
    Immutable,
    Mutable
}

public enum TypedConstantInitializerKind
{
    Integer,
    Float,
    Bool,
    Text,
    Null,
    FixedArray,
    NamedAggregate,
    EnumAggregate
}

public sealed record TypedConstantInitializer(
    TypedConstantInitializerKind Kind,
    StarkTypeSymbol Type,
    BigInteger? IntegerValue = null,
    string? FloatLiteralText = null,
    bool? BoolValue = null,
    string? TextLiteralText = null,
    string? VariantName = null,
    IReadOnlyList<TypedConstantInitializer>? Elements = null)
{
    public IReadOnlyList<TypedConstantInitializer> ElementValues => Elements ?? [];
}

public sealed record TypedGlobalSymbol(
    string Name,
    StarkTypeSymbol Type,
    GlobalBindingKind BindingKind,
    TypedConstantInitializer? ConstantInitializer = null)
{
    public bool IsMutable => BindingKind == GlobalBindingKind.Mutable;

    public bool IsConst => BindingKind == GlobalBindingKind.Const;

    public ConstProvenanceKind ConstProvenance => BindingKind switch
    {
        GlobalBindingKind.Const => ConstProvenanceKind.PermanentConst,
        GlobalBindingKind.Immutable => ConstProvenanceKind.StaticImmutableBinding,
        _ => ConstProvenanceKind.None
    };
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
    string? EnclosingFunctionName = null,
    IReadOnlyDictionary<string, ConstProvenanceKind>? DeclaratorConstProvenance = null)
{
    public IReadOnlyDictionary<string, ConstProvenanceKind> ConstProvenanceByDeclarator =>
        DeclaratorConstProvenance ?? EmptyConstProvenanceByDeclarator;

    private static readonly IReadOnlyDictionary<string, ConstProvenanceKind> EmptyConstProvenanceByDeclarator =
        new Dictionary<string, ConstProvenanceKind>(StringComparer.Ordinal);
}

public sealed record LocalStorageCapacityTypingRecord(
    string Kind,
    string Name,
    int Capacity,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record CallArgumentTypingRecord(
    int ParameterIndex,
    int SourceArgumentIndex,
    StarkTypeSymbol ParameterType,
    StarkTypeSymbol ArgumentType,
    bool IsReceiver,
    bool RequiresAddressable,
    bool RequiresMutable,
    bool RequiresConstProvenance,
    bool ArgumentIsAddressable,
    bool ArgumentIsMutable,
    bool ArgumentHasConstProvenance);

public sealed record DirectCallTypingRecord(
    TypedFunctionSignature Signature,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords = null)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

public sealed record FunctionPointerPromotionTypingRecord(
    TypedFunctionSignature Signature,
    StarkTypeSymbol TargetType,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record ClosureFunctionPromotionTypingRecord(
    TypedFunctionSignature Signature,
    StarkTypeSymbol ClosureType,
    string AdapterFunctionName,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record AddressTakenFunctionTypingRecord(
    TypedFunctionSignature Signature,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record IndirectCallTypingRecord(
    StarkTypeSymbol FunctionPointerType,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords = null)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

public sealed record ClosureCallTypingRecord(
    StarkTypeSymbol ClosureType,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords = null)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

public sealed record IndexAccessTypingRecord(
    string Kind,
    StarkTypeSymbol SourceType,
    StarkTypeSymbol ResultType,
    int IndexCount,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record DynamicStorageOperationTypingRecord(
    string OperationName,
    StarkTypeSymbol ReceiverType,
    StarkTypeSymbol ResultType,
    int ArgumentCount,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    bool ReceiverIsAddressable = true,
    bool ReceiverIsMutable = true);

public enum BoundOperationKind
{
    DirectCall,
    MemberCall,
    FunctionPointerCall,
    ClosureCall,
    IndexAccess,
    SliceAccess,
    ObjectCreation,
    EnumConstruction,
    EnumCall,
    EnumValue,
    DynamicStorageOperation,
    DynTraitFromParts,
    TextInterpolation,
    TextBuild,
    LayoutQuery,
    SwitchDispatch
}

public enum BoundIndexAccessKind
{
    Element,
    Slice,
    TextElement,
    TextSlice,
    DynamicElement,
    DynamicSlice,
    RawPointerRegion
}

public enum BoundLayoutQueryKind
{
    SizeOf,
    AlignOf
}

internal static class TypeLayoutQueryFacts
{
    public static StarkTypeSymbol GetResultType(BoundLayoutQueryKind kind)
    {
        return kind == BoundLayoutQueryKind.AlignOf
            ? StarkTypeSymbols.Integer(64, BigInteger.One, new BigInteger(long.MaxValue))
            : StarkTypeSymbols.Integer(64, BigInteger.Zero, new BigInteger(long.MaxValue));
    }

    public static BigInteger GetResultValue(BoundLayoutQueryKind kind, ConcreteTypeLayout layout)
    {
        return kind == BoundLayoutQueryKind.AlignOf
            ? layout.AlignmentBytes
            : layout.SizeBytes;
    }
}

public abstract record BoundOperation(
    BoundOperationKind Kind,
    StarkTypeSymbol ResultType,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record BoundDirectCallOperation(
    TypedFunctionSignature Signature,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.DirectCall, Signature.ReturnType, Location, EnclosingFunctionName)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

public sealed record BoundMemberCallOperation(
    TypedFunctionSignature Signature,
    StarkTypeSymbol ReceiverType,
    bool ReceiverIsAddressable,
    bool ReceiverIsMutable,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.MemberCall, Signature.ReturnType, Location, EnclosingFunctionName)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

public sealed record BoundFunctionPointerCallOperation(
    StarkTypeSymbol FunctionPointerType,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(
        BoundOperationKind.FunctionPointerCall,
        FunctionPointerType.FunctionPointerReturnType ?? StarkTypeSymbols.Error,
        Location,
        EnclosingFunctionName)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

public sealed record BoundClosureCallOperation(
    StarkTypeSymbol ClosureType,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(
        BoundOperationKind.ClosureCall,
        ClosureType.ClosureReturnType ?? StarkTypeSymbols.Error,
        Location,
        EnclosingFunctionName)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

public sealed record BoundIndexAccessOperation(
    BoundIndexAccessKind AccessKind,
    string SourceKind,
    StarkTypeSymbol SourceType,
    StarkTypeSymbol ResultType,
    int IndexCount,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(
        AccessKind is BoundIndexAccessKind.Slice
            or BoundIndexAccessKind.TextSlice
            or BoundIndexAccessKind.DynamicSlice
            or BoundIndexAccessKind.RawPointerRegion
            ? BoundOperationKind.SliceAccess
            : BoundOperationKind.IndexAccess,
        ResultType,
        Location,
        EnclosingFunctionName);

public sealed record BoundDynamicStorageOperation(
    string OperationName,
    StarkTypeSymbol ReceiverType,
    StarkTypeSymbol ResultType,
    int ArgumentCount,
    bool ReceiverIsAddressable,
    bool ReceiverIsMutable,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.DynamicStorageOperation, ResultType, Location, EnclosingFunctionName);

public sealed record BoundDynTraitFromPartsOperation(
    string OperationName,
    StarkTypeSymbol TargetType,
    StarkTypeSymbol ContextType,
    StarkTypeSymbol VtableType,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.DynTraitFromParts, TargetType, Location, EnclosingFunctionName);

public sealed record BoundObjectCreationOperation(
    string ExpressionText,
    StarkTypeSymbol CreatedType,
    TypedConstructorShape? Constructor,
    ObjectCreationStorageSelector StorageSelector,
    IReadOnlyList<ObjectInitializerMemberTypingRecord>? InitializerMembers,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.ObjectCreation, CreatedType, Location, EnclosingFunctionName)
{
    public IReadOnlyList<ObjectInitializerMemberTypingRecord> Members =>
        InitializerMembers ?? [];
}

public sealed record BoundEnumConstructionOperation(
    StarkTypeSymbol EnumType,
    string VariantName,
    IReadOnlyList<EnumConstructorMemberTypingRecord>? MemberRecords,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.EnumConstruction, EnumType, Location, EnclosingFunctionName)
{
    public IReadOnlyList<EnumConstructorMemberTypingRecord> Members =>
        MemberRecords ?? [];
}

public sealed record BoundEnumCallOperation(
    StarkTypeSymbol EnumType,
    string VariantName,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.EnumCall, EnumType, Location, EnclosingFunctionName);

public sealed record BoundEnumValueOperation(
    StarkTypeSymbol EnumType,
    string VariantName,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.EnumValue, EnumType, Location, EnclosingFunctionName);

public sealed record BoundTextInterpolationOperation(
    StarkTypeSymbol ResultType,
    int SegmentCount,
    int HoleCount,
    bool UsesFixedStorage,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.TextInterpolation, ResultType, Location, EnclosingFunctionName);

public sealed record BoundTextBuildOperation(
    string BuildKind,
    StarkTypeSymbol ResultType,
    int OperandCount,
    bool UsesFixedStorage,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.TextBuild, ResultType, Location, EnclosingFunctionName);

public sealed record BoundLayoutQueryOperation(
    BoundLayoutQueryKind QueryKind,
    StarkTypeSymbol TargetType,
    StarkTypeSymbol ResultType,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.LayoutQuery, ResultType, Location, EnclosingFunctionName);

public sealed record BoundSwitchDispatchOperation(
    string Family,
    StarkTypeSymbol SwitchType,
    int SectionCount,
    int LabelCount,
    int ExplicitDefaultLabelCount,
    int LoweredDefaultLabelCount,
    int LiteralLabelCount,
    int MatchAllLabelCount,
    int CaptureLabelCount,
    int StructuredPatternLabelCount,
    int GuardedLabelCount,
    SourceLocation Location,
    string? EnclosingFunctionName = null)
    : BoundOperation(BoundOperationKind.SwitchDispatch, StarkTypeSymbols.Void, Location, EnclosingFunctionName);

public static class SwitchLoweringFamilies
{
    public const string Native = "native";
    public const string PartitionedText = "partitioned-text";
    public const string Guarded = "guarded";

    public static bool IsKnown(string family)
    {
        return string.Equals(family, Native, StringComparison.Ordinal)
            || string.Equals(family, PartitionedText, StringComparison.Ordinal)
            || string.Equals(family, Guarded, StringComparison.Ordinal);
    }
}

public sealed record SwitchTypingRecord(
    string Family,
    StarkTypeSymbol SwitchType,
    int SectionCount,
    int LabelCount,
    int ExplicitDefaultLabelCount,
    int LoweredDefaultLabelCount,
    int LiteralLabelCount,
    int MatchAllLabelCount,
    int CaptureLabelCount,
    int StructuredPatternLabelCount,
    int GuardedLabelCount,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record LambdaTypingRecord(
    string FunctionName,
    StarkTypeSymbol FunctionPointerType,
    IReadOnlyList<string> ParameterNames,
    SourceLocation Location,
    string? EnclosingFunctionName = null);

public sealed record ClosureLambdaTypingRecord(
    string FunctionName,
    StarkTypeSymbol ClosureType,
    StarkTypeSymbol EnvironmentParameterType,
    IReadOnlyList<string> ParameterNames,
    SourceLocation Location,
    string? EnclosingFunctionName = null,
    string? EnvironmentTypeName = null,
    IReadOnlyList<ClosureCaptureFieldSymbol>? CaptureFieldRecords = null)
{
    public IReadOnlyList<ClosureCaptureFieldSymbol> CaptureFields =>
        CaptureFieldRecords ?? [];

    public bool HasCaptures => CaptureFields.Count != 0;
}

public enum ClosureCaptureStorageKind
{
    Value,
    Address
}

public sealed record ClosureCaptureFieldSymbol(
    string Name,
    string FieldName,
    string Mode,
    bool IsUnsafe,
    StarkTypeSymbol SourceType,
    StarkTypeSymbol BodyType,
    StarkTypeSymbol FieldType,
    ClosureCaptureStorageKind StorageKind);

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

/// <summary>
/// One <c>try</c> expression under the role model (doc 11 v2). The type checker
/// resolves the operand's and the enclosing return type's <c>[Ok]</c>/<c>[Err]</c>
/// role variants, the success/failure payload types (with generic substitution), and
/// the <c>from</c> funnel variant used when the failure payload types differ. MIR
/// lowering reads this back by <see cref="Location"/> to emit the discriminant test,
/// the success unwrap, and the failure early return — using the recorded variant
/// names, never hard-coded ones.
/// </summary>
public sealed record TryPropagationTypingRecord(
    SourceLocation Location,
    StarkTypeSymbol OperandType,
    string OperandOkVariantName,
    string OperandErrVariantName,
    StarkTypeSymbol? SuccessPayloadType,
    StarkTypeSymbol? OperandFailurePayloadType,
    StarkTypeSymbol ReturnType,
    string EnclosingErrVariantName,
    StarkTypeSymbol? EnclosingFailurePayloadType,
    string? ConversionFunnelVariant,
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
    string? EnclosingFunctionName = null,
    IReadOnlyList<CallArgumentTypingRecord>? ArgumentRecords = null)
{
    public IReadOnlyList<CallArgumentTypingRecord> Arguments =>
        ArgumentRecords ?? [];
}

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
    ObjectCreationStorageSelector StorageSelector = ObjectCreationStorageSelector.Default,
    IReadOnlyList<ObjectInitializerMemberTypingRecord>? InitializerMembers = null)
{
    public IReadOnlyList<ObjectInitializerMemberTypingRecord> Members =>
        InitializerMembers ?? [];
}

public enum ObjectCreationStorageSelector
{
    Default,
    Arena
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
    string? EnclosingFunctionName = null,
    IReadOnlyList<AggregatePatternMemberTypingRecord>? MemberRecords = null)
{
    public IReadOnlyList<AggregatePatternMemberTypingRecord> Members =>
        MemberRecords ?? [];
}

public sealed record AggregatePatternMemberTypingRecord(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType);

public sealed record FunctionInstantiationTriggerRecord(
    string FunctionName,
    IReadOnlyList<StarkTypeSymbol> TypeArguments,
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
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
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
    SourceLocation Location);

public sealed record LoweringContractValidationModel(
    string ModuleName,
    int CheckedFunctionCount,
    int CheckedCallCount,
    int CheckedIndexAccessCount,
    int CheckedObjectCreationCount,
    int CheckedEnumConstructorCount,
    int CheckedLambdaCount,
    int CheckedTypeLayoutExpressionCount,
    int CheckedDynamicStorageOperationCount,
    int CheckedDynTraitFromPartsCount,
    int CheckedSwitchCount);

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
    IReadOnlyList<LocalStorageCapacityTypingRecord>? LocalStorageCapacityRecords = null,
    IReadOnlyList<ConversionTypingRecord>? ConversionRecords = null,
    IReadOnlyList<DirectCallTypingRecord>? DirectCallRecords = null,
    IReadOnlyList<FunctionPointerPromotionTypingRecord>? FunctionPointerPromotionRecords = null,
    IReadOnlyList<IndirectCallTypingRecord>? IndirectCallRecords = null,
    IReadOnlyList<ClosureCallTypingRecord>? ClosureCallRecords = null,
    IReadOnlyList<FieldAccessTypingRecord>? FieldAccessRecords = null,
    IReadOnlyList<MemberCallTypingRecord>? MemberCallRecords = null,
    IReadOnlyList<TypeLayoutExpressionTypingRecord>? TypeLayoutExpressionRecords = null,
    IReadOnlyList<LambdaTypingRecord>? LambdaRecords = null,
    IReadOnlyList<ClosureLambdaTypingRecord>? ClosureLambdaRecords = null,
    IReadOnlyList<LambdaCaptureTypingRecord>? LambdaCaptureRecords = null,
    IReadOnlyList<AddressTakenFunctionTypingRecord>? AddressTakenFunctionRecords = null,
    IReadOnlyList<IndexAccessTypingRecord>? IndexAccessRecords = null,
    IReadOnlyList<DynamicStorageOperationTypingRecord>? DynamicStorageOperationRecords = null,
    IReadOnlyList<SwitchTypingRecord>? SwitchRecords = null,
    IReadOnlyList<ClosureFunctionPromotionTypingRecord>? ClosureFunctionPromotionRecords = null,
    IReadOnlyList<BoundOperation>? BoundOperationRecords = null,
    IReadOnlyList<TryPropagationTypingRecord>? TryPropagationRecords = null,
    IReadOnlyDictionary<string, ThreadSafetyLawTypeFacts>? ThreadSafetyLawFactRecords = null,
    IReadOnlyDictionary<string, IReadOnlyList<TypedConstructorShape>>? ConstructorShapeRecords = null)
{
    public IReadOnlyList<TryPropagationTypingRecord> TryPropagations =>
        TryPropagationRecords ?? [];

    public IReadOnlyDictionary<string, IReadOnlyList<TypedConstructorShape>> ConstructorShapes =>
        ConstructorShapeRecords ?? EmptyConstructorShapes;

    private static IReadOnlyDictionary<string, IReadOnlyList<TypedConstructorShape>> EmptyConstructorShapes { get; } =
        new Dictionary<string, IReadOnlyList<TypedConstructorShape>>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ThreadSafetyLawTypeFacts> ThreadSafetyLawFacts =>
        ThreadSafetyLawFactRecords ?? EmptyThreadSafetyLawFacts;

    private static IReadOnlyDictionary<string, ThreadSafetyLawTypeFacts> EmptyThreadSafetyLawFacts { get; } =
        new Dictionary<string, ThreadSafetyLawTypeFacts>(StringComparer.Ordinal);

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

    public IReadOnlyList<LocalStorageCapacityTypingRecord> LocalStorageCapacities =>
        LocalStorageCapacityRecords ?? [];

    public IReadOnlyList<ConversionTypingRecord> Conversions =>
        ConversionRecords ?? [];

    public IReadOnlyList<DirectCallTypingRecord> DirectCalls =>
        DirectCallRecords ?? [];

    public IReadOnlyList<FunctionPointerPromotionTypingRecord> FunctionPointerPromotions =>
        FunctionPointerPromotionRecords ?? [];

    public IReadOnlyList<ClosureFunctionPromotionTypingRecord> ClosureFunctionPromotions =>
        ClosureFunctionPromotionRecords ?? [];

    public IReadOnlyList<AddressTakenFunctionTypingRecord> AddressTakenFunctions =>
        AddressTakenFunctionRecords ?? [];

    public IReadOnlyList<IndirectCallTypingRecord> IndirectCalls =>
        IndirectCallRecords ?? [];

    public IReadOnlyList<ClosureCallTypingRecord> ClosureCalls =>
        ClosureCallRecords ?? [];

    public IReadOnlyList<LambdaTypingRecord> Lambdas =>
        LambdaRecords ?? [];

    public IReadOnlyList<ClosureLambdaTypingRecord> ClosureLambdas =>
        ClosureLambdaRecords ?? [];

    public IReadOnlyList<LambdaCaptureTypingRecord> LambdaCaptures =>
        LambdaCaptureRecords ?? [];

    public IReadOnlyList<FieldAccessTypingRecord> FieldAccesses =>
        FieldAccessRecords ?? [];

    public IReadOnlyList<MemberCallTypingRecord> MemberCalls =>
        MemberCallRecords ?? [];

    public IReadOnlyList<TypeLayoutExpressionTypingRecord> TypeLayoutExpressions =>
        TypeLayoutExpressionRecords ?? [];

    public IReadOnlyList<IndexAccessTypingRecord> IndexAccesses =>
        IndexAccessRecords ?? [];

    public IReadOnlyList<DynamicStorageOperationTypingRecord> DynamicStorageOperations =>
        DynamicStorageOperationRecords ?? [];

    public IReadOnlyList<SwitchTypingRecord> Switches =>
        SwitchRecords ?? [];

    public IReadOnlyList<BoundOperation> BoundOperations =>
        BoundOperationRecords ?? [];
}

internal static class TemplateLocalDeclarationFacts
{
    public const string ConstantKind = "const";
    public const string VariableKind = "var";
    public const string ForVariableKind = "forvar";
    public const string TraversalIndexKind = "forindex";
    public const string TraversalElementKind = "forelement";

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
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
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
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
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
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
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
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
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
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
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
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArguments,
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
    LinkOnceOdrComdat,

    // A globally-visible, deduplicated definition that the optimizer must NOT
    // discard even when it has no in-module references (unlike linkonce_odr,
    // which GlobalDCE/ThinLTO may drop). Used to emit a fallback body for an
    // imported function whose owning package pruned the symbol, so cross-module
    // callers still resolve while a real strong definition elsewhere still wins.
    WeakOdrPreserved
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
    string? RawPointerElementCountExpression = null,
    IReadOnlyList<StarkTypeSymbol>? LlvmParameterTypes = null)
{
    public IReadOnlyList<StarkTypeSymbol> EffectiveLlvmParameterTypes =>
        LlvmParameterTypes is { Count: > 0 }
            ? LlvmParameterTypes
            : [LlvmType];

    public bool IsExpandedDirectParameter =>
        Kind == AbiParameterKind.Direct && EffectiveLlvmParameterTypes.Count > 1;

    public string GetLlvmValueName(int carrierIndex) =>
        IsExpandedDirectParameter
            ? $"{LlvmName}_{carrierIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : LlvmName;
}

public sealed record AbiFunctionSignature(
    string Name,
    string SymbolName,
    StarkTypeSymbol SourceReturnType,
    StarkTypeSymbol LlvmReturnType,
    IReadOnlyList<AbiParameterSymbol> Parameters,
    bool IsFfi,
    string? SourceName = null,
    bool UsesFastCallingConvention = false,
    bool IsVarargs = false,
    StarkFfiAbi? FfiAbi = null,
    bool UsesTailCallingConvention = false)
{
    public string DisplaySourceName => SourceName ?? Name;

    public bool ReturnsIndirect => Parameters.Any(static parameter => parameter.Kind == AbiParameterKind.SRet);

    public AbiParameterSymbol? ReturnBufferParameter => Parameters.FirstOrDefault(static parameter => parameter.Kind == AbiParameterKind.SRet);

    public IReadOnlyList<AbiParameterSymbol> UserParameters => Parameters
        .Where(static parameter => parameter.Kind != AbiParameterKind.SRet)
        .ToArray();

    public IReadOnlyList<AbiParameterSymbol> LlvmParameters => Parameters
        .SelectMany(static parameter => parameter.IsExpandedDirectParameter
            ? parameter.EffectiveLlvmParameterTypes.Select((carrierType, carrierIndex) => parameter with
            {
                LlvmName = parameter.GetLlvmValueName(carrierIndex),
                LlvmType = carrierType,
                LlvmParameterTypes = null
            })
            : [parameter])
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

public sealed record ParameterInitializationRangeSummary(
    long StartByte,
    long EndByte);

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
    ParameterCaptureKind CaptureKind,
    IReadOnlyList<ParameterInitializationRangeSummary>? InitializationRanges = null,
    bool PointeeDeadOnReturn = false);

public sealed record ConcreteFieldLayout(
    string Name,
    int OffsetBytes,
    int SizeBytes,
    int NaturalAlignmentBytes,
    int EffectiveAlignmentBytes,
    bool IsMisaligned);

public sealed record ConcreteTypeLayout(
    int SizeBytes,
    int AlignmentBytes,
    IReadOnlyList<ConcreteFieldLayout>? FieldLayouts = null)
{
    public IReadOnlyList<ConcreteFieldLayout> Fields => FieldLayouts ?? [];

    public bool TryGetField(string name, out ConcreteFieldLayout fieldLayout)
    {
        foreach (var field in Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                fieldLayout = field;
                return true;
            }
        }

        fieldLayout = null!;
        return false;
    }
}

internal static class ConcreteTypeLayoutHelper
{
    public static NamedTypeSymbol? TryGetConcreteNamedTypeSymbol(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        var concreteType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
        if (concreteType.Kind != StarkTypeKind.Named
            || concreteType.NamedType is not { } concreteName)
        {
            return null;
        }

        if (namedTypes.TryGetValue(concreteName, out var exactType)
            && !ShouldUseGenericTemplateFallback(concreteType, exactType, namedTypes))
        {
            return exactType;
        }

        if (!StarkTypeSymbols.IsGenericInstantiation(concreteType))
        {
            return null;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(concreteName);
        if (!namedTypes.TryGetValue(baseName, out var template)
            || !TryBuildGenericLayoutSubstitutions(
                template,
                concreteType.TypeArguments ?? [],
                concreteType.ComptimeValueArguments ?? [],
                out var typeSubstitution,
                out var valueSubstitution))
        {
            return null;
        }

        return SubstituteNamedTypeForLayout(concreteName, template, typeSubstitution, valueSubstitution);
    }

    private static bool ShouldUseGenericTemplateFallback(
        StarkTypeSymbol concreteType,
        NamedTypeSymbol exactType,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        if (!StarkTypeSymbols.IsGenericInstantiation(concreteType)
            || concreteType.NamedType is not { } concreteName
            || exactType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
            || exactType.OrderedFields.Count != 0)
        {
            return false;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(concreteName);
        return namedTypes.TryGetValue(baseName, out var template)
            && !ReferenceEquals(template, exactType)
            && template.Kind == exactType.Kind
            && template.OrderedFields.Count > 0;
    }

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
        if (StarkTypeSymbols.IsPointerBackedBorrowType(type))
        {
            return TryGetPointerLayout();
        }

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
            StarkTypeKind.Closure =>
                TryGetClosureLayout(type),
            StarkTypeKind.Slice or StarkTypeKind.Ascii or StarkTypeKind.Unicode =>
                TryGetViewLayout(),
            StarkTypeKind.Dynamic =>
                TryGetDynamicStorageLayout(),
            StarkTypeKind.FixedArray when concreteType.ElementType is not null && concreteType.FixedLength is int fixedLength =>
                TryGetFixedArrayLayout(concreteType.ElementType, fixedLength, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes),
            StarkTypeKind.Named when concreteType.NamedType is not null =>
                TryGetConcreteNamedTypeLayout(concreteType, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes),
            _ => null
        };
    }

    private static ConcreteTypeLayout? TryGetConcreteNamedTypeLayout(
        StarkTypeSymbol concreteType,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        ISet<string> activeNamedTypes)
    {
        var concreteName = concreteType.NamedType!;
        if (publishedConcreteLayouts is not null
            && publishedConcreteLayouts.TryGetValue(concreteName, out var publishedLayout))
        {
            return publishedLayout;
        }

        if (namedTypes.TryGetValue(concreteName, out var exactType))
        {
            return exactType.Kind switch
            {
                DeclarationKind.Struct or DeclarationKind.Record =>
                    TryGetNamedTypeLayout(exactType, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes),
                DeclarationKind.Enum when enumLayouts is not null
                                          && enumLayouts.TryGetValue(concreteName, out var exactEnumLayout) =>
                    TryGetEnumTypeLayout(exactEnumLayout, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes),
                _ => null
            };
        }

        if (!StarkTypeSymbols.IsGenericInstantiation(concreteType))
        {
            return null;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(concreteName);
        if (!namedTypes.TryGetValue(baseName, out var template))
        {
            return null;
        }

        if (!TryBuildGenericLayoutSubstitutions(
                template,
                concreteType.TypeArguments ?? [],
                concreteType.ComptimeValueArguments ?? [],
                out var typeSubstitution,
                out var valueSubstitution))
        {
            return null;
        }

        return template.Kind switch
        {
            DeclarationKind.Struct or DeclarationKind.Record =>
                TryGetNamedTypeLayout(
                    SubstituteNamedTypeForLayout(concreteName, template, typeSubstitution, valueSubstitution),
                    namedTypes,
                    enumLayouts,
                    publishedConcreteLayouts,
                    activeNamedTypes),
            DeclarationKind.Enum when enumLayouts is not null
                                      && enumLayouts.TryGetValue(baseName, out var templateEnumLayout) =>
                TryGetEnumTypeLayout(
                    SubstituteEnumLayoutForLayout(concreteName, templateEnumLayout, typeSubstitution, valueSubstitution),
                    namedTypes,
                    enumLayouts,
                    publishedConcreteLayouts,
                    activeNamedTypes),
            _ => null
        };
    }

    private static bool TryBuildGenericLayoutSubstitutions(
        NamedTypeSymbol template,
        IReadOnlyList<StarkTypeSymbol> typeArguments,
        IReadOnlyList<ComptimeValueArgumentSymbol> valueArguments,
        out IReadOnlyDictionary<string, StarkTypeSymbol> typeSubstitution,
        out IReadOnlyDictionary<string, BigInteger> valueSubstitution)
    {
        typeSubstitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        valueSubstitution = new Dictionary<string, BigInteger>(StringComparer.Ordinal);

        if (template.GenericParams.Count != typeArguments.Count
            || template.ComptimeGenericParams.Count != valueArguments.Count)
        {
            return false;
        }

        var types = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        for (var index = 0; index < template.GenericParams.Count; index++)
        {
            types[template.GenericParams[index]] = typeArguments[index];
        }

        var values = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        for (var index = 0; index < template.ComptimeGenericParams.Count; index++)
        {
            var argument = valueArguments[index];
            if (argument.IsSymbolic)
            {
                return false;
            }

            values[template.ComptimeGenericParams[index].Name] = argument.IntegerValue;
        }

        typeSubstitution = types;
        valueSubstitution = values;
        return true;
    }

    private static NamedTypeSymbol SubstituteNamedTypeForLayout(
        string concreteName,
        NamedTypeSymbol template,
        IReadOnlyDictionary<string, StarkTypeSymbol> typeSubstitution,
        IReadOnlyDictionary<string, BigInteger> valueSubstitution)
    {
        var orderedFields = template.OrderedFields
            .Select(field => SubstituteFieldForLayout(field, typeSubstitution, valueSubstitution))
            .ToArray();
        var fields = orderedFields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        return template with
        {
            Name = concreteName,
            Fields = fields,
            OrderedFields = orderedFields,
            GenericParameterNames = null,
            ComptimeGenericParameterNames = null
        };
    }

    private static EnumLayoutSymbol SubstituteEnumLayoutForLayout(
        string concreteName,
        EnumLayoutSymbol template,
        IReadOnlyDictionary<string, StarkTypeSymbol> typeSubstitution,
        IReadOnlyDictionary<string, BigInteger> valueSubstitution)
    {
        return template with
        {
            EnumName = concreteName,
            TagField = SubstituteFieldForLayout(template.TagField, typeSubstitution, valueSubstitution),
            OrderedFields = template.OrderedFields
                .Select(field => SubstituteFieldForLayout(field, typeSubstitution, valueSubstitution))
                .ToArray(),
            Variants = template.Variants.ToDictionary(
                static item => item.Key,
                item => item.Value with
                {
                    Fields = item.Value.Fields
                        .Select(field => field with
                        {
                            Type = FunctionOverloadFacts.SubstituteType(field.Type, typeSubstitution, comptimeValueSubstitution: valueSubstitution)
                        })
                        .ToArray()
                },
                StringComparer.Ordinal)
        };
    }

    private static FieldSymbol SubstituteFieldForLayout(
        FieldSymbol field,
        IReadOnlyDictionary<string, StarkTypeSymbol> typeSubstitution,
        IReadOnlyDictionary<string, BigInteger> valueSubstitution)
    {
        return field with
        {
            Type = FunctionOverloadFacts.SubstituteType(field.Type, typeSubstitution, comptimeValueSubstitution: valueSubstitution)
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
            return type.Layout?.Kind == StructLayoutKind.Explicit
                ? TryGetExplicitNamedTypeLayout(type, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes)
                : TryGetSequentialNamedTypeLayout(type, namedTypes, enumLayouts, publishedConcreteLayouts, activeNamedTypes);
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

    private static ConcreteTypeLayout? TryGetSequentialNamedTypeLayout(
        NamedTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        ISet<string> activeNamedTypes)
    {
        var sizeBytes = 0;
        var alignmentBytes = 1;
        var packBytes = type.Layout?.Kind == StructLayoutKind.C ? type.Layout.PackBytes : null;
        var fieldLayouts = new List<ConcreteFieldLayout>(type.OrderedFields.Count);

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

            var naturalAlignmentBytes = fieldLayout.AlignmentBytes;
            var effectiveAlignmentBytes = packBytes is { } pack
                ? Math.Min(naturalAlignmentBytes, pack)
                : naturalAlignmentBytes;
            var fieldOffsetBytes = AlignTo(sizeBytes, effectiveAlignmentBytes);
            fieldLayouts.Add(new ConcreteFieldLayout(
                field.Name,
                fieldOffsetBytes,
                fieldLayout.SizeBytes,
                naturalAlignmentBytes,
                effectiveAlignmentBytes,
                fieldOffsetBytes % naturalAlignmentBytes != 0));

            sizeBytes = checked(fieldOffsetBytes + fieldLayout.SizeBytes);
            alignmentBytes = Math.Max(alignmentBytes, effectiveAlignmentBytes);
        }

        if (type.Layout?.AlignBytes is { } explicitAlignmentBytes)
        {
            alignmentBytes = Math.Max(alignmentBytes, explicitAlignmentBytes);
        }

        sizeBytes = AlignTo(sizeBytes, alignmentBytes);
        return new ConcreteTypeLayout(sizeBytes, alignmentBytes, fieldLayouts);
    }

    private static ConcreteTypeLayout? TryGetExplicitNamedTypeLayout(
        NamedTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        IReadOnlyDictionary<string, EnumLayoutSymbol>? enumLayouts,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts,
        ISet<string> activeNamedTypes)
    {
        var sizeBytes = 0;
        var alignmentBytes = 1;
        var fieldLayouts = new List<ConcreteFieldLayout>(type.OrderedFields.Count);

        foreach (var field in type.OrderedFields)
        {
            if (field.ExplicitOffsetBytes is not { } fieldOffsetBytes)
            {
                return null;
            }

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

            fieldLayouts.Add(new ConcreteFieldLayout(
                field.Name,
                fieldOffsetBytes,
                fieldLayout.SizeBytes,
                fieldLayout.AlignmentBytes,
                fieldOffsetBytes % fieldLayout.AlignmentBytes == 0
                    ? fieldLayout.AlignmentBytes
                    : GreatestPowerOfTwoDivisor(fieldOffsetBytes),
                fieldOffsetBytes % fieldLayout.AlignmentBytes != 0));

            sizeBytes = Math.Max(sizeBytes, checked(fieldOffsetBytes + fieldLayout.SizeBytes));
            alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
        }

        if (type.Layout?.AlignBytes is { } explicitAlignmentBytes)
        {
            alignmentBytes = Math.Max(alignmentBytes, explicitAlignmentBytes);
        }

        sizeBytes = AlignTo(sizeBytes, alignmentBytes);
        return new ConcreteTypeLayout(sizeBytes, alignmentBytes, fieldLayouts);
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
            var fieldLayouts = new List<ConcreteFieldLayout>(layout.OrderedFields.Count);

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

                var fieldOffsetBytes = AlignTo(sizeBytes, fieldLayout.AlignmentBytes);
                fieldLayouts.Add(new ConcreteFieldLayout(
                    field.Name,
                    fieldOffsetBytes,
                    fieldLayout.SizeBytes,
                    fieldLayout.AlignmentBytes,
                    fieldLayout.AlignmentBytes,
                    IsMisaligned: false));
                sizeBytes = checked(fieldOffsetBytes + fieldLayout.SizeBytes);
                alignmentBytes = Math.Max(alignmentBytes, fieldLayout.AlignmentBytes);
            }

            sizeBytes = AlignTo(sizeBytes, alignmentBytes);
            return new ConcreteTypeLayout(sizeBytes, alignmentBytes, fieldLayouts);
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

    private static ConcreteTypeLayout TryGetClosureLayout(StarkTypeSymbol type)
    {
        var pointerLayout = TryGetPointerLayout();
        var sizeBytes = AlignTo(pointerLayout.SizeBytes, pointerLayout.AlignmentBytes);
        sizeBytes = checked(sizeBytes + pointerLayout.SizeBytes);
        if (type.ClosureStorageKind == StarkClosureStorageKind.Heap)
        {
            sizeBytes = AlignTo(sizeBytes, pointerLayout.AlignmentBytes);
            sizeBytes = checked(sizeBytes + pointerLayout.SizeBytes);
        }

        sizeBytes = AlignTo(sizeBytes, pointerLayout.AlignmentBytes);
        return new ConcreteTypeLayout(sizeBytes, pointerLayout.AlignmentBytes);
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

    private static int GreatestPowerOfTwoDivisor(int value)
    {
        if (value == 0)
        {
            return 1;
        }

        return value & -value;
    }
}

public sealed record FunctionMemoryEffectSummary(
    bool ReadsArgumentMemory,
    bool WritesArgumentMemory,
    bool CapturesArgumentMemory,
    bool ReadsOtherMemory = false,
    bool WritesOtherMemory = false,
    bool InitializesArgumentMemory = false,
    bool HasPointeeDeadOnReturnArgument = false)
{
    public bool ReadsMemory => ReadsArgumentMemory || ReadsOtherMemory;

    public bool WritesMemory => WritesArgumentMemory || WritesOtherMemory || InitializesArgumentMemory;
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
    FunctionOptimizationSummary? OptimizationSummary = null,
    bool HasBody = false,
    bool HasOpaqueCall = false)
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
    FunctionOptimizationSummary? OptimizationSummary = null,
    FunctionOwnershipSummary? OwnershipSummary = null,
    bool HasOpaqueCall = true)
{
    public IReadOnlyList<CallMemoryEffectSummary> Calls => CallSummaries ?? [];

    public bool CanStrengthenKind => FunctionKindFacts.Rank(EffectiveKind) > FunctionKindFacts.Rank(DeclaredKind);

    public FunctionOwnershipSummary? Ownership => OwnershipSummary;
}

public sealed record SemanticValidationModel(
    string ModuleName,
    IReadOnlyDictionary<string, FunctionValidationSummary> Functions);

public enum OwnershipStorageRootKind
{
    Local,
    Parameter,
    Global
}

public enum OwnershipRootAvailabilityKind
{
    Unknown,
    Initialized,
    Uninitialized,
    PartiallyInitialized,
    Moved,
    ControlFlow
}

public enum OwnershipEventKind
{
    Move,
    FieldMove,
    ImplicitDrop,
    AssignmentDrop,
    Reinitialize,
    AddressTaken
}

public sealed record OwnershipPlaceSummary(
    string RootName,
    StarkTypeSymbol Type,
    IReadOnlyList<string>? ProjectionPath = null,
    bool HasIndexProjection = false)
{
    public IReadOnlyList<string> Projections => ProjectionPath ?? [];

    public string DisplayName =>
        Projections.Count == 0
            ? RootName
            : $"{RootName}.{string.Join(".", Projections)}";
}

public sealed record OwnershipEventSummary(
    OwnershipEventKind Kind,
    OwnershipPlaceSummary Place,
    SourceLocation? Location = null);

public sealed record OwnershipRootSummary(
    string Name,
    StarkTypeSymbol Type,
    OwnershipStorageRootKind RootKind,
    bool IsMutable,
    bool IsConstant,
    bool IsAddressTaken,
    bool HasRawPointerEscape,
    bool HasMove,
    bool HasPartialMove,
    bool HasImplicitDrop,
    bool HasAssignmentDrop,
    bool HasReinitialization,
    bool RequiresDrop,
    OwnershipRootAvailabilityKind FinalAvailability);

public sealed record FunctionOwnershipSummary(
    string Name,
    bool OwnershipValid,
    IReadOnlyList<string> ImplicitDrops,
    IReadOnlyList<string> Moves,
    IReadOnlyList<OwnershipEventSummary>? OwnershipEvents = null,
    IReadOnlyList<OwnershipRootSummary>? OwnershipRoots = null)
{
    public IReadOnlyList<OwnershipEventSummary> Events => OwnershipEvents ?? [];

    public IReadOnlyList<OwnershipRootSummary> Roots => OwnershipRoots ?? [];
}

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
    IReadOnlyDictionary<string, StarkTypeSymbol>? GenericTypeSubstitution = null,
    IReadOnlyDictionary<string, BigInteger>? GenericValueSubstitution = null);

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
    ArenaFrameEnter,
    ArenaFrameLeave,
    Assign,
    StoreIndirect,
    Evaluate
}

public enum MemoryWriteKind
{
    Replacement,
    Initialization
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

public enum IndexedElementOperationFamily
{
    FixedArrayElement,
    ViewComponent,
    ClosureComponent,
    // Components of a `dyn Trait` fat pointer: slot 0 is the erased data pointer
    // (rawmutptr<i8>), slot 1 is the read-only typed vtable pointer
    // (rawptr<Trait.Vtable>).
    DynTraitComponent
}

public enum MidLevelIrTerminatorKind
{
    Goto,
    Branch,
    Switch,
    Return,
    TailCall,
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
    bool HasConstProvenance = false,
    ConstProvenanceKind ConstProvenance = ConstProvenanceKind.None);

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

public sealed record MidLevelIrClosureValueOperand(string InvokeFunctionName, StarkTypeSymbol Type)
    : MidLevelIrOperand(Type, $"closure<{InvokeFunctionName}>");

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

public enum MidLevelIrObjectConstructionKind
{
    Empty,
    PrimaryConstructor,
    ExplicitConstructor,
    Initializer,
    ConstructorAndInitializer
}

public sealed record MidLevelIrObjectConstructionFacts(
    StarkTypeSymbol CreatedType,
    string TypeName,
    MidLevelIrObjectConstructionKind Kind,
    TypedConstructorShape? Constructor,
    IReadOnlyList<ObjectInitializerMemberTypingRecord> InitializerMembers,
    string? ConstructorBodyKey = null);

public sealed record MidLevelIrObjectConstructionOperand(
    MidLevelIrOperand Value,
    MidLevelIrObjectConstructionFacts Facts)
    : MidLevelIrOperand(MidLevelIrArtifactValidation.ValidateObjectConstruction(Value, Facts), Value.Text);

public sealed record MidLevelIrEnumPayloadFieldConstructionFacts(
    string FieldName,
    int FieldIndex,
    StarkTypeSymbol FieldType,
    string StorageFieldName,
    int StorageFieldIndex,
    StarkTypeSymbol StorageFieldType);

public sealed record MidLevelIrEnumConstructionFacts(
    StarkTypeSymbol EnumType,
    EnumLayoutSymbol Layout,
    EnumVariantLayoutSymbol Variant,
    IReadOnlyList<MidLevelIrEnumPayloadFieldConstructionFacts> PayloadFields);

public sealed record MidLevelIrEnumConstructionOperand(
    MidLevelIrOperand Value,
    MidLevelIrEnumConstructionFacts Facts)
    : MidLevelIrOperand(MidLevelIrArtifactValidation.ValidateEnumConstruction(Value, Facts), Value.Text);

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
    IReadOnlyList<MidLevelIrOperand?>? IndirectArgumentAddresses = null,
    IReadOnlyList<MidLevelIrDynamicStorageLengthCommit>? PostCallDynamicLengthCommits = null)
    : MidLevelIrRValue(MidLevelIrArtifactValidation.ValidateValueCallType(Type, FunctionName), Text);

public sealed record MidLevelIrDynamicStorageLengthCommit(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    MidLevelIrOperand InitializedLength);

public abstract record MidLevelIrCallStatementOperation(
    StarkTypeSymbol ReturnType,
    string Text);

public sealed record MidLevelIrDirectCallStatementOperation(
    string FunctionName,
    IReadOnlyList<MidLevelIrOperand> Arguments,
    StarkTypeSymbol ReturnType,
    string Text,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<MidLevelIrOperand?>? IndirectArgumentAddresses = null,
    IReadOnlyList<MidLevelIrDynamicStorageLengthCommit>? PostCallDynamicLengthCommits = null)
    : MidLevelIrCallStatementOperation(ReturnType, Text);

public sealed record MidLevelIrIndirectCallStatementOperation(
    MidLevelIrOperand Target,
    IReadOnlyList<MidLevelIrOperand> Arguments,
    StarkTypeSymbol ReturnType,
    string Text,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    IReadOnlyList<MidLevelIrOperand?>? IndirectArgumentAddresses = null,
    bool MayFree = false)
    : MidLevelIrCallStatementOperation(ReturnType, Text);

public sealed record MidLevelIrIndirectCallRValue(
    MidLevelIrOperand Target,
    IReadOnlyList<MidLevelIrOperand> Arguments,
    StarkTypeSymbol Type,
    string Text,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    IReadOnlyList<MidLevelIrOperand?>? IndirectArgumentAddresses = null,
    bool MayFree = false)
    : MidLevelIrRValue(MidLevelIrArtifactValidation.ValidateValueCallType(Type, Text), Text);

public sealed record MidLevelIrConvertRValue(
    MidLevelIrOperand Operand,
    StarkTypeSymbol TargetType,
    string Text)
    : MidLevelIrRValue(TargetType, Text);

// Loads a method slot from a `dyn Trait` vtable: the function pointer at slot
// `SlotIndex` reached via `getelementptr ptr, ptr <vtable>, i32 SlotIndex`. The
// result type is the slot's `fnptr<kind ...>` so an indirect call through it
// preserves the trait method's effect contract (law/finite call-site attributes).
public sealed record MidLevelIrDynVTableSlotRValue(
    MidLevelIrOperand VtablePointer,
    int SlotIndex,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(Type, Text);

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
    IndexedElementOperationFamily OperationFamily,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(MidLevelIrArtifactValidation.ValidateIndexExtraction(Target, ElementIndex, OperationFamily, Type, Text), Text);

public sealed record MidLevelIrInsertIndexRValue(
    MidLevelIrOperand Target,
    int ElementIndex,
    IndexedElementOperationFamily OperationFamily,
    MidLevelIrOperand Value,
    StarkTypeSymbol Type,
    string Text)
    : MidLevelIrRValue(MidLevelIrArtifactValidation.ValidateIndexInsertion(Target, ElementIndex, OperationFamily, Value, Type, Text), Text);

internal static class MidLevelIrArtifactValidation
{
    public static StarkTypeSymbol ValidateValueCallType(StarkTypeSymbol type, string callName)
    {
        if (type.Kind == StarkTypeKind.Void)
        {
            throw new ArgumentException(
                $"MIR value call '{callName}' cannot have void result type. Use a statement-only call operation instead.",
                nameof(type));
        }

        return type;
    }

    public static StarkTypeSymbol ValidateIndexExtraction(
        MidLevelIrOperand target,
        int elementIndex,
        IndexedElementOperationFamily operationFamily,
        StarkTypeSymbol resultType,
        string text)
    {
        var elementType = GetIndexElementType(target.Type, elementIndex, operationFamily, text, "MIR");
        if (!HaveSameIndexType(elementType, resultType))
        {
            throw new ArgumentException(
                $"MIR index extraction '{text}' result type '{resultType.DisplayName}' does not match indexed element type '{elementType.DisplayName}'.",
                nameof(resultType));
        }

        return resultType;
    }

    public static StarkTypeSymbol ValidateIndexInsertion(
        MidLevelIrOperand target,
        int elementIndex,
        IndexedElementOperationFamily operationFamily,
        MidLevelIrOperand value,
        StarkTypeSymbol resultType,
        string text)
    {
        ArgumentNullException.ThrowIfNull(value);

        var elementType = GetIndexElementType(target.Type, elementIndex, operationFamily, text, "MIR");
        if (!HaveSameIndexType(target.Type, resultType))
        {
            throw new ArgumentException(
                $"MIR index insertion '{text}' result type '{resultType.DisplayName}' does not match target type '{target.Type.DisplayName}'.",
                nameof(resultType));
        }

        if (!HaveSameIndexType(elementType, value.Type))
        {
            throw new ArgumentException(
                $"MIR index insertion '{text}' value type '{value.Type.DisplayName}' does not match indexed element type '{elementType.DisplayName}'.",
                nameof(value));
        }

        return resultType;
    }

    private static StarkTypeSymbol GetIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        IndexedElementOperationFamily operationFamily,
        string text,
        string artifactName)
    {
        return operationFamily switch
        {
            IndexedElementOperationFamily.FixedArrayElement => GetFixedArrayIndexElementType(targetType, elementIndex, text, artifactName),
            IndexedElementOperationFamily.ViewComponent => GetViewIndexElementType(targetType, elementIndex, text, artifactName),
            IndexedElementOperationFamily.ClosureComponent => GetClosureIndexElementType(targetType, elementIndex, text, artifactName),
            IndexedElementOperationFamily.DynTraitComponent => MidLevelIrArtifactValidation.GetDynTraitIndexElementType(targetType, elementIndex, text, artifactName),
            _ => throw new ArgumentException(
                $"{artifactName} index operation '{text}' uses unsupported operation family '{operationFamily}'.",
                nameof(operationFamily))
        };
    }

    private static StarkTypeSymbol GetFixedArrayIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        string text,
        string artifactName)
    {
        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is not { } elementType
            || targetType.FixedLength is not int fixedLength)
        {
            throw new ArgumentException(
                $"{artifactName} fixed-array index operation '{text}' requires a fixed-array target, but found '{targetType.DisplayName}'.",
                nameof(targetType));
        }

        if (elementIndex < 0 || elementIndex >= fixedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementIndex),
                $"{artifactName} fixed-array index operation '{text}' index '{elementIndex}' is out of range for '{targetType.DisplayName}' with length {fixedLength}.");
        }

        return elementType;
    }

    private static StarkTypeSymbol GetViewIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        string text,
        string artifactName)
    {
        if (elementIndex is not 0 and not 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementIndex),
                $"{artifactName} view index operation '{text}' index '{elementIndex}' is out of range for '{targetType.DisplayName}' with 2 component(s).");
        }

        return targetType.Kind switch
        {
            StarkTypeKind.Slice when targetType.ElementType is { } elementType => elementIndex == 0
                ? StarkTypeSymbols.RawPointer(elementType, isMutable: targetType.IsMutableView)
                : StarkTypeSymbols.Integer(64),
            StarkTypeKind.Ascii => elementIndex == 0
                ? StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false)
                : StarkTypeSymbols.Integer(64),
            StarkTypeKind.Unicode => elementIndex == 0
                ? StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false)
                : StarkTypeSymbols.Integer(64),
            StarkTypeKind.Slice => throw new ArgumentException(
                $"{artifactName} view index operation '{text}' requires a slice with a known element type, but found '{targetType.DisplayName}'.",
                nameof(targetType)),
            _ => throw new ArgumentException(
                $"{artifactName} view index operation '{text}' requires a slice or text target, but found '{targetType.DisplayName}'.",
                nameof(targetType))
        };
    }

    private static StarkTypeSymbol GetClosureIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        string text,
        string artifactName)
    {
        if (targetType.Kind != StarkTypeKind.Closure)
        {
            throw new ArgumentException(
                $"{artifactName} closure index operation '{text}' requires a closure target, but found '{targetType.DisplayName}'.",
                nameof(targetType));
        }

        var elementType = elementIndex switch
        {
            0 => CallableValueFacts.BuildClosureInvokeFunctionPointerType(targetType),
            1 => CallableValueFacts.BuildClosureEnvironmentPointerType(targetType),
            2 when targetType.ClosureStorageKind == StarkClosureStorageKind.Heap
                => CallableValueFacts.BuildClosureDropFunctionPointerType(),
            _ => StarkTypeSymbols.Error
        };

        if (elementType.Kind == StarkTypeKind.Error)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementIndex),
                $"{artifactName} closure index operation '{text}' index '{elementIndex}' is out of range or unresolved for closure value '{targetType.DisplayName}'.");
        }

        return elementType;
    }

    private static bool HaveSameIndexType(StarkTypeSymbol expectedType, StarkTypeSymbol actualType)
    {
        return expectedType == actualType
            || expectedType.Kind == actualType.Kind
            && string.Equals(expectedType.DisplayName, actualType.DisplayName, StringComparison.Ordinal);
    }

    // The component types of a `dyn Trait` fat pointer: slot 0 is the erased data
    // pointer (rawmutptr<i8>), slot 1 is the read-only typed vtable pointer
    // (rawptr<Trait.Vtable>).
    // Shared by MIR and SSA index validation so both agree on the fat-pointer layout.
    internal static StarkTypeSymbol GetDynTraitIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        string text,
        string artifactName)
    {
        if (targetType.Kind != StarkTypeKind.DynTrait)
        {
            throw new ArgumentException(
                $"{artifactName} dyn-trait index operation '{text}' requires a dyn-trait target, but found '{targetType.DisplayName}'.",
                nameof(targetType));
        }

        return elementIndex switch
        {
            0 => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true),
            1 => StarkTypeSymbols.DynTraitVtablePointerForTraitObject(targetType),
            _ => throw new ArgumentOutOfRangeException(
                nameof(elementIndex),
                $"{artifactName} dyn-trait index operation '{text}' index '{elementIndex}' is out of range for '{targetType.DisplayName}' with 2 component(s).")
        };
    }

    public static StarkTypeSymbol ValidateObjectConstruction(
        MidLevelIrOperand value,
        MidLevelIrObjectConstructionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.InitializerMembers);

        if (facts.CreatedType.Kind != StarkTypeKind.Named || string.IsNullOrWhiteSpace(facts.CreatedType.NamedType))
        {
            throw new ArgumentException(
                $"MIR object construction requires a named created type, but received '{facts.CreatedType.DisplayName}'.",
                nameof(facts));
        }

        if (value.Type != facts.CreatedType)
        {
            throw new ArgumentException(
                $"MIR object construction value type '{value.Type.DisplayName}' does not match constructed type '{facts.CreatedType.DisplayName}'.",
                nameof(value));
        }

        if (string.IsNullOrWhiteSpace(facts.TypeName))
        {
            throw new ArgumentException("MIR object construction facts require a type name.", nameof(facts));
        }

        switch (facts.Kind)
        {
            case MidLevelIrObjectConstructionKind.Empty:
            case MidLevelIrObjectConstructionKind.Initializer:
                if (facts.Constructor is not null || facts.ConstructorBodyKey is not null)
                {
                    throw new ArgumentException(
                        $"MIR object construction kind '{facts.Kind}' cannot carry constructor facts.",
                        nameof(facts));
                }

                break;

            case MidLevelIrObjectConstructionKind.PrimaryConstructor:
                if (facts.Constructor is not { IsPrimaryShape: true } || facts.ConstructorBodyKey is not null)
                {
                    throw new ArgumentException(
                        "MIR primary-constructor object construction requires a primary constructor shape and no body key.",
                        nameof(facts));
                }

                break;

            case MidLevelIrObjectConstructionKind.ExplicitConstructor:
                if (facts.Constructor is not { IsPrimaryShape: false }
                    || string.IsNullOrWhiteSpace(facts.ConstructorBodyKey)
                    || !string.Equals(facts.Constructor.BodyKey, facts.ConstructorBodyKey, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "MIR explicit-constructor object construction requires a resolved constructor body key.",
                        nameof(facts));
                }

                break;

            case MidLevelIrObjectConstructionKind.ConstructorAndInitializer:
                if (facts.Constructor is null)
                {
                    throw new ArgumentException(
                        "MIR constructor-plus-initializer object construction requires constructor facts.",
                        nameof(facts));
                }

                if (!facts.Constructor.IsPrimaryShape
                    && (string.IsNullOrWhiteSpace(facts.ConstructorBodyKey)
                        || !string.Equals(facts.Constructor.BodyKey, facts.ConstructorBodyKey, StringComparison.Ordinal)))
                {
                    throw new ArgumentException(
                        "MIR explicit constructor-plus-initializer object construction requires a resolved constructor body key.",
                        nameof(facts));
                }

                break;

            default:
                throw new ArgumentException(
                    $"Unsupported MIR object construction kind '{facts.Kind}'.",
                    nameof(facts));
        }

        foreach (var member in facts.InitializerMembers)
        {
            if (string.IsNullOrWhiteSpace(member.FieldName)
                || member.FieldIndex < 0
                || member.FieldType.Kind == StarkTypeKind.Error)
            {
                throw new ArgumentException(
                    $"MIR object construction has invalid initializer member fact '{member}'.",
                    nameof(facts));
            }
        }

        return facts.CreatedType;
    }

    public static StarkTypeSymbol ValidateEnumConstruction(
        MidLevelIrOperand value,
        MidLevelIrEnumConstructionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.Layout);
        ArgumentNullException.ThrowIfNull(facts.Variant);
        ArgumentNullException.ThrowIfNull(facts.PayloadFields);

        if (facts.EnumType.Kind != StarkTypeKind.Named || string.IsNullOrWhiteSpace(facts.EnumType.NamedType))
        {
            throw new ArgumentException(
                $"MIR enum construction requires a named enum type, but received '{facts.EnumType.DisplayName}'.",
                nameof(facts));
        }

        if (value.Type != facts.EnumType)
        {
            throw new ArgumentException(
                $"MIR enum construction value type '{value.Type.DisplayName}' does not match enum type '{facts.EnumType.DisplayName}'.",
                nameof(value));
        }

        if (facts.Layout.Kind != EnumLayoutKind.DirectTag)
        {
            throw new ArgumentException(
                $"MIR enum construction does not support layout kind '{facts.Layout.Kind}'.",
                nameof(facts));
        }

        if (!facts.Layout.TryGetVariant(facts.Variant.Name, out var layoutVariant)
            || !ReferenceEquals(layoutVariant, facts.Variant) && layoutVariant != facts.Variant)
        {
            throw new ArgumentException(
                $"MIR enum construction variant '{facts.Variant.Name}' is not present in layout '{facts.Layout.EnumName}'.",
                nameof(facts));
        }

        if (facts.PayloadFields.Count != facts.Variant.Fields.Count)
        {
            throw new ArgumentException(
                $"MIR enum construction for '{facts.Variant.Name}' carries {facts.PayloadFields.Count} payload fact(s), but the variant requires {facts.Variant.Fields.Count}.",
                nameof(facts));
        }

        for (var index = 0; index < facts.PayloadFields.Count; index++)
        {
            var payload = facts.PayloadFields[index];
            var field = facts.Variant.Fields[index];
            if (payload.FieldIndex != index
                || payload.FieldType != field.Type
                || !string.Equals(payload.StorageFieldName, field.StorageFieldName, StringComparison.Ordinal)
                || payload.StorageFieldIndex != field.StorageFieldIndex
                || payload.StorageFieldType != field.Type
                || string.IsNullOrWhiteSpace(payload.FieldName))
            {
                throw new ArgumentException(
                    $"MIR enum construction payload fact {index} does not match variant field '{field.StorageFieldName}'.",
                    nameof(facts));
            }
        }

        return facts.EnumType;
    }
}

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
    DynamicStorageAllocationKind AllocationKind,
    string Text)
    : MidLevelIrRValue(Type, Text);

public sealed record MidLevelIrDynamicStorageFreeRValue(
    MidLevelIrOperand Storage,
    string Text)
    : MidLevelIrRValue(StarkTypeSymbols.Void, Text);

public sealed record MidLevelIrHeapStorageFreeRValue(
    MidLevelIrOperand Pointer,
    string Text)
    : MidLevelIrRValue(StarkTypeSymbols.Void, Text);

public sealed record MidLevelIrDynamicStorageReserveRValue(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    MidLevelIrOperand AdditionalCapacity,
    DynamicStorageAllocationKind AllocationKind,
    string Text)
    : MidLevelIrRValue(StarkTypeSymbols.Void, Text);

public sealed record MidLevelIrDynamicStorageTryReserveRValue(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    MidLevelIrOperand AdditionalCapacity,
    DynamicStorageAllocationKind AllocationKind,
    string Text)
    : MidLevelIrRValue(StarkTypeSymbols.Bool, Text);

public sealed record MidLevelIrDynamicStorageTryReserveCapacityRValue(
    MidLevelIrOperand StorageAddress,
    StarkTypeSymbol StorageType,
    MidLevelIrOperand TargetCapacity,
    DynamicStorageAllocationKind AllocationKind,
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

public enum AliasProofCarrierKind
{
    RuntimeDisjointCondition,
    UnsafeAssumeDisjoint
}

public sealed record AliasProofCarrier(
    AliasProofCarrierKind Kind,
    string ProofId,
    IReadOnlyList<string> RootKeys,
    SourceLocation? Location = null);

public sealed record ScopedNoAliasGroup(
    string ScopeId,
    IReadOnlyList<string> RootKeys,
    AliasProofCarrier ProofCarrier);

public sealed record MidLevelIrStatement(
    MidLevelIrStatementKind Kind,
    string Text,
    string? TargetName = null,
    StarkTypeSymbol? TargetType = null,
    MidLevelIrOperand? Address = null,
    MidLevelIrRValue? Value = null,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null,
    MemoryWriteKind WriteKind = MemoryWriteKind.Replacement,
    MidLevelIrCallStatementOperation? Call = null);

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
    MidLevelIrCallStatementOperation? TailCall = null,
    IReadOnlyList<MidLevelIrSwitchCase>? SwitchCases = null,
    int? DefaultTarget = null,
    SourceLocation? Location = null,
    IReadOnlyList<int>? BranchWeights = null,
    string? LoopBehavior = null,
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
    SourceLocation? Location = null,
    IReadOnlyList<ParameterDisjointGroup>? DisjointParameterGroups = null,
    IReadOnlyList<ParameterSameGroup>? SameParameterGroups = null,
    FunctionOwnershipSummary? Ownership = null)
{
    public IReadOnlyList<ParameterDisjointGroup> DisjointGroups => DisjointParameterGroups ?? [];
    public IReadOnlyList<ParameterSameGroup> SameGroups => SameParameterGroups ?? [];
}

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

public sealed record SsaClosureValue(string InvokeFunctionName, StarkTypeSymbol Type)
    : SsaValue(Type, $"closure<{InvokeFunctionName}>");

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
    WrappingExponent,
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

public interface ISsaDirectCallOperation
{
    string FunctionName { get; }

    IReadOnlyList<SsaValue> Arguments { get; }

    StarkTypeSymbol Type { get; }

    string Text { get; }

    IReadOnlyList<string?>? IndirectArgumentLocalNames { get; }

    StarkTypeSymbol? SourceReturnType { get; }

    IReadOnlyList<SsaValue?>? IndirectArgumentAddresses { get; }
}

public interface ISsaIndirectCallOperation
{
    SsaValue Target { get; }

    IReadOnlyList<SsaValue> Arguments { get; }

    StarkTypeSymbol Type { get; }

    string Text { get; }

    StarkTypeSymbol? SourceReturnType { get; }

    IReadOnlyList<string?>? IndirectArgumentLocalNames { get; }

    IReadOnlyList<SsaValue?>? IndirectArgumentAddresses { get; }

    bool MayFree { get; }
}

public sealed record SsaCallRValue(
    string FunctionName,
    IReadOnlyList<SsaValue> Arguments,
    StarkTypeSymbol Type,
    string Text,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<SsaValue?>? IndirectArgumentAddresses = null)
    : SsaRValue(SsaArtifactValidation.ValidateValueCallType(Type, FunctionName), Text),
        ISsaDirectCallOperation;

public sealed record SsaIndirectCallRValue(
    SsaValue Target,
    IReadOnlyList<SsaValue> Arguments,
    StarkTypeSymbol Type,
    string Text,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    IReadOnlyList<SsaValue?>? IndirectArgumentAddresses = null,
    bool MayFree = false)
    : SsaRValue(SsaArtifactValidation.ValidateValueCallType(Type, Text), Text),
        ISsaIndirectCallOperation;

internal static class SsaArtifactValidation
{
    public static StarkTypeSymbol ValidateValueCallType(StarkTypeSymbol type, string callName)
    {
        if (type.Kind == StarkTypeKind.Void)
        {
            throw new ArgumentException(
                $"SSA value call '{callName}' cannot have void result type. Use a statement-only call instruction instead.",
                nameof(type));
        }

        return type;
    }

    public static StarkTypeSymbol ValidateIndexExtraction(
        SsaValue target,
        int elementIndex,
        IndexedElementOperationFamily operationFamily,
        StarkTypeSymbol resultType,
        string text)
    {
        var elementType = GetIndexElementType(target.Type, elementIndex, operationFamily, text, "SSA");
        if (!HaveSameIndexType(elementType, resultType))
        {
            throw new ArgumentException(
                $"SSA index extraction '{text}' result type '{resultType.DisplayName}' does not match indexed element type '{elementType.DisplayName}'.",
                nameof(resultType));
        }

        return resultType;
    }

    public static StarkTypeSymbol ValidateIndexInsertion(
        SsaValue target,
        int elementIndex,
        IndexedElementOperationFamily operationFamily,
        SsaValue value,
        StarkTypeSymbol resultType,
        string text)
    {
        ArgumentNullException.ThrowIfNull(value);

        var elementType = GetIndexElementType(target.Type, elementIndex, operationFamily, text, "SSA");
        if (!HaveSameIndexType(target.Type, resultType))
        {
            throw new ArgumentException(
                $"SSA index insertion '{text}' result type '{resultType.DisplayName}' does not match target type '{target.Type.DisplayName}'.",
                nameof(resultType));
        }

        if (!HaveSameIndexType(elementType, value.Type))
        {
            throw new ArgumentException(
                $"SSA index insertion '{text}' value type '{value.Type.DisplayName}' does not match indexed element type '{elementType.DisplayName}'.",
                nameof(value));
        }

        return resultType;
    }

    private static StarkTypeSymbol GetIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        IndexedElementOperationFamily operationFamily,
        string text,
        string artifactName)
    {
        return operationFamily switch
        {
            IndexedElementOperationFamily.FixedArrayElement => GetFixedArrayIndexElementType(targetType, elementIndex, text, artifactName),
            IndexedElementOperationFamily.ViewComponent => GetViewIndexElementType(targetType, elementIndex, text, artifactName),
            IndexedElementOperationFamily.ClosureComponent => GetClosureIndexElementType(targetType, elementIndex, text, artifactName),
            IndexedElementOperationFamily.DynTraitComponent => MidLevelIrArtifactValidation.GetDynTraitIndexElementType(targetType, elementIndex, text, artifactName),
            _ => throw new ArgumentException(
                $"{artifactName} index operation '{text}' uses unsupported operation family '{operationFamily}'.",
                nameof(operationFamily))
        };
    }

    private static StarkTypeSymbol GetFixedArrayIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        string text,
        string artifactName)
    {
        if (targetType.Kind != StarkTypeKind.FixedArray
            || targetType.ElementType is not { } elementType
            || targetType.FixedLength is not int fixedLength)
        {
            throw new ArgumentException(
                $"{artifactName} fixed-array index operation '{text}' requires a fixed-array target, but found '{targetType.DisplayName}'.",
                nameof(targetType));
        }

        if (elementIndex < 0 || elementIndex >= fixedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementIndex),
                $"{artifactName} fixed-array index operation '{text}' index '{elementIndex}' is out of range for '{targetType.DisplayName}' with length {fixedLength}.");
        }

        return elementType;
    }

    private static StarkTypeSymbol GetViewIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        string text,
        string artifactName)
    {
        if (elementIndex is not 0 and not 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementIndex),
                $"{artifactName} view index operation '{text}' index '{elementIndex}' is out of range for '{targetType.DisplayName}' with 2 component(s).");
        }

        return targetType.Kind switch
        {
            StarkTypeKind.Slice when targetType.ElementType is { } elementType => elementIndex == 0
                ? StarkTypeSymbols.RawPointer(elementType, isMutable: targetType.IsMutableView)
                : StarkTypeSymbols.Integer(64),
            StarkTypeKind.Ascii => elementIndex == 0
                ? StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false)
                : StarkTypeSymbols.Integer(64),
            StarkTypeKind.Unicode => elementIndex == 0
                ? StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(32), isMutable: false)
                : StarkTypeSymbols.Integer(64),
            StarkTypeKind.Slice => throw new ArgumentException(
                $"{artifactName} view index operation '{text}' requires a slice with a known element type, but found '{targetType.DisplayName}'.",
                nameof(targetType)),
            _ => throw new ArgumentException(
                $"{artifactName} view index operation '{text}' requires a slice or text target, but found '{targetType.DisplayName}'.",
                nameof(targetType))
        };
    }

    private static StarkTypeSymbol GetClosureIndexElementType(
        StarkTypeSymbol targetType,
        int elementIndex,
        string text,
        string artifactName)
    {
        if (targetType.Kind != StarkTypeKind.Closure)
        {
            throw new ArgumentException(
                $"{artifactName} closure index operation '{text}' requires a closure target, but found '{targetType.DisplayName}'.",
                nameof(targetType));
        }

        var elementType = elementIndex switch
        {
            0 => CallableValueFacts.BuildClosureInvokeFunctionPointerType(targetType),
            1 => CallableValueFacts.BuildClosureEnvironmentPointerType(targetType),
            2 when targetType.ClosureStorageKind == StarkClosureStorageKind.Heap
                => CallableValueFacts.BuildClosureDropFunctionPointerType(),
            _ => StarkTypeSymbols.Error
        };

        if (elementType.Kind == StarkTypeKind.Error)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementIndex),
                $"{artifactName} closure index operation '{text}' index '{elementIndex}' is out of range or unresolved for closure value '{targetType.DisplayName}'.");
        }

        return elementType;
    }

    private static bool HaveSameIndexType(StarkTypeSymbol expectedType, StarkTypeSymbol actualType)
    {
        return expectedType == actualType
            || expectedType.Kind == actualType.Kind
            && string.Equals(expectedType.DisplayName, actualType.DisplayName, StringComparison.Ordinal);
    }
}

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
    IndexedElementOperationFamily OperationFamily,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(SsaArtifactValidation.ValidateIndexExtraction(Target, ElementIndex, OperationFamily, Type, Text), Text);

public sealed record SsaInsertIndexRValue(
    SsaValue Target,
    int ElementIndex,
    IndexedElementOperationFamily OperationFamily,
    SsaValue Value,
    StarkTypeSymbol Type,
    string Text)
    : SsaRValue(SsaArtifactValidation.ValidateIndexInsertion(Target, ElementIndex, OperationFamily, Value, Type, Text), Text);

// Loads a method slot from a `dyn Trait` vtable (see MidLevelIrDynVTableSlotRValue).
// The result is the slot's `fnptr<kind ...>`; the devirtualizer can recover a
// direct call when the vtable traces back to a known concrete table.
public sealed record SsaDynVTableSlotRValue(
    SsaValue VtablePointer,
    int SlotIndex,
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
    DynamicStorageAllocationKind AllocationKind,
    string Text)
    : SsaRValue(Type, Text);

public sealed record SsaDynamicStorageFreeRValue(
    SsaValue Storage,
    string Text)
    : SsaRValue(StarkTypeSymbols.Void, Text);

public sealed record SsaHeapStorageFreeRValue(
    SsaValue Pointer,
    string Text)
    : SsaRValue(StarkTypeSymbols.Void, Text);

public sealed record SsaDynamicStorageReserveRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    SsaValue AdditionalCapacity,
    DynamicStorageAllocationKind AllocationKind,
    string Text)
    : SsaRValue(StarkTypeSymbols.Void, Text);

public sealed record SsaDynamicStorageTryReserveRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    SsaValue AdditionalCapacity,
    DynamicStorageAllocationKind AllocationKind,
    string Text)
    : SsaRValue(StarkTypeSymbols.Bool, Text);

public sealed record SsaDynamicStorageTryReserveCapacityRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    SsaValue TargetCapacity,
    DynamicStorageAllocationKind AllocationKind,
    string Text)
    : SsaRValue(StarkTypeSymbols.Bool, Text);

public sealed record SsaDynamicStorageMoveLastRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    StarkTypeSymbol Type,
    string Text,
    bool IsKnownNonEmpty = false)
    : SsaRValue(Type, Text);

public sealed record SsaDynamicStorageMoveAtRValue(
    SsaValue StorageAddress,
    StarkTypeSymbol StorageType,
    SsaValue Index,
    StarkTypeSymbol Type,
    string Text,
    bool IsKnownInBounds = false)
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

public sealed record SsaCallInstruction(
    string FunctionName,
    IReadOnlyList<SsaValue> Arguments,
    StarkTypeSymbol Type,
    string Text,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<SsaValue?>? IndirectArgumentAddresses = null,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null)
    : SsaInstruction,
        ISsaDirectCallOperation;

public sealed record SsaIndirectCallInstruction(
    SsaValue Target,
    IReadOnlyList<SsaValue> Arguments,
    StarkTypeSymbol Type,
    string Text,
    StarkTypeSymbol? SourceReturnType = null,
    IReadOnlyList<string?>? IndirectArgumentLocalNames = null,
    IReadOnlyList<SsaValue?>? IndirectArgumentAddresses = null,
    bool MayFree = false,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null)
    : SsaInstruction,
        ISsaIndirectCallOperation;

public sealed record SsaAllocateLocalInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    string StorageClass = "stack",
    SourceLocation? Location = null,
    bool IsImmutable = false,
    bool HasConstProvenance = false,
    ConstProvenanceKind ConstProvenance = ConstProvenanceKind.None)
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

public sealed record SsaArenaFrameEnterInstruction(SourceLocation? Location = null)
    : SsaInstruction;

public sealed record SsaArenaFrameLeaveInstruction(SourceLocation? Location = null)
    : SsaInstruction;

public sealed record SsaStoreLocalInstruction(
    string LocalName,
    StarkTypeSymbol LocalType,
    SsaValue Value,
    SourceLocation? Location = null,
    MemoryWriteKind WriteKind = MemoryWriteKind.Replacement)
    : SsaInstruction;

public sealed record SsaStoreIndirectInstruction(
    SsaValue Address,
    StarkTypeSymbol ValueType,
    SsaValue Value,
    SourceLocation? Location = null,
    IReadOnlyList<ScopedNoAliasGroup>? ScopedNoAliasGroups = null,
    IReadOnlyList<string>? LoopAccessGroups = null,
    MemoryWriteKind WriteKind = MemoryWriteKind.Replacement)
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
    IReadOnlyList<string>? LoopAccessGroups = null,
    MemoryWriteKind WriteKind = MemoryWriteKind.Replacement)
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
    TailCall,
    Unreachable
}

public sealed record SsaTerminator(
    SsaTerminatorKind Kind,
    IReadOnlyList<int> Targets,
    SsaValue? Condition = null,
    SsaValue? Value = null,
    ISsaDirectCallOperation? TailDirectCall = null,
    ISsaIndirectCallOperation? TailIndirectCall = null,
    IReadOnlyList<SsaSwitchCase>? SwitchCases = null,
    int? DefaultTarget = null,
    SourceLocation? Location = null,
    IReadOnlyList<int>? BranchWeights = null,
    string? LoopBehavior = null,
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
    SourceLocation? Location = null,
    IReadOnlyList<ParameterDisjointGroup>? DisjointParameterGroups = null,
    IReadOnlyList<ParameterSameGroup>? SameParameterGroups = null,
    FunctionOwnershipSummary? Ownership = null)
{
    public IReadOnlyList<ParameterDisjointGroup> DisjointGroups => DisjointParameterGroups ?? [];
    public IReadOnlyList<ParameterSameGroup> SameGroups => SameParameterGroups ?? [];
}

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

public sealed record SsaBoundedRawPointerRegionFact(
    SsaValue ElementCount,
    SsaIntegerRangeFact? ElementCountRange = null,
    int? ElementAlignmentBytes = null);

public enum SsaDynamicStorageBackingAllocationKind
{
    Unknown,
    None,
    RuntimeAllocation,
    ArenaAllocation
}

public enum SsaDynamicStorageAllocatorProvenanceKind
{
    Unknown,
    RuntimeDefault,
    ArenaFrame
}

public enum DynamicStorageAllocationKind
{
    Runtime,
    Arena
}

public sealed record SsaDynamicStorageRegionFact(
    string? OwnerRootName,
    SsaDynamicStorageBackingAllocationKind BackingAllocationKind,
    string? BackingAllocationId,
    StarkTypeSymbol ElementType,
    SsaIntegerRangeFact? CapacityRange,
    SsaIntegerRangeFact? InitializedLengthRange,
    SsaIntegerRangeFact? InitializedPrefixRange,
    SsaIntegerRangeFact? SpareCapacityRange,
    int? ElementAlignmentBytes,
    SsaDynamicStorageAllocatorProvenanceKind AllocatorProvenance);

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
    SsaFactLatticeKind CapacityKind = SsaFactLatticeKind.Unknown,
    SsaIntegerRangeFact? CapacityRange = null,
    SsaFactLatticeKind InitializedPrefixKind = SsaFactLatticeKind.Unknown,
    SsaIntegerRangeFact? InitializedPrefixRange = null,
    SsaFactLatticeKind TextLiteralPayloadKind = SsaFactLatticeKind.Unknown,
    SsaTextLiteralPayloadFact? TextLiteralPayload = null,
    SsaFactLatticeKind BoundedRawPointerRegionKind = SsaFactLatticeKind.Unknown,
    SsaBoundedRawPointerRegionFact? BoundedRawPointerRegion = null,
    SsaFactLatticeKind DynamicStorageRegionKind = SsaFactLatticeKind.Unknown,
    SsaDynamicStorageRegionFact? DynamicStorageRegion = null);

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
