namespace Stark.Compiler;

public enum ConstProvenanceKind
{
    None,
    ImmutableBinding,
    StaticImmutableBinding,
    FrozenBorrow,
    ReadonlyRawPointer,
    TemporaryReadonlyView,
    PermanentConst
}

public static class ConstProvenanceFacts
{
    public static bool HasPermanentConstProvenance(ConstProvenanceKind kind) =>
        kind == ConstProvenanceKind.PermanentConst;

    public static ConstProvenanceKind FromPermanentConst(bool hasConstProvenance) =>
        hasConstProvenance ? ConstProvenanceKind.PermanentConst : ConstProvenanceKind.None;

    public static ConstProvenanceKind Normalize(bool hasConstProvenance, ConstProvenanceKind kind) =>
        hasConstProvenance ? ConstProvenanceKind.PermanentConst : kind;

    public static string? ToManifestText(ConstProvenanceKind kind) =>
        kind switch
        {
            ConstProvenanceKind.None => null,
            ConstProvenanceKind.ImmutableBinding => "immutable-binding",
            ConstProvenanceKind.StaticImmutableBinding => "static-immutable-binding",
            ConstProvenanceKind.FrozenBorrow => "frozen-borrow",
            ConstProvenanceKind.ReadonlyRawPointer => "readonly-raw-pointer",
            ConstProvenanceKind.TemporaryReadonlyView => "temporary-readonly-view",
            ConstProvenanceKind.PermanentConst => "permanent-const",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown const provenance kind.")
        };

    public static bool TryParseManifestText(string? text, out ConstProvenanceKind kind)
    {
        kind = text switch
        {
            null or "" => ConstProvenanceKind.None,
            "immutable-binding" => ConstProvenanceKind.ImmutableBinding,
            "static-immutable-binding" => ConstProvenanceKind.StaticImmutableBinding,
            "frozen-borrow" => ConstProvenanceKind.FrozenBorrow,
            "readonly-raw-pointer" => ConstProvenanceKind.ReadonlyRawPointer,
            "temporary-readonly-view" => ConstProvenanceKind.TemporaryReadonlyView,
            "permanent-const" => ConstProvenanceKind.PermanentConst,
            _ => ConstProvenanceKind.None
        };

        return text is null
            || text.Length == 0
            || kind != ConstProvenanceKind.None;
    }
}
