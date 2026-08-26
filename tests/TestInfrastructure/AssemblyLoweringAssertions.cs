namespace compiler.TestInfrastructure;

internal static class AssemblyLoweringAssertions
{
    private const string RequiredLinuxX64SyscallClobbers =
        "~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}";

    public static void ContainsDirectLinuxX64Syscall(string text, long syscallNumber)
    {
        var firstArgument = $"(i64 {syscallNumber}";
        var embeddedTemplate = syscallNumber == 0
            ? "call i64 asm sideeffect \"xorq %rax, %rax\\0Asyscall\""
            : $"call i64 asm sideeffect \"movq $${syscallNumber}, %rax\\0Asyscall\"";
        var callLine = text
            .Split('\n')
            .FirstOrDefault(line =>
                HasRequiredLinuxX64SyscallContract(line)
                && (line.Contains(embeddedTemplate, StringComparison.Ordinal)
                    || HasSyscallNumberOperand(line, firstArgument)));

        Assert.NotNull(callLine);
    }

    public static void ContainsDirectLinuxArm64Syscall(string text, long syscallNumber)
    {
        var callLine = text
            .Split('\n')
            .FirstOrDefault(line =>
                line.Contains("call i64 asm sideeffect", StringComparison.Ordinal)
                && line.Contains($"mov x8, #{syscallNumber}\\0Asvc #0", StringComparison.Ordinal));

        Assert.NotNull(callLine);
        Assert.Contains("~{x8}", callLine, StringComparison.Ordinal);
        Assert.Contains("~{memory}", callLine, StringComparison.Ordinal);
        Assert.Contains(" nounwind", callLine, StringComparison.Ordinal);
    }

    public static void ContainsDirectLinuxArm64WriteSyscall(string text) =>
        ContainsDirectLinuxArm64Syscall(text, 64);

    public static void ContainsDirectLinuxX64WriteSyscall(string text) =>
        ContainsDirectLinuxX64Syscall(text, 1);

    public static void DoesNotContainLinuxSyscallBridgeCalls(string text)
    {
        Assert.DoesNotContain("call i64 @LinuxSyscall", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "call i64 @System_Runtime_Platform_Linux_LinuxSyscall",
            text,
            StringComparison.Ordinal);
    }

    private static bool HasRequiredLinuxX64SyscallContract(string line) =>
        line.Contains(RequiredLinuxX64SyscallClobbers, StringComparison.Ordinal)
        && line.Contains(" nounwind", StringComparison.Ordinal);

    private static bool HasSyscallNumberOperand(string line, string firstArgument)
    {
        if (!line.Contains("call i64 asm sideeffect \"syscall\"", StringComparison.Ordinal))
        {
            return false;
        }

        var argumentIndex = line.IndexOf(firstArgument, StringComparison.Ordinal);
        if (argumentIndex < 0)
        {
            return false;
        }

        var delimiterIndex = argumentIndex + firstArgument.Length;
        return delimiterIndex < line.Length
            && line[delimiterIndex] is ',' or ')';
    }
}
