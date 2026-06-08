namespace Stark.Compiler;

internal static class AssociatedTypeFacts
{
    public static bool TryResolveAssociatedType(
        StarkTypeSymbol ownerType,
        string associatedTypeName,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        out StarkTypeSymbol targetType)
    {
        var ownerCore = StripTopLevelQualifiers(ownerType);
        if (ownerCore.Kind == StarkTypeKind.AssociatedType
            && ownerCore.AssociatedTypeOwner is not null
            && ownerCore.AssociatedTypeName is not null
            && TryResolveAssociatedType(ownerCore.AssociatedTypeOwner, ownerCore.AssociatedTypeName, namedTypes, out var resolvedOwner))
        {
            ownerCore = StripTopLevelQualifiers(resolvedOwner);
        }

        if (ownerCore.Kind == StarkTypeKind.DynTrait && ownerCore.DynTraitName is { } dynTraitName)
        {
            return TryResolveAssociatedTypeFromNamedType(
                dynTraitName,
                ownerCore.TypeArguments,
                associatedTypeName,
                namedTypes,
                out targetType);
        }

        if (ownerCore.Kind != StarkTypeKind.Named || ownerCore.NamedType is not { } ownerName)
        {
            targetType = StarkTypeSymbols.Error;
            return false;
        }

        if (TryResolveAssociatedTypeFromNamedType(
                ownerName,
                ownerCore.TypeArguments,
                associatedTypeName,
                namedTypes,
                out targetType))
        {
            return true;
        }

        var ownerBaseName = StarkTypeSymbols.GetGenericBaseName(ownerName);
        if (!string.Equals(ownerBaseName, ownerName, StringComparison.Ordinal))
        {
            return TryResolveAssociatedTypeFromNamedType(
                ownerBaseName,
                ownerCore.TypeArguments,
                associatedTypeName,
                namedTypes,
                out targetType);
        }

        targetType = StarkTypeSymbols.Error;
        return false;
    }

    public static StarkTypeSymbol ResolveOpenAssociatedTypes(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        return ResolveOpenAssociatedTypes(type, namedTypes, resolving: []);
    }

    public static bool ContainsAssociatedType(StarkTypeSymbol type)
    {
        var coreType = StripTopLevelQualifiers(type);
        return coreType.Kind == StarkTypeKind.AssociatedType
            || coreType.ElementType is not null && ContainsAssociatedType(coreType.ElementType)
            || coreType.FunctionPointerReturnType is not null && ContainsAssociatedType(coreType.FunctionPointerReturnType)
            || coreType.FunctionPointerParameterTypes is { Count: > 0 } && coreType.FunctionPointerParameterTypes.Any(ContainsAssociatedType)
            || coreType.ClosureReturnType is not null && ContainsAssociatedType(coreType.ClosureReturnType)
            || coreType.ClosureParameterTypes is { Count: > 0 } && coreType.ClosureParameterTypes.Any(ContainsAssociatedType)
            || coreType.TypeArguments is { Count: > 0 } && coreType.TypeArguments.Any(ContainsAssociatedType);
    }

    private static bool TryResolveAssociatedTypeFromNamedType(
        string ownerName,
        IReadOnlyList<StarkTypeSymbol>? ownerTypeArguments,
        string associatedTypeName,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        out StarkTypeSymbol targetType)
    {
        if (!namedTypes.TryGetValue(ownerName, out var owner)
            || !owner.AssociatedTypes.TryGetValue(associatedTypeName, out var associatedType)
            || associatedType.TargetType is null)
        {
            targetType = StarkTypeSymbols.Error;
            return false;
        }

        targetType = associatedType.TargetType;
        if (owner.GenericParams.Count > 0 && ownerTypeArguments is { Count: > 0 })
        {
            var substitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            for (var index = 0; index < owner.GenericParams.Count && index < ownerTypeArguments.Count; index++)
            {
                substitution[owner.GenericParams[index]] = ownerTypeArguments[index];
            }

            targetType = SubstituteType(targetType, substitution, namedTypes);
        }

        targetType = ResolveOpenAssociatedTypes(targetType, namedTypes);
        return true;
    }

