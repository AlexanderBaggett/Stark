namespace Stark.Compiler;

/// <summary>
/// Structural copyability: a type is copyable when reading it out of a place
/// is byte-copy-safe and creates no drop obligation. Scalars, raw pointers,
/// and text views are copyable; a named struct/record/enum is copyable when
/// it has no destructor and every field (of every variant) is copyable.
/// Anything that owns resources — dynamic storage, owned text, heap closures,
/// heap dyn traits, destructor-bearing types — is not. Generic types are
/// conservatively non-copyable until bounds can express it.
/// Shared by ownership validation, borrow-liveness validation, and SSA
/// lowering so "is this read a copy or a move" never diverges between them.
/// </summary>
internal sealed class CopyabilityFacts
{
    private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;
    private readonly Dictionary<string, bool> _cache = new(StringComparer.Ordinal);

    public CopyabilityFacts(IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes)
    {
        _namedTypes = namedTypes;
    }

    public bool IsCopyable(StarkTypeSymbol type)
    {
        if (type.BorrowKind != StarkBorrowKind.None)
        {
            return false;
        }

        return type.Kind switch
        {
            StarkTypeKind.Bool => true,
            StarkTypeKind.Integer => true,
            StarkTypeKind.Float => true,
            StarkTypeKind.RawPointer => true,
            StarkTypeKind.Ascii => true,
            StarkTypeKind.Unicode => true,
            StarkTypeKind.Null => true,
            StarkTypeKind.Named => IsCopyableNamed(type),
            _ => false
        };
    }

    private bool IsCopyableNamed(StarkTypeSymbol type)
    {
        if (type.NamedType is null)
        {
            return false;
        }

        // The builtin owned-text structs look like plain {ptr, len, cap}
        // aggregates but own their heap data through special-cased drops.
        if (string.Equals(type.NamedType, StarkTypeSymbols.OwnedAsciiName, StringComparison.Ordinal)
            || string.Equals(type.NamedType, StarkTypeSymbols.OwnedUnicodeName, StringComparison.Ordinal))
        {
            return false;
        }

        if (_cache.TryGetValue(type.NamedType, out var cached))
        {
            return cached;
        }

        // Conservative on cycles: a type currently being classified reports
        // non-copyable to any recursive mention of itself.
        _cache[type.NamedType] = false;
        var result = ComputeNamedCopyability(type.NamedType);
        _cache[type.NamedType] = result;
        return result;
    }

    private bool ComputeNamedCopyability(string name)
    {
        if (!_namedTypes.TryGetValue(name, out var namedType))
        {
            return false;
        }

        if (namedType.HasDestructor
            || namedType.GenericParameterNames is { Count: > 0 }
            || namedType.ComptimeGenericParameterNames is { Count: > 0 })
        {
            return false;
        }

        switch (namedType.Kind)
        {
            case DeclarationKind.Enum:
                foreach (var variant in namedType.EnumVariants ?? [])
                {
                    foreach (var field in variant.Fields)
                    {
                        if (!IsCopyable(field.Type))
                        {
                            return false;
                        }
                    }
                }

                return true;

            case DeclarationKind.Struct:
            case DeclarationKind.Record:
                foreach (var field in namedType.OrderedFields)
                {
                    if (!IsCopyable(field.Type))
                    {
                        return false;
                    }
                }

                return true;

            default:
                return false;
        }
    }
}
