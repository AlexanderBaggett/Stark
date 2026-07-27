namespace Stark.Compiler;

/// <summary>
/// The atomic operation surface shared by every System.Threading atomic type
/// (docs/StandardLibrary/System.Threading.md). RMW operations return the previous value;
/// CompareExchange returns whether the swap happened.
/// </summary>
internal enum SystemThreadingAtomicOperation
{
    Load,
    Store,
    Add,
    Sub,
    And,
    Or,
    Xor,
    Exchange,
    CompareExchange
}

/// <summary>
/// Lowering strategy tiers (doc 12 §4). The tier is an implementation fact derived
/// from the value width, never an API difference.
/// </summary>
internal enum SystemThreadingAtomicTier
{
    /// <summary>8/16/32/64-bit and bool: single hardware atomic instructions.</summary>
    SingleInstruction,

    /// <summary>24/48/96/128-bit: lock-free CAS loop on the power-of-2 storage container.</summary>
    CompareExchangeLoop,

    /// <summary>192-1024-bit: the struct embeds its own spinlock word; operations serialize through it.</summary>
    EmbeddedLock
}

/// <summary>
/// One recognized atomic builtin method: which atomic type it belongs to, the value
/// shape, and the operation it performs.
/// </summary>
internal readonly record struct SystemThreadingAtomicBuiltin(
    string AtomicTypeName,
    int ValueBitWidth,
    bool IsUnsigned,
    bool IsBool,
    SystemThreadingAtomicOperation Operation)
{
    public SystemThreadingAtomicTier Tier => SystemThreadingAtomicFacts.GetTier(ValueBitWidth, IsBool);

    /// <summary>
    /// The power-of-2 bit width the value is stored and atomically operated on.
    /// Equals the value width for tier 1/3; the next power-of-2 container for tier 2.
    /// </summary>
    public int StorageBitWidth => SystemThreadingAtomicFacts.GetStorageBitWidth(ValueBitWidth, IsBool);

    public int StorageAlignmentBytes => SystemThreadingAtomicFacts.GetStorageAlignmentBytes(ValueBitWidth, IsBool);

    public bool ReturnsPreviousValue => Operation is
        SystemThreadingAtomicOperation.Load
        or SystemThreadingAtomicOperation.Add
        or SystemThreadingAtomicOperation.Sub
        or SystemThreadingAtomicOperation.And
        or SystemThreadingAtomicOperation.Or
        or SystemThreadingAtomicOperation.Xor
        or SystemThreadingAtomicOperation.Exchange;
}

/// <summary>
/// Recognition and layout facts for the System.Threading atomic builtins. The atomic
/// types are stdlib structs whose semicolon-bodied methods lower through compiler-known
/// builtins onto LLVM atomic instructions (seq-cst only). Shared by SSA validation
/// (contract checking) and LLVM emission (instruction selection) so the two stay in
/// lockstep with the stdlib declarations.
/// </summary>
internal static class SystemThreadingAtomicFacts
{
    public const string ModuleName = "System.Threading";

    private const string ModulePrefix = "System.Threading.";
    private const string BoolTypeName = "AtomicBool";
    private const string SignedTypeNamePrefix = "AtomicI";
    private const string UnsignedTypeNamePrefix = "AtomicU";

    /// <summary>Every integer width Stark has — the atomic family mirrors it exactly (doc 12 §2).</summary>
    private static readonly int[] SupportedValueBitWidths =
    [
        8,
        16,
        24,
        32,
        48,
        64,
        96,
        128,
        192,
        256,
        384,
        512,
        768,
        1024
    ];

    public static IReadOnlyList<int> ValueBitWidths => SupportedValueBitWidths;

