using System.Numerics;

namespace Stark.Compiler;

internal static partial class PackageImageBuilder
{
    private static StarkPackageTypeReference BuildPublishedAbiTypeReference(StarkTypeSymbol type, LoadedModuleDocument module)
    {
        return BuildPublishedAbiTypeReference(type, module.SyntaxModel.ModuleName, GetModuleLocalNamedTypes(module));
    }

    private static StarkPackageTypeReference BuildPublishedAbiTypeReference(
        StarkTypeSymbol type,
        string moduleName,
        ISet<string> localNamedTypes)
    {
        var normalizedNamedType = type.NamedType is null && type.DynTraitName is null
            ? null
            : QualifyModuleLocalNamedType(type, moduleName, localNamedTypes);
        var callableFunctionKind = GetPackageCallableFunctionKind(type);
        var callableReturnType = GetCallableReturnType(type);
        var callableParameterTypes = GetCallableParameterTypes(type);
        return new StarkPackageTypeReference(
            type.Kind.ToString().ToLowerInvariant(),
            Name: normalizedNamedType,
            BitWidth: type.BitWidth,
            RangeMin: type.RangeMin?.ToString(),
            RangeMax: type.RangeMax?.ToString(),
            IsUnsigned: type.IsUnsigned ? true : null,
            IsMutablePointer: type.IsMutablePointer,
            BorrowKind: type.BorrowKind == StarkBorrowKind.None ? null : type.BorrowKind.ToString().ToLowerInvariant(),
            AccessKind: type.AccessKind == StarkAccessKind.None ? null : type.AccessKind.ToString().ToLowerInvariant(),
            InitializationKind: type.InitializationKind == StarkInitializationKind.None ? null : type.InitializationKind.ToString().ToLowerInvariant(),
            IsMutableView: type.IsMutableView,
            FixedLength: type.FixedLength,
            FixedLengthParameterName: type.FixedLengthParameterName,
            ElementType: type.ElementType is null ? null : BuildPublishedAbiTypeReference(type.ElementType, moduleName, localNamedTypes),
            TypeArguments: type.TypeArguments is { Count: > 0 }
                ? type.TypeArguments.Select(argument => BuildPublishedAbiTypeReference(argument, moduleName, localNamedTypes)).ToArray()
                : null,
            ComptimeValueArguments: type.ComptimeValueArguments is { Count: > 0 }
                ? type.ComptimeValueArguments.Select(argument => BuildPublishedAbiComptimeValueArgument(argument, moduleName, localNamedTypes)).ToArray()
                : null,
            FunctionKind: callableFunctionKind is null ? null : RenderPackageFunctionKind(callableFunctionKind.Value),
            FunctionAbi: type.Kind == StarkTypeKind.FunctionPointer && type.FunctionPointerAbi is { } functionPointerAbi
                ? StarkFfiAbiFacts.DisplayName(functionPointerAbi)
                : null,
            FunctionIsUnsafe: type.Kind == StarkTypeKind.FunctionPointer && type.FunctionPointerIsUnsafe ? true : null,
            ClosureStorageKind: type.Kind == StarkTypeKind.Closure ? RenderPackageClosureStorageKind(type.ClosureStorageKind) : null,
            ClosureCallCapability: type.Kind == StarkTypeKind.Closure ? RenderPackageClosureCallCapability(type.ClosureCallCapability) : null,
            DynTraitStorageKind: type.Kind == StarkTypeKind.DynTrait ? RenderPackageDynTraitStorageKind(type.DynTraitStorageKind) : null,
            ReturnType: callableReturnType is null
                ? null
                : BuildPublishedAbiTypeReference(callableReturnType, moduleName, localNamedTypes),
            ParameterTypes: callableParameterTypes is { Count: > 0 }
                ? callableParameterTypes.Select(parameter => BuildPublishedAbiTypeReference(parameter, moduleName, localNamedTypes)).ToArray()
                : null,
            ParameterRawPointerElementCountExpressions: GetCallableRawPointerElementCountExpressions(type),
            OverlapParameterGroups: BuildParameterOverlapGroupManifests(GetCallableOverlapParameterGroups(type)),
            SameParameterGroups: BuildParameterSameGroupManifests(GetCallableSameParameterGroups(type)),
            AssociatedOwnerType: type.AssociatedTypeOwner is null
                ? null
                : BuildPublishedAbiTypeReference(type.AssociatedTypeOwner, moduleName, localNamedTypes),
            AssociatedTypeName: type.AssociatedTypeName,
            SourceAliasName: type.CSourceAliasName);
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
        var normalizedNamedType = type.NamedType is null && type.DynTraitName is null
            ? null
            : NormalizeNamedType(type, moduleName, stripCurrentModulePrefix);
        var callableFunctionKind = GetPackageCallableFunctionKind(type);
        var callableReturnType = GetCallableReturnType(type);
        var callableParameterTypes = GetCallableParameterTypes(type);
        return new StarkPackageTypeReference(
            type.Kind.ToString().ToLowerInvariant(),
            Name: normalizedNamedType,
            BitWidth: type.BitWidth,
            RangeMin: type.RangeMin?.ToString(),
            RangeMax: type.RangeMax?.ToString(),
            IsUnsigned: type.IsUnsigned ? true : null,
            IsMutablePointer: type.IsMutablePointer,
            BorrowKind: type.BorrowKind == StarkBorrowKind.None ? null : type.BorrowKind.ToString().ToLowerInvariant(),
            AccessKind: type.AccessKind == StarkAccessKind.None ? null : type.AccessKind.ToString().ToLowerInvariant(),
            InitializationKind: type.InitializationKind == StarkInitializationKind.None ? null : type.InitializationKind.ToString().ToLowerInvariant(),
            IsMutableView: type.IsMutableView,
            FixedLength: type.FixedLength,
            FixedLengthParameterName: type.FixedLengthParameterName,
            ElementType: type.ElementType is null ? null : BuildTypeReference(type.ElementType, moduleName, stripCurrentModulePrefix),
            TypeArguments: type.TypeArguments is { Count: > 0 }
                ? type.TypeArguments.Select(argument => BuildTypeReference(argument, moduleName, stripCurrentModulePrefix)).ToArray()
                : null,
            ComptimeValueArguments: type.ComptimeValueArguments is { Count: > 0 }
                ? type.ComptimeValueArguments.Select(argument => BuildComptimeValueArgument(argument, moduleName, stripCurrentModulePrefix)).ToArray()
                : null,
            FunctionKind: callableFunctionKind is null ? null : RenderPackageFunctionKind(callableFunctionKind.Value),
            FunctionAbi: type.Kind == StarkTypeKind.FunctionPointer && type.FunctionPointerAbi is { } functionPointerAbi
                ? StarkFfiAbiFacts.DisplayName(functionPointerAbi)
                : null,
            FunctionIsUnsafe: type.Kind == StarkTypeKind.FunctionPointer && type.FunctionPointerIsUnsafe ? true : null,
            ClosureStorageKind: type.Kind == StarkTypeKind.Closure ? RenderPackageClosureStorageKind(type.ClosureStorageKind) : null,
            ClosureCallCapability: type.Kind == StarkTypeKind.Closure ? RenderPackageClosureCallCapability(type.ClosureCallCapability) : null,
            DynTraitStorageKind: type.Kind == StarkTypeKind.DynTrait ? RenderPackageDynTraitStorageKind(type.DynTraitStorageKind) : null,
            ReturnType: callableReturnType is null
                ? null
                : BuildTypeReference(callableReturnType, moduleName, stripCurrentModulePrefix),
            ParameterTypes: callableParameterTypes is { Count: > 0 }
                ? callableParameterTypes.Select(parameter => BuildTypeReference(parameter, moduleName, stripCurrentModulePrefix)).ToArray()
                : null,
            ParameterRawPointerElementCountExpressions: GetCallableRawPointerElementCountExpressions(type),
            OverlapParameterGroups: BuildParameterOverlapGroupManifests(GetCallableOverlapParameterGroups(type)),
            SameParameterGroups: BuildParameterSameGroupManifests(GetCallableSameParameterGroups(type)),
            AssociatedOwnerType: type.AssociatedTypeOwner is null
                ? null
                : BuildTypeReference(type.AssociatedTypeOwner, moduleName, stripCurrentModulePrefix),
            AssociatedTypeName: type.AssociatedTypeName,
            SourceAliasName: type.CSourceAliasName);
    }

