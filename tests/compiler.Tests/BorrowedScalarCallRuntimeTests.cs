using System.Diagnostics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class BorrowedScalarCallRuntimeTests
{
    private const string Program = """
        module BorrowedScalar
        unsafe ffi finite rawmutptr<u32[0 max]> Next(rawmutptr<u32[0 max]> value);
        unsafe ffi finite u32[0 max] CallsCount();
        noinline finite void Set(mut borrow u32[0 max] value) { unsafe { *((rawmutptr<u32[0 max]>)(&value)) = 42; } }
        noinline finite void SetWithToken(mut borrow u32[0 max] value, u32[0 max] token) { unsafe { *((rawmutptr<u32[0 max]>)(&value)) = token; } }
        unsafe ffi finite u32[0 max] Redirect(rawmutptr<rawmutptr<u32[0 max]>> cursor, rawmutptr<u32[0 max]> next);
        struct Cell {
            u32[0 max] Value;
            finite law retborrow u32[0 max] Get(borrow Cell self) { return self.Value; }
        }
        noinline finite law bool Equal(borrow u32[0 max] value) { unsafe { return *((rawptr<u32[0 max]>)(&value)) == 42; } }
        export unsafe ffi fn i32[min max] main() {
            stack Cell cell = new Cell() { Value = 42 };
            if (!Equal(cell.Get())) { return 4; }
            stack mut u32[0 max] value = 3;
            Set(*Next(&value));
            if (CallsCount() != 1 || value != 42 || !Equal(*Next(&value)) || CallsCount() != 2) { return 1; }
            stack fnptr<finite void(mut borrow u32[0 max])> callback = Set;
            value = 7;
            callback(*Next(&value));
            if (CallsCount() != 3 || value != 42) { return 2; }
            stack mut u32[0 max] other = 11;
            stack mut rawmutptr<u32[0 max]> cursor = &value;
            SetWithToken(*cursor, Redirect(&cursor, &other));
            if (value != 51 || other != 11 || cursor != &other) { return 3; }
            return 0;
        }
        """;

    [Fact]
    public void ScalarBorrowsKeepValueTypesAndEvaluatedAddressesInInitialIr()
    {
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(Program),
            new CompilerOptions(EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var mir = result.Artifacts.GetRequired(CompilerArtifactKeys.MidLevelIr);
        var conversions = mir.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Statements)
            .Select(s => s.Value).OfType<MidLevelIrConvertRValue>();
        Assert.DoesNotContain(conversions, conversion =>
            conversion.Operand.Type.Kind == StarkTypeKind.Integer
            && conversion.Operand.Type.BorrowKind == StarkBorrowKind.None
            && StarkTypeSymbols.IsPointerBackedBorrowType(conversion.Type));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Empty(LlvmModuleLint.Check(llvm));
    }

    [Fact]
    public async Task MutableBorrowUsesOriginalReferentAndEvaluatesPointerOnce()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-borrowed-scalar-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(source, Program);
            var observer = Path.Combine(directory.FullName, "observer.c");
            await File.WriteAllTextAsync(observer, """
                #include <stdint.h>
                static uint32_t calls;
                uint32_t *Next(uint32_t *value) { ++calls; return value; }
                uint32_t CallsCount(void) { return calls; }
                uint32_t Redirect(uint32_t **cursor, uint32_t *next) { *cursor = next; return 51; }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var exit = await CompilerCli.RunAsync([source, "--emit-exe", "--target", target.Triple, "--native-source", observer, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            using var process = Process.Start(new ProcessStartInfo(binary) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            Assert.NotNull(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stdout);
            Assert.Equal("", await stderr);
        }
        finally { directory.Delete(recursive: true); }
    }
}
