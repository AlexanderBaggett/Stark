namespace Stark.Compiler;

internal enum StarkAsmRegisterClass
{
    Unknown,
    GeneralPurpose,
    FloatingPoint
}

internal static class StarkAsmRegisterFacts
{
    private static readonly IReadOnlySet<string> X86_64GeneralPurposeRegisters = CreateSet(
    [
        "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp",
        "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"
    ]);

    private static readonly IReadOnlySet<string> X86_64FloatingPointRegisters = CreateIndexedSet("xmm", 16);

    private static readonly IReadOnlySet<string> AArch64GeneralPurposeRegisters = CreateSet(
    [
        "x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7",
        "x8", "x9", "x10", "x11", "x12", "x13", "x14", "x15",
        "x16", "x17", "x18", "x19", "x20", "x21", "x22", "x23",
        "x24", "x25", "x26", "x27", "x28", "x29", "x30", "sp",
        "w0", "w1", "w2", "w3", "w4", "w5", "w6", "w7",
        "w8", "w9", "w10", "w11", "w12", "w13", "w14", "w15",
        "w16", "w17", "w18", "w19", "w20", "w21", "w22", "w23",
        "w24", "w25", "w26", "w27", "w28", "w29", "w30"
    ]);

    private static readonly IReadOnlySet<string> AArch64FloatingPointRegisters = CreateSet(
        Enumerable.Range(0, 32).Select(index => $"s{index}")
            .Concat(Enumerable.Range(0, 32).Select(index => $"d{index}")));

    private static readonly IReadOnlySet<string> RiscV64GeneralPurposeRegisters = CreateSet(
    [
        "x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7",
        "x8", "x9", "x10", "x11", "x12", "x13", "x14", "x15",
        "x16", "x17", "x18", "x19", "x20", "x21", "x22", "x23",
        "x24", "x25", "x26", "x27", "x28", "x29", "x30", "x31",
        "zero", "ra", "sp", "gp", "tp",
        "t0", "t1", "t2", "t3", "t4", "t5", "t6",
        "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10", "s11",
        "a0", "a1", "a2", "a3", "a4", "a5", "a6", "a7",
        "fp"
    ]);

    private static readonly IReadOnlySet<string> RiscV64FloatingPointRegisters = CreateSet(Array.Empty<string>());

    private static readonly IReadOnlySet<string> X86GeneralPurposeRegisters = CreateSet(
    [
        "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp"
    ]);

    private static readonly IReadOnlySet<string> X86FloatingPointRegisters = CreateIndexedSet("xmm", 8);

    private static readonly IReadOnlySet<string> Arm32GeneralPurposeRegisters = CreateSet(
    [
        "r0", "r1", "r2", "r3", "r4", "r5", "r6", "r7",
        "r8", "r9", "r10", "r11", "r12", "sp", "lr", "pc"
    ]);

    private static readonly IReadOnlySet<string> Arm32FloatingPointRegisters = CreateSet(Array.Empty<string>());

    public static bool IsValidRegister(StarkAsmArchitecture architecture, string registerName)
    {
        return TryGetRegisterClass(architecture, registerName, out _);
    }

    public static bool TryGetRegisterClass(
        StarkAsmArchitecture architecture,
        string registerName,
        out StarkAsmRegisterClass registerClass)
    {
        var normalized = Normalize(registerName);
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 => TryGetRegisterClass(normalized, X86_64GeneralPurposeRegisters, X86_64FloatingPointRegisters, out registerClass),
            StarkAsmArchitecture.AArch64 => TryGetRegisterClass(normalized, AArch64GeneralPurposeRegisters, AArch64FloatingPointRegisters, out registerClass),
            StarkAsmArchitecture.RiscV64 => TryGetRegisterClass(normalized, RiscV64GeneralPurposeRegisters, RiscV64FloatingPointRegisters, out registerClass),
            StarkAsmArchitecture.X86 => TryGetRegisterClass(normalized, X86GeneralPurposeRegisters, X86FloatingPointRegisters, out registerClass),
            StarkAsmArchitecture.Arm32 => TryGetRegisterClass(normalized, Arm32GeneralPurposeRegisters, Arm32FloatingPointRegisters, out registerClass),
            _ => TryGetUnknownRegisterClass(out registerClass)
        };
    }

    public static string Normalize(string registerName)
    {
        return registerName.Trim().ToLowerInvariant();
    }

    private static bool TryGetRegisterClass(
        string normalizedRegisterName,
        IReadOnlySet<string> generalPurposeRegisters,
        IReadOnlySet<string> floatingPointRegisters,
        out StarkAsmRegisterClass registerClass)
    {
        if (generalPurposeRegisters.Contains(normalizedRegisterName))
        {
            registerClass = StarkAsmRegisterClass.GeneralPurpose;
            return true;
        }

        if (floatingPointRegisters.Contains(normalizedRegisterName))
        {
            registerClass = StarkAsmRegisterClass.FloatingPoint;
            return true;
        }

        registerClass = StarkAsmRegisterClass.Unknown;
        return false;
    }

    private static bool TryGetUnknownRegisterClass(out StarkAsmRegisterClass registerClass)
    {
        registerClass = StarkAsmRegisterClass.Unknown;
        return false;
    }

    private static IReadOnlySet<string> CreateSet(IEnumerable<string> registers)
    {
        return registers.ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> CreateIndexedSet(string prefix, int count)
    {
        return CreateSet(Enumerable.Range(0, count).Select(index => $"{prefix}{index}"));
    }
}