    private static StarkTypeSymbol ResolveOpenAssociatedTypes(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        HashSet<string> resolving)
    {
        var coreType = StripTopLevelQualifiers(type);
        StarkTypeSymbol resolvedCore;

        if (coreType.Kind == StarkTypeKind.AssociatedType
            && coreType.AssociatedTypeOwner is not null
            && coreType.AssociatedTypeName is not null)
        {
            var resolvedOwner = ResolveOpenAssociatedTypes(coreType.AssociatedTypeOwner, namedTypes, resolving);
            var key = $"{resolvedOwner.DisplayName}.{coreType.AssociatedTypeName}";
            resolvedCore = resolving.Add(key)
                && TryResolveAssociatedType(resolvedOwner, coreType.AssociatedTypeName, namedTypes, out var resolvedAssociated)
                    ? ResolveOpenAssociatedTypes(resolvedAssociated, namedTypes, resolving)
                    : StarkTypeSymbols.AssociatedType(resolvedOwner, coreType.AssociatedTypeName);
            resolving.Remove(key);
        }
        else if (coreType.ElementType is not null)
        {
            var resolvedElement = ResolveOpenAssociatedTypes(coreType.ElementType, namedTypes, resolving);
            resolvedCore = coreType.Kind switch
            {
                StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(resolvedElement, coreType.FixedLength, coreType.FixedLengthParameterName),
                StarkTypeKind.Slice => StarkTypeSymbols.Slice(resolvedElement),
                StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(resolvedElement, coreType.IsMutablePointer),
                StarkTypeKind.Dynamic => StarkTypeSymbols.Dynamic(resolvedElement),
                _ => coreType
            };
        }
        else if (coreType.Kind == StarkTypeKind.Named
            && StarkTypeSymbols.IsGenericInstantiation(coreType)
            && coreType.NamedType is not null
            && coreType.TypeArguments is { Count: > 0 } typeArguments)
        {
            resolvedCore = StarkTypeSymbols.GenericInstantiation(
                StarkTypeSymbols.GetGenericBaseName(coreType.NamedType),
                typeArguments.Select(argument => ResolveOpenAssociatedTypes(argument, namedTypes, resolving)).ToArray());
        }
        else if (coreType.Kind == StarkTypeKind.FunctionPointer
            && coreType.FunctionPointerKind is { } functionKind
            && coreType.FunctionPointerReturnType is { } returnType
            && coreType.FunctionPointerParameterTypes is { } parameterTypes)
        {
            resolvedCore = StarkTypeSymbols.FunctionPointer(
                functionKind,
                ResolveOpenAssociatedTypes(returnType, namedTypes, resolving),
                parameterTypes.Select(parameter => ResolveOpenAssociatedTypes(parameter, namedTypes, resolving)).ToArray(),
                coreType.FunctionPointerDisjointParameterGroups,
                coreType.FunctionPointerOverlapParameterGroups,
                coreType.FunctionPointerSameParameterGroups,
                coreType.FunctionPointerParameterRawPointerElementCountExpressions,
                coreType.FunctionPointerAbi,
                coreType.FunctionPointerIsUnsafe);
        }
        else if (coreType.Kind == StarkTypeKind.Closure
            && coreType.ClosureFunctionKind is { } closureFunctionKind
            && coreType.ClosureReturnType is { } closureReturnType
            && coreType.ClosureParameterTypes is { } closureParameterTypes)
        {
            resolvedCore = StarkTypeSymbols.Closure(
                coreType.ClosureStorageKind,
                coreType.ClosureCallCapability,
                closureFunctionKind,
                ResolveOpenAssociatedTypes(closureReturnType, namedTypes, resolving),
                closureParameterTypes.Select(parameter => ResolveOpenAssociatedTypes(parameter, namedTypes, resolving)).ToArray(),
                coreType.ClosureDisjointParameterGroups,
                coreType.ClosureOverlapParameterGroups,
                coreType.ClosureSameParameterGroups,
                coreType.ClosureParameterRawPointerElementCountExpressions);
        }
        else
        {
            resolvedCore = coreType;
        }

        return StarkTypeSymbols.WithQualifiers(
            resolvedCore,
            borrowKind: type.BorrowKind,
            accessKind: type.AccessKind,
            initializationKind: type.InitializationKind,
            isMutableView: type.IsMutableView);
    }