    /// <summary>
    /// Recognizes one atomic builtin method by name. Accepts the fully-qualified
    /// "System.Threading.AtomicI64.Add" form (imported references) and the in-module
    /// "AtomicI64.Add" form (compiling System.Threading itself).
    /// </summary>
    public static bool TryGetAtomicBuiltin(
        string moduleName,
        string functionName,
        out SystemThreadingAtomicBuiltin builtin)
    {
        builtin = default;

        string sourceName;
        if (functionName.StartsWith(ModulePrefix, StringComparison.Ordinal))
        {
            sourceName = functionName[ModulePrefix.Length..];
        }
        else if (string.Equals(moduleName, ModuleName, StringComparison.Ordinal))
        {
            sourceName = functionName;
        }
        else
        {
            return false;
        }

        // Atomic builtins are always methods: exactly "AtomicXxx.Operation".
        var separatorIndex = sourceName.IndexOf('.');
        if (separatorIndex <= 0
            || separatorIndex != sourceName.LastIndexOf('.')
            || separatorIndex == sourceName.Length - 1)
        {
            return false;
        }

        var typeName = sourceName[..separatorIndex];
        var operationName = sourceName[(separatorIndex + 1)..];

        if (!TryParseAtomicTypeName(typeName, out var valueBitWidth, out var isUnsigned, out var isBool))
        {
            return false;
        }

        if (!TryParseOperationName(operationName, isBool, out var operation))
        {
            return false;
        }

        builtin = new SystemThreadingAtomicBuiltin(typeName, valueBitWidth, isUnsigned, isBool, operation);
        return true;
    }