    private static StarkPackageComptimeValueArgumentManifest BuildPublishedAbiComptimeValueArgument(
        ComptimeValueArgumentSymbol argument,
        string moduleName,
        ISet<string> localNamedTypes)
    {
        return new StarkPackageComptimeValueArgumentManifest(
            argument.ParameterName,
            argument.IntegerValue.ToString(),
            BuildPublishedAbiTypeReference(argument.Type, moduleName, localNamedTypes),
            argument.IsSymbolic,
            argument.SymbolicSourceName);
    }

    private static StarkPackageComptimeValueArgumentManifest BuildComptimeValueArgument(
        ComptimeValueArgumentSymbol argument,
        string moduleName,
        bool stripCurrentModulePrefix)
    {
        return new StarkPackageComptimeValueArgumentManifest(
            argument.ParameterName,
            argument.IntegerValue.ToString(),
            BuildTypeReference(argument.Type, moduleName, stripCurrentModulePrefix),
            argument.IsSymbolic,
            argument.SymbolicSourceName);
    }

    private static IReadOnlyList<StarkPackageComptimeGenericParameterManifest>? BuildComptimeGenericParameterManifests(
        IReadOnlyList<ComptimeGenericParameterSymbol> parameters,
        string moduleName,
        bool stripCurrentModulePrefix = true)
    {
        if (parameters.Count == 0)
        {
            return null;
        }

        return parameters
            .Select(parameter => new StarkPackageComptimeGenericParameterManifest(
                parameter.Name,
                BuildTypeReference(parameter.Type, moduleName, stripCurrentModulePrefix)))
            .ToArray();
    }

