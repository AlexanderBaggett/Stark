using System;
using System.Collections.Generic;
using System.Linq;

namespace Stark.Compiler;

// Central facts for `dyn Trait` trait objects: object safety, the vtable slot
// layout, and symbol naming. Shared by semantic validation (object-safety
// diagnostics), MIR dispatch lowering (slot index), and LLVM emission (vtable
// synthesis) so the slot order is computed in exactly one place and stays
// consistent across the call site and the table.
//
// A `dyn Trait` value is a two-word fat pointer { data_ptr, vtable_ptr }. The
// vtable is a read-only table laid out as:
//   { <slot 0 method ptr>, ..., <slot N-1 method ptr>, <drop ptr>, i64 size, i64 align }
// All slots before the size/align tail are pointers, so a slot is reached with a
// `getelementptr ptr, ptr <vtable>, i32 <slot>` — no struct layout is needed at
// the dispatch site.
internal static class DynTraitFacts
{
    // The implicit `Self` type parameter of every trait method, bound to the
    // implementing type during conformance and erased to a raw pointer in the vtable.
    public const string SelfTypeName = "Self";

    // A single dispatchable vtable entry: the trait method and the slot it occupies.
    public sealed record VtableSlot(int Index, string MethodName, TypedFunctionSignature TraitSignature);

