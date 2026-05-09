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
        var normalizedNamedType = type.NamedType is null
            ? null
            : QualifyModuleLocalNamedType(type, moduleName, localNamedTypes);
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
            ElementType: type.ElementType is null ? null : BuildPublishedAbiTypeReference(type.ElementType, moduleName, localNamedTypes),
            TypeArguments: type.TypeArguments is { Count: > 0 }
                ? type.TypeArguments.Select(argument => BuildPublishedAbiTypeReference(argument, moduleName, localNamedTypes)).ToArray()
                : null,
            FunctionKind: type.FunctionPointerKind is null ? null : RenderPackageFunctionKind(type.FunctionPointerKind.Value),
            ReturnType: type.FunctionPointerReturnType is null
                ? null
                : BuildPublishedAbiTypeReference(type.FunctionPointerReturnType, moduleName, localNamedTypes),
            ParameterTypes: type.FunctionPointerParameterTypes is { Count: > 0 }
                ? type.FunctionPointerParameterTypes.Select(parameter => BuildPublishedAbiTypeReference(parameter, moduleName, localNamedTypes)).ToArray()
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
            IsUnsigned: type.IsUnsigned ? true : null,
            IsMutablePointer: type.IsMutablePointer,
            BorrowKind: type.BorrowKind == StarkBorrowKind.None ? null : type.BorrowKind.ToString().ToLowerInvariant(),
            AccessKind: type.AccessKind == StarkAccessKind.None ? null : type.AccessKind.ToString().ToLowerInvariant(),
            InitializationKind: type.InitializationKind == StarkInitializationKind.None ? null : type.InitializationKind.ToString().ToLowerInvariant(),
            IsMutableView: type.IsMutableView,
            FixedLength: type.FixedLength,
            ElementType: type.ElementType is null ? null : BuildTypeReference(type.ElementType, moduleName, stripCurrentModulePrefix),
            TypeArguments: type.TypeArguments is { Count: > 0 }
                ? type.TypeArguments.Select(argument => BuildTypeReference(argument, moduleName, stripCurrentModulePrefix)).ToArray()
                : null,
            FunctionKind: type.FunctionPointerKind is null ? null : RenderPackageFunctionKind(type.FunctionPointerKind.Value),
            ReturnType: type.FunctionPointerReturnType is null
                ? null
                : BuildTypeReference(type.FunctionPointerReturnType, moduleName, stripCurrentModulePrefix),
            ParameterTypes: type.FunctionPointerParameterTypes is { Count: > 0 }
                ? type.FunctionPointerParameterTypes.Select(parameter => BuildTypeReference(parameter, moduleName, stripCurrentModulePrefix)).ToArray()
                : null);
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
            "null" => StarkTypeSymbols.Null,
            "integer" => StarkTypeSymbols.Integer(
                type.BitWidth ?? 32,
                type.RangeMin is null ? null : BigInteger.Parse(type.RangeMin, System.Globalization.CultureInfo.InvariantCulture),
                type.RangeMax is null ? null : BigInteger.Parse(type.RangeMax, System.Globalization.CultureInfo.InvariantCulture),
                type.IsUnsigned == true),
            "float" => StarkTypeSymbols.Float(type.BitWidth ?? 32),
            "rawpointer" => StarkTypeSymbols.RawPointer(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes), type.IsMutablePointer),
            "fixedarray" => StarkTypeSymbols.FixedArray(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes), type.FixedLength),
            "slice" => StarkTypeSymbols.Slice(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes)),
            "dynamic" => StarkTypeSymbols.Dynamic(BuildTypeSymbol(type.ElementType!, currentModuleName, localNamedTypes)),
            "functionpointer" when type.ReturnType is not null => StarkTypeSymbols.FunctionPointer(
                ParsePackageFunctionKind(type.FunctionKind),
                BuildTypeSymbol(type.ReturnType, currentModuleName, localNamedTypes),
                (type.ParameterTypes ?? []).Select(parameter => BuildTypeSymbol(parameter, currentModuleName, localNamedTypes)).ToArray()),
            "named" when type.TypeArguments is { Count: > 0 } => StarkTypeSymbols.GenericInstantiation(
                normalizedNamedType ?? "<unnamed>",
                type.TypeArguments.Select(argument => BuildTypeSymbol(argument, currentModuleName, localNamedTypes)).ToArray()),
            "named" => StarkTypeSymbols.Named(normalizedNamedType ?? "<unnamed>"),
            _ => StarkTypeSymbols.Error
        };

        return StarkTypeSymbols.ApplyQualifiers(
            core,
            borrowKind: ParseBorrowKind(type.BorrowKind),
            accessKind: ParseAccessKind(type.AccessKind),
            initializationKind: ParseInitializationKind(type.InitializationKind),
            isMutableView: type.IsMutableView);
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

        var core = type.Kind switch
        {
            "error" => "<error>",
            "void" => "void",
            "bool" => "bool",
            "ascii" => "ascii",
            "unicode" => "unicode",
            "null" => "null",
            "integer" => PackageImageIntegerTypeText.Render(type.BitWidth, type.RangeMin, type.RangeMax, type.IsUnsigned == true),
            "float" => $"f{type.BitWidth}",
            "rawpointer" => $"{(type.IsMutablePointer ? "rawmutptr" : "rawptr")}<{RenderTypeReference(type.ElementType!)}>",
            "fixedarray" => $"{RenderTypeReference(type.ElementType!)}[{(type.FixedLength is { } fixedLength ? fixedLength.ToString() : "?")}]",
            "slice" => $"{RenderTypeReference(type.ElementType!)}[]",
            "dynamic" => $"dynamic {RenderTypeReference(type.ElementType!)}",
            "functionpointer" => $"fnptr<{RenderTypeReferenceFunctionKind(type.FunctionKind)} {RenderTypeReference(type.ReturnType!)}({string.Join(", ", (type.ParameterTypes ?? []).Select(RenderTypeReference))})>",
            "named" when type.TypeArguments is { Count: > 0 } => $"{type.Name}<{string.Join(", ", type.TypeArguments.Select(RenderTypeReference))}>",
            "named" => type.Name ?? "<unnamed>",
            _ => type.Name ?? type.Kind
        };

        return qualifiers.Count == 0
            ? core
            : $"{string.Join(" ", qualifiers)} {core}";
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
