namespace Stark.Compiler;

internal sealed class ThreadSafetyLawEvaluator
{
    private static readonly IReadOnlyList<string> KnownLawNames =
    [
        ThreadSafetyLawNames.Transferable,
        ThreadSafetyLawNames.Shareable
    ];

    private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;
    private readonly string _rootModuleName;
    private readonly Action<string, string> _reportDiagnostic;
    private readonly Dictionary<string, ThreadSafetyLawFact> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedConflicts = new(StringComparer.Ordinal);

    public ThreadSafetyLawEvaluator(
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        string rootModuleName,
        Action<string, string> reportDiagnostic)
    {
        _namedTypes = namedTypes;
        _rootModuleName = rootModuleName;
        _reportDiagnostic = reportDiagnostic;
    }

    public IReadOnlyDictionary<string, ThreadSafetyLawTypeFacts> ComputeNamedTypeFacts()
    {
        var facts = new Dictionary<string, ThreadSafetyLawTypeFacts>(StringComparer.Ordinal);
        foreach (var namedType in _namedTypes.Values.OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            var type = StarkTypeSymbols.Named(namedType.Name);
            facts[namedType.Name] = new ThreadSafetyLawTypeFacts(
                type,
                Evaluate(ThreadSafetyLawNames.Transferable, type),
                Evaluate(ThreadSafetyLawNames.Shareable, type));
        }

        return facts;
    }

    public ThreadSafetyLawFact Evaluate(string lawName, StarkTypeSymbol type)
    {
        if (!KnownLawNames.Contains(lawName, StringComparer.Ordinal))
        {
            return Failure(
                lawName,
                type,
                ThreadSafetyLawFailureKind.UnknownType,
                $"Unknown thread-safety law '{lawName}'.",
                type);
        }

        var key = $"{lawName}|{BuildTypeKey(type)}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_active.Add(key))
        {
            return Success(lawName, type);
        }

        try
        {
            var fact = EvaluateUncached(lawName, type);
            _cache[key] = fact;
            return fact;
        }
        finally
        {
            _active.Remove(key);
        }
    }

    private ThreadSafetyLawFact EvaluateUncached(string lawName, StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.Error)
        {
            return Success(lawName, type);
        }

        if (type.BorrowKind == StarkBorrowKind.StoreBorrow)
        {
            return Failure(
                lawName,
                type,
                ThreadSafetyLawFailureKind.StoredBorrow,
                $"Type '{type.DisplayName}' is a stored borrow and cannot satisfy {lawName}.",
                type);
        }