    private static StarkFunctionKind? GetPackageCallableFunctionKind(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.FunctionPointer => type.FunctionPointerKind,
            StarkTypeKind.Closure => type.ClosureFunctionKind,
            _ => null
        };
    }

    private static StarkTypeSymbol? GetCallableReturnType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.FunctionPointer => type.FunctionPointerReturnType,
            StarkTypeKind.Closure => type.ClosureReturnType,
            _ => null
        };
    }

    private static IReadOnlyList<StarkTypeSymbol>? GetCallableParameterTypes(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.FunctionPointer => type.FunctionPointerParameterTypes,
            StarkTypeKind.Closure => type.ClosureParameterTypes,
            _ => null
        };
    }

    private static IReadOnlyList<string?>? GetCallableRawPointerElementCountExpressions(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.FunctionPointer => type.FunctionPointerParameterRawPointerElementCountExpressions,
            StarkTypeKind.Closure => type.ClosureParameterRawPointerElementCountExpressions,
            _ => null
        };
    }

    private static IReadOnlyList<ParameterOverlapGroup> GetCallableOverlapParameterGroups(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.FunctionPointer => type.FunctionPointerOverlapParameterGroups ?? [],
            StarkTypeKind.Closure => type.ClosureOverlapParameterGroups ?? [],
            _ => []
        };
    }

    private static IReadOnlyList<ParameterSameGroup> GetCallableSameParameterGroups(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.FunctionPointer => type.FunctionPointerSameParameterGroups ?? [],
            StarkTypeKind.Closure => type.ClosureSameParameterGroups ?? [],
            _ => []
        };
    }

    private static string RenderPackageFunctionKind(StarkFunctionKind kind)
    {
        return kind switch
        {
            StarkFunctionKind.Finite => "finite",
            StarkFunctionKind.Law => "law",
            StarkFunctionKind.FiniteLaw => "finite law",
            _ => "fn"
        };
    }

    private static string? RenderPackageClosureStorageKind(StarkClosureStorageKind storageKind)
    {
        return storageKind switch
        {
            StarkClosureStorageKind.Inline => "inline",
            StarkClosureStorageKind.Heap => "heap",
            _ => null
        };
    }

    private static string? RenderPackageClosureCallCapability(StarkClosureCallCapability callCapability)
    {
        return callCapability switch
        {
            StarkClosureCallCapability.Mut => "mut",
            StarkClosureCallCapability.Once => "once",
            _ => null
        };
    }

    private static string RenderPackageDynTraitStorageKind(StarkDynTraitStorageKind storageKind)
    {
        return storageKind switch
        {
            StarkDynTraitStorageKind.Heap => "heap",
            _ => "view"
        };
    }

    private static string NormalizeNamedType(StarkTypeSymbol type, string moduleName, bool stripCurrentModulePrefix)
    {
        var name = type.Kind == StarkTypeKind.DynTrait
            ? type.DynTraitName!
            : StarkTypeSymbols.IsGenericInstantiation(type)
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
        var name = type.Kind == StarkTypeKind.DynTrait
            ? type.DynTraitName!
            : StarkTypeSymbols.IsGenericInstantiation(type)
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

internal static partial class PackageImageLoader
{
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

    private static StarkTypeSymbol BuildTypeSymbol(StarkPackageTypeReference type)
    {
        return BuildTypeSymbol(type, currentModuleName: null, localNamedTypes: null);
    }

    private static StarkTypeSymbol BuildTypeSymbol(
        StarkPackageTypeReference type,
        string? currentModuleName,
        ISet<string>? localNamedTypes)
    {
        var normalizedNamedType = type.Name is null
            ? null
            : QualifyLoadedNamedType(type.Name, currentModuleName, localNamedTypes);
        StarkTypeSymbol core = type.Kind switch
        {
            "error" => StarkTypeSymbols.Error,
            "void" => StarkTypeSymbols.Void,
            "bool" => StarkTypeSymbols.Bool,
            "ascii" => StarkTypeSymbols.Ascii,
            "unicode" => StarkTypeSymbols.Unicode,
            "cvoid" => StarkTypeSymbols.CVoid,
            "null" => StarkTypeSymbols.Null,
            "integer" => StarkTypeSymbols.Integer(
                type.BitWidth ?? 32,
                type.RangeMin is null ? null : BigInteger.Parse(type.RangeMin, System.Globalization.CultureInfo.InvariantCulture),
                type.RangeMax is null ? null : BigInteger.Parse(type.RangeMax, System.Globalization.CultureInfo.InvariantCulture),
                type.IsUnsigned == true),
            "float" => StarkTypeSymbols.Float(type.BitWidth ?? 32),
            "rawpointer" => StarkTypeSymbols.RawPointer(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes), type.IsMutablePointer),
            "fixedarray" => StarkTypeSymbols.FixedArray(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes), type.FixedLength, type.FixedLengthParameterName),
            "slice" => StarkTypeSymbols.Slice(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes)),
            "dynamic" => StarkTypeSymbols.Dynamic(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes)),
            "functionpointer" when type.ReturnType is not null => StarkTypeSymbols.FunctionPointer(
                ParsePackageFunctionKind(type.FunctionKind),
                BuildTypeSymbol(type.ReturnType, currentModuleName, localNamedTypes),
                (type.ParameterTypes ?? []).Select(parameter => BuildTypeSymbol(parameter, currentModuleName, localNamedTypes)).ToArray(),
                BuildTypeReferenceParameterDisjointGroups(type.DisjointParameterGroups),
                BuildParameterOverlapGroups(type.OverlapParameterGroups),
                BuildParameterSameGroups(type.SameParameterGroups),
                type.ParameterRawPointerElementCountExpressions,
                ParsePackageFfiAbi(type.FunctionAbi),
                type.FunctionIsUnsafe == true),
            "closure" when type.ReturnType is not null => StarkTypeSymbols.Closure(
                ParsePackageClosureStorageKind(type.ClosureStorageKind),
                ParsePackageClosureCallCapability(type.ClosureCallCapability),
                ParsePackageFunctionKind(type.FunctionKind),
                BuildTypeSymbol(type.ReturnType, currentModuleName, localNamedTypes),
                (type.ParameterTypes ?? []).Select(parameter => BuildTypeSymbol(parameter, currentModuleName, localNamedTypes)).ToArray(),
                BuildTypeReferenceParameterDisjointGroups(type.DisjointParameterGroups),
                BuildParameterOverlapGroups(type.OverlapParameterGroups),
                BuildParameterSameGroups(type.SameParameterGroups),
                type.ParameterRawPointerElementCountExpressions),
            "dyntrait" => StarkTypeSymbols.DynTrait(
                normalizedNamedType ?? "<unnamed>",
                ParsePackageDynTraitStorageKind(type.DynTraitStorageKind),
                (type.TypeArguments ?? []).Select(argument => BuildTypeSymbol(argument, currentModuleName, localNamedTypes)).ToArray()),
            "named" when type.TypeArguments is { Count: > 0 } || type.ComptimeValueArguments is { Count: > 0 } => StarkTypeSymbols.GenericInstantiation(
                normalizedNamedType ?? "<unnamed>",
                (type.TypeArguments ?? []).Select(argument => BuildTypeSymbol(argument, currentModuleName, localNamedTypes)).ToArray(),
                BuildComptimeValueArgumentSymbols(type.ComptimeValueArguments, currentModuleName, localNamedTypes)),
            "named" => StarkTypeSymbols.Named(normalizedNamedType ?? "<unnamed>"),
            "associatedtype" when type.AssociatedOwnerType is not null
                                    && type.AssociatedTypeName is not null
                => StarkTypeSymbols.AssociatedType(
                    BuildTypeSymbol(type.AssociatedOwnerType, currentModuleName, localNamedTypes),
                    type.AssociatedTypeName),
            _ => StarkTypeSymbols.Error
        };

        core = StarkTypeSymbols.WithCSourceAlias(core, type.SourceAliasName);

        return StarkTypeSymbols.ApplyQualifiers(
            core,
            borrowKind: ParseBorrowKind(type.BorrowKind),
            accessKind: ParseAccessKind(type.AccessKind),
            initializationKind: ParseInitializationKind(type.InitializationKind),
            isMutableView: type.IsMutableView);
    }

    private static IReadOnlyList<ComptimeValueArgumentSymbol>? BuildComptimeValueArgumentSymbols(
        IReadOnlyList<StarkPackageComptimeValueArgumentManifest>? arguments,
        string? currentModuleName,
        ISet<string>? localNamedTypes)
    {
        if (arguments is not { Count: > 0 })
        {
            return null;
        }

        return arguments
            .Select(argument => new ComptimeValueArgumentSymbol(
                argument.ParameterName,
                BigInteger.Parse(argument.IntegerValue),
                BuildTypeSymbol(argument.Type, currentModuleName, localNamedTypes),
                argument.IsSymbolic,
                argument.SymbolicSourceName))
            .ToArray();
    }

    private static IReadOnlyList<ComptimeGenericParameterSymbol>? BuildComptimeGenericParameterSymbols(
        IReadOnlyList<StarkPackageComptimeGenericParameterManifest>? parameters,
        string? currentModuleName,
        ISet<string>? localNamedTypes)
    {
        if (parameters is not { Count: > 0 })
        {
            return null;
        }

        return parameters
            .Select(parameter => new ComptimeGenericParameterSymbol(
                parameter.Name,
                BuildTypeSymbol(parameter.Type, currentModuleName, localNamedTypes)))
            .ToArray();
    }

    private static StarkFunctionKind ParsePackageFunctionKind(string? functionKind)
    {
        return functionKind switch
        {
            "finite" => StarkFunctionKind.Finite,
            "law" => StarkFunctionKind.Law,
            "finite law" or "finitelaw" => StarkFunctionKind.FiniteLaw,
            _ => StarkFunctionKind.Fn
        };
    }

    private static StarkFfiAbi? ParsePackageFfiAbi(string? functionAbi)
    {
        if (string.IsNullOrWhiteSpace(functionAbi))
        {
            return null;
        }

        return StarkFfiAbiFacts.TryParse(functionAbi, out var abi)
            ? abi
            : null;
    }

    private static StarkClosureStorageKind ParsePackageClosureStorageKind(string? storageKind)
    {
        return storageKind switch
        {
            "inline" => StarkClosureStorageKind.Inline,
            "heap" => StarkClosureStorageKind.Heap,
            _ => StarkClosureStorageKind.Unspecified
        };
    }

    private static StarkClosureCallCapability ParsePackageClosureCallCapability(string? callCapability)
    {
        return callCapability switch
        {
            "mut" => StarkClosureCallCapability.Mut,
            "once" => StarkClosureCallCapability.Once,
            _ => StarkClosureCallCapability.None
        };
    }

    private static StarkDynTraitStorageKind ParsePackageDynTraitStorageKind(string? storageKind)
    {
        return storageKind switch
        {
            "heap" => StarkDynTraitStorageKind.Heap,
            _ => StarkDynTraitStorageKind.View
        };
    }

    private static string QualifyLoadedNamedType(
        string name,
        string? currentModuleName,
        ISet<string>? localNamedTypes)
    {
        if (string.IsNullOrWhiteSpace(currentModuleName)
            || localNamedTypes is null
            || name.Contains('.', StringComparison.Ordinal)
            || !localNamedTypes.Contains(name))
        {
            return name;
        }

        return $"{currentModuleName}.{name}";
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

        var core = !string.IsNullOrWhiteSpace(type.SourceAliasName)
            ? type.SourceAliasName
            : type.Kind switch
        {
            "error" => "<error>",
            "void" => "void",
            "bool" => "bool",
            "ascii" => "ascii",
            "unicode" => "unicode",
            "cvoid" => "System.C.c_void",
            "null" => "null",
            "integer" => PackageImageIntegerTypeText.Render(type.BitWidth, type.RangeMin, type.RangeMax, type.IsUnsigned == true),
            "float" => $"f{type.BitWidth}",
            "rawpointer" => $"{(type.IsMutablePointer ? "rawmutptr" : "rawptr")}<{RenderTypeReference(type.ElementType!)}>",
            "fixedarray" => $"{RenderTypeReference(type.ElementType!)}[{(type.FixedLength is { } fixedLength ? fixedLength.ToString() : type.FixedLengthParameterName ?? "?")}]",
            "slice" => $"{RenderTypeReference(type.ElementType!)}[]",
            "dynamic" => $"dynamic {RenderTypeReference(type.ElementType!)}",
            "functionpointer" => $"fnptr<{RenderTypeReferenceFunctionSafetyPrefix(type.FunctionIsUnsafe)}{RenderTypeReferenceFunctionAbi(type.FunctionAbi)}{RenderTypeReferenceFunctionKind(type.FunctionKind)} {RenderTypeReference(type.ReturnType!)}({string.Join(", ", (type.ParameterTypes ?? []).Select((parameter, index) => RenderFunctionPointerParameterTypeReference(parameter, type.ParameterRawPointerElementCountExpressions, index)))}){RenderFunctionPointerMemoryContracts(type)}>",
            "closure" => $"{RenderClosureStoragePrefix(type.ClosureStorageKind)}closure<{RenderClosureCallCapabilityPrefix(type.ClosureCallCapability)}{RenderTypeReferenceFunctionKind(type.FunctionKind)} {RenderTypeReference(type.ReturnType!)}({string.Join(", ", (type.ParameterTypes ?? []).Select((parameter, index) => RenderFunctionPointerParameterTypeReference(parameter, type.ParameterRawPointerElementCountExpressions, index)))}){RenderFunctionPointerMemoryContracts(type)}>",
            "dyntrait" when type.TypeArguments is { Count: > 0 } || type.ComptimeValueArguments is { Count: > 0 }
                => $"{RenderDynTraitStoragePrefix(type.DynTraitStorageKind)}dyn {type.Name}<{RenderTypeReferenceGenericArguments(type)}>",
            "dyntrait" => $"{RenderDynTraitStoragePrefix(type.DynTraitStorageKind)}dyn {type.Name ?? "<unnamed>"}",
            "named" when type.TypeArguments is { Count: > 0 } || type.ComptimeValueArguments is { Count: > 0 }
                => $"{type.Name}<{RenderTypeReferenceGenericArguments(type)}>",
            "named" => type.Name ?? "<unnamed>",
            "associatedtype" when type.AssociatedOwnerType is not null
                                    && type.AssociatedTypeName is not null
                => $"{RenderTypeReference(type.AssociatedOwnerType)}.{type.AssociatedTypeName}",
            _ => type.Name ?? type.Kind
        };

        return qualifiers.Count == 0
            ? core
            : $"{string.Join(" ", qualifiers)} {core}";
    }

    private static string RenderTypeReferenceGenericArguments(StarkPackageTypeReference type)
    {
        var parts = (type.TypeArguments ?? [])
            .Select(RenderTypeReference)
            .Concat((type.ComptimeValueArguments ?? []).Select(static argument => argument.IsSymbolic
                ? $"comptime {argument.SymbolicSourceName ?? argument.ParameterName}"
                : argument.IntegerValue))
            .ToArray();
        return string.Join(", ", parts);
    }

    private static string RenderTypeReferenceFunctionKind(string? functionKind)
    {
        return functionKind switch
        {
            "finite" => "finite",
            "law" => "law",
            "finite law" or "finitelaw" => "finite law",
            _ => "fn"
        };
    }

    private static string RenderTypeReferenceFunctionAbi(string? functionAbi)
    {
        return string.IsNullOrWhiteSpace(functionAbi)
            ? string.Empty
            : $"ffi({functionAbi}) ";
    }

    private static string RenderTypeReferenceFunctionSafetyPrefix(bool? isUnsafe)
    {
        return isUnsafe == true ? "unsafe " : string.Empty;
    }

    private static string RenderClosureStoragePrefix(string? storageKind)
    {
        return storageKind switch
        {
            "inline" => "inline ",
            "heap" => "heap ",
            _ => string.Empty
        };
    }

    private static string RenderDynTraitStoragePrefix(string? storageKind)
    {
        return string.Equals(storageKind, "heap", StringComparison.Ordinal)
            ? "heap "
            : string.Empty;
    }

    private static string RenderClosureCallCapabilityPrefix(string? callCapability)
    {
        return callCapability switch
        {
            "mut" => "mut ",
            "once" => "once ",
            _ => string.Empty
        };
    }

    private static string RenderFunctionPointerParameterTypeReference(
        StarkPackageTypeReference parameterType,
        IReadOnlyList<string?>? rawPointerElementCountExpressions,
        int parameterIndex)
    {
        var typeText = RenderTypeReference(parameterType);
        return rawPointerElementCountExpressions is not null
               && parameterIndex >= 0
               && parameterIndex < rawPointerElementCountExpressions.Count
               && !string.IsNullOrWhiteSpace(rawPointerElementCountExpressions[parameterIndex])
            ? $"{typeText}[{rawPointerElementCountExpressions[parameterIndex]}]"
            : typeText;
    }

    private static string RenderFunctionPointerMemoryContracts(StarkPackageTypeReference type)
    {
        var clauses = new List<string>();
        AppendFunctionPointerMemoryContractClauses(clauses, "overlap", type.OverlapParameterGroups);
        AppendFunctionPointerMemoryContractClauses(clauses, "same", type.SameParameterGroups);
        return clauses.Count == 0
            ? string.Empty
            : $" where {string.Join(", ", clauses)}";
    }

    private static void AppendFunctionPointerMemoryContractClauses(
        List<string> clauses,
        string relationName,
        IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? groups)
    {
        foreach (var group in groups ?? [])
        {
            var names = group.ParameterNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (names.Length >= 2)
            {
                clauses.Add($"{relationName}({string.Join(", ", names)})");
            }
        }
    }

    private static IReadOnlyList<ParameterDisjointGroup>? BuildTypeReferenceParameterDisjointGroups(
        IReadOnlyList<StarkPackageParameterDisjointGroupManifest>? groups)
    {
        var result = groups?
            .Select(static group =>
            {
                if (group.Regions is { Count: >= 2 } regions)
                {
                    var memoryRegions = regions
                        .Where(static region => !string.IsNullOrWhiteSpace(region.ParameterName))
                        .Select(static region => new ParameterMemoryRegion(
                            region.ParameterName,
                            region.StartExpression,
                            region.CountExpression))
                        .ToArray();
                    var regionNames = memoryRegions
                        .Select(static region => region.ParameterName)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    return memoryRegions.Length >= 2
                        ? new ParameterDisjointGroup(regionNames, memoryRegions)
                        : null;
                }

                var names = group.ParameterNames
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return names.Length >= 2 ? new ParameterDisjointGroup(names) : null;
            })
            .Where(static group => group is not null)
            .Cast<ParameterDisjointGroup>()
            .ToArray();
        return result is { Length: > 0 } ? result : null;
    }
}

