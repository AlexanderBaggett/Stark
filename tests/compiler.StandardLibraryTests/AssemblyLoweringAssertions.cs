namespace compiler.StandardLibraryTests;

internal static class AssemblyLoweringAssertions
{
    private const string RequiredLinuxX64SyscallClobbers =
        "~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}";

    public static void ContainsDirectLinuxX64Syscall(string text, long syscallNumber)
    {
        var firstArgument = $"(i64 {syscallNumber}";
        var callLine = text
            .Split('\n')
            .FirstOrDefault(line =>
            {
                if (!line.Contains("call i64 asm sideeffect \"syscall\"", StringComparison.Ordinal))
                {
                    return false;
                }

                if (!line.Contains(RequiredLinuxX64SyscallClobbers, StringComparison.Ordinal)
                    || !line.Contains(" nounwind", StringComparison.Ordinal))
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
            });

        Assert.NotNull(callLine);
        Assert.Contains(RequiredLinuxX64SyscallClobbers, callLine, StringComparison.Ordinal);
        Assert.Contains(" nounwind", callLine, StringComparison.Ordinal);
    }

    public static void ContainsDirectLinuxArm64WriteSyscall(string text)
    {
        var callLine = text
            .Split('\n')
            .FirstOrDefault(line =>
                line.Contains(
                    "call i64 asm sideeffect \"mov x8, #64\\0Asvc #0\"",
                    StringComparison.Ordinal));

        Assert.NotNull(callLine);
        Assert.Contains("~{x8}", callLine, StringComparison.Ordinal);
        Assert.Contains("~{memory}", callLine, StringComparison.Ordinal);
        Assert.Contains(" nounwind", callLine, StringComparison.Ordinal);
    }

    public static void ContainsDirectLinuxX64WriteSyscall(string text)
    {
        var callLine = text
            .Split('\n')
            .FirstOrDefault(line =>
                line.Contains(
                    "call i64 asm sideeffect \"movq $$1, %rax\\0Asyscall\"",
                    StringComparison.Ordinal));

        Assert.NotNull(callLine);
        Assert.Contains(RequiredLinuxX64SyscallClobbers, callLine, StringComparison.Ordinal);
        Assert.Contains(" nounwind", callLine, StringComparison.Ordinal);
    }

    public static void DoesNotContainLinuxSyscallBridgeCalls(string text)
    {
        Assert.DoesNotContain("call i64 @LinuxSyscall", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "call i64 @System_Runtime_Platform_Linux_LinuxSyscall",
            text,
            StringComparison.Ordinal);
    }
}
