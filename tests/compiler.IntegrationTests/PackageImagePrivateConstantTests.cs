using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class PackageImagePrivateConstantTests
{
    [Theory]
    [InlineData(false, 7, false)]
    [InlineData(true, 7, false)]
    [InlineData(true, -100, true)]
    [InlineData(true, 1024, true)]
    public async Task PrivateConstantsSurviveInlineBodiesAndRetainVisibility(bool convert, int eventMask, bool assembly)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        if (assembly && !target.Triple.StartsWith("x86_64", StringComparison.Ordinal)) return;
        var assemblyIdentity = assembly ? """
            public unsafe ffi asm(x86_64) finite i64[min max] Identity64(i64[min max] value)
                in("rax") value,
                out("rax") return
            {
                ""
            }
            """ : string.Empty;
        var callbackSource = assembly ? """
            noinline unsafe finite i64[min max] InvokeCallback(fnptr<unsafe finite i64[min max](i64[min max])> callback, i64[min max] value)
            {
                return callback(value);
            }
            """ : string.Empty;
        var directory = Directory.CreateTempSubdirectory("stark-package-private-constant-");
        try
        {
            var source = Path.Combine(directory.FullName, "Library.stark");
            var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Library.lib" : "libLibrary.a");
            await File.WriteAllTextAsync(source, $$"""
                module PrivateConstants
                const EventMask = {{eventMask}};
                struct PrivatePair { i64[min max] Value; i32[min max] Offset; }
                internal noinline finite law i64[min max] ReadPrivatePair(PrivatePair pair)
                {
                    return pair.Value + (i64[min max])pair.Offset;
                }
                public noinline finite law i64[min max] InspectPair<T>(i64[min max] value)
                {
                    stack PrivatePair pair = new PrivatePair() { Value = value, Offset = 3 };
                    return ReadPrivatePair(pair);
                }
                {{assemblyIdentity}}
                public static mut u32[0 max] Calls = 0;
                internal noinline finite i32[min max] Apply(i16[min max] mask, i32[min max] value)
                {
                    Calls += 1;
                    return (i32[min max])mask + value;
                }
                public inline finite i32[min max] Observe(i32[min max] value)
                {
                    return Apply({{(convert ? "(i16[min max])EventMask" : "EventMask")}}, value);
                }
                public inline finite law i32[min max] Shadow(i32[min max] EventMask)
                {
                    return EventMask;
                }
                """);
            await CompileAsync([source, "--emit-lib", "--target", target.Triple, "--no-stark-path", "-o", library]);
            File.Delete(source);
            var app = Path.Combine(directory.FullName, "App.stark");
            await File.WriteAllTextAsync(app, $$"""
                import PrivateConstants
                module Consumer
                {{callbackSource}}
                export {{(assembly ? "unsafe " : "")}}finite i32[min max] main()
                {
                    if (Observe(5) != {{eventMask + 5}}) { return 1; }
                    if (Calls != 1) { return 3; }
                    if (Shadow(21) != 21) { return 2; }
                    if (InspectPair<i32[min max]>((i64[min max])Calls + 4294967302) != 4294967306) { return 6; }
                    {{(assembly ? "if (Identity64(4294967303) != 4294967303) { return 4; } if (InvokeCallback(Identity64, 4294967303) != 4294967303) { return 5; }" : "")}}
                    return 0;
                }
                """);
            var executable = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await CompileAsync([app, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-o", executable]);
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

            var inaccessible = DefaultCompilerPipeline.Create().Run(
                new CompilationInput("""
                    import PrivateConstants
                    module Consumer
                    export finite i32[min max] main() { return PrivateConstants.EventMask; }
                    """, app),
                new CompilerOptions(TargetInfo: target, ModuleResolver: new FileSystemModuleResolver(directory.FullName)));
            Assert.False(inaccessible.Succeeded);
            Assert.Contains(inaccessible.Diagnostics, diagnostic => diagnostic.Code == "STK3015");

            var inaccessibleType = DefaultCompilerPipeline.Create().Run(
                new CompilationInput("""
                    import PrivateConstants
                    module Consumer
                    finite law void Reject(PrivateConstants.PrivatePair value) { }
                    """, app),
                new CompilerOptions(TargetInfo: target, ModuleResolver: new FileSystemModuleResolver(directory.FullName)));
            Assert.False(inaccessibleType.Succeeded);
            Assert.Contains(inaccessibleType.Diagnostics, diagnostic => diagnostic.Code == "STK3015");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task CompileAsync(string[] arguments)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var result = await CompilerCli.RunAsync(arguments, new StringReader(string.Empty), stdout, stderr);
        Assert.True(result == 0, stdout + Environment.NewLine + stderr);
    }
}