file static class PackageImageIntegerTypeText
{
    public static string Render(int? bitWidth, string? rangeMin, string? rangeMax, bool isUnsigned)
    {
        var normalizedBitWidth = bitWidth ?? 32;
        var prefix = isUnsigned ? "u" : "i";
        if (normalizedBitWidth <= 0)
        {
            return $"{prefix}{normalizedBitWidth}";
        }

        var min = isUnsigned ? BigInteger.Zero : -(BigInteger.One << (normalizedBitWidth - 1));
        var max = isUnsigned
            ? (BigInteger.One << normalizedBitWidth) - BigInteger.One
            : (BigInteger.One << (normalizedBitWidth - 1)) - BigInteger.One;
        if (rangeMin is null && rangeMax is null)
        {
            return isUnsigned
                ? $"{prefix}{normalizedBitWidth}[0 max]"
                : $"{prefix}{normalizedBitWidth}[min max]";
        }

        if (rangeMin is not null && rangeMax is not null)
        {
            var renderedMin = RenderEndpoint(rangeMin, min, isLowerEndpoint: true, isUnsigned);
            var renderedMax = RenderEndpoint(rangeMax, max, isLowerEndpoint: false, isUnsigned);
            return $"{prefix}{normalizedBitWidth}[{renderedMin} {renderedMax}]";
        }

        return $"{prefix}{normalizedBitWidth}[{min} {max}]";
    }

    private static string RenderEndpoint(
        string endpoint,
        BigInteger typeBoundary,
        bool isLowerEndpoint,
        bool isUnsigned)
    {
        if (!BigInteger.TryParse(endpoint, out var value)
            || value != typeBoundary)
        {
            return endpoint;
        }

        if (!isLowerEndpoint)
        {
            return "max";
        }

        return isUnsigned ? "0" : "min";
    }
}