    /// <summary>
    /// Parses an atomic type name: "AtomicBool", "AtomicI8".."AtomicI1024",
    /// "AtomicU8".."AtomicU1024". Only the widths Stark's integer family has are accepted.
    /// </summary>
    public static bool TryParseAtomicTypeName(
        string typeName,
        out int valueBitWidth,
        out bool isUnsigned,
        out bool isBool)
    {
        valueBitWidth = 0;
        isUnsigned = false;
        isBool = false;

        if (string.Equals(typeName, BoolTypeName, StringComparison.Ordinal))
        {
            valueBitWidth = 1;
            isBool = true;
            return true;
        }

        string widthText;
        if (typeName.StartsWith(SignedTypeNamePrefix, StringComparison.Ordinal))
        {
            widthText = typeName[SignedTypeNamePrefix.Length..];
        }
        else if (typeName.StartsWith(UnsignedTypeNamePrefix, StringComparison.Ordinal))
        {
            isUnsigned = true;
            widthText = typeName[UnsignedTypeNamePrefix.Length..];
        }
        else
        {
            return false;
        }

        if (!int.TryParse(widthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var width)
            || !widthText.Equals(width.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var supportedWidth in SupportedValueBitWidths)
        {
            if (width == supportedWidth)
            {
                valueBitWidth = width;
                return true;
            }
        }

        return false;
    }

    public static string GetAtomicTypeName(int valueBitWidth, bool isUnsigned, bool isBool)
    {
        if (isBool)
        {
            return BoolTypeName;
        }

        return (isUnsigned ? UnsignedTypeNamePrefix : SignedTypeNamePrefix) + valueBitWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static SystemThreadingAtomicTier GetTier(int valueBitWidth, bool isBool)
    {
        if (isBool)
        {
            return SystemThreadingAtomicTier.SingleInstruction;
        }

        return valueBitWidth switch
        {
            8 or 16 or 32 or 64 => SystemThreadingAtomicTier.SingleInstruction,
            24 or 48 or 96 or 128 => SystemThreadingAtomicTier.CompareExchangeLoop,
            _ => SystemThreadingAtomicTier.EmbeddedLock
        };
    }

    /// <summary>
    /// The power-of-2 container width the value is stored in. Tier-2 values live in the
    /// next power-of-2 container (i24 in i32, i48 in i64, i96 in i128) so that hardware
    /// compare-exchange can operate on them; everything else stores at its natural width.
    /// </summary>
    public static int GetStorageBitWidth(int valueBitWidth, bool isBool)
    {
        if (isBool)
        {
            return 8;
        }

        return valueBitWidth switch
        {
            24 => 32,
            48 => 64,
            96 => 128,
            _ => valueBitWidth
        };
    }

    public static int GetStorageAlignmentBytes(int valueBitWidth, bool isBool)
    {
        var storageBitWidth = GetStorageBitWidth(valueBitWidth, isBool);

        // Embedded-lock widths are operated on under the lock, never by hardware atomic
        // instructions, so they only need their natural Stark alignment (8 bytes).
        return GetTier(valueBitWidth, isBool) == SystemThreadingAtomicTier.EmbeddedLock
            ? 8
            : storageBitWidth / 8;
    }

    /// <summary>
    /// Validates the struct layout the lowering relies on. Tier 1/2: a single
    /// power-of-2 container field at offset 0 holding the sign/zero-extended value
    /// (the canonical-extension invariant). Tier 3 (embedded lock): the value at
    /// offset 0 followed by a u32 spinlock word.
    /// </summary>
    public static bool HasValidAtomicFieldLayout(NamedTypeSymbol atomicStructType, SystemThreadingAtomicBuiltin builtin)
    {
        if (builtin.Tier == SystemThreadingAtomicTier.EmbeddedLock)
        {
            return atomicStructType.OrderedFields.Count == 2
                && atomicStructType.OrderedFields[0].Type.Kind == StarkTypeKind.Integer
                && atomicStructType.OrderedFields[0].Type.BitWidth == builtin.ValueBitWidth
                && atomicStructType.OrderedFields[0].Type.IsUnsigned == builtin.IsUnsigned
                && atomicStructType.OrderedFields[1].Type.Kind == StarkTypeKind.Integer
                && atomicStructType.OrderedFields[1].Type.BitWidth == 32
                && atomicStructType.OrderedFields[1].Type.IsUnsigned;
        }

        return atomicStructType.OrderedFields.Count == 1
            && atomicStructType.OrderedFields[0].Type.Kind == StarkTypeKind.Integer
            && atomicStructType.OrderedFields[0].Type.BitWidth == builtin.StorageBitWidth
            && (builtin.IsBool || atomicStructType.OrderedFields[0].Type.IsUnsigned == builtin.IsUnsigned);
    }

    /// <summary>
    /// The layout requirement description used in diagnostics when
    /// <see cref="HasValidAtomicFieldLayout"/> fails.
    /// </summary>
    public static string DescribeRequiredAtomicFieldLayout(SystemThreadingAtomicBuiltin builtin)
    {
        return builtin.Tier == SystemThreadingAtomicTier.EmbeddedLock
            ? $"System.Threading atomic type '{builtin.AtomicTypeName}' must store its value at offset 0 followed by a u32 lock word (the embedded-lock layout)."
            : $"System.Threading atomic type '{builtin.AtomicTypeName}' must store its value as a single {(builtin.IsUnsigned ? "u" : "i")}{builtin.StorageBitWidth} container field at offset 0 (the canonical-extension invariant).";
    }

    private static bool TryParseOperationName(
        string operationName,
        bool isBool,
        out SystemThreadingAtomicOperation operation)
    {
        operation = default;

        switch (operationName)
        {
            case "Load":
                operation = SystemThreadingAtomicOperation.Load;
                return true;
            case "Store":
                operation = SystemThreadingAtomicOperation.Store;
                return true;
            case "Exchange":
                operation = SystemThreadingAtomicOperation.Exchange;
                return true;
            case "CompareExchange":
                operation = SystemThreadingAtomicOperation.CompareExchange;
                return true;
        }

        // Arithmetic and bitwise RMW operations exist only on the integer atomics;
        // AtomicBool has no Add/Sub/And/Or/Xor surface.
        if (isBool)
        {
            return false;
        }

        switch (operationName)
        {
            case "Add":
                operation = SystemThreadingAtomicOperation.Add;
                return true;
            case "Sub":
                operation = SystemThreadingAtomicOperation.Sub;
                return true;
            case "And":
                operation = SystemThreadingAtomicOperation.And;
                return true;
            case "Or":
                operation = SystemThreadingAtomicOperation.Or;
                return true;
            case "Xor":
                operation = SystemThreadingAtomicOperation.Xor;
                return true;
            default:
                return false;
        }
    }
}
