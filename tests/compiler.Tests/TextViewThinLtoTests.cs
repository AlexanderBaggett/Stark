using System.Diagnostics;
using System.Text.RegularExpressions;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class TextViewThinLtoTests
{
    [Fact]
    public async Task BothViewCallsRemainInlineableAcrossSeparatelyCompiledModules()
    {
        var tools = NativeToolchain.Resolve();
        if (!OperatingSystem.IsLinux() || !tools.Clang.IsAvailable || !tools.Lld.IsAvailable
            || !NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;

        var directory = Directory.CreateTempSubdirectory("stark-text-view-lto-");
        try
        {
            const string declarations = """
                module ViewLibrary
                public inline finite law ascii Narrow(ascii value);
                public inline finite law unicode Wide(unicode value);
                """;
            var library = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                module ViewLibrary
                public inline finite law ascii Narrow(ascii value) { return value; }
                public inline finite law unicode Wide(unicode value) { return value; }
                """), new CompilerOptions(EmitLlvmIr: true, TargetInfo: target, QualifyModuleSymbols: true));
            Assert.True(library.Succeeded, string.Join("\n", library.Diagnostics));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "ViewLibrary.stark"), declarations);
            var consumer = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                import ViewLibrary
                module Consumer
                export unsafe ffi finite i32[min max] main()
                {
                    if (Narrow("unrelated bytes") != "unrelated bytes") { return 1; }
                    if (Wide((unicode)"λ🦉") != (unicode)"λ🦉") { return 2; }
                    return 0;
                }
                """), new CompilerOptions(EmitLlvmIr: true, TargetInfo: target,
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName)));
            Assert.True(consumer.Succeeded, string.Join("\n", consumer.Diagnostics));
            const string calls = @"\b(?:call|invoke)\b[^\n]*@ViewLibrary_(?:Narrow|Wide)\(";
            Assert.Equal(2, Regex.Matches(
                consumer.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text, calls).Count);

            // Only declarations enter the caller, so Stark's source inliner cannot
            // hide a mismatched LLVM function type at the native LTO boundary.
            foreach (var (name, result) in new[] { ("library", library), ("consumer", consumer) })
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name + ".ll"),
                    result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text);
                await Run(tools.Clang.CommandPathOrName, directory.FullName,
                    "-O3", "-flto=thin", "-c", name + ".ll", "-o", name + ".o");
            }
            await Run(tools.Clang.CommandPathOrName, directory.FullName,
                "-flto=thin", "-fuse-ld=" + tools.Lld.Path, "library.o", "consumer.o",
                "-Wl,--save-temps", "-o", "app");
            await Run(Path.Combine(directory.FullName, "app"), directory.FullName);
            await Run(tools.Clang.CommandPathOrName, directory.FullName,
                "-S", "-emit-llvm", "consumer.o.4.opt.bc", "-o", "optimized.ll");
            var optimized = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "optimized.ll"));
            Assert.Empty(Regex.Matches(optimized, calls));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclarationPreservesReachableReadsAndWrites(bool sourceFree)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-payload-write-");
        try
        {
            var librarySource = Path.Combine(directory.FullName, "PayloadLibrary.stark");
            var archive = Path.Combine(directory.FullName, "libPayloadLibrary.a");
            await File.WriteAllTextAsync(librarySource, """
                module PayloadLibrary
                public struct Buffer { rawmutptr<i8[min max]> Data; }
                public unsafe ffi finite void Store(rawmutptr<Buffer> buffer);
                public unsafe ffi finite law i32[min max] ReadAddress(u64[0 max] address);
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var exit = await CompilerCli.RunAsync(
                [librarySource, "--emit-lib", "--target", target.Triple, "-o", archive],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            if (sourceFree) File.Delete(librarySource);
            var app = Path.Combine(directory.FullName, "App.stark");
            await File.WriteAllTextAsync(app, """
                import PayloadLibrary
                module App
                export unsafe ffi finite i32[min max] main()
                {
                    stack mut i8[min max][3] storage = { 7, 8, 9 };
                    stack mut Buffer owner = new Buffer() { Data = &storage[0] };
                    Store(&owner);
                    if (storage[0] != 91 || storage[1] != 8 || storage[2] != 9) { return 1; }
                    stack u64[0 max] address = (u64[0 max])(&storage[1]);
                    if (ReadAddress(address) != 8) { return 2; }
                    storage[1] = 73;
                    if (ReadAddress(address) != 73) { return 3; }
                    return 0;
                }
                """);
            var native = Path.Combine(directory.FullName, "writer.c");
            await File.WriteAllTextAsync(native, """
                #include <stdint.h>
                struct Buffer { int8_t *data; };
                void Store(struct Buffer *buffer) { buffer->data[0] = 91; }
                __attribute__((noinline)) int32_t ReadAddress(uint64_t address)
                {
                    return *(const int8_t *)(uintptr_t)address;
                }
                """);
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            output.GetStringBuilder().Clear();
            errors.GetStringBuilder().Clear();
            exit = await CompilerCli.RunAsync(
                [app, "--emit-exe", "--target", target.Triple, "-I", directory.FullName,
                    "--native-source", native, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            await Run(binary, directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task Run(string executable, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + "\n" + await errors);
    }
}
