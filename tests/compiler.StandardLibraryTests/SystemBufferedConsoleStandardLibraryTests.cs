using System.Diagnostics;
using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemBufferedConsoleStandardLibraryTests : StandardLibraryTestSuite
{
    [Theory]
    [InlineData("Linux", "x86_64-unknown-linux-gnu")]
    [InlineData("MacOS", "aarch64-apple-darwin")]
    [InlineData("Windows", "x86_64-pc-windows-msvc")]
    public void PlatformScratchPathsCompileForTheirTarget(string platform, string triple)
    {
        var root = Path.Combine(FindRepositoryRoot(), "stdlib", "src");
        var path = Path.Combine(root, "System", "Runtime", "Platform", platform + ".stark");
        var target = new LlvmTargetInfo(triple, "e-p:64:64-i64:64-n8:16:32:64-S128");
        var resolver = new TargetAwareStdLibModuleResolver(new FileSystemModuleResolver(root), [root], target);
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(File.ReadAllText(path), path),
            new CompilerOptions(EmitLlvmIr: true, TargetInfo: target, ModuleResolver: resolver));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
    }

    private const string Program = """
        import System.Console
        import System.Text
        import System.IO
        import System.Runtime.Platform.Linux
        module BufferedConsoleTests
        fn bool Ok(IOStatus status) {
            switch(status) { case IOStatus.Ok: return true; case IOStatus.Err(var error): return false; }
        }
        noinline fn IOStatus Dynamic(mut borrow BufferedUtf8Writer writer, unicode text) {
            return writer.WriteLine(text);
        }
        export unsafe ffi fn i32[min max] main() {
            { stack mut BufferedUtf8Writer dropped = new();
              if (!Ok(dropped.Write("discarded without flush"))) { return 10; } }
            stack mut BufferedUtf8Writer writer = new();
            if (writer.PendingBytes() != 0 || !Ok(writer.Flush())) { return 1; }
            if (!Ok(writer.WriteLine((unicode)"arbitrary café 🌍\0"))) { return 2; }
            if (writer.PendingBytes() == 0) { return 3; }
            stack mut i8[min max][8193] bytes;
            for willexit(stack mut u16[0 8193] i=0; i<8193; i+=1) { bytes[i]=120; }
            stack i8[min max][] view = slice(&bytes[0],8193);
            if (!Ok(writer.WriteLine(view))) { return 4; }
            stack mut i32[min max][4] scalars = {-1,55296,1114112,128512};
            stack Unicode text = new Unicode() { Data=&scalars[0], Length=4, Capacity=4 };
            if (!Ok(Dynamic(writer,UnicodeView(text)))) { return 5; }
            if (!Ok(writer.WriteLine("tail")) || !Ok(writer.Flush()) || !Ok(writer.Flush())
                || writer.PendingBytes() != 0) { return 6; }
            stack u16[min max][7] lengths = {0,1,511,512,8191,8192,8193};
            for willexit(stack mut u8[0 7] i=0; i<7; i+=1) {
                stack u16[0 8193] count = (u16[0 8193])lengths[i];
                stack i8[min max][] line = slice(&bytes[0],count);
                if (!Ok(WriteLine(line))) { return 11; }
            }
            stack mut BufferedUtf8Writer error = new(true);
            if (!Ok(error.WriteLine((unicode)"stderr é🌍")) || !Ok(error.Flush())) { return 7; }
            if (!Ok(writer.Write("pending")) || System.Runtime.Platform.Linux.close(1) != 0) { return 8; }
            if (Ok(writer.Flush()) || Ok(writer.Write("never replay")) || Ok(writer.Write((unicode)""))
                || Ok(writer.Flush())) { return 9; }
            return 0;
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedUtf8OutputAndStickyFailureWorkThroughSourceAndPackages(bool packaged)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target) || !OperatingSystem.IsLinux()) return;
        var repository = FindRepositoryRoot();
        var root = packaged ? await SharedStdlibPackage.GetDirectoryAsync() : Path.Combine(repository, "stdlib", "src");
        var directory = Directory.CreateTempSubdirectory("stark-buffered-console-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, "app");
            await File.WriteAllTextAsync(source, Program);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = await CompilerCli.RunAsync([source, "--emit-exe", "-I", root, "--target", target.Triple, "-o", binary],
                new StringReader(""), stdout, stderr);
            Assert.True(exit == 0, stdout + "\n" + stderr);
            using var process = Process.Start(new ProcessStartInfo(binary) {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            var immediateLines = string.Concat(new[] { 0, 1, 511, 512, 8191, 8192, 8193 }
                .Select(length => new string('x', length) + "\n"));
            Assert.Equal("arbitrary café 🌍\0\n" + new string('x', 8193) + "\n���😀\ntail\n" + immediateLines, await output);
            Assert.Equal("stderr é🌍\n", await error);

            var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(Program, source),
                new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa", TargetInfo: target,
                    ModuleResolver: new FileSystemModuleResolver(root)));
            Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
            var ssa = result.Artifacts.GetRequired(CompilerArtifactKeys.OptimizedSsaIr);
            var main = Assert.Single(ssa.Functions, function => function.Name == "main");
            Assert.Contains(main.Blocks.SelectMany(block => block.Instructions).OfType<SsaValueInstruction>(),
                instruction => instruction.Value is SsaCallRValue call && call.Arguments.Any(arg => arg is SsaStaticArrayAddressValue));
            var dynamic = Assert.Single(ssa.Functions, function => function.Name == "Dynamic");
            Assert.DoesNotContain(dynamic.Blocks.SelectMany(block => block.Instructions).OfType<SsaValueInstruction>(),
                instruction => instruction.Value is SsaCallRValue call && call.Arguments.Any(arg => arg is SsaStaticArrayAddressValue));
        }
        finally { directory.Delete(recursive: true); }
    }
}
