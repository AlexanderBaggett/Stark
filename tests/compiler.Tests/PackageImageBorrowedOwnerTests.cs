using System.Diagnostics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageBorrowedOwnerTests
{
    [Fact]
    public async Task BorrowingAnOwnerDoesNotConsumeItsLaterForwardedValue()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-borrow-owner-");
        try
        {
            var source = Path.Combine(directory.FullName, "BorrowLibrary.stark");
            var archive = Path.Combine(directory.FullName, "libBorrowLibrary.a");
            await File.WriteAllTextAsync(source, """
                module BorrowLibrary
                public struct Owner { public dynamic u8[0 max] Bytes; public u64[0 max] Token; }
                public noinline finite law u64[0 max] Inspect<T>(borrow T value) { return sizeof(T); }
                public noinline finite law T Relay<T>(T value) { return value; }
                public noinline finite law T Forward<T>(T value)
                {
                    stack u64[0 max] size = Inspect(value);
                    return Relay(value);
                }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var exit = await CompilerCli.RunAsync(
                [source, "--emit-lib", "--target", target.Triple, "-o", archive],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            File.Delete(source);
            var app = Path.Combine(directory.FullName, "App.stark");
            const string program = """
                import BorrowLibrary
                module App
                export fn i32[min max] main()
                {
                    stack Owner owner = new Owner() { Bytes = new(), Token = 42 };
                    stack Owner forwarded = Forward(owner);
                    return forwarded.Token == 42 ? 0 : 1;
                }
                """;
            await File.WriteAllTextAsync(app, program);
            var lowered = DefaultCompilerPipeline.Create().Run(new CompilationInput(program, app),
                new CompilerOptions(TargetInfo: target, StopAfterPassId: "lower-ssa",
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName)));
            Assert.True(lowered.Succeeded, string.Join("\n", lowered.Diagnostics));
            var ssa = lowered.Artifacts.GetRequired(CompilerArtifactKeys.SsaIr);
            var forward = Assert.Single(ssa.Functions,
                function => function.Name.Contains("BorrowLibrary_Forward", StringComparison.Ordinal));
            var relay = Assert.Single(forward.Blocks.SelectMany(block => block.Instructions)
                .OfType<SsaValueInstruction>().Select(instruction => instruction.Value)
                .OfType<SsaCallRValue>(), call => call.FunctionName.Contains("BorrowLibrary_Relay", StringComparison.Ordinal));
            Assert.IsNotType<SsaUndefValue>(Assert.Single(relay.Arguments));

            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            output.GetStringBuilder().Clear();
            errors.GetStringBuilder().Clear();
            exit = await CompilerCli.RunAsync(
                [app, "--emit-exe", "--target", target.Triple, "-I", directory.FullName, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            using var process = Process.Start(new ProcessStartInfo(binary) { UseShellExecute = false });
            Assert.NotNull(process);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
