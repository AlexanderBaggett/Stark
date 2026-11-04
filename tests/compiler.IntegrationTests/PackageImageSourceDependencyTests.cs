using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class PackageImageSourceDependencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedFunctionLinksItsTransitiveSourceDependency(bool separateModule)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-source-dependency-");
        try
        {
            var dependencyDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "dependency")).FullName;
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "package-source")).FullName;
            var packageDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "package")).FullName;
            await File.WriteAllTextAsync(Path.Combine(dependencyDirectory, "Dependency.stark"), """
                module Dependency
                public noinline finite law i32[min max] AddOne(i32[min max] value) { return value + 1; }
                """);
            var librarySource = Path.Combine(sourceDirectory, "Bridge.stark");
            var functionSource = $$"""
                import Dependency
                module {{(separateModule ? "Bridge.Core" : "Bridge")}}
                public noinline finite law i32[min max] Evaluate(i32[min max] value) { return AddOne(value); }
                """;
            if (separateModule)
            {
                Directory.CreateDirectory(Path.Combine(sourceDirectory, "Bridge"));
                await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "Bridge", "Core.stark"), functionSource);
                await File.WriteAllTextAsync(librarySource, "export import Bridge.Core\nmodule Bridge\n");
            }
            else
            {
                await File.WriteAllTextAsync(librarySource, functionSource);
            }
            var library = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "Bridge.lib" : "libBridge.a");
            await CompileAsync([librarySource, "--emit-lib", "--target", target.Triple, "--no-stark-path", "-I", dependencyDirectory, "-I", sourceDirectory, "-o", library]);
            Directory.Delete(sourceDirectory, recursive: true);
            var app = Path.Combine(directory.FullName, "App.stark");
            await File.WriteAllTextAsync(app, """
                import Bridge
                module Consumer
                export finite i32[min max] main()
                {
                    if (Evaluate(41) != 42) { return 1; }
                    return 0;
                }
                """);
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await CompileAsync([app, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", packageDirectory, "-I", dependencyDirectory, "-o", executable]);
            await RunAndAssertZeroAsync(executable);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task FilteredSourceObjectRetainsCompilerProvidedCalleesOfWeakBodies()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-filtered-builtin-callee-");
        try
        {
            var result = DefaultCompilerPipeline.Create().Run(new CompilationInput("""
                module System.Text
                public finite law i64[min max] AsciiLength(ascii value);
                public noinline finite law i64[min max] ReadLength(ascii value) { return AsciiLength(value); }
                public finite law i32[min max] Retained() { return 0; }
                """, Path.Combine(directory.FullName, "Text.stark")), new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: target,
                    QualifyModuleSymbols: true,
                    ImportedInlineCloneSeedFunctions: new HashSet<string>(StringComparer.Ordinal) { "Retained" }));
            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule);
            var objectPath = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Text.obj" : "Text.o");
            var emitted = NativeToolchain.EmitObject(llvm.Text, objectPath, targetInfo: target);
            Assert.True(emitted.Succeeded, emitted.StandardError);
            var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Text.lib" : "libText.a");
            var archived = NativeToolchain.CreateStaticLibrary([objectPath], library);
            Assert.True(archived.Succeeded, archived.StandardError);
            var manifest = PackageImageBuilder.Create(result, library);
            await File.WriteAllBytesAsync(Path.ChangeExtension(library, ".starkpkg"), PackageImageBinaryFormat.Encode(manifest));
            var app = Path.Combine(directory.FullName, "App.stark");
            await File.WriteAllTextAsync(app, """
                import System.Text
                module Consumer
                export finite i32[min max] main()
                {
                    if (ReadLength("abcd") != 4) { return 1; }
                    return 0;
                }
                """);
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await CompileAsync([app, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-o", executable]);
            await RunAndAssertZeroAsync(executable);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task RunAndAssertZeroAsync(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await stdout);
        Assert.Equal(string.Empty, await stderr);
    }

    private static async Task CompileAsync(string[] arguments)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var result = await CompilerCli.RunAsync(arguments, new StringReader(string.Empty), stdout, stderr);
        Assert.True(result == 0, stdout + Environment.NewLine + stderr);
    }
}
