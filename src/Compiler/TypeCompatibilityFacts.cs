using System.Numerics;

namespace Stark.Compiler;

internal static class TypeCompatibilityFacts
{
    public static bool CanAssign(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (target.Kind == StarkTypeKind.Error || source.Kind == StarkTypeKind.Error)
        {
            return true;
        }

        if (!AreQualifiersAssignable(target, source))
        {
            return false;
        }

        if (Equals(target, source))
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Integer && source.Kind == StarkTypeKind.Integer)
        {
            if (target.BitWidth is null || source.BitWidth is null || source.BitWidth > target.BitWidth)
            {
                return false;
            }

            if (!TryGetEffectiveIntegerRange(source, out var sourceMin, out var sourceMax)
                || !TryGetEffectiveIntegerRange(target, out var targetMin, out var targetMax))
            {
                return false;
            }

            return IsRangeContained(sourceMin, sourceMax, targetMin, targetMax);
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Float)
        {
            return source.BitWidth <= target.BitWidth;
        }

        if (target.Kind == StarkTypeKind.Float && source.Kind == StarkTypeKind.Integer)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.RawPointer)
        {
            if (target.ElementType is null || source.ElementType is null)
            {
                return target.IsMutablePointer == source.IsMutablePointer;
            }

            if (target.IsMutablePointer && !source.IsMutablePointer)
            {
                return false;
            }

            return CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.Null)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.FixedArray && target.ElementType is not null && source.ElementType is not null)
        {
            return CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.FixedArray && source.Kind == StarkTypeKind.FixedArray)
        {
            return target.FixedLength == source.FixedLength
                && target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType);
        }

        if (target.Kind == StarkTypeKind.Slice && source.Kind == StarkTypeKind.Slice)
        {
            return target.ElementType is not null
                && source.ElementType is not null
                && CanAssign(target.ElementType, source.ElementType);
        }

        return target.Kind == StarkTypeKind.Named
            && source.Kind == StarkTypeKind.Named
            && string.Equals(target.NamedType, source.NamedType, StringComparison.Ordinal);
    }

    public static bool AreQualifiersAssignable(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (!IsBorrowAssignable(target.BorrowKind, source.BorrowKind))
        {
            return false;
        }

        if (!IsAccessAssignable(target.AccessKind, source.AccessKind))
        {
            return false;
        }

        if (target.InitializationKind != source.InitializationKind)
        {
            return false;
        }

        if (target.IsMutableView && !source.IsMutableView)
        {
            return false;
        }

        return true;
    }

    public static bool WouldEraseFrozenProvenance(StarkTypeSymbol target, StarkTypeSymbol source)
    {
        if (source.AccessKind == StarkAccessKind.Frozen && target.AccessKind != StarkAccessKind.Frozen)
        {
            return true;
        }

        if (target.Kind == StarkTypeKind.RawPointer && source.Kind == StarkTypeKind.RawPointer)
        {
            if (target.ElementType is not null && source.ElementType is not null)
            {
                return WouldEraseFrozenProvenance(target.ElementType, source.ElementType);
            }
        }

        if ((target.Kind == StarkTypeKind.FixedArray || target.Kind == StarkTypeKind.Slice)
            && source.Kind == target.Kind
            && target.ElementType is not null
            && source.ElementType is not null)
        {
            return WouldEraseFrozenProvenance(target.ElementType, source.ElementType);
        }

        return false;
    }

    public static bool IsRangeContained(BigInteger? sourceMin, BigInteger? sourceMax, BigInteger? targetMin, BigInteger? targetMax)
    {
        if (targetMin is null || targetMax is null)
        {
            return true;
        }

        if (sourceMin is null || sourceMax is null)
        {
            return false;
        }

        return sourceMin >= targetMin && sourceMax <= targetMax;
    }

    private static bool TryGetEffectiveIntegerRange(StarkTypeSymbol type, out BigInteger min, out BigInteger max)
    {
        if (type.BitWidth is not int bitWidth || bitWidth <= 0)
        {
            min = default;
            max = default;
            return false;
        }

        if (type.RangeMin is not null && type.RangeMax is not null)
        {
            min = type.RangeMin.Value;
            max = type.RangeMax.Value;
            return true;
        }

        min = -(BigInteger.One << (bitWidth - 1));
        max = (BigInteger.One << (bitWidth - 1)) - BigInteger.One;
        return true;
    }

    private static bool IsBorrowAssignable(StarkBorrowKind target, StarkBorrowKind source)
    {
        if (target == StarkBorrowKind.None || source == StarkBorrowKind.None)
        {
            return target == source;
        }

        return BorrowRank(source) >= BorrowRank(target);
    }

    private static bool IsAccessAssignable(StarkAccessKind target, StarkAccessKind source)
    {
        if (target == source)
        {
            return true;
        }

        if (source == StarkAccessKind.Frozen || target == StarkAccessKind.Frozen)
        {
            return false;
        }

        return AccessRank(source) >= AccessRank(target);
    }

    private static int BorrowRank(StarkBorrowKind kind)
    {
        return kind switch
        {
            StarkBorrowKind.Borrow => 1,
            StarkBorrowKind.RetBorrow => 2,
            StarkBorrowKind.StoreBorrow => 3,
            _ => 0
        };
    }

    private static int AccessRank(StarkAccessKind kind)
    {
        return kind switch
        {
            StarkAccessKind.Shared => 1,
            StarkAccessKind.Frozen => 2,
            _ => 0
        };
    }
}
