using System.Diagnostics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageInlineBodyTests
{
    [Theory]
    [InlineData(false, 41, false)]
    [InlineData(true, 41, false)]
    [InlineData(true, -173, false)]
    [InlineData(true, 1500000, false)]
    [InlineData(true, 59, true)]
    public async Task ClosedInlineBodiesAndPrivateHelpersSurviveSourceFreeConsumption(bool readPayload, int value, bool bulk)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-inline-");
        try
        {
            var librarySource = Path.Combine(directory.FullName, "Library.stark");
            var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Library.lib" : "libLibrary.a");
            var consumer = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            var stdlib = FindStdlibSourceDirectory();
            await File.WriteAllTextAsync(librarySource, $$"""
                {{(bulk ? "import System.Memory" : "")}}
                module InlineLibrary
                public struct Buffer
                {
                    dynamic i32[min max] Values;

                    public inline finite law u64[0 2 ** 63 - 1] Count(borrow Buffer self)
                    {
                        return self.Values.Length;
                    }
                }

                internal inline finite law i32[min max] InternalInline(i32[min max] value)
                {
                    return value;
                }

                noinline fn bool Seed(mut borrow Buffer buffer, i32[min max] value)
                {
                    if (!buffer.Values.TryReserve(3)) { return false; }
                    stack init i32[min max][] tail = init buffer.Values[buffer.Values.Length, 3];
                    {{(bulk ? "switch (System.Memory.FillCodePoints(tail, value, 3)) { case System.Memory.MemoryStatus.Err(var error): return false; case System.Memory.MemoryStatus.Ok: }" : "init tail[0] = value; init tail[1] = value + 1; init tail[2] = value + 2;")}}
                    return true;
                }

                public inline fn bool Fill(mut borrow Buffer buffer, i32[min max] value)
                {
                    return Seed(buffer, value);
                }

                public inline finite law i32[min max] Read(borrow Buffer buffer, u8[0 2] index)
                {
                    return buffer.Values[0, buffer.Values.Length][index];
                }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var libraryExit = await CompilerCli.RunAsync(
                [librarySource, "--emit-lib", "--target", target.Triple, "-I", stdlib, "-o", library],
                new StringReader(""), output, errors);
            Assert.True(libraryExit == 0, output + "\n" + errors);
            File.Delete(librarySource);
            await File.WriteAllTextAsync(consumer, $$"""
                import InlineLibrary
                module App
                export fn i32[min max] main()
                {
                    stack mut Buffer buffer = new();
                    if (!Fill(buffer, {{value}}) || buffer.Count() != 3) { return 1; }
                    {{(readPayload ? $"if (Read(buffer, 0) != {value} || Read(buffer, 1) != {value + (bulk ? 0 : 1)} || Read(buffer, 2) != {value + (bulk ? 0 : 2)}) {{ return 2; }}" : "")}}
                    return 0;
                }
                """);
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(await File.ReadAllTextAsync(consumer), consumer),
                new CompilerOptions(EmitLlvmIr: true, TargetInfo: target,
                    ModuleResolver: new FileSystemModuleResolver([directory.FullName, stdlib])));
            Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
            var imported = Assert.Single(result.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules).ImportedModules, module => module.SyntaxModel.ModuleName == "InlineLibrary");
            var facts = imported.PackageImageFacts!;
            Assert.NotNull(facts.FunctionTemplates["InlineLibrary.InternalInline"].TypedBody);
            Assert.True(facts.FunctionTemplates.ContainsKey("InlineLibrary.Seed"),
                "Templates: " + string.Join(", ", facts.FunctionTemplates.Keys));
            Assert.True(facts.FunctionEffects.ContainsKey("InlineLibrary.Seed"),
                "Effects: " + string.Join(", ", facts.FunctionEffects.Keys));
            var calls = ImportedTemplateCallFacts.GetReachableCallSignatures(facts.FunctionTemplates["InlineLibrary.Fill"]);
            Assert.Contains(calls, signature => signature.Name == "InlineLibrary.Seed");
            var mir = result.Artifacts.GetRequired(CompilerArtifactKeys.MidLevelIr);
            Assert.Contains(mir.Functions, function => function.Name == "InlineLibrary.Fill" && function.HasBody);
            Assert.Contains(mir.Functions, function => function.Name == "InlineLibrary.Seed" && function.HasBody);
            output.GetStringBuilder().Clear();
            errors.GetStringBuilder().Clear();
            var exit = await CompilerCli.RunAsync(
                [consumer, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-I", stdlib, "-o", binary],
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
            var privateCall = DefaultCompilerPipeline.Create().Run(
                new CompilationInput((await File.ReadAllTextAsync(consumer)).Replace("Fill(buffer,", "Seed(buffer,"), consumer),
                new CompilerOptions(ModuleResolver: new FileSystemModuleResolver([directory.FullName, stdlib])));
            Assert.False(privateCall.Succeeded);
        }
        finally { directory.Delete(recursive: true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedDisjointFactsSurvivePackagesWithoutEscapingTheirScope(bool useRegions)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-disjoint-");
        try
        {
            var librarySource = Path.Combine(directory.FullName, "Library.stark");
            var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Library.lib" : "libLibrary.a");
            var consumer = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(librarySource, $$"""
                module ScopedLibrary
                public inline unsafe finite i32[min max] Touch(
                    rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right, bool distinct)
                    where overlap(left, right)
                {
                    *left = 1;
                    if (distinct)
                    {
                        unsafe
                        {
                            assume disjoint({{(useRegions ? "left[0, 1], right[0, 1]" : "left, right")}})
                            {
                                *left = 7;
                                stack i32[min max] observed = *right;
                            }
                        }
                    }
                    *left = 9;
                    return *right;
                }

                public inline unsafe finite law i32[min max] ReadScoped(
                    rawptr<i32[min max]> left, rawptr<i32[min max]> right)
                    where overlap(left, right)
                {
                    unsafe
                    {
                        assume disjoint(left, right)
                        {
                            return *right;
                        }
                    }
                }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var exit = await CompilerCli.RunAsync(
                [librarySource, "--emit-lib", "--target", target.Triple, "-o", library],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            var libraryText = await File.ReadAllTextAsync(librarySource);
            File.Delete(librarySource);
            await File.WriteAllTextAsync(consumer, """
                import ScopedLibrary
                module App
                export unsafe finite i32[min max] main()
                {
                    stack mut i32[min max] left = 11;
                    stack mut i32[min max] right = 23;
                    if (ReadScoped(&left, &right) != 23) { return 3; }
                    if (Touch(&left, &right, true) != 23 || left != 9) { return 1; }
                    if (Touch(&left, &left, false) != 9) { return 2; }
                    return 0;
                }
                """);
            var consumerText = await File.ReadAllTextAsync(consumer);
            var inModule = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(libraryText + "\n" + consumerText[consumerText.IndexOf("export unsafe", StringComparison.Ordinal)..], consumer),
                new CompilerOptions(EmitLlvmIr: true, TargetInfo: target));
            Assert.True(inModule.Succeeded, string.Join("\n", inModule.Diagnostics));
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(consumerText, consumer),
                new CompilerOptions(EmitLlvmIr: true, TargetInfo: target,
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName)));
            Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
            var packageFacts = Assert.Single(result.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules).ImportedModules).PackageImageFacts!;
            Assert.NotNull(packageFacts.FunctionTemplates["ScopedLibrary.ReadScoped"].TypedBody);
            var function = Assert.Single(result.Artifacts.GetRequired(CompilerArtifactKeys.MidLevelIr).Functions,
                function => function.Name == "ScopedLibrary.Touch");
            var operations = function.Blocks.SelectMany(block => block.Statements).ToArray();
            var groups = operations.SelectMany(operation => operation.ScopedNoAliasGroups ?? []).ToArray();
            Assert.NotEmpty(groups);
            Assert.All(groups, group =>
            {
                Assert.Equal(AliasProofCarrierKind.UnsafeAssumeDisjoint, group.ProofCarrier.Kind);
                Assert.Equal(useRegions ? ["param:left[0..0]", "param:right[0..0]"] : ["param:left", "param:right"], group.RootKeys);
            });
            var stores = operations.Where(operation => operation.Kind == MidLevelIrStatementKind.StoreIndirect).ToArray();
            Assert.Equal(3, stores.Length);
            Assert.Null(stores[0].ScopedNoAliasGroups);
            Assert.NotEmpty(stores[1].ScopedNoAliasGroups ?? []);
            Assert.Null(stores[2].ScopedNoAliasGroups);
            output.GetStringBuilder().Clear();
            errors.GetStringBuilder().Clear();
            exit = await CompilerCli.RunAsync(
                [consumer, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            using var process = Process.Start(new ProcessStartInfo(binary) { UseShellExecute = false });
            Assert.NotNull(process);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task UnsupportedPrivateBodyUsesItsOriginalNativeWrapper()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-inline-fallback-");
        try
        {
            var source = Path.Combine(directory.FullName, "Library.stark");
            var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Library.lib" : "libLibrary.a");
            var app = Path.Combine(directory.FullName, "App.stark");
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(source, """
                module FallbackLibrary
                const PrivateValue = 7;
                unsafe finite i32[min max] Local(rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                    where overlap(left, right)
                {
                    if disjoint(left, right)
                    {
                        *left = PrivateValue;
                        return *right;
                    }
                    *left = 9;
                    return *right;
                }
                public inline unsafe finite i32[min max] Wrapped(
                    rawmutptr<i32[min max]> left, rawmutptr<i32[min max]> right)
                    where overlap(left, right)
                {
                    return Local(left, right);
                }
                """);
            var output = new StringWriter();
            var errors = new StringWriter();
            var exit = await CompilerCli.RunAsync(
                [source, "--emit-lib", "--target", target.Triple, "-o", library],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            File.Delete(source);
            await File.WriteAllTextAsync(app, """
                import FallbackLibrary
                module App
                export unsafe finite i32[min max] main()
                {
                    stack mut i32[min max] left = 11;
                    stack mut i32[min max] right = 23;
                    if (Wrapped(&left, &right) != 23 || left != 7) { return 1; }
                    if (Wrapped(&left, &left) != 9) { return 2; }
                    return 0;
                }
                """);
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(await File.ReadAllTextAsync(app), app),
                new CompilerOptions(EmitLlvmIr: true, TargetInfo: target,
                    ModuleResolver: new FileSystemModuleResolver(directory.FullName)));
            Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
            var facts = Assert.Single(result.Artifacts.GetRequired(CompilerArtifactKeys.LoadedModules).ImportedModules).PackageImageFacts!;
            Assert.False(facts.FunctionTemplates["FallbackLibrary.Wrapped"].IsGeneric);
            Assert.NotNull(facts.FunctionTemplates["FallbackLibrary.Wrapped"].TypedBody);
            Assert.Null(facts.FunctionTemplates["FallbackLibrary.Local"].TypedBody);
            Assert.False(Assert.Single(result.Artifacts.GetRequired(CompilerArtifactKeys.MidLevelIr).Functions,
                function => function.Name == "FallbackLibrary.Wrapped").HasBody);
            output.GetStringBuilder().Clear();
            errors.GetStringBuilder().Clear();
            exit = await CompilerCli.RunAsync(
                [app, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-o", binary],
                new StringReader(""), output, errors);
            Assert.True(exit == 0, output + "\n" + errors);
            using var process = Process.Start(new ProcessStartInfo(binary) { UseShellExecute = false });
            Assert.NotNull(process);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static string FindStdlibSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "stdlib", "src");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Unable to locate stdlib/src for the package initialization fixture.");
    }

}