        var coreType = StripTopLevelQualifiers(type);
        return coreType.Kind switch
        {
            StarkTypeKind.Void
                or StarkTypeKind.Bool
                or StarkTypeKind.Ascii
                or StarkTypeKind.Unicode
                or StarkTypeKind.CVoid
                or StarkTypeKind.Integer
                or StarkTypeKind.Float
                or StarkTypeKind.Null => Success(lawName, type),

            StarkTypeKind.RawPointer => Failure(
                lawName,
                type,
                ThreadSafetyLawFailureKind.RawPointer,
                $"Type '{coreType.DisplayName}' is a raw pointer and cannot satisfy {lawName} by default.",
                coreType),

            StarkTypeKind.FixedArray
                or StarkTypeKind.Slice
                or StarkTypeKind.Dynamic when coreType.ElementType is { } elementType =>
                    Rewrap(lawName, type, Evaluate(lawName, elementType)),

            StarkTypeKind.FunctionPointer => Success(lawName, type),
            StarkTypeKind.Closure => Success(lawName, type),
            StarkTypeKind.AssociatedType => ConditionalRequirement(lawName, coreType),
            StarkTypeKind.DynTrait => Success(lawName, type),
            StarkTypeKind.Named => EvaluateNamed(lawName, type, coreType),
            _ => Success(lawName, type)
        };
    }

    private ThreadSafetyLawFact EvaluateNamed(
        string lawName,
        StarkTypeSymbol originalType,
        StarkTypeSymbol coreType)
    {
        if (coreType.NamedType is not { } typeName)
        {
            return Success(lawName, originalType);
        }

        if (!_namedTypes.TryGetValue(typeName, out var namedType))
        {
            return ConditionalRequirement(lawName, coreType);
        }

        if (IsIntrinsicAtomic(namedType))
        {
            return Success(lawName, originalType);
        }

        var typeConflict = TryBuildAttributeConflictFact(
            lawName,
            originalType,
            namedType.ThreadSafetyLaws,
            $"type '{namedType.Name}'",
            fieldPath: null);
        if (typeConflict is not null)
        {
            return typeConflict;
        }

        foreach (var grant in namedType.ThreadSafetyLaws.Where(attribute =>
                     attribute.Kind == ThreadSafetyLawAttributeKind.Grant
                     && string.Equals(attribute.LawName, lawName, StringComparison.Ordinal)))
        {
            if (TryApplyGrant(lawName, originalType, grant, out var grantFact))
            {
                return grantFact;
            }
        }

        if (namedType.ThreadSafetyLaws.Any(attribute =>
                attribute.Kind == ThreadSafetyLawAttributeKind.Deny
                && string.Equals(attribute.LawName, lawName, StringComparison.Ordinal)))
        {
            return Failure(
                lawName,
                originalType,
                ThreadSafetyLawFailureKind.DeniedByTypeAttribute,
                $"Type '{namedType.Name}' explicitly denies {lawName}.",
                originalType);
        }

        var failures = new List<ThreadSafetyLawFailure>();
        var requirements = new List<ThreadSafetyLawRequirement>();

        foreach (var field in namedType.OrderedFields)
        {
            AddFieldContribution(
                lawName,
                originalType,
                field.Name,
                field.Type,
                field.ThreadSafetyLaws,
                failures,
                requirements);
        }

        foreach (var variant in namedType.Variants)
        {
            foreach (var field in variant.Fields)
            {
                var segment = field.Name is { Length: > 0 }
                    ? $"{variant.Name}.{field.Name}"
                    : $"{variant.Name}#{field.Position}";
                AddFieldContribution(
                    lawName,
                    originalType,
                    segment,
                    field.Type,
                    attributes: [],
                    failures,
                    requirements);
            }
        }

        return BuildFact(lawName, originalType, failures, requirements);
    }

    private void AddFieldContribution(
        string lawName,
        StarkTypeSymbol ownerType,
        string fieldName,
        StarkTypeSymbol fieldType,
        IReadOnlyList<ThreadSafetyLawAttributeSymbol> attributes,
        List<ThreadSafetyLawFailure> failures,
        List<ThreadSafetyLawRequirement> requirements)
    {
        var conflict = TryBuildAttributeConflictFact(
            lawName,
            ownerType,
            attributes,
            $"field '{ownerType.DisplayName}.{fieldName}'",
            [fieldName]);
        if (conflict is not null)
        {
            failures.AddRange(conflict.FailureReasons);
            return;
        }

        foreach (var grant in attributes.Where(attribute =>
                     attribute.Kind == ThreadSafetyLawAttributeKind.Grant
                     && string.Equals(attribute.LawName, lawName, StringComparison.Ordinal)))
        {
            if (TryApplyGrant(lawName, fieldType, grant, out var grantFact))
            {
                requirements.AddRange(grantFact.RequiredPredicates);
                return;
            }
        }

        if (attributes.Any(attribute =>
                attribute.Kind == ThreadSafetyLawAttributeKind.Deny
                && string.Equals(attribute.LawName, lawName, StringComparison.Ordinal)))
        {
            failures.Add(new ThreadSafetyLawFailure(
                ThreadSafetyLawFailureKind.DeniedByFieldAttribute,
                $"Field '{ownerType.DisplayName}.{fieldName}' explicitly denies {lawName}.",
                fieldType,
                [fieldName]));
            return;
        }

        var fieldFact = Evaluate(lawName, fieldType);
        if (fieldFact.Holds)
        {
            requirements.AddRange(fieldFact.RequiredPredicates);
            return;
        }

        failures.AddRange(fieldFact.FailureReasons.Select(failure => PrefixFieldPath(fieldName, failure)));
    }

    private ThreadSafetyLawFact? TryBuildAttributeConflictFact(
        string lawName,
        StarkTypeSymbol type,
        IReadOnlyList<ThreadSafetyLawAttributeSymbol> attributes,
        string targetDescription,
        IReadOnlyList<string>? fieldPath)
    {
        var hasGrant = attributes.Any(attribute =>
            attribute.Kind == ThreadSafetyLawAttributeKind.Grant
            && string.Equals(attribute.LawName, lawName, StringComparison.Ordinal));
        var hasDeny = attributes.Any(attribute =>
            attribute.Kind == ThreadSafetyLawAttributeKind.Deny
            && string.Equals(attribute.LawName, lawName, StringComparison.Ordinal));
        if (!hasGrant || !hasDeny)
        {
            return null;
        }

        var message = $"{targetDescription} both grants and denies {lawName}. Keep exactly one thread-safety law attribute for that law.";
        var reportKey = $"{targetDescription}|{lawName}";
        if (_reportedConflicts.Add(reportKey))
        {
            _reportDiagnostic("STK3050", message);
        }

        return new ThreadSafetyLawFact(
            lawName,
            type,
            Holds: false,
            Failures:
            [
                new ThreadSafetyLawFailure(
                    ThreadSafetyLawFailureKind.ConflictingAttributes,
                    message,
                    type,
                    fieldPath)
            ]);
    }

    private bool TryApplyGrant(
        string lawName,
        StarkTypeSymbol type,
        ThreadSafetyLawAttributeSymbol grant,
        out ThreadSafetyLawFact fact)
    {
        if (grant.Condition is null)
        {
            fact = Success(lawName, type);
            return true;
        }

        var conditionFact = Evaluate(grant.Condition.LawName, grant.Condition.Type);
        if (!conditionFact.Holds)
        {
            fact = null!;
            return false;
        }

        fact = new ThreadSafetyLawFact(
            lawName,
            type,
            Holds: true,
            Requirements: conditionFact.RequiredPredicates);
        return true;
    }

    private bool IsIntrinsicAtomic(NamedTypeSymbol namedType)
    {
        var typeName = namedType.Name;
        if (typeName.StartsWith(SystemThreadingAtomicFacts.ModuleName + ".", StringComparison.Ordinal))
        {
            typeName = typeName[(SystemThreadingAtomicFacts.ModuleName.Length + 1)..];
        }
        else if (typeName.Contains('.', StringComparison.Ordinal)
            || !string.Equals(_rootModuleName, SystemThreadingAtomicFacts.ModuleName, StringComparison.Ordinal))
        {
            return false;
        }

        return SystemThreadingAtomicFacts.TryParseAtomicTypeName(
            typeName,
            out _,
            out _,
            out _);
    }

    private static ThreadSafetyLawFact Rewrap(
        string lawName,
        StarkTypeSymbol type,
        ThreadSafetyLawFact elementFact)
    {
        if (elementFact.Holds)
        {
            return new ThreadSafetyLawFact(
                lawName,
                type,
                Holds: true,
                Requirements: elementFact.RequiredPredicates);
        }

        return new ThreadSafetyLawFact(
            lawName,
            type,
            Holds: false,
            Failures: elementFact.FailureReasons);
    }

    private static ThreadSafetyLawFact BuildFact(
        string lawName,
        StarkTypeSymbol type,
        IReadOnlyList<ThreadSafetyLawFailure> failures,
        IReadOnlyList<ThreadSafetyLawRequirement> requirements)
    {
        var dedupedRequirements = DeduplicateRequirements(requirements);
        if (failures.Count != 0)
        {
            return new ThreadSafetyLawFact(
                lawName,
                type,
                Holds: false,
                Requirements: dedupedRequirements,
                Failures: failures.ToArray());
        }

        return new ThreadSafetyLawFact(
            lawName,
            type,
            Holds: true,
            Requirements: dedupedRequirements);
    }

    private static ThreadSafetyLawFact Success(string lawName, StarkTypeSymbol type) =>
        new(lawName, type, Holds: true);

    private static ThreadSafetyLawFact ConditionalRequirement(string lawName, StarkTypeSymbol type) =>
        new(
            lawName,
            type,
            Holds: true,
            Requirements: [new ThreadSafetyLawRequirement(lawName, type)]);

    private static ThreadSafetyLawFact Failure(
        string lawName,
        StarkTypeSymbol type,
        ThreadSafetyLawFailureKind kind,
        string message,
        StarkTypeSymbol? failureType) =>
        new(
            lawName,
            type,
            Holds: false,
            Failures: [new ThreadSafetyLawFailure(kind, message, failureType)]);

    private static IReadOnlyList<ThreadSafetyLawRequirement> DeduplicateRequirements(
        IReadOnlyList<ThreadSafetyLawRequirement> requirements)
    {
        if (requirements.Count <= 1)
        {
            return requirements;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ThreadSafetyLawRequirement>();
        foreach (var requirement in requirements)
        {
            var key = $"{requirement.LawName}|{BuildTypeKey(requirement.Type)}";
            if (seen.Add(key))
            {
                result.Add(requirement);
            }
        }

        return result;
    }

    private static ThreadSafetyLawFailure PrefixFieldPath(string fieldName, ThreadSafetyLawFailure failure)
    {
        var path = new string[1 + failure.Path.Count];
        path[0] = fieldName;
        for (var index = 0; index < failure.Path.Count; index++)
        {
            path[index + 1] = failure.Path[index];
        }

        return failure with { FieldPath = path };
    }

    private static StarkTypeSymbol StripTopLevelQualifiers(StarkTypeSymbol type) =>
        StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);

    private static string BuildTypeKey(StarkTypeSymbol type)
    {
        var identity = type.NamedType ?? type.DisplayName;
        return string.Equals(identity, type.DisplayName, StringComparison.Ordinal)
            ? identity
            : $"{type.DisplayName}|{identity}";
    }
}
