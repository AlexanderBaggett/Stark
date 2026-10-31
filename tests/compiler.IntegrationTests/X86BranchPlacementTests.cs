using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class X86BranchPlacementTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NativeBranchesAndFusedPairsStayWithinFetchBoundaries(bool thinLto, bool nativeSource)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)
            || !target.Triple.StartsWith("x86_64-", StringComparison.Ordinal)
            || (NativeToolchain.FindExecutable("objdump") ?? NativeToolchain.FindExecutable("llvm-objdump")) is not { } objdump
            || (thinLto && !NativeToolchain.SupportsExecutableThinLto()))
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("stark-branch-placement-");
        try
        {
            var objectPath = Path.Combine(directory.FullName, "probe.o");
            var binaryPath = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "probe.exe" : "probe");
            // Sweep instruction offsets so the test exercises boundary crossings,
            // independently of a benchmark's particular loop or function layout.
            var offsets = Enumerable.Range(0, 32).ToArray();
            NativeToolchainResult emitted;
            if (nativeSource)
            {
                var sourcePath = Path.Combine(directory.FullName, "probe.c");
                await File.WriteAllTextAsync(sourcePath, BuildNativeSource(offsets));
                emitted = NativeToolchain.EmitNativeObject(sourcePath, objectPath, targetInfo: target);
            }
            else
            {
                emitted = NativeToolchain.EmitObject(BuildLlvmSource(offsets), objectPath,
                    targetInfo: target, enableLto: thinLto);
            }

            Assert.True(emitted.Succeeded, emitted.StandardError);
            var linked = NativeToolchain.LinkExecutable([objectPath], binaryPath, targetInfo: target, enableLto: thinLto);
            Assert.True(linked.Succeeded, linked.StandardError);
            var execution = await RunAsync(binaryPath);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal("", execution.Stdout);
            Assert.Equal("", execution.Stderr);

            // Non-LTO object output and final LTO machine code must both satisfy
            // the placement contract. Do not just assert command-line spelling.
            var disassembly = await RunAsync(objdump, "-d", "--no-show-raw-insn", thinLto ? binaryPath : objectPath);
            Assert.Equal(0, disassembly.ExitCode);
            AssertAlignedBranches(disassembly.Stdout, offsets.Length);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("aarch64-unknown-linux-gnu")]
    [InlineData("arm64-apple-macosx14.0.0")]
    [InlineData("aarch64-pc-windows-msvc")]
    public void NonX86TargetsStillEmitNativeObjects(string triple)
    {
        if (!NativeToolchain.Resolve().Clang.IsAvailable) return;
        var directory = Directory.CreateTempSubdirectory("stark-non-x86-placement-");
        try
        {
            var output = Path.Combine(directory.FullName, "probe.o");
            var emitted = NativeToolchain.EmitObject("define i32 @Probe(i32 %value) { ret i32 %value }", output,
                targetInfo: new LlvmTargetInfo(triple, null));
            Assert.True(emitted.Succeeded, emitted.StandardError);
            Assert.True(new FileInfo(output).Length > 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string BuildLlvmSource(int[] offsets)
    {
        var text = new StringBuilder("@data = global [4 x i32] [i32 0, i32 3, i32 5, i32 0], align 4\n");
        foreach (var offset in offsets)
        {
            var padding = string.Join("\n", Enumerable.Range(0, offset).Select(i => $"  %pad{i} = load volatile i32, ptr %p, align 4"));
            text.AppendLine($$"""
                define i32 @Probe{{offset}}(ptr %p) noinline align 32 {
                entry:
                  {{padding}}
                  %value = load volatile i32, ptr %p, align 4
                  %otherp = getelementptr i32, ptr %p, i64 3
                  %other = load volatile i32, ptr %otherp, align 4
                  %square = mul i32 %value, %value
                  %othersquare = mul i32 %other, %other
                  %zero = icmp eq i32 %square, %othersquare
                  br i1 %zero, label %yes, label %no
                yes:
                  %a = getelementptr i32, ptr %p, i64 1
                  store volatile i32 3, ptr %a, align 4
                  %x = load volatile i32, ptr %a, align 4
                  ret i32 %x
                no:
                  %b = getelementptr i32, ptr %p, i64 2
                  %y = load volatile i32, ptr %b, align 4
                  ret i32 %y
                }
                """);
        }
        text.AppendLine("define i32 @main() {\nentry:");
        for (var i = 0; i < offsets.Length; i++)
        {
            text.AppendLine($$"""
                  store volatile i32 0, ptr @data, align 4
                  %x{{i}} = call i32 @Probe{{offsets[i]}}(ptr @data)
                  store volatile i32 1, ptr @data, align 4
                  %y{{i}} = call i32 @Probe{{offsets[i]}}(ptr @data)
                  %a{{i}} = xor i32 %x{{i}}, 3
                  %b{{i}} = xor i32 %y{{i}}, 5
                  %c{{i}} = or i32 %a{{i}}, %b{{i}}
                """);
            text.AppendLine(i == 0 ? "  %r0 = or i32 %c0, 0" : $"  %r{i} = or i32 %r{i - 1}, %c{i}");
        }
        text.AppendLine($"  ret i32 %r{offsets.Length - 1}\n}}");
        return text.ToString();
    }

    private static string BuildNativeSource(int[] offsets)
    {
        var text = new StringBuilder("volatile int data[4] = {0, 3, 5, 0};\n");
        foreach (var offset in offsets)
        {
            var padding = string.Concat(Enumerable.Repeat("(void)*p; ", offset));
            text.AppendLine($$"""
                __attribute__((noinline, aligned(32))) int Probe{{offset}}(volatile int *p) {
                    {{padding}}
                    int x = *p; int y = p[3];
                    if (x * x == y * y) { p[1] = 3; return p[1]; }
                    return p[2];
                }
                """);
        }
        text.AppendLine("int main(void) { int result = 0;");
        foreach (var offset in offsets)
        {
            text.AppendLine($"data[0] = 0; result |= Probe{offset}(data) ^ 3; data[0] = 1; result |= Probe{offset}(data) ^ 5;");
        }
        return text.AppendLine("return result; }").ToString();
    }

    private static void AssertAlignedBranches(string disassembly, int expectedBranches)
    {
        var instructions = new List<(ulong Address, string Text)>();
        var inProbe = false;
        var branches = 0;
        var fusedPairs = 0;
        foreach (var line in disassembly.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^[0-9a-fA-F]+ <[^>]+>:$"))
            {
                inProbe = Regex.IsMatch(line, @"<_?Probe\d+(?:\.[^>]*)?>:");
                instructions.Clear();
                continue;
            }
            if (!inProbe) continue;
            var match = Regex.Match(line, @"^\s*([0-9a-fA-F]+):\s+(.+)$");
            if (!match.Success) continue;
            var current = (Address: ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber), Text: match.Groups[2].Value);
            if (instructions.Count > 0)
            {
                var previous = instructions[^1];
                if (Regex.IsMatch(previous.Text, @"\bj[a-z]+\b"))
                {
                    branches++;
                    var start = previous.Address;
                    if (instructions.Count > 1 && IsFusibleCompare(instructions[^2].Text))
                    {
                        start = instructions[^2].Address;
                        fusedPairs++;
                    }
                    Assert.True(start / 32 == current.Address / 32,
                        $"Branch/pair crosses or ends at a fetch boundary: {start:x}..{current.Address:x}: {previous.Text}");
                }
            }
            instructions.Add(current);
        }
        Assert.True(branches >= expectedBranches, $"Expected branch-offset sweep, found {branches} branches.");
        Assert.True(fusedPairs > 0, "Expected at least one compare/test and conditional branch pair.");
    }

    private static bool IsFusibleCompare(string instruction)
    {
        // LLVM's x86 assembler excludes RIP-relative operands and the
        // memory/immediate compare form from its macro-fusion classification.
        return Regex.IsMatch(instruction, @"\b(cmp|test)[a-z]*\b")
            && !instruction.Contains("%rip", StringComparison.Ordinal)
            && !(instruction.Contains('$') && instruction.Contains('('));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string tool, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