    private static StarkTypeSymbol SubstituteType(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, StarkTypeSymbol> substitution,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        var coreType = StripTopLevelQualifiers(type);
        StarkTypeSymbol substitutedCore;

        if (coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } name)
        {
            if (substitution.TryGetValue(name, out var substituted))
            {
                substitutedCore = StripTopLevelQualifiers(substituted);
            }
            else if (StarkTypeSymbols.IsGenericInstantiation(coreType) && coreType.TypeArguments is not null)
            {
                substitutedCore = StarkTypeSymbols.GenericInstantiation(
                    StarkTypeSymbols.GetGenericBaseName(name),
                    coreType.TypeArguments.Select(argument => SubstituteType(argument, substitution, namedTypes)).ToArray());
            }
            else
            {
                substitutedCore = coreType;
            }
        }
        else if (coreType.Kind == StarkTypeKind.AssociatedType
            && coreType.AssociatedTypeOwner is not null
            && coreType.AssociatedTypeName is not null)
        {
            var substitutedOwner = SubstituteType(coreType.AssociatedTypeOwner, substitution, namedTypes);
            substitutedCore = TryResolveAssociatedType(substitutedOwner, coreType.AssociatedTypeName, namedTypes, out var associatedTarget)
                ? StripTopLevelQualifiers(associatedTarget)
                : StarkTypeSymbols.AssociatedType(substitutedOwner, coreType.AssociatedTypeName);
        }
        else if (coreType.ElementType is not null)
        {
            var substitutedElement = SubstituteType(coreType.ElementType, substitution, namedTypes);
            substitutedCore = coreType.Kind switch
            {
                StarkTypeKind.FixedArray => StarkTypeSymbols.FixedArray(substitutedElement, coreType.FixedLength, coreType.FixedLengthParameterName),
                StarkTypeKind.Slice => StarkTypeSymbols.Slice(substitutedElement),
                StarkTypeKind.RawPointer => StarkTypeSymbols.RawPointer(substitutedElement, coreType.IsMutablePointer),
                StarkTypeKind.Dynamic => StarkTypeSymbols.Dynamic(substitutedElement),
                _ => coreType
            };
        }
        else if (coreType.Kind == StarkTypeKind.FunctionPointer
            && coreType.FunctionPointerKind is { } functionKind
            && coreType.FunctionPointerReturnType is { } returnType
            && coreType.FunctionPointerParameterTypes is { } parameterTypes)
        {
            substitutedCore = StarkTypeSymbols.FunctionPointer(
                functionKind,
                SubstituteType(returnType, substitution, namedTypes),
                parameterTypes.Select(parameter => SubstituteType(parameter, substitution, namedTypes)).ToArray(),
                coreType.FunctionPointerDisjointParameterGroups,
                coreType.FunctionPointerOverlapParameterGroups,
                coreType.FunctionPointerSameParameterGroups,
                coreType.FunctionPointerParameterRawPointerElementCountExpressions,
                coreType.FunctionPointerAbi,
                coreType.FunctionPointerIsUnsafe);
        }
        else if (coreType.Kind == StarkTypeKind.Closure
            && coreType.ClosureFunctionKind is { } closureFunctionKind
            && coreType.ClosureReturnType is { } closureReturnType
            && coreType.ClosureParameterTypes is { } closureParameterTypes)
        {
            substitutedCore = StarkTypeSymbols.Closure(
                coreType.ClosureStorageKind,
                coreType.ClosureCallCapability,
                closureFunctionKind,
                SubstituteType(closureReturnType, substitution, namedTypes),
                closureParameterTypes.Select(parameter => SubstituteType(parameter, substitution, namedTypes)).ToArray(),
                coreType.ClosureDisjointParameterGroups,
                coreType.ClosureOverlapParameterGroups,
                coreType.ClosureSameParameterGroups,
                coreType.ClosureParameterRawPointerElementCountExpressions);
        }
        else
        {
            substitutedCore = coreType;
        }

        return StarkTypeSymbols.WithQualifiers(
            substitutedCore,
            borrowKind: type.BorrowKind,
            accessKind: type.AccessKind,
            initializationKind: type.InitializationKind,
            isMutableView: type.IsMutableView);
    }

    private static StarkTypeSymbol StripTopLevelQualifiers(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }
}