    // The object-safe instance methods of a trait, in a deterministic order
    // (sorted by method name). Static and non-object-safe methods are excluded;
    // the dispatch site and the emitted table both call this, so the slot order
    // is identical on both sides.
    public static IReadOnlyList<VtableSlot> GetVtableLayout(
        string traitName,
        IReadOnlyDictionary<string, TypedFunctionSignature> functions)
    {
        var simpleTraitName = LastSegment(traitName);
        var methods = new List<(string Method, TypedFunctionSignature Signature)>();
        foreach (var pair in functions)
        {
            var key = pair.Key;
            var dot = key.LastIndexOf('.');
            if (dot <= 0)
            {
                continue;
            }

            var owner = key[..dot];
            if (!string.Equals(owner, traitName, StringComparison.Ordinal)
                && !string.Equals(owner, simpleTraitName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsObjectSafeInstanceMethod(pair.Value))
            {
                continue;
            }

            methods.Add((key[(dot + 1)..], pair.Value));
        }

        methods.Sort(static (left, right) => string.CompareOrdinal(left.Method, right.Method));
        var slots = new List<VtableSlot>(methods.Count);
        for (var index = 0; index < methods.Count; index++)
        {
            slots.Add(new VtableSlot(index, methods[index].Method, methods[index].Signature));
        }

        return slots;
    }

    public static bool TryGetSlot(
        string traitName,
        string methodName,
        IReadOnlyDictionary<string, TypedFunctionSignature> functions,
        out VtableSlot slot)
    {
        foreach (var candidate in GetVtableLayout(traitName, functions))
        {
            if (string.Equals(candidate.MethodName, methodName, StringComparison.Ordinal))
            {
                slot = candidate;
                return true;
            }
        }

        slot = default!;
        return false;
    }

    // True when a method can be dispatched through a fat pointer: an instance
    // method whose receiver is `borrow Self`/`mut borrow Self`, with no generic
    // parameters of its own and no by-value `Self` in parameter/return position.
    public static bool IsObjectSafeInstanceMethod(TypedFunctionSignature signature)
        => HasSelfReceiver(signature) && TryCheckObjectSafety(signature, out _);

    // Validates a method declared in a `dyn trait`. Static / no-self members are
    // legal (they are simply excluded from the vtable). An instance method that
    // is not object-safe yields a human-readable reason for the diagnostic.
    public static bool TryValidateDynTraitMethod(TypedFunctionSignature signature, out string? reason)
    {
        reason = null;
        if (signature.IsStatic || !HasSelfReceiver(signature))
        {
            return true;
        }

        return TryCheckObjectSafety(signature, out reason);
    }

    private static bool TryCheckObjectSafety(TypedFunctionSignature signature, out string? reason)
    {
        reason = null;
        var receiver = signature.Parameters[0].Type;
        if (receiver.BorrowKind == StarkBorrowKind.None)
        {
            reason = "its receiver must be 'borrow Self self' or 'mut borrow Self self' (a by-value or consuming receiver cannot be dispatched dynamically)";
            return false;
        }

        if (signature.GenericParams.Any(static name => !string.Equals(name, SelfTypeName, StringComparison.Ordinal)))
        {
            reason = "it is generic (a vtable cannot hold infinitely many instantiations)";
            return false;
        }

        for (var index = 1; index < signature.Parameters.Count; index++)
        {
            if (IsByValueSelf(signature.Parameters[index].Type))
            {
                reason = $"parameter '{signature.Parameters[index].Name}' takes 'Self' by value (its size is unknown behind a trait object)";
                return false;
            }
        }

        if (IsByValueSelf(signature.ReturnType))
        {
            reason = "it returns 'Self' by value (its size is unknown behind a trait object)";
            return false;
        }

        return true;
    }

    private static bool HasSelfReceiver(TypedFunctionSignature signature)
        => !signature.IsStatic
           && signature.Parameters.Count > 0
           && IsSelfType(signature.Parameters[0].Type);

    private static bool IsSelfType(StarkTypeSymbol type)
        => string.Equals(type.NamedType, SelfTypeName, StringComparison.Ordinal);

    private static bool IsByValueSelf(StarkTypeSymbol type)
        => IsSelfType(type)
           && type.BorrowKind == StarkBorrowKind.None
           && type.Kind != StarkTypeKind.RawPointer;

    // The shared prefix of every synthesized trait-object vtable global. Such
    // globals are emitted by the module surface emitter rather than the user/global
    // type model, so validation treats them as known read-only data.
    public const string VtableGlobalNamePrefix = "__stark_vtable_";

    // The read-only vtable global for an (implementing type, trait) pair. Mangled
    // so the symbol is valid and unique across modules and generic instantiations.
    public static string BuildVtableGlobalName(string concreteTypeName, string traitName)
        => $"{VtableGlobalNamePrefix}{Mangle(concreteTypeName)}__{Mangle(traitName)}";

    public static bool IsVtableGlobalName(string globalName)
        => globalName.StartsWith(VtableGlobalNamePrefix, StringComparison.Ordinal);

    // The per-(implementing type) drop thunk referenced by a vtable's Drop slot.
    // It takes the box pointer (rawmutptr<i8>), runs the concrete type's drop
    // (destructor + field drops), then frees the box. An owning `heap dyn` calls it
    // through the vtable at scope exit; borrowed trait objects never invoke it.
    public const string DropThunkNameSuffix = ".__dyn_drop";

    public static string BuildDropThunkName(string concreteTypeName)
        => $"{concreteTypeName}{DropThunkNameSuffix}";

    // Recovers the implementing type name from a drop-thunk function name, or null
    // if the name is not a dyn drop thunk.
    public static string? TryGetDropThunkConcreteType(string functionName)
        => functionName.EndsWith(DropThunkNameSuffix, StringComparison.Ordinal)
            ? functionName[..^DropThunkNameSuffix.Length]
            : null;

    // True for functions that are reachable only through a synthesized vtable global
    // (drop thunks), so SSA-only reachability analyses must treat them as roots.
    public static bool IsVtableReferencedRoot(string functionName)
        => functionName.EndsWith(DropThunkNameSuffix, StringComparison.Ordinal);

    private static string Mangle(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (var index = 0; index < name.Length; index++)
        {
            var ch = name[index];
            buffer[index] = ch is '.' or '<' or '>' or ',' or ' ' or ':' ? '_' : ch;
        }

        return new string(buffer);
    }

    private static string LastSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }
}
